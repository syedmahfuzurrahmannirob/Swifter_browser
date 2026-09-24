using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Swifter.Config;

namespace Swifter.Protocols
{
    /// <summary>
    /// Shared chrome for every swifter:// page: CSS design tokens (dark + light),
    /// the JS host bridge (window.Swifter.call / .on), logo data URIs, SVG chart
    /// helpers and JSON/HTML encoders used by all internal pages.
    /// </summary>
    public static class ProtocolPageRenderer
    {
        public const string Doctype = "<!DOCTYPE html>";

        // ------------------------------------------------------------------
        // Encoding helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Emits a JSON string literal safe for inlining inside &lt;script&gt; blocks
        /// (escapes quotes, control chars and the &lt;/script&gt; terminator).
        /// </summary>
        public static string JsonEncode(string value)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '<': sb.Append("\\u003C"); break;
                    case '>': sb.Append("\\u003E"); break;
                    case '&': sb.Append("\\u0026"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string ToJson(object value)
        {
            string json = JsonSerializer.Serialize(value, SettingsManager.JsonOptions);
            return json.Replace("<", "\\u003C").Replace(">", "\\u003E").Replace("&", "\\u0026");
        }

        public static string Esc(string value)
        {
            return (value ?? "")
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        // ------------------------------------------------------------------
        // Design tokens
        // ------------------------------------------------------------------

        public static string BaseCss(bool dark, string accent, double fontScale)
        {
            string bg, panel, card, text, muted, border, hover, input, shadow;
            if (dark)
            {
                bg = "#0f1216"; panel = "#161b22"; card = "#1c232c"; text = "#e8edf2";
                muted = "#98a4b3"; border = "#2a3440"; hover = "#232c37"; input = "#10151b";
                shadow = "0 10px 30px rgba(0,0,0,.45)";
            }
            else
            {
                bg = "#f3f5f8"; panel = "#ffffff"; card = "#ffffff"; text = "#1b2430";
                muted = "#5c6b7a"; border = "#d8dfe7"; hover = "#eef2f6"; input = "#ffffff";
                shadow = "0 8px 24px rgba(20,30,45,.12)";
            }
            string scale = fontScale.ToString("0.##", CultureInfo.InvariantCulture);
            return BaseCssRaw
                .Replace("__BG__", bg).Replace("__PANEL__", panel).Replace("__CARD__", card)
                .Replace("__TEXT__", text).Replace("__MUTED__", muted).Replace("__BORDER__", border)
                .Replace("__HOVER__", hover).Replace("__INPUT__", input).Replace("__SHADOW__", shadow)
                .Replace("__ACCENT__", accent ?? "#4cc2ff").Replace("__SCALE__", scale);
        }

        public const string BaseCssRaw = """
            :root {
              --bg: __BG__; --panel: __PANEL__; --card: __CARD__; --text: __TEXT__;
              --muted: __MUTED__; --border: __BORDER__; --hover: __HOVER__; --input: __INPUT__;
              --accent: __ACCENT__; --accent-soft: color-mix(in srgb, var(--accent) 18%, transparent);
              --danger: #ff5d5d; --ok: #37d67a; --warn: #ffb454;
              --radius: 12px; --shadow: __SHADOW__;
            }
            * { box-sizing: border-box; }
            html, body { margin:0; padding:0; height:100%; }
            body {
              background: var(--bg); color: var(--text);
              font: calc(14px * __SCALE__) / 1.5 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
              -webkit-font-smoothing: antialiased;
            }
            ::-webkit-scrollbar { width: 11px; height: 11px; }
            ::-webkit-scrollbar-thumb { background: var(--border); border-radius: 8px; border: 3px solid var(--bg); }
            ::-webkit-scrollbar-thumb:hover { background: var(--muted); }
            ::-webkit-scrollbar-track { background: transparent; }
            a { color: var(--accent); text-decoration: none; }
            a:hover { text-decoration: underline; }
            .wrap { max-width: 1180px; margin: 0 auto; padding: 26px 28px 80px; }
            header.page { display:flex; align-items:center; gap:14px; margin-bottom: 22px; }
            header.page img.logo { width: 34px; height: 34px; border-radius: 9px; }
            header.page h1 { font-size: 1.5em; margin: 0; font-weight: 650; letter-spacing:.2px; }
            header.page .sub { color: var(--muted); font-size: .9em; }
            .card { background: var(--card); border: 1px solid var(--border); border-radius: var(--radius);
                    padding: 18px 20px; margin-bottom: 18px; box-shadow: var(--shadow); }
            .card h2 { margin: 0 0 12px; font-size: 1.06em; font-weight: 620; }
            .row { display:flex; gap:12px; align-items:center; flex-wrap:wrap; }
            .row.between { justify-content: space-between; }
            .grow { flex: 1; }
            .muted { color: var(--muted); }
            .small { font-size: .85em; }
            .grid { display:grid; gap: 14px; }
            button, .btn {
              background: var(--hover); color: var(--text); border: 1px solid var(--border);
              border-radius: 9px; padding: 7px 13px; font: inherit; cursor: pointer;
              transition: background .15s ease, border-color .15s ease, transform .05s ease;
            }
            button:hover, .btn:hover { border-color: var(--accent); }
            button:active, .btn:active { transform: scale(.98); }
            button.primary { background: var(--accent); border-color: var(--accent); color: #06131c; font-weight: 600; }
            button.danger { background: transparent; border-color: var(--danger); color: var(--danger); }
            button.icon { padding: 6px 9px; line-height: 1; }
            button.ghost { background: transparent; border-color: transparent; }
            button.ghost:hover { background: var(--hover); }
            input[type=text], input[type=url], input[type=search], input[type=date], input[type=number],
            select, textarea {
              background: var(--input); color: var(--text); border: 1px solid var(--border);
              border-radius: 9px; padding: 8px 11px; font: inherit; outline: none;
            }
            input:focus, select:focus, textarea:focus { border-color: var(--accent); box-shadow: 0 0 0 3px var(--accent-soft); }
            textarea { width: 100%; min-height: 120px; resize: vertical; font-family: Consolas, monospace; }
            table { width: 100%; border-collapse: collapse; }
            th, td { text-align: left; padding: 9px 10px; border-bottom: 1px solid var(--border); vertical-align: top; }
            th { color: var(--muted); font-weight: 600; font-size: .88em; text-transform: uppercase; letter-spacing: .4px; }
            tr:hover td { background: var(--hover); }
            .chip { display:inline-flex; align-items:center; gap:6px; background: var(--hover);
                    border:1px solid var(--border); border-radius: 999px; padding: 3px 11px; font-size:.85em; }
            .chip.active { background: var(--accent); border-color: var(--accent); color:#06131c; font-weight:600; }
            .pill { display:inline-block; border-radius:999px; padding:2px 9px; font-size:.8em;
                    background: var(--accent-soft); color: var(--accent); font-weight:600; }
            .stat { background: var(--card); border:1px solid var(--border); border-radius: var(--radius);
                    padding: 14px 18px; min-width: 130px; }
            .stat .v { font-size: 1.7em; font-weight: 700; }
            .stat .k { color: var(--muted); font-size: .82em; text-transform: uppercase; letter-spacing:.5px; }
            .bar-track { background: var(--hover); border-radius: 999px; height: 8px; overflow:hidden; }
            .bar-fill { height:100%; background: var(--accent); border-radius:999px; transition: width .3s ease; }
            .empty { text-align:center; color: var(--muted); padding: 42px 0; }
            .empty .big { font-size: 2.4em; margin-bottom: 8px; opacity:.6; }
            dialog, .modal { background: var(--card); color: var(--text); border:1px solid var(--border);
                             border-radius: 14px; box-shadow: var(--shadow); padding: 20px; max-width: 560px; width: 92%; }
            dialog::backdrop { background: rgba(0,0,0,.5); backdrop-filter: blur(3px); }
            .toast-wrap { position: fixed; right: 18px; bottom: 18px; display:flex; flex-direction:column; gap:8px; z-index: 99; }
            .toast { background: var(--card); border:1px solid var(--border); border-left: 4px solid var(--accent);
                     border-radius: 10px; padding: 10px 16px; box-shadow: var(--shadow); animation: slidein .2s ease; }
            .toast.err { border-left-color: var(--danger); }
            @keyframes slidein { from { transform: translateX(20px); opacity:0 } to { transform:none; opacity:1 } }
            .seg { display:flex; border:1px solid var(--border); border-radius: 10px; overflow:hidden; }
            .seg > div { height: 14px; background: var(--hover); flex:1; border-right:1px solid var(--border); position:relative; }
            .seg > div:last-child { border-right: none; }
            .seg > div > i { position:absolute; inset:0; background: var(--accent); transform-origin:left; }
            .seg > div.done > i { background: var(--ok); }
            .seg > div.err > i { background: var(--danger); }
            .tabsbar { display:flex; gap:6px; border-bottom:1px solid var(--border); margin-bottom:18px; flex-wrap:wrap; }
            .tabsbar button { border:none; border-bottom:2px solid transparent; border-radius:0; background:transparent; }
            .tabsbar button.active { border-bottom-color: var(--accent); color: var(--accent); font-weight:600; }
            .kbd { background: var(--hover); border:1px solid var(--border); border-bottom-width:2px; border-radius:6px;
                   padding:1px 6px; font-family: Consolas, monospace; font-size:.85em; }
            .hidden { display:none !important; }
            """;

        // ------------------------------------------------------------------
        // Host bridge JS
        // ------------------------------------------------------------------

        public const string BaseJs = """
            window.Swifter = (function () {
              var pending = {}, seq = 1, handlers = {};
              function send(msg) {
                try { window.chrome.webview.postMessage(JSON.stringify(msg)); } catch (e) { }
              }
              function call(action, payload) {
                return new Promise(function (resolve, reject) {
                  var id = seq++;
                  pending[id] = { resolve: resolve, reject: reject };
                  send({ t: 'req', id: id, action: action, payload: payload || {} });
                  setTimeout(function () {
                    if (pending[id]) { delete pending[id]; reject(new Error('timeout: ' + action)); }
                  }, 20000);
                });
              }
              function on(name, fn) { (handlers[name] = handlers[name] || []).push(fn); }
              function emit(name, data) {
                (handlers[name] || []).forEach(function (fn) { try { fn(data); } catch (e) { console.error(e); } });
              }
              if (window.chrome && window.chrome.webview) {
                window.chrome.webview.addEventListener('message', function (ev) {
                  var m = ev.data;
                  if (!m || typeof m !== 'object') return;
                  if (m.t === 'res') {
                    var p = pending[m.id];
                    if (!p) return;
                    delete pending[m.id];
                    if (m.ok) p.resolve(m.data); else p.reject(new Error(m.error || 'error'));
                  } else if (m.t === 'evt') {
                    emit(m.name, m.data);
                  }
                });
              }
              function esc(s) {
                return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
                  return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
                });
              }
              function fmtBytes(n) {
                if (n == null || n < 0) return '--';
                var u = ['B', 'KB', 'MB', 'GB', 'TB'], i = 0, v = n;
                while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
                return (v >= 100 || i === 0 ? v.toFixed(0) : v.toFixed(1)) + ' ' + u[i];
              }
              function fmtSpeed(n) { return n > 0 ? fmtBytes(n) + '/s' : '--'; }
              function fmtEta(s) {
                if (s == null || s < 0 || !isFinite(s)) return '--:--';
                var m = Math.floor(s / 60), sec = Math.floor(s % 60), h = Math.floor(m / 60);
                if (h > 0) return h + ':' + String(m % 60).padStart(2, '0') + ':' + String(sec).padStart(2, '0');
                return String(m).padStart(2, '0') + ':' + String(sec).padStart(2, '0');
              }
              function fmtDate(ms) {
                var d = new Date(ms);
                return d.toLocaleDateString() + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
              }
              function toast(msg, isErr) {
                var wrap = document.querySelector('.toast-wrap');
                if (!wrap) { wrap = document.createElement('div'); wrap.className = 'toast-wrap'; document.body.appendChild(wrap); }
                var el = document.createElement('div');
                el.className = 'toast' + (isErr ? ' err' : '');
                el.textContent = msg;
                wrap.appendChild(el);
                setTimeout(function () { el.style.opacity = '0'; setTimeout(function () { el.remove(); }, 250); }, 3200);
              }
              function debounce(fn, ms) {
                var t; return function () { var a = arguments, c = this; clearTimeout(t); t = setTimeout(function () { fn.apply(c, a); }, ms); };
              }
              return { call: call, on: on, emit: emit, send: send, esc: esc, fmtBytes: fmtBytes,
                       fmtSpeed: fmtSpeed, fmtEta: fmtEta, fmtDate: fmtDate, toast: toast, debounce: debounce };
            })();
            """;

        // ------------------------------------------------------------------
        // Page wrapper
        // ------------------------------------------------------------------

        public static string Page(string title, string bodyHtml, string initJs, string extraCss,
            SettingsManager settings)
        {
            bool dark = settings.IsDarkTheme();
            string accent = SettingsManager.ToHex(settings.Accent());
            StringBuilder sb = new StringBuilder();
            sb.Append(Doctype).Append("\n<html><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"color-scheme\" content=\"").Append(dark ? "dark light" : "light dark").Append("\">");
            sb.Append("<title>").Append(Esc(title)).Append(" - Swifter</title>");
            sb.Append("<link rel=\"icon\" href=\"").Append(LogoDataUri(64)).Append("\">");
            sb.Append("<style>").Append(BaseCss(dark, accent, settings.Model.Appearance.FontScale)).Append("</style>");
            if (!string.IsNullOrEmpty(extraCss)) sb.Append("<style>").Append(extraCss).Append("</style>");
            sb.Append("</head><body>");
            sb.Append(bodyHtml);
            sb.Append("<script>").Append(BaseJs).Append("\n").Append(initJs ?? "").Append("</script>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        public static string PageHeader(string logoAlt, string title, string subtitle)
        {
            return "<header class=\"page\"><img class=\"logo\" src=\"" + LogoDataUri(64) + "\" alt=\"" +
                   Esc(logoAlt) + "\"><div><h1>" + Esc(title) + "</h1><div class=\"sub\">" +
                   Esc(subtitle) + "</div></div></header>";
        }

        // ------------------------------------------------------------------
        // Logo / favicon helpers
        // ------------------------------------------------------------------

        private static readonly Dictionary<int, string> LogoCache = new Dictionary<int, string>();

        public static string LogoDataUri(int size)
        {
            lock (LogoCache)
            {
                string cached;
                if (LogoCache.TryGetValue(size, out cached)) return cached;
            }
            string uri = BuildLogoUri(size);
            lock (LogoCache) LogoCache[size] = uri;
            return uri;
        }

        private static string BuildLogoUri(int size)
        {
            try
            {
                Assembly asm = typeof(ProtocolPageRenderer).Assembly;
                using (Stream s = asm.GetManifestResourceStream("Swifter.Assets.logo.png"))
                {
                    if (s == null) return "";
                    using (Image src = Image.FromStream(s))
                    using (Bitmap bmp = new Bitmap(size, size))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            g.DrawImage(src, 0, 0, size, size);
                        }
                        using (MemoryStream ms = new MemoryStream())
                        {
                            bmp.Save(ms, ImageFormat.Png);
                            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Swifter.Log.Error("logo render: " + ex.Message);
                return "";
            }
        }

        /// <summary>Letter-avatar favicon used when a site has no icon yet.</summary>
        public static string LetterIcon(string label, string host)
        {
            string text = "";
            foreach (char c in (label ?? ""))
            {
                if (char.IsLetterOrDigit(c)) { text = c.ToString().ToUpperInvariant(); break; }
            }
            if (text.Length == 0) text = (host ?? "?").Substring(0, 1).ToUpperInvariant();
            int hue = 0;
            foreach (char c in host ?? "") hue = (hue * 31 + c) & 0x1FF;
            string color = "hsl(" + (hue % 360).ToString(CultureInfo.InvariantCulture) + ",62%,52%)";
            string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='32' height='32'>" +
                         "<rect width='32' height='32' rx='8' fill='" + color + "'/>" +
                         "<text x='16' y='22' font-family='Segoe UI,sans-serif' font-size='17' font-weight='700' " +
                         "fill='white' text-anchor='middle'>" + Esc(text) + "</text></svg>";
            return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        }

        // ------------------------------------------------------------------
        // SVG charts
        // ------------------------------------------------------------------

        public static string BarChartSvg(List<KeyValuePair<string, int>> points, int width, int height,
            string accent, string muted)
        {
            if (points == null || points.Count == 0)
                return "<svg width=\"" + width + "\" height=\"" + height + "\"></svg>";
            int max = 1;
            foreach (KeyValuePair<string, int> p in points) if (p.Value > max) max = p.Value;
            double slot = (double)width / points.Count;
            double barW = Math.Max(2, slot * 0.62);
            StringBuilder sb = new StringBuilder();
            sb.Append("<svg viewBox=\"0 0 ").Append(width).Append(' ').Append(height)
              .Append("\" width=\"100%\" height=\"").Append(height).Append("\" preserveAspectRatio=\"none\">");
            sb.Append("<line x1=\"0\" y1=\"").Append(height - 1).Append("\" x2=\"").Append(width)
              .Append("\" y2=\"").Append(height - 1).Append("\" stroke=\"").Append(muted)
              .Append("\" stroke-opacity=\".35\"/>");
            for (int i = 0; i < points.Count; i++)
            {
                double h = points[i].Value == 0 ? 0 : Math.Max(2, (double)points[i].Value / max * (height - 8));
                double x = i * slot + (slot - barW) / 2;
                double y = height - 2 - h;
                sb.Append("<rect x=\"").Append(x.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append("\" y=\"").Append(y.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append("\" width=\"").Append(barW.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append("\" height=\"").Append(h.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append("\" rx=\"2\" fill=\"").Append(accent).Append("\"><title>")
                  .Append(Esc(points[i].Key)).Append(": ").Append(points[i].Value)
                  .Append("</title></rect>");
            }
            sb.Append("</svg>");
            return sb.ToString();
        }

        public static string LineChartSvg(List<KeyValuePair<string, int>> points, int width, int height,
            string accent)
        {
            if (points == null || points.Count == 0) return "";
            int max = 1;
            foreach (KeyValuePair<string, int> p in points) if (p.Value > max) max = p.Value;
            StringBuilder sb = new StringBuilder();
            sb.Append("<svg viewBox=\"0 0 ").Append(width).Append(' ').Append(height)
              .Append("\" width=\"100%\" height=\"").Append(height).Append("\" preserveAspectRatio=\"none\">");
            StringBuilder path = new StringBuilder();
            StringBuilder area = new StringBuilder();
            for (int i = 0; i < points.Count; i++)
            {
                double x = points.Count == 1 ? width / 2.0 : (double)i / (points.Count - 1) * width;
                double y = height - 4 - (double)points[i].Value / max * (height - 12);
                string cmd = (i == 0 ? "M" : "L") + x.ToString("0.#", CultureInfo.InvariantCulture) + " " +
                             y.ToString("0.#", CultureInfo.InvariantCulture);
                path.Append(cmd).Append(' ');
                if (i == 0) area.Append("M0 ").Append(height).Append(' ').Append(cmd).Append(' ');
                else area.Append(cmd).Append(' ');
            }
            area.Append("L").Append(width).Append(' ').Append(height).Append(" Z");
            sb.Append("<path d=\"").Append(area).Append("\" fill=\"").Append(accent).Append("\" opacity=\".14\"/>");
            sb.Append("<path d=\"").Append(path).Append("\" fill=\"none\" stroke=\"").Append(accent)
              .Append("\" stroke-width=\"2\" stroke-linejoin=\"round\"/>");
            sb.Append("</svg>");
            return sb.ToString();
        }
    }
}
