using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Swifter.Engine
{
    public enum MediaKind { Video, Audio, Stream, Playlist, Unknown }

    public sealed class SniffedMedia
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Url { get; set; } = "";
        public MediaKind Kind { get; set; } = MediaKind.Unknown;
        public string MimeType { get; set; } = "";
        public long SizeBytes { get; set; } = -1;
        public string PageUrl { get; set; } = "";
        public string PageTitle { get; set; } = "";
        public string TabId { get; set; } = "";
        public long DetectedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public string Hint { get; set; } = "";
    }

    /// <summary>
    /// Watches every network request and the DOM of each page for streaming media
    /// (MP4/WebM/MKV/MP3/FLAC, HLS .m3u8, DASH .mpd, raw .ts segments) and keeps a
    /// de-duplicated catalogue the download manager can pull from with one click.
    /// </summary>
    public sealed class MediaSniffer
    {
        private readonly object _lock = new object();
        private readonly List<SniffedMedia> _items = new List<SniffedMedia>();
        private const int MaxItems = 300;

        public event Action Changed;

        private static readonly string[] VideoExt =
            { ".mp4", ".webm", ".mkv", ".mov", ".avi", ".m4v", ".ts", ".m2ts", ".flv", ".wmv" };
        private static readonly string[] AudioExt =
            { ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".oga", ".aac", ".opus" };
        private static readonly string[] PlaylistExt = { ".m3u8", ".mpd", ".m3u", ".f4m" };

        public static MediaKind KindOf(string url, string mime)
        {
            string u = (url ?? "").ToLowerInvariant();
            string m = (mime ?? "").ToLowerInvariant();
            int q = u.IndexOf('?');
            string path = q >= 0 ? u.Substring(0, q) : u;
            if (m.Contains("mpegurl") || m.Contains("m3u8") || path.EndsWith(".m3u8") || path.EndsWith(".m3u"))
                return MediaKind.Playlist;
            if (m.Contains("dash") || path.EndsWith(".mpd")) return MediaKind.Playlist;
            if (m.StartsWith("video/") || Array.Exists(VideoExt, e => path.EndsWith(e))) return MediaKind.Video;
            if (m.StartsWith("audio/") || Array.Exists(AudioExt, e => path.EndsWith(e))) return MediaKind.Audio;
            if (m.Contains("octet-stream") && Array.Exists(VideoExt, e => path.EndsWith(e))) return MediaKind.Video;
            if (path.EndsWith(".ts")) return MediaKind.Stream;
            return MediaKind.Unknown;
        }

        public static bool IsMediaCandidate(string url, string mime)
        {
            return KindOf(url, mime) != MediaKind.Unknown;
        }

        public void ObserveRequest(string tabId, string pageUrl, string pageTitle, string url, string method)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            MediaKind kind = KindOf(url, "");
            if (kind == MediaKind.Unknown) return;
            Add(tabId, pageUrl, pageTitle, url, kind, "", -1,
                string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) ? "network" : "network");
        }

        public void ObserveResponse(string tabId, string pageUrl, string pageTitle, string url,
            string mime, long length)
        {
            MediaKind kind = KindOf(url, mime);
            if (kind == MediaKind.Unknown) return;
            Add(tabId, pageUrl, pageTitle, url, kind, mime, length, "response");
        }

        /// <summary>Consumes the JSON payload posted by the injected DOM probe.</summary>
        public void ObserveDom(string tabId, string pageUrl, string pageTitle, string json)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    JsonElement items;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("media", out items)) return;
                    if (items.ValueKind != JsonValueKind.Array) return;
                    foreach (JsonElement el in items.EnumerateArray())
                    {
                        string url = el.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? "" : "";
                        string mime = el.TryGetProperty("mime", out JsonElement mm) ? mm.GetString() ?? "" : "";
                        string hint = el.TryGetProperty("hint", out JsonElement h) ? h.GetString() ?? "" : "";
                        double size = el.TryGetProperty("size", out JsonElement s) && s.ValueKind == JsonValueKind.Number
                            ? s.GetDouble() : -1;
                        if (url.Length == 0) continue;
                        MediaKind kind = KindOf(url, mime);
                        if (kind == MediaKind.Unknown) kind = mime.StartsWith("video") ? MediaKind.Video : MediaKind.Audio;
                        Add(tabId, pageUrl, pageTitle, url, kind, mime, (long)size, hint);
                    }
                }
            }
            catch (Exception ex)
            {
                Swifter.Log.Error("media probe parse: " + ex.Message);
            }
        }

        private void Add(string tabId, string pageUrl, string pageTitle, string url, MediaKind kind,
            string mime, long size, string hint)
        {
            bool changed = false;
            lock (_lock)
            {
                SniffedMedia existing = _items.FirstOrDefault(i => i.Url == url && i.TabId == tabId);
                if (existing != null)
                {
                    if (size > 0 && existing.SizeBytes <= 0) { existing.SizeBytes = size; changed = true; }
                    if (mime.Length > 0 && existing.MimeType.Length == 0) { existing.MimeType = mime; changed = true; }
                    if (hint.Length > 0 && existing.Hint.Length == 0) { existing.Hint = hint; changed = true; }
                }
                else
                {
                    _items.Insert(0, new SniffedMedia
                    {
                        TabId = tabId ?? "",
                        PageUrl = pageUrl ?? "",
                        PageTitle = pageTitle ?? "",
                        Url = url,
                        Kind = kind,
                        MimeType = mime,
                        SizeBytes = size,
                        Hint = hint
                    });
                    if (_items.Count > MaxItems) _items.RemoveAt(_items.Count - 1);
                    changed = true;
                }
            }
            if (changed)
            {
                Action h = Changed;
                if (h != null) h();
            }
        }

        public List<SniffedMedia> ForTab(string tabId)
        {
            lock (_lock) return _items.Where(i => i.TabId == tabId).ToList();
        }

        public List<SniffedMedia> All()
        {
            lock (_lock) return new List<SniffedMedia>(_items);
        }

        public int CountFor(string tabId)
        {
            lock (_lock) return _items.Count(i => i.TabId == tabId);
        }

        public void ClearTab(string tabId)
        {
            lock (_lock) _items.RemoveAll(i => i.TabId == tabId);
            Action h = Changed;
            if (h != null) h();
        }

        public void ClearAll()
        {
            lock (_lock) _items.Clear();
            Action h = Changed;
            if (h != null) h();
        }

        /// <summary>
        /// DOM probe injected into every http(s) page: reports media elements,
        /// their currentSrc/blob sources, performance entries and keeps watching
        /// the DOM for dynamically inserted players.
        /// </summary>
        public const string ProbeScript = """
            (function () {
              if (window.__swifterMediaProbe) return;
              window.__swifterMediaProbe = true;
              var seen = {};
              function send(url, mime, hint, size) {
                if (!url || seen[url]) return;
                if (url.indexOf('blob:') === 0 && !window.__swifterBlobOk) { hint = hint + ' (blob)'; }
                seen[url] = true;
                try {
                  window.chrome.webview.postMessage(JSON.stringify({
                    t: 'media', url: url, mime: mime || '', hint: hint || '', size: size || -1
                  }));
                } catch (e) { }
              }
              function scanRoot(root) {
                try {
                  var nodes = root.querySelectorAll ? root.querySelectorAll('video, audio, source, track') : [];
                  for (var i = 0; i < nodes.length; i++) {
                    var n = nodes[i];
                    var src = n.currentSrc || n.src || n.getAttribute('src') || '';
                    if (src) send(src, n.type || '', n.tagName.toLowerCase());
                  }
                } catch (e) { }
              }
              function scanPerf() {
                try {
                  var entries = performance.getEntriesByType('resource');
                  for (var i = 0; i < entries.length; i++) {
                    var e = entries[i];
                    var u = (e.name || '').toLowerCase();
                    if (/\.(mp4|webm|mkv|mov|m4v|ts|m3u8|mpd|mp3|m4a|flac|ogg|aac|opus)(\?|$)/.test(u)) {
                      send(e.name, '', 'perf', e.transferSize || -1);
                    }
                  }
                } catch (e) { }
              }
              function scanAll() { scanRoot(document); scanPerf(); }
              scanAll();
              document.addEventListener('DOMContentLoaded', scanAll);
              window.addEventListener('load', function () { scanAll(); setTimeout(scanAll, 3000); });
              try {
                var mo = new MutationObserver(function (muts) {
                  for (var i = 0; i < muts.length; i++) {
                    var added = muts[i].addedNodes;
                    for (var j = 0; j < added.length; j++) {
                      var node = added[j];
                      if (node.nodeType !== 1) continue;
                      if (node.tagName === 'VIDEO' || node.tagName === 'AUDIO' || node.tagName === 'SOURCE') {
                        send(node.currentSrc || node.src || '', node.type || '', node.tagName.toLowerCase());
                      }
                      if (node.querySelectorAll) scanRoot(node);
                    }
                  }
                });
                mo.observe(document.documentElement, { childList: true, subtree: true });
              } catch (e) { }
              setInterval(scanPerf, 8000);
            })();
            """;
    }
}
