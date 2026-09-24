using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Swifter.Config;

namespace Swifter.Engine
{
    /// <summary>Serialisable snapshot of a download task for downloads.json.</summary>
    public sealed class DownloadDto
    {
        public string Id { get; set; } = "";
        public string Url { get; set; } = "";
        public string FileName { get; set; } = "";
        public string TargetPath { get; set; } = "";
        public string Referrer { get; set; } = "";
        public string MimeType { get; set; } = "";
        public string SourcePage { get; set; } = "";
        public long TotalBytes { get; set; } = -1;
        public long ReceivedBytes { get; set; }
        public string State { get; set; } = "Queued";
        public string Sha256 { get; set; } = "";
        public bool RangesSupported { get; set; }
        public int SegmentCount { get; set; } = 1;
        public bool IsHls { get; set; }
        public int HlsDone { get; set; }
        public int HlsTotal { get; set; }
        public long QueuedAt { get; set; }
        public long CompletedAt { get; set; }
        public List<SegmentDto> Segments { get; set; } = new List<SegmentDto>();
    }

    public sealed class SegmentDto
    {
        public int Index { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
        public long Received { get; set; }
    }

    /// <summary>
    /// Owns the download queue: concurrency limit, global throttle, scheduler,
    /// persistence, clipboard link sniffing and all task lifecycle operations.
    /// </summary>
    public sealed class DownloadQueueManager
    {
        private readonly object _lock = new object();
        private readonly List<DownloadTask> _tasks = new List<DownloadTask>();
        private readonly Dictionary<string, CancellationTokenSource> _cts =
            new Dictionary<string, CancellationTokenSource>();
        private readonly SettingsManager _settings;
        private System.Windows.Forms.Timer _tick;
        private DateTime _lastPersist = DateTime.MinValue;
        private ClipboardMonitor _clipboard;

        public event Action Changed;
        public event Action<string, string> LinkSniffed;    // url, fileName
        public event Action<DownloadTask> TaskCompleted;

        public DownloadQueueManager(SettingsManager settings)
        {
            _settings = settings;
            SegmentedDownloader.Governor.SetRate(settings.Model.Downloads.ThrottleKibPerSec * 1024L);
        }

        // ------------------------------------------------------------------
        // Snapshot / enumeration
        // ------------------------------------------------------------------

        public List<DownloadTask> Snapshot()
        {
            lock (_lock) return new List<DownloadTask>(_tasks);
        }

        public DownloadTask Find(string id)
        {
            lock (_lock) return _tasks.FirstOrDefault(t => t.Id == id);
        }

        public int ActiveCount
        {
            get
            {
                lock (_lock)
                    return _tasks.Count(t => t.State == DownloadState.Downloading || t.State == DownloadState.Starting);
            }
        }

        public double AggregateSpeed
        {
            get
            {
                lock (_lock)
                    return _tasks.Where(t => t.State == DownloadState.Downloading).Sum(t => t.SpeedBps);
            }
        }

        public bool AnyRunning { get { return ActiveCount > 0; } }

        // ------------------------------------------------------------------
        // Queue engine
        // ------------------------------------------------------------------

        public DownloadTask Enqueue(string url, string fileName, string targetDir, string referrer,
            string sourcePage, bool isHls)
        {
            DownloadTask task = new DownloadTask
            {
                Url = url,
                FileName = SegmentedDownloader.SanitizeFileName(
                    string.IsNullOrEmpty(fileName) ? "download.bin" : fileName),
                TargetPath = SegmentedDownloader.UniquePath(
                    string.IsNullOrEmpty(targetDir) ? _settings.Model.General.DownloadPath : targetDir,
                    string.IsNullOrEmpty(fileName) ? "download.bin" : fileName),
                Referrer = referrer ?? "",
                SourcePage = sourcePage ?? "",
                IsHls = isHls,
                State = DownloadState.Queued
            };
            lock (_lock) _tasks.Insert(0, task);
            Pump();
            PersistSoon();
            RaiseChanged();
            return task;
        }

        public void StartNow(string id)
        {
            DownloadTask task = Find(id);
            if (task == null) return;
            if (task.State == DownloadState.Downloading || task.State == DownloadState.Starting) return;
            task.State = DownloadState.Queued;
            task.Error = "";
            Launch(task);
        }

        public void Pause(string id)
        {
            DownloadTask task = Find(id);
            if (task == null) return;
            if (task.State != DownloadState.Downloading && task.State != DownloadState.Starting &&
                task.State != DownloadState.Queued) return;
            if (task.State == DownloadState.Queued)
            {
                task.State = DownloadState.Paused;
                task.Notify();
                RaiseChanged();
                return;
            }
            task.State = DownloadState.Paused;
            task.SpeedBps = 0;
            CancelCts(id);
            task.Notify();
            PersistSoon();
            RaiseChanged();
            Pump();
        }

        public void Resume(string id)
        {
            DownloadTask task = Find(id);
            if (task == null) return;
            if (task.State != DownloadState.Paused && task.State != DownloadState.Failed &&
                task.State != DownloadState.Cancelled) return;
            task.Error = "";
            Launch(task);
        }

        public void Cancel(string id)
        {
            DownloadTask task = Find(id);
            if (task == null) return;
            task.State = DownloadState.Cancelled;
            task.SpeedBps = 0;
            CancelCts(id);
            CleanupParts(task);
            task.Notify();
            PersistSoon();
            RaiseChanged();
            Pump();
        }

        public void Retry(string id)
        {
            DownloadTask task = Find(id);
            if (task == null) return;
            task.RetryCount = 0;
            task.Error = "";
            Launch(task);
        }

        public void Remove(string id, bool deleteFile)
        {
            DownloadTask task = Find(id);
            if (task == null) return;
            if (task.State == DownloadState.Downloading || task.State == DownloadState.Starting) Cancel(id);
            lock (_lock) _tasks.Remove(task);
            if (deleteFile)
            {
                try { if (File.Exists(task.TargetPath)) File.Delete(task.TargetPath); } catch { }
                CleanupParts(task);
            }
            PersistSoon();
            RaiseChanged();
        }

        public int ClearCompleted()
        {
            int removed;
            lock (_lock)
            {
                removed = _tasks.RemoveAll(t =>
                    t.State == DownloadState.Completed || t.State == DownloadState.Cancelled ||
                    t.State == DownloadState.Failed);
            }
            PersistSoon();
            RaiseChanged();
            return removed;
        }

        private void CleanupParts(DownloadTask task)
        {
            try
            {
                foreach (DownloadSegment seg in task.Segments)
                {
                    string part = SegmentedDownloader.PartPath(task, seg.Index);
                    if (File.Exists(part)) File.Delete(part);
                }
                string stitch = task.TargetPath + ".swstitch";
                if (File.Exists(stitch)) File.Delete(stitch);
            }
            catch { }
        }

        private void CancelCts(string id)
        {
            CancellationTokenSource cts;
            lock (_lock)
            {
                if (!_cts.TryGetValue(id, out cts)) return;
                _cts.Remove(id);
            }
            try { cts.Cancel(); } catch { }
        }

        private void Launch(DownloadTask task)
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            lock (_lock)
            {
                CancellationTokenSource old;
                if (_cts.TryGetValue(task.Id, out old)) { try { old.Cancel(); } catch { } }
                _cts[task.Id] = cts;
            }
            task.State = DownloadState.Starting;
            task.Notify();
            RaiseChanged();

            int segments = _settings.Model.Downloads.SegmentCount;
            bool verify = _settings.Model.Downloads.VerifySha256;
            int retries = _settings.Model.Downloads.AutoRetry ? _settings.Model.Downloads.MaxRetries : 0;
            CancellationToken token = cts.Token;

            Task.Run(() => SegmentedDownloader.RunAsync(task, segments, verify, retries, token), token)
                .ContinueWith(delegate (Task antecedent)
                {
                    lock (_lock) _cts.Remove(task.Id);
                    if (antecedent.IsCanceled || antecedent.IsFaulted)
                    {
                        if (task.State != DownloadState.Paused && task.State != DownloadState.Cancelled)
                        {
                            task.State = DownloadState.Failed;
                            task.Error = antecedent.Exception != null
                                ? antecedent.Exception.GetBaseException().Message
                                : "Interrupted";
                        }
                        task.SpeedBps = 0;
                    }
                    task.Notify();
                    if (task.State == DownloadState.Completed)
                    {
                        Action<DownloadTask> done = TaskCompleted;
                        if (done != null) done(task);
                    }
                    PersistSoon();
                    RaiseChanged();
                    Pump();
                }, TaskScheduler.Default);
        }

        /// <summary>Starts queued tasks while a slot and the scheduler window allow.</summary>
        public void Pump()
        {
            if (!IsWithinScheduleWindow()) return;
            List<DownloadTask> toStart = new List<DownloadTask>();
            lock (_lock)
            {
                int active = _tasks.Count(t =>
                    t.State == DownloadState.Downloading || t.State == DownloadState.Starting);
                foreach (DownloadTask t in _tasks)
                {
                    if (active >= _settings.Model.Downloads.MaxActiveDownloads) break;
                    if (t.State != DownloadState.Queued) continue;
                    toStart.Add(t);
                    active++;
                }
            }
            foreach (DownloadTask t in toStart)
            {
                if (t.State != DownloadState.Queued) continue;
                Launch(t);
            }
        }

        public bool IsWithinScheduleWindow()
        {
            DownloadSettings d = _settings.Model.Downloads;
            if (!d.SchedulerEnabled) return true;
            TimeSpan start = ParseTime(d.ScheduleStart, new TimeSpan(22, 0, 0));
            TimeSpan end = ParseTime(d.ScheduleEnd, new TimeSpan(6, 0, 0));
            TimeSpan now = DateTime.Now.TimeOfDay;
            if (start <= end) return now >= start && now <= end;
            return now >= start || now <= end;   // window wraps midnight
        }

        private static TimeSpan ParseTime(string s, TimeSpan fallback)
        {
            TimeSpan t;
            if (TimeSpan.TryParseExact(s, "hh\\:mm", CultureInfo.InvariantCulture, out t)) return t;
            if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out t)) return t;
            return fallback;
        }

        // ------------------------------------------------------------------
        // Speed sampling tick
        // ------------------------------------------------------------------

        public void StartTicker()
        {
            if (_tick != null) return;
            _tick = new System.Windows.Forms.Timer();
            _tick.Interval = 500;
            _tick.Tick += delegate { Tick(); };
            _tick.Start();
        }

        private void Tick()
        {
            bool dirty = false;
            List<DownloadTask> tasks;
            lock (_lock) tasks = new List<DownloadTask>(_tasks);
            foreach (DownloadTask t in tasks)
            {
                if (t.State == DownloadState.Downloading || t.State == DownloadState.Starting)
                {
                    long delta = SegmentedDownloader.TakeReceivedDelta(t);
                    t.SpeedBps = delta / 0.5;
                    if (!t.IsHls) t.ReceivedBytes = t.Segments.Sum(s => s.Received);
                    if (t.TotalBytes > 0 && t.SpeedBps > 0)
                        t.EtaSeconds = (t.TotalBytes - t.ReceivedBytes) / t.SpeedBps;
                    else t.EtaSeconds = -1;
                    dirty = true;
                }
            }
            if (dirty) RaiseChanged();
            if ((DateTime.Now - _lastPersist).TotalSeconds > 5) Persist();
        }

        private void RaiseChanged()
        {
            Action h = Changed;
            if (h != null) h();
        }

        // ------------------------------------------------------------------
        // Settings plumbing
        // ------------------------------------------------------------------

        public void SetThrottleKib(int kibPerSec)
        {
            _settings.Model.Downloads.ThrottleKibPerSec = kibPerSec;
            SegmentedDownloader.Governor.SetRate(kibPerSec * 1024L);
            _settings.SaveLater();
            RaiseChanged();
        }

        public void SetSegmentCount(int count)
        {
            _settings.Model.Downloads.SegmentCount = Math.Max(1, Math.Min(32, count));
            _settings.SaveLater();
            RaiseChanged();
        }

        public void SetMaxActive(int count)
        {
            _settings.Model.Downloads.MaxActiveDownloads = Math.Max(1, Math.Min(8, count));
            _settings.SaveLater();
            Pump();
            RaiseChanged();
        }

        // ------------------------------------------------------------------
        // Persistence
        // ------------------------------------------------------------------

        private void PersistSoon()
        {
            _lastPersist = DateTime.Now;
            Persist();
        }

        public void Persist()
        {
            _lastPersist = DateTime.Now;
            List<DownloadDto> dtos = new List<DownloadDto>();
            lock (_lock)
            {
                foreach (DownloadTask t in _tasks)
                {
                    if (t.State == DownloadState.Cancelled) continue;
                    DownloadDto d = new DownloadDto
                    {
                        Id = t.Id,
                        Url = t.Url,
                        FileName = t.FileName,
                        TargetPath = t.TargetPath,
                        Referrer = t.Referrer,
                        MimeType = t.MimeType,
                        SourcePage = t.SourcePage,
                        TotalBytes = t.TotalBytes,
                        ReceivedBytes = t.ReceivedBytes,
                        State = (t.State == DownloadState.Downloading || t.State == DownloadState.Starting ||
                                 t.State == DownloadState.Stitching || t.State == DownloadState.Verifying)
                            ? "Queued" : t.State.ToString(),
                        Sha256 = t.Sha256,
                        RangesSupported = t.RangesSupported,
                        SegmentCount = t.SegmentCount,
                        IsHls = t.IsHls,
                        HlsDone = t.HlsDone,
                        HlsTotal = t.HlsTotal,
                        QueuedAt = t.QueuedAt,
                        CompletedAt = t.CompletedAt
                    };
                    foreach (DownloadSegment s in t.Segments)
                        d.Segments.Add(new SegmentDto { Index = s.Index, Start = s.Start, End = s.End, Received = s.Received });
                    dtos.Add(d);
                }
            }
            try
            {
                string tmp = AppPaths.DownloadsJson + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(dtos, SettingsManager.JsonOptions));
                if (File.Exists(AppPaths.DownloadsJson)) File.Replace(tmp, AppPaths.DownloadsJson, null);
                else File.Move(tmp, AppPaths.DownloadsJson);
            }
            catch (Exception ex)
            {
                Swifter.Log.Error("downloads persist failed: " + ex.Message);
            }
        }

        public void RestorePersistedTasks()
        {
            try
            {
                if (!File.Exists(AppPaths.DownloadsJson)) return;
                List<DownloadDto> dtos = JsonSerializer.Deserialize<List<DownloadDto>>(
                    File.ReadAllText(AppPaths.DownloadsJson), SettingsManager.JsonOptions);
                if (dtos == null) return;
                lock (_lock)
                {
                    foreach (DownloadDto d in dtos)
                    {
                        DownloadTask t = new DownloadTask
                        {
                            Url = d.Url,
                            FileName = d.FileName,
                            TargetPath = d.TargetPath,
                            Referrer = d.Referrer,
                            MimeType = d.MimeType,
                            SourcePage = d.SourcePage,
                            TotalBytes = d.TotalBytes,
                            ReceivedBytes = d.ReceivedBytes,
                            Sha256 = d.Sha256,
                            RangesSupported = d.RangesSupported,
                            SegmentCount = d.SegmentCount,
                            IsHls = d.IsHls,
                            HlsDone = d.HlsDone,
                            HlsTotal = d.HlsTotal,
                            QueuedAt = d.QueuedAt,
                            CompletedAt = d.CompletedAt
                        };
                        foreach (SegmentDto s in d.Segments)
                            t.Segments.Add(new DownloadSegment
                            {
                                Index = s.Index, Start = s.Start, End = s.End, Received = s.Received,
                                State = s.Received > 0 ? "idle" : "idle"
                            });
                        DownloadState st;
                        t.State = Enum.TryParse(d.State, out st) ? st : DownloadState.Queued;
                        if (d.Id.Length > 0) t.Id = d.Id;
                        if (t.State == DownloadState.Completed && !File.Exists(t.TargetPath))
                        {
                            t.State = DownloadState.Failed;
                            t.Error = "File missing after restart";
                        }
                        _tasks.Add(t);
                    }
                }
                Pump();
            }
            catch (Exception ex)
            {
                Swifter.Log.Error("downloads restore failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Shell helpers
        // ------------------------------------------------------------------

        public static void OpenFile(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Cannot open file: " + ex.Message, "Swifter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public static void ShowInFolder(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
                { UseShellExecute = true });
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Clipboard link sniffer
        // ------------------------------------------------------------------

        private static readonly Regex FileLinkRegex = new Regex(
            @"https?://[^\s""'<>]+?\.(?:zip|exe|iso|mp4|mkv|mov|avi|webm|pdf|rar|7z|apk|mp3|flac|m4a|ogg|wav|" +
            @"msi|dmg|tar|gz|bz2|xz|deb|rpm|img|bin|epub|mobi|docx?|xlsx?|pptx?)(?:\?[^\s""'<>]*)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public void StartClipboardSniffer()
        {
            if (_clipboard != null) return;
            if (!_settings.Model.General.ClipboardSniffer) return;
            try
            {
                _clipboard = new ClipboardMonitor(OnClipboardChanged);
            }
            catch (Exception ex)
            {
                Swifter.Log.Error("clipboard sniffer failed: " + ex.Message);
            }
        }

        public void StopClipboardSniffer()
        {
            if (_clipboard != null) { _clipboard.Dispose(); _clipboard = null; }
        }

        private string _lastClipboard = "";

        private void OnClipboardChanged()
        {
            if (!_settings.Model.General.ClipboardSniffer) return;
            string text;
            try
            {
                if (!Clipboard.ContainsText()) return;
                text = Clipboard.GetText();
            }
            catch { return; }
            if (string.IsNullOrWhiteSpace(text) || text == _lastClipboard) return;
            _lastClipboard = text;
            SniffText(text);
        }

        /// <summary>Raises LinkSniffed for every direct file URL found in the text.</summary>
        public void SniffText(string text)
        {
            foreach (Match m in FileLinkRegex.Matches(text ?? ""))
            {
                string url = m.Value;
                string name = "";
                int slash = url.LastIndexOf('/');
                if (slash >= 0 && slash + 1 < url.Length) name = Uri.UnescapeDataString(url.Substring(slash + 1));
                int q = name.IndexOf('?');
                if (q >= 0) name = name.Substring(0, q);
                Action<string, string> h = LinkSniffed;
                if (h != null) h(url, name);
            }
        }

        /// <summary>Hidden message-only window listening to WM_CLIPBOARDUPDATE.</summary>
        private sealed class ClipboardMonitor : NativeWindow, IDisposable
        {
            private const int WM_CLIPBOARDUPDATE = 0x031D;

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool AddClipboardFormatListener(IntPtr hwnd);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

            private readonly Action _callback;

            public ClipboardMonitor(Action callback)
            {
                _callback = callback;
                CreateParams cp = new CreateParams();
                cp.Caption = "SwifterClipboardSniffer";
                CreateHandle(cp);
                AddClipboardFormatListener(Handle);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_CLIPBOARDUPDATE)
                {
                    try { _callback(); } catch { }
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                try { RemoveClipboardFormatListener(Handle); } catch { }
                try { DestroyHandle(); } catch { }
            }
        }
    }
}
