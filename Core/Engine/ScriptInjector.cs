using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Swifter.Config;

namespace Swifter.Engine
{
    /// <summary>
    /// Greasemonkey/Tampermonkey style user script engine: domain wildcard
    /// matching with document-start / document-end injection of custom JS and CSS.
    /// </summary>
    public sealed class ScriptInjector
    {
        private readonly SettingsManager _settings;
        private readonly Dictionary<string, Regex> _cache = new Dictionary<string, Regex>();

        public event Action Changed;

        public ScriptInjector(SettingsManager settings)
        {
            _settings = settings;
        }

        public List<UserScript> All()
        {
            return _settings.Model.UserScripts;
        }

        public UserScript Get(string id)
        {
            return _settings.Model.UserScripts.Find(s => s.Id == id);
        }

        public UserScript Create(string name, string match, string js, string css, string runAt)
        {
            UserScript s = new UserScript
            {
                Name = string.IsNullOrEmpty(name) ? "User script" : name,
                JsCode = js ?? "",
                CssCode = css ?? "",
                RunAt = runAt == "document-start" ? "document-start" : "document-end"
            };
            if (!string.IsNullOrWhiteSpace(match)) s.Matches.Add(match.Trim());
            _settings.Model.UserScripts.Add(s);
            _settings.SaveLater();
            Raise();
            return s;
        }

        public void Update(UserScript s)
        {
            _settings.SaveLater();
            Raise();
        }

        public void Remove(string id)
        {
            _settings.Model.UserScripts.RemoveAll(s => s.Id == id);
            _settings.SaveLater();
            Raise();
        }

        private void Raise()
        {
            _cache.Clear();
            Action h = Changed;
            if (h != null) h();
        }

        // ------------------------------------------------------------------
        // Matching
        // ------------------------------------------------------------------

        public bool Matches(UserScript script, string url)
        {
            if (script == null || !script.Enabled) return false;
            if (script.Matches.Count == 0) return false;
            foreach (string pattern in script.Matches)
                if (PatternMatches(pattern, url)) return true;
            return false;
        }

        public bool PatternMatches(string pattern, string url)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            Regex rx;
            if (!_cache.TryGetValue(pattern, out rx))
            {
                rx = Compile(pattern);
                _cache[pattern] = rx;
            }
            if (rx == null) return false;
            return rx.IsMatch(url ?? "");
        }

        private static Regex Compile(string pattern)
        {
            string p = pattern.Trim();
            bool hostOnly = p.IndexOf('/') < 0 && p.IndexOf("://") < 0;
            string scheme = "https?";
            if (p.IndexOf("://", StringComparison.Ordinal) > 0)
            {
                int idx = p.IndexOf("://", StringComparison.Ordinal);
                scheme = Regex.Escape(p.Substring(0, idx)).Replace(@"\*", "[a-z-]+");
                p = p.Substring(idx + 3);
            }
            string hostPart = p;
            string pathPart = "";
            int slash = p.IndexOf('/');
            if (slash >= 0)
            {
                hostPart = p.Substring(0, slash);
                pathPart = p.Substring(slash);
            }
            StringBuilder rx = new StringBuilder();
            rx.Append('^').Append(scheme).Append("://");
            rx.Append(Wildcard(hostPart, true));
            if (hostOnly) rx.Append("(?:/.*)?$");
            else
            {
                rx.Append(Wildcard(pathPart, false));
                rx.Append('$');
            }
            try { return new Regex(rx.ToString(), RegexOptions.IgnoreCase); }
            catch { return null; }
        }

        private static string Wildcard(string text, bool isHost)
        {
            StringBuilder sb = new StringBuilder();
            StringBuilder cur = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '*')
                {
                    sb.Append(Regex.Escape(cur.ToString())); cur.Clear();
                    sb.Append(isHost ? "(?:[^/]*?)" : "(?:.*?)");
                }
                else if (c == '?')
                {
                    sb.Append(Regex.Escape(cur.ToString())); cur.Clear();
                    sb.Append(isHost ? "[^/.]" : "[^/]");
                }
                else cur.Append(c);
            }
            sb.Append(Regex.Escape(cur.ToString()));
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Injection payloads
        // ------------------------------------------------------------------

        public string DocumentStartScript(string url)
        {
            StringBuilder sb = new StringBuilder();
            foreach (UserScript s in _settings.Model.UserScripts)
            {
                if (s.RunAt != "document-start" || !Matches(s, url)) continue;
                AppendScript(sb, s);
            }
            return sb.ToString();
        }

        public string DocumentEndScript(string url)
        {
            StringBuilder sb = new StringBuilder();
            foreach (UserScript s in _settings.Model.UserScripts)
            {
                if (s.RunAt != "document-end" || !Matches(s, url)) continue;
                AppendScript(sb, s);
            }
            return sb.ToString();
        }

        public string CssFor(string url)
        {
            StringBuilder css = new StringBuilder();
            foreach (UserScript s in _settings.Model.UserScripts)
            {
                if (!Matches(s, url) || string.IsNullOrWhiteSpace(s.CssCode)) continue;
                css.Append(s.CssCode).Append('\n');
            }
            return css.ToString();
        }

        private static void AppendScript(StringBuilder sb, UserScript s)
        {
            sb.Append("(function(){try{");
            if (!string.IsNullOrWhiteSpace(s.CssCode))
            {
                sb.Append("var st=document.createElement('style');st.setAttribute('data-swifter-script',")
                  .Append(JsonString(s.Id)).Append(");st.textContent=")
                  .Append(JsonString(s.CssCode)).Append(";(document.head||document.documentElement).appendChild(st);");
            }
            sb.Append(s.JsCode ?? "");
            sb.Append("}catch(e){console.warn('[Swifter] user script failed: ")
              .Append((s.Name ?? "").Replace("'", "")).Append("',e);}})();");
        }

        public static string JsonString(string value)
        {
            return Swifter.Protocols.ProtocolPageRenderer.JsonEncode(value ?? "");
        }
    }
}
