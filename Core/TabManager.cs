using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Swifter.Config;

namespace Swifter
{
    public sealed class BrowserTab
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Title = "New tab";
        public string Url = "";
        public string Favicon = "";
        public bool Pinned;
        public bool Muted;
        public bool PlayingAudio;
        public string GroupId = "";
        public WebView2 View;
        public bool Frozen;
        public bool Discarded;
        public bool Loading;
        public bool IsInternal;
        public bool IsReader;
        public string ReaderSourceUrl = "";
        public long HistoryVisitId = -1;
        public DateTime LastActive = DateTime.Now;
        public DateTime CreatedAt = DateTime.Now;
    }

    public sealed class TabGroup
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "Group";
        public Color Color = Color.FromArgb(76, 194, 255);
        public bool Collapsed;
    }

    /// <summary>
    /// Tab strip state machine: ordering, pinning, colour coded groups, mute state
    /// plus the owner drawn strip control with drag reordering and hover previews.
    /// </summary>
    public sealed class TabManager
    {
        public List<BrowserTab> Tabs { get; } = new List<BrowserTab>();
        public List<TabGroup> Groups { get; } = new List<TabGroup>();
        public BrowserTab Active { get; private set; }
        public TabStrip Strip { get; }

        private readonly Panel _host;
        private readonly CoreWebView2Environment _env;

        public event Action<BrowserTab> TabCreated;
        public event Action<BrowserTab> TabClosed;
        public event Action<BrowserTab> ActiveChanged;
        public event Action Changed;

        public TabManager(Panel host, CoreWebView2Environment env)
        {
            _host = host;
            _env = env;
            Strip = new TabStrip(this);
        }

        public BrowserTab this[int index] { get { return Tabs[index]; } }

        public int IndexOf(BrowserTab tab) { return Tabs.IndexOf(tab); }

        public BrowserTab CreateTab(string url, bool activate, bool pinned)
        {
            BrowserTab tab = new BrowserTab { Url = url ?? "", Pinned = pinned };
            if (pinned) Tabs.Insert(0, tab);
            else
            {
                int insertAt = Tabs.Count;
                if (Active != null && !activate)
                {
                    insertAt = IndexOf(Active) + 1;
                }
                Tabs.Insert(insertAt, tab);
            }
            tab.View = new WebView2
            {
                Dock = DockStyle.Fill,
                Visible = false,
                DefaultBackgroundColor = Color.FromArgb(15, 18, 22)
            };
            _host.Controls.Add(tab.View);
            tab.View.BringToFront();

            Action<BrowserTab> created = TabCreated;
            if (created != null) created(tab);
            if (activate || Active == null) ActivateTab(tab);
            RaiseChanged();
            return tab;
        }

        public void CloseTab(BrowserTab tab)
        {
            if (tab == null || !Tabs.Contains(tab)) return;
            int index = IndexOf(tab);
            Tabs.Remove(tab);

            BrowserTab next = null;
            if (Active == tab)
            {
                next = Tabs.ElementAtOrDefault(index) ?? Tabs.ElementAtOrDefault(index - 1);
            }
            if (tab.View != null)
            {
                try
                {
                    if (tab.View.CoreWebView2 != null) tab.View.CoreWebView2.Stop();
                    _host.Controls.Remove(tab.View);
                    tab.View.Dispose();
                }
                catch { }
                tab.View = null;
            }
            Action<BrowserTab> closed = TabClosed;
            if (closed != null) closed(tab);
            if (next != null) ActivateTab(next);
            else if (Active == tab) Active = null;
            RaiseChanged();
        }

        public void ActivateTab(BrowserTab tab)
        {
            if (tab == null || Active == tab) return;
            BrowserTab previous = Active;
            Active = tab;
            foreach (BrowserTab t in Tabs)
            {
                if (t.View == null) continue;
                bool show = t == tab;
                t.View.Visible = show;
                if (show) t.View.BringToFront();
            }
            if (previous != null) AppServices.Memory?.Note(previous.Id);
            AppServices.Memory?.Wake(tab.Id);
            tab.LastActive = DateTime.Now;
            Action<BrowserTab> h = ActiveChanged;
            if (h != null) h(tab);
            RaiseChanged();
        }

        public void MoveTab(int from, int to)
        {
            if (from < 0 || from >= Tabs.Count || to < 0 || to >= Tabs.Count || from == to) return;
            BrowserTab tab = Tabs[from];
            Tabs.RemoveAt(from);
            Tabs.Insert(to, tab);
            RaiseChanged();
        }

        public void TogglePin(BrowserTab tab)
        {
            tab.Pinned = !tab.Pinned;
            if (tab.Pinned)
            {
                Tabs.Remove(tab);
                Tabs.Insert(0, tab);
            }
            RaiseChanged();
        }

        public void ToggleMute(BrowserTab tab)
        {
            tab.Muted = !tab.Muted;
            try
            {
                if (tab.View != null && tab.View.CoreWebView2 != null)
                    tab.View.CoreWebView2.IsMuted = tab.Muted;
            }
            catch { }
            RaiseChanged();
        }

        // ---- groups ---------------------------------------------------------

        public TabGroup CreateGroup(string name, Color color, BrowserTab firstTab)
        {
            TabGroup g = new TabGroup { Name = name, Color = color };
            Groups.Add(g);
            if (firstTab != null) firstTab.GroupId = g.Id;
            RaiseChanged();
            return g;
        }

        public TabGroup GroupOf(BrowserTab tab)
        {
            if (string.IsNullOrEmpty(tab.GroupId)) return null;
            return Groups.Find(g => g.Id == tab.GroupId);
        }

        public void AddToGroup(BrowserTab tab, TabGroup group)
        {
            tab.GroupId = group == null ? "" : group.Id;
            RaiseChanged();
        }

        public void RenameGroup(TabGroup g, string name)
        {
            g.Name = name;
            RaiseChanged();
        }

        public void CloseGroup(TabGroup g)
        {
            foreach (BrowserTab t in Tabs.Where(x => x.GroupId == g.Id).ToList()) CloseTab(t);
            Groups.Remove(g);
            RaiseChanged();
        }

        public void RemoveGroup(TabGroup g)
        {
            foreach (BrowserTab t in Tabs.Where(x => x.GroupId == g.Id)) t.GroupId = "";
            Groups.Remove(g);
            RaiseChanged();
        }

        public List<BrowserTab> PinnedTabs() { return Tabs.Where(t => t.Pinned).ToList(); }

        private void RaiseChanged()
        {
            Action h = Changed;
            if (h != null) h();
        }

        public SettingsManager Settings { get { return AppServices.Settings; } }
    }

    /// <summary>Owner drawn modern tab strip.</summary>
    public sealed class TabStrip : Control
    {
        private readonly TabManager _mgr;
        private readonly List<Rectangle> _rects = new List<Rectangle>();
        private readonly List<Rectangle> _closeRects = new List<Rectangle>();
        private readonly List<Rectangle> _audioRects = new List<Rectangle>();
        private Rectangle _newTabRect;
        private int _hover = -1;
        private int _dragIndex = -1;
        private Point _dragStart = Point.Empty;
        private bool _dragging;
        private readonly Timer _previewTimer;
        private TabPreviewPopup _preview;
        private int _scroll;

        private Color _bg, _tab, _tabActive, _text, _muted, _border, _accent;

        public TabStrip(TabManager mgr)
        {
            _mgr = mgr;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 42;
            _previewTimer = new Timer { Interval = 750 };
            _previewTimer.Tick += delegate { _previewTimer.Stop(); ShowPreviewAsync(); };
            mgr.Changed += delegate { Invalidate(); };
            RefreshTheme();
        }

        public void RefreshTheme()
        {
            SettingsManager s = AppServices.Settings;
            bool dark = s.IsDarkTheme();
            _bg = dark ? Color.FromArgb(22, 27, 34) : Color.FromArgb(235, 239, 244);
            _tab = dark ? Color.FromArgb(35, 42, 52) : Color.FromArgb(255, 255, 255);
            _tabActive = dark ? Color.FromArgb(15, 18, 22) : Color.FromArgb(255, 255, 255);
            _text = dark ? Color.FromArgb(232, 237, 242) : Color.FromArgb(27, 36, 48);
            _muted = dark ? Color.FromArgb(152, 164, 179) : Color.FromArgb(92, 107, 122);
            _border = dark ? Color.FromArgb(42, 52, 64) : Color.FromArgb(216, 223, 231);
            _accent = s.Accent();
            Invalidate();
        }

        private void LayoutTabs()
        {
            _rects.Clear(); _closeRects.Clear(); _audioRects.Clear();
            int x = 8 - _scroll;
            int y = 6;
            int h = Height - 10;
            int available = ClientSize.Width - 46;
            int count = _mgr.Tabs.Count;
            int normal = _mgr.Tabs.Count(t => !t.Pinned);
            int pinned = count - normal;
            int pinnedW = 44;
            int maxW = 210;
            int minW = 110;
            int w = normal == 0 ? maxW : Math.Max(minW, Math.Min(maxW, (available - pinned * pinnedW) / normal));
            foreach (BrowserTab tab in _mgr.Tabs)
            {
                int tw = tab.Pinned ? pinnedW : w;
                Rectangle r = new Rectangle(x, y, tw, h);
                _rects.Add(r);
                _closeRects.Add(tab.Pinned ? Rectangle.Empty :
                    new Rectangle(r.Right - 24, r.Top + (h - 18) / 2, 18, 18));
                _audioRects.Add(new Rectangle(r.Right - (tab.Pinned ? 22 : 44), r.Top + (h - 16) / 2, 16, 16));
                x += tw + 4;
            }
            _newTabRect = new Rectangle(Math.Min(x + 4, ClientSize.Width - 34), y + (h - 26) / 2, 26, 26);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            LayoutTabs();
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_bg);
            using (Pen border = new Pen(_border))
                g.DrawLine(border, 0, Height - 1, Width, Height - 1);

            for (int i = 0; i < _mgr.Tabs.Count; i++)
            {
                BrowserTab tab = _mgr.Tabs[i];
                Rectangle r = _rects[i];
                if (r.Right < 0 || r.Left > Width) continue;
                bool active = _mgr.Active == tab;
                TabGroup group = _mgr.GroupOf(tab);

                using (SolidBrush brush = new SolidBrush(active ? _tabActive :
                       (_hover == i ? _tab : Color.FromArgb((int)(_tab.A * 0.55), _tab))))
                using (GraphicsPath path = RoundRect(r, 9))
                {
                    g.FillPath(brush, path);
                    if (group != null)
                    {
                        using (Pen gp = new Pen(group.Color, 2))
                        {
                            g.DrawLine(gp, r.Left + 4, r.Top + 1, r.Right - 4, r.Top + 1);
                        }
                    }
                }

                int iconX = r.Left + 8;
                if (tab.Frozen || tab.Discarded)
                {
                    using (Font f = new Font("Segoe UI", 8f, FontStyle.Bold))
                    using (SolidBrush b = new SolidBrush(_muted))
                        g.DrawString(tab.Discarded ? "zZ" : "*", f, b, iconX + 2, r.Top + (r.Height - 12) / 2);
                }
                else if (!string.IsNullOrEmpty(tab.Favicon))
                {
                    Image img = ImageCache.Get(tab.Favicon);
                    if (img != null) g.DrawImage(img, iconX, r.Top + (r.Height - 16) / 2, 16, 16);
                }
                else if (tab.Loading)
                {
                    using (Pen p = new Pen(_accent, 2))
                        g.DrawArc(p, iconX, r.Top + (r.Height - 14) / 2, 14, 14, -60, 260);
                }

                if (!tab.Pinned)
                {
                    Rectangle textRect = new Rectangle(iconX + 20, r.Top, r.Width - 20 - 46, r.Height);
                    string title = tab.Title;
                    if (group != null && i == FirstIndexOfGroup(group))
                        title = group.Name + " · " + title;
                    TextRenderer.DrawText(g, Truncate(g, title, textRect.Width, active),
                        Font, textRect, active ? _text : _muted,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                    if (tab.PlayingAudio || tab.Muted) DrawSpeaker(g, _audioRects[i], tab.Muted, tab.Muted ? _muted : _accent);
                    Rectangle cr = _closeRects[i];
                    bool hoverClose = cr.Contains(PointToClient(Cursor.Position));
                    using (Font f = new Font("Segoe UI", 9f, FontStyle.Bold))
                    using (SolidBrush b = new SolidBrush(hoverClose ? _text : _muted))
                        g.DrawString("✕", f, b, cr.Left + 3, cr.Top + 1);
                }
                else if (tab.PlayingAudio || tab.Muted)
                {
                    DrawSpeaker(g, _audioRects[i], tab.Muted, tab.Muted ? _muted : _accent);
                }
            }

            using (Font f = new Font("Segoe UI", 12f))
            using (SolidBrush b = new SolidBrush(_muted))
                g.DrawString("+", f, b, _newTabRect.Left + 6, _newTabRect.Top + 2);
        }

        private static void DrawSpeaker(Graphics g, Rectangle r, bool muted, Color color)
        {
            using (SolidBrush b = new SolidBrush(color))
            {
                Point[] cone =
                {
                    new Point(r.Left + 1, r.Top + r.Height / 2 - 2),
                    new Point(r.Left + 4, r.Top + r.Height / 2 - 2),
                    new Point(r.Left + 8, r.Top + 1),
                    new Point(r.Left + 8, r.Bottom - 1),
                    new Point(r.Left + 4, r.Top + r.Height / 2 + 2),
                    new Point(r.Left + 1, r.Top + r.Height / 2 + 2)
                };
                g.FillPolygon(b, cone);
            }
            using (Pen p = new Pen(color, 1.4f))
            {
                if (muted)
                {
                    g.DrawLine(p, r.Left + 10, r.Top + 3, r.Right - 2, r.Bottom - 3);
                    g.DrawLine(p, r.Right - 2, r.Top + 3, r.Left + 10, r.Bottom - 3);
                }
                else
                {
                    g.DrawArc(p, r.Left + 7, r.Top + 2, 8, r.Height - 4, -50, 100);
                }
            }
        }

        private int FirstIndexOfGroup(TabGroup group)
        {
            for (int i = 0; i < _mgr.Tabs.Count; i++)
                if (_mgr.Tabs[i].GroupId == group.Id) return i;
            return -1;
        }

        private string Truncate(Graphics g, string text, int width, bool active)
        {
            if (string.IsNullOrEmpty(text)) return active ? "New tab" : "";
            return text;
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

        private int HitTab(Point p)
        {
            for (int i = 0; i < _rects.Count; i++)
                if (_rects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = HitTab(e.Location);
            if (idx != _hover) { _hover = idx; Invalidate(); }
            if (_dragIndex >= 0 && (Math.Abs(e.X - _dragStart.X) > 5 || _dragging))
            {
                _dragging = true;
                int target = HitTab(e.Location);
                if (target >= 0 && target != _dragIndex)
                {
                    _mgr.MoveTab(_dragIndex, target);
                    _dragIndex = target;
                }
            }
            if (_hover >= 0) { _previewTimer.Stop(); _previewTimer.Start(); }
            else HidePreview();
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                if (_newTabRect.Contains(e.Location))
                {
                    AppServices.MainWindow.OpenNewTab(AppServices.Settings.Model.General.HomeUrl, true);
                    return;
                }
                int idx = HitTab(e.Location);
                if (idx >= 0)
                {
                    if (_closeRects[idx].Contains(e.Location) && !_mgr.Tabs[idx].Pinned)
                    {
                        _mgr.CloseTab(_mgr.Tabs[idx]);
                        return;
                    }
                    if (_audioRects[idx].Contains(e.Location) &&
                        (_mgr.Tabs[idx].PlayingAudio || _mgr.Tabs[idx].Muted))
                    {
                        _mgr.ToggleMute(_mgr.Tabs[idx]);
                        return;
                    }
                    _dragIndex = idx;
                    _dragStart = e.Location;
                    _dragging = false;
                }
            }
            else if (e.Button == MouseButtons.Middle)
            {
                int idx = HitTab(e.Location);
                if (idx >= 0) _mgr.CloseTab(_mgr.Tabs[idx]);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _dragIndex >= 0 && !_dragging)
            {
                int idx = HitTab(e.Location);
                if (idx >= 0) _mgr.ActivateTab(_mgr.Tabs[idx]);
            }
            _dragIndex = -1;
            _dragging = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1;
            _previewTimer.Stop();
            HidePreview();
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (HitTab(e.Location) < 0 && !_newTabRect.Contains(e.Location))
                AppServices.MainWindow.OpenNewTab(AppServices.Settings.Model.General.HomeUrl, true);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _scroll = Math.Max(0, _scroll - e.Delta / 4);
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                int idx = HitTab(e.Location);
                if (idx >= 0) ShowContextMenu(_mgr.Tabs[idx]);
            }
            base.OnMouseClick(e);
        }

        private void ShowContextMenu(BrowserTab tab)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("New tab", null, delegate { AppServices.MainWindow.OpenNewTab(AppServices.Settings.Model.General.HomeUrl, true); });
            menu.Items.Add("Duplicate", null, delegate { AppServices.MainWindow.OpenNewTab(tab.Url, true); });
            menu.Items.Add(tab.Pinned ? "Unpin tab" : "Pin tab", null, delegate { _mgr.TogglePin(tab); });
            menu.Items.Add(tab.Muted ? "Unmute site" : "Mute site", null, delegate { _mgr.ToggleMute(tab); });
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem groupItem = new ToolStripMenuItem("Add to group");
            foreach (TabGroup g in _mgr.Groups)
            {
                ToolStripMenuItem gi = new ToolStripMenuItem(g.Name);
                gi.Click += delegate { _mgr.AddToGroup(tab, g); };
                groupItem.DropDownItems.Add(gi);
            }
            ToolStripMenuItem ng = new ToolStripMenuItem("New group...");
            ng.Click += delegate
            {
                TabGroup g = _mgr.CreateGroup("Group " + (_mgr.Groups.Count + 1),
                    GroupPalette[_mgr.Groups.Count % GroupPalette.Length], tab);
                RenameInline(g);
            };
            groupItem.DropDownItems.Add(ng);
            if (!string.IsNullOrEmpty(tab.GroupId))
            {
                ToolStripMenuItem rg = new ToolStripMenuItem("Remove from group");
                rg.Click += delegate { _mgr.AddToGroup(tab, null); };
                groupItem.DropDownItems.Add(rg);
            }
            menu.Items.Add(groupItem);

            menu.Items.Add("Copy URL", null, delegate
            {
                try { Clipboard.SetText(tab.Url); } catch { }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Close tabs to the right", null, delegate
            {
                int idx = _mgr.IndexOf(tab);
                foreach (BrowserTab t in _mgr.Tabs.Skip(idx + 1).Where(t => !t.Pinned).ToList())
                    _mgr.CloseTab(t);
            });
            menu.Items.Add("Close other tabs", null, delegate
            {
                foreach (BrowserTab t in _mgr.Tabs.Where(t => t != tab && !t.Pinned).ToList())
                    _mgr.CloseTab(t);
            });
            menu.Items.Add("Close tab", null, delegate { _mgr.CloseTab(tab); });
            menu.Show(this, PointToClient(Cursor.Position));
        }

        public static readonly Color[] GroupPalette =
        {
            Color.FromArgb(76, 194, 255), Color.FromArgb(124, 92, 255), Color.FromArgb(55, 214, 122),
            Color.FromArgb(255, 140, 97), Color.FromArgb(255, 209, 102), Color.FromArgb(255, 93, 93)
        };

        private void RenameInline(TabGroup g)
        {
            string name = PromptDialog.Ask("Tab group", "Group name", g.Name);
            if (!string.IsNullOrWhiteSpace(name)) _mgr.RenameGroup(g, name);
        }

        // ---- hover preview ----------------------------------------------------

        private async void ShowPreviewAsync()
        {
            if (_hover < 0 || _hover >= _mgr.Tabs.Count) return;
            BrowserTab tab = _mgr.Tabs[_hover];
            if (tab.View == null || tab.View.CoreWebView2 == null || tab.Frozen || tab.Discarded) return;
            int hover = _hover;
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    await tab.View.CoreWebView2.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Jpeg, ms).ConfigureAwait(true);
                    Image img = Image.FromStream(new MemoryStream(ms.ToArray()));
                    HidePreview();
                    _preview = new TabPreviewPopup(img, tab.Title, tab.Url);
                    if (hover < _rects.Count)
                    {
                        Rectangle r = _rects[hover];
                        Point screen = PointToScreen(new Point(r.Left, r.Bottom));
                        _preview.ShowAt(screen);
                    }
                }
            }
            catch
            {
                // Preview is best effort only.
            }
        }

        private void HidePreview()
        {
            if (_preview != null) { _preview.Close(); _preview.Dispose(); _preview = null; }
        }

        /// <summary>Small shared cache so favicons are decoded once per URL.</summary>
        internal static class ImageCache
        {
            private static readonly Dictionary<string, Image> Cache = new Dictionary<string, Image>();
            private static readonly object Lock = new object();

            public static Image Get(string uriOrUrl)
            {
                lock (Lock)
                {
                    Image img;
                    if (Cache.TryGetValue(uriOrUrl, out img)) return img;
                }
                try
                {
                    if (uriOrUrl.StartsWith("data:", StringComparison.Ordinal))
                    {
                        int comma = uriOrUrl.IndexOf(',');
                        byte[] bytes = Convert.FromBase64String(uriOrUrl.Substring(comma + 1));
                        Image decoded = Image.FromStream(new MemoryStream(bytes));
                        lock (Lock)
                        {
                            if (Cache.Count > 200) Cache.Clear();
                            Cache[uriOrUrl] = decoded;
                        }
                        return decoded;
                    }
                }
                catch { }
                return null;
            }
        }
    }

    /// <summary>Tiny modal text prompt used for group names and quick edits.</summary>
    public sealed class PromptDialog : Form
    {
        private readonly TextBox _box = new TextBox();

        private PromptDialog(string title, string label, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Width = 380;
            Height = 160;
            ShowInTaskbar = false;
            Label l = new Label { Text = label, Left = 14, Top = 14, AutoSize = true };
            _box.Text = initial ?? "";
            _box.Left = 14; _box.Top = 38; _box.Width = 336;
            Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 200, Top = 74, Width = 72 };
            Button cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 280, Top = 74, Width = 72 };
            Controls.AddRange(new Control[] { l, _box, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public static string Ask(string title, string label, string initial)
        {
            using (PromptDialog dlg = new PromptDialog(title, label, initial))
            {
                return dlg.ShowDialog() == DialogResult.OK ? dlg._box.Text.Trim() : "";
            }
        }
    }

    /// <summary>Borderless always-on-top thumbnail shown while hovering a tab.</summary>
    public sealed class TabPreviewPopup : Form
    {
        public TabPreviewPopup(Image image, string title, string url)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(24, 28, 34);
            Padding = new Padding(6, 6, 6, 26);
            Width = 300;
            Height = 210;
            PictureBox box = new PictureBox
            {
                Dock = DockStyle.Fill,
                Image = image,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            Label label = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 20,
                ForeColor = Color.FromArgb(200, 210, 220),
                Text = Trunc(title + "  -  " + url, 58),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            Controls.Add(box);
            Controls.Add(label);
        }

        private static string Trunc(string s, int n)
        {
            return s.Length <= n ? s : s.Substring(0, n - 1) + "...";
        }

        public void ShowAt(Point screen)
        {
            Location = screen;
            Show();
        }

        protected override bool ShowWithoutActivation { get { return true; } }
    }
}
