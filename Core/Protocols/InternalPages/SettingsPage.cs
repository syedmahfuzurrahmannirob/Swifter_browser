using System.Collections.Generic;
using Swifter.Config;

namespace Swifter.Protocols.InternalPages
{
    /// <summary>swifter://settings - every browser preference, engines and user scripts.</summary>
    public static class SettingsPage
    {
        public static string Render(SettingsManager settings)
        {
            Dictionary<string, object> state = new Dictionary<string, object>
            {
                { "accent", SettingsManager.ToHex(settings.Accent()) },
                { "logo", ProtocolPageRenderer.LogoDataUri(128) },
                { "model", settings.Model }
            };
            string init = InitJs.Replace("__STATE__", ProtocolPageRenderer.ToJson(state));
            return ProtocolPageRenderer.Page("Settings", BodyHtml, init, ExtraCss, settings);
        }

        public const string BodyHtml = """
            <div class="wrap">
              <header class="page">
                <span class="logoBadge">&#9881;</span>
                <div><h1>Settings</h1><div class="sub">Stored in %AppData%\Swifter\settings.json</div></div>
              </header>
              <div class="tabsbar">
                <button data-t="general" class="active">General</button>
                <button data-t="appearance">Appearance</button>
                <button data-t="privacy">Privacy</button>
                <button data-t="downloads">Downloads</button>
                <button data-t="search">Search</button>
                <button data-t="scripts">User scripts</button>
                <button data-t="about">About</button>
              </div>

              <section id="tab-general">
                <div class="card">
                  <h2>Startup</h2>
                  <div class="row">
                    <label>On start
                      <select id="startup">
                        <option value="NewTab">Open new tab page</option>
                        <option value="RestoreSession">Restore previous session</option>
                        <option value="CustomHome">Open home URL</option>
                      </select></label>
                    <label class="grow">Home URL <input id="homeUrl" type="text" class="grow" style="min-width:260px"></label>
                  </div>
                  <div class="row" style="margin-top:10px">
                    <label><input id="confirmExit" type="checkbox"> Ask before closing with multiple tabs</label>
                    <label><input id="clipboardSniffer" type="checkbox"> Clipboard link sniffer</label>
                    <label><input id="showBookmarksBar" type="checkbox"> Bookmarks bar</label>
                    <label><input id="showStatusBar" type="checkbox"> Status bar</label>
                  </div>
                </div>
                <div class="card">
                  <h2>New tab page</h2>
                  <div class="row">
                    <label><input id="showClock" type="checkbox"> Clock</label>
                    <label><input id="showGreeting" type="checkbox"> Greeting</label>
                    <label><input id="showNotepad" type="checkbox"> Notepad</label>
                    <label><input id="rssEnabled" type="checkbox"> RSS news</label>
                    <label class="grow">RSS URL <input id="rssUrl" type="text" class="grow" style="min-width:260px"></label>
                  </div>
                </div>
              </section>

              <section id="tab-appearance" class="hidden">
                <div class="card">
                  <h2>Theme</h2>
                  <div class="row">
                    <label>Mode
                      <select id="theme">
                        <option value="System">Follow Windows</option>
                        <option value="Dark">Dark</option>
                        <option value="Light">Light</option>
                      </select></label>
                    <label>Accent
                      <select id="accent">
                        <option value="#4cc2ff">Sky</option>
                        <option value="#7c5cff">Violet</option>
                        <option value="#37d67a">Mint</option>
                        <option value="#ff8c61">Coral</option>
                        <option value="#ffd166">Amber</option>
                        <option value="system">Windows accent</option>
                      </select></label>
                    <label>UI scale <input id="fontScale" type="range" min="0.8" max="1.5" step="0.05">
                      <span id="fontScaleVal" class="pill">1</span></label>
                  </div>
                  <div class="row" style="margin-top:10px">
                    <label>Tab bar
                      <select id="tabPlacement">
                        <option value="Top">Top</option>
                        <option value="Bottom">Bottom</option>
                      </select></label>
                    <label><input id="rounded" type="checkbox"> Rounded window corners</label>
                    <label><input id="acrylic" type="checkbox"> Acrylic top bar</label>
                  </div>
                </div>
              </section>

              <section id="tab-privacy" class="hidden">
                <div class="card">
                  <h2>Clear on exit</h2>
                  <div class="row">
                    <label><input id="clearOnExit" type="checkbox"> Enable</label>
                    <label><input id="clearCookies" type="checkbox"> Cookies</label>
                    <label><input id="clearCache" type="checkbox"> Cache</label>
                    <label><input id="clearHistory" type="checkbox"> History</label>
                    <label><input id="clearForm" type="checkbox"> Form data</label>
                  </div>
                  <div class="row" style="margin-top:14px">
                    <button id="clearNow">Clear browsing data now...</button>
                  </div>
                </div>
                <div class="card">
                  <h2>Tracking prevention</h2>
                  <div class="row">
                    <label>Level
                      <select id="tracking">
                        <option value="None">Off</option>
                        <option value="Basic">Basic</option>
                        <option value="Balanced">Balanced</option>
                        <option value="Strict">Strict</option>
                      </select></label>
                    <span class="muted small">Engine level; Swifter Shields add request-level blocking on top.</span>
                    <button data-open="shields">Open Shields dashboard</button>
                  </div>
                </div>
              </section>

              <section id="tab-downloads" class="hidden">
                <div class="card">
                  <h2>Segmented engine</h2>
                  <div class="row">
                    <label>Streams per download <input id="segments" type="range" min="1" max="32">
                      <span id="segmentsVal" class="pill">16</span></label>
                    <label><input id="verifySha" type="checkbox"> Verify SHA-256 after stitching</label>
                    <label><input id="askWhere" type="checkbox"> Ask where to save each file</label>
                  </div>
                  <div class="row" style="margin-top:10px">
                    <button data-open="downloads">Open download manager</button>
                    <span class="muted small">Queue, throttle and scheduler live on the downloads page.</span>
                  </div>
                </div>
              </section>

              <section id="tab-search" class="hidden">
                <div class="card">
                  <h2>Search engines</h2>
                  <table><thead><tr><th>Name</th><th>Query template</th><th>Default</th></tr></thead>
                    <tbody id="engines"></tbody></table>
                </div>
              </section>

              <section id="tab-scripts" class="hidden">
                <div class="card">
                  <div class="row between"><h2 style="margin:0">User scripts &amp; CSS</h2>
                    <button class="primary" id="scriptNew">+ New script</button></div>
                  <div id="scripts"></div>
                </div>
              </section>

              <section id="tab-about" class="hidden">
                <div class="card">
                  <div class="row"><img id="aboutLogo" style="width:64px;height:64px;border-radius:16px">
                    <div><h2 style="margin:0">Swifter Browser</h2>
                      <div class="muted">Version <span id="ver"></span> &middot; <span id="fw"></span></div>
                      <div class="muted small">WebView2 runtime <span id="rt"></span></div></div></div>
                  <div class="muted small" style="margin-top:12px">
                    Profile data: <span id="dataDir"></span><br>
                    History database: <span id="histDb"></span>
                  </div>
                </div>
              </section>
            </div>

            <dialog id="scriptDlg" style="max-width:760px">
              <h3>User script</h3>
              <div class="row">
                <label class="grow">Name <input id="sName" type="text" class="grow"></label>
                <label>Run at
                  <select id="sRunAt"><option value="document-start">document-start</option>
                    <option value="document-end">document-end</option></select></label>
                <label><input id="sEnabled" type="checkbox"> enabled</label>
              </div>
              <label class="lab">Matches (one per line, e.g. *.example.com or https://mail.example.com/*)</label>
              <textarea id="sMatches" style="min-height:60px"></textarea>
              <label class="lab">JavaScript</label>
              <textarea id="sJs" style="min-height:150px"></textarea>
              <label class="lab">CSS</label>
              <textarea id="sCss" style="min-height:90px"></textarea>
              <div class="row" style="margin-top:14px">
                <button class="primary" id="sSave">Save</button>
                <button id="sCancel">Cancel</button>
                <span class="grow"></span>
                <button class="danger" id="sDelete">Delete</button>
              </div>
            </dialog>
            """;

        public const string ExtraCss = """
            .logoBadge { width:40px; height:40px; border-radius:12px; display:flex; align-items:center;
                         justify-content:center; font-size:1.3em; background: var(--accent-soft); color: var(--accent); }
            label { display:inline-flex; gap:7px; align-items:center; }
            input[type=range] { accent-color: var(--accent); }
            .scriptRow { display:flex; gap:10px; align-items:center; padding:9px 6px; border-bottom:1px solid var(--border); }
            .scriptRow .n { font-weight:600; }
            .scriptRow .m { color:var(--muted); font-size:.8em; }
            .lab { display:block; margin:10px 0 4px; color:var(--muted); font-size:.85em; }
            """;

        public const string InitJs = """
            var STATE = __STATE__;
            var M = STATE.model;

            document.querySelectorAll('.tabsbar button').forEach(function (b) {
              b.addEventListener('click', function () {
                document.querySelectorAll('.tabsbar button').forEach(function (x) { x.classList.remove('active'); });
                b.classList.add('active');
                document.querySelectorAll('section[id^=tab-]').forEach(function (s) { s.classList.add('hidden'); });
                document.getElementById('tab-' + b.getAttribute('data-t')).classList.remove('hidden');
              });
            });
            document.querySelectorAll('[data-open]').forEach(function (b) {
              b.addEventListener('click', function () { Swifter.call('open-internal', { page: b.getAttribute('data-open') }); });
            });

            function patch(obj) { Swifter.call('settings.patch', obj); }
            function bind(id, key, kind) {
              var el = document.getElementById(id);
              if (!el) return;
              if (kind === 'check') { el.checked = !!M_lookup(key); el.addEventListener('change', function () { var o = {}; o[key] = el.checked; patch(o); }); }
              else if (kind === 'num') { el.value = M_lookup(key); el.addEventListener('change', function () { var o = {}; o[key] = Number(el.value); patch(o); }); }
              else { el.value = M_lookup(key) || ''; el.addEventListener('change', function () { var o = {}; o[key] = el.value; patch(o); }); }
            }
            function M_lookup(key) {
              var g = M.general, a = M.appearance, p = M.privacy, n = M.newTab, d = M.downloads;
              switch (key) {
                case 'startup': return g.startup;
                case 'homeUrl': return g.homeUrl;
                case 'confirmExit': return g.confirmBeforeExit;
                case 'clipboardSniffer': return g.clipboardSniffer;
                case 'showBookmarksBar': return g.showBookmarksBar;
                case 'showStatusBar': return g.showStatusBar;
                case 'theme': return a.theme;
                case 'accent': return a.accentColor;
                case 'fontScale': return a.fontScale;
                case 'tabPlacement': return a.tabPlacement;
                case 'rounded': return a.roundedCorners;
                case 'acrylic': return a.acrylicTopBar;
                case 'clearOnExit': return p.clearOnExit;
                case 'clearCookies': return p.clearCookiesOnExit;
                case 'clearCache': return p.clearCacheOnExit;
                case 'clearHistory': return p.clearHistoryOnExit;
                case 'clearForm': return p.clearFormDataOnExit;
                case 'tracking': return p.trackingLevel;
                case 'showClock': return n.showClock;
                case 'showGreeting': return n.showGreeting;
                case 'showNotepad': return n.showNotepad;
                case 'rssEnabled': return n.rssEnabled;
                case 'rssUrl': return n.rssUrl;
                case 'segments': return d.segmentCount;
                case 'verifySha': return d.verifySha256;
                case 'askWhere': return g.askWhereToSave;
              }
              return '';
            }
            bind('startup', 'startup', 'text');
            bind('homeUrl', 'homeUrl', 'text');
            bind('confirmExit', 'confirmExit', 'check');
            bind('clipboardSniffer', 'clipboardSniffer', 'check');
            bind('showBookmarksBar', 'showBookmarksBar', 'check');
            bind('showStatusBar', 'showStatusBar', 'check');
            bind('theme', 'theme', 'text');
            bind('accent', 'accent', 'text');
            bind('tabPlacement', 'tabPlacement', 'text');
            bind('rounded', 'rounded', 'check');
            bind('acrylic', 'acrylic', 'check');
            bind('clearOnExit', 'clearOnExit', 'check');
            bind('clearCookies', 'clearCookies', 'check');
            bind('clearCache', 'clearCache', 'check');
            bind('clearHistory', 'clearHistory', 'check');
            bind('clearForm', 'clearForm', 'check');
            bind('tracking', 'tracking', 'text');
            bind('showClock', 'showClock', 'check');
            bind('showGreeting', 'showGreeting', 'check');
            bind('showNotepad', 'showNotepad', 'check');
            bind('rssEnabled', 'rssEnabled', 'check');
            bind('rssUrl', 'rssUrl', 'text');
            bind('verifySha', 'verifySha', 'check');
            bind('askWhere', 'askWhere', 'check');

            var fs = document.getElementById('fontScale');
            fs.value = M.appearance.fontScale;
            document.getElementById('fontScaleVal').textContent = M.appearance.fontScale;
            fs.addEventListener('input', Swifter.debounce(function () {
              document.getElementById('fontScaleVal').textContent = this.value;
              patch({ fontScale: Number(this.value) });
            }, 200));
            var seg = document.getElementById('segments');
            seg.value = M.downloads.segmentCount;
            document.getElementById('segmentsVal').textContent = M.downloads.segmentCount;
            seg.addEventListener('input', Swifter.debounce(function () {
              document.getElementById('segmentsVal').textContent = this.value;
              patch({ segments: Number(this.value) });
            }, 200));

            document.getElementById('clearNow').addEventListener('click', function () {
              if (!confirm('Clear cookies, cache and site data now?')) return;
              Swifter.call('clearBrowsingData', {})
                .then(function () { Swifter.toast('Browsing data cleared'); })
                .catch(function (e) { Swifter.toast(e.message, true); });
            });

            function loadEngines() {
              Swifter.call('search.engines').then(function (s) {
                document.getElementById('engines').innerHTML = s.engines.map(function (e) {
                  return '<tr><td>' + Swifter.esc(e.name) + '</td><td class="muted small">' + Swifter.esc(e.id) + '</td>' +
                    '<td><input type="radio" name="def" data-id="' + e.id + '"' +
                    (e.id === s.current ? ' checked' : '') + '></td></tr>';
                }).join('');
                document.querySelectorAll('input[name=def]').forEach(function (r) {
                  r.addEventListener('change', function () {
                    Swifter.call('search.set', { id: r.getAttribute('data-id') });
                  });
                });
              });
            }
            loadEngines();

            var editingScript = '';
            function loadScripts() {
              Swifter.call('scripts.list').then(function (list) {
                var box = document.getElementById('scripts');
                if (!list.length) { box.innerHTML = '<div class="muted small" style="margin-top:10px">No user scripts yet. Create one to inject JS/CSS into matching sites.</div>'; return; }
                box.innerHTML = list.map(function (s) {
                  return '<div class="scriptRow"><input type="checkbox" data-en="' + s.id + '"' + (s.enabled ? ' checked' : '') + '>' +
                    '<div class="grow"><div class="n">' + Swifter.esc(s.name) + '</div>' +
                    '<div class="m">' + Swifter.esc((s.matches || []).join(', ')) + ' &middot; ' + s.runAt + '</div></div>' +
                    '<button data-ed="' + s.id + '">Edit</button></div>';
                }).join('');
                box.querySelectorAll('[data-en]').forEach(function (c) {
                  c.addEventListener('change', function () {
                    Swifter.call('scripts.update', { id: c.getAttribute('data-en'), enabled: c.checked });
                  });
                });
                box.querySelectorAll('[data-ed]').forEach(function (b) {
                  b.addEventListener('click', function () {
                    var id = b.getAttribute('data-ed');
                    Swifter.call('scripts.list').then(function (list2) {
                      var s = list2.find(function (x) { return x.id === id; });
                      if (!s) return;
                      editingScript = id;
                      document.getElementById('sName').value = s.name;
                      document.getElementById('sRunAt').value = s.runAt;
                      document.getElementById('sEnabled').checked = s.enabled;
                      document.getElementById('sMatches').value = (s.matches || []).join('\n');
                      document.getElementById('sJs').value = s.jsCode || '';
                      document.getElementById('sCss').value = s.cssCode || '';
                      document.getElementById('scriptDlg').showModal();
                    });
                  });
                });
              });
            }
            loadScripts();
            document.getElementById('scriptNew').addEventListener('click', function () {
              editingScript = '';
              document.getElementById('sName').value = 'New script';
              document.getElementById('sRunAt').value = 'document-end';
              document.getElementById('sEnabled').checked = true;
              document.getElementById('sMatches').value = '*.example.com';
              document.getElementById('sJs').value = '// runs in page context\nconsole.log("hello from Swifter");';
              document.getElementById('sCss').value = '/* injected css */';
              document.getElementById('scriptDlg').showModal();
            });
            document.getElementById('sCancel').addEventListener('click', function () {
              document.getElementById('scriptDlg').close();
            });
            document.getElementById('sSave').addEventListener('click', function () {
              var payload = {
                name: document.getElementById('sName').value,
                runAt: document.getElementById('sRunAt').value,
                enabled: document.getElementById('sEnabled').checked,
                matches: document.getElementById('sMatches').value.split('\n').map(function (s) { return s.trim(); }).filter(function (s) { return s; }),
                js: document.getElementById('sJs').value,
                css: document.getElementById('sCss').value
              };
              var p;
              if (editingScript) { payload.id = editingScript; p = Swifter.call('scripts.update', payload); }
              else p = Swifter.call('scripts.add', {
                name: payload.name, match: (payload.matches || [])[0] || '*',
                js: payload.js, css: payload.css, runAt: payload.runAt
              }).then(function (id) {
                payload.id = id;
                return Swifter.call('scripts.update', payload);
              });
              p.then(function () { document.getElementById('scriptDlg').close(); loadScripts(); });
            });
            document.getElementById('sDelete').addEventListener('click', function () {
              if (!editingScript) return;
              Swifter.call('scripts.remove', { id: editingScript }).then(function () {
                document.getElementById('scriptDlg').close(); loadScripts();
              });
            });

            Swifter.call('about.info').then(function (i) {
              document.getElementById('aboutLogo').src = STATE.logo || '';
              document.getElementById('ver').textContent = i.version;
              document.getElementById('fw').textContent = i.framework;
              document.getElementById('rt').textContent = i.runtime;
              document.getElementById('dataDir').textContent = i.dataDir;
              document.getElementById('histDb').textContent = i.historyDb;
            });
            """;
    }
}
