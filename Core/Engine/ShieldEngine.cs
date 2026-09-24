using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Swifter.Config;

namespace Swifter.Engine
{
    public enum BlockCategory { Ad, Tracker, Both, None }

    public sealed class BlockDecision
    {
        public bool Blocked;
        public string Reason = "";
        public string Rule = "";
        public BlockCategory Category = BlockCategory.None;
    }

    public sealed class ShieldStats
    {
        public long AdsBlocked { get; set; }
        public long TrackersBlocked { get; set; }
        public long CookiesBlocked { get; set; }
        public long BytesSaved { get; set; }
        public double SecondsSaved { get; set; }
        public long SinceUnixMs { get; set; }
        public Dictionary<string, long> PerHost { get; set; } = new Dictionary<string, long>();
    }

    public sealed class FilterRule
    {
        public string Raw = "";
        public bool IsException;
        public bool Important;
        public bool ThirdPartyOnly;
        public bool FirstPartyOnly;
        public Regex Pattern;
        public List<string> DomainOnly = new List<string>();
        public List<string> DomainExcluded = new List<string>();
        public HashSet<string> Types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TypesExcluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool IsHostRule;
        public string Host = "";
    }

    /// <summary>
    /// Ad / tracker blocking engine: uBlock &amp; EasyList style rule matching
    /// (domain anchors, separators, wildcards, options), hosts-file rules,
    /// per-site overrides, live statistics, DNS-over-HTTPS resolution and the
    /// browser-side fingerprint / WebRTC leak protection scripts.
    /// </summary>
    public sealed class ShieldEngine
    {
        private readonly object _lock = new object();
        private readonly List<FilterRule> _rules = new List<FilterRule>();
        private readonly SettingsManager _settings;
        private ShieldStats _stats = new ShieldStats();
        private readonly DohClient _doh = new DohClient();
        private readonly HashSet<string> _httpsUpgradeFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public event Action StatsChanged;

        public ShieldEngine(SettingsManager settings)
        {
            _settings = settings;
            _stats.SinceUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LoadStats();
            LoadRules();
        }

        // ------------------------------------------------------------------
        // Rule loading
        // ------------------------------------------------------------------

        private void LoadRules()
        {
            lock (_lock)
            {
                _rules.Clear();
                if (_settings.Model.Shields.LoadBundledLists)
                    foreach (string line in BundledList.Split('\n')) AddRuleCore(line);
                foreach (string line in _settings.Model.Shields.CustomRules) AddRuleCore(line);
                try
                {
                    if (File.Exists(AppPaths.RulesFile))
                        foreach (string line in File.ReadAllLines(AppPaths.RulesFile)) AddRuleCore(line);
                }
                catch { }
            }
        }

        public void ReloadRules()
        {
            LoadRules();
        }

        public bool AddRule(string raw)
        {
            FilterRule rule = Parse(raw);
            if (rule == null) return false;
            lock (_lock) _rules.Add(rule);
            if (!_settings.Model.Shields.CustomRules.Contains(raw))
            {
                _settings.Model.Shields.CustomRules.Add(raw);
                _settings.SaveLater();
            }
            return true;
        }

        public bool RemoveRule(string raw)
        {
            lock (_lock) _rules.RemoveAll(r => r.Raw == raw);
            bool removed = _settings.Model.Shields.CustomRules.Remove(raw);
            _settings.SaveLater();
            return removed;
        }

        public List<string> Rules()
        {
            return new List<string>(_settings.Model.Shields.CustomRules);
        }

        private void AddRuleCore(string raw)
        {
            FilterRule rule = Parse(raw);
            if (rule != null) _rules.Add(rule);
        }

        public static FilterRule Parse(string rawLine)
        {
            if (rawLine == null) return null;
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("!") || line.StartsWith("[") || line.StartsWith("#")) return null;

            FilterRule rule = new FilterRule { Raw = line };

            // hosts-file style: 127.0.0.1 badhost.com / 0.0.0.0 badhost.com
            Match hosts = Regex.Match(line, @"^(?:127\.0\.0\.1|0\.0\.0\.0)\s+([a-z0-9.\-]+)$", RegexOptions.IgnoreCase);
            if (hosts.Success)
            {
                rule.IsHostRule = true;
                rule.Host = hosts.Groups[1].Value.ToLowerInvariant();
                if (rule.Host == "localhost" || rule.Host == "local") return null;
                return rule;
            }

            string body = line;
            if (body.StartsWith("@@")) { rule.IsException = true; body = body.Substring(2); }

            // Options section (skip when the rule is a /regex/ that contains $).
            int dollar = -1;
            if (body.StartsWith("/"))
            {
                int close = body.LastIndexOf('/');
                if (close > 0) dollar = body.IndexOf('$', close);
            }
            else dollar = body.IndexOf('$');
            string options = "";
            if (dollar >= 0)
            {
                options = body.Substring(dollar + 1);
                body = body.Substring(0, dollar);
            }

            foreach (string opt in options.Split(','))
            {
                string o = opt.Trim();
                if (o.Length == 0) continue;
                if (o == "important") rule.Important = true;
                else if (o == "third-party" || o == "3p") rule.ThirdPartyOnly = true;
                else if (o == "~third-party" || o == "1p" || o == "first-party") rule.FirstPartyOnly = true;
                else if (o.StartsWith("domain=", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string d in o.Substring(7).Split('|'))
                    {
                        if (d.StartsWith("~")) rule.DomainExcluded.Add(d.Substring(1).ToLowerInvariant());
                        else rule.DomainOnly.Add(d.ToLowerInvariant());
                    }
                }
                else if (o.StartsWith("~")) rule.TypesExcluded.Add(o.Substring(1));
                else if (o == "match-case" || o == "badfilter" || o == "popunder" || o == "popup") { }
                else rule.Types.Add(o);
            }

            bool leftDomain = false, leftAnchor = false, rightAnchor = false;
            if (body.StartsWith("||")) { leftDomain = true; body = body.Substring(2); }
            else if (body.StartsWith("|")) { leftAnchor = true; body = body.Substring(1); }
            if (body.EndsWith("|")) { rightAnchor = true; body = body.Substring(0, body.Length - 1); }

            if (body.Length >= 2 && body.StartsWith("/") && body.EndsWith("/"))
            {
                try { rule.Pattern = new Regex(body.Substring(1, body.Length - 2), RegexOptions.IgnoreCase); }
                catch { return null; }
                return rule;
            }

            StringBuilder rx = new StringBuilder();
            if (leftDomain) rx.Append(@"^[a-zA-Z][a-zA-Z0-9+\-.]*://(?:[^/?#:.]+\.)?");
            else if (leftAnchor) rx.Append('^');
            StringBuilder cur = new StringBuilder();
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '*')
                {
                    rx.Append(Regex.Escape(cur.ToString())); cur.Clear();
                    rx.Append(".*");
                }
                else if (c == '^')
                {
                    rx.Append(Regex.Escape(cur.ToString())); cur.Clear();
                    rx.Append("(?:[^a-zA-Z0-9_\\-.%]|$)");
                }
                else cur.Append(c);
            }
            rx.Append(Regex.Escape(cur.ToString()));
            if (rightAnchor) rx.Append('$');
            try { rule.Pattern = new Regex(rx.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled); }
            catch { return null; }
            return rule;
        }

        // ------------------------------------------------------------------
        // Matching
        // ------------------------------------------------------------------

        public static string RegistrableDomain(string host)
        {
            if (string.IsNullOrEmpty(host)) return "";
            host = host.ToLowerInvariant();
            string[] twoLevel = { "co.uk", "org.uk", "ac.uk", "com.au", "co.nz", "com.br", "co.jp", "com.tr" };
            foreach (string t in twoLevel)
                if (host.EndsWith("." + t, StringComparison.Ordinal))
                {
                    int idx = host.LastIndexOf('.', host.Length - t.Length - 2);
                    return idx >= 0 ? host.Substring(idx + 1) : host;
                }
            int dot = host.LastIndexOf('.');
            if (dot <= 0) return host;
            int dot2 = host.LastIndexOf('.', dot - 1);
            return dot2 >= 0 ? host.Substring(dot2 + 1) : host;
        }

        public bool SiteShieldsDisabled(string documentHost)
        {
            bool v;
            if (_settings.Model.Shields.SiteOverrides.TryGetValue(documentHost ?? "", out v)) return v;
            return false;
        }

        public void SetSiteOverride(string documentHost, bool disabled)
        {
            _settings.Model.Shields.SiteOverrides[documentHost ?? ""] = disabled;
            _settings.SaveLater();
        }

        public BlockDecision Evaluate(string url, string documentHost, string resourceType)
        {
            BlockDecision decision = new BlockDecision();
            if (!_settings.Model.Shields.Enabled) return decision;
            if (SiteShieldsDisabled(documentHost)) return decision;
            if (string.IsNullOrEmpty(url) || url.StartsWith("swifter:", StringComparison.OrdinalIgnoreCase)) return decision;

            string host = "";
            try { host = new Uri(url).Host.ToLowerInvariant(); }
            catch { return decision; }
            if (host.Length == 0) return decision;

            string docDomain = RegistrableDomain(documentHost);
            string reqDomain = RegistrableDomain(host);
            bool thirdParty = docDomain.Length > 0 && reqDomain.Length > 0 && docDomain != reqDomain;
            string type = string.IsNullOrEmpty(resourceType) ? "other" : resourceType.ToLowerInvariant();

            List<FilterRule> snapshot;
            lock (_lock) snapshot = new List<FilterRule>(_rules);

            FilterRule matched = null;
            foreach (FilterRule r in snapshot)
            {
                if (r.IsException) continue;
                if (!AppliesTo(r, documentHost, thirdParty, type)) continue;
                if (Matches(r, url, host)) { matched = r; break; }
            }
            if (matched == null) return decision;

            foreach (FilterRule r in snapshot)
            {
                if (!r.IsException) continue;
                if (!AppliesTo(r, documentHost, thirdParty, type)) continue;
                if (Matches(r, url, host))
                {
                    if (!matched.Important) return decision;   // exception wins
                }
            }

            decision.Blocked = true;
            decision.Rule = matched.Raw;
            decision.Category = Classify(matched, host);
            decision.Reason = matched.IsHostRule ? "hosts rule" : "filter rule";
            return decision;
        }

        private static bool AppliesTo(FilterRule r, string documentHost, bool thirdParty, string type)
        {
            if (r.ThirdPartyOnly && !thirdParty) return false;
            if (r.FirstPartyOnly && thirdParty) return false;
            string doc = (documentHost ?? "").ToLowerInvariant();
            if (r.DomainExcluded.Count > 0)
            {
                foreach (string d in r.DomainExcluded)
                    if (doc == d || doc.EndsWith("." + d, StringComparison.Ordinal)) return false;
            }
            if (r.DomainOnly.Count > 0)
            {
                bool ok = false;
                foreach (string d in r.DomainOnly)
                    if (doc == d || doc.EndsWith("." + d, StringComparison.Ordinal)) { ok = true; break; }
                if (!ok) return false;
            }
            if (r.Types.Count > 0 && !r.Types.Contains(type)) return false;
            if (r.TypesExcluded.Contains(type)) return false;
            return true;
        }

        private static bool Matches(FilterRule r, string url, string host)
        {
            if (r.IsHostRule) return host == r.Host || host.EndsWith("." + r.Host, StringComparison.Ordinal);
            return r.Pattern != null && r.Pattern.IsMatch(url);
        }

        private static BlockCategory Classify(FilterRule rule, string host)
        {
            string h = host ?? "";
            bool trackerish =
                h.Contains("analytics") || h.Contains("doubleclick") || h.Contains("googleads") ||
                h.Contains("facebook") || h.Contains("scorecard") || h.Contains("hotjar") ||
                h.Contains("segment") || h.Contains("amplitude") || h.Contains("mixpanel") ||
                h.Contains("tracker") || h.Contains("telemetry") || h.Contains("metric") ||
                h.Contains("omtrdc") || h.Contains("adobedtm") || h.Contains("krxd") || h.Contains("bluekai");
            bool adish = h.Contains("ads") || h.Contains("advert") || h.Contains("banner") ||
                         h.Contains("sponsor") || h.Contains("popunder") || h.Contains("popup") ||
                         rule.Raw.IndexOf("ads", StringComparison.OrdinalIgnoreCase) >= 0;
            if (trackerish && adish) return BlockCategory.Both;
            if (trackerish) return BlockCategory.Tracker;
            return BlockCategory.Ad;
        }

        // ------------------------------------------------------------------
        // Statistics
        // ------------------------------------------------------------------

        public void RecordBlock(BlockCategory category, string host, long estimatedBytes)
        {
            lock (_lock)
            {
                if (category == BlockCategory.Ad || category == BlockCategory.Both) _stats.AdsBlocked++;
                if (category == BlockCategory.Tracker || category == BlockCategory.Both) _stats.TrackersBlocked++;
                _stats.BytesSaved += estimatedBytes > 0 ? estimatedBytes : 28 * 1024;
                _stats.SecondsSaved += 0.35;
                long c;
                _stats.PerHost.TryGetValue(host ?? "", out c);
                _stats.PerHost[host ?? ""] = c + 1;
                if (_stats.PerHost.Count > 400)
                {
                    var top = _stats.PerHost.OrderByDescending(kv => kv.Value).Take(200).ToDictionary(kv => kv.Key, kv => kv.Value);
                    _stats.PerHost = top;
                }
            }
            Action h = StatsChanged;
            if (h != null) h();
            SaveStatsLater();
        }

        public void RecordCookieBlock()
        {
            lock (_lock) _stats.CookiesBlocked++;
            SaveStatsLater();
        }

        public ShieldStats Stats()
        {
            lock (_lock)
            {
                ShieldStats copy = new ShieldStats
                {
                    AdsBlocked = _stats.AdsBlocked,
                    TrackersBlocked = _stats.TrackersBlocked,
                    CookiesBlocked = _stats.CookiesBlocked,
                    BytesSaved = _stats.BytesSaved,
                    SecondsSaved = _stats.SecondsSaved,
                    SinceUnixMs = _stats.SinceUnixMs
                };
                foreach (KeyValuePair<string, long> kv in _stats.PerHost) copy.PerHost[kv.Key] = kv.Value;
                return copy;
            }
        }

        public void ResetStats()
        {
            lock (_lock)
            {
                _stats = new ShieldStats { SinceUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            }
            SaveStats();
            Action h = StatsChanged;
            if (h != null) h();
        }

        private DateTime _lastStatsSave = DateTime.MinValue;

        private void SaveStatsLater()
        {
            if ((DateTime.Now - _lastStatsSave).TotalSeconds < 15) return;
            SaveStats();
        }

        public void SaveStats()
        {
            _lastStatsSave = DateTime.Now;
            try
            {
                ShieldStats s = Stats();
                File.WriteAllText(AppPaths.ShieldStatsJson, JsonSerializer.Serialize(s, SettingsManager.JsonOptions));
            }
            catch { }
        }

        private void LoadStats()
        {
            try
            {
                if (!File.Exists(AppPaths.ShieldStatsJson)) return;
                ShieldStats s = JsonSerializer.Deserialize<ShieldStats>(
                    File.ReadAllText(AppPaths.ShieldStatsJson), SettingsManager.JsonOptions);
                if (s != null) _stats = s;
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // HTTPS upgrade
        // ------------------------------------------------------------------

        public string TryUpgrade(string url)
        {
            if (!_settings.Model.Privacy.HttpsUpgrade) return null;
            if (url == null || !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;
            string host;
            try { host = new Uri(url).Host; } catch { return null; }
            lock (_lock) { if (_httpsUpgradeFailures.Contains(host)) return null; }
            return "https://" + url.Substring(7);
        }

        public void MarkUpgradeFailed(string host)
        {
            lock (_lock) _httpsUpgradeFailures.Add(host ?? "");
        }

        // ------------------------------------------------------------------
        // Browser-side privacy protection scripts
        // ------------------------------------------------------------------

        public string BuildProtectionScript()
        {
            PrivacySettings p = _settings.Model.Privacy;
            StringBuilder sb = new StringBuilder();
            sb.Append("(function(){try{");
            sb.Append("Object.defineProperty(navigator,'webdriver',{get:function(){return undefined;}});");
            if (p.CanvasDefense)
            {
                sb.Append(CanvasDefenseScript);
            }
            if (p.WebRtcLeakProtection)
            {
                sb.Append(WebRtcDefenseScript);
            }
            sb.Append("}catch(e){}})();");
            return sb.ToString();
        }

        private const string CanvasDefenseScript = """
            var seed = (Math.random() * 0xFFFFFF) | 0;
            function noise(v) { var x = Math.sin(v + seed) * 10000; return (x - Math.floor(x)) - 0.5; }
            var origGet = HTMLCanvasElement.prototype.getContext;
            HTMLCanvasElement.prototype.getContext = function (type, attrs) {
              var ctx = origGet.apply(this, arguments);
              if (ctx && type === '2d' && !ctx.__swifterPatched) {
                ctx.__swifterPatched = true;
                var origImageData = ctx.getImageData;
                ctx.getImageData = function () {
                  var d = origImageData.apply(this, arguments);
                  for (var i = 0; i < d.data.length; i += 97) {
                    d.data[i] = (d.data[i] + ((noise(i) * 2) | 0)) & 255;
                  }
                  return d;
                };
              }
              return ctx;
            };
            var origToData = HTMLCanvasElement.prototype.toDataURL;
            HTMLCanvasElement.prototype.toDataURL = function () {
              try {
                var ctx = this.getContext('2d');
                if (ctx) {
                  var img = ctx.getImageData(0, 0, Math.max(1, this.width), Math.max(1, this.height));
                  ctx.putImageData(img, 0, 0);
                }
              } catch (e) { }
              return origToData.apply(this, arguments);
            };
            try {
              var getParam = WebGLRenderingContext.prototype.getParameter;
              WebGLRenderingContext.prototype.getParameter = function (p) {
                if (p === 37445) return 'Swifter Generic';
                if (p === 37446) return 'Swifter Renderer';
                return getParam.apply(this, arguments);
              };
              if (window.WebGL2RenderingContext) {
                var getParam2 = WebGL2RenderingContext.prototype.getParameter;
                WebGL2RenderingContext.prototype.getParameter = function (p) {
                  if (p === 37445) return 'Swifter Generic';
                  if (p === 37446) return 'Swifter Renderer';
                  return getParam2.apply(this, arguments);
                };
              }
            } catch (e) { }
            try {
              var origCreate = window.AudioContext || window.webkitAudioContext;
              if (origCreate && origCreate.prototype.createOscillator) {
                var origOsc = origCreate.prototype.createOscillator;
                origCreate.prototype.createOscillator = function () {
                  var o = origOsc.apply(this, arguments);
                  try { o.frequency.value = o.frequency.value + noise(1) * 0.0001; } catch (e) { }
                  return o;
                };
              }
            } catch (e) { }
            """;

        private const string WebRtcDefenseScript = """
            (function () {
              function blocked(config) {
                if (config && config.iceServers && config.iceServers.length) {
                  for (var i = 0; i < config.iceServers.length; i++) {
                    var urls = config.iceServers[i].urls || config.iceServers[i].url || '';
                    if (typeof urls === 'string' && urls.indexOf('turn:') === 0) return false;
                  }
                }
                return true;
              }
              function patch(Ctor) {
                if (!Ctor) return;
                var Orig = Ctor;
                function Wrapped(config, constraints) {
                  var cfg = config || {};
                  if (blocked(cfg)) {
                    cfg.iceTransportPolicy = 'relay';
                    cfg.iceServers = [];
                  }
                  return new Orig(cfg, constraints);
                }
                Wrapped.prototype = Orig.prototype;
                return Wrapped;
              }
              if (window.RTCPeerConnection) window.RTCPeerConnection = patch(window.RTCPeerConnection);
              if (window.webkitRTCPeerConnection) window.webkitRTCPeerConnection = patch(window.webkitRTCPeerConnection);
              if (window.mozRTCPeerConnection) window.mozRTCPeerConnection = patch(window.mozRTCPeerConnection);
            })();
            """;

        // ------------------------------------------------------------------
        // DNS-over-HTTPS
        // ------------------------------------------------------------------

        public DohClient Doh { get { return _doh; } }

        public string DohTemplate()
        {
            switch (_settings.Model.Privacy.Doh)
            {
                case DohProvider.Cloudflare: return "https://cloudflare-dns.com/dns-query?name={0}&type=A";
                case DohProvider.Google: return "https://dns.google/resolve?name={0}&type=A";
                case DohProvider.Quad9: return "https://dns.quad9.net/dns-query?name={0}&type=A";
                case DohProvider.Custom: return _settings.Model.Privacy.DohCustomUrl;
                default: return "";
            }
        }

        // ------------------------------------------------------------------
        // Bundled starter filter list (EasyList / EasyPrivacy style subset)
        // ------------------------------------------------------------------

        public const string BundledList = """
            ! Swifter bundled protection list (EasyList/EasyPrivacy compatible subset)
            ! ---- hosts-file style ----
            127.0.0.1 ads.example.invalid
            0.0.0.0 adserver.example.invalid
            ! ---- ad networks ----
            ||doubleclick.net^
            ||googleadservices.com^
            ||googlesyndication.com^
            ||googletagservices.com^
            ||adservice.google.com^
            ||adnxs.com^
            ||appnexus.com^
            ||ads.yahoo.com^
            ||adtech.de^
            ||adsystem.amazon-adsystem.com^
            ||amazon-adsystem.com^
            ||taboola.com^
            ||outbrain.com^
            ||taboola.stream^
            ||mgid.com^
            ||revcontent.com^
            ||adroll.com^
            ||criteo.com^
            ||criteo.net^
            ||bidswitch.net^
            ||openx.net^
            ||rubiconproject.com^
            ||pubmatic.com^
            ||indexww.com^
            ||casalemedia.com^
            ||smartadserver.com^
            ||mediavine.com^
            ||monumentads.com^
            ||popads.net^
            ||popunder.ru^
            ||propellerads.com^
            ||exoclick.com^
            ||juicyads.com^
            ||trafficjunky.com^
            ||adskeeper.co.uk^
            ||zergnet.com^
            ||sharethrough.com^
            ||sponsorpay.com^
            ||bannerflow.com^
            ||adbanner.io^
            ! ---- trackers / analytics ----
            ||google-analytics.com^
            ||googletagmanager.com^
            ||analytics.google.com^
            ||hotjar.com^
            ||scorecardresearch.com^
            ||quantserve.com^
            ||quantcount.com^
            ||chartbeat.com^
            ||chartbeat.net^
            ||mixpanel.com^
            ||amplitude.com^
            ||segment.io^
            ||segment.com^
            ||fullstory.com^
            ||mouseflow.com^
            ||luckyorange.com^
            ||crazyegg.com^
            ||optimizely.com^
            ||vwo.com^
            ||matomo.cloud^
            ||piwik.pro^
            ||omtrdc.net^
            ||adobedtm.com^
            ||demdex.net^
            ||krxd.net^
            ||bluekai.com^
            ||oracleinfinity.io^
            ||tealiumiq.com^
            ||nr-data.net^
            ||newrelic.com^
            ||sentry.io^$third-party
            ||facebook.net^$third-party
            ||facebook.com/tr/$third-party
            ||connect.facebook.net^$third-party
            ||t.co^$third-party
            ||analytics.twitter.com^
            ||ads.linkedin.com^
            ||snap.licdn.com^$third-party
            ||tiktok.com/pixel/$third-party
            ||pinterest.com/js/pinit.js$third-party
            ||addthis.com^$third-party
            ||sharethis.com^$third-party
            ||statcounter.com^
            ||clicky.com^
            ||getclicky.com^
            ||woopra.com^
            ||parsely.com^
            ||parse.ly^
            ||perfdrive.com^
            ||speedcurve.com^$third-party
            ! ---- cryptomining / annoyances ----
            ||coinhive.com^
            ||coin-hive.com^
            ||cryptoloot.pro^
            ||authedmine.com^
            ||webminepool.com^
            ||onesignal.com^$third-party
            ||pushengage.com^$third-party
            ||pushcrew.com^$third-party
            ||getpushmonkey.com^$third-party
            ||list-manage.com/track/$third-party
            ||mailchimp.com/track/$third-party
            ! ---- exceptions (keep sites working) ----
            @@||google-analytics.com/analytics.js$domain=localhost
            @@||googletagmanager.com/gtm.js$domain=localhost
            """;
    }

    /// <summary>DNS-over-HTTPS JSON resolver with a 10 minute answer cache.</summary>
    public sealed class DohClient
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, DohAnswer> _cache = new Dictionary<string, DohAnswer>();
        private static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(() =>
        {
            HttpClient c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            c.DefaultRequestHeaders.Add("Accept", "application/dns-json");
            return c;
        });

        public sealed class DohAnswer
        {
            public string Host = "";
            public List<string> Addresses = new List<string>();
            public long TtlSeconds;
            public bool Blocked;
            public string Server = "";
            public long LatencyMs;
            public DateTime When;
        }

        public async Task<DohAnswer> ResolveAsync(string host, string template)
        {
            DohAnswer cached;
            lock (_lock)
            {
                if (_cache.TryGetValue(host, out cached) &&
                    (DateTime.UtcNow - cached.When).TotalSeconds < 600)
                    return cached;
            }
            DohAnswer answer = new DohAnswer { Host = host, Server = template };
            if (string.IsNullOrEmpty(template))
            {
                answer.Addresses.Add("(DoH disabled)");
                return answer;
            }
            string url = template.Contains("{0}")
                ? string.Format(CultureInfo.InvariantCulture, template, host)
                : template + "?name=" + Uri.EscapeDataString(host) + "&type=A";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string json = await Http.Value.GetStringAsync(url).ConfigureAwait(false);
                watch.Stop();
                answer.LatencyMs = watch.ElapsedMilliseconds;
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("Answer", out JsonElement arr) &&
                        arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement a in arr.EnumerateArray())
                        {
                            if (a.TryGetProperty("type", out JsonElement t) && t.GetInt32() == 1 &&
                                a.TryGetProperty("data", out JsonElement data))
                                answer.Addresses.Add(data.GetString());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                answer.Addresses.Add("error: " + ex.Message);
            }
            lock (_lock)
            {
                _cache[host] = answer;
                answer.When = DateTime.UtcNow;
            }
            return answer;
        }

    }
}
