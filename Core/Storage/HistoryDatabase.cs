using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Swifter.Storage
{
    public sealed class HistoryEntry
    {
        public long Id;
        public string Url = "";
        public string Title = "";
        public string Description = "";
        public string Domain = "";
        public long VisitedAt;      // unix ms
        public long DurationMs;
    }

    public sealed class DayStat
    {
        public string Day = "";      // yyyy-MM-dd
        public int Visits;
    }

    public sealed class HourStat
    {
        public int Hour;
        public int Visits;
    }

    public sealed class DomainStat
    {
        public string Domain = "";
        public int Visits;
        public long Seconds;
        public string Favicon = "";
    }

    /// <summary>
    /// SQLite history store with an FTS5 full-text index over url/title/description
    /// plus visit analytics (per-day density, hourly heat-map, domain breakdown).
    /// All operations are serialised through a single lock; the database lives at
    /// %AppData%\Swifter\history.db in WAL mode.
    /// </summary>
    public sealed class HistoryDatabase : IDisposable
    {
        private readonly object _lock = new object();
        private SqliteConnection _db;
        private readonly string _path;
        private bool _disposed;

        public HistoryDatabase(string path)
        {
            _path = path;
            Open();
        }

        private void Open()
        {
            SqliteConnectionStringBuilder sb = new SqliteConnectionStringBuilder
            {
                DataSource = _path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = true
            };
            _db = new SqliteConnection(sb.ToString());
            _db.Open();
            Exec(@"PRAGMA journal_mode=WAL;
                   PRAGMA synchronous=NORMAL;
                   PRAGMA temp_store=MEMORY;
                   CREATE TABLE IF NOT EXISTS visits (
                     id INTEGER PRIMARY KEY AUTOINCREMENT,
                     url TEXT NOT NULL,
                     title TEXT NOT NULL DEFAULT '',
                     description TEXT NOT NULL DEFAULT '',
                     domain TEXT NOT NULL DEFAULT '',
                     visited_at INTEGER NOT NULL,
                     duration_ms INTEGER NOT NULL DEFAULT 0);
                   CREATE INDEX IF NOT EXISTS ix_visits_time ON visits(visited_at DESC);
                   CREATE INDEX IF NOT EXISTS ix_visits_domain ON visits(domain);
                   CREATE TABLE IF NOT EXISTS favicons (
                     domain TEXT PRIMARY KEY,
                     data TEXT NOT NULL,
                     updated_at INTEGER NOT NULL);
                   CREATE VIRTUAL TABLE IF NOT EXISTS history_fts USING fts5(
                     url, title, description, content='', tokenize='unicode61');");
        }

        private void Exec(string sql)
        {
            using (SqliteCommand cmd = _db.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        private static string DomainOf(string url)
        {
            try
            {
                Uri u = new Uri(url);
                return u.Host;
            }
            catch
            {
                int i = url.IndexOf("://", StringComparison.Ordinal);
                string rest = i >= 0 ? url.Substring(i + 3) : url;
                int slash = rest.IndexOf('/');
                return slash > 0 ? rest.Substring(0, slash) : rest;
            }
        }

        public long RecordVisit(string url, string title, string description)
        {
            if (string.IsNullOrEmpty(url)) return -1;
            lock (_lock)
            {
                if (_disposed) return -1;
                try
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    long id;
                    using (SqliteCommand cmd = _db.CreateCommand())
                    {
                        cmd.CommandText =
                            "INSERT INTO visits(url,title,description,domain,visited_at,duration_ms) " +
                            "VALUES($u,$t,$d,$dom,$at,0); SELECT last_insert_rowid();";
                        cmd.Parameters.AddWithValue("$u", url);
                        cmd.Parameters.AddWithValue("$t", title ?? "");
                        cmd.Parameters.AddWithValue("$d", description ?? "");
                        cmd.Parameters.AddWithValue("$dom", DomainOf(url));
                        cmd.Parameters.AddWithValue("$at", now);
                        id = Convert.ToInt64(cmd.ExecuteScalar());
                    }
                    using (SqliteCommand fts = _db.CreateCommand())
                    {
                        fts.CommandText =
                            "INSERT INTO history_fts(rowid,url,title,description) VALUES($id,$u,$t,$d);";
                        fts.Parameters.AddWithValue("$id", id);
                        fts.Parameters.AddWithValue("$u", url);
                        fts.Parameters.AddWithValue("$t", title ?? "");
                        fts.Parameters.AddWithValue("$d", description ?? "");
                        fts.ExecuteNonQuery();
                    }
                    return id;
                }
                catch (Exception ex)
                {
                    Swifter.Log.Error("history insert: " + ex.Message);
                    return -1;
                }
            }
        }

        public void UpdateDuration(long visitId, long durationMs)
        {
            if (visitId <= 0) return;
            lock (_lock)
            {
                if (_disposed) return;
                try
                {
                    using (SqliteCommand cmd = _db.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE visits SET duration_ms = duration_ms + $d WHERE id = $id;";
                        cmd.Parameters.AddWithValue("$d", durationMs);
                        cmd.Parameters.AddWithValue("$id", visitId);
                        cmd.ExecuteNonQuery();
                    }
                }
                catch { }
            }
        }

        public void SetFavicon(string domain, string dataUri)
        {
            if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(dataUri)) return;
            lock (_lock)
            {
                if (_disposed) return;
                try
                {
                    using (SqliteCommand cmd = _db.CreateCommand())
                    {
                        cmd.CommandText =
                            "INSERT INTO favicons(domain,data,updated_at) VALUES($d,$data,$t) " +
                            "ON CONFLICT(domain) DO UPDATE SET data=$data, updated_at=$t;";
                        cmd.Parameters.AddWithValue("$d", domain);
                        cmd.Parameters.AddWithValue("$data", dataUri);
                        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        cmd.ExecuteNonQuery();
                    }
                }
                catch { }
            }
        }

        public string GetFavicon(string domain)
        {
            lock (_lock)
            {
                if (_disposed) return "";
                try
                {
                    using (SqliteCommand cmd = _db.CreateCommand())
                    {
                        cmd.CommandText = "SELECT data FROM favicons WHERE domain = $d;";
                        cmd.Parameters.AddWithValue("$d", domain ?? "");
                        object r = cmd.ExecuteScalar();
                        return r == null || r == DBNull.Value ? "" : Convert.ToString(r);
                    }
                }
                catch { return ""; }
            }
        }

        /// <summary>FTS5 search over url/title/description with prefix matching.</summary>
        public List<HistoryEntry> Search(string query, int limit)
        {
            List<HistoryEntry> rows = new List<HistoryEntry>();
            if (string.IsNullOrWhiteSpace(query)) return rows;
            lock (_lock)
            {
                if (_disposed) return rows;
                string match = BuildMatch(query);
                try
                {
                    using (SqliteCommand cmd = _db.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT v.id, v.url, v.title, v.description, v.domain, v.visited_at, v.duration_ms " +
                            "FROM visits v JOIN (SELECT rowid FROM history_fts WHERE history_fts MATCH $m) f " +
                            "ON f.rowid = v.id ORDER BY v.visited_at DESC LIMIT $n;";
                        cmd.Parameters.AddWithValue("$m", match);
                        cmd.Parameters.AddWithValue("$n", limit);
                        Fill(cmd, rows);
                    }
                }
                catch
                {
                    // Fallback to LIKE when the query is not valid FTS syntax.
                    using (SqliteCommand cmd = _db.CreateCommand())
                    {
                        cmd.CommandText =
                            "SELECT id,url,title,description,domain,visited_at,duration_ms FROM visits " +
                            "WHERE url LIKE $q OR title LIKE $q ORDER BY visited_at DESC LIMIT $n;";
                        cmd.Parameters.AddWithValue("$q", "%" + query.Replace("%", "[%]") + "%");
                        cmd.Parameters.AddWithValue("$n", limit);
                        Fill(cmd, rows);
                    }
                }
            }
            return rows;
        }

        private static string BuildMatch(string query)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string token in query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string clean = token.Replace("\"", "");
                if (clean.Length == 0) continue;
                if (sb.Length > 0) sb.Append(" AND ");
                sb.Append('"').Append(clean).Append('"').Append('*');
            }
            return sb.Length == 0 ? "\"\"" : sb.ToString();
        }

        public List<HistoryEntry> ListRange(long fromUnixMs, long toUnixMs, int limit)
        {
            List<HistoryEntry> rows = new List<HistoryEntry>();
            lock (_lock)
            {
                if (_disposed) return rows;
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT id,url,title,description,domain,visited_at,duration_ms FROM visits " +
                        "WHERE visited_at BETWEEN $f AND $t ORDER BY visited_at DESC LIMIT $n;";
                    cmd.Parameters.AddWithValue("$f", fromUnixMs);
                    cmd.Parameters.AddWithValue("$t", toUnixMs);
                    cmd.Parameters.AddWithValue("$n", limit);
                    Fill(cmd, rows);
                }
            }
            return rows;
        }

        public List<HistoryEntry> ListRecent(int limit)
        {
            return ListRange(0, long.MaxValue, limit);
        }

        private static void Fill(SqliteCommand cmd, List<HistoryEntry> rows)
        {
            using (SqliteDataReader r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    rows.Add(new HistoryEntry
                    {
                        Id = r.GetInt64(0),
                        Url = r.GetString(1),
                        Title = r.IsDBNull(2) ? "" : r.GetString(2),
                        Description = r.IsDBNull(3) ? "" : r.GetString(3),
                        Domain = r.IsDBNull(4) ? "" : r.GetString(4),
                        VisitedAt = r.GetInt64(5),
                        DurationMs = r.IsDBNull(6) ? 0 : r.GetInt64(6)
                    });
                }
            }
        }

        /// <summary>Prefix suggestions for the omnibox (title or url starts with prefix).</summary>
        public List<HistoryEntry> Suggest(string prefix, int limit)
        {
            List<HistoryEntry> rows = new List<HistoryEntry>();
            if (string.IsNullOrEmpty(prefix)) return rows;
            lock (_lock)
            {
                if (_disposed) return rows;
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT id,url,title,description,domain,visited_at,duration_ms FROM visits " +
                        "WHERE title LIKE $p OR url LIKE $p2 ORDER BY visited_at DESC LIMIT $n;";
                    cmd.Parameters.AddWithValue("$p", prefix + "%");
                    cmd.Parameters.AddWithValue("$p2", "%" + prefix + "%");
                    cmd.Parameters.AddWithValue("$n", limit);
                    Fill(cmd, rows);
                }
            }
            return rows;
        }

        public List<DayStat> DailyStats(int days)
        {
            List<DayStat> rows = new List<DayStat>();
            long from = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
            lock (_lock)
            {
                if (_disposed) return rows;
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT visited_at FROM visits WHERE visited_at >= $f;";
                    cmd.Parameters.AddWithValue("$f", from);
                    Dictionary<string, int> buckets = new Dictionary<string, int>();
                    using (SqliteDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            string day = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0))
                                .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                            int c;
                            buckets.TryGetValue(day, out c);
                            buckets[day] = c + 1;
                        }
                    }
                    for (int i = days - 1; i >= 0; i--)
                    {
                        string day = DateTimeOffset.UtcNow.AddDays(-i).UtcDateTime
                            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        int c;
                        buckets.TryGetValue(day, out c);
                        rows.Add(new DayStat { Day = day, Visits = c });
                    }
                }
            }
            return rows;
        }

        public List<HourStat> HourlyStats(int days)
        {
            int[] hours = new int[24];
            long from = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
            lock (_lock)
            {
                if (_disposed) return new List<HourStat>();
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT visited_at FROM visits WHERE visited_at >= $f;";
                    cmd.Parameters.AddWithValue("$f", from);
                    using (SqliteDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            int h = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)).UtcDateTime
                                .ToLocalTime().Hour;
                            hours[h]++;
                        }
                    }
                }
            }
            List<HourStat> rows = new List<HourStat>();
            for (int i = 0; i < 24; i++) rows.Add(new HourStat { Hour = i, Visits = hours[i] });
            return rows;
        }

        public List<DomainStat> TopDomains(int limit, int days)
        {
            List<DomainStat> rows = new List<DomainStat>();
            long from = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
            lock (_lock)
            {
                if (_disposed) return rows;
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT domain, COUNT(*) AS c, SUM(duration_ms)/1000 AS s FROM visits " +
                        "WHERE visited_at >= $f GROUP BY domain ORDER BY c DESC LIMIT $n;";
                    cmd.Parameters.AddWithValue("$f", from);
                    cmd.Parameters.AddWithValue("$n", limit);
                    using (SqliteDataReader r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            rows.Add(new DomainStat
                            {
                                Domain = r.GetString(0),
                                Visits = r.GetInt32(1),
                                Seconds = r.IsDBNull(2) ? 0 : r.GetInt64(2)
                            });
                        }
                    }
                }
                foreach (DomainStat d in rows) d.Favicon = GetFavicon(d.Domain);
            }
            return rows;
        }

        public List<DomainStat> TopSites(int limit)
        {
            return TopDomains(limit, 30);
        }

        public long TotalVisits()
        {
            lock (_lock)
            {
                if (_disposed) return 0;
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM visits;";
                    return Convert.ToInt64(cmd.ExecuteScalar());
                }
            }
        }

        public void DeleteVisit(long id)
        {
            lock (_lock)
            {
                if (_disposed) return;
                HistoryEntry e = null;
                using (SqliteCommand q = _db.CreateCommand())
                {
                    q.CommandText = "SELECT url,title,description FROM visits WHERE id=$id;";
                    q.Parameters.AddWithValue("$id", id);
                    using (SqliteDataReader r = q.ExecuteReader())
                    {
                        if (r.Read()) e = new HistoryEntry { Url = r.GetString(0), Title = r.GetString(1), Description = r.GetString(2) };
                    }
                }
                if (e == null) return;
                using (SqliteCommand cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM visits WHERE id=$id;";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                using (SqliteCommand fts = _db.CreateCommand())
                {
                    fts.CommandText =
                        "INSERT INTO history_fts(history_fts,rowid,url,title,description) VALUES('delete',$id,$u,$t,$d);";
                    fts.Parameters.AddWithValue("$id", id);
                    fts.Parameters.AddWithValue("$u", e.Url);
                    fts.Parameters.AddWithValue("$t", e.Title);
                    fts.Parameters.AddWithValue("$d", e.Description);
                    try { fts.ExecuteNonQuery(); } catch { }
                }
            }
        }

        public int DeleteDomain(string domain)
        {
            lock (_lock)
            {
                if (_disposed) return 0;
                List<long> ids = new List<long>();
                using (SqliteCommand q = _db.CreateCommand())
                {
                    q.CommandText = "SELECT id FROM visits WHERE domain=$d;";
                    q.Parameters.AddWithValue("$d", domain);
                    using (SqliteDataReader r = q.ExecuteReader())
                    {
                        while (r.Read()) ids.Add(r.GetInt64(0));
                    }
                }
                foreach (long id in ids) DeleteVisit(id);
                return ids.Count;
            }
        }

        public void ClearAll()
        {
            lock (_lock)
            {
                if (_disposed) return;
                Exec("DELETE FROM visits; DELETE FROM favicons; INSERT INTO history_fts(history_fts) VALUES('delete-all');");
            }
        }

        public void ExportCsv(string path)
        {
            List<HistoryEntry> all = ListRecent(100000);
            StringBuilder sb = new StringBuilder();
            sb.Append("url,title,domain,visited_at,duration_ms\r\n");
            foreach (HistoryEntry e in all)
            {
                sb.Append(Csv(e.Url)).Append(',')
                  .Append(Csv(e.Title)).Append(',')
                  .Append(Csv(e.Domain)).Append(',')
                  .Append(DateTimeOffset.FromUnixTimeMilliseconds(e.VisitedAt).UtcDateTime.ToString("o", CultureInfo.InvariantCulture)).Append(',')
                  .Append(e.DurationMs).Append("\r\n");
            }
            File.WriteAllText(path, sb.ToString());
        }

        public void ExportJson(string path)
        {
            List<HistoryEntry> all = ListRecent(100000);
            StringBuilder sb = new StringBuilder();
            sb.Append("[");
            for (int i = 0; i < all.Count; i++)
            {
                HistoryEntry e = all[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"url\":").Append(Swifter.Protocols.ProtocolPageRenderer.JsonEncode(e.Url))
                  .Append(",\"title\":").Append(Swifter.Protocols.ProtocolPageRenderer.JsonEncode(e.Title))
                  .Append(",\"domain\":").Append(Swifter.Protocols.ProtocolPageRenderer.JsonEncode(e.Domain))
                  .Append(",\"visitedAt\":").Append(e.VisitedAt)
                  .Append(",\"durationMs\":").Append(e.DurationMs).Append('}');
            }
            sb.Append(']');
            File.WriteAllText(path, sb.ToString());
        }

        private static string Csv(string s)
        {
            string v = (s ?? "").Replace("\"", "\"\"");
            if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) v = "\"" + v + "\"";
            return v;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                try { _db.Dispose(); } catch { }
            }
        }
    }
}
