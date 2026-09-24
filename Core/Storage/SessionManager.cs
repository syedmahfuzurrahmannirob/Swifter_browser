using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using Swifter.Config;

namespace Swifter.Storage
{
    public sealed class SessionTab
    {
        public string Url { get; set; } = "";
        public string Title { get; set; } = "";
        public bool Pinned { get; set; }
        public bool Muted { get; set; }
        public string GroupId { get; set; } = "";
    }

    public sealed class SessionGroup
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Color { get; set; } = "#4cc2ff";
        public bool Collapsed { get; set; }
    }

    public sealed class SessionWindow
    {
        public List<SessionTab> Tabs { get; set; } = new List<SessionTab>();
        public List<SessionGroup> Groups { get; set; } = new List<SessionGroup>();
        public int ActiveIndex { get; set; }
    }

    public sealed class SessionData
    {
        public long SavedAt { get; set; }
        public bool CleanExit { get; set; } = true;
        public List<SessionWindow> Windows { get; set; } = new List<SessionWindow>();
    }

    /// <summary>
    /// Crash recovery + tab restore. The session file is rewritten on a timer
    /// (default 10 s) with <c>CleanExit = false</c>; a clean shutdown flips the
    /// flag so the next launch knows whether to offer recovery.
    /// </summary>
    public sealed class SessionManager
    {
        private readonly string _path;
        private readonly object _lock = new object();
        private Timer _timer;
        private Func<SessionData> _provider;

        public SessionData Pending { get; private set; }
        public bool NeedsRestore { get; set; }

        public SessionManager(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path))
                {
                    Pending = JsonSerializer.Deserialize<SessionData>(
                        File.ReadAllText(path), SettingsManager.JsonOptions);
                    NeedsRestore = Pending != null && !Pending.CleanExit && HasTabs(Pending);
                }
            }
            catch (Exception ex)
            {
                Swifter.Log.Error("session load failed: " + ex.Message);
            }
            if (Pending == null) Pending = new SessionData();
        }

        private static bool HasTabs(SessionData d)
        {
            if (d.Windows == null) return false;
            foreach (SessionWindow w in d.Windows)
                if (w.Tabs != null && w.Tabs.Count > 0) return true;
            return false;
        }

        /// <summary>Called at boot: marks the session as "possibly crashed".</summary>
        public void MarkCrashSuspected()
        {
            Write(new SessionData { CleanExit = false, Windows = Pending.Windows ?? new List<SessionWindow>() },
                keepPending: true);
        }

        public void Start(Func<SessionData> provider, int intervalSeconds)
        {
            _provider = provider;
            if (_timer != null) _timer.Dispose();
            _timer = new Timer();
            _timer.Interval = Math.Max(3, intervalSeconds) * 1000;
            _timer.Tick += delegate { TickSave(); };
            _timer.Start();
        }

        public void Stop()
        {
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
        }

        private void TickSave()
        {
            if (_provider == null) return;
            try
            {
                SessionData data = _provider();
                if (data != null) SaveNow(data);
            }
            catch (Exception ex)
            {
                Swifter.Log.Error("session autosave failed: " + ex.Message);
            }
        }

        public void SaveNow(SessionData data)
        {
            data.CleanExit = false;
            data.SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Write(data, keepPending: true);
        }

        public void MarkCleanExit()
        {
            lock (_lock)
            {
                try
                {
                    SessionData data = Pending ?? new SessionData();
                    data.CleanExit = true;
                    data.SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    string json = JsonSerializer.Serialize(data, SettingsManager.JsonOptions);
                    File.WriteAllText(_path, json);
                }
                catch (Exception ex)
                {
                    Swifter.Log.Error("clean exit mark failed: " + ex.Message);
                }
            }
        }

        public void MarkCrashed()
        {
            lock (_lock)
            {
                try
                {
                    SessionData data = Pending ?? new SessionData();
                    data.CleanExit = false;
                    File.WriteAllText(_path, JsonSerializer.Serialize(data, SettingsManager.JsonOptions));
                }
                catch { }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                Pending = new SessionData { CleanExit = true };
                NeedsRestore = false;
                try { if (File.Exists(_path)) File.Delete(_path); } catch { }
            }
        }

        private void Write(SessionData data, bool keepPending)
        {
            lock (_lock)
            {
                try
                {
                    string tmp = _path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(data, SettingsManager.JsonOptions));
                    if (File.Exists(_path)) File.Replace(tmp, _path, null);
                    else File.Move(tmp, _path);
                    if (keepPending) Pending = data;
                }
                catch (Exception ex)
                {
                    Swifter.Log.Error("session write failed: " + ex.Message);
                }
            }
        }
    }
}
