using System;
using System.Collections.Generic;

namespace Swifter.Config
{
    public enum StartupBehavior { NewTab, RestoreSession, CustomHome }
    public enum ThemeMode { System, Dark, Light }
    public enum TabPlacement { Top, Bottom }
    public enum TrackingPrevention { None, Basic, Balanced, Strict }
    public enum DohProvider { Off, Cloudflare, Google, Quad9, Custom }

    /// <summary>Search engine definition used by the omnibox and the new-tab page.</summary>
    public sealed class SearchEngine
    {
        public string Id { get; set; } = "duckduckgo";
        public string Name { get; set; } = "DuckDuckGo";
        public string UrlTemplate { get; set; } = "https://duckduckgo.com/?q={0}";
        public string SuggestTemplate { get; set; } = "https://duckduckgo.com/ac/?q={0}&type=list";
    }

    /// <summary>Speed-dial entry on swifter://newtab.</summary>
    public sealed class QuickDialItem
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
    }

    /// <summary>Greasemonkey/Tampermonkey style user script.</summary>
    public sealed class UserScript
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New script";
        public bool Enabled { get; set; } = true;
        /// <summary>Domain wildcard matches, e.g. "*.example.com" or "example.com/*".</summary>
        public List<string> Matches { get; set; } = new List<string>();
        public string JsCode { get; set; } = "";
        public string CssCode { get; set; } = "";
        /// <summary>document-start or document-end.</summary>
        public string RunAt { get; set; } = "document-end";
    }

    public sealed class GeneralSettings
    {
        public StartupBehavior Startup { get; set; } = StartupBehavior.NewTab;
        public string HomeUrl { get; set; } = "swifter://newtab";
        public bool AskWhereToSave { get; set; } = false;
        public string DownloadPath { get; set; } = "";
        public bool ClipboardSniffer { get; set; } = true;
        public bool ConfirmBeforeExit { get; set; } = true;
        public int SessionAutosaveSeconds { get; set; } = 10;
        public double DefaultZoom { get; set; } = 1.0;
        public bool ShowBookmarksBar { get; set; } = true;
        public bool ShowStatusBar { get; set; } = true;
    }

    public sealed class DownloadSettings
    {
        /// <summary>Simultaneous segment streams per download (1..32).</summary>
        public int SegmentCount { get; set; } = 16;
        /// <summary>How many downloads may run at once.</summary>
        public int MaxActiveDownloads { get; set; } = 3;
        /// <summary>Global throttle in KiB/s, 0 = unlimited.</summary>
        public int ThrottleKibPerSec { get; set; } = 0;
        public bool VerifySha256 { get; set; } = true;
        public bool AutoRetry { get; set; } = true;
        public int MaxRetries { get; set; } = 3;
        public bool SchedulerEnabled { get; set; } = false;
        public string ScheduleStart { get; set; } = "22:00";
        public string ScheduleEnd { get; set; } = "06:00";
    }

    public sealed class AppearanceSettings
    {
        public ThemeMode Theme { get; set; } = ThemeMode.System;
        public string AccentColor { get; set; } = "#4cc2ff";
        public double FontScale { get; set; } = 1.0;
        public TabPlacement TabPlacement { get; set; } = TabPlacement.Top;
        public bool RoundedCorners { get; set; } = true;
        public bool AcrylicTopBar { get; set; } = true;
    }

    public sealed class PrivacySettings
    {
        public bool ClearOnExit { get; set; } = false;
        public bool ClearCookiesOnExit { get; set; } = false;
        public bool ClearCacheOnExit { get; set; } = false;
        public bool ClearHistoryOnExit { get; set; } = false;
        public bool ClearFormDataOnExit { get; set; } = false;
        public TrackingPrevention TrackingLevel { get; set; } = TrackingPrevention.Balanced;
        public bool HttpsUpgrade { get; set; } = true;
        public bool CanvasDefense { get; set; } = true;
        public bool WebRtcLeakProtection { get; set; } = true;
        public bool BlockThirdPartyCookies { get; set; } = true;
        public bool SendDoNotTrack { get; set; } = true;
        public DohProvider Doh { get; set; } = DohProvider.Cloudflare;
        public string DohCustomUrl { get; set; } = "https://dns.quad9.net/dns-query";
    }

    public sealed class ShieldSettings
    {
        public bool Enabled { get; set; } = true;
        public List<string> CustomRules { get; set; } = new List<string>();
        /// <summary>Host overrides: true = site shields disabled (allow), false = force block.</summary>
        public Dictionary<string, bool> SiteOverrides { get; set; } = new Dictionary<string, bool>();
        public bool LoadBundledLists { get; set; } = true;
    }

    public sealed class NewTabSettings
    {
        /// <summary>Preset id ("gradient-*") or "file:" + path or "data:" + base64 uri.</summary>
        public string Wallpaper { get; set; } = "gradient-aurora";
        public bool ShowClock { get; set; } = true;
        public bool ShowGreeting { get; set; } = true;
        public bool ShowNotepad { get; set; } = true;
        public string NotepadText { get; set; } = "";
        public bool RssEnabled { get; set; } = true;
        public string RssUrl { get; set; } = "https://feeds.bbci.co.uk/news/technology/rss.xml";
        public List<QuickDialItem> QuickDial { get; set; } = new List<QuickDialItem>
        {
            new QuickDialItem { Title = "GitHub", Url = "https://github.com" },
            new QuickDialItem { Title = "YouTube", Url = "https://youtube.com" },
            new QuickDialItem { Title = "Wikipedia", Url = "https://wikipedia.org" },
            new QuickDialItem { Title = "Reddit", Url = "https://reddit.com" },
            new QuickDialItem { Title = "X", Url = "https://x.com" },
            new QuickDialItem { Title = "Hacker News", Url = "https://news.ycombinator.com" },
            new QuickDialItem { Title = "Maps", Url = "https://maps.google.com" },
            new QuickDialItem { Title = "Mail", Url = "https://mail.google.com" }
        };
        public string SearchEngine { get; set; } = "duckduckgo";
    }

    public sealed class ReaderSettings
    {
        public string Theme { get; set; } = "sepia";
        public string FontFamily { get; set; } = "Georgia";
        public int FontSize { get; set; } = 19;
        public int LineHeight { get; set; } = 170;
        public int ContentWidth { get; set; } = 720;
    }

    public sealed class WindowState
    {
        public int X { get; set; } = -1;
        public int Y { get; set; } = -1;
        public int Width { get; set; } = 1360;
        public int Height { get; set; } = 860;
        public bool Maximized { get; set; } = false;
    }

    /// <summary>Root configuration document persisted to %AppData%\Swifter\settings.json.</summary>
    public sealed class SettingsModel
    {
        public int SchemaVersion { get; set; } = 1;
        public GeneralSettings General { get; set; } = new GeneralSettings();
        public DownloadSettings Downloads { get; set; } = new DownloadSettings();
        public AppearanceSettings Appearance { get; set; } = new AppearanceSettings();
        public PrivacySettings Privacy { get; set; } = new PrivacySettings();
        public ShieldSettings Shields { get; set; } = new ShieldSettings();
        public NewTabSettings NewTab { get; set; } = new NewTabSettings();
        public ReaderSettings Reader { get; set; } = new ReaderSettings();
        public WindowState Window { get; set; } = new WindowState();
        public List<SearchEngine> SearchEngines { get; set; } = new List<SearchEngine>
        {
            new SearchEngine
            {
                Id = "duckduckgo", Name = "DuckDuckGo",
                UrlTemplate = "https://duckduckgo.com/?q={0}",
                SuggestTemplate = "https://duckduckgo.com/ac/?q={0}&type=list"
            },
            new SearchEngine
            {
                Id = "google", Name = "Google",
                UrlTemplate = "https://www.google.com/search?q={0}",
                SuggestTemplate = "https://suggestqueries.google.com/complete/search?client=firefox&q={0}"
            },
            new SearchEngine
            {
                Id = "bing", Name = "Bing",
                UrlTemplate = "https://www.bing.com/search?q={0}",
                SuggestTemplate = "https://api.bing.com/osjson.aspx?query={0}"
            },
            new SearchEngine
            {
                Id = "brave", Name = "Brave",
                UrlTemplate = "https://search.brave.com/search?q={0}",
                SuggestTemplate = "https://search.brave.com/api/suggest?q={0}"
            },
            new SearchEngine
            {
                Id = "youtube", Name = "YouTube",
                UrlTemplate = "https://www.youtube.com/results?search_query={0}",
                SuggestTemplate = "https://suggestqueries.google.com/complete/search?client=firefox&ds=yt&q={0}"
            },
            new SearchEngine
            {
                Id = "github", Name = "GitHub",
                UrlTemplate = "https://github.com/search?q={0}",
                SuggestTemplate = ""
            }
        };
        public List<UserScript> UserScripts { get; set; } = new List<UserScript>();
    }
}
