using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Swifter.Config;

namespace Swifter.Engine
{
    public sealed class ReaderArticle
    {
        public string Title = "";
        public string Byline = "";
        public string SiteName = "";
        public string Excerpt = "";
        public string Html = "";
        public int TextLength;
        public string SourceUrl = "";
    }

    /// <summary>
    /// Distraction free reading: a Readability-style extractor running inside the
    /// page plus a typography view with adjustable font, width and theme.
    /// </summary>
    public static class ReaderModeExtractor
    {
        public const string ExtractScript = """
            (function () {
              function bad(el) {
                var c = (el.className || '') + ' ' + (el.id || '');
                return /comment|social|share|related|promo|sidebar|newsletter|subscribe|advert|ad-|popup|cookie|footer|nav|breadcrumb|recommended|outbrain|taboola/i.test(c);
              }
              var clone = document.body ? document.body.cloneNode(true) : null;
              if (!clone) return JSON.stringify({ ok: false });
              var drop = clone.querySelectorAll('script,style,noscript,iframe,form,nav,footer,aside,header,button,svg,link,meta');
              for (var i = 0; i < drop.length; i++) drop[i].parentNode.removeChild(drop[i]);
              var nodes = clone.querySelectorAll('div,section,article,main,td,blockquote');
              var scores = new Map();
              var paras = clone.querySelectorAll('p, pre, li');
              for (var p = 0; p < paras.length; p++) {
                var para = paras[p];
                var text = (para.textContent || '').replace(/\s+/g, ' ').trim();
                if (text.length < 25) continue;
                var score = 1 + text.split(',').length - 1 + Math.min(text.length / 100, 3);
                var parent = para.parentNode;
                var gp = parent ? parent.parentNode : null;
                if (parent) scores.set(parent, (scores.get(parent) || 0) + score);
                if (gp) scores.set(gp, (scores.get(gp) || 0) + score / 2);
              }
              var best = null, bestScore = 0;
              scores.forEach(function (v, k) { if (v > bestScore) { bestScore = v; best = k; } });
              if (!best) {
                best = clone.querySelector('article') || clone.querySelector('main') || clone;
              }
              var clean = best.querySelectorAll('div,section,figure,aside');
              for (var c = 0; c < clean.length; c++) {
                var el = clean[c];
                if (bad(el)) { if (el.parentNode) el.parentNode.removeChild(el); continue; }
                var ps = el.querySelectorAll('p').length;
                if (ps === 0 && (el.textContent || '').trim().length < 80 && el.querySelectorAll('img').length === 0) {
                  if (el.parentNode) el.parentNode.removeChild(el);
                }
              }
              var imgs = best.querySelectorAll('img');
              for (var g = 0; g < imgs.length; g++) {
                var img = imgs[g];
                var src = img.getAttribute('src') || img.getAttribute('data-src') || '';
                if (src) { try { img.setAttribute('src', new URL(src, location.href).href); } catch (e) { } }
              }
              var links = best.querySelectorAll('a');
              for (var l = 0; l < links.length; l++) {
                var a = links[l];
                try { if (a.href) a.setAttribute('href', new URL(a.getAttribute('href'), location.href).href); } catch (e) { }
              }
              function meta(sel) {
                var m = document.querySelector(sel);
                return m ? (m.getAttribute('content') || m.textContent || '') : '';
              }
              var title = meta('meta[property="og:title"]') || meta('meta[name="twitter:title"]') ||
                          (document.querySelector('h1') ? document.querySelector('h1').textContent : '') ||
                          document.title || '';
              var bodyHtml = best.innerHTML || '';
              var plain = (best.textContent || '').replace(/\s+/g, ' ').trim();
              return JSON.stringify({
                ok: plain.length > 200,
                title: (title || document.title || '').trim(),
                byline: meta('meta[name="author"]') || meta('meta[property="article:author"]'),
                site: meta('meta[property="og:site_name"]') || location.hostname,
                excerpt: plain.substring(0, 300),
                html: bodyHtml,
                len: plain.length
              });
            })()
            """;

        public const string ReadableCheck = """
            (function () {
              var ps = document.querySelectorAll('p');
              var chars = 0;
              for (var i = 0; i < ps.length; i++) chars += (ps[i].textContent || '').length;
              return JSON.stringify({ readable: ps.length >= 3 && chars > 800 });
            })()
            """;

        public static async Task<ReaderArticle> ExtractAsync(CoreWebView2 core)
        {
            string raw = await core.ExecuteScriptAsync(ExtractScript).ConfigureAwait(true);
            ReaderArticle article = new ReaderArticle { SourceUrl = core.Source ?? "" };
            try
            {
                string text = raw;
                if (text.StartsWith("\"") && text.EndsWith("\""))
                    text = JsonSerializer.Deserialize<string>(text);
                using (JsonDocument doc = JsonDocument.Parse(text))
                {
                    JsonElement root = doc.RootElement;
                    bool ok = root.TryGetProperty("ok", out JsonElement o) && o.GetBoolean();
                    if (!ok) return null;
                    article.Title = Prop(root, "title");
                    article.Byline = Prop(root, "byline");
                    article.SiteName = Prop(root, "site");
                    article.Excerpt = Prop(root, "excerpt");
                    article.Html = Prop(root, "html");
                    int len;
                    article.TextLength = root.TryGetProperty("len", out JsonElement le) && le.TryGetInt32(out len) ? len : 0;
                }
            }
            catch (Exception ex)
            {
                Swifter.Log.Error("reader extract: " + ex.Message);
                return null;
            }
            return article;
        }

        private static string Prop(JsonElement root, string name)
        {
            JsonElement e;
            return root.TryGetProperty(name, out e) && e.ValueKind == JsonValueKind.String ? e.GetString() : "";
        }

        public static async Task<bool> IsReadableAsync(CoreWebView2 core)
        {
            try
            {
                string raw = await core.ExecuteScriptAsync(ReadableCheck).ConfigureAwait(true);
                using (JsonDocument doc = JsonDocument.Parse(raw))
                {
                    JsonElement e;
                    return doc.RootElement.TryGetProperty("readable", out e) && e.GetBoolean();
                }
            }
            catch { return false; }
        }

        public static string Render(ReaderArticle article, ReaderSettings cfg, string accent)
        {
            string theme = cfg.Theme ?? "sepia";
            StringBuilder sb = new StringBuilder();
            sb.Append(Swifter.Protocols.ProtocolPageRenderer.Doctype + "\n");
            sb.Append("<html><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<title>").Append(Esc(article.Title)).Append(" - Swifter Reader</title>");
            sb.Append("<style>").Append(TypographyCss).Append("</style>");
            sb.Append("</head><body data-theme=\"").Append(Esc(theme)).Append("\">");
            sb.Append("<div class=\"bar\">");
            sb.Append("<button id=\"exit\" title=\"Exit reader mode\">&#8592; Back</button>");
            sb.Append("<span class=\"brand\">Swifter Reader</span>");
            sb.Append("<span class=\"spacer\"></span>");
            sb.Append("<button data-act=\"font-\" title=\"Smaller text\">A-</button>");
            sb.Append("<button data-act=\"font+\" title=\"Larger text\">A+</button>");
            sb.Append("<button data-act=\"width-\" title=\"Narrower\">&#8596;</button>");
            sb.Append("<button data-act=\"width+\" title=\"Wider\">&#8644;</button>");
            sb.Append("<button data-theme-set=\"light\">&#9728;</button>");
            sb.Append("<button data-theme-set=\"sepia\">&#128210;</button>");
            sb.Append("<button data-theme-set=\"dark\">&#9790;</button>");
            sb.Append("</div>");
            sb.Append("<main class=\"doc\" id=\"doc\">");
            sb.Append("<h1>").Append(Esc(article.Title)).Append("</h1>");
            sb.Append("<div class=\"meta\">");
            if (!string.IsNullOrEmpty(article.Byline)) sb.Append("<span>").Append(Esc(article.Byline)).Append("</span>");
            if (!string.IsNullOrEmpty(article.SiteName)) sb.Append("<span class=\"site\">").Append(Esc(article.SiteName)).Append("</span>");
            sb.Append("<span class=\"words\">").Append(article.TextLength.ToString(CultureInfo.InvariantCulture)).Append(" chars</span>");
            sb.Append("</div>");
            sb.Append("<article>").Append(article.Html).Append("</article>");
            sb.Append("</main>");
            sb.Append("<script>").Append(ReaderJs
                .Replace("__FONT__", cfg.FontSize.ToString(CultureInfo.InvariantCulture))
                .Replace("__WIDTH__", cfg.ContentWidth.ToString(CultureInfo.InvariantCulture))
                .Replace("__LH__", cfg.LineHeight.ToString(CultureInfo.InvariantCulture))
                .Replace("__FAMILY__", Esc(cfg.FontFamily))
                .Replace("__ACCENT__", Esc(accent ?? "#4cc2ff"))).Append("</script>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        public const string TypographyCss = """
            :root { --fg:#1f2328; --bg:#f6f1e7; --muted:#7a736a; --card:#fffdf7; }
            body[data-theme="light"] { --fg:#1f2328; --bg:#ffffff; --muted:#6b7280; --card:#ffffff; }
            body[data-theme="dark"]  { --fg:#e6e1d8; --bg:#14161a; --muted:#8b929c; --card:#1b1e24; }
            * { box-sizing: border-box; }
            html, body { margin:0; padding:0; background:var(--bg); color:var(--fg);
              transition: background .25s ease, color .25s ease; }
            .bar { position:sticky; top:0; z-index:5; display:flex; align-items:center; gap:8px;
              padding:8px 14px; background:var(--card); border-bottom:1px solid rgba(128,128,128,.25);
              backdrop-filter: blur(10px); }
            .bar button { border:1px solid rgba(128,128,128,.35); background:transparent; color:var(--fg);
              border-radius:8px; padding:4px 10px; cursor:pointer; font-size:14px; }
            .bar button:hover { background:rgba(128,128,128,.15); }
            .brand { font-weight:600; letter-spacing:.4px; opacity:.8; font-size:13px; }
            .spacer { flex:1; }
            .doc { margin:0 auto; padding:48px 22px 120px; }
            .doc h1 { font-size:2.1em; line-height:1.2; margin:0 0 10px; }
            .meta { color:var(--muted); font-size:.85em; display:flex; gap:14px; flex-wrap:wrap; margin-bottom:26px; }
            article { line-height:var(--lh); font-family:var(--family); font-size:var(--fs); max-width:var(--w); margin:0 auto; }
            article p { margin:0 0 1.1em; }
            article img { max-width:100%; height:auto; border-radius:10px; }
            article h2, article h3 { line-height:1.25; margin:1.4em 0 .5em; }
            article blockquote { border-left:3px solid var(--muted); margin:1em 0; padding:.2em 1em; color:var(--muted); }
            article a { color:inherit; text-decoration:underline; text-decoration-color:var(--accent); }
            article pre { overflow:auto; background:rgba(128,128,128,.12); padding:12px; border-radius:8px; }
            article figure { margin:1em 0; }
            """;

        public const string ReaderJs = """
            (function () {
              var doc = document.documentElement;
              var st = document.createElement('style');
              st.textContent = ':root{--fs:__FONT__px;--w:__WIDTH__px;--lh:__LH__%;--family:"__FAMILY__",Georgia,serif;--accent:__ACCENT__;}';
              document.head.appendChild(st);
              var fs = __FONT__, w = __WIDTH__;
              function apply() {
                st.textContent = ':root{--fs:' + fs + 'px;--w:' + w + 'px;--lh:__LH__%;--family:"__FAMILY__",Georgia,serif;--accent:__ACCENT__;}';
              }
              document.querySelectorAll('[data-act]').forEach(function (b) {
                b.addEventListener('click', function () {
                  var act = b.getAttribute('data-act');
                  if (act === 'font+') fs = Math.min(32, fs + 1);
                  if (act === 'font-') fs = Math.max(13, fs - 1);
                  if (act === 'width+') w = Math.min(1100, w + 60);
                  if (act === 'width-') w = Math.max(480, w - 60);
                  apply();
                  try {
                    window.chrome.webview.postMessage(JSON.stringify({
                      t: 'req', id: 0, action: 'reader.configure',
                      payload: { fontSize: fs, width: w }
                    }));
                  } catch (e) { }
                });
              });
              document.querySelectorAll('[data-theme-set]').forEach(function (b) {
                b.addEventListener('click', function () {
                  document.body.setAttribute('data-theme', b.getAttribute('data-theme-set'));
                  try {
                    window.chrome.webview.postMessage(JSON.stringify({
                      t: 'req', id: 0, action: 'reader.configure',
                      payload: { theme: b.getAttribute('data-theme-set') }
                    }));
                  } catch (e) { }
                });
              });
              document.getElementById('exit').addEventListener('click', function () {
                try {
                  window.chrome.webview.postMessage(JSON.stringify({
                    t: 'req', id: 0, action: 'reader.exit', payload: {}
                  }));
                } catch (e) { history.back(); }
              });
            })();
            """;
    }
}
