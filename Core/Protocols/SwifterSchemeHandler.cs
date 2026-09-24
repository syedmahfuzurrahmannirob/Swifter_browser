using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Web.WebView2.Core;
using Swifter.Config;
using Swifter.Engine;
using Swifter.Protocols.InternalPages;
using Swifter.Storage;

namespace Swifter.Protocols
{
    /// <summary>
    /// Serves the swifter:// scheme through CoreWebView2.WebResourceRequested and
    /// hosts the JSON message bridge every internal page talks to the host with.
    /// </summary>
    public static class SwifterSchemeHandler
    {
        public const string Scheme = "swifter";

        public static void Attach(CoreWebView2 core)
        {
            core.AddWebResourceRequestedFilter(Scheme + ":*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
        }

        private static void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string uri = e.Request.Uri;
            if (uri == null || !uri.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase)) return;
            CoreWebView2Deferral deferral = e.GetDeferral();
            CoreWebView2Environment env = ((CoreWebView2)sender).Environment;
            Task.Run(() =>
            {
                try
                {
                    PageRender render = Render(uri);
                    Stream stream = new MemoryStream(render.Bytes);
                    string headers = "Content-Type: " + render.ContentType + "\n" +
                                     "Cache-Control: no-store\n" +
                                     "Access-Control-Allow-Origin: *";
                    e.Response = env.CreateWebResourceResponse(stream, render.Status, render.StatusText, headers);
                }
                catch (Exception ex)
                {
                    Log.Error("scheme handler: " + ex.Message);
                    byte[] err = Encoding.UTF8.GetBytes("<h1>swifter:// error</h1><p>" + ex.Message + "</p>");
                    e.Response = env.CreateWebResourceResponse(new MemoryStream(err), 500, "Internal Error",
                        "Content-Type: text/html; charset=utf-8");
                }
                finally
                {
                    deferral.Complete();
                }
            });
        }

        public sealed class PageRender
        {
            public byte[] Bytes = new byte[0];
            public string ContentType = "text/html; charset=utf-8";
            public int Status = 200;
            public string StatusText = "OK";
        }

        public static bool IsInternal(string url)
        {
            return url != null && url.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase);
        }

        public static string PageName(string url)
        {
            try
            {
                Uri u = new Uri(url);
                string host = u.Host;
                if (host.Length == 0) host = url.Substring((Scheme + "://").Length);
                int slash = host.IndexOf('/');
                return (slash >= 0 ? host.Substring(0, slash) : host).ToLowerInvariant();
            }
            catch
            {
                return "";
            }
        }

        public static PageRender Render(string url)
        {
            SettingsManager settings = AppServices.Settings;
            string name = PageName(url);
            string html;
            switch (name)
            {
                case "newtab": html = NewTabPage.Render(settings, Query(url)); break;
                case "downloads": html = DownloadsPage.Render(settings); break;
                case "history": html = HistoryPage.Render(settings, Query(url)); break;
                case "bookmarks": html = BookmarksPage.Render(settings); break;
                case "shields": html = ShieldsPage.Render(settings); break;
                case "settings": html = SettingsPage.Render(settings); break;
                case "asset": return Asset(url);
                case "": html = NewTabPage.Render(settings, new Dictionary<string, string>()); break;
                default:
                    html = ProtocolPageRenderer.Page("Not found",
                        ProtocolPageRenderer.PageHeader("swifter", "Unknown page", url) +
                        "<div class=\"wrap\"><div class=\"card empty\"><div class=\"big\">404</div>" +
                        "This internal page does not exist.<br><br>" +
                        "<button class=\"primary\" onclick=\"Swifter.call('open-internal',{page:'newtab'})\">" +
                        "Open new tab</button></div></div>",
                        "", "", settings);
                    return new PageRender { Bytes = Encoding.UTF8.GetBytes(html), Status = 404, StatusText = "Not Found" };
            }
            return new PageRender { Bytes = Encoding.UTF8.GetBytes(html) };
        }

        private static PageRender Asset(string url)
        {
            string rest = url.Substring((Scheme + "://").Length);
            if (rest.StartsWith("asset/", StringComparison.OrdinalIgnoreCase))
                rest = rest.Substring(6);
            if (rest == "logo.png")
            {
                EmbeddedBytes logo = new EmbeddedBytes("Swifter.Assets.logo.png");
                if (logo.Data != null)
                    return new PageRender { Bytes = logo.Data, ContentType = "image/png" };
            }
            if (rest == "app.ico")
            {
                EmbeddedBytes ico = new EmbeddedBytes("Swifter.Assets.app.ico");
                if (ico.Data != null)
                    return new PageRender { Bytes = ico.Data, ContentType = "image/x-icon" };
            }
            return new PageRender { Bytes = Encoding.UTF8.GetBytes("not found"), Status = 404, StatusText = "Not Found" };
        }

        private sealed class EmbeddedBytes
        {
            public byte[] Data;

            public EmbeddedBytes(string resource)
            {
                using (Stream s = typeof(SwifterSchemeHandler).Assembly.GetManifestResourceStream(resource))
                {
                    if (s == null) return;
                    using (MemoryStream ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        Data = ms.ToArray();
                    }
                }
            }
        }

        public static Dictionary<string, string> Query(string url)
        {
            Dictionary<string, string> q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Uri u = new Uri(url);
                string query = u.Query;
                if (query.Length > 1)
                {
                    foreach (string pair in query.Substring(1).Split('&'))
                    {
                        int eq = pair.IndexOf('=');
                        if (eq > 0)
                            q[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1));
                    }
                }
            }
            catch { }
            return q;
        }

        /// <summary>Runtime fallback when the custom scheme registration was rejected.</summary>
        public static void NavigateFallback(CoreWebView2 core, string url)
        {
            try
            {
                PageRender render = Render(url);
                core.NavigateToString(Encoding.UTF8.GetString(render.Bytes));
            }
            catch (Exception ex)
            {
                Log.Error("fallback navigate: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// JSON request/response router between internal pages and the C# engines.
    /// Pages call <c>Swifter.call(action, payload)</c>; the host answers with
    /// <c>{t:'res', id, ok, data}</c> and pushes <c>{t:'evt', name, data}</c> updates.
    /// </summary>
    public static class SwifterBridge
    {
        public static async Task HandleAsync(CoreWebView2 core, string json)
        {
            long id = 0;
            string action = "";
            JsonElement payload = default(JsonElement);
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    JsonElement t;
                    if (root.TryGetProperty("t", out t) && t.GetString() == "media")
                    {
                        AppServices.Sniffer.ObserveDom(CurrentTabId(core), core.Source ?? "",
                            core.DocumentTitle ?? "", json);
                        return;
                    }
                    if (root.TryGetProperty("id", out t) && t.ValueKind == JsonValueKind.Number) id = t.GetInt64();
                    if (root.TryGetProperty("action", out t)) action = t.GetString() ?? "";
                    if (root.TryGetProperty("payload", out t)) payload = t;
                }
            }
            catch (Exception ex)
            {
                Log.Error("bridge parse: " + ex.Message);
                return;
            }

            try
            {
                object data = await RouteAsync(core, action, payload).ConfigureAwait(true);
                Respond(core, id, true, data, null);
            }
            catch (Exception ex)
            {
                Log.Error("bridge action '" + action + "': " + ex.Message);
                Respond(core, id, false, null, ex.Message);
            }
        }

        private static string CurrentTabId(CoreWebView2 core)
        {
            BrowserForm form = AppServices.MainWindow;
            if (form == null) return "";
            return form.TabIdFor(core) ?? "";
        }

        private static void Respond(CoreWebView2 core, long id, bool ok, object data, string error)
        {
            Dictionary<string, object> msg = new Dictionary<string, object>
            {
                { "t", "res" }, { "id", id }, { "ok", ok }
            };
            if (ok) msg["data"] = data;
            else msg["error"] = error ?? "error";
            try { core.PostWebMessageAsJson(ProtocolPageRenderer.ToJson(msg)); } catch { }
        }

        public static void PushEvent(CoreWebView2 core, string name, object data)
        {
            Dictionary<string, object> msg = new Dictionary<string, object>
            {
                { "t", "evt" }, { "name", name }, { "data", data }
            };
            try { core.PostWebMessageAsJson(ProtocolPageRenderer.ToJson(msg)); } catch { }
        }

        // ------------------------------------------------------------------

        private static async Task<object> RouteAsync(CoreWebView2 core, string action, JsonElement p)
        {
            SettingsManager settings = AppServices.Settings;
            BrowserForm form = AppServices.MainWindow;
            switch (action)
            {
                // ---- navigation / window ------------------------------------
                case "navigate":
                    form.Navigate(GetStr(p, "url"));
                    return true;
                case "newtab":
                    form.OpenNewTab(GetStr(p, "url"), true);
                    return true;
                case "close-tab":
                    form.CloseActiveTab();
                    return true;
                case "open-internal":
                    form.Navigate(SwifterSchemeHandler.Scheme + "://" + GetStr(p, "page"));
                    return true;
                case "reload":
                    form.ReloadActive();
                    return true;
                case "back":
                    form.GoBack();
                    return true;
                case "forward":
                    form.GoForward();
                    return true;

                // ---- search -------------------------------------------------
                case "search":
                    {
                        string engine = GetStr(p, "engine");
                        string q = GetStr(p, "query");
                        SearchEngine se = string.IsNullOrEmpty(engine) ? settings.CurrentSearchEngine() : settings.EngineById(engine);
                        string url = string.Format(CultureInfo.InvariantCulture, se.UrlTemplate, Uri.EscapeDataString(q));
                        form.Navigate(url);
                        return url;
                    }
                case "search.engines":
                    {
                        List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
                        foreach (SearchEngine e in settings.Model.SearchEngines)
                            list.Add(new Dictionary<string, object> { { "id", e.Id }, { "name", e.Name } });
                        return new Dictionary<string, object>
                        {
                            { "engines", list }, { "current", settings.Model.NewTab.SearchEngine }
                        };
                    }
                case "search.set":
                    settings.Model.NewTab.SearchEngine = GetStr(p, "id");
                    settings.SaveLater();
                    return true;

                // ---- new tab widgets ----------------------------------------
                case "quickdial.get":
                    {
                        List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
                        foreach (QuickDialItem qi in settings.Model.NewTab.QuickDial)
                            items.Add(new Dictionary<string, object> { { "title", qi.Title }, { "url", qi.Url } });
                        return items;
                    }
                case "quickdial.set":
                    {
                        settings.Model.NewTab.QuickDial.Clear();
                        foreach (JsonElement el in GetArr(p, "items"))
                        {
                            settings.Model.NewTab.QuickDial.Add(new QuickDialItem
                            {
                                Title = PropStr(el, "title"),
                                Url = PropStr(el, "url")
                            });
                        }
                        settings.SaveLater();
                        return true;
                    }
                case "wallpaper.get":
                    return ResolveWallpaper(settings);
                case "wallpaper.set":
                    {
                        string v = GetStr(p, "value");
                        if (v.StartsWith("upload:", StringComparison.Ordinal))
                        {
                            string path = await PickFileAsync(form, "Select wallpaper",
                                "Images|*.png;*.jpg;*.jpeg;*.webp").ConfigureAwait(true);
                            if (path.Length == 0) return ResolveWallpaper(settings);
                            string dest = Path.Combine(AppPaths.WallpapersDir, Path.GetFileName(path));
                            File.Copy(path, dest, true);
                            v = "file:" + dest;
                        }
                        settings.Model.NewTab.Wallpaper = v;
                        settings.SaveLater();
                        return ResolveWallpaper(settings);
                    }
                case "notepad.get":
                    return settings.Model.NewTab.NotepadText;
                case "notepad.set":
                    settings.Model.NewTab.NotepadText = GetStr(p, "text");
                    settings.SaveLater();
                    return true;
                case "rss.fetch":
                    return await FetchRssAsync(GetStr(p, "url")).ConfigureAwait(true);
                case "topsitedata":
                    {
                        List<DomainStat> top = AppServices.History.TopSites(8);
                        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
                        foreach (DomainStat d in top)
                            rows.Add(new Dictionary<string, object>
                            {
                                { "domain", d.Domain }, { "visits", d.Visits },
                                { "icon", string.IsNullOrEmpty(d.Favicon)
                                    ? ProtocolPageRenderer.LetterIcon(d.Domain, d.Domain) : d.Favicon }
                            });
                        return rows;
                    }

                // ---- history --------------------------------------------------
                case "history.search":
                    return HistoryRows(AppServices.History.Search(GetStr(p, "q"), GetInt(p, "limit", 60)));
                case "history.range":
                    {
                        long from = GetLong(p, "from", 0);
                        long to = GetLong(p, "to", long.MaxValue);
                        return HistoryRows(AppServices.History.ListRange(from, to, GetInt(p, "limit", 200)));
                    }
                case "history.recent":
                    return HistoryRows(AppServices.History.ListRecent(GetInt(p, "limit", 12)));
                case "history.stats":
                    {
                        int days = GetInt(p, "days", 14);
                        List<KeyValuePair<string, int>> daily = new List<KeyValuePair<string, int>>();
                        foreach (DayStat d in AppServices.History.DailyStats(days)) daily.Add(new KeyValuePair<string, int>(d.Day, d.Visits));
                        List<KeyValuePair<string, int>> hourly = new List<KeyValuePair<string, int>>();
                        foreach (HourStat h in AppServices.History.HourlyStats(days))
                            hourly.Add(new KeyValuePair<string, int>(h.Hour.ToString("00", CultureInfo.InvariantCulture), h.Visits));
                        List<Dictionary<string, object>> domains = new List<Dictionary<string, object>>();
                        foreach (DomainStat d in AppServices.History.TopDomains(8, days))
                            domains.Add(new Dictionary<string, object>
                            {
                                { "domain", d.Domain }, { "visits", d.Visits }, { "seconds", d.Seconds }
                            });
                        return new Dictionary<string, object>
                        {
                            { "total", AppServices.History.TotalVisits() },
                            { "daily", daily }, { "hourly", hourly }, { "domains", domains }
                        };
                    }
                case "history.delete":
                    AppServices.History.DeleteVisit(GetLong(p, "id", -1));
                    return true;
                case "history.deleteDomain":
                    AppServices.History.DeleteDomain(GetStr(p, "domain"));
                    return true;
                case "history.clear":
                    AppServices.History.ClearAll();
                    return true;
                case "history.export":
                    {
                        string format = GetStr(p, "format");
                        string dir = settings.Model.General.DownloadPath;
                        string stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
                        string path = Path.Combine(dir, "swifter-history-" + stamp + "." + format);
                        if (format == "json") AppServices.History.ExportJson(path);
                        else AppServices.History.ExportCsv(path);
                        return path;
                    }

                // ---- bookmarks ------------------------------------------------
                case "bookmarks.tree":
                    return BookmarkTree(settings);
                case "bookmarks.add":
                    {
                        BookmarkNode n = AppServices.Bookmarks.Add(GetStr(p, "parentId"), GetStr(p, "title"),
                            GetStr(p, "url"), StrList(p, "tags"));
                        return new Dictionary<string, object> { { "id", n.Id } };
                    }
                case "bookmarks.addFolder":
                    {
                        BookmarkNode n = AppServices.Bookmarks.AddFolder(GetStr(p, "parentId"), GetStr(p, "title"));
                        return new Dictionary<string, object> { { "id", n.Id } };
                    }
                case "bookmarks.update":
                    {
                        BookmarkNode n = AppServices.Bookmarks.Find(GetStr(p, "id"));
                        if (n == null) return false;
                        if (p.TryGetProperty("title", out JsonElement tt)) n.Title = tt.GetString() ?? n.Title;
                        if (p.TryGetProperty("url", out JsonElement uu)) n.Url = uu.GetString() ?? n.Url;
                        if (p.TryGetProperty("tags", out JsonElement tg)) n.Tags = StrListFromElement(tg);
                        if (p.TryGetProperty("favorite", out JsonElement fv)) n.IsFavorite = fv.GetBoolean();
                        if (p.TryGetProperty("bar", out JsonElement bv)) n.InBar = bv.GetBoolean();
                        AppServices.Bookmarks.Update(n);
                        return true;
                    }
                case "bookmarks.delete":
                    AppServices.Bookmarks.Delete(GetStr(p, "id"));
                    return true;
                case "bookmarks.move":
                    AppServices.Bookmarks.Move(GetStr(p, "id"), GetStr(p, "parentId"), GetInt(p, "index", -1));
                    return true;
                case "bookmarks.search":
                    return BookmarkRows(AppServices.Bookmarks.Search(GetStr(p, "q")));
                case "bookmarks.tags":
                    return AppServices.Bookmarks.AllTags();
                case "bookmarks.byTag":
                    return BookmarkRows(AppServices.Bookmarks.ByTag(GetStr(p, "tag")));
                case "bookmarks.favorites":
                    return BookmarkRows(AppServices.Bookmarks.Favorites());
                case "bookmarks.bar":
                    return BookmarkRows(AppServices.Bookmarks.BarItems());
                case "bookmarks.top":
                    return BookmarkRows(AppServices.Bookmarks.TopUsed(8));
                case "bookmarks.touch":
                    AppServices.Bookmarks.Touch(GetStr(p, "id"));
                    return true;
                case "bookmarks.export":
                    {
                        string path = GetStr(p, "path");
                        if (path.Length == 0)
                            path = Path.Combine(settings.Model.General.DownloadPath,
                                "swifter-bookmarks-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".html");
                        AppServices.Bookmarks.ExportToFile(path);
                        return path;
                    }
                case "bookmarks.import":
                    {
                        string html = GetStr(p, "html");
                        if (html.Length == 0)
                        {
                            string path = await PickFileAsync(form, "Import bookmarks (Netscape HTML)",
                                "HTML files|*.html;*.htm").ConfigureAwait(true);
                            if (path.Length == 0) return 0;
                            html = File.ReadAllText(path);
                        }
                        return AppServices.Bookmarks.ImportNetscape(html);
                    }

                // ---- downloads --------------------------------------------------
                case "downloads.snapshot":
                    return DownloadSnapshot(settings);
                case "downloads.enqueue":
                    {
                        DownloadTask t = AppServices.Downloads.Enqueue(GetStr(p, "url"), GetStr(p, "fileName"),
                            GetStr(p, "dir"), GetStr(p, "referrer"), GetStr(p, "source"), GetBool(p, "hls"));
                        return new Dictionary<string, object> { { "id", t.Id } };
                    }
                case "downloads.pause": AppServices.Downloads.Pause(GetStr(p, "id")); return true;
                case "downloads.resume": AppServices.Downloads.Resume(GetStr(p, "id")); return true;
                case "downloads.startNow": AppServices.Downloads.StartNow(GetStr(p, "id")); return true;
                case "downloads.cancel": AppServices.Downloads.Cancel(GetStr(p, "id")); return true;
                case "downloads.retry": AppServices.Downloads.Retry(GetStr(p, "id")); return true;
                case "downloads.remove":
                    AppServices.Downloads.Remove(GetStr(p, "id"), GetBool(p, "deleteFile"));
                    return true;
                case "downloads.clearCompleted":
                    return AppServices.Downloads.ClearCompleted();
                case "downloads.open":
                    {
                        DownloadTask t = AppServices.Downloads.Find(GetStr(p, "id"));
                        if (t != null) DownloadQueueManager.OpenFile(t.TargetPath);
                        return true;
                    }
                case "downloads.showFolder":
                    {
                        DownloadTask t = AppServices.Downloads.Find(GetStr(p, "id"));
                        if (t != null) DownloadQueueManager.ShowInFolder(t.TargetPath);
                        return true;
                    }
                case "downloads.copyUrl":
                    {
                        DownloadTask t = AppServices.Downloads.Find(GetStr(p, "id"));
                        if (t != null) SafeClipboard(t.Url);
                        return true;
                    }
                case "downloads.setThreads":
                    AppServices.Downloads.SetSegmentCount(GetInt(p, "n", 16));
                    return true;
                case "downloads.setMaxActive":
                    AppServices.Downloads.SetMaxActive(GetInt(p, "n", 3));
                    return true;
                case "downloads.setThrottle":
                    AppServices.Downloads.SetThrottleKib(GetInt(p, "kib", 0));
                    return true;
                case "downloads.scheduler":
                    {
                        settings.Model.Downloads.SchedulerEnabled = GetBool(p, "enabled");
                        if (p.TryGetProperty("start", out JsonElement s1)) settings.Model.Downloads.ScheduleStart = s1.GetString() ?? "22:00";
                        if (p.TryGetProperty("end", out JsonElement s2)) settings.Model.Downloads.ScheduleEnd = s2.GetString() ?? "06:00";
                        settings.SaveLater();
                        return true;
                    }
                case "downloads.sha":
                    {
                        DownloadTask t = AppServices.Downloads.Find(GetStr(p, "id"));
                        return t != null ? t.Sha256 : "";
                    }
                case "media.list":
                    {
                        List<SniffedMedia> items = string.IsNullOrEmpty(GetStr(p, "tab"))
                            ? AppServices.Sniffer.All() : AppServices.Sniffer.ForTab(GetStr(p, "tab"));
                        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
                        foreach (SniffedMedia m in items)
                            rows.Add(new Dictionary<string, object>
                            {
                                { "id", m.Id }, { "url", m.Url }, { "kind", m.Kind.ToString() },
                                { "mime", m.MimeType }, { "size", m.SizeBytes }, { "hint", m.Hint },
                                { "page", m.PageTitle }, { "pageUrl", m.PageUrl }, { "at", m.DetectedAt }
                            });
                        return rows;
                    }
                case "media.clear":
                    AppServices.Sniffer.ClearAll();
                    return true;
                case "downloads.pickFolder":
                    return await PickFolderAsync(form).ConfigureAwait(true);
                case "downloads.setPath":
                    settings.Model.General.DownloadPath = GetStr(p, "path");
                    settings.SaveLater();
                    return true;

                // ---- shields -----------------------------------------------------
                case "shields.stats":
                    {
                        ShieldStats s = AppServices.Shields.Stats();
                        List<Dictionary<string, object>> hosts = new List<Dictionary<string, object>>();
                        List<KeyValuePair<string, long>> sorted = new List<KeyValuePair<string, long>>(s.PerHost);
                        sorted.Sort(delegate (KeyValuePair<string, long> a, KeyValuePair<string, long> b)
                        { return b.Value.CompareTo(a.Value); });
                        int n = 0;
                        foreach (KeyValuePair<string, long> kv in sorted)
                        {
                            if (n++ >= 10) break;
                            hosts.Add(new Dictionary<string, object> { { "host", kv.Key }, { "count", kv.Value } });
                        }
                        return new Dictionary<string, object>
                        {
                            { "ads", s.AdsBlocked }, { "trackers", s.TrackersBlocked },
                            { "cookies", s.CookiesBlocked }, { "bytes", s.BytesSaved },
                            { "seconds", s.SecondsSaved }, { "since", s.SinceUnixMs },
                            { "hosts", hosts },
                            { "enabled", settings.Model.Shields.Enabled }
                        };
                    }
                case "shields.setEnabled":
                    settings.Model.Shields.Enabled = GetBool(p, "enabled");
                    settings.SaveLater();
                    return true;
                case "shields.site":
                    {
                        string host = GetStr(p, "host");
                        return AppServices.Shields.SiteShieldsDisabled(host);
                    }
                case "shields.setSite":
                    AppServices.Shields.SetSiteOverride(GetStr(p, "host"), GetBool(p, "disabled"));
                    return true;
                case "shields.rules":
                    return AppServices.Shields.Rules();
                case "shields.addRule":
                    return AppServices.Shields.AddRule(GetStr(p, "rule"));
                case "shields.removeRule":
                    return AppServices.Shields.RemoveRule(GetStr(p, "rule"));
                case "shields.reset":
                    AppServices.Shields.ResetStats();
                    return true;
                case "shields.doh":
                    {
                        settings.Model.Privacy.Doh = ParseEnum(p, "provider", settings.Model.Privacy.Doh);
                        if (p.TryGetProperty("custom", out JsonElement c))
                            settings.Model.Privacy.DohCustomUrl = c.GetString() ?? settings.Model.Privacy.DohCustomUrl;
                        settings.SaveLater();
                        return settings.Model.Privacy.Doh.ToString();
                    }
                case "shields.resolve":
                    {
                        DohClient.DohAnswer a = await AppServices.Shields.Doh
                            .ResolveAsync(GetStr(p, "host"), AppServices.Shields.DohTemplate()).ConfigureAwait(true);
                        return new Dictionary<string, object>
                        {
                            { "host", a.Host }, { "addresses", a.Addresses },
                            { "latency", a.LatencyMs }, { "server", a.Server }
                        };
                    }
                case "shields.protections":
                    {
                        PrivacySettings priv = settings.Model.Privacy;
                        if (p.TryGetProperty("canvas", out JsonElement c1)) priv.CanvasDefense = c1.GetBoolean();
                        if (p.TryGetProperty("webrtc", out JsonElement c2)) priv.WebRtcLeakProtection = c2.GetBoolean();
                        if (p.TryGetProperty("https", out JsonElement c3)) priv.HttpsUpgrade = c3.GetBoolean();
                        if (p.TryGetProperty("cookies", out JsonElement c4)) priv.BlockThirdPartyCookies = c4.GetBoolean();
                        if (p.TryGetProperty("dnt", out JsonElement c5)) priv.SendDoNotTrack = c5.GetBoolean();
                        if (p.TryGetProperty("tracking", out JsonElement c6))
                            priv.TrackingLevel = ParseEnum(p, "tracking", priv.TrackingLevel);
                        settings.SaveLater();
                        form.ApplyPrivacySettings();
                        return true;
                    }

                // ---- settings / scripts -------------------------------------------
                case "settings.get":
                    return settings.Model;
                case "settings.patch":
                    {
                        ApplyPatch(settings, p);
                        settings.SaveLater();
                        form.ApplyAppearance();
                        form.ApplyPrivacySettings();
                        return true;
                    }
                case "scripts.list":
                    return settings.Model.UserScripts;
                case "scripts.add":
                    {
                        UserScript s = AppServices.Scripts.Create(GetStr(p, "name"), GetStr(p, "match"),
                            GetStr(p, "js"), GetStr(p, "css"), GetStr(p, "runAt"));
                        return s.Id;
                    }
                case "scripts.update":
                    {
                        UserScript s = AppServices.Scripts.Get(GetStr(p, "id"));
                        if (s == null) return false;
                        if (p.TryGetProperty("name", out JsonElement n1)) s.Name = n1.GetString() ?? s.Name;
                        if (p.TryGetProperty("enabled", out JsonElement n2)) s.Enabled = n2.GetBoolean();
                        if (p.TryGetProperty("js", out JsonElement n3)) s.JsCode = n3.GetString() ?? s.JsCode;
                        if (p.TryGetProperty("css", out JsonElement n4)) s.CssCode = n4.GetString() ?? s.CssCode;
                        if (p.TryGetProperty("runAt", out JsonElement n5)) s.RunAt = n5.GetString() ?? s.RunAt;
                        if (p.TryGetProperty("matches", out JsonElement n6)) s.Matches = StrListFromElement(n6);
                        AppServices.Scripts.Update(s);
                        return true;
                    }
                case "scripts.remove":
                    AppServices.Scripts.Remove(GetStr(p, "id"));
                    return true;

                // ---- session / misc -------------------------------------------------
                case "session.restore":
                    form.RestoreSession();
                    return true;
                case "session.dismiss":
                    form.DismissSessionPrompt();
                    return true;
                case "reader.configure":
                    {
                        ReaderSettings r = settings.Model.Reader;
                        if (p.TryGetProperty("fontSize", out JsonElement f1)) r.FontSize = f1.GetInt32();
                        if (p.TryGetProperty("width", out JsonElement f2)) r.ContentWidth = f2.GetInt32();
                        if (p.TryGetProperty("theme", out JsonElement f3)) r.Theme = f3.GetString() ?? r.Theme;
                        settings.SaveLater();
                        return true;
                    }
                case "clipboard.copy":
                    SafeClipboard(GetStr(p, "text"));
                    return true;
                case "about.info":
                    {
                        string runtime = "";
                        try { runtime = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
                        return new Dictionary<string, object>
                        {
                            { "version", "1.0.0" },
                            { "runtime", runtime },
                            { "dataDir", AppPaths.DataDir },
                            { "historyDb", AppPaths.HistoryDb },
                            { "framework", ".NET 8 (win-x64)" }
                        };
                    }
                case "clearBrowsingData":
                    {
                        CoreWebView2BrowsingDataKinds kinds = CoreWebView2BrowsingDataKinds.AllProfile;
                        await core.Profile.ClearBrowsingDataAsync(kinds).ConfigureAwait(true);
                        return true;
                    }
                case "pickFolder":
                    return await PickFolderAsync(form).ConfigureAwait(true);
                default:
                    throw new NotSupportedException("unknown action: " + action);
            }
        }

        // ------------------------------------------------------------------
        // Payload helpers
        // ------------------------------------------------------------------

        private static string GetStr(JsonElement p, string name)
        {
            JsonElement e;
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out e) &&
                e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
            return "";
        }

        private static bool GetBool(JsonElement p, string name)
        {
            JsonElement e;
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out e))
            {
                if (e.ValueKind == JsonValueKind.True) return true;
                if (e.ValueKind == JsonValueKind.False) return false;
                if (e.ValueKind == JsonValueKind.String) return e.GetString() == "true";
            }
            return false;
        }

        private static int GetInt(JsonElement p, string name, int fallback)
        {
            JsonElement e;
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out e))
            {
                if (e.ValueKind == JsonValueKind.Number)
                {
                    int v;
                    if (e.TryGetInt32(out v)) return v;
                }
                if (e.ValueKind == JsonValueKind.String)
                {
                    int v;
                    if (int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
                }
            }
            return fallback;
        }

        private static long GetLong(JsonElement p, string name, long fallback)
        {
            JsonElement e;
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out e) &&
                e.ValueKind == JsonValueKind.Number)
            {
                long v;
                if (e.TryGetInt64(out v)) return v;
            }
            return fallback;
        }

        private static JsonElement GetArr(JsonElement p, string name)
        {
            JsonElement e;
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out e) &&
                e.ValueKind == JsonValueKind.Array) return e;
            return default(JsonElement);
        }

        private static string PropStr(JsonElement el, string name)
        {
            JsonElement e;
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out e) &&
                e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
            return "";
        }

        private static List<string> StrList(JsonElement p, string name)
        {
            JsonElement arr = GetArr(p, name);
            return StrListFromElement(arr);
        }

        private static List<string> StrListFromElement(JsonElement arr)
        {
            List<string> list = new List<string>();
            if (arr.ValueKind != JsonValueKind.Array) return list;
            foreach (JsonElement e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString());
            return list;
        }

        private static T ParseEnum<T>(JsonElement p, string name, T fallback) where T : struct
        {
            JsonElement e;
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out e) &&
                e.ValueKind == JsonValueKind.String)
            {
                T v;
                if (Enum.TryParse(e.GetString(), true, out v)) return v;
            }
            return fallback;
        }

        // ------------------------------------------------------------------
        // Composite snapshots
        // ------------------------------------------------------------------

        private static object HistoryRows(List<HistoryEntry> rows)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            foreach (HistoryEntry e in rows)
                list.Add(new Dictionary<string, object>
                {
                    { "id", e.Id }, { "url", e.Url }, { "title", e.Title }, { "domain", e.Domain },
                    { "at", e.VisitedAt }, { "duration", e.DurationMs },
                    { "icon", IconFor(e.Domain) }
                });
            return list;
        }

        private static string IconFor(string domain)
        {
            string fav = AppServices.History.GetFavicon(domain);
            return string.IsNullOrEmpty(fav) ? ProtocolPageRenderer.LetterIcon(domain, domain) : fav;
        }

        private static object BookmarkRows(List<BookmarkNode> nodes)
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (BookmarkNode n in nodes) rows.Add(BookmarkJson(n));
            return rows;
        }

        private static Dictionary<string, object> BookmarkJson(BookmarkNode n)
        {
            return new Dictionary<string, object>
            {
                { "id", n.Id }, { "parent", n.ParentId }, { "folder", n.IsFolder },
                { "title", n.Title }, { "url", n.Url }, { "tags", n.Tags },
                { "favorite", n.IsFavorite }, { "bar", n.InBar }, { "order", n.Order },
                { "uses", n.UseCount },
                { "icon", n.IsFolder ? "" :
                    (string.IsNullOrEmpty(n.Favicon) ? ProtocolPageRenderer.LetterIcon(n.Title, n.Url) : n.Favicon) }
            };
        }

        private static object BookmarkTree(SettingsManager settings)
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (BookmarkNode n in AppServices.Bookmarks.All()) rows.Add(BookmarkJson(n));
            return new Dictionary<string, object> { { "root", AppServices.Bookmarks.RootId }, { "nodes", rows } };
        }

        private static object DownloadSnapshot(SettingsManager settings)
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (DownloadTask t in AppServices.Downloads.Snapshot())
            {
                List<Dictionary<string, object>> segs = new List<Dictionary<string, object>>();
                foreach (DownloadSegment s in t.Segments)
                    segs.Add(new Dictionary<string, object>
                    {
                        { "i", s.Index }, { "st", s.State },
                        { "p", s.End > s.Start ? (double)s.Received / (s.End - s.Start + 1) : (s.State == "done" ? 1.0 : 0.0) },
                        { "sp", s.SpeedBps }
                    });
                rows.Add(new Dictionary<string, object>
                {
                    { "id", t.Id }, { "url", t.Url }, { "file", t.FileName }, { "path", t.TargetPath },
                    { "total", t.TotalBytes }, { "received", t.ReceivedBytes },
                    { "speed", t.SpeedBps }, { "eta", t.EtaSeconds }, { "state", t.State.ToString() },
                    { "error", t.Error }, { "sha", t.Sha256 }, { "ranges", t.RangesSupported },
                    { "hls", t.IsHls }, { "hlsDone", t.HlsDone }, { "hlsTotal", t.HlsTotal },
                    { "progress", t.Progress }, { "segments", segs }, { "source", t.SourcePage },
                    { "queuedAt", t.QueuedAt }, { "completedAt", t.CompletedAt }
                });
            }
            return new Dictionary<string, object>
            {
                { "tasks", rows },
                { "threads", settings.Model.Downloads.SegmentCount },
                { "maxActive", settings.Model.Downloads.MaxActiveDownloads },
                { "throttle", settings.Model.Downloads.ThrottleKibPerSec },
                { "aggregate", AppServices.Downloads.AggregateSpeed },
                { "active", AppServices.Downloads.ActiveCount },
                { "verify", settings.Model.Downloads.VerifySha256 },
                { "scheduler", settings.Model.Downloads.SchedulerEnabled },
                { "scheduleStart", settings.Model.Downloads.ScheduleStart },
                { "scheduleEnd", settings.Model.Downloads.ScheduleEnd },
                { "inWindow", AppServices.Downloads.IsWithinScheduleWindow() },
                { "path", settings.Model.General.DownloadPath }
            };
        }

        private static string ResolveWallpaper(SettingsManager settings)
        {
            string v = settings.Model.NewTab.Wallpaper;
            if (v.StartsWith("file:", StringComparison.Ordinal))
            {
                try
                {
                    string path = v.Substring(5);
                    if (File.Exists(path))
                        return "data:image/" + (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpeg") +
                               ";base64," + Convert.ToBase64String(File.ReadAllBytes(path));
                }
                catch { }
                return "gradient-aurora";
            }
            return v;
        }

        // ------------------------------------------------------------------
        // UI-thread helpers
        // ------------------------------------------------------------------

        private static void SafeClipboard(string text)
        {
            BrowserForm form = AppServices.MainWindow;
            if (form == null) return;
            try
            {
                form.Invoke((Action)delegate { Clipboard.SetText(text ?? ""); });
            }
            catch { }
        }

        private static Task<string> PickFolderAsync(BrowserForm form)
        {
            TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
            try
            {
                form.Invoke((Action)delegate
                {
                    using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                    {
                        dlg.Description = "Select folder";
                        dlg.ShowNewFolderButton = true;
                        tcs.SetResult(dlg.ShowDialog(form) == DialogResult.OK ? dlg.SelectedPath : "");
                    }
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult("");
                Log.Error("pickFolder: " + ex.Message);
            }
            return tcs.Task;
        }

        private static Task<string> PickFileAsync(BrowserForm form, string title, string filter)
        {
            TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
            try
            {
                form.Invoke((Action)delegate
                {
                    using (OpenFileDialog dlg = new OpenFileDialog())
                    {
                        dlg.Title = title;
                        dlg.Filter = filter;
                        tcs.SetResult(dlg.ShowDialog(form) == DialogResult.OK ? dlg.FileName : "");
                    }
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult("");
                Log.Error("pickFile: " + ex.Message);
            }
            return tcs.Task;
        }

        // ------------------------------------------------------------------
        // Settings patch
        // ------------------------------------------------------------------

        private static void ApplyPatch(SettingsManager settings, JsonElement p)
        {
            GeneralSettings g = settings.Model.General;
            AppearanceSettings a = settings.Model.Appearance;
            PrivacySettings priv = settings.Model.Privacy;
            NewTabSettings nt = settings.Model.NewTab;
            DownloadSettings dl = settings.Model.Downloads;

            if (p.TryGetProperty("startup", out JsonElement e1))
            {
                StartupBehavior v;
                if (Enum.TryParse(e1.GetString(), true, out v)) g.Startup = v;
            }
            if (p.TryGetProperty("homeUrl", out JsonElement e2)) g.HomeUrl = e2.GetString() ?? g.HomeUrl;
            if (p.TryGetProperty("confirmExit", out JsonElement e3)) g.ConfirmBeforeExit = e3.GetBoolean();
            if (p.TryGetProperty("clipboardSniffer", out JsonElement e4)) g.ClipboardSniffer = e4.GetBoolean();
            if (p.TryGetProperty("showBookmarksBar", out JsonElement e5)) g.ShowBookmarksBar = e5.GetBoolean();
            if (p.TryGetProperty("showStatusBar", out JsonElement e6)) g.ShowStatusBar = e6.GetBoolean();
            if (p.TryGetProperty("theme", out JsonElement e7))
            {
                ThemeMode v;
                if (Enum.TryParse(e7.GetString(), true, out v)) a.Theme = v;
            }
            if (p.TryGetProperty("accent", out JsonElement e8)) a.AccentColor = e8.GetString() ?? a.AccentColor;
            if (p.TryGetProperty("fontScale", out JsonElement e9) && e9.ValueKind == JsonValueKind.Number)
                a.FontScale = e9.GetDouble();
            if (p.TryGetProperty("tabPlacement", out JsonElement e10))
            {
                TabPlacement v;
                if (Enum.TryParse(e10.GetString(), true, out v)) a.TabPlacement = v;
            }
            if (p.TryGetProperty("rounded", out JsonElement e11)) a.RoundedCorners = e11.GetBoolean();
            if (p.TryGetProperty("acrylic", out JsonElement e12)) a.AcrylicTopBar = e12.GetBoolean();
            if (p.TryGetProperty("clearOnExit", out JsonElement e13)) priv.ClearOnExit = e13.GetBoolean();
            if (p.TryGetProperty("clearCookies", out JsonElement e14)) priv.ClearCookiesOnExit = e14.GetBoolean();
            if (p.TryGetProperty("clearCache", out JsonElement e15)) priv.ClearCacheOnExit = e15.GetBoolean();
            if (p.TryGetProperty("clearHistory", out JsonElement e16)) priv.ClearHistoryOnExit = e16.GetBoolean();
            if (p.TryGetProperty("clearForm", out JsonElement e17)) priv.ClearFormDataOnExit = e17.GetBoolean();
            if (p.TryGetProperty("tracking", out JsonElement e18))
            {
                TrackingPrevention v;
                if (Enum.TryParse(e18.GetString(), true, out v)) priv.TrackingLevel = v;
            }
            if (p.TryGetProperty("showClock", out JsonElement e19)) nt.ShowClock = e19.GetBoolean();
            if (p.TryGetProperty("showGreeting", out JsonElement e20)) nt.ShowGreeting = e20.GetBoolean();
            if (p.TryGetProperty("showNotepad", out JsonElement e21)) nt.ShowNotepad = e21.GetBoolean();
            if (p.TryGetProperty("rssEnabled", out JsonElement e22)) nt.RssEnabled = e22.GetBoolean();
            if (p.TryGetProperty("rssUrl", out JsonElement e23)) nt.RssUrl = e23.GetString() ?? nt.RssUrl;
            if (p.TryGetProperty("segments", out JsonElement e24) && e24.ValueKind == JsonValueKind.Number)
                dl.SegmentCount = Math.Max(1, Math.Min(32, e24.GetInt32()));
            if (p.TryGetProperty("verifySha", out JsonElement e25)) dl.VerifySha256 = e25.GetBoolean();
            if (p.TryGetProperty("askWhere", out JsonElement e26)) g.AskWhereToSave = e26.GetBoolean();
        }

        // ------------------------------------------------------------------
        // RSS
        // ------------------------------------------------------------------

        private static async Task<object> FetchRssAsync(string url)
        {
            List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
            if (string.IsNullOrEmpty(url)) return items;
            try
            {
                string xml = await SegmentedDownloader.Client.GetStringAsync(url).ConfigureAwait(false);
                XmlDocument doc = new XmlDocument();
                doc.XmlResolver = null;
                XmlReaderSettings rs = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(new StringReader(xml), rs))
                {
                    doc.Load(reader);
                }
                XmlNodeList nodes = doc.SelectNodes("//item");
                if (nodes == null || nodes.Count == 0) nodes = doc.SelectNodes("//*[local-name()='entry']");
                int count = 0;
                foreach (XmlNode node in nodes)
                {
                    if (count++ >= 6) break;
                    string title = ChildText(node, "title");
                    string link = ChildText(node, "link");
                    if (link.Length == 0)
                    {
                        XmlNode linkNode = node.SelectSingleNode("*[local-name()='link']");
                        if (linkNode != null && linkNode.Attributes != null && linkNode.Attributes["href"] != null)
                            link = linkNode.Attributes["href"].Value;
                    }
                    string date = ChildText(node, "pubDate");
                    if (date.Length == 0) date = ChildText(node, "updated");
                    items.Add(new Dictionary<string, object>
                    {
                        { "title", title }, { "link", link }, { "date", date }
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error("rss fetch: " + ex.Message);
            }
            return items;
        }

        private static string ChildText(XmlNode node, string name)
        {
            XmlNode child = node.SelectSingleNode("*[local-name()='" + name + "']");
            return child != null ? (child.InnerText ?? "").Trim() : "";
        }
    }
}
