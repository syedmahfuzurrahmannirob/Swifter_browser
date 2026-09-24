using System.Collections.Generic;
using Swifter.Config;

namespace Swifter.Protocols.InternalPages
{
    /// <summary>swifter://bookmarks - tree manager, tags, Netscape import/export.</summary>
    public static class BookmarksPage
    {
        public static string Render(SettingsManager settings)
        {
            Dictionary<string, object> state = new Dictionary<string, object>
            {
                { "accent", SettingsManager.ToHex(settings.Accent()) }
            };
            string init = InitJs.Replace("__STATE__", ProtocolPageRenderer.ToJson(state));
            return ProtocolPageRenderer.Page("Bookmarks", BodyHtml, init, ExtraCss, settings);
        }

        public const string BodyHtml = """
            <div class="wrap">
              <header class="page">
                <span class="logoBadge">&#9733;</span>
                <div><h1>Bookmarks</h1><div class="sub">Hierarchical, tagged, Netscape-compatible</div></div>
                <span class="grow"></span>
                <input id="search" type="search" placeholder="Search bookmarks" style="width:220px">
              </header>

              <div class="card">
                <h2>Quick launch</h2>
                <div id="launch" class="launch"></div>
              </div>

              <div class="row" style="margin-bottom:14px">
                <button class="primary" id="addLink">+ Bookmark</button>
                <button id="addFolder">+ Folder</button>
                <button id="importBtn">Import HTML</button>
                <button id="exportBtn">Export HTML</button>
                <span class="grow"></span>
                <span class="chip" id="favFilter">&#9733; favourites</span>
                <span class="chip" id="barFilter">bookmarks bar</span>
              </div>
              <div id="tagBar" class="row" style="margin-bottom:14px"></div>

              <div class="grid" style="grid-template-columns: 260px 1fr;">
                <div class="card tree" id="tree"></div>
                <div class="card">
                  <div class="row between" style="margin-bottom:10px">
                    <h2 id="crumb" style="margin:0">Bookmarks</h2>
                    <span class="muted small" id="countLabel"></span>
                  </div>
                  <div id="items"></div>
                </div>
              </div>
            </div>

            <dialog id="editDlg">
              <h3 id="editTitle">Bookmark</h3>
              <label class="lab">Title</label><input id="eTitle" type="text" style="width:100%">
              <label class="lab">URL</label><input id="eUrl" type="text" style="width:100%">
              <label class="lab">Tags (comma separated)</label><input id="eTags" type="text" style="width:100%">
              <div class="row" style="margin-top:8px">
                <label><input id="eFav" type="checkbox"> favourite</label>
                <label><input id="eBar" type="checkbox"> show in bookmarks bar</label>
              </div>
              <div class="row" style="margin-top:16px">
                <button class="primary" id="eSave">Save</button>
                <button id="eCancel">Cancel</button>
                <span class="grow"></span>
                <button class="danger" id="eDelete">Delete</button>
              </div>
            </dialog>
            """;

        public const string ExtraCss = """
            .logoBadge { width:40px; height:40px; border-radius:12px; display:flex; align-items:center;
                         justify-content:center; font-size:1.3em; background: var(--accent-soft); color: var(--accent); }
            .launch { display:grid; grid-template-columns: repeat(auto-fill, minmax(120px,1fr)); gap:10px; }
            .launch .l { display:flex; gap:8px; align-items:center; padding:8px 10px; border:1px solid var(--border);
                         border-radius:10px; cursor:pointer; overflow:hidden; }
            .launch .l:hover { border-color: var(--accent); }
            .launch img { width:18px; height:18px; border-radius:4px; }
            .launch .n { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-size:.88em; }
            .tree .node { padding:5px 8px; border-radius:8px; cursor:pointer; display:flex; gap:7px; align-items:center; }
            .tree .node:hover { background: var(--hover); }
            .tree .node.sel { background: var(--accent-soft); color: var(--accent); font-weight:600; }
            .tree .node.drop { outline: 2px dashed var(--accent); }
            .item { display:flex; gap:10px; align-items:center; padding:8px; border-radius:9px; cursor:grab; }
            .item:hover { background: var(--hover); }
            .item.drop { outline:2px dashed var(--accent); }
            .item img { width:18px; height:18px; border-radius:4px; }
            .item .t { font-weight:600; }
            .item .u { color:var(--muted); font-size:.8em; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
            .item .acts { margin-left:auto; display:flex; gap:5px; opacity:0; }
            .item:hover .acts { opacity:1; }
            .chip.on { background: var(--accent); color:#06131c; border-color: var(--accent); font-weight:600; }
            .lab { display:block; margin:10px 0 4px; color:var(--muted); font-size:.85em; }
            """;

        public const string InitJs = """
            var STATE = __STATE__;
            var TREE = { root: '', nodes: [] };
            var currentFolder = '';
            var filterTag = '';
            var filterFav = false;
            var filterBar = false;
            var editingId = '';
            var dragId = '';

            function nodeById(id) {
              for (var i = 0; i < TREE.nodes.length; i++) if (TREE.nodes[i].id === id) return TREE.nodes[i];
              return null;
            }
            function childrenOf(id) {
              var kids = TREE.nodes.filter(function (n) { return n.parent === id; });
              kids.sort(function (a, b) { return (b.folder ? 1 : 0) - (a.folder ? 1 : 0) || a.order - b.order; });
              return kids;
            }

            function renderTree() {
              var box = document.getElementById('tree');
              box.innerHTML = '<h2 style="margin-top:0">Folders</h2>';
              function walk(id, depth) {
                childrenOf(id).filter(function (n) { return n.folder; }).forEach(function (n) {
                  var el = document.createElement('div');
                  el.className = 'node' + (n.id === currentFolder ? ' sel' : '');
                  el.style.marginLeft = (depth * 14) + 'px';
                  el.innerHTML = '<span>&#128193;</span><span class="grow">' + Swifter.esc(n.title) + '</span>';
                  el.addEventListener('click', function () { currentFolder = n.id; renderTree(); renderItems(); });
                  el.addEventListener('dragover', function (ev) { ev.preventDefault(); el.classList.add('drop'); });
                  el.addEventListener('dragleave', function () { el.classList.remove('drop'); });
                  el.addEventListener('drop', function (ev) {
                    ev.preventDefault(); el.classList.remove('drop');
                    if (dragId) Swifter.call('bookmarks.move', { id: dragId, parentId: n.id, index: -1 }).then(load);
                  });
                  box.appendChild(el);
                  walk(n.id, depth + 1);
                });
              }
              var rootEl = document.createElement('div');
              rootEl.className = 'node' + (currentFolder === TREE.root ? ' sel' : '');
              rootEl.innerHTML = '<span>&#127968;</span><span class="grow">All bookmarks</span>';
              rootEl.addEventListener('click', function () { currentFolder = TREE.root; renderTree(); renderItems(); });
              box.appendChild(rootEl);
              walk(TREE.root, 0);
            }

            function itemRow(n) {
              var el = document.createElement('div');
              el.className = 'item';
              el.draggable = true;
              el.innerHTML = (n.folder ? '<span style="font-size:1.1em">&#128193;</span>'
                                       : '<img src="' + Swifter.esc(n.icon) + '">') +
                '<div class="grow" style="min-width:0"><div class="t">' + Swifter.esc(n.title) +
                (n.favorite ? ' <span style="color:var(--warn)">&#9733;</span>' : '') + '</div>' +
                (n.folder ? '<div class="u">folder &middot; ' + childrenOf(n.id).length + ' items</div>'
                          : '<div class="u">' + Swifter.esc(n.url) + '</div>') + '</div>' +
                (n.tags || []).map(function (t) { return '<span class="chip">' + Swifter.esc(t) + '</span>'; }).join('') +
                '<div class="acts"><button class="ghost" data-edit title="Edit">&#9998;</button>' +
                '<button class="ghost" data-open title="Open">&#8599;</button></div>';
              el.addEventListener('dragstart', function () { dragId = n.id; });
              el.addEventListener('dragover', function (ev) { ev.preventDefault(); el.classList.add('drop'); });
              el.addEventListener('dragleave', function () { el.classList.remove('drop'); });
              el.addEventListener('drop', function (ev) {
                ev.preventDefault(); el.classList.remove('drop');
                if (!dragId || dragId === n.id) return;
                var kids = childrenOf(currentFolder);
                var idx = kids.findIndex(function (k) { return k.id === n.id; });
                Swifter.call('bookmarks.move', { id: dragId, parentId: currentFolder, index: idx }).then(load);
              });
              el.querySelector('[data-edit]').addEventListener('click', function (ev) {
                ev.stopPropagation(); openEdit(n.id);
              });
              el.querySelector('[data-open]').addEventListener('click', function (ev) {
                ev.stopPropagation();
                if (n.folder) { currentFolder = n.id; renderTree(); renderItems(); }
                else Swifter.call('navigate', { url: n.url });
              });
              el.addEventListener('dblclick', function () {
                if (!n.folder) Swifter.call('navigate', { url: n.url });
              });
              return el;
            }

            function renderItems() {
              var box = document.getElementById('items');
              box.innerHTML = '';
              var kids = childrenOf(currentFolder);
              var crumb = nodeById(currentFolder);
              document.getElementById('crumb').textContent = crumb ? crumb.title : 'All bookmarks';
              document.getElementById('countLabel').textContent = kids.length + ' items';
              if (!kids.length) box.innerHTML = '<div class="empty"><div class="big">&#128193;</div>Empty folder.<br>' +
                '<span class="small">Drag bookmarks here or press "+ Bookmark".</span></div>';
              kids.forEach(function (n) { box.appendChild(itemRow(n)); });
              box.ondragover = function (ev) { ev.preventDefault(); };
              box.ondrop = function (ev) {
                ev.preventDefault();
                if (dragId) Swifter.call('bookmarks.move', { id: dragId, parentId: currentFolder, index: -1 }).then(load);
              };
            }

            function openEdit(id) {
              editingId = id;
              var n = id ? nodeById(id) : null;
              document.getElementById('editTitle').textContent = n ? (n.folder ? 'Folder' : 'Bookmark') : 'New bookmark';
              document.getElementById('eTitle').value = n ? n.title : '';
              document.getElementById('eUrl').value = n ? n.url : '';
              document.getElementById('eTags').value = n ? (n.tags || []).join(', ') : '';
              document.getElementById('eFav').checked = n ? n.favorite : false;
              document.getElementById('eBar').checked = n ? n.bar : false;
              document.getElementById('eUrl').disabled = !!(n && n.folder);
              document.getElementById('eDelete').style.display = n ? '' : 'none';
              document.getElementById('editDlg').showModal();
            }

            document.getElementById('addLink').addEventListener('click', function () { openEdit(''); });
            document.getElementById('addFolder').addEventListener('click', function () {
              var title = prompt('Folder name');
              if (!title) return;
              Swifter.call('bookmarks.addFolder', { parentId: currentFolder, title: title }).then(load);
            });
            document.getElementById('eCancel').addEventListener('click', function () {
              document.getElementById('editDlg').close();
            });
            document.getElementById('eSave').addEventListener('click', function () {
              var title = document.getElementById('eTitle').value.trim();
              var url = document.getElementById('eUrl').value.trim();
              var tags = document.getElementById('eTags').value.split(',').map(function (s) { return s.trim(); })
                .filter(function (s) { return s.length; });
              var fav = document.getElementById('eFav').checked;
              var bar = document.getElementById('eBar').checked;
              var p;
              if (editingId) {
                p = Swifter.call('bookmarks.update', { id: editingId, title: title, url: url, tags: tags, favorite: fav, bar: bar });
              } else {
                p = Swifter.call('bookmarks.add', { parentId: currentFolder, title: title || url, url: url, tags: tags })
                  .then(function (r) {
                    return Swifter.call('bookmarks.update', { id: r.id, favorite: fav, bar: bar });
                  });
              }
              p.then(function () { document.getElementById('editDlg').close(); load(); });
            });
            document.getElementById('eDelete').addEventListener('click', function () {
              if (!editingId) return;
              if (confirm('Delete this item and everything inside it?')) {
                Swifter.call('bookmarks.delete', { id: editingId }).then(function () {
                  document.getElementById('editDlg').close(); load();
                });
              }
            });

            function renderTags() {
              Swifter.call('bookmarks.tags').then(function (tags) {
                var bar = document.getElementById('tagBar');
                bar.innerHTML = '';
                tags.forEach(function (t) {
                  var c = document.createElement('span');
                  c.className = 'chip' + (filterTag === t ? ' on' : '');
                  c.textContent = '#' + t;
                  c.addEventListener('click', function () {
                    filterTag = filterTag === t ? '' : t;
                    renderTags(); renderFiltered();
                  });
                  bar.appendChild(c);
                });
              });
            }

            function renderFiltered() {
              var box = document.getElementById('items');
              var p;
              if (filterTag) p = Swifter.call('bookmarks.byTag', { tag: filterTag });
              else if (filterFav) p = Swifter.call('bookmarks.favorites');
              else if (filterBar) p = Swifter.call('bookmarks.bar');
              else { renderItems(); return; }
              p.then(function (rows) {
                box.innerHTML = '';
                if (!rows.length) box.innerHTML = '<div class="empty">Nothing here.</div>';
                rows.forEach(function (n) { box.appendChild(itemRow(n)); });
              });
            }

            document.getElementById('favFilter').addEventListener('click', function () {
              filterFav = !filterFav; filterBar = false; filterTag = '';
              document.getElementById('favFilter').classList.toggle('on', filterFav);
              document.getElementById('barFilter').classList.remove('on');
              renderFiltered();
            });
            document.getElementById('barFilter').addEventListener('click', function () {
              filterBar = !filterBar; filterFav = false; filterTag = '';
              document.getElementById('barFilter').classList.toggle('on', filterBar);
              document.getElementById('favFilter').classList.remove('on');
              renderFiltered();
            });
            document.getElementById('search').addEventListener('input', Swifter.debounce(function () {
              var q = this.value.trim();
              if (!q) { renderItems(); return; }
              Swifter.call('bookmarks.search', { q: q }).then(function (rows) {
                var box = document.getElementById('items');
                box.innerHTML = '';
                if (!rows.length) box.innerHTML = '<div class="empty">No matches.</div>';
                rows.forEach(function (n) { box.appendChild(itemRow(n)); });
              });
            }, 220));

            document.getElementById('importBtn').addEventListener('click', function () {
              Swifter.call('bookmarks.import', {}).then(function (n) {
                Swifter.toast('Imported ' + n + ' bookmarks');
                load();
              });
            });
            document.getElementById('exportBtn').addEventListener('click', function () {
              Swifter.call('bookmarks.export', {}).then(function (p) { Swifter.toast('Exported to ' + p); });
            });

            function renderLaunch() {
              Swifter.call('bookmarks.top', {}).then(function (rows) {
                var box = document.getElementById('launch');
                if (!rows.length) { box.innerHTML = '<div class="muted small">Launch your bookmarks a few times and the most used ones appear here.</div>'; return; }
                box.innerHTML = rows.map(function (n) {
                  return '<div class="l" data-id="' + n.id + '" data-url="' + Swifter.esc(n.url) + '">' +
                    '<img src="' + Swifter.esc(n.icon) + '"><span class="n">' + Swifter.esc(n.title) + '</span></div>';
                }).join('');
                box.querySelectorAll('.l').forEach(function (el) {
                  el.addEventListener('click', function () {
                    Swifter.call('bookmarks.touch', { id: el.getAttribute('data-id') });
                    Swifter.call('navigate', { url: el.getAttribute('data-url') });
                  });
                });
              });
            }

            function load() {
              Swifter.call('bookmarks.tree').then(function (t) {
                TREE = t;
                if (!currentFolder) currentFolder = t.root;
                renderTree();
                if (!filterTag && !filterFav && !filterBar) renderItems(); else renderFiltered();
              });
              renderTags();
              renderLaunch();
            }
            load();
            """;
    }
}
