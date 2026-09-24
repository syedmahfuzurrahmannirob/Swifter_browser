using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Swifter.Config;

namespace Swifter.Protocols.InternalPages
{
    /// <summary>swifter://newtab - speed dial, wallpapers, widgets, clock, search.</summary>
    public static class NewTabPage
    {
        public static string Render(SettingsManager settings, Dictionary<string, string> query)
        {
            Dictionary<string, object> state = new Dictionary<string, object>
            {
                { "logo", ProtocolPageRenderer.LogoDataUri(96) },
                { "engines", EngineList(settings) },
                { "currentEngine", settings.Model.NewTab.SearchEngine },
                { "wallpaper", settings.Model.NewTab.Wallpaper },
                { "showClock", settings.Model.NewTab.ShowClock },
                { "showGreeting", settings.Model.NewTab.ShowGreeting },
                { "showNotepad", settings.Model.NewTab.ShowNotepad },
                { "rssEnabled", settings.Model.NewTab.RssEnabled },
                { "rssUrl", settings.Model.NewTab.RssUrl },
                { "accent", SettingsManager.ToHex(settings.Accent()) },
                { "dark", settings.IsDarkTheme() },
                { "query", query.ContainsKey("q") ? query["q"] : "" }
            };

            string init = InitJs.Replace("__STATE__", ProtocolPageRenderer.ToJson(state));
            return ProtocolPageRenderer.Page("New Tab", BodyHtml, init, ExtraCss, settings);
        }

        private static List<Dictionary<string, object>> EngineList(SettingsManager settings)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            foreach (SearchEngine e in settings.Model.SearchEngines)
                list.Add(new Dictionary<string, object> { { "id", e.Id }, { "name", e.Name } });
            return list;
        }

        public const string BodyHtml = """
            <div class="wall" id="wall"></div>
            <div class="veil"></div>
            <nav class="topbar">
              <img id="logo" alt="Swifter">
              <span class="brand">Swifter</span>
              <span class="spacer"></span>
              <button class="nt" data-open="downloads" title="Downloads">&#8681;</button>
              <button class="nt" data-open="history" title="History">&#9719;</button>
              <button class="nt" data-open="bookmarks" title="Bookmarks">&#9733;</button>
              <button class="nt" data-open="shields" title="Shields">&#128737;</button>
              <button class="nt" data-open="settings" title="Settings">&#9881;</button>
            </nav>
            <main class="hero">
              <div id="clock" class="clock hidden"></div>
              <div id="greet" class="greet hidden"></div>
              <form id="searchbox" class="search">
                <span class="mag">&#128269;</span>
                <input id="q" type="text" autocomplete="off" placeholder="Search the web or type a URL" spellcheck="false">
                <button type="submit" class="go">&#8594;</button>
              </form>
              <div id="pills" class="pills"></div>
              <div id="dials" class="dials"></div>
              <div class="widgets">
                <section class="widget" id="notepadWidget">
                  <div class="wh row between"><b>&#128221; Quick notepad</b>
                    <span class="muted small" id="noteSaved">saved</span></div>
                  <textarea id="note" placeholder="Jot something down - it is saved automatically"></textarea>
                </section>
                <section class="widget">
                  <div class="wh row between"><b>&#128240; News feed</b>
                    <button class="ghost small" id="rssRefresh">&#8635;</button></div>
                  <div id="rss" class="rss"><div class="muted small">Loading feed...</div></div>
                </section>
                <section class="widget">
                  <div class="wh"><b>&#128200; Your browsing</b></div>
                  <div id="stats" class="stats"></div>
                  <div class="wh" style="margin-top:10px"><b>&#128337; Recent</b></div>
                  <div id="recent" class="recent"></div>
                </section>
              </div>
            </main>
            <dialog id="dialEdit">
              <h3 id="dialTitle">Edit shortcut</h3>
              <label class="lab">Name</label>
              <input id="dialName" type="text" style="width:100%">
              <label class="lab">URL</label>
              <input id="dialUrl" type="text" style="width:100%" placeholder="https://">
              <div class="row" style="margin-top:16px">
                <button class="primary" id="dialSave">Save</button>
                <button id="dialCancel">Cancel</button>
                <span class="spacer"></span>
                <button class="danger" id="dialDelete">Delete</button>
              </div>
            </dialog>
            <dialog id="wallDialog">
              <h3>Background</h3>
              <div id="wallPresets" class="wallgrid"></div>
              <div class="row" style="margin-top:14px">
                <button id="wallUpload">Upload image...</button>
                <button id="wallClose">Close</button>
              </div>
            </dialog>
            <button id="wallBtn" class="wallbtn" title="Customize background">&#127912;</button>
            """;

        public const string ExtraCss = """
            body { overflow-x: hidden; }
            .wall { position: fixed; inset: 0; z-index: -2; background-size: cover; background-position: center; }
            .veil { position: fixed; inset: 0; z-index: -1;
                    background: linear-gradient(180deg, rgba(8,10,14,.25), rgba(8,10,14,.72)); }
            body.light .veil { background: linear-gradient(180deg, rgba(255,255,255,.15), rgba(255,255,255,.65)); }
            .topbar { display:flex; align-items:center; gap:10px; padding: 12px 18px; }
            .topbar img { width:26px; height:26px; border-radius:7px; }
            .brand { font-weight:700; letter-spacing:.6px; opacity:.9; }
            .topbar button.nt { background: rgba(255,255,255,.08); border-color: rgba(255,255,255,.14); color: inherit; }
            .hero { max-width: 980px; margin: 0 auto; padding: 4vh 22px 60px; }
            .clock { font-size: 4.4em; font-weight: 200; text-align:center; letter-spacing: 2px;
                     text-shadow: 0 2px 24px rgba(0,0,0,.35); }
            .greet { text-align:center; opacity:.85; margin-bottom: 22px; font-size: 1.05em; }
            .search { display:flex; align-items:center; gap:10px; background: var(--card);
                      border:1px solid var(--border); border-radius: 999px; padding: 6px 8px 6px 18px;
                      box-shadow: var(--shadow); max-width: 640px; margin: 0 auto; }
            .search input { flex:1; border:none; background:transparent; color:var(--text); font-size:1.05em; outline:none; padding: 8px 0; }
            .search .go { border-radius:999px; }
            .pills { display:flex; gap:6px; justify-content:center; flex-wrap:wrap; margin: 14px 0 26px; }
            .dials { display:grid; grid-template-columns: repeat(auto-fill, minmax(96px, 1fr)); gap: 14px; margin-bottom: 30px; }
            .dial { background: color-mix(in srgb, var(--card) 82%, transparent); border:1px solid var(--border);
                    border-radius: 14px; padding: 14px 8px 10px; text-align:center; cursor:pointer; position:relative;
                    backdrop-filter: blur(8px); transition: transform .12s ease, border-color .12s ease; }
            .dial:hover { transform: translateY(-3px); border-color: var(--accent); }
            .dial img { width: 34px; height: 34px; border-radius: 9px; }
            .dial .t { font-size: .82em; margin-top: 7px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
            .dial .edit { position:absolute; top:4px; right:6px; opacity:0; font-size:.8em; }
            .dial:hover .edit { opacity:.8; }
            .dial.add { border-style: dashed; display:flex; align-items:center; justify-content:center;
                        min-height: 88px; font-size: 1.6em; color: var(--muted); }
            .widgets { display:grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 14px; }
            .widget { background: color-mix(in srgb, var(--card) 86%, transparent); border:1px solid var(--border);
                      border-radius: 14px; padding: 14px 16px; backdrop-filter: blur(8px); }
            .wh { margin-bottom: 8px; }
            .widget textarea { min-height: 110px; background: transparent; }
            .rss a { display:block; padding: 6px 0; border-bottom: 1px dashed var(--border); color: var(--text); }
            .rss a:hover { color: var(--accent); text-decoration:none; }
            .rss .d { color: var(--muted); font-size: .78em; }
            .stats { display:flex; gap: 18px; }
            .stats .v { font-size: 1.4em; font-weight: 700; }
            .stats .k { color: var(--muted); font-size:.78em; text-transform:uppercase; letter-spacing:.4px; }
            .recent a { display:flex; gap:8px; align-items:center; padding:4px 0; color:var(--text); font-size:.9em; }
            .recent img { width:16px; height:16px; border-radius:4px; }
            .wallbtn { position: fixed; right: 18px; bottom: 18px; border-radius: 999px; width: 44px; height: 44px;
                       font-size: 1.2em; background: color-mix(in srgb, var(--card) 85%, transparent); }
            .wallgrid { display:grid; grid-template-columns: repeat(4, 1fr); gap: 8px; }
            .wallgrid button { height: 52px; border-radius: 10px; padding:0; }
            .lab { display:block; margin: 10px 0 4px; color: var(--muted); font-size:.85em; }
            """;

        public const string InitJs = """
            var STATE = __STATE__;
            var PRESETS = {
              'gradient-aurora': 'linear-gradient(135deg,#0f2027,#203a43 40%,#2c5364 70%,#4cc2ff)',
              'gradient-sunset': 'linear-gradient(160deg,#2b1055,#7597de 55%,#ff8c61)',
              'gradient-mesh':   'radial-gradient(1200px 600px at 10% 10%, #1b3a5c, transparent), radial-gradient(900px 500px at 90% 30%, #3d1b5c, transparent), linear-gradient(#0b1020,#0b1020)',
              'gradient-mono':   'linear-gradient(180deg,#14161a,#1f242b)',
              'gradient-forest': 'linear-gradient(150deg,#0b3d2e,#14746f 55%,#7fd6a4)',
              'gradient-candy':  'linear-gradient(140deg,#ff9a9e,#fad0c4 45%,#a18cd1)'
            };
            document.getElementById('logo').src = STATE.logo;
            if (!STATE.dark) document.body.classList.add('light');

            function applyWall(v) {
              var wall = document.getElementById('wall');
              if (v && v.indexOf('data:') === 0) wall.style.backgroundImage = 'url(' + v + ')';
              else if (PRESETS[v]) wall.style.backgroundImage = PRESETS[v];
              else wall.style.backgroundImage = PRESETS['gradient-aurora'];
            }
            applyWall(STATE.wallpaper);

            document.querySelectorAll('[data-open]').forEach(function (b) {
              b.addEventListener('click', function () {
                Swifter.call('open-internal', { page: b.getAttribute('data-open') });
              });
            });

            if (STATE.showClock) {
              var clock = document.getElementById('clock');
              clock.classList.remove('hidden');
              function tick() {
                var d = new Date();
                clock.textContent = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
              }
              tick(); setInterval(tick, 10000);
            }
            if (STATE.showGreeting) {
              var g = document.getElementById('greet');
              g.classList.remove('hidden');
              var h = new Date().getHours();
              var word = h < 5 ? 'Burning the midnight oil' : h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening';
              g.textContent = word + ' - here is your corner of the web.';
            }

            var pills = document.getElementById('pills');
            STATE.engines.forEach(function (e) {
              var b = document.createElement('button');
              b.className = 'chip' + (e.id === STATE.currentEngine ? ' active' : '');
              b.textContent = e.name;
              b.addEventListener('click', function () {
                STATE.currentEngine = e.id;
                Swifter.call('search.set', { id: e.id });
                pills.querySelectorAll('.chip').forEach(function (c) { c.classList.remove('active'); });
                b.classList.add('active');
                document.getElementById('q').focus();
              });
              pills.appendChild(b);
            });

            document.getElementById('searchbox').addEventListener('submit', function (ev) {
              ev.preventDefault();
              var q = document.getElementById('q').value.trim();
              if (!q) return;
              if (/^[a-z]+:[^\s]*$/i.test(q) || /^[\w-]+(\.[\w-]+)+([\/?#].*)?$/i.test(q) || q.indexOf('swifter://') === 0) {
                var url = q.indexOf('://') > 0 ? q : (q.indexOf('swifter://') === 0 ? q : 'https://' + q);
                Swifter.call('navigate', { url: url });
              } else {
                Swifter.call('search', { engine: STATE.currentEngine, query: q });
              }
            });
            if (STATE.query) document.getElementById('q').value = STATE.query;

            var dialBox = document.getElementById('dials');
            var editing = -1;
            function favFor(url, title) {
              try {
                var host = new URL(url).hostname;
                return 'https://www.google.com/s2/favicons?domain=' + host + '&sz=64';
              } catch (e) { return ''; }
            }
            function renderDials(items) {
              dialBox.innerHTML = '';
              items.forEach(function (it, i) {
                var d = document.createElement('div');
                d.className = 'dial';
                d.innerHTML = '<img src="' + Swifter.esc(favFor(it.url)) + '" onerror="this.style.visibility=\'hidden\'">' +
                  '<div class="t">' + Swifter.esc(it.title || it.url) + '</div>' +
                  '<span class="edit">&#9998;</span>';
                d.addEventListener('click', function (ev) {
                  if (ev.target.className === 'edit') { openEdit(i, items); return; }
                  Swifter.call('navigate', { url: it.url });
                });
                dialBox.appendChild(d);
              });
              var add = document.createElement('div');
              add.className = 'dial add';
              add.textContent = '+';
              add.title = 'Add shortcut';
              add.addEventListener('click', function () { openEdit(-1, items); });
              dialBox.appendChild(add);
            }
            function openEdit(index, items) {
              editing = index;
              var dlg = document.getElementById('dialEdit');
              document.getElementById('dialTitle').textContent = index < 0 ? 'New shortcut' : 'Edit shortcut';
              document.getElementById('dialName').value = index < 0 ? '' : items[index].title;
              document.getElementById('dialUrl').value = index < 0 ? '' : items[index].url;
              document.getElementById('dialDelete').style.display = index < 0 ? 'none' : '';
              dlg.showModal();
            }
            document.getElementById('dialCancel').addEventListener('click', function () {
              document.getElementById('dialEdit').close();
            });
            document.getElementById('dialSave').addEventListener('click', function () {
              var name = document.getElementById('dialName').value.trim();
              var url = document.getElementById('dialUrl').value.trim();
              if (!url) return;
              if (url.indexOf('://') < 0) url = 'https://' + url;
              Swifter.call('quickdial.get').then(function (items) {
                if (editing < 0) items.push({ title: name || url, url: url });
                else { items[editing].title = name || url; items[editing].url = url; }
                return Swifter.call('quickdial.set', { items: items });
              }).then(function () {
                document.getElementById('dialEdit').close();
                loadDials();
              });
            });
            document.getElementById('dialDelete').addEventListener('click', function () {
              Swifter.call('quickdial.get').then(function (items) {
                items.splice(editing, 1);
                return Swifter.call('quickdial.set', { items: items });
              }).then(function () {
                document.getElementById('dialEdit').close();
                loadDials();
              });
            });
            function loadDials() {
              Swifter.call('quickdial.get').then(renderDials).catch(function (e) { Swifter.toast(e.message, true); });
            }
            loadDials();

            var noteArea = document.getElementById('note');
            if (!STATE.showNotepad) document.getElementById('notepadWidget').classList.add('hidden');
            Swifter.call('notepad.get').then(function (t) { noteArea.value = t || ''; });
            noteArea.addEventListener('input', Swifter.debounce(function () {
              Swifter.call('notepad.set', { text: noteArea.value });
              var s = document.getElementById('noteSaved');
              s.textContent = 'saved ' + new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
            }, 500));

            function loadRss() {
              var box = document.getElementById('rss');
              if (!STATE.rssEnabled) { box.innerHTML = '<div class="muted small">News feed disabled in settings.</div>'; return; }
              box.innerHTML = '<div class="muted small">Loading feed...</div>';
              Swifter.call('rss.fetch', { url: STATE.rssUrl }).then(function (items) {
                if (!items || !items.length) { box.innerHTML = '<div class="muted small">Feed unavailable.</div>'; return; }
                box.innerHTML = items.map(function (it) {
                  return '<a href="#" data-link="' + Swifter.esc(it.link) + '">' + Swifter.esc(it.title) +
                    '</a><div class="d">' + Swifter.esc(it.date) + '</div>';
                }).join('');
                box.querySelectorAll('a').forEach(function (a) {
                  a.addEventListener('click', function (ev) {
                    ev.preventDefault();
                    Swifter.call('navigate', { url: a.getAttribute('data-link') });
                  });
                });
              }).catch(function () { box.innerHTML = '<div class="muted small">Feed unavailable.</div>'; });
            }
            loadRss();
            document.getElementById('rssRefresh').addEventListener('click', loadRss);

            Swifter.call('history.stats', { days: 7 }).then(function (s) {
              document.getElementById('stats').innerHTML =
                '<div><div class="v">' + s.total + '</div><div class="k">total visits</div></div>' +
                '<div><div class="v">' + (s.daily || []).reduce(function (a, b) { return a + b.value; }, 0) +
                '</div><div class="k">last 7 days</div></div>' +
                '<div><div class="v">' + (s.domains || []).length + '</div><div class="k">top domains</div></div>';
            });
            Swifter.call('history.recent', { limit: 6 }).then(function (rows) {
              var box = document.getElementById('recent');
              if (!rows.length) { box.innerHTML = '<div class="muted small">No history yet.</div>'; return; }
              box.innerHTML = rows.map(function (r) {
                return '<a href="#" data-url="' + Swifter.esc(r.url) + '"><img src="' + Swifter.esc(r.icon) + '">' +
                  Swifter.esc(r.title || r.url) + '</a>';
              }).join('');
              box.querySelectorAll('a').forEach(function (a) {
                a.addEventListener('click', function (ev) {
                  ev.preventDefault();
                  Swifter.call('navigate', { url: a.getAttribute('data-url') });
                });
              });
            });

            var wallDlg = document.getElementById('wallDialog');
            var grid = document.getElementById('wallPresets');
            Object.keys(PRESETS).forEach(function (k) {
              var b = document.createElement('button');
              b.style.backgroundImage = PRESETS[k];
              b.title = k;
              b.addEventListener('click', function () {
                Swifter.call('wallpaper.set', { value: k }).then(function (v) { applyWall(v); });
              });
              grid.appendChild(b);
            });
            document.getElementById('wallBtn').addEventListener('click', function () { wallDlg.showModal(); });
            document.getElementById('wallClose').addEventListener('click', function () { wallDlg.close(); });
            document.getElementById('wallUpload').addEventListener('click', function () {
              Swifter.call('wallpaper.set', { value: 'upload:' }).then(function (v) { applyWall(v); });
            });
            """;
    }
}
