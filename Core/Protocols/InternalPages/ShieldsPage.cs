using System.Collections.Generic;
using Swifter.Config;

namespace Swifter.Protocols.InternalPages
{
    /// <summary>swifter://shields - blocking stats, rules editor, DoH, protections.</summary>
    public static class ShieldsPage
    {
        public static string Render(SettingsManager settings)
        {
            PrivacySettings p = settings.Model.Privacy;
            Dictionary<string, object> state = new Dictionary<string, object>
            {
                { "accent", SettingsManager.ToHex(settings.Accent()) },
                { "doh", p.Doh.ToString() },
                { "dohCustom", p.DohCustomUrl },
                { "canvas", p.CanvasDefense },
                { "webrtc", p.WebRtcLeakProtection },
                { "https", p.HttpsUpgrade },
                { "cookies", p.BlockThirdPartyCookies },
                { "dnt", p.SendDoNotTrack },
                { "tracking", p.TrackingLevel.ToString() },
                { "enabled", settings.Model.Shields.Enabled }
            };
            string init = InitJs.Replace("__STATE__", ProtocolPageRenderer.ToJson(state));
            return ProtocolPageRenderer.Page("Shields", BodyHtml, init, ExtraCss, settings);
        }

        public const string BodyHtml = """
            <div class="wrap">
              <header class="page">
                <span class="logoBadge">&#128737;</span>
                <div><h1>Shields</h1><div class="sub">Ads, trackers, fingerprinting and DNS privacy</div></div>
                <span class="grow"></span>
                <label class="switch"><input id="master" type="checkbox"><span>Shields up</span></label>
              </header>

              <div class="grid stats-row">
                <div class="stat"><div class="v" id="adsV">0</div><div class="k">ads blocked</div></div>
                <div class="stat"><div class="v" id="trkV">0</div><div class="k">trackers blocked</div></div>
                <div class="stat"><div class="v" id="ckV">0</div><div class="k">cookie traps</div></div>
                <div class="stat"><div class="v" id="bwV">0</div><div class="k">bandwidth saved</div></div>
                <div class="stat"><div class="v" id="tmV">0</div><div class="k">time saved</div></div>
              </div>

              <div class="grid" style="grid-template-columns: 1fr 1fr;">
                <div class="card">
                  <h2>Most blocked domains</h2>
                  <table><thead><tr><th>Domain</th><th>Blocked</th></tr></thead>
                    <tbody id="hosts"></tbody></table>
                  <div class="row" style="margin-top:12px">
                    <button id="resetStats">Reset statistics</button>
                    <span class="muted small" id="sinceLabel"></span>
                  </div>
                </div>
                <div class="card">
                  <h2>DNS over HTTPS</h2>
                  <div class="row">
                    <select id="doh">
                      <option value="Cloudflare">Cloudflare (1.1.1.1)</option>
                      <option value="Google">Google (8.8.8.8)</option>
                      <option value="Quad9">Quad9 (9.9.9.9)</option>
                      <option value="Custom">Custom provider</option>
                      <option value="Off">Off (system DNS)</option>
                    </select>
                    <input id="dohCustom" type="text" class="grow" placeholder="https://my-doh/dns-query?name={0}&type=A">
                  </div>
                  <div class="row" style="margin-top:10px">
                    <input id="resolveHost" type="text" class="grow" placeholder="example.com">
                    <button id="resolveBtn">Resolve</button>
                  </div>
                  <div id="resolveOut" class="muted small" style="margin-top:10px"></div>
                </div>
              </div>

              <div class="card">
                <h2>Privacy protections</h2>
                <div class="grid" style="grid-template-columns: repeat(auto-fit,minmax(240px,1fr));">
                  <label class="opt"><input id="pCanvas" type="checkbox">
                    <div><b>Canvas fingerprint defence</b><div class="muted small">Adds deterministic noise to canvas, WebGL and audio fingerprints.</div></div></label>
                  <label class="opt"><input id="pWebrtc" type="checkbox">
                    <div><b>WebRTC IP leak prevention</b><div class="muted small">Forces relay-only ICE so local IPs never leave the machine.</div></div></label>
                  <label class="opt"><input id="pHttps" type="checkbox">
                    <div><b>Strict HTTPS upgrade</b><div class="muted small">Rewrites http:// navigations to https:// where possible.</div></div></label>
                  <label class="opt"><input id="pCookies" type="checkbox">
                    <div><b>Block third-party cookies</b><div class="muted small">Pairs with tracking prevention at the engine level.</div></div></label>
                  <label class="opt"><input id="pDnt" type="checkbox">
                    <div><b>Send Do-Not-Track</b><div class="muted small">Adds the DNT: 1 header to every request.</div></div></label>
                  <label class="opt"><select id="pTracking">
                      <option value="None">Tracking prevention: off</option>
                      <option value="Basic">Basic</option>
                      <option value="Balanced">Balanced</option>
                      <option value="Strict">Strict</option>
                    </select></label>
                </div>
              </div>

              <div class="card">
                <div class="row between"><h2 style="margin:0">Custom filter rules</h2>
                  <span class="muted small">EasyList / uBlock syntax: <code>||example.com^</code>, <code>127.0.0.1 badhost.com</code>, <code>@@||ok.com^</code></span></div>
                <div class="row" style="margin-top:12px">
                  <input id="newRule" type="text" class="grow" placeholder="||ads.example.org^$third-party">
                  <button class="primary" id="addRule">Add rule</button>
                </div>
                <div id="rules" class="rules"></div>
              </div>
            </div>
            """;

        public const string ExtraCss = """
            .logoBadge { width:40px; height:40px; border-radius:12px; display:flex; align-items:center;
                         justify-content:center; font-size:1.3em; background: var(--accent-soft); color: var(--accent); }
            .stats-row { grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); }
            .switch { display:flex; gap:8px; align-items:center; cursor:pointer; }
            .switch input { width:18px; height:18px; accent-color: var(--accent); }
            .opt { display:flex; gap:10px; align-items:flex-start; padding:10px; border:1px solid var(--border);
                   border-radius:10px; cursor:pointer; }
            .opt:hover { border-color: var(--accent); }
            .opt input { margin-top:3px; accent-color: var(--accent); }
            .opt select { width:100%; }
            .rules { margin-top:12px; max-height: 300px; overflow:auto; }
            .rule { display:flex; gap:8px; align-items:center; padding:5px 8px; border-radius:8px;
                    font-family: Consolas, monospace; font-size:.85em; }
            .rule:hover { background: var(--hover); }
            .rule .grow { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
            code { background: var(--hover); padding:1px 5px; border-radius:5px; }
            """;

        public const string InitJs = """
            var STATE = __STATE__;
            document.getElementById('master').checked = STATE.enabled;
            document.getElementById('doh').value = STATE.doh;
            document.getElementById('dohCustom').value = STATE.dohCustom || '';
            document.getElementById('pCanvas').checked = STATE.canvas;
            document.getElementById('pWebrtc').checked = STATE.webrtc;
            document.getElementById('pHttps').checked = STATE.https;
            document.getElementById('pCookies').checked = STATE.cookies;
            document.getElementById('pDnt').checked = STATE.dnt;
            document.getElementById('pTracking').value = STATE.tracking;

            function animate(el, to, suffix) {
              var from = Number(el.dataset.v || 0);
              el.dataset.v = to;
              var start = performance.now();
              function step(t) {
                var k = Math.min(1, (t - start) / 450);
                var v = Math.round(from + (to - from) * k);
                el.textContent = suffix ? Swifter.fmtBytes(v) : v.toLocaleString();
                if (k < 1) requestAnimationFrame(step);
              }
              requestAnimationFrame(step);
            }

            function loadStats() {
              Swifter.call('shields.stats').then(function (s) {
                animate(document.getElementById('adsV'), s.ads);
                animate(document.getElementById('trkV'), s.trackers);
                animate(document.getElementById('ckV'), s.cookies);
                animate(document.getElementById('bwV'), s.bytes, true);
                document.getElementById('tmV').textContent = Math.round(s.seconds / 60) + 'm';
                document.getElementById('sinceLabel').textContent = 'since ' + Swifter.fmtDate(s.since);
                document.getElementById('hosts').innerHTML = (s.hosts || []).map(function (h) {
                  return '<tr><td>' + Swifter.esc(h.host) + '</td><td>' + h.count + '</td></tr>';
                }).join('') || '<tr><td colspan="2" class="muted">Nothing blocked yet.</td></tr>';
              });
            }
            loadStats();
            setInterval(loadStats, 4000);

            document.getElementById('master').addEventListener('change', function () {
              Swifter.call('shields.setEnabled', { enabled: this.checked });
            });
            document.getElementById('resetStats').addEventListener('click', function () {
              Swifter.call('shields.reset').then(loadStats);
            });

            function pushProtections(extra) {
              var payload = {
                canvas: document.getElementById('pCanvas').checked,
                webrtc: document.getElementById('pWebrtc').checked,
                https: document.getElementById('pHttps').checked,
                cookies: document.getElementById('pCookies').checked,
                dnt: document.getElementById('pDnt').checked
              };
              if (extra) for (var k in extra) payload[k] = extra[k];
              Swifter.call('shields.protections', payload).then(function () {
                Swifter.toast('Protections updated');
              });
            }
            ['pCanvas', 'pWebrtc', 'pHttps', 'pCookies', 'pDnt'].forEach(function (id) {
              document.getElementById(id).addEventListener('change', function () { pushProtections(); });
            });
            document.getElementById('pTracking').addEventListener('change', function () {
              pushProtections({ tracking: this.value });
            });

            document.getElementById('doh').addEventListener('change', function () {
              Swifter.call('shields.doh', { provider: this.value, custom: document.getElementById('dohCustom').value });
            });
            document.getElementById('dohCustom').addEventListener('change', function () {
              Swifter.call('shields.doh', { provider: document.getElementById('doh').value, custom: this.value });
            });
            document.getElementById('resolveBtn').addEventListener('click', function () {
              var host = document.getElementById('resolveHost').value.trim();
              if (!host) return;
              var out = document.getElementById('resolveOut');
              out.textContent = 'Resolving via DoH...';
              Swifter.call('shields.resolve', { host: host }).then(function (a) {
                out.innerHTML = '<b>' + Swifter.esc(a.host) + '</b> &rarr; ' +
                  (a.addresses || []).map(Swifter.esc).join(', ') +
                  ' <span class="pill">' + a.latency + ' ms</span>';
              }).catch(function (e) { out.textContent = e.message; });
            });

            function loadRules() {
              Swifter.call('shields.rules').then(function (rules) {
                var box = document.getElementById('rules');
                if (!rules.length) { box.innerHTML = '<div class="muted small">No custom rules yet - the bundled list is still active.</div>'; return; }
                box.innerHTML = rules.map(function (r) {
                  return '<div class="rule"><span class="grow">' + Swifter.esc(r) + '</span>' +
                    '<button class="ghost" data-rm="' + Swifter.esc(r) + '">&#10005;</button></div>';
                }).join('');
                box.querySelectorAll('[data-rm]').forEach(function (b) {
                  b.addEventListener('click', function () {
                    Swifter.call('shields.removeRule', { rule: b.getAttribute('data-rm') }).then(loadRules);
                  });
                });
              });
            }
            loadRules();
            document.getElementById('addRule').addEventListener('click', function () {
              var r = document.getElementById('newRule').value.trim();
              if (!r) return;
              Swifter.call('shields.addRule', { rule: r }).then(function (ok) {
                if (!ok) { Swifter.toast('Rule syntax rejected', true); return; }
                document.getElementById('newRule').value = '';
                loadRules();
              });
            });
            """;
    }
}
