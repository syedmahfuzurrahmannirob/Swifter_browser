using System.Collections.Generic;
using Swifter.Config;

namespace Swifter.Protocols.InternalPages
{
    /// <summary>swifter://downloads - live segment visualiser, throttles, media tab.</summary>
    public static class DownloadsPage
    {
        public static string Render(SettingsManager settings)
        {
            Dictionary<string, object> state = new Dictionary<string, object>
            {
                { "accent", SettingsManager.ToHex(settings.Accent()) }
            };
            string init = InitJs.Replace("__STATE__", ProtocolPageRenderer.ToJson(state));
            return ProtocolPageRenderer.Page("Downloads", BodyHtml, init, ExtraCss, settings);
        }

        public const string BodyHtml = """
            <div class="wrap">
              <header class="page">
                <span class="logoBadge">&#8681;</span>
                <div><h1>Downloads</h1>
                  <div class="sub"><span id="aggSpeed">--</span> aggregate &middot;
                    <span id="activeCount">0</span> active &middot;
                    <span id="throttleLabel">unlimited</span></div></div>
              </header>

              <div class="card">
                <div class="row between">
                  <div class="row">
                    <label class="muted small">Threads</label>
                    <input id="threads" type="range" min="1" max="32" step="1">
                    <span id="threadsVal" class="pill">16</span>
                    <label class="muted small">Active</label>
                    <input id="maxActive" type="number" min="1" max="8" style="width:64px">
                    <label class="muted small">Limit</label>
                    <select id="throttle">
                      <option value="0">Unlimited</option>
                      <option value="256">256 KB/s</option>
                      <option value="512">512 KB/s</option>
                      <option value="1024">1 MB/s</option>
                      <option value="5120">5 MB/s</option>
                      <option value="10240">10 MB/s</option>
                      <option value="20480">20 MB/s</option>
                    </select>
                  </div>
                  <div class="row">
                    <button id="clearDone">Clear completed</button>
                    <button id="openFolder">Open folder</button>
                  </div>
                </div>
                <div class="row" style="margin-top:12px">
                  <label class="muted small">Save to</label>
                  <input id="savePath" type="text" class="grow" readonly>
                  <button id="pickPath">Browse...</button>
                </div>
                <div class="row" style="margin-top:12px">
                  <label class="muted small">Scheduler</label>
                  <input id="schedOn" type="checkbox">
                  <span class="muted small">from</span><input id="schedStart" type="text" style="width:70px" placeholder="22:00">
                  <span class="muted small">to</span><input id="schedEnd" type="text" style="width:70px" placeholder="06:00">
                  <span id="schedState" class="chip">window open</span>
                </div>
              </div>

              <div class="tabsbar">
                <button data-view="active" class="active">Active</button>
                <button data-view="done">Completed</button>
                <button data-view="media">Detected media <span id="mediaCount" class="pill">0</span></button>
              </div>

              <div id="listActive"></div>
              <div id="listDone" class="hidden"></div>
              <div id="listMedia" class="hidden">
                <div class="card">
                  <div class="row between"><h2 style="margin:0">Streams sniffed from open tabs</h2>
                    <button id="mediaClear">Clear list</button></div>
                  <table id="mediaTable">
                    <thead><tr><th>Kind</th><th>Stream</th><th>Size</th><th>Found on</th><th></th></tr></thead>
                    <tbody></tbody>
                  </table>
                </div>
              </div>
            </div>
            """;

        public const string ExtraCss = """
            .logoBadge { width:40px; height:40px; border-radius:12px; display:flex; align-items:center;
                         justify-content:center; font-size:1.3em; background: var(--accent-soft); color: var(--accent); }
            .task { margin-bottom: 14px; }
            .task .head { display:flex; gap:10px; align-items:center; }
            .task .name { font-weight:600; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
            .task .url { color:var(--muted); font-size:.8em; overflow:hidden; text-overflow:ellipsis;
                         white-space:nowrap; max-width: 60%; }
            .task .meta { color:var(--muted); font-size:.82em; display:flex; gap:14px; margin-top:6px; flex-wrap:wrap; }
            .task .acts { margin-left:auto; display:flex; gap:6px; }
            .state { font-size:.75em; text-transform:uppercase; letter-spacing:.5px; font-weight:700; }
            .state.Downloading { color: var(--accent); }
            .state.Paused { color: var(--warn); }
            .state.Failed { color: var(--danger); }
            .state.Completed { color: var(--ok); }
            .state.Queued, .state.Starting { color: var(--muted); }
            input[type=range] { accent-color: var(--accent); width: 150px; }
            .sha { font-family: Consolas, monospace; font-size:.72em; color:var(--muted); word-break: break-all; }
            """;

        public const string InitJs = """
            var STATE = __STATE__;
            var view = 'active';
            document.querySelectorAll('.tabsbar button').forEach(function (b) {
              b.addEventListener('click', function () {
                view = b.getAttribute('data-view');
                document.querySelectorAll('.tabsbar button').forEach(function (x) { x.classList.remove('active'); });
                b.classList.add('active');
                document.getElementById('listActive').classList.toggle('hidden', view !== 'active');
                document.getElementById('listDone').classList.toggle('hidden', view !== 'done');
                document.getElementById('listMedia').classList.toggle('hidden', view !== 'media');
                refresh();
              });
            });

            function segBar(segs) {
              if (!segs || !segs.length) return '';
              var html = '<div class="seg" title="' + segs.length + ' parallel streams">';
              segs.forEach(function (s) {
                var cls = s.st === 'done' ? ' done' : (s.st === 'error' ? ' err' : '');
                var w = Math.max(0, Math.min(1, s.p)) * 100;
                html += '<div class="' + cls.trim() + '"><i style="transform:scaleX(' + (w / 100) + ')"></i></div>';
              });
              return html + '</div>';
            }

            function taskCard(t) {
              var pct = Math.round(t.progress * 100);
              var acts = '';
              if (t.state === 'Downloading' || t.state === 'Starting')
                acts += '<button data-a="pause" data-id="' + t.id + '">Pause</button>';
              if (t.state === 'Paused' || t.state === 'Failed' || t.state === 'Cancelled')
                acts += '<button data-a="resume" data-id="' + t.id + '">Resume</button>';
              if (t.state === 'Queued')
                acts += '<button data-a="startNow" data-id="' + t.id + '">Start now</button>';
              if (t.state === 'Downloading' || t.state === 'Starting' || t.state === 'Queued' || t.state === 'Paused')
                acts += '<button data-a="cancel" data-id="' + t.id + '">Cancel</button>';
              if (t.state === 'Completed') {
                acts += '<button data-a="open" data-id="' + t.id + '">Open</button>';
                acts += '<button data-a="showFolder" data-id="' + t.id + '">Folder</button>';
              }
              if (t.state === 'Failed') acts += '<button data-a="retry" data-id="' + t.id + '" title="Retry and re-stitch existing segments">Re-stitch / retry</button>';
              acts += '<button data-a="copyUrl" data-id="' + t.id + '" title="Copy URL">&#128203;</button>';
              acts += '<button class="danger" data-a="remove" data-id="' + t.id + '" title="Remove">&#10005;</button>';
              var meta = '<span>' + Swifter.fmtBytes(t.received) + ' / ' + Swifter.fmtBytes(t.total) + '</span>' +
                '<span>' + pct + '%</span>' +
                '<span>' + Swifter.fmtSpeed(t.speed) + '</span>' +
                '<span>ETA ' + Swifter.fmtEta(t.eta) + '</span>';
              if (t.hls) meta += '<span>HLS ' + t.hlsDone + '/' + t.hlsTotal + '</span>';
              if (!t.ranges && t.state !== 'Completed') meta += '<span class="pill">single stream (no Range support)</span>';
              var err = t.error ? '<div class="state Failed">' + Swifter.esc(t.error) + '</div>' : '';
              var sha = (t.sha && t.state === 'Completed') ? '<div class="sha">sha256:' + Swifter.esc(t.sha) + '</div>' : '';
              return '<div class="card task">' +
                '<div class="head"><div class="grow" style="min-width:0">' +
                '<div class="name">' + Swifter.esc(t.file) + ' <span class="state ' + t.state + '">' + t.state + '</span></div>' +
                '<div class="url">' + Swifter.esc(t.url) + '</div></div>' +
                '<div class="acts">' + acts + '</div></div>' +
                (t.hls ? '<div class="bar-track" style="margin-top:10px"><div class="bar-fill" style="width:' + pct + '%"></div></div>'
                       : segBar(t.segments)) +
                '<div class="meta">' + meta + '</div>' + err + sha + '</div>';
            }

            function renderTasks(snap) {
              var active = snap.tasks.filter(function (t) {
                return t.state !== 'Completed' && t.state !== 'Cancelled';
              });
              var done = snap.tasks.filter(function (t) {
                return t.state === 'Completed' || t.state === 'Cancelled';
              });
              document.getElementById('listActive').innerHTML = active.length
                ? active.map(taskCard).join('')
                : '<div class="card empty"><div class="big">&#8681;</div>No active downloads.<br>' +
                  '<span class="small">Links you copy, media the sniffer finds and every browser download land here.</span></div>';
              document.getElementById('listDone').innerHTML = done.length
                ? done.map(taskCard).join('')
                : '<div class="card empty"><div class="big">&#10003;</div>Nothing finished yet.</div>';
              document.getElementById('aggSpeed').textContent = Swifter.fmtSpeed(snap.aggregate);
              document.getElementById('activeCount').textContent = snap.active;
              document.getElementById('throttleLabel').textContent =
                snap.throttle > 0 ? (snap.throttle >= 1024 ? (snap.throttle / 1024) + ' MB/s' : snap.throttle + ' KB/s') : 'unlimited';
              bindActions();
            }

            function bindActions() {
              document.querySelectorAll('[data-a]').forEach(function (b) {
                b.onclick = function () {
                  var a = b.getAttribute('data-a'), id = b.getAttribute('data-id');
                  var payload = { id: id };
                  if (a === 'remove') payload.deleteFile = false;
                  Swifter.call('downloads.' + a, payload).catch(function (e) { Swifter.toast(e.message, true); });
                };
              });
            }

            function renderMedia(items) {
              document.getElementById('mediaCount').textContent = items.length;
              var tbody = document.querySelector('#mediaTable tbody');
              if (!items.length) {
                tbody.innerHTML = '<tr><td colspan="5" class="muted">No streams detected yet. Play a video on any tab and it shows up here.</td></tr>';
                return;
              }
              tbody.innerHTML = items.map(function (m) {
                return '<tr><td><span class="chip">' + m.kind + '</span></td>' +
                  '<td style="max-width:340px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title="' + Swifter.esc(m.url) + '">' +
                  Swifter.esc(m.url) + (m.hint ? ' <span class="pill">' + Swifter.esc(m.hint) + '</span>' : '') + '</td>' +
                  '<td>' + Swifter.fmtBytes(m.size) + '</td>' +
                  '<td class="muted small">' + Swifter.esc(m.page || m.pageUrl) + '</td>' +
                  '<td><button class="primary" data-media="' + Swifter.esc(m.url) + '" data-kind="' + m.kind + '">Download</button></td></tr>';
              }).join('');
              tbody.querySelectorAll('[data-media]').forEach(function (b) {
                b.onclick = function () {
                  var url = b.getAttribute('data-media');
                  var kind = b.getAttribute('data-kind');
                  var name = url.split('/').pop().split('?')[0] || 'stream';
                  Swifter.call('downloads.enqueue', {
                    url: url, fileName: name,
                    hls: kind === 'Playlist', referrer: '', source: 'media sniffer'
                  }).then(function () {
                    Swifter.toast('Queued ' + name);
                    view = 'active';
                    document.querySelector('[data-view="active"]').click();
                  });
                };
              });
            }

            function refresh() {
              Swifter.call('downloads.snapshot').then(function (snap) {
                document.getElementById('threads').value = snap.threads;
                document.getElementById('threadsVal').textContent = snap.threads;
                document.getElementById('maxActive').value = snap.maxActive;
                document.getElementById('throttle').value = String(snap.throttle);
                document.getElementById('savePath').value = snap.path;
                document.getElementById('schedOn').checked = snap.scheduler;
                document.getElementById('schedStart').value = snap.scheduleStart;
                document.getElementById('schedEnd').value = snap.scheduleEnd;
                document.getElementById('schedState').textContent = snap.inWindow ? 'window open' : 'outside window';
                renderTasks(snap);
                if (view === 'media') {
                  Swifter.call('media.list', {}).then(renderMedia);
                } else {
                  Swifter.call('media.list', {}).then(function (i) {
                    document.getElementById('mediaCount').textContent = i.length;
                  });
                }
              }).catch(function (e) { console.error(e); });
            }
            setInterval(refresh, 700);
            refresh();

            document.getElementById('threads').addEventListener('input', Swifter.debounce(function () {
              Swifter.call('downloads.setThreads', { n: Number(this.value) });
            }, 250));
            document.getElementById('maxActive').addEventListener('change', function () {
              Swifter.call('downloads.setMaxActive', { n: Number(this.value) });
            });
            document.getElementById('throttle').addEventListener('change', function () {
              Swifter.call('downloads.setThrottle', { kib: Number(this.value) });
            });
            document.getElementById('clearDone').addEventListener('click', function () {
              Swifter.call('downloads.clearCompleted').then(refresh);
            });
            document.getElementById('openFolder').addEventListener('click', function () {
              Swifter.call('downloads.snapshot').then(function (s) {
                Swifter.call('clipboard.copy', { text: s.path });
                Swifter.toast('Download folder path copied');
              });
            });
            document.getElementById('pickPath').addEventListener('click', function () {
              Swifter.call('pickFolder').then(function (path) {
                if (!path) return;
                return Swifter.call('downloads.setPath', { path: path });
              }).then(refresh);
            });
            document.getElementById('mediaClear').addEventListener('click', function () {
              Swifter.call('media.clear').then(refresh);
            });
            document.getElementById('schedOn').addEventListener('change', pushSched);
            document.getElementById('schedStart').addEventListener('change', pushSched);
            document.getElementById('schedEnd').addEventListener('change', pushSched);
            function pushSched() {
              Swifter.call('downloads.scheduler', {
                enabled: document.getElementById('schedOn').checked,
                start: document.getElementById('schedStart').value || '22:00',
                end: document.getElementById('schedEnd').value || '06:00'
              });
            }
            """;
    }
}
