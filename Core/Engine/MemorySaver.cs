using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Swifter.Config;

namespace Swifter.Engine
{
    /// <summary>Per tab bookkeeping used by the sleep/freeze engine.</summary>
    public sealed class MemoryTab
    {
        public string Id = "";
        public string Url = "";
        public bool Active;
        public bool Pinned;
        public bool PlayingAudio;
        public DateTime LastActive = DateTime.Now;
        public bool Frozen;
        public bool Discarded;
        public Func<Task<bool>> Freeze;
        public Action Thaw;
        public Func<Task> Discard;
        public Action Restore;
    }

    /// <summary>
    /// Background tab sleep engine: tabs idle for more than 30 minutes are frozen
    /// (WebView2 TrySuspend, renderer paused + memory reclaimed), tabs idle for more
    /// than 90 minutes are discarded entirely and restored on demand.
    /// </summary>
    public sealed class MemorySaver
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, MemoryTab> _tabs = new Dictionary<string, MemoryTab>();
        private Timer _timer;

        public int FreezeAfterMinutes = 30;
        public int DiscardAfterMinutes = 90;

        public event Action Changed;
        public event Action<MemoryTab> TabStateChanged;

        public MemorySaver(SettingsManager settings)
        {
            // Thresholds are exposed as public fields so the shields/settings UI
            // can tune them; the manager instance keeps the settings reference
            // contract for future per-profile memory policies.
            if (settings == null) throw new ArgumentNullException("settings");
        }

        public void Start()
        {
            if (_timer != null) return;
            _timer = new Timer { Interval = 20000 };
            _timer.Tick += delegate { Tick(); };
            _timer.Start();
        }

        public void Stop()
        {
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
        }

        public void Register(MemoryTab tab)
        {
            lock (_lock) _tabs[tab.Id] = tab;
        }

        public void Unregister(string id)
        {
            lock (_lock) _tabs.Remove(id);
        }

        /// <summary>Called on any user interaction / navigation with the tab.</summary>
        public void Note(string id)
        {
            MemoryTab tab;
            lock (_lock) _tabs.TryGetValue(id, out tab);
            if (tab == null) return;
            tab.LastActive = DateTime.Now;
        }

        public List<MemoryTab> Tabs()
        {
            lock (_lock) return _tabs.Values.ToList();
        }

        public int FrozenCount
        {
            get { lock (_lock) return _tabs.Values.Count(t => t.Frozen); }
        }

        public int DiscardedCount
        {
            get { lock (_lock) return _tabs.Values.Count(t => t.Discarded); }
        }

        /// <summary>Rough RAM reclaimed estimate shown in the tab UI.</summary>
        public double EstimatedSavedMb
        {
            get
            {
                lock (_lock)
                    return _tabs.Values.Sum(t => t.Discarded ? 180.0 : (t.Frozen ? 90.0 : 0.0));
            }
        }

        private void Tick()
        {
            List<MemoryTab> snapshot;
            lock (_lock) snapshot = _tabs.Values.ToList();
            foreach (MemoryTab tab in snapshot)
            {
                if (tab.Active || tab.Pinned || tab.PlayingAudio) continue;
                double idleMin = (DateTime.Now - tab.LastActive).TotalMinutes;

                if (!tab.Discarded && idleMin > DiscardAfterMinutes && tab.Frozen && tab.Discard != null)
                {
                    try
                    {
                        tab.Discard();
                        tab.Discarded = true;
                        tab.Frozen = false;
                        Raise(tab);
                    }
                    catch (Exception ex)
                    {
                        Swifter.Log.Error("discard failed: " + ex.Message);
                    }
                }
                else if (!tab.Frozen && !tab.Discarded && idleMin > FreezeAfterMinutes && tab.Freeze != null)
                {
                    Func<Task<bool>> freeze = tab.Freeze;
                    Task.Run(async () =>
                    {
                        try
                        {
                            bool ok = await freeze().ConfigureAwait(false);
                            if (ok)
                            {
                                tab.Frozen = true;
                                Raise(tab);
                            }
                        }
                        catch { }
                    });
                }
            }
        }

        /// <summary>Wakes a frozen or discarded tab (called when it becomes active).</summary>
        public void Wake(string id)
        {
            MemoryTab tab;
            lock (_lock) _tabs.TryGetValue(id, out tab);
            if (tab == null) return;
            tab.LastActive = DateTime.Now;
            if (tab.Discarded)
            {
                tab.Discarded = false;
                if (tab.Restore != null) tab.Restore();
                Raise(tab);
            }
            else if (tab.Frozen)
            {
                tab.Frozen = false;
                if (tab.Thaw != null) tab.Thaw();
                Raise(tab);
            }
        }

        public bool IsFrozen(string id)
        {
            MemoryTab tab;
            lock (_lock) _tabs.TryGetValue(id, out tab);
            return tab != null && tab.Frozen;
        }

        public bool IsDiscarded(string id)
        {
            MemoryTab tab;
            lock (_lock) _tabs.TryGetValue(id, out tab);
            return tab != null && tab.Discarded;
        }

        private void Raise(MemoryTab tab)
        {
            Action<MemoryTab> h = TabStateChanged;
            if (h != null) h(tab);
            Action c = Changed;
            if (c != null) c();
        }
    }
}
