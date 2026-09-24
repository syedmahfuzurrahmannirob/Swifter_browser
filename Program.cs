using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Swifter.Config;
using Swifter.Engine;
using Swifter.Protocols;
using Swifter.Storage;

namespace Swifter
{
    /// <summary>
    /// Application entry point. Boots the storage engines, the WebView2 environment
    /// (with the custom <c>swifter://</c> scheme registered) and the main window.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            Application.ThreadException += OnThreadException;

            // Make sure every engine is wired before the first form is created.
            AppServices.Boot();
            AppServices.Session.MarkCrashSuspected();

            CoreWebView2Environment env = CreateEnvironment();
            if (env == null)
            {
                MessageBox.Show(
                    "Swifter could not start the Microsoft Edge WebView2 runtime.\n\n" +
                    "Please install the 'Evergreen' WebView2 Runtime from\n" +
                    "https://developer.microsoft.com/microsoft-edge/webview2/\n" +
                    "and start Swifter again.",
                    "Swifter - missing WebView2 runtime",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
            AppServices.Env = env;

            BrowserForm window = new BrowserForm(args ?? new string[0]);
            AppServices.MainWindow = window;
            Application.Run(window);

            AppServices.Shutdown();
        }

        private static CoreWebView2Environment CreateEnvironment()
        {
            // First attempt uses the full featured option set, including the
            // custom swifter:// scheme registration. If the runtime rejects any
            // option we retry with a minimal configuration so the browser still opens.
            try
            {
                return CreateEnvironmentAsync(true).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Error("Full option environment creation failed, retrying minimal: " + ex.Message);
                try
                {
                    return CreateEnvironmentAsync(false).GetAwaiter().GetResult();
                }
                catch (Exception ex2)
                {
                    Log.Error("Environment creation failed: " + ex2.Message);
                    return null;
                }
            }
        }

        private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(bool full)
        {
            CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions();
            if (full)
            {
                List<string> origins = new List<string> { "swifter://*" };
                CoreWebView2CustomSchemeRegistration scheme =
                    new CoreWebView2CustomSchemeRegistration(SwifterSchemeHandler.Scheme)
                    {
                        HasAuthorityComponent = true,
                        TreatAsSecure = true
                    };
                foreach (string o in origins) scheme.AllowedOrigins.Add(o);
                options.CustomSchemeRegistrations.Add(scheme);
            }
            options.AdditionalBrowserArguments =
                "--disable-features=msSmartScreenProtection,msEnhancedSecurityMode " +
                "--autoplay-policy=no-user-gesture-required";
            options.EnableTrackingPrevention = true;

            return await CoreWebView2Environment.CreateAsync(
                null, AppPaths.WebView2Folder, options).ConfigureAwait(true);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Log.Error("Unhandled exception: " + e.ExceptionObject);
            try { AppServices.Session.MarkCrashed(); } catch { }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Log.Error("UI thread exception: " + e.Exception);
        }
    }

    /// <summary>
    /// Composition root / service locator for every engine in the browser.
    /// Kept static because every subsystem (pages, tabs, downloads, shields)
    /// needs the same singletons and WinForms does not provide DI out of the box.
    /// </summary>
    public static class AppServices
    {
        public static SettingsManager Settings { get; private set; }
        public static HistoryDatabase History { get; private set; }
        public static BookmarksDatabase Bookmarks { get; private set; }
        public static SessionManager Session { get; private set; }
        public static DownloadQueueManager Downloads { get; private set; }
        public static MediaSniffer Sniffer { get; private set; }
        public static ShieldEngine Shields { get; private set; }
        public static ScriptInjector Scripts { get; private set; }
        public static MemorySaver Memory { get; private set; }
        public static CoreWebView2Environment Env { get; set; }
        public static BrowserForm MainWindow { get; set; }

        private static bool _booted;
        private static readonly object BootLock = new object();

        public static void Boot()
        {
            lock (BootLock)
            {
                if (_booted) return;
                _booted = true;

                AppPaths.EnsureFolders();
                Settings = SettingsManager.Load();
                History = new HistoryDatabase(AppPaths.HistoryDb);
                Bookmarks = BookmarksDatabase.Load(AppPaths.BookmarksJson);
                Shields = new ShieldEngine(Settings);
                Sniffer = new MediaSniffer();
                Scripts = new ScriptInjector(Settings);
                Downloads = new DownloadQueueManager(Settings);
                Session = new SessionManager(AppPaths.SessionJson);
                Memory = new MemorySaver(Settings);

                Downloads.RestorePersistedTasks();
                Downloads.StartClipboardSniffer();
                Memory.Start();
            }
        }

        public static void Shutdown()
        {
            if (!_booted) return;
            _booted = false;
            try { Memory.Stop(); } catch { }
            try { Downloads.StopClipboardSniffer(); } catch { }
            try { Downloads.Persist(); } catch { }
            try { Settings.Save(); } catch { }
            try { Bookmarks.Save(); } catch { }
            try { History.Dispose(); } catch { }
            try { Session.MarkCleanExit(); } catch { }
        }
    }

    /// <summary>Tiny append-only crash log kept in %AppData%\Swifter.</summary>
    public static class Log
    {
        private static readonly object Lock = new object();

        public static void Error(object message)
        {
            try
            {
                lock (Lock)
                {
                    File.AppendAllText(AppPaths.CrashLog,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never take the browser down.
            }
        }
    }
}
