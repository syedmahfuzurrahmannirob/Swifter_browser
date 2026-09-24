using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Swifter.Config;
using Swifter.Engine;
using Swifter.Protocols;
using Swifter.Storage;

namespace Swifter
{
    /// <summary>
    /// Main application window: custom title bar, navigation toolbar with the smart
    /// omnibox, shield / media / reader / PiP / download controls, bookmark bar,
    /// status bar and per-tab WebView2 wiring for every engine.
    /// </summary>
    public sealed class BrowserForm : Form
    {
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                          HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int data, int size);

        private readonly TabManager _tabs;
        private readonly Panel _titleBar = new Panel();
        private readonly Panel _toolbar = new Panel();
        private readonly Panel _bookmarksBar = new Panel();
        private readonly Panel _content = new Panel();
        private readonly Panel _statusBar = new Panel();
        private readonly Panel _sessionBar = new Panel();
        private readonly Panel _findBar = new Panel();
        private readonly CaptionButtons _caption = new CaptionButtons();
        private readonly PictureBox _logo = new PictureBox();
        private readonly ToolButton _back = new ToolButton("\u2190");
        private readonly ToolButton _forward = new ToolButton("\u2192");
        private readonly ToolButton _reload = new ToolButton("\u21bb");
        private readonly ToolButton _home = new ToolButton("\u2302");
        private readonly Omnibox _omnibox;
        private readonly ToolButton _shieldBtn = new ToolButton("");
        private readonly ToolButton _snifferBtn = new ToolButton("\u25b6");
        private readonly ToolButton _readerBtn = new ToolButton("\u2263");
        private readonly ToolButton _pipBtn = new ToolButton("\u29c9");
        private readonly ToolButton _downloadPill = new ToolButton("\u2b07 0");
        private readonly ToolButton _menuBtn = new ToolButton("\u22ee");
        private readonly Label _statusLabel = new Label();
        private readonly Label _statusRight = new Label();
        private readonly TextBox _findBox = new TextBox();
        private readonly Label _findCount = new Label();

        private readonly Dictionary<string, int> _blockedPerTab = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> _visitStarted = new Dictionary<string, DateTime>();
        private SuggestPopup _suggest;
        private PiPForm _pip;
        private bool _closing;
        private string _findQuery = "";
        private int _findIndex;

        public BrowserForm(string[] args)
        {
            Text = "Swifter";
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(15, 18, 22);
            MinimumSize = new Size(720, 480);
            KeyPreview = true;

            try
            {
                using (Stream s = typeof(BrowserForm).Assembly.GetManifestResourceStream("Swifter.Assets.app.ico"))
                {
                    if (s != null) Icon = new Icon(s);
                }
            }
            catch { }

            _tabs = new TabManager(_content, AppServices.Env);
            _omnibox = new Omnibox(this);

            BuildLayout();
            ApplyAppearance();
            WireTabManager();
            WireEngines();

            RestoreWindowState();
            BootTabs(args);
        }

        // ------------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------------

        private void BuildLayout()
        {
            _titleBar.Height = 42;
            _titleBar.Dock = DockStyle.Top;
            _titleBar.Controls.Add(_tabs.Strip);
            _tabs.Strip.Dock = DockStyle.Fill;
            _caption.Dock = DockStyle.Right;
            _caption.Width = 138;
            _titleBar.Controls.Add(_caption);
            _caption.Minimize += delegate { WindowState = FormWindowState.Minimized; };
            _caption.Maximize += ToggleMaximize;
            _caption.Close += delegate { Close(); };

            _toolbar.Dock = DockStyle.Top;
            _toolbar.Height = 46;
            _logo.SizeMode = PictureBoxSizeMode.Zoom;
            _logo.Size = new Size(26, 26);
            _logo.Location = new Point(8, 10);
            try
            {
                using (Stream s = typeof(BrowserForm).Assembly.GetManifestResourceStream("Swifter.Assets.logo.png"))
                {
                    if (s != null)
                    {
                        using (MemoryStream keep = new MemoryStream())
                        {
                            s.CopyTo(keep);
                            _logo.Image = Image.FromStream(new MemoryStream(keep.ToArray()));
                        }
                    }
                }
            }
            catch { }
            _toolbar.Controls.Add(_logo);

            int x = 40;
            foreach (ToolButton b in new[] { _back, _forward, _reload, _home })
            {
                b.Size = new Size(34, 32);
                b.Location = new Point(x, 7);
                _toolbar.Controls.Add(b);
                x += 36;
            }
            _back.Click += delegate { GoBack(); };
            _forward.Click += delegate { GoForward(); };
            _reload.Click += delegate { ReloadActive(); };
            _home.Click += delegate { Navigate(Settings.Model.General.HomeUrl); };

            _omnibox.Location = new Point(x + 4, 7);
            _toolbar.Controls.Add(_omnibox);
            _omnibox.Commit += OnOmniboxCommit;

            _shieldBtn.Size = new Size(52, 32);
            _snifferBtn.Size = new Size(34, 32);
            _readerBtn.Size = new Size(34, 32);
            _pipBtn.Size = new Size(34, 32);
            _downloadPill.AutoSize = false;
            _downloadPill.Size = new Size(78, 32);
            _menuBtn.Size = new Size(34, 32);
            _toolbar.Controls.Add(_shieldBtn);
            _toolbar.Controls.Add(_snifferBtn);
            _toolbar.Controls.Add(_readerBtn);
            _toolbar.Controls.Add(_pipBtn);
            _toolbar.Controls.Add(_downloadPill);
            _toolbar.Controls.Add(_menuBtn);
            _shieldBtn.Click += delegate { OpenShieldMenu(); };
            _snifferBtn.Click += delegate { Navigate("swifter://downloads"); };
            _readerBtn.Click += delegate { _ = ToggleReaderAsync(); };
            _pipBtn.Click += delegate { _ = TogglePipAsync(); };
            _downloadPill.Click += delegate { Navigate("swifter://downloads"); };
            _menuBtn.Click += delegate { ShowMainMenu(); };

            _bookmarksBar.Dock = DockStyle.Top;
            _bookmarksBar.Height = 32;
            _bookmarksBar.AutoScroll = true;

            _statusBar.Dock = DockStyle.Bottom;
            _statusBar.Height = 24;
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.Padding = new Padding(8, 0, 0, 0);
            _statusRight.Dock = DockStyle.Right;
            _statusRight.Width = 300;
            _statusRight.TextAlign = ContentAlignment.MiddleRight;
            _statusRight.Padding = new Padding(0, 0, 8, 0);
            _statusBar.Controls.Add(_statusLabel);
            _statusBar.Controls.Add(_statusRight);

            _sessionBar.Dock = DockStyle.Top;
            _sessionBar.Height = 40;
            _sessionBar.Visible = false;
            Label sbLabel = new Label
            {
                Text = "Swifter closed unexpectedly last time. Restore your tabs?",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0)
            };
            Button sbRestore = new Button { Text = "Restore", Width = 84, Dock = DockStyle.Right };
            Button sbDismiss = new Button { Text = "Dismiss", Width = 84, Dock = DockStyle.Right };
            sbRestore.Click += delegate { RestoreSession(); };
            sbDismiss.Click += delegate { DismissSessionPrompt(); };
            _sessionBar.Controls.Add(sbLabel);
            _sessionBar.Controls.Add(sbDismiss);
            _sessionBar.Controls.Add(sbRestore);

            _findBar.Dock = DockStyle.Top;
            _findBar.Height = 36;
            _findBar.Visible = false;
            _findBox.Width = 240;
            _findBox.Location = new Point(10, 7);
            _findCount.Location = new Point(258, 11);
            _findCount.AutoSize = true;
            Button findNext = new Button { Text = "\u2193", Width = 32, Location = new Point(340, 5) };
            Button findPrev = new Button { Text = "\u2191", Width = 32, Location = new Point(376, 5) };
            Button findClose = new Button { Text = "\u2715", Width = 32, Location = new Point(412, 5) };
            findNext.Click += delegate { FindStep(1); };
            findPrev.Click += delegate { FindStep(-1); };
            findClose.Click += delegate { HideFind(); };
            _findBox.TextChanged += delegate { _findIndex = 0; RunFind(); };
            _findBar.Controls.AddRange(new Control[] { _findBox, _findCount, findNext, findPrev, findClose });

            _content.Dock = DockStyle.Fill;

            Controls.Add(_content);
            Controls.Add(_findBar);
            Controls.Add(_sessionBar);
            Controls.Add(_bookmarksBar);
            Controls.Add(_toolbar);
            Controls.Add(_titleBar);
            Controls.Add(_statusBar);

            Resize += delegate { LayoutToolbar(); };
            LayoutToolbar();
        }

        private void LayoutToolbar()
        {
            int right = _toolbar.ClientSize.Width;
            int x = right - 8;
            x -= _menuBtn.Width; _menuBtn.Location = new Point(x, 7);
            x -= _downloadPill.Width + 4; _downloadPill.Location = new Point(x, 7);
            x -= _pipBtn.Width + 4; _pipBtn.Location = new Point(x, 7);
            x -= _readerBtn.Width + 4; _readerBtn.Location = new Point(x, 7);
            x -= _snifferBtn.Width + 4; _snifferBtn.Location = new Point(x, 7);
            x -= _shieldBtn.Width + 4; _shieldBtn.Location = new Point(x, 7);
            _omnibox.Width = Math.Max(180, x - _omnibox.Left - 10);
        }

        public SettingsManager Settings { get { return AppServices.Settings; } }

        public string ActiveUrl { get { return _tabs.Active != null ? _tabs.Active.Url : ""; } }

        // ------------------------------------------------------------------
        // Theming / appearance
        // ------------------------------------------------------------------

        public void ApplyAppearance()
        {
            bool dark = Settings.IsDarkTheme();
            Color chrome = dark ? Color.FromArgb(22, 27, 34) : Color.FromArgb(243, 245, 248);
            Color chromeText = dark ? Color.FromArgb(232, 237, 242) : Color.FromArgb(27, 36, 48);
            Color border = dark ? Color.FromArgb(42, 52, 64) : Color.FromArgb(216, 223, 231);
            BackColor = dark ? Color.FromArgb(15, 18, 22) : Color.FromArgb(248, 250, 252);
            _titleBar.BackColor = chrome;
            _toolbar.BackColor = chrome;
            _bookmarksBar.BackColor = chrome;
            _statusBar.BackColor = chrome;
            _sessionBar.BackColor = Color.FromArgb(dark ? 30 : 255, dark ? 38 : 214, dark ? 48 : 102);
            _findBar.BackColor = chrome;
            foreach (Control c in _toolbar.Controls) c.ForeColor = chromeText;
            _statusLabel.ForeColor = dark ? Color.FromArgb(152, 164, 179) : Color.FromArgb(92, 107, 122);
            _statusRight.ForeColor = _statusLabel.ForeColor;
            _statusBar.Visible = Settings.Model.General.ShowStatusBar;
            _titleBar.Dock = Settings.Model.Appearance.TabPlacement == TabPlacement.Bottom
                ? DockStyle.Bottom : DockStyle.Top;
            _omnibox.ApplyTheme(dark, Settings.Accent(), border, chromeText);
            _tabs.Strip.RefreshTheme();
            _caption.ApplyTheme(dark);
            RefreshBookmarksBar();

            // Window manager chrome
            try
            {
                int darkMode = dark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref darkMode, 4);
                int corner = Settings.Model.Appearance.RoundedCorners ? 2 : 1;
                DwmSetWindowAttribute(Handle, 33, ref corner, 4);
                int backdrop = Settings.Model.Appearance.AcrylicTopBar ? 4 : 2;
                DwmSetWindowAttribute(Handle, 38, ref backdrop, 4);
                int caption = dark ? 0x1f1616 : 0xf8f3f3;   // BGR
                DwmSetWindowAttribute(Handle, 35, ref caption, 4);
                int text = dark ? 0xf2ede8 : 0x30241b;
                DwmSetWindowAttribute(Handle, 36, ref text, 4);
            }
            catch { }

            foreach (BrowserTab tab in _tabs.Tabs)
            {
                try
                {
                    if (tab.View != null && tab.View.CoreWebView2 != null)
                        tab.View.CoreWebView2.Profile.PreferredColorScheme =
                            dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
                }
                catch { }
            }
            Invalidate(true);
        }

        public void ApplyPrivacySettings()
        {
            foreach (BrowserTab tab in _tabs.Tabs)
            {
                try
                {
                    CoreWebView2 core = tab.View != null ? tab.View.CoreWebView2 : null;
                    if (core == null) continue;
                    core.Profile.PreferredTrackingPreventionLevel = MapTracking(Settings.Model.Privacy.TrackingLevel);
                }
                catch { }
            }
        }

        private static CoreWebView2TrackingPreventionLevel MapTracking(TrackingPrevention level)
        {
            switch (level)
            {
                case TrackingPrevention.None: return CoreWebView2TrackingPreventionLevel.None;
                case TrackingPrevention.Basic: return CoreWebView2TrackingPreventionLevel.Basic;
                case TrackingPrevention.Strict: return CoreWebView2TrackingPreventionLevel.Strict;
                default: return CoreWebView2TrackingPreventionLevel.Balanced;
            }
        }

        // ------------------------------------------------------------------
        // Boot / session
        // ------------------------------------------------------------------

        private void RestoreWindowState()
        {
            WindowState w = Settings.Model.Window;
            if (w.X >= 0 && w.Y >= 0 && w.Width > 400 && w.Height > 300)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(w.X, w.Y);
                Size = new Size(w.Width, w.Height);
            }
            else
            {
                StartPosition = FormStartPosition.CenterScreen;
            }
            if (w.Maximized) WindowState = FormWindowState.Maximized;
        }

        private void BootTabs(string[] args)
        {
            bool restored = false;
            if (AppServices.Session.NeedsRestore)
            {
                if (Settings.Model.General.Startup == StartupBehavior.RestoreSession)
                {
                    RestoreSession();
                    restored = true;
                }
                else
                {
                    _sessionBar.Visible = true;
                }
            }
            if (!restored)
            {
                string first = null;
                if (args != null && args.Length > 0 && args[0].IndexOf("://", StringComparison.Ordinal) > 0)
                    first = args[0];
                else if (Settings.Model.General.Startup == StartupBehavior.RestoreSession &&
                         AppServices.Session.Pending.Windows.Count > 0 &&
                         AppServices.Session.Pending.Windows[0].Tabs.Count > 0)
                {
                    RestoreSession();
                    restored = true;
                }
                else if (Settings.Model.General.Startup == StartupBehavior.CustomHome)
                    first = Settings.Model.General.HomeUrl;
                if (!restored) OpenNewTab(first ?? Settings.Model.General.HomeUrl, true);
            }
            AppServices.Session.Start(SessionProvider, Settings.Model.General.SessionAutosaveSeconds);
            AppServices.Downloads.StartTicker();
        }

        public void RestoreSession()
        {
            _sessionBar.Visible = false;
            SessionData data = AppServices.Session.Pending;
            if (data == null || data.Windows.Count == 0) return;
            SessionWindow win = data.Windows[0];
            foreach (SessionGroup g in win.Groups)
            {
                Color c;
                try { c = ColorTranslator.FromHtml(g.Color); } catch { c = TabStrip.GroupPalette[0]; }
                _tabs.Groups.Add(new TabGroup { Id = g.Id, Name = g.Name, Color = c, Collapsed = g.Collapsed });
            }
            foreach (SessionTab st in win.Tabs)
            {
                BrowserTab tab = _tabs.CreateTab(st.Url, false, st.Pinned);
                tab.Title = string.IsNullOrEmpty(st.Title) ? st.Url : st.Title;
                tab.Muted = st.Muted;
                tab.GroupId = st.GroupId;
            }
            int active = Math.Min(Math.Max(0, win.ActiveIndex), Math.Max(0, _tabs.Tabs.Count - 1));
            if (_tabs.Tabs.Count > 0) _tabs.ActivateTab(_tabs.Tabs[active]);
            AppServices.Session.NeedsRestore = false;
        }

        public void DismissSessionPrompt()
        {
            _sessionBar.Visible = false;
            AppServices.Session.NeedsRestore = false;
        }

        private SessionData SessionProvider()
        {
            SessionData data = new SessionData();
            SessionWindow win = new SessionWindow();
            foreach (TabGroup g in _tabs.Groups)
                win.Groups.Add(new SessionGroup
                {
                    Id = g.Id, Name = g.Name, Color = SettingsManager.ToHex(g.Color), Collapsed = g.Collapsed
                });
            foreach (BrowserTab t in _tabs.Tabs)
            {
                win.Tabs.Add(new SessionTab
                {
                    Url = t.IsReader ? t.ReaderSourceUrl : t.Url,
                    Title = t.Title,
                    Pinned = t.Pinned,
                    Muted = t.Muted,
                    GroupId = t.GroupId
                });
            }
            win.ActiveIndex = _tabs.Active != null ? _tabs.IndexOf(_tabs.Active) : 0;
            data.Windows.Add(win);
            return data;
        }

        // ------------------------------------------------------------------
        // Tab manager wiring
        // ------------------------------------------------------------------

        private void WireEngines()
        {
            AppServices.Downloads.Changed += delegate
            {
                if (IsDisposed) return;
                try { BeginInvoke((Action)UpdateDownloadPill); } catch { }
            };
            AppServices.Downloads.LinkSniffed += delegate (string url, string name)
            {
                if (IsDisposed) return;
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        DialogResult r = MessageBox.Show(this,
                            "Swifter detected a downloadable file on your clipboard:\n\n" + url +
                            "\n\nQueue it in the download manager?",
                            "Clipboard link sniffer", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (r == DialogResult.Yes)
                            AppServices.Downloads.Enqueue(url, name, Settings.Model.General.DownloadPath, "", "clipboard", false);
                    });
                }
                catch { }
            };
            AppServices.Bookmarks.Changed += delegate
            {
                if (IsDisposed) return;
                try { BeginInvoke((Action)RefreshBookmarksBar); } catch { }
            };
            AppServices.Sniffer.Changed += delegate
            {
                if (IsDisposed) return;
                try { BeginInvoke((Action)UpdateSnifferBadge); } catch { }
            };
            AppServices.Memory.Changed += delegate
            {
                if (IsDisposed) return;
                try { BeginInvoke((Action)UpdateChromeForActiveTab); } catch { }
            };
            Settings.Changed += delegate
            {
                if (IsDisposed) return;
                try { BeginInvoke((Action)ApplyAppearance); } catch { }
            };
        }

        private void WireTabManager()
        {
            _tabs.TabCreated += tab => { _ = InitTabAsync(tab); };
            _tabs.TabClosed += tab =>
            {
                AppServices.Memory.Unregister(tab.Id);
                AppServices.Sniffer.ClearTab(tab.Id);
                _blockedPerTab.Remove(tab.Id);
                _visitStarted.Remove(tab.Id);
            };
            _tabs.ActiveChanged += tab => OnActiveTabChanged(tab);
            _tabs.Changed += delegate { UpdateChromeForActiveTab(); };
        }

        private void OnActiveTabChanged(BrowserTab tab)
        {
            UpdateChromeForActiveTab();
            AppServices.Memory.Note(tab.Id);
        }

        // ------------------------------------------------------------------
        // Per tab WebView2 initialisation
        // ------------------------------------------------------------------

        private async Task InitTabAsync(BrowserTab tab)
        {
            try
            {
                if (tab.View.CoreWebView2 == null)
                    await tab.View.EnsureCoreWebView2Async(AppServices.Env).ConfigureAwait(true);
                CoreWebView2 core = tab.View.CoreWebView2;
                if (core == null) return;

                core.Settings.IsScriptEnabled = true;
                core.Settings.IsWebMessageEnabled = true;
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.Settings.AreDevToolsEnabled = true;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsBuiltInErrorPageEnabled = true;
                core.Settings.IsPasswordAutosaveEnabled = true;
                core.Settings.IsGeneralAutofillEnabled = true;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsSwipeNavigationEnabled = false;
                core.Profile.PreferredColorScheme = Settings.IsDarkTheme()
                    ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
                try { core.Profile.PreferredTrackingPreventionLevel = MapTracking(Settings.Model.Privacy.TrackingLevel); }
                catch { }

                SwifterSchemeHandler.Attach(core);
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    AppServices.Shields.BuildProtectionScript()).ConfigureAwait(true);

                core.NavigationStarting += OnNavigationStarting;
                core.NavigationCompleted += OnNavigationCompleted;
                core.SourceChanged += OnSourceChanged;
                core.DocumentTitleChanged += OnDocumentTitleChanged;
                core.FaviconChanged += OnFaviconChanged;
                core.WebResourceRequested += OnWebResourceRequested;
                core.WebResourceResponseReceived += OnWebResourceResponseReceived;
                core.WebMessageReceived += OnWebMessageReceived;
                core.DownloadStarting += OnDownloadStarting;
                core.NewWindowRequested += OnNewWindowRequested;
                core.PermissionRequested += OnPermissionRequested;
                core.ProcessFailed += OnProcessFailed;
                core.IsDocumentPlayingAudioChanged += OnAudioChanged;
                core.DOMContentLoaded += OnDOMContentLoaded;
                core.StatusBarTextChanged += OnStatusBarText;

                AppServices.Memory.Register(new MemoryTab
                {
                    Id = tab.Id,
                    Freeze = async () =>
                    {
                        if (tab.View == null || tab.View.CoreWebView2 == null) return false;
                        tab.View.Visible = false;
                        bool ok = await tab.View.CoreWebView2.TrySuspendAsync().ConfigureAwait(true);
                        tab.Frozen = ok;
                        return ok;
                    },
                    Thaw = () =>
                    {
                        if (tab.View == null || tab.View.CoreWebView2 == null) return;
                        tab.View.CoreWebView2.Resume();
                        tab.View.Visible = _tabs.Active == tab;
                    },
                    Discard = () =>
                    {
                        if (tab.View != null)
                        {
                            tab.View.Visible = false;
                            try { tab.View.Dispose(); } catch { }
                            tab.View = null;
                        }
                        return Task.CompletedTask;
                    },
                    Restore = () => { _ = RestoreDiscardedAsync(tab); }
                });

                if (File.Exists(AppPaths.DataDir + "\\clear-on-start.flag"))
                {
                    try
                    {
                        File.Delete(AppPaths.DataDir + "\\clear-on-start.flag");
                        await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile)
                            .ConfigureAwait(true);
                    }
                    catch { }
                }

                string url = tab.Url;
                tab.Url = "";
                NavigateTab(tab, url);
            }
            catch (Exception ex)
            {
                Log.Error("tab init: " + ex.Message);
            }
        }

        private async Task RestoreDiscardedAsync(BrowserTab tab)
        {
            try
            {
                tab.View = new WebView2 { Dock = DockStyle.Fill, Visible = false };
                _content.Controls.Add(tab.View);
                await InitTabAsync(tab).ConfigureAwait(true);
                _tabs.ActivateTab(tab);
            }
            catch (Exception ex)
            {
                Log.Error("restore discarded: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Navigation helpers
        // ------------------------------------------------------------------

        public void Navigate(string url)
        {
            if (_tabs.Active == null) { OpenNewTab(url, true); return; }
            NavigateTab(_tabs.Active, url);
        }

        private void NavigateTab(BrowserTab tab, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) url = Settings.Model.General.HomeUrl;
            url = url.Trim();
            string target = ResolveUrl(url);
            CoreWebView2 core = tab.View != null ? tab.View.CoreWebView2 : null;
            if (core == null) { tab.Url = target; return; }
            tab.Loading = true;
            if (SwifterSchemeHandler.IsInternal(target))
            {
                // Prefer the registered scheme; fall back to inline rendering.
                try { core.Navigate(target); }
                catch { SwifterSchemeHandler.NavigateFallback(core, target); }
            }
            else
            {
                try { core.Navigate(target); }
                catch (Exception ex) { Log.Error("navigate: " + ex.Message); }
            }
        }

        public static string ResolveUrl(string input)
        {
            string s = input.Trim();
            if (s.Length == 0) return "swifter://newtab";
            if (s.StartsWith("swifter://", StringComparison.OrdinalIgnoreCase)) return s;
            if (Uri.IsWellFormedUriString(s, UriKind.Absolute) &&
                (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                 s.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                 s.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
                 s.StartsWith("view-source:", StringComparison.OrdinalIgnoreCase) ||
                 s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)))
                return s;
            if (System.Text.RegularExpressions.Regex.IsMatch(s,
                    @"^[a-zA-Z][a-zA-Z0-9+.-]*://")) return s;
            if (System.Text.RegularExpressions.Regex.IsMatch(s,
                    @"^(?:[a-zA-Z0-9-]+\.)+[a-zA-Z]{2,}(:\d+)?([/?#].*)?$") ||
                s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{1,3}(\.\d{1,3}){3}(:\d+)?([/?#].*)?$"))
                return "https://" + s;
            SearchEngine e = AppServices.Settings.CurrentSearchEngine();
            return string.Format(CultureInfo.InvariantCulture, e.UrlTemplate, Uri.EscapeDataString(s));
        }

        public void OpenNewTab(string url, bool activate)
        {
            _tabs.CreateTab(url, activate, false);
        }

        public void CloseActiveTab()
        {
            if (_tabs.Active != null) _tabs.CloseTab(_tabs.Active);
            if (_tabs.Tabs.Count == 0) Close();
        }

        public void ReloadActive()
        {
            BrowserTab tab = _tabs.Active;
            if (tab == null || tab.View == null || tab.View.CoreWebView2 == null) return;
            if (tab.IsReader) { _ = ToggleReaderAsync(); return; }
            if (SwifterSchemeHandler.IsInternal(tab.Url))
            {
                // Re-render internal pages from scratch.
                NavigateTab(tab, tab.Url);
                return;
            }
            try { tab.View.CoreWebView2.Reload(); } catch { }
        }

        public void GoBack()
        {
            BrowserTab tab = _tabs.Active;
            if (tab != null && tab.View != null && tab.View.CoreWebView2 != null && tab.View.CoreWebView2.CanGoBack)
                tab.View.CoreWebView2.GoBack();
        }

        public void GoForward()
        {
            BrowserTab tab = _tabs.Active;
            if (tab != null && tab.View != null && tab.View.CoreWebView2 != null && tab.View.CoreWebView2.CanGoForward)
                tab.View.CoreWebView2.GoForward();
        }

        public string TabIdFor(CoreWebView2 core)
        {
            foreach (BrowserTab t in _tabs.Tabs)
                if (t.View != null && t.View.CoreWebView2 == core) return t.Id;
            return null;
        }

        private BrowserTab TabFor(CoreWebView2 core)
        {
            foreach (BrowserTab t in _tabs.Tabs)
                if (t.View != null && t.View.CoreWebView2 == core) return t;
            return null;
        }

        // ------------------------------------------------------------------
        // WebView2 events
        // ------------------------------------------------------------------

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            BrowserTab tab = TabFor((CoreWebView2)sender);
            if (tab == null) return;
            string upgraded = AppServices.Shields.TryUpgrade(e.Uri);
            if (upgraded != null)
            {
                e.Cancel = true;
                NavigateTab(tab, upgraded);
                return;
            }
            if (tab.HistoryVisitId > 0 && _visitStarted.ContainsKey(tab.Id))
            {
                long ms = (long)(DateTime.Now - _visitStarted[tab.Id]).TotalMilliseconds;
                AppServices.History.UpdateDuration(tab.HistoryVisitId, ms);
            }
            _visitStarted[tab.Id] = DateTime.Now;
            tab.Loading = true;
            if (_tabs.Active == tab) _reload.Text = "\u2715";
            AppServices.Memory.Note(tab.Id);
            AppServices.Sniffer.ObserveRequest(tab.Id, tab.Url, tab.Title, e.Uri, "GET");
            UpdateChromeForActiveTab();
        }

        private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            BrowserTab tab = TabFor((CoreWebView2)sender);
            CoreWebView2 core = (CoreWebView2)sender;
            if (tab == null) return;
            tab.Loading = false;
            if (_tabs.Active == tab) _reload.Text = "\u21bb";
            if (!e.IsSuccess && SwifterSchemeHandler.IsInternal(core.Source ?? ""))
            {
                SwifterSchemeHandler.NavigateFallback(core, core.Source);
                return;
            }
            if (e.IsSuccess)
            {
                string url = core.Source ?? "";
                tab.Url = url;
                tab.IsInternal = SwifterSchemeHandler.IsInternal(url);
                if (!tab.IsInternal && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    string description = "";
                    try
                    {
                        string raw = await core.ExecuteScriptAsync(
                            "(function(){var m=document.querySelector('meta[name=description]');return m?m.content:'';})()")
                            .ConfigureAwait(true);
                        if (raw.StartsWith("\"")) description = JsonSerializer.Deserialize<string>(raw) ?? "";
                    }
                    catch { }
                    tab.HistoryVisitId = AppServices.History.RecordVisit(url, core.DocumentTitle ?? "", description);
                }
            }
            UpdateChromeForActiveTab();
        }

        private void OnSourceChanged(object sender, CoreWebView2SourceChangedEventArgs e)
        {
            BrowserTab tab = TabFor((CoreWebView2)sender);
            if (tab == null) return;
            tab.Url = ((CoreWebView2)sender).Source ?? "";
            tab.IsInternal = SwifterSchemeHandler.IsInternal(tab.Url);
            if (_tabs.Active == tab) UpdateChromeForActiveTab();
        }

        private void OnDocumentTitleChanged(object sender, object e)
        {
            BrowserTab tab = TabFor((CoreWebView2)sender);
            if (tab == null) return;
            tab.Title = ((CoreWebView2)sender).DocumentTitle ?? "";
            if (_tabs.Active == tab) Text = tab.Title + " - Swifter";
            _tabs.Strip.Invalidate();
        }

        private async void OnFaviconChanged(object sender, object e)
        {
            BrowserTab tab = TabFor((CoreWebView2)sender);
            CoreWebView2 core = (CoreWebView2)sender;
            if (tab == null) return;
            string uri = core.FaviconUri;
            if (string.IsNullOrEmpty(uri)) return;
            tab.Favicon = uri;
            _tabs.Strip.Invalidate();
            string host;
            try { host = new Uri(tab.Url).Host; } catch { return; }
            if (AppServices.History.GetFavicon(host).Length > 0) return;
            try
            {
                byte[] bytes = await SegmentedDownloader.Client.GetByteArrayAsync(uri).ConfigureAwait(false);
                if (bytes.Length == 0 || bytes.Length > 200 * 1024) return;
                string mime = uri.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" : "image/png";
                string dataUri = "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
                AppServices.History.SetFavicon(host, dataUri);
                tab.Favicon = dataUri;
                _tabs.Strip.Invalidate();
            }
            catch { }
        }

        private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            BrowserTab tab = TabFor((CoreWebView2)sender);
            CoreWebView2 core = (CoreWebView2)sender;
            if (tab == null) return;
            string url = e.Request.Uri;
            if (SwifterSchemeHandler.IsInternal(url)) return;   // handled by scheme handler

            string docHost = "";
            try { docHost = new Uri(tab.Url).Host; } catch { }

            if (Settings.Model.Privacy.SendDoNotTrack)
            {
                try { e.Request.Headers.SetHeader("DNT", "1"); } catch { }
            }

            // HTTPS upgrade for sub resources
            string upgraded = AppServices.Shields.TryUpgrade(url);
            if (upgraded != null)
            {
                e.Response = core.Environment.CreateWebResourceResponse(null, 307, "Temporary Redirect",
                    "Location: " + upgraded);
                return;
            }

            // Third party cookie stripping
            if (Settings.Model.Privacy.BlockThirdPartyCookies)
            {
                try
                {
                    string reqHost = new Uri(url).Host;
                    string reqDomain = ShieldEngine.RegistrableDomain(reqHost);
                    string docDomain = ShieldEngine.RegistrableDomain(docHost);
                    if (reqDomain.Length > 0 && docDomain.Length > 0 && reqDomain != docDomain &&
                        e.Request.Headers.Contains("Cookie"))
                    {
                        e.Request.Headers.RemoveHeader("Cookie");
                        AppServices.Shields.RecordCookieBlock();
                    }
                }
                catch { }
            }

            BlockDecision decision = AppServices.Shields.Evaluate(url, docHost,
                e.ResourceContext.ToString());
            if (decision.Blocked)
            {
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked by Swifter Shields",
                    "Content-Type: text/plain");
                int count;
                _blockedPerTab.TryGetValue(tab.Id, out count);
                _blockedPerTab[tab.Id] = count + 1;
                try { AppServices.Shields.RecordBlock(decision.Category, new Uri(url).Host, 0); } catch { }
                if (_tabs.Active == tab) UpdateShieldBadge();
                return;
            }

            if (e.ResourceContext == CoreWebView2WebResourceContext.Media ||
                e.ResourceContext == CoreWebView2WebResourceContext.XmlHttpRequest ||
                e.ResourceContext == CoreWebView2WebResourceContext.Fetch ||
                e.ResourceContext == CoreWebView2WebResourceContext.Other)
            {
                AppServices.Sniffer.ObserveRequest(tab.Id, tab.Url, tab.Title, url, e.Request.Method);
            }
        }

        private void OnWebResourceResponseReceived(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            BrowserTab tab = TabFor((CoreWebView2)sender);
            if (tab == null) return;
            try
            {
                string mime = "";
                var headers = e.Response.Headers;
                if (headers != null && headers.Contains("Content-Type")) mime = headers.GetHeader("Content-Type");
                long length = -1;
                if (headers != null && headers.Contains("Content-Length"))
                {
                    string cl = headers.GetHeader("Content-Length");
                    long.TryParse(cl, NumberStyles.Integer, CultureInfo.InvariantCulture, out length);
                }
                AppServices.Sniffer.ObserveResponse(tab.Id, tab.Url, tab.Title, e.Request.Uri, mime, length);
            }
            catch { }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            CoreWebView2 core = (CoreWebView2)sender;
            string message;
            try { message = e.TryGetWebMessageAsString(); }
            catch
            {
                try { message = e.WebMessageAsJson; } catch { return; }
            }
            if (message == null) return;

            // Picture-in-picture frame stream from the source page
            if (message.Contains("\"pipframe\""))
            {
                if (_pip != null) _pip.PushFrame(message);
                return;
            }
            if (message.Contains("\"media\""))
            {
                BrowserTab tab = TabFor(core);
                AppServices.Sniffer.ObserveDom(tab != null ? tab.Id : "", core.Source ?? "",
                    core.DocumentTitle ?? "", message);
                UpdateSnifferBadge();
                return;
            }
            _ = SwifterBridge.HandleAsync(core, message);
        }

        private void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            BrowserTab tab = TabFor((CoreWebView2)sender);
            e.Handled = true;
            e.Cancel = true;
            CoreWebView2DownloadOperation op = e.DownloadOperation;
            string url = op.Uri;
            string name = "";
            try
            {
                string suggested = op.ResultFilePath;
                if (!string.IsNullOrEmpty(suggested)) name = Path.GetFileName(suggested);
            }
            catch { }
            string dir = Settings.Model.General.DownloadPath;
            if (Settings.Model.General.AskWhereToSave)
            {
                using (SaveFileDialog dlg = new SaveFileDialog())
                {
                    dlg.FileName = name;
                    dlg.InitialDirectory = dir;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    dir = Path.GetDirectoryName(dlg.FileName);
                    name = Path.GetFileName(dlg.FileName);
                }
            }
            AppServices.Downloads.Enqueue(url, name, dir, tab != null ? tab.Url : "",
                tab != null ? tab.Title : "", url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));
        }

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            if (!string.IsNullOrEmpty(e.Uri)) OpenNewTab(e.Uri, true);
        }

        private void OnPermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            e.State = e.PermissionKind == CoreWebView2PermissionKind.UnknownPermission
                ? CoreWebView2PermissionState.Deny
                : CoreWebView2PermissionState.Allow;
            e.Handled = true;
        }

        private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            BrowserTab tab = TabFor((CoreWebView2)sender);
            Log.Error("renderer process failed for tab " + (tab != null ? tab.Title : "?") + ": " + e.ProcessFailedKind);
            if (tab == null) return;
            BeginInvoke((Action)delegate
            {
                try
                {
                    if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RendererCrashed &&
                        tab.View != null && tab.View.CoreWebView2 != null)
                        tab.View.CoreWebView2.Reload();
                }
                catch { }
            });
        }

        private void OnAudioChanged(object sender, object e)
        {
            CoreWebView2 core = (CoreWebView2)sender;
            BrowserTab tab = TabFor(core);
            if (tab == null) return;
            tab.PlayingAudio = core.IsDocumentPlayingAudio;
            _tabs.Strip.Invalidate();
        }

        private async void OnDOMContentLoaded(object sender, CoreWebView2DOMContentLoadedEventArgs e)
        {
            CoreWebView2 core = (CoreWebView2)sender;
            BrowserTab tab = TabFor(core);
            if (tab == null) return;
            string url = core.Source ?? "";
            if (SwifterSchemeHandler.IsInternal(url)) return;

            string startScript = AppServices.Scripts.DocumentStartScript(url);
            if (startScript.Length > 0)
            {
                try { await core.ExecuteScriptAsync(startScript).ConfigureAwait(true); } catch { }
            }
            string css = AppServices.Scripts.CssFor(url);
            if (css.Length > 0)
            {
                string inject = "(function(){var s=document.createElement('style');s.textContent=" +
                                ProtocolPageRenderer.JsonEncode(css) +
                                ";(document.head||document.documentElement).appendChild(s);})();";
                try { await core.ExecuteScriptAsync(inject).ConfigureAwait(true); } catch { }
            }
            string endScript = AppServices.Scripts.DocumentEndScript(url);
            if (endScript.Length > 0)
            {
                try { await core.ExecuteScriptAsync(endScript).ConfigureAwait(true); } catch { }
            }
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try { await core.ExecuteScriptAsync(MediaSniffer.ProbeScript).ConfigureAwait(true); } catch { }
                bool readable = await ReaderModeExtractor.IsReadableAsync(core).ConfigureAwait(true);
                if (_tabs.Active == tab) _readerBtn.Enabled = readable || tab.IsReader;
            }
        }

        private void OnStatusBarText(object sender, object e)
        {
            string text = ((CoreWebView2)sender).StatusBarText;
            if (InvokeRequired) { BeginInvoke((Action)delegate { _statusLabel.Text = text ?? ""; }); return; }
            _statusLabel.Text = text ?? "";
        }

        // ------------------------------------------------------------------
        // Chrome updates (omnibox, badges, bookmark bar)
        // ------------------------------------------------------------------

        private void UpdateChromeForActiveTab()
        {
            if (InvokeRequired) { BeginInvoke((Action)UpdateChromeForActiveTab); return; }
            BrowserTab tab = _tabs.Active;
            if (tab == null) return;
            Text = (string.IsNullOrEmpty(tab.Title) ? "Swifter" : tab.Title) + " - Swifter";
            if (!_omnibox.ContainsFocus) _omnibox.SetText(tab.Url);
            _back.Enabled = tab.View != null && tab.View.CoreWebView2 != null && tab.View.CoreWebView2.CanGoBack;
            _forward.Enabled = tab.View != null && tab.View.CoreWebView2 != null && tab.View.CoreWebView2.CanGoForward;
            _omnibox.SetSecurity(tab.Url);
            UpdateShieldBadge();
            UpdateSnifferBadge();
            UpdateDownloadPill();
            if (tab.View != null && tab.View.CoreWebView2 != null)
                _statusRight.Text = "zoom " + Math.Round(tab.View.ZoomFactor * 100).ToString(CultureInfo.InvariantCulture) +
                                    "%  |  " + (AppServices.Memory.FrozenCount > 0
                                        ? AppServices.Memory.FrozenCount + " sleeping tabs" : "memory OK");
        }

        private void UpdateShieldBadge()
        {
            BrowserTab tab = _tabs.Active;
            int count = 0;
            if (tab != null) _blockedPerTab.TryGetValue(tab.Id, out count);
            _shieldBtn.Text = count > 0 ? count.ToString(CultureInfo.InvariantCulture) : "0";
            _shieldBtn.Badge = count > 0;
            _shieldBtn.AccentColor = Settings.Accent();
            _shieldBtn.Invalidate();
        }

        private void UpdateSnifferBadge()
        {
            BrowserTab tab = _tabs.Active;
            int count = tab != null ? AppServices.Sniffer.CountFor(tab.Id) : 0;
            _snifferBtn.Badge = count > 0;
            _snifferBtn.Text = count > 0 ? "\u25b6" : "\u25b6";
            _snifferBtn.AccentColor = Settings.Accent();
            _snifferBtn.Invalidate();
        }

        private void UpdateDownloadPill()
        {
            int active = AppServices.Downloads.ActiveCount;
            double speed = AppServices.Downloads.AggregateSpeed;
            _downloadPill.Text = active > 0
                ? "\u2b07 " + SegmentedDownloader.FormatSpeed(speed)
                : "\u2b07 " + AppServices.Downloads.Snapshot().Count(t => t.State == DownloadState.Completed)
                      .ToString(CultureInfo.InvariantCulture);
            _downloadPill.Badge = active > 0;
            _downloadPill.AccentColor = Settings.Accent();
            _downloadPill.Invalidate();
        }

        private void RefreshBookmarksBar()
        {
            _bookmarksBar.Visible = Settings.Model.General.ShowBookmarksBar;
            _bookmarksBar.Controls.Clear();
            int x = 8;
            foreach (BookmarkNode n in AppServices.Bookmarks.BarItems())
            {
                BookmarkNode captured = n;
                Label l = new Label
                {
                    Text = "  " + (string.IsNullOrEmpty(captured.Title) ? captured.Url : captured.Title),
                    AutoSize = true,
                    Location = new Point(x, 7),
                    ForeColor = Settings.IsDarkTheme() ? Color.FromArgb(220, 226, 233) : Color.FromArgb(40, 50, 62),
                    Cursor = Cursors.Hand,
                    MaximumSize = new Size(180, 20),
                    AutoEllipsis = true
                };
                Image img = TabStrip.ImageCache.Get(captured.Favicon);
                if (img != null)
                {
                    l.ImageAlign = ContentAlignment.MiddleLeft;
                    l.Image = img;
                    l.Padding = new Padding(18, 0, 6, 0);
                }
                l.Click += delegate { Navigate(captured.Url); AppServices.Bookmarks.Touch(captured.Id); };
                _bookmarksBar.Controls.Add(l);
                x += l.PreferredWidth + 8;
            }
            Label add = new Label
            {
                Text = "+",
                AutoSize = true,
                Location = new Point(x, 7),
                ForeColor = Color.Gray,
                Cursor = Cursors.Hand
            };
            add.Click += delegate { Navigate("swifter://bookmarks"); };
            _bookmarksBar.Controls.Add(add);
        }

        // ------------------------------------------------------------------
        // Omnibox
        // ------------------------------------------------------------------

        private void OnOmniboxCommit(string text)
        {
            HideSuggest();
            string url = ResolveUrl(text);
            Navigate(url);
        }

        internal async void OmniboxChanged(string text)
        {
            List<SuggestItem> items = new List<SuggestItem>();
            string prefix = text.Trim();
            if (prefix.Length > 0)
            {
                foreach (HistoryEntry h in AppServices.History.Suggest(prefix, 5))
                    items.Add(new SuggestItem { Text = h.Title.Length > 0 ? h.Title : h.Url, Sub = h.Url, Kind = "history" });
                foreach (BookmarkNode b in AppServices.Bookmarks.Search(prefix))
                {
                    if (items.Count >= 9) break;
                    if (!b.IsFolder) items.Add(new SuggestItem { Text = b.Title, Sub = b.Url, Kind = "bookmark" });
                }
                try
                {
                    List<string> remote = await FetchSuggestionsAsync(prefix).ConfigureAwait(true);
                    foreach (string r in remote)
                    {
                        if (items.Count >= 10) break;
                        if (!items.Exists(i => i.Text == r))
                            items.Add(new SuggestItem { Text = r, Sub = ResolveUrl(r), Kind = "search" });
                    }
                }
                catch { }
            }
            if (_closing || IsDisposed) return;
            BeginInvoke((Action)delegate { ShowSuggest(items); });
        }

        private static async Task<List<string>> FetchSuggestionsAsync(string query)
        {
            List<string> results = new List<string>();
            SearchEngine engine = AppServices.Settings.CurrentSearchEngine();
            if (string.IsNullOrEmpty(engine.SuggestTemplate)) return results;
            string url = string.Format(CultureInfo.InvariantCulture, engine.SuggestTemplate, Uri.EscapeDataString(query));
            string body = await SegmentedDownloader.Client.GetStringAsync(url).ConfigureAwait(false);
            using (JsonDocument doc = JsonDocument.Parse(body))
            {
                JsonElement root = doc.RootElement;
                JsonElement arr = default(JsonElement);
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 2)
                    arr = root[1];
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("suggestions", out JsonElement s))
                    arr = s;
                if (arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement el in arr.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String) results.Add(el.GetString());
                        else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("phrase", out JsonElement ph))
                            results.Add(ph.GetString());
                        if (results.Count >= 8) break;
                    }
                }
            }
            return results;
        }

        private void ShowSuggest(List<SuggestItem> items)
        {
            if (items.Count == 0) { HideSuggest(); return; }
            if (_suggest == null || _suggest.IsDisposed) _suggest = new SuggestPopup(PickSuggestion);
            _suggest.Populate(items);
            Point p = _omnibox.PointToScreen(new Point(0, _omnibox.Height + 2));
            _suggest.ShowAt(p, _omnibox.Width);
        }

        private void HideSuggest()
        {
            if (_suggest != null && !_suggest.IsDisposed) _suggest.Hide();
        }

        private void PickSuggestion(SuggestItem item)
        {
            HideSuggest();
            if (item.Kind == "search") _omnibox.SetText(item.Text);
            Navigate(item.Kind == "search" ? ResolveUrl(item.Text) : item.Sub);
        }

        // ------------------------------------------------------------------
        // Shield / menu / PiP / reader / find
        // ------------------------------------------------------------------

        private void OpenShieldMenu()
        {
            BrowserTab tab = _tabs.Active;
            string host = "";
            try { host = new Uri(tab != null ? tab.Url : "").Host; } catch { }
            ContextMenuStrip menu = new ContextMenuStrip();
            int count = 0;
            if (tab != null) _blockedPerTab.TryGetValue(tab.Id, out count);
            menu.Items.Add(new ToolStripLabel(host.Length > 0 ? host : "no site"));
            menu.Items.Add(count + " requests blocked on this page");
            menu.Items.Add(new ToolStripSeparator());
            if (host.Length > 0)
            {
                bool disabled = AppServices.Shields.SiteShieldsDisabled(host);
                ToolStripItem toggle = menu.Items.Add(disabled ? "Re-enable shields on this site" : "Disable shields on this site");
                toggle.Click += delegate
                {
                    AppServices.Shields.SetSiteOverride(host, !disabled);
                    ReloadActive();
                };
            }
            menu.Items.Add("Open shields dashboard", null, delegate { Navigate("swifter://shields"); });
            ToolStripItem cert = menu.Items.Add("View site certificate...");
            cert.Click += delegate { _ = ShowCertificateAsync(host); };
            menu.Show(_shieldBtn, new Point(0, _shieldBtn.Height));
        }

        private async Task ShowCertificateAsync(string host)
        {
            if (host.Length == 0) return;
            Dictionary<string, string> info = await SecurityInspector.InspectAsync(host).ConfigureAwait(true);
            if (_closing || IsDisposed) return;
            BeginInvoke((Action)delegate
            {
                using (CertificateDialog dlg = new CertificateDialog(host, info))
                    dlg.ShowDialog(this);
            });
        }

        private void ShowMainMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("New tab\tCtrl+T", null, delegate { OpenNewTab(Settings.Model.General.HomeUrl, true); });
            menu.Items.Add("New window\tCtrl+N", null, delegate { new BrowserForm(new string[0]).Show(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Downloads\tCtrl+J", null, delegate { Navigate("swifter://downloads"); });
            menu.Items.Add("History\tCtrl+H", null, delegate { Navigate("swifter://history"); });
            menu.Items.Add("Bookmarks\tCtrl+B", null, delegate { Navigate("swifter://bookmarks"); });
            menu.Items.Add("Shields", null, delegate { Navigate("swifter://shields"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Find in page\tCtrl+F", null, delegate { ShowFind(); });
            menu.Items.Add("Zoom in\tCtrl++", null, delegate { Zoom(0.1); });
            menu.Items.Add("Zoom out\tCtrl+-", null, delegate { Zoom(-0.1); });
            menu.Items.Add("Print...\tCtrl+P", null, delegate { PrintActive(); });
            menu.Items.Add("Print to PDF", null, delegate { _ = PrintToPdfAsync(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings", null, delegate { Navigate("swifter://settings"); });
            menu.Items.Add("Task manager", null, delegate
            {
                try { if (_tabs.Active != null && _tabs.Active.View != null) _tabs.Active.View.CoreWebView2.OpenTaskManagerWindow(); }
                catch { }
            });
            menu.Items.Add("Dev tools\tF12", null, delegate
            {
                try { if (_tabs.Active != null && _tabs.Active.View != null) _tabs.Active.View.CoreWebView2.OpenDevToolsWindow(); }
                catch { }
            });
            menu.Show(_menuBtn, new Point(0, _menuBtn.Height));
        }

        private void PrintActive()
        {
            BrowserTab tab = _tabs.Active;
            if (tab == null || tab.View == null || tab.View.CoreWebView2 == null) return;
            _ = tab.View.CoreWebView2.ExecuteScriptAsync("window.print()");
        }

        private async Task PrintToPdfAsync()
        {
            BrowserTab tab = _tabs.Active;
            if (tab == null || tab.View == null || tab.View.CoreWebView2 == null) return;
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "PDF files|*.pdf";
                dlg.FileName = SegmentedDownloader.SanitizeFileName(tab.Title) + ".pdf";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    await tab.View.CoreWebView2.PrintToPdfAsync(dlg.FileName,
                        new CoreWebView2PrintToPdfFormatOptions()).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Print to PDF failed: " + ex.Message, "Swifter",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void Zoom(double delta)
        {
            BrowserTab tab = _tabs.Active;
            if (tab == null || tab.View == null) return;
            double z = Math.Max(0.25, Math.Min(5, tab.View.ZoomFactor + delta));
            tab.View.ZoomFactor = z;
            UpdateChromeForActiveTab();
        }

        private async Task ToggleReaderAsync()
        {
            BrowserTab tab = _tabs.Active;
            if (tab == null || tab.View == null || tab.View.CoreWebView2 == null) return;
            CoreWebView2 core = tab.View.CoreWebView2;
            if (tab.IsReader)
            {
                tab.IsReader = false;
                string src = tab.ReaderSourceUrl;
                tab.ReaderSourceUrl = "";
                NavigateTab(tab, src);
                return;
            }
            ReaderArticle article = await ReaderModeExtractor.ExtractAsync(core).ConfigureAwait(true);
            if (article == null)
            {
                MessageBox.Show(this, "This page does not look like an article.", "Swifter Reader",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            tab.IsReader = true;
            tab.ReaderSourceUrl = tab.Url;
            string html = ReaderModeExtractor.Render(article, Settings.Model.Reader,
                SettingsManager.ToHex(Settings.Accent()));
            core.NavigateToString(html);
        }

        private async Task TogglePipAsync()
        {
            BrowserTab tab = _tabs.Active;
            if (tab == null || tab.View == null || tab.View.CoreWebView2 == null) return;
            CoreWebView2 core = tab.View.CoreWebView2;
            if (_pip != null) { _pip.Close(); _pip = null; StopPipStream(core); return; }

            // Prefer a directly sniffed media URL for this tab.
            SniffedMedia direct = AppServices.Sniffer.ForTab(tab.Id)
                .FirstOrDefault(m => m.Kind == MediaKind.Video || m.Kind == MediaKind.Audio);
            _pip = new PiPForm(tab.Title);
            _pip.FormClosed += delegate
            {
                StopPipStream(core);
                _pip = null;
            };
            if (direct != null && !direct.Url.StartsWith("blob:", StringComparison.Ordinal))
            {
                _pip.ShowDirect(direct.Url);
                return;
            }
            // Otherwise mirror frames from the playing video element.
            try
            {
                string started = await core.ExecuteScriptAsync(PiPStreamScript).ConfigureAwait(true);
                bool ok = started != null && started.Contains("true");
                if (!ok)
                {
                    MessageBox.Show(this, "No playable <video> element found on this page.", "Swifter PiP",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    _pip.Close();
                    _pip = null;
                }
            }
            catch { }
        }

        private void StopPipStream(CoreWebView2 core)
        {
            try { _ = core.ExecuteScriptAsync("window.__swifterPipStop && window.__swifterPipStop();"); } catch { }
        }

        public const string PiPStreamScript = """
            (function () {
              var v = document.querySelector('video');
              if (!v) return 'false';
              try { v.play(); } catch (e) { }
              var canvas = document.createElement('canvas');
              var ctx = canvas.getContext('2d');
              var timer = null;
              function frame() {
                try {
                  if (v.readyState >= 2) {
                    canvas.width = 480;
                    canvas.height = Math.max(1, Math.round(480 * v.videoHeight / Math.max(1, v.videoWidth)));
                    ctx.drawImage(v, 0, 0, canvas.width, canvas.height);
                    window.chrome.webview.postMessage(JSON.stringify({
                      t: 'pipframe', data: canvas.toDataURL('image/jpeg', 0.72),
                      w: v.videoWidth, h: v.videoHeight, paused: v.paused
                    }));
                  }
                } catch (e) { }
              }
              timer = setInterval(frame, 90);
              window.__swifterPipStop = function () { clearInterval(timer); timer = null; };
              return 'true';
            })()
            """;

        private void ShowFind()
        {
            _findBar.Visible = true;
            _findBox.Focus();
            _findBox.SelectAll();
        }

        private void HideFind()
        {
            _findBar.Visible = false;
            BrowserTab tab = _tabs.Active;
            if (tab != null && tab.View != null && tab.View.CoreWebView2 != null)
                _ = tab.View.CoreWebView2.ExecuteScriptAsync(FindScript("''", -1));
            _findQuery = "";
        }

        private async void RunFind()
        {
            _findQuery = _findBox.Text;
            BrowserTab tab = _tabs.Active;
            if (tab == null || tab.View == null || tab.View.CoreWebView2 == null) return;
            try
            {
                string raw = await tab.View.CoreWebView2.ExecuteScriptAsync(
                    FindScript(ProtocolPageRenderer.JsonEncode(_findQuery), _findIndex)).ConfigureAwait(true);
                FindResult(raw);
            }
            catch { }
        }

        private void FindStep(int delta)
        {
            _findIndex += delta;
            RunFind();
        }

        private void FindResult(string json)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement c;
                    int total = doc.RootElement.TryGetProperty("count", out c) ? c.GetInt32() : 0;
                    if (InvokeRequired)
                    {
                        BeginInvoke((Action)delegate { _findCount.Text = (_findIndex + 1) + "/" + total; });
                    }
                    else _findCount.Text = (_findIndex + 1) + "/" + total;
                }
            }
            catch { }
        }

        public const string FindScriptTemplate = """
            (function (q, idx) {
              function clear() {
                document.querySelectorAll('mark[data-swfind]').forEach(function (m) {
                  var parent = m.parentNode;
                  parent.replaceChild(document.createTextNode(m.textContent), m);
                  parent.normalize();
                });
              }
              clear();
              if (!q) return JSON.stringify({ count: 0 });
              var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null);
              var nodes = [], n;
              var lower = q.toLowerCase();
              while ((n = walker.nextNode())) {
                if (n.nodeValue.toLowerCase().indexOf(lower) >= 0) nodes.push(n);
              }
              var count = 0;
              nodes.forEach(function (node) {
                var text = node.nodeValue;
                var parts = text.toLowerCase().split(lower);
                if (parts.length < 2) return;
                var frag = document.createDocumentFragment();
                var pos = 0;
                for (var i = 0; i < parts.length - 1; i++) {
                  frag.appendChild(document.createTextNode(text.substr(pos, parts[i].length)));
                  var mark = document.createElement('mark');
                  mark.setAttribute('data-swfind', '1');
                  mark.textContent = text.substr(pos + parts[i].length, q.length);
                  frag.appendChild(mark);
                  pos += parts[i].length + q.length;
                  count++;
                }
                frag.appendChild(document.createTextNode(text.substr(pos)));
                node.parentNode.replaceChild(frag, node);
              });
              var marks = document.querySelectorAll('mark[data-swfind]');
              if (idx < 0) idx = 0;
              if (marks.length) {
                idx = ((idx % marks.length) + marks.length) % marks.length;
                marks[idx].scrollIntoView({ block: 'center' });
                marks[idx].style.background = '#ff8c61';
              }
              return JSON.stringify({ count: count });
            })(__QUERY__, __INDEX__)
            """;

        private string FindScript(string quotedQuery, int index)
        {
            return FindScriptTemplate.Replace("__QUERY__", quotedQuery)
                .Replace("__INDEX__", index.ToString(CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------
        // Window plumbing
        // ------------------------------------------------------------------

        private void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST && WindowState != FormWindowState.Maximized)
            {
                Point p = PointToClient(new Point(m.LParam.ToInt32() & 0xFFFF, m.LParam.ToInt32() >> 16));
                const int border = 7;
                bool left = p.X <= border, right = p.X >= ClientSize.Width - border;
                bool top = p.Y <= border, bottom = p.Y >= ClientSize.Height - border;
                if (left && top) { m.Result = (IntPtr)HTTOPLEFT; return; }
                if (right && top) { m.Result = (IntPtr)HTTOPRIGHT; return; }
                if (left && bottom) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
                if (right && bottom) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
                if (left) { m.Result = (IntPtr)HTLEFT; return; }
                if (right) { m.Result = (IntPtr)HTRIGHT; return; }
                if (top) { m.Result = (IntPtr)HTTOP; return; }
                if (bottom) { m.Result = (IntPtr)HTBOTTOM; return; }
                if (p.Y < _titleBar.Bottom && p.Y >= 0)
                {
                    Control child = _titleBar.GetChildAtPoint(_titleBar.PointToClient(PointToScreen(p)));
                    if (child == null) { m.Result = (IntPtr)HTCAPTION; return; }
                }
            }
            else if (m.Msg == WM_GETMINMAXINFO)
            {
                Win32.MinMaxInfo mmi = Marshal.PtrToStructure<Win32.MinMaxInfo>(m.LParam);
                Screen screen = Screen.FromHandle(Handle);
                Rectangle work = screen.WorkingArea;
                mmi.ptMaxPosition.X = work.Left - screen.Bounds.Left;
                mmi.ptMaxPosition.Y = work.Top - screen.Bounds.Top;
                mmi.ptMaxSize.X = work.Width;
                mmi.ptMaxSize.Y = work.Height;
                Marshal.StructureToPtr(mmi, m.LParam, true);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.T: OpenNewTab(Settings.Model.General.HomeUrl, true); return true;
                case Keys.Control | Keys.N: new BrowserForm(new string[0]).Show(); return true;
                case Keys.Control | Keys.W: CloseActiveTab(); return true;
                case Keys.Control | Keys.L: _omnibox.FocusAndSelect(); return true;
                case Keys.Control | Keys.R: ReloadActive(); return true;
                case Keys.Control | Keys.F5: ReloadActive(); return true;
                case Keys.F5: ReloadActive(); return true;
                case Keys.Control | Keys.J: Navigate("swifter://downloads"); return true;
                case Keys.Control | Keys.H: Navigate("swifter://history"); return true;
                case Keys.Control | Keys.B: Navigate("swifter://bookmarks"); return true;
                case Keys.Control | Keys.F: ShowFind(); return true;
                case Keys.Control | Keys.D: _ = PrintToPdfAsync(); return true;
                case Keys.Control | Keys.P: PrintActive(); return true;
                case Keys.F12:
                    if (_tabs.Active != null && _tabs.Active.View != null && _tabs.Active.View.CoreWebView2 != null)
                        _tabs.Active.View.CoreWebView2.OpenDevToolsWindow();
                    return true;
                case Keys.Alt | Keys.Left: GoBack(); return true;
                case Keys.Alt | Keys.Right: GoForward(); return true;
                case Keys.Control | Keys.Add: Zoom(0.1); return true;
                case Keys.Control | Keys.Oemplus: Zoom(0.1); return true;
                case Keys.Control | Keys.Subtract: Zoom(-0.1); return true;
                case Keys.Control | Keys.OemMinus: Zoom(-0.1); return true;
                case Keys.Control | Keys.D0:
                    if (_tabs.Active != null && _tabs.Active.View != null) _tabs.Active.View.ZoomFactor = 1.0;
                    return true;
                case Keys.F11:
                    _fullScreen = !_fullScreen;
                    WindowState = _fullScreen ? FormWindowState.Maximized : FormWindowState.Normal;
                    _titleBar.Visible = !_fullScreen;
                    _toolbar.Visible = !_fullScreen;
                    _statusBar.Visible = !_fullScreen && Settings.Model.General.ShowStatusBar;
                    return true;
                case Keys.Escape:
                    if (_findBar.Visible) { HideFind(); return true; }
                    break;
            }
            if ((keyData & Keys.Control) == Keys.Control)
            {
                Keys digit = keyData & Keys.KeyCode;
                if (digit >= Keys.D1 && digit <= Keys.D9)
                {
                    int idx = digit - Keys.D1;
                    if (idx == 8) { if (_tabs.Tabs.Count > 0) _tabs.ActivateTab(_tabs.Tabs[_tabs.Tabs.Count - 1]); }
                    else if (idx < _tabs.Tabs.Count) _tabs.ActivateTab(_tabs.Tabs[idx]);
                    return true;
                }
                if (digit == Keys.Tab && _tabs.Tabs.Count > 0)
                {
                    int i = _tabs.IndexOf(_tabs.Active);
                    _tabs.ActivateTab(_tabs.Tabs[(i + 1) % _tabs.Tabs.Count]);
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private bool _fullScreen;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Normal)
            {
                Settings.Model.Window.X = Location.X;
                Settings.Model.Window.Y = Location.Y;
                Settings.Model.Window.Width = Width;
                Settings.Model.Window.Height = Height;
                Settings.Model.Window.Maximized = false;
            }
            else if (WindowState == FormWindowState.Maximized)
            {
                Settings.Model.Window.Maximized = true;
            }
            Settings.SaveLater();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closing)
            {
                _closing = true;
                if (Settings.Model.General.ConfirmBeforeExit && _tabs.Tabs.Count > 1 &&
                    e.CloseReason == CloseReason.UserClosing)
                {
                    DialogResult r = MessageBox.Show(this,
                        "Close Swifter with " + _tabs.Tabs.Count + " tabs open?",
                        "Swifter", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r == DialogResult.No) { _closing = false; e.Cancel = true; return; }
                }
                HideSuggest();
                if (_pip != null) _pip.Close();
                AppServices.Session.SaveNow(SessionProvider());
                if (Settings.Model.Privacy.ClearOnExit)
                {
                    try { File.WriteAllText(AppPaths.DataDir + "\\clear-on-start.flag", "1"); } catch { }
                    if (Settings.Model.Privacy.ClearHistoryOnExit) AppServices.History.ClearAll();
                }
                AppServices.Downloads.Persist();
                AppServices.Shields.SaveStats();
            }
            base.OnFormClosing(e);
        }

        private static class Win32
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct MinMaxInfo
            {
                public Point ptReserved;
                public Point ptMaxSize;
                public Point ptMaxPosition;
                public Point ptMaxTrackSize;
                public Point ptMinTrackSize;
            }
        }
    }

    /// <summary>Window caption buttons (minimise / maximise / close).</summary>
    public sealed class CaptionButtons : Control
    {
        public event Action Minimize;
        public event Action Maximize;
        public event Action Close;

        private bool _dark = true;
        private int _hover = -1;

        public CaptionButtons()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            Height = 42;
        }

        public void ApplyTheme(bool dark)
        {
            _dark = dark;
            Invalidate();
        }

        private Rectangle Zone(int i)
        {
            return new Rectangle(i * 46, 0, 46, Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fg = _dark ? Color.FromArgb(232, 237, 242) : Color.FromArgb(27, 36, 48);
            for (int i = 0; i < 3; i++)
            {
                Rectangle r = Zone(i);
                if (_hover == i)
                {
                    using (SolidBrush b = new SolidBrush(i == 2 ? Color.FromArgb(232, 17, 35) :
                           Color.FromArgb(_dark ? 45 : 225, _dark ? 52 : 229, _dark ? 62 : 235)))
                        g.FillRectangle(b, r);
                }
                using (Pen p = new Pen(_hover == 2 && i == 2 ? Color.White : fg, 1.2f))
                {
                    int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
                    if (i == 0) g.DrawLine(p, cx - 5, cy, cx + 5, cy);
                    else if (i == 1) g.DrawRectangle(p, cx - 5, cy - 5, 10, 10);
                    else { g.DrawLine(p, cx - 5, cy - 5, cx + 5, cy + 5); g.DrawLine(p, cx + 5, cy - 5, cx - 5, cy + 5); }
                }
            }
        }

        private int Hit(Point p)
        {
            for (int i = 0; i < 3; i++) if (Zone(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h != _hover) { _hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            switch (Hit(e.Location))
            {
                case 0: if (Minimize != null) Minimize(); break;
                case 1: if (Maximize != null) Maximize(); break;
                case 2: if (Close != null) Close(); break;
            }
            base.OnMouseClick(e);
        }
    }

    /// <summary>Toolbar button with optional accent badge glow.</summary>
    public sealed class ToolButton : Control
    {
        private string _text;
        private bool _badge;
        private Color _accent = Color.FromArgb(76, 194, 255);
        private bool _hover;

        public ToolButton(string text)
        {
            _text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
        }

        public string Text2 { get { return _text; } }

        public new string Text
        {
            get { return _text; }
            set { _text = value; Invalidate(); }
        }

        public bool Badge
        {
            get { return _badge; }
            set { _badge = value; Invalidate(); }
        }

        public Color AccentColor
        {
            get { return _accent; }
            set { _accent = value; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_hover || _badge)
            {
                using (SolidBrush b = new SolidBrush(_badge
                    ? Color.FromArgb(40, _accent) : Color.FromArgb(30, 128, 128, 128)))
                {
                    using (GraphicsPath path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
                        g.FillPath(b, path);
                }
            }
            Color fg = Enabled ? ForeColor : Color.Gray;
            TextRenderer.DrawText(g, _text, Font, new Rectangle(0, 0, Width, Height), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (_badge)
            {
                using (Pen p = new Pen(_accent, 1.4f))
                    g.DrawEllipse(p, Width - 9, 5, 6, 6);
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { OnClick(e); base.OnMouseClick(e); }
    }

    /// <summary>Rounded omnibox with padlock / protocol badge and inline suggestions.</summary>
    public sealed class Omnibox : Control
    {
        public event Action<string> Commit;
        private readonly TextBox _box = new TextBox();
        private Color _accent = Color.FromArgb(76, 194, 255);
        private Color _border = Color.FromArgb(42, 52, 64);
        private bool _suppress;
        private string _scheme = "";
        private readonly BrowserForm _form;

        public Omnibox(BrowserForm form)
        {
            _form = form;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            Height = 32;
            _box.BorderStyle = BorderStyle.None;
            _box.Font = new Font("Segoe UI", 10f);
            Controls.Add(_box);
            _box.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    Action<string> h = Commit;
                    if (h != null) h(_box.Text);
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    _box.Text = _form != null && _form.ActiveUrl != null ? _form.ActiveUrl : "";
                }
            };
            _box.TextChanged += delegate
            {
                if (_suppress) return;
                if (_form != null) _form.OmniboxChanged(_box.Text);
            };
        }

        public string ActiveUrlText { get { return _box.Text; } }

        public void ApplyTheme(bool dark, Color accent, Color border, Color text)
        {
            _accent = accent;
            _border = border;
            _box.BackColor = dark ? Color.FromArgb(16, 21, 27) : Color.White;
            _box.ForeColor = text;
            Invalidate();
        }

        public void SetText(string url)
        {
            if (_box.Focused) return;
            _suppress = true;
            _box.Text = url ?? "";
            _suppress = false;
            _scheme = "";
            if (url != null && url.StartsWith("swifter://", StringComparison.OrdinalIgnoreCase))
                _scheme = "SWIFTER";
            else if (url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                _scheme = "SECURE";
            else if (url != null && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                _scheme = "INSECURE";
            Invalidate();
        }

        public void SetSecurity(string url)
        {
            SetText(url);
        }

        public void FocusAndSelect()
        {
            _box.Focus();
            _box.SelectAll();
        }


        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (SolidBrush b = new SolidBrush(_box.BackColor))
            using (GraphicsPath path = RoundRect(r, 9))
                g.FillPath(b, path);
            using (Pen p = new Pen(_box.Focused ? _accent : _border, _box.Focused ? 1.6f : 1f))
            using (GraphicsPath path = RoundRect(r, 9))
                g.DrawPath(p, path);

            int iconX = 10;
            if (_scheme == "SWIFTER")
            {
                using (SolidBrush b = new SolidBrush(_accent))
                using (Font f = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                    g.DrawString("SWIFTER", f, b, iconX, (Height - 12) / 2);
                iconX += 52;
            }
            else if (_scheme == "SECURE")
            {
                using (Pen p = new Pen(Color.FromArgb(55, 214, 122), 1.5f))
                {
                    g.DrawArc(p, iconX + 1, 8, 8, 7, 180, 180);
                    g.DrawRectangle(p, iconX, 14, 10, 8);
                }
                iconX += 18;
            }
            else if (_scheme == "INSECURE")
            {
                using (Pen p = new Pen(Color.FromArgb(255, 180, 84), 1.5f))
                {
                    g.DrawRectangle(p, iconX, 11, 10, 9);
                    g.DrawArc(p, iconX + 1, 6, 8, 8, 180, 180);
                    g.DrawLine(p, iconX + 5, 13, iconX + 5, 16);
                }
                iconX += 18;
            }
            else iconX += 4;
            _box.Location = new Point(iconX, (Height - 20) / 2);
            _box.Width = Math.Max(40, Width - iconX - 8);
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }
    }

    public sealed class SuggestItem
    {
        public string Text = "";
        public string Sub = "";
        public string Kind = "";
    }

    /// <summary>Borderless non-activating suggestion dropdown.</summary>
    public sealed class SuggestPopup : Form
    {
        private readonly ListBox _list = new ListBox();
        private readonly Action<SuggestItem> _pick;

        public SuggestPopup(Action<SuggestItem> pick)
        {
            _pick = pick;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(28, 34, 42);
            _list.BorderStyle = BorderStyle.None;
            _list.Dock = DockStyle.Fill;
            _list.BackColor = BackColor;
            _list.ForeColor = Color.FromArgb(232, 237, 242);
            _list.Font = new Font("Segoe UI", 9.5f);
            _list.ItemHeight = 34;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.DrawItem += OnDrawItem;
            _list.MouseDown += delegate (object s, MouseEventArgs e)
            {
                int idx = _list.IndexFromPoint(e.Location);
                if (idx >= 0 && _list.Items[idx] is SuggestItem item) _pick(item);
            };
            Controls.Add(_list);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x00800000;      // WS_BORDER for a crisp edge
                return cp;
            }
        }

        private void OnDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            SuggestItem item = (SuggestItem)_list.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (SolidBrush b = new SolidBrush(sel ? Color.FromArgb(45, 55, 68) : BackColor))
                e.Graphics.FillRectangle(b, e.Bounds);
            Color kind = item.Kind == "bookmark" ? Color.FromArgb(255, 209, 102) :
                         item.Kind == "history" ? Color.FromArgb(152, 164, 179) : Color.FromArgb(76, 194, 255);
            TextRenderer.DrawText(e.Graphics, item.Text, e.Font,
                new Rectangle(e.Bounds.Left + 8, e.Bounds.Top + 2, e.Bounds.Width - 90, 18),
                ForeColor, TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, item.Sub, e.Font,
                new Rectangle(e.Bounds.Left + 8, e.Bounds.Top + 18, e.Bounds.Width - 90, 14),
                Color.FromArgb(120, 130, 142), TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, item.Kind, e.Font,
                new Rectangle(e.Bounds.Right - 78, e.Bounds.Top + 9, 70, 16),
                kind, TextFormatFlags.Right);
        }

        public void Populate(List<SuggestItem> items)
        {
            _list.Items.Clear();
            foreach (SuggestItem i in items) _list.Items.Add(i);
            Height = Math.Min(320, 12 + items.Count * 34);
        }

        public void ShowAt(Point screen, int width)
        {
            Width = width;
            Location = screen;
            if (!Visible) Show();
        }
    }

    /// <summary>Floating always-on-top picture-in-picture window.</summary>
    public sealed class PiPForm : Form
    {
        private readonly PictureBox _box = new PictureBox();
        private WebView2 _direct;
        private readonly Label _title;

        public PiPForm(string title)
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Width = 480;
            Height = 300;
            Screen s = Screen.PrimaryScreen;
            Location = new Point(s.WorkingArea.Right - Width - 24, s.WorkingArea.Bottom - Height - 24);
            _box.Dock = DockStyle.Fill;
            _box.SizeMode = PictureBoxSizeMode.Zoom;
            _title = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 24,
                BackColor = Color.FromArgb(20, 22, 26),
                ForeColor = Color.FromArgb(220, 226, 233),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                AutoEllipsis = true
            };
            Label close = new Label
            {
                Text = "\u2715",
                Dock = DockStyle.Right,
                Width = 30,
                BackColor = Color.FromArgb(20, 22, 26),
                ForeColor = Color.FromArgb(220, 226, 233),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            close.Click += delegate { Close(); };
            Controls.Add(_box);
            Controls.Add(_title);
            Controls.Add(close);
            MouseDown += OnDragStart;
            _title.MouseDown += OnDragStart;
        }

        private Point _dragOffset;
        private bool _drag;

        private void OnDragStart(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _drag = true;
            _dragOffset = e.Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag) Location = new Point(Location.X + e.X - _dragOffset.X, Location.Y + e.Y - _dragOffset.Y);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _drag = false;
            base.OnMouseUp(e);
        }

        public async void ShowDirect(string url)
        {
            _box.Visible = false;
            _direct = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_direct);
            _direct.BringToFront();
            try
            {
                await _direct.EnsureCoreWebView2Async(AppServices.Env).ConfigureAwait(true);
                _direct.CoreWebView2.Navigate(url);
            }
            catch { }
            Show();
        }

        public void PushFrame(string json)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)delegate
                {
                    try
                    {
                        using (JsonDocument doc = JsonDocument.Parse(json))
                        {
                            JsonElement d;
                            if (!doc.RootElement.TryGetProperty("data", out d)) return;
                            string data = d.GetString();
                            int comma = data.IndexOf(',');
                            byte[] bytes = Convert.FromBase64String(data.Substring(comma + 1));
                            Image old = _box.Image;
                            _box.Image = Image.FromStream(new MemoryStream(bytes));
                            if (old != null) old.Dispose();
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_direct != null) { try { _direct.Dispose(); } catch { } _direct = null; }
            base.OnFormClosed(e);
        }
    }

    /// <summary>Reads the live TLS certificate of a host over SslStream.</summary>
    public static class SecurityInspector
    {
        public static async Task<Dictionary<string, string>> InspectAsync(string host)
        {
            Dictionary<string, string> info = new Dictionary<string, string>();
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    await client.ConnectAsync(host, 443).ConfigureAwait(false);
                    using (SslStream ssl = new SslStream(client.GetStream(), false,
                        delegate { return true; }))
                    {
                        await ssl.AuthenticateAsClientAsync(host).ConfigureAwait(false);
                        info["Protocol"] = ssl.SslProtocol.ToString();
                        info["Cipher"] = ssl.NegotiatedCipherSuite.ToString();
                        if (ssl.RemoteCertificate is X509Certificate2 cert)
                        {
                            info["Subject"] = cert.Subject;
                            info["Issuer"] = cert.Issuer;
                            info["Valid from"] = cert.NotBefore.ToString("u");
                            info["Valid to"] = cert.NotAfter.ToString("u");
                            info["Serial"] = cert.SerialNumber;
                            info["Thumbprint"] = cert.Thumbprint;
                            info["Signature"] = cert.SignatureAlgorithm.FriendlyName;
                            info["Key"] = cert.PublicKey.Oid.FriendlyName;
                            using (RSA rsa = cert.GetRSAPublicKey())
                            {
                                if (rsa != null) info["Key"] += " " + rsa.KeySize.ToString(CultureInfo.InvariantCulture) + " bit";
                            }
                            foreach (X509Extension ext in cert.Extensions)
                            {
                                if (ext is X509SubjectAlternativeNameExtension san)
                                    info["Alt names"] = san.Format(true).Replace("\r\n", ", ");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                info["Error"] = ex.Message;
            }
            return info;
        }
    }

    /// <summary>Modal certificate detail dialog.</summary>
    public sealed class CertificateDialog : Form
    {
        public CertificateDialog(string host, Dictionary<string, string> info)
        {
            Text = "Certificate - " + host;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Width = 560;
            Height = 420;
            TextBox box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5f),
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(24, 28, 34),
                ForeColor = Color.FromArgb(226, 232, 240)
            };
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Host: " + host);
            sb.AppendLine(new string('-', 60));
            foreach (KeyValuePair<string, string> kv in info)
            {
                sb.AppendLine(kv.Key + ":");
                sb.AppendLine("    " + kv.Value);
            }
            box.Text = sb.ToString();
            Button ok = new Button { Text = "Close", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 36 };
            Controls.Add(box);
            Controls.Add(ok);
            AcceptButton = ok;
        }
    }
}
