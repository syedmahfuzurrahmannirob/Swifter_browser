using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Swifter.Config;

namespace Swifter.Storage
{
    public sealed class BookmarkNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ParentId { get; set; } = "";
        public bool IsFolder { get; set; }
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public List<string> Tags { get; set; } = new List<string>();
        public string Favicon { get; set; } = "";
        public long DateAdded { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public long LastUsed { get; set; }
        public int UseCount { get; set; }
        public int Order { get; set; }
        public bool IsFavorite { get; set; }
        public bool InBar { get; set; }
    }

    public sealed class BookmarkStore
    {
        public string RootId { get; set; } = "";
        public List<BookmarkNode> Nodes { get; set; } = new List<BookmarkNode>();
    }

    /// <summary>
    /// Hierarchical bookmark store persisted as JSON. Supports folders, tags,
    /// favourites, bookmark-bar sync and full Netscape HTML import/export
    /// (Chrome / Firefox / Edge / Brave compatible).
    /// </summary>
    public sealed class BookmarksDatabase
    {
        private readonly object _lock = new object();
        private BookmarkStore _store = new BookmarkStore();
        private string _path;

        public event Action Changed;

        public static BookmarksDatabase Load(string path)
        {
            BookmarksDatabase db = new BookmarksDatabase();
            db._path = path;
            try
            {
                if (File.Exists(path))
                {
                    BookmarkStore s = JsonSerializer.Deserialize<BookmarkStore>(
                        File.ReadAllText(path), SettingsManager.JsonOptions);
                    if (s != null && s.Nodes != null) db._store = s;
                }
            }
            catch (Exception ex)
            {
                Swifter.Log.Error("bookmarks load failed: " + ex.Message);
            }
            db.Seed();
            return db;
        }

        private void Seed()
        {
            lock (_lock)
            {
                if (_store.RootId == "" || Find(_store.RootId) == null)
                {
                    BookmarkNode root = new BookmarkNode { Id = "root", ParentId = "", IsFolder = true, Title = "Bookmarks" };
                    _store.RootId = root.Id;
                    _store.Nodes.Insert(0, root);
                }
                if (_store.Nodes.Count <= 1)
                {
                    BookmarkNode bar = new BookmarkNode
                    {
                        Id = "bar", ParentId = "root", IsFolder = true, Title = "Bookmarks bar", InBar = true
                    };
                    _store.Nodes.Add(bar);
                    Add("bar", "Swifter Home", "https://github.com", new List<string> { "swifter" });
                }
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    string tmp = _path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(_store, SettingsManager.JsonOptions));
                    if (File.Exists(_path)) File.Replace(tmp, _path, null);
                    else File.Move(tmp, _path);
                }
                catch (Exception ex)
                {
                    Swifter.Log.Error("bookmarks save failed: " + ex.Message);
                }
            }
        }

        private void Notify()
        {
            Save();
            Action h = Changed;
            if (h != null) h();
        }

        public BookmarkNode Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (BookmarkNode n in _store.Nodes)
                if (n.Id == id) return n;
            return null;
        }

        public List<BookmarkNode> Children(string parentId)
        {
            List<BookmarkNode> kids = new List<BookmarkNode>();
            foreach (BookmarkNode n in _store.Nodes)
                if (n.ParentId == parentId) kids.Add(n);
            kids.Sort(delegate (BookmarkNode a, BookmarkNode b)
            {
                int c = b.IsFolder.CompareTo(a.IsFolder);
                if (c != 0) return c;
                return a.Order.CompareTo(b.Order);
            });
            return kids;
        }

        public List<BookmarkNode> All()
        {
            return new List<BookmarkNode>(_store.Nodes);
        }

        public string RootId { get { return _store.RootId; } }

        public BookmarkNode Add(string parentId, string title, string url, List<string> tags)
        {
            BookmarkNode n = AddCore(parentId, title, url, tags);
            Notify();
            return n;
        }

        private BookmarkNode AddCore(string parentId, string title, string url, List<string> tags)
        {
            BookmarkNode n = new BookmarkNode
            {
                ParentId = string.IsNullOrEmpty(parentId) ? _store.RootId : parentId,
                Title = title,
                Url = url,
                Order = NextOrder(string.IsNullOrEmpty(parentId) ? _store.RootId : parentId)
            };
            if (tags != null) n.Tags.AddRange(tags);
            lock (_lock) { _store.Nodes.Add(n); }
            return n;
        }

        private int NextOrder(string n_parentId)
        {
            int max = 0;
            foreach (BookmarkNode n in _store.Nodes)
                if (n.ParentId == n_parentId && n.Order >= max) max = n.Order + 1;
            return max;
        }

        public BookmarkNode AddFolder(string parentId, string title)
        {
            BookmarkNode n = AddFolderCore(parentId, title);
            Notify();
            return n;
        }

        private BookmarkNode AddFolderCore(string parentId, string title)
        {
            BookmarkNode n = new BookmarkNode
            {
                ParentId = string.IsNullOrEmpty(parentId) ? _store.RootId : parentId,
                IsFolder = true,
                Title = title,
                Order = NextOrder(string.IsNullOrEmpty(parentId) ? _store.RootId : parentId)
            };
            lock (_lock) { _store.Nodes.Add(n); }
            return n;
        }

        public void Update(BookmarkNode n)
        {
            lock (_lock) { }
            Notify();
        }

        public void SetTags(string id, List<string> tags)
        {
            BookmarkNode n = Find(id);
            if (n == null) return;
            n.Tags = tags ?? new List<string>();
            Notify();
        }

        public void ToggleFavorite(string id)
        {
            BookmarkNode n = Find(id);
            if (n == null) return;
            n.IsFavorite = !n.IsFavorite;
            Notify();
        }

        public void ToggleBar(string id)
        {
            BookmarkNode n = Find(id);
            if (n == null) return;
            n.InBar = !n.InBar;
            Notify();
        }

        public void Touch(string id)
        {
            BookmarkNode n = Find(id);
            if (n == null) return;
            n.LastUsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            n.UseCount++;
            Save();
        }

        public void Delete(string id)
        {
            lock (_lock)
            {
                HashSet<string> doomed = new HashSet<string> { id };
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (BookmarkNode n in _store.Nodes)
                    {
                        if (!doomed.Contains(n.Id) && doomed.Contains(n.ParentId))
                        {
                            doomed.Add(n.Id);
                            grew = true;
                        }
                    }
                }
                _store.Nodes.RemoveAll(delegate (BookmarkNode n) { return doomed.Contains(n.Id); });
            }
            Notify();
        }

        public bool IsDescendant(string candidateId, string ancestorId)
        {
            string cur = candidateId;
            for (int i = 0; i < 64 && !string.IsNullOrEmpty(cur); i++)
            {
                if (cur == ancestorId) return true;
                BookmarkNode n = Find(cur);
                if (n == null) return false;
                cur = n.ParentId;
            }
            return false;
        }

        public void Move(string id, string newParentId, int index)
        {
            BookmarkNode n = Find(id);
            BookmarkNode parent = Find(newParentId);
            if (n == null || parent == null || !parent.IsFolder) return;
            if (n.Id == parent.Id || (n.IsFolder && IsDescendant(parent.Id, n.Id))) return;
            n.ParentId = parent.Id;
            List<BookmarkNode> kids = Children(parent.Id);
            kids.Remove(n);
            if (index < 0 || index > kids.Count) index = kids.Count;
            kids.Insert(index, n);
            for (int i = 0; i < kids.Count; i++) kids[i].Order = i;
            Notify();
        }

        public List<BookmarkNode> Search(string q)
        {
            List<BookmarkNode> res = new List<BookmarkNode>();
            if (string.IsNullOrWhiteSpace(q)) return res;
            string needle = q.ToLowerInvariant();
            foreach (BookmarkNode n in _store.Nodes)
            {
                if (n.Id == _store.RootId) continue;
                if ((n.Title ?? "").ToLowerInvariant().Contains(needle) ||
                    (n.Url ?? "").ToLowerInvariant().Contains(needle) ||
                    n.Tags.Exists(delegate (string t) { return t.ToLowerInvariant().Contains(needle); }))
                    res.Add(n);
            }
            return res;
        }

        public List<string> AllTags()
        {
            SortedSet<string> tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BookmarkNode n in _store.Nodes)
                foreach (string t in n.Tags)
                    if (!string.IsNullOrWhiteSpace(t)) tags.Add(t.Trim());
            return new List<string>(tags);
        }

        public List<BookmarkNode> ByTag(string tag)
        {
            List<BookmarkNode> res = new List<BookmarkNode>();
            foreach (BookmarkNode n in _store.Nodes)
                if (n.Tags.Exists(delegate (string t) { return string.Equals(t, tag, StringComparison.OrdinalIgnoreCase); }))
                    res.Add(n);
            return res;
        }

        public List<BookmarkNode> Favorites()
        {
            List<BookmarkNode> res = new List<BookmarkNode>();
            foreach (BookmarkNode n in _store.Nodes) if (n.IsFavorite) res.Add(n);
            return res;
        }

        public List<BookmarkNode> BarItems()
        {
            List<BookmarkNode> res = new List<BookmarkNode>();
            foreach (BookmarkNode n in Children("bar")) res.Add(n);
            if (res.Count == 0)
                foreach (BookmarkNode n in Children(_store.RootId))
                {
                    if (n.InBar) res.Add(n);
                    if (res.Count >= 12) break;
                }
            return res;
        }

        public List<BookmarkNode> TopUsed(int limit)
        {
            List<BookmarkNode> links = new List<BookmarkNode>();
            foreach (BookmarkNode n in _store.Nodes) if (!n.IsFolder) links.Add(n);
            links.Sort(delegate (BookmarkNode a, BookmarkNode b) { return b.UseCount.CompareTo(a.UseCount); });
            if (links.Count > limit) links.RemoveRange(limit, links.Count - limit);
            return links;
        }

        public BookmarkNode FindByUrl(string url)
        {
            foreach (BookmarkNode n in _store.Nodes)
                if (!n.IsFolder && string.Equals(n.Url, url, StringComparison.OrdinalIgnoreCase)) return n;
            return null;
        }

        // ------------------------------------------------------------------
        // Netscape bookmark file import / export
        // ------------------------------------------------------------------

        public string ExportNetscape()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<!DOCTYPE NETSCAPE-Bookmark-file-1>\r\n");
            sb.Append("<!-- This is an automatically generated file.\r\n     It will be read and overwritten.\r\n     DO NOT EDIT! -->\r\n");
            sb.Append("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\r\n");
            sb.Append("<TITLE>Bookmarks</TITLE>\r\n<H1>Bookmarks</H1>\r\n");
            sb.Append("<DL><p>\r\n");
            AppendFolder(sb, _store.RootId, 1);
            sb.Append("</DL><p>\r\n");
            return sb.ToString();
        }

        private void AppendFolder(StringBuilder sb, string folderId, int depth)
        {
            string pad = new string(' ', depth * 4);
            foreach (BookmarkNode n in Children(folderId))
            {
                if (n.IsFolder)
                {
                    sb.Append(pad).Append("<DT><H3 ADD_DATE=\"").Append(ChromeTime(n.DateAdded))
                      .Append("\" LAST_MODIFIED=\"").Append(ChromeTime(n.LastUsed == 0 ? n.DateAdded : n.LastUsed))
                      .Append("\">").Append(Esc(n.Title)).Append("</H3>\r\n");
                    sb.Append(pad).Append("<DL><p>\r\n");
                    AppendFolder(sb, n.Id, depth + 1);
                    sb.Append(pad).Append("</DL><p>\r\n");
                }
                else
                {
                    sb.Append(pad).Append("<DT><A HREF=\"").Append(Esc(n.Url))
                      .Append("\" ADD_DATE=\"").Append(ChromeTime(n.DateAdded))
                      .Append("\" LAST_MODIFIED=\"").Append(ChromeTime(n.LastUsed == 0 ? n.DateAdded : n.LastUsed))
                      .Append("\" TAGS=\"").Append(Esc(string.Join(",", n.Tags)))
                      .Append('"');
                    if (!string.IsNullOrEmpty(n.Favicon))
                        sb.Append(" ICON=\"").Append(n.Favicon).Append('"');
                    sb.Append('>').Append(Esc(n.Title)).Append("</A>\r\n");
                }
            }
        }

        private static long ChromeTime(long unixMs) { return unixMs / 1000; }

        private static string Esc(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static string Unesc(string s)
        {
            return (s ?? "").Replace("&quot;", "\"").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
        }

        public int ImportNetscape(string html)
        {
            int added = 0;
            Stack<string> folders = new Stack<string>();
            folders.Push(_store.RootId);
            using (StringReader reader = new StringReader(html ?? ""))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string t = line.Trim();
                    if (t.Length == 0) continue;
                    string upper = t.ToUpperInvariant();
                    if (upper.StartsWith("<DL>"))
                    {
                        continue; // structure handled on H3/A lines below
                    }
                    if (upper.StartsWith("</DL>"))
                    {
                        if (folders.Count > 1) folders.Pop();
                        continue;
                    }
                    Match h3 = Regex.Match(t, "<H3([^>]*)>(.*?)</H3>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (h3.Success)
                    {
                        string title = Unesc(h3.Groups[2].Value);
                        BookmarkNode folder = AddFolderCore(folders.Peek(), title);
                        if (h3.Groups[1].Value.IndexOf("PERSONAL_TOOLBAR_FOLDER", StringComparison.OrdinalIgnoreCase) >= 0)
                            folder.InBar = true;
                        // The following <DL> belongs to this folder; because we skip
                        // bare <DL> lines we push the folder now.
                        folders.Push(folder.Id);
                        added++;
                        continue;
                    }
                    Match a = Regex.Match(t, "<A\\s+([^>]*)>(.*?)</A>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (a.Success)
                    {
                        string attrs = a.Groups[1].Value;
                        string title = Unesc(a.Groups[2].Value);
                        string href = Attr(attrs, "HREF");
                        string icon = Attr(attrs, "ICON");
                        string tags = Attr(attrs, "TAGS");
                        BookmarkNode node = AddCore(folders.Peek(), title, href,
                            string.IsNullOrEmpty(tags) ? new List<string>() : new List<string>(tags.Split(',')));
                        node.Favicon = icon;
                        added++;
                    }
                }
            }
            Notify();
            return added;
        }

        private static string Attr(string attrs, string name)
        {
            Match m = Regex.Match(attrs, name + "\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return m.Success ? Unesc(m.Groups[1].Value) : "";
        }

        public void ExportToFile(string path)
        {
            File.WriteAllText(path, ExportNetscape(), Encoding.UTF8);
        }
    }
}
