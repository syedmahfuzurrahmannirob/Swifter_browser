using System.Collections.Generic;
using Swifter.Config;

namespace Swifter.Protocols.InternalPages
{
    /// <summary>swifter://history - FTS5 search, timeline, SVG analytics, export.</summary>
    public static class HistoryPage
    {
        public static string Render(SettingsManager settings, Dictionary<string, string> query)
        {
            Dictionary<string, object> state = new Dictionary<string, object>
            {
                { "accent", SettingsManager.ToHex(settings.Accent()) },
                { "muted", settings.IsDarkTheme() ? "#98a4b3" : "#5c6b7a" },
                { "q", query.ContainsKey("q") ? query["q"] : "" }
            };
            string init = InitJs.Replace("__STATE__", ProtocolPageRenderer.ToJson(state));
            return ProtocolPageRenderer.Page("History", BodyHtml, init, ExtraCss, settings);
        }

        public const string BodyHtml = """
            <div class="wrap">
              <header class="page">
                <span class="logoBadge">&#9719;</span>
                <div><h1>History</h1><div class="sub"><span id="totalCount">0</span> recorded visits, full-text indexed</div></div>
              </header>

              <div class="card">
                <div class="row">
                  <input id="q" type="search" class="grow" placeholder="Full-text search titles, URLs and descriptions (FTS5)">
                  <label class="muted small">From</label><input id="from" type="date">
                  <label class="muted small">To</label><input id="to" type="date">
                  <button id="applyRange">Apply</button>
                  <button id="resetRange">All time</button>
                </div>
                <div class="row" style="margin-top:10px">
                  <button id="expCsv">Export CSV</button>
                  <button id="expJson">Export JSON</button>
                  <span class="grow"></span>
                  <button class="danger" id="clearAll">Clear all browsing data</button>
                </div>
              </div>

              <div class="grid" style="grid-template-columns: 2fr 1fr;">
                <div class="card">
                  <h2>Visit density (last 30 days)</h2>
                  <div id="dailyChart"></div>
                  <h2 style="margin-top:18px">Peak browsing hours</h2>
                  <div id="hourChart"></div>
                </div>
                <div class="card">
                  <h2>Top domains</h2>
                  <div id="domains"></div>
                </div>
              </div>

              <div class="card">
                <h2>Timeline</h2>
                <div id="timeline"></div>
              </div>
            </div>
            """;

        public const string ExtraCss = """
            .logoBadge { width:40px; height:40px; border-radius:12px; display:flex; align-items:center;
                         justify-content:center; font-size:1.3em; background: var(--accent-soft); color: var(--accent); }
            .day { margin-bottom: 18px; }
            .day h3 { margin: 0 0 8px; font-size: .95em; color: var(--muted); }
            .entry { display:flex; gap:10px; align-items:center; padding:7px 8px; border-radius:9px; }
            .entry:hover { background: var(--hover); }
            .entry img { width:18px; height:18px; border-radius:5px; flex:none; }
            .entry .tt { font-weight:600; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
            .entry .uu { color:var(--muted); font-size:.8em; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
            .entry .tm { color:var(--muted); font-size:.78em; flex:none; }
            .entry .del { opacity:0; }
            .entry:hover .del { opacity:1; }
            .dom { display:flex; align-items:center; gap:10px; margin-bottom:9px; }
            .dom .n { width: 130px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-size:.9em; }
            .dom .bar-track { flex:1; }
            .dom .c { width: 46px; text-align:right; color:var(--muted); font-size:.85em; }
            .dom button { opacity:0; padding:2px 7px; }
            .dom:hover button { opacity:1; }
            """;

        public const string InitJs = """
            var STATE = __STATE__;
            var range = { from: 0, to: 0 };

            function dayKey(ms) {
              var d = new Date(ms);
              return d.toLocaleDateString([], { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
            }

            function renderTimeline(rows) {
              var box = document.getElementById('timeline');
              if (!rows.length) {
                box.innerHTML = '<div class="empty"><div class="big">&#128337;</div>No history matches.</div>';
                return;
              }
              var groups = {};
              var order = [];
              rows.forEach(function (r) {
                var k = dayKey(r.at);
                if (!groups[k]) { groups[k] = []; order.push(k); }
                groups[k].push(r);
              });
              var html = '';
              order.forEach(function (k) {
                html += '<div class="day"><h3>' + Swifter.esc(k) + '</h3>';
                groups[k].forEach(function (r) {
                  var t = new Date(r.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
                  html += '<div class="entry">' +
                    '<img src="' + Swifter.esc(r.icon) + '">' +
                    '<div class="grow" style="min-width:0"><div class="tt">' + Swifter.esc(r.title || r.url) + '</div>' +
                    '<div class="uu">' + Swifter.esc(r.url) + '</div></div>' +
                    '<span class="tm">' + t + (r.duration > 0 ? ' &middot; ' + Math.max(1, Math.round(r.duration / 1000)) + 's' : '') + '</span>' +
                    '<button class="ghost del" data-del="' + r.id + '" title="Delete">&#10005;</button>' +
                    '<button class="ghost del" data-dom="' + Swifter.esc(r.domain) + '" title="Delete all from this domain">&#128465;</button>' +
                    '</div>';
                });
                html += '</div>';
              });
              box.innerHTML = html;
                            box.querySelectorAll('[data-del]').forEach(function (b) {
                b.addEventListener('click', function () {
                  Swifter.call('history.delete', { id: Number(b.getAttribute('data-del')) }).then(load);
                });
              });
              box.querySelectorAll('[data-dom]').forEach(function (b) {
                b.addEventListener('click', function () {
                  var d = b.getAttribute('data-dom');
                  if (confirm('Delete every visit to ' + d + '?')) {
                    Swifter.call('history.deleteDomain', { domain: d }).then(load);
                  }
                });
              });
              box.querySelectorAll('.entry').forEach(function (el) {
                el.addEventListener('dblclick', function () {
                  var u = el.querySelector('.uu').textContent;
                  Swifter.call('navigate', { url: u });
                });
              });
            }

            function renderCharts(stats) {
              document.getElementById('totalCount').textContent = stats.total;
              document.getElementById('dailyChart').innerHTML = STATS_CHARTS.daily(stats.daily);
              document.getElementById('hourChart').innerHTML = STATS_CHARTS.hourly(stats.hourly);
              var max = 1;
              (stats.domains || []).forEach(function (d) { if (d.visits > max) max = d.visits; });
              document.getElementById('domains').innerHTML = (stats.domains || []).map(function (d) {
                return '<div class="dom"><span class="n" title="' + Swifter.esc(d.domain) + '">' + Swifter.esc(d.domain) + '</span>' +
                  '<div class="bar-track"><div class="bar-fill" style="width:' + Math.round(d.visits / max * 100) + '%"></div></div>' +
                  '<span class="c">' + d.visits + '</span>' +
                  '<button class="ghost" data-dom="' + Swifter.esc(d.domain) + '" title="Purge domain">&#128465;</button></div>';
              }).join('') || '<div class="muted small">No data yet.</div>';
              document.getElementById('domains').querySelectorAll('[data-dom]').forEach(function (b) {
                b.addEventListener('click', function () {
                  var d = b.getAttribute('data-dom');
                  if (confirm('Delete every visit to ' + d + '?')) {
                    Swifter.call('history.deleteDomain', { domain: d }).then(load);
                  }
                });
              });
            }

            function svgBars(points, h) {
              if (!points || !points.length) return '';
              var max = 1;
              points.forEach(function (p) { if (p.value > max) max = p.value; });
              var w = 600, slot = w / points.length, bw = Math.max(2, slot * 0.6);
              var s = '<svg viewBox="0 0 ' + w + ' ' + h + '" width="100%" height="' + h + '" preserveAspectRatio="none">';
              points.forEach(function (p, i) {
                var hh = p.value === 0 ? 0 : Math.max(2, p.value / max * (h - 6));
                var x = i * slot + (slot - bw) / 2, y = h - 2 - hh;
                s += '<rect x="' + x.toFixed(1) + '" y="' + y.toFixed(1) + '" width="' + bw.toFixed(1) +
                     '" height="' + hh.toFixed(1) + '" rx="2" fill="' + STATE.accent + '"><title>' +
                     Swifter.esc(p.key) + ': ' + p.value + '</title></rect>';
              });
              return s + '</svg>';
            }

            var STATS_CHARTS = {
              daily: function (daily) { return svgBars(daily, 120); },
              hourly: function (hourly) { return svgBars(hourly, 90); }
            };

            function load() {
              var q = document.getElementById('q').value.trim();
              var p;
              if (q) p = Swifter.call('history.search', { q: q, limit: 120 });
              else if (range.from || range.to)
                p = Swifter.call('history.range', { from: range.from, to: range.to || Date.now() + 86400000, limit: 300 });
              else p = Swifter.call('history.range', { from: 0, to: 9e15, limit: 300 });
              p.then(renderTimeline);
              Swifter.call('history.stats', { days: 30 }).then(renderCharts);
            }

            document.getElementById('q').addEventListener('input', Swifter.debounce(load, 260));
            if (STATE.q) document.getElementById('q').value = STATE.q;
            document.getElementById('applyRange').addEventListener('click', function () {
              var f = document.getElementById('from').value, t = document.getElementById('to').value;
              range.from = f ? new Date(f + 'T00:00:00').getTime() : 0;
              range.to = t ? new Date(t + 'T23:59:59').getTime() : 0;
              load();
            });
            document.getElementById('resetRange').addEventListener('click', function () {
              range = { from: 0, to: 0 };
              document.getElementById('from').value = '';
              document.getElementById('to').value = '';
              load();
            });
            document.getElementById('expCsv').addEventListener('click', function () {
              Swifter.call('history.export', { format: 'csv' }).then(function (p) { Swifter.toast('Exported to ' + p); });
            });
            document.getElementById('expJson').addEventListener('click', function () {
              Swifter.call('history.export', { format: 'json' }).then(function (p) { Swifter.toast('Exported to ' + p); });
            });
            document.getElementById('clearAll').addEventListener('click', function () {
              if (confirm('Delete ALL history, favicons and the search index?')) {
                Swifter.call('history.clear').then(load);
              }
            });
            load();
            """;
    }
}
