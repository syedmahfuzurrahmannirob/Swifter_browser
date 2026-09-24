using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Swifter.Engine
{
    public enum DownloadState
    {
        Queued,
        Starting,
        Downloading,
        Paused,
        Stitching,
        Verifying,
        Completed,
        Failed,
        Cancelled
    }

    public sealed class DownloadSegment
    {
        public int Index { get; set; }
        public long Start { get; set; }
        public long End { get; set; }          // inclusive
        public long Received { get; set; }     // bytes already on disk for this segment
        public double SpeedBps { get; set; }
        public string State { get; set; } = "idle";   // idle | active | done | error
        public string Error { get; set; } = "";
    }

    public sealed class DownloadTask
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Url { get; set; } = "";
        public string FileName { get; set; } = "";
        public string TargetPath { get; set; } = "";
        public string Referrer { get; set; } = "";
        public string MimeType { get; set; } = "";
        public string SourcePage { get; set; } = "";
        public long TotalBytes { get; set; } = -1;
        public long ReceivedBytes { get; set; }
        public double SpeedBps { get; set; }
        public double EtaSeconds { get; set; } = -1;
        public DownloadState State { get; set; } = DownloadState.Queued;
        public string Error { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public bool RangesSupported { get; set; }
        public int SegmentCount { get; set; } = 1;
        public int RetryCount { get; set; }
        public bool IsHls { get; set; }
        public int HlsDone { get; set; }
        public int HlsTotal { get; set; }
        public long QueuedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public long CompletedAt { get; set; }
        public List<DownloadSegment> Segments { get; set; } = new List<DownloadSegment>();

        public event Action<DownloadTask> Changed;
        internal long _receivedDelta;

        public void Notify()
        {
            Action<DownloadTask> h = Changed;
            if (h != null) h(this);
        }

        public double Progress
        {
            get
            {
                if (TotalBytes <= 0) return IsHls && HlsTotal > 0 ? (double)HlsDone / HlsTotal : 0;
                return Math.Min(1.0, (double)ReceivedBytes / TotalBytes);
            }
        }
    }

    /// <summary>
    /// Process wide token-bucket throttle shared by every segment stream so the
    /// "global transfer rate governor" setting is honoured across all downloads.
    /// </summary>
    public sealed class BandwidthGovernor
    {
        private readonly object _lock = new object();
        private double _tokens;
        private long _lastRefill = Environment.TickCount64;
        private long _bytesPerSec;   // 0 = unlimited

        public long BytesPerSec
        {
            get { lock (_lock) return _bytesPerSec; }
        }

        public void SetRate(long bytesPerSec)
        {
            lock (_lock)
            {
                _bytesPerSec = Math.Max(0, bytesPerSec);
                _tokens = 0;
                _lastRefill = Environment.TickCount64;
            }
        }

        public async Task ConsumeAsync(int bytes, CancellationToken ct)
        {
            while (true)
            {
                double waitMs;
                lock (_lock)
                {
                    if (_bytesPerSec <= 0) return;
                    long now = Environment.TickCount64;
                    double elapsed = (now - _lastRefill) / 1000.0;
                    _lastRefill = now;
                    _tokens = Math.Min(_bytesPerSec, _tokens + elapsed * _bytesPerSec);
                    if (_tokens >= bytes)
                    {
                        _tokens -= bytes;
                        return;
                    }
                    double missing = bytes - _tokens;
                    waitMs = missing / _bytesPerSec * 1000.0;
                }
                await Task.Delay((int)Math.Min(500, Math.Max(1, Math.Ceiling(waitMs))), ct).ConfigureAwait(false);
            }
        }
    }

    public sealed class DownloadProbe
    {
        public string FinalUrl = "";
        public string FileName = "";
        public string MimeType = "";
        public long ContentLength = -1;
        public bool AcceptsRanges;
        public bool IsHls;
    }

    /// <summary>
    /// IDM grade parallel downloader: probes the server, splits the payload into
    /// up to 32 HTTP Range streams, persists per-segment .part files for resume,
    /// stitches them on completion and optionally verifies SHA-256. Also handles
    /// HLS (.m3u8) playlists by fetching every media segment in parallel.
    /// </summary>
    public static class SegmentedDownloader
    {
        public static readonly BandwidthGovernor Governor = new BandwidthGovernor();

        private static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(CreateClient);
        private const int ChunkSize = 64 * 1024;

        private static HttpClient CreateClient()
        {
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                UseCookies = false,
                MaxConnectionsPerServer = 48,
                ConnectTimeout = TimeSpan.FromSeconds(20),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.None
            };
            HttpClient client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36 Swifter/1.0");
            client.DefaultRequestHeaders.Add("Accept", "*/*");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.8");
            return client;
        }

        public static HttpClient Client { get { return Http.Value; } }

        // ------------------------------------------------------------------
        // Probing
        // ------------------------------------------------------------------

        public static async Task<DownloadProbe> ProbeAsync(string url, string referrer)
        {
            DownloadProbe probe = new DownloadProbe { FinalUrl = url };
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Head, url))
            {
                if (!string.IsNullOrEmpty(referrer)) req.Headers.TryAddWithoutValidation("Referer", referrer);
                using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    try
                    {
                        using (HttpResponseMessage res = await Http.Value.SendAsync(
                            req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                        {
                            ApplyHeaders(probe, res);
                            if (res.IsSuccessStatusCode) return probe;
                        }
                    }
                    catch
                    {
                        // Some servers reject HEAD; fall through to a ranged GET probe.
                    }
                }
            }
            using (HttpRequestMessage get = new HttpRequestMessage(HttpMethod.Get, url))
            {
                get.Headers.TryAddWithoutValidation("Range", "bytes=0-1");
                if (!string.IsNullOrEmpty(referrer)) get.Headers.TryAddWithoutValidation("Referer", referrer);
                using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    using (HttpResponseMessage res = await Http.Value.SendAsync(
                        get, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        ApplyHeaders(probe, res);
                    }
                }
            }
            return probe;
        }

        private static void ApplyHeaders(DownloadProbe probe, HttpResponseMessage res)
        {
            probe.FinalUrl = res.RequestMessage.RequestUri.ToString();
            probe.MimeType = res.Content.Headers.ContentType?.MediaType ?? "";
            probe.ContentLength = res.Content.Headers.ContentLength ?? -1;
            probe.AcceptsRanges = res.Headers.AcceptRanges.Contains("bytes");
            string lower = probe.FinalUrl.ToLowerInvariant();
            string mime = (probe.MimeType ?? "").ToLowerInvariant();
            probe.IsHls = lower.Contains(".m3u8") || mime.Contains("mpegurl") || mime.Contains("m3u8");
            if (probe.ContentLength < 0 && res.StatusCode == HttpStatusCode.PartialContent &&
                res.Content.Headers.ContentRange != null && res.Content.Headers.ContentRange.HasLength)
            {
                probe.ContentLength = res.Content.Headers.ContentRange.Length.Value;
                probe.AcceptsRanges = true;
            }
            probe.FileName = FileNameFromResponse(res, probe.FinalUrl);
        }

        public static string FileNameFromResponse(HttpResponseMessage res, string url)
        {
            string name = "";
            try
            {
                if (res.Content.Headers.ContentDisposition != null)
                {
                    string cd = res.Content.Headers.ContentDisposition.FileNameStar;
                    if (string.IsNullOrEmpty(cd)) cd = res.Content.Headers.ContentDisposition.FileName;
                    if (!string.IsNullOrEmpty(cd)) name = Uri.UnescapeDataString(cd.Trim('"'));
                }
            }
            catch { }
            if (string.IsNullOrEmpty(name))
            {
                try
                {
                    Uri u = new Uri(url);
                    string seg = u.AbsolutePath;
                    int slash = seg.LastIndexOf('/');
                    if (slash >= 0 && slash + 1 < seg.Length) name = Uri.UnescapeDataString(seg.Substring(slash + 1));
                    if (string.IsNullOrEmpty(name)) name = u.Host;
                }
                catch { name = "download"; }
            }
            return SanitizeFileName(name);
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "download.bin";
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            string clean = sb.ToString().Trim(' ', '.');
            if (clean.Length == 0) clean = "download.bin";
            if (clean.Length > 180) clean = clean.Substring(0, 180);
            return clean;
        }

        public static string UniquePath(string dir, string fileName)
        {
            Directory.CreateDirectory(dir);
            string candidate = Path.Combine(dir, fileName);
            int i = 1;
            while (File.Exists(candidate))
            {
                string ext = Path.GetExtension(fileName);
                string stem = Path.GetFileNameWithoutExtension(fileName);
                candidate = Path.Combine(dir, stem + " (" + i.ToString(CultureInfo.InvariantCulture) + ")" + ext);
                i++;
            }
            return candidate;
        }

        // ------------------------------------------------------------------
        // Segmented download
        // ------------------------------------------------------------------

        public static async Task RunAsync(DownloadTask task, int segmentCount, bool verifySha,
            int maxRetries, CancellationToken ct)
        {
            task.State = DownloadState.Starting;
            task.Notify();

            DownloadProbe probe = await ProbeAsync(task.Url, task.Referrer).ConfigureAwait(false);
            task.MimeType = probe.MimeType;
            if (string.IsNullOrEmpty(task.FileName)) task.FileName = probe.FileName;
            if (string.IsNullOrEmpty(task.TargetPath)) return;

            if (probe.IsHls || task.IsHls)
            {
                task.IsHls = true;
                await RunHlsAsync(task, segmentCount, verifySha, ct).ConfigureAwait(false);
                return;
            }

            bool resumable = probe.AcceptsRanges && probe.ContentLength > 0;
            task.TotalBytes = probe.ContentLength;
            task.RangesSupported = resumable;

            bool resuming = task.Segments.Count > 0 && task.ReceivedBytes > 0;
            if (!resuming)
            {
                task.Segments.Clear();
                if (!resumable || probe.ContentLength < 2 * 1024 * 1024)
                {
                    task.SegmentCount = 1;
                    task.Segments.Add(new DownloadSegment { Index = 0, Start = 0, End = long.MaxValue });
                }
                else
                {
                    int n = Math.Max(1, Math.Min(32, segmentCount));
                    long per = probe.ContentLength / n;
                    for (int i = 0; i < n; i++)
                    {
                        long start = i * per;
                        long end = (i == n - 1) ? probe.ContentLength - 1 : start + per - 1;
                        task.Segments.Add(new DownloadSegment { Index = i, Start = start, End = end });
                    }
                    task.SegmentCount = n;
                }
                PreparePartFiles(task);
            }

            task.State = DownloadState.Downloading;
            task.Notify();

            List<Task> workers = new List<Task>();
            foreach (DownloadSegment seg in task.Segments)
            {
                DownloadSegment captured = seg;
                workers.Add(Task.Run(() => RunSegmentAsync(task, captured, probe.FinalUrl, maxRetries, ct), ct));
            }
            await Task.WhenAll(workers).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            if (task.State == DownloadState.Cancelled || task.State == DownloadState.Failed) return;

            long missing = 0;
            foreach (DownloadSegment seg in task.Segments)
                missing += Math.Max(0, (seg.End == long.MaxValue ? task.TotalBytes : seg.End + 1) - seg.Start - seg.Received);
            if (missing > 0 && task.TotalBytes > 0)
            {
                task.State = DownloadState.Failed;
                task.Error = "Incomplete transfer (" + FormatBytes(missing) + " missing)";
                task.Notify();
                return;
            }

            task.State = DownloadState.Stitching;
            task.Notify();
            await StitchAsync(task, verifySha, ct).ConfigureAwait(false);
        }

        private static void PreparePartFiles(DownloadTask task)
        {
            foreach (DownloadSegment seg in task.Segments)
            {
                string part = PartPath(task, seg.Index);
                if (File.Exists(part))
                {
                    var info = new FileInfo(part);
                    seg.Received = info.Length;
                }
                else seg.Received = 0;
            }
            task.ReceivedBytes = task.Segments.Sum(s => s.Received);
        }

        public static string PartPath(DownloadTask task, int index)
        {
            return task.TargetPath + ".swpart" + index.ToString(CultureInfo.InvariantCulture);
        }

        private static async Task RunSegmentAsync(DownloadTask task, DownloadSegment seg, string url,
            int maxRetries, CancellationToken ct)
        {
            long segEnd = seg.End;
            long total = segEnd == long.MaxValue ? -1 : segEnd + 1;
            int attempt = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (total > 0 && seg.Received >= total - seg.Start)
                {
                    seg.State = "done";
                    seg.SpeedBps = 0;
                    task.Notify();
                    return;
                }
                attempt++;
                seg.State = "active";
                try
                {
                    using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (segEnd != long.MaxValue)
                        {
                            long from = seg.Start + seg.Received;
                            if (from > segEnd) { seg.State = "done"; return; }
                            req.Headers.TryAddWithoutValidation("Range",
                                "bytes=" + from.ToString(CultureInfo.InvariantCulture) + "-" +
                                segEnd.ToString(CultureInfo.InvariantCulture));
                        }
                        else if (seg.Received > 0)
                        {
                            req.Headers.TryAddWithoutValidation("Range",
                                "bytes=" + (seg.Start + seg.Received).ToString(CultureInfo.InvariantCulture) + "-");
                        }
                        if (!string.IsNullOrEmpty(task.Referrer))
                            req.Headers.TryAddWithoutValidation("Referer", task.Referrer);

                        using (HttpResponseMessage res = await Http.Value.SendAsync(
                            req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                        {
                            if (res.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                            {
                                seg.State = "done";
                                seg.Received = total > 0 ? total - seg.Start : seg.Received;
                                task.Notify();
                                return;
                            }
                            res.EnsureSuccessStatusCode();

                            bool serverHonouredRange = res.StatusCode == HttpStatusCode.PartialContent;
                            if (!serverHonouredRange && seg.Index != 0)
                            {
                                // Misconfigured server ignored the Range header: segment 0
                                // already receives the complete payload, nothing to do here.
                                seg.State = "done";
                                seg.Start = 0;
                                seg.End = -1;
                                seg.Received = 0;
                                task.Notify();
                                return;
                            }
                            using (Stream net = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                            using (FileStream file = new FileStream(PartPath(task, seg.Index),
                                       serverHonouredRange ? FileMode.Append : FileMode.Create,
                                       FileAccess.Write, FileShare.None, ChunkSize, FileOptions.Asynchronous))
                            {
                                if (!serverHonouredRange) seg.Received = 0;  // full body rewrite
                                byte[] buffer = new byte[ChunkSize];
                                DateTime window = DateTime.UtcNow;
                                long windowBytes = 0;
                                while (true)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    int read = await net.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                                    if (read <= 0) break;
                                    await Governor.ConsumeAsync(read, ct).ConfigureAwait(false);
                                    await file.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                                    seg.Received += read;
                                    Interlocked.Add(ref task._receivedDelta, read);
                                    windowBytes += read;
                                    double secs = (DateTime.UtcNow - window).TotalSeconds;
                                    if (secs >= 0.5)
                                    {
                                        seg.SpeedBps = windowBytes / secs;
                                        window = DateTime.UtcNow;
                                        windowBytes = 0;
                                    }
                                    if (segEnd != long.MaxValue && seg.Start + seg.Received > segEnd + 1) break;
                                }
                            }
                        }
                    }
                    seg.State = "done";
                    seg.SpeedBps = 0;
                    task.Notify();
                    return;
                }
                catch (OperationCanceledException)
                {
                    seg.State = "idle";
                    seg.SpeedBps = 0;
                    throw;
                }
                catch (Exception ex)
                {
                    seg.Error = ex.Message;
                    seg.State = "error";
                    seg.SpeedBps = 0;
                    task.Notify();
                    if (attempt > maxRetries)
                    {
                        task.State = DownloadState.Failed;
                        task.Error = "Segment " + seg.Index + ": " + ex.Message;
                        task.Notify();
                        return;
                    }
                    try { await Task.Delay(800 * attempt, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { seg.State = "idle"; throw; }
                }
            }
        }

        /// <summary>Field used by the queue manager to sample aggregate speed without locking.</summary>
        internal static long TakeReceivedDelta(DownloadTask task)
        {
            return Interlocked.Exchange(ref task._receivedDelta, 0);
        }

        // ------------------------------------------------------------------
        // Stitching + hashing
        // ------------------------------------------------------------------

        private static async Task StitchAsync(DownloadTask task, bool verifySha, CancellationToken ct)
        {
            string tmpOut = task.TargetPath + ".swstitch";
            using (IncrementalHash hash = verifySha ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null)
            {
                using (FileStream outStream = new FileStream(tmpOut, FileMode.Create, FileAccess.Write,
                           FileShare.None, 256 * 1024, FileOptions.Asynchronous))
                {
                    List<DownloadSegment> ordered = task.Segments.OrderBy(s => s.Index).ToList();
                    foreach (DownloadSegment seg in ordered)
                    {
                        string part = PartPath(task, seg.Index);
                        if (!File.Exists(part)) continue;
                        using (FileStream partStream = new FileStream(part, FileMode.Open, FileAccess.Read,
                                   FileShare.Read, 256 * 1024, FileOptions.Asynchronous))
                        {
                            byte[] buffer = new byte[256 * 1024];
                            while (true)
                            {
                                ct.ThrowIfCancellationRequested();
                                int read = await partStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                                if (read <= 0) break;
                                await outStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                                if (hash != null) hash.AppendData(buffer, 0, read);
                            }
                        }
                        File.Delete(part);
                    }
                }
                if (hash != null)
                {
                    task.State = DownloadState.Verifying;
                    task.Notify();
                    byte[] digest = hash.GetHashAndReset();
                    StringBuilder hex = new StringBuilder(digest.Length * 2);
                    foreach (byte b in digest) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    task.Sha256 = hex.ToString();
                }
            }
            if (File.Exists(task.TargetPath)) File.Delete(task.TargetPath);
            File.Move(tmpOut, task.TargetPath);
            task.State = DownloadState.Completed;
            task.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            task.SpeedBps = 0;
            task.Notify();
        }

        // ------------------------------------------------------------------
        // HLS (.m3u8) support
        // ------------------------------------------------------------------

        public static async Task RunHlsAsync(DownloadTask task, int segmentCount, bool verifySha, CancellationToken ct)
        {
            task.State = DownloadState.Downloading;
            task.Notify();
            string playlist = await FetchTextAsync(task.Url, task.Referrer, ct).ConfigureAwait(false);
            List<string> mediaUrls = new List<string>();

            // Master playlist: pick the variant with the highest bandwidth.
            if (playlist.IndexOf("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string bestUrl = "";
                long bestBw = -1;
                string[] lines = playlist.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;
                    long bw = 0;
                    int idx = line.IndexOf("BANDWIDTH=", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        string rest = line.Substring(idx + 10);
                        int comma = rest.IndexOf(',');
                        if (comma > 0) rest = rest.Substring(0, comma);
                        long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out bw);
                    }
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        string next = lines[j].Trim();
                        if (next.Length == 0 || next.StartsWith("#")) continue;
                        if (bw > bestBw) { bestBw = bw; bestUrl = Resolve(task.Url, next); }
                        break;
                    }
                }
                if (bestUrl.Length > 0)
                {
                    playlist = await FetchTextAsync(bestUrl, task.Referrer, ct).ConfigureAwait(false);
                    task.Url = bestUrl;
                }
            }

            Uri baseUri = new Uri(task.Url);
            foreach (string raw in playlist.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                mediaUrls.Add(baseUri.IsBaseOf(new Uri(baseUri, line)) ? new Uri(baseUri, line).ToString() : line);
            }
            if (mediaUrls.Count == 0)
            {
                task.State = DownloadState.Failed;
                task.Error = "Empty HLS playlist";
                task.Notify();
                return;
            }

            task.HlsTotal = mediaUrls.Count;
            task.HlsDone = 0;
            task.Segments.Clear();
            for (int i = 0; i < mediaUrls.Count; i++)
                task.Segments.Add(new DownloadSegment { Index = i, Start = 0, End = 0 });
            task.Notify();

            int concurrency = Math.Max(1, Math.Min(32, segmentCount));
            using (SemaphoreSlim gate = new SemaphoreSlim(concurrency))
            {
                List<Task> jobs = new List<Task>();
                for (int i = 0; i < mediaUrls.Count; i++)
                {
                    int index = i;
                    string url = mediaUrls[i];
                    jobs.Add(Task.Run(async () =>
                    {
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            ct.ThrowIfCancellationRequested();
                            byte[] body = await FetchBytesAsync(url, task.Referrer, ct).ConfigureAwait(false);
                            await Governor.ConsumeAsync(body.Length, ct).ConfigureAwait(false);
                            File.WriteAllBytes(PartPath(task, index), body);
                            lock (task.Segments)
                            {
                                task.HlsDone++;
                                task.ReceivedBytes += body.Length;
                                task.Segments[index].State = "done";
                                task.Segments[index].Received = body.Length;
                            }
                            task.Notify();
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }, ct));
                }
                await Task.WhenAll(jobs).ConfigureAwait(false);
            }

            task.State = DownloadState.Stitching;
            task.Notify();
            string tmpOut = task.TargetPath + ".swstitch";
            using (FileStream outStream = new FileStream(tmpOut, FileMode.Create, FileAccess.Write))
            {
                for (int i = 0; i < mediaUrls.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    string part = PartPath(task, i);
                    if (!File.Exists(part)) continue;
                    using (FileStream partStream = new FileStream(part, FileMode.Open, FileAccess.Read))
                        await partStream.CopyToAsync(outStream, 256 * 1024, ct).ConfigureAwait(false);
                    File.Delete(part);
                }
            }
            if (verifySha)
            {
                task.State = DownloadState.Verifying;
                task.Notify();
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = new FileStream(tmpOut, FileMode.Open, FileAccess.Read))
                {
                    byte[] digest = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
                    StringBuilder hex = new StringBuilder();
                    foreach (byte b in digest) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    task.Sha256 = hex.ToString();
                }
            }
            if (File.Exists(task.TargetPath)) File.Delete(task.TargetPath);
            File.Move(tmpOut, task.TargetPath);
            task.State = DownloadState.Completed;
            task.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            task.Notify();
        }

        public static string Resolve(string baseUrl, string relative)
        {
            try { return new Uri(new Uri(baseUrl), relative).ToString(); }
            catch { return relative; }
        }

        private static async Task<string> FetchTextAsync(string url, string referrer, CancellationToken ct)
        {
            byte[] body = await FetchBytesAsync(url, referrer, ct).ConfigureAwait(false);
            return Encoding.UTF8.GetString(body);
        }

        private static async Task<byte[]> FetchBytesAsync(string url, string referrer, CancellationToken ct)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (!string.IsNullOrEmpty(referrer)) req.Headers.TryAddWithoutValidation("Referer", referrer);
                using (HttpResponseMessage res = await Http.Value.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    res.EnsureSuccessStatusCode();
                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (Stream net = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                        {
                            byte[] buffer = new byte[64 * 1024];
                            while (true)
                            {
                                int read = await net.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                                if (read <= 0) break;
                                ms.Write(buffer, 0, read);
                            }
                        }
                        return ms.ToArray();
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Formatting helpers shared by the UI
        // ------------------------------------------------------------------

        public static string FormatBytes(double bytes)
        {
            if (bytes < 0) return "?";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int u = 0;
            double v = bytes;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return v.ToString(v >= 100 || u == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + units[u];
        }

        public static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec <= 0) return "0 B/s";
            return FormatBytes(bytesPerSec) + "/s";
        }

        public static string FormatEta(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return "--:--";
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            if (t.TotalHours >= 1)
                return ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" +
                       t.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                       t.Seconds.ToString("00", CultureInfo.InvariantCulture);
            return t.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                   t.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
