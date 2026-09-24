using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Win32;
using System.Drawing;

namespace Swifter.Config
{
    /// <summary>Well known on-disk locations for every Swifter artefact.</summary>
    public static class AppPaths
    {
        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Swifter");

        public static string SettingsJson => Path.Combine(DataDir, "settings.json");
        public static string HistoryDb => Path.Combine(DataDir, "history.db");
        public static string BookmarksJson => Path.Combine(DataDir, "bookmarks.json");
        public static string SessionJson => Path.Combine(DataDir, "session.json");
        public static string DownloadsJson => Path.Combine(DataDir, "downloads.json");
        public static string ShieldStatsJson => Path.Combine(DataDir, "shield-stats.json");
        public static string RulesFile => Path.Combine(DataDir, "custom-rules.txt");
        public static string CrashLog => Path.Combine(DataDir, "crash.log");
        public static string AppLog => Path.Combine(DataDir, "swifter.log");
        public static string WallpapersDir => Path.Combine(DataDir, "wallpapers");
        public static string TempDir => Path.Combine(DataDir, "tmp");
        public static string WebView2Folder => Path.Combine(DataDir, "WebView2");

        public static string DefaultDownloadsDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        public static void EnsureFolders()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(WallpapersDir);
            Directory.CreateDirectory(TempDir);
        }
    }

    /// <summary>
    /// JSON backed configuration store at %AppData%\Swifter\settings.json with
    /// debounced atomic saves so every engine can call <see cref="SaveLater"/> freely.
    /// </summary>
    public sealed class SettingsManager
    {
        public SettingsModel Model { get; private set; }

        public event Action Changed;

        private readonly Timer _debounce;
        private readonly object _ioLock = new object();

        public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            JsonSerializerOptions o = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            o.Converters.Add(new JsonStringEnumConverter());
            return o;
        }

        private SettingsManager(SettingsModel model)
        {
            Model = model;
            _debounce = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public static SettingsManager Load()
        {
            AppPaths.EnsureFolders();
            SettingsModel model = null;
            try
            {
                if (File.Exists(AppPaths.SettingsJson))
                {
                    string json = File.ReadAllText(AppPaths.SettingsJson);
                    model = JsonSerializer.Deserialize<SettingsModel>(json, JsonOptions);
                }
            }
            catch (Exception ex)
            {
                try { File.Copy(AppPaths.SettingsJson, AppPaths.SettingsJson + ".corrupt", true); } catch { }
                Swifter.Log.Error("settings.json unreadable: " + ex.Message);
            }
            if (model == null) model = new SettingsModel();
            Normalize(model);
            SettingsManager mgr = new SettingsManager(model);
            return mgr;
        }

        private static void Normalize(SettingsModel m)
        {
            if (string.IsNullOrWhiteSpace(m.General.DownloadPath) || !Directory.Exists(Path.GetDirectoryName(m.General.DownloadPath + "\\")))
            {
                if (string.IsNullOrWhiteSpace(m.General.DownloadPath))
                    m.General.DownloadPath = AppPaths.DefaultDownloadsDir;
            }
            m.Downloads.SegmentCount = Clamp(m.Downloads.SegmentCount, 1, 32);
            m.Downloads.MaxActiveDownloads = Clamp(m.Downloads.MaxActiveDownloads, 1, 8);
            m.General.SessionAutosaveSeconds = Clamp(m.General.SessionAutosaveSeconds, 3, 120);
            m.Appearance.FontScale = Math.Round(Math.Min(1.6, Math.Max(0.8, m.Appearance.FontScale)), 2);
            m.Reader.FontSize = Clamp(m.Reader.FontSize, 13, 30);
            if (m.SearchEngines == null || m.SearchEngines.Count == 0) m.SearchEngines = new SettingsModel().SearchEngines;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        /// <summary>Persists immediately (atomic replace).</summary>
        public void Save()
        {
            lock (_ioLock)
            {
                try
                {
                    AppPaths.EnsureFolders();
                    string tmp = AppPaths.SettingsJson + ".tmp";
                    string json = JsonSerializer.Serialize(Model, JsonOptions);
                    File.WriteAllText(tmp, json);
                    if (File.Exists(AppPaths.SettingsJson))
                        File.Replace(tmp, AppPaths.SettingsJson, null);
                    else
                        File.Move(tmp, AppPaths.SettingsJson);
                }
                catch (Exception ex)
                {
                    Swifter.Log.Error("settings save failed: " + ex.Message);
                }
            }
        }

        /// <summary>Coalesced save; the file is written at most once per 400 ms.</summary>
        public void SaveLater()
        {
            try { _debounce.Change(400, Timeout.Infinite); } catch { }
        }

        /// <summary>Marks settings dirty and notifies listeners (theme swaps etc.).</summary>
        public void Touch()
        {
            SaveLater();
            Action h = Changed;
            if (h != null) h();
        }

        public SearchEngine CurrentSearchEngine()
        {
            string id = Model.NewTab.SearchEngine;
            foreach (SearchEngine e in Model.SearchEngines)
                if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
            return Model.SearchEngines[0];
        }

        public SearchEngine EngineById(string id)
        {
            foreach (SearchEngine e in Model.SearchEngines)
                if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
            return CurrentSearchEngine();
        }

        /// <summary>Resolves System theme using the Windows personalisation registry key.</summary>
        public bool IsDarkTheme()
        {
            switch (Model.Appearance.Theme)
            {
                case ThemeMode.Dark: return true;
                case ThemeMode.Light: return false;
                default:
                    try
                    {
                        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                        {
                            if (key != null && key.GetValue("AppsUseLightTheme") is int v) return v == 0;
                        }
                    }
                    catch { }
                    return true;
            }
        }

        /// <summary>Reads the Windows accent colour (ABGR dword) used when accent = system.</summary>
        public static Color SystemAccent()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    if (key != null && key.GetValue("AccentColorMenu") is int raw)
                    {
                        byte a = (byte)((raw >> 24) & 0xFF);
                        byte b = (byte)((raw >> 16) & 0xFF);
                        byte g = (byte)((raw >> 8) & 0xFF);
                        byte r = (byte)(raw & 0xFF);
                        return Color.FromArgb(255, r, g, b);
                    }
                }
            }
            catch { }
            return Color.FromArgb(76, 194, 255);
        }

        public Color Accent()
        {
            string hex = Model.Appearance.AccentColor;
            if (string.Equals(hex, "system", StringComparison.OrdinalIgnoreCase)) return SystemAccent();
            try
            {
                if (hex.StartsWith("#")) hex = hex.Substring(1);
                if (hex.Length == 6)
                {
                    int v = Convert.ToInt32(hex, 16);
                    return Color.FromArgb(255, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
                }
            }
            catch { }
            return Color.FromArgb(76, 194, 255);
        }

        public static string ToHex(Color c)
        {
            return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
        }
    }
}
