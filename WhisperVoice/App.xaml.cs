using System.Windows;

namespace WhisperVoice
{
    public partial class App : System.Windows.Application
    {
        // Unique GUID-based mutex name for single-instance enforcement
        private const string MutexName = "Global\\{8F6F3D9A-2B4C-4E1A-9A7B-3C5D6E7F8A9B}";
        private static Mutex? _instanceMutex;

        // Supported interface language codes (must match Strings.{lang}.xaml filenames)
        private static readonly string[] SupportedLangs =
            { "en", "ru", "uk", "pl", "de", "es", "fr" };

        /// <summary>
        /// Called when the application starts. Enforces single-instance behavior.
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            _instanceMutex = new Mutex(true, MutexName, out bool createdNew);

            if (!createdNew)
            {
                // Another instance is running — exit without touching WPF
                _instanceMutex.Dispose();
                Environment.Exit(0);
                return;
            }

            base.OnStartup(e);

            bool isAutoStart = e.Args.Contains("--autostart");

            var mainWindow = new MainWindow();

            if (isAutoStart)
                mainWindow.Hide();   // start silently in tray at boot
            else
                mainWindow.Show();
        }

        /// <summary>
        /// Called when the application exits. Ensures proper Mutex cleanup.
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            DiagnosticLogger.Instance.Dispose();
            base.OnExit(e);
        }

        /// <summary>
        /// Swaps the merged ResourceDictionary for localised strings at runtime.
        /// All DynamicResource bindings update automatically across every open window.
        /// Call this from SettingsWindow after saving AppInterfaceLanguage.
        /// </summary>
        public static void ApplyInterfaceLanguage(string langCode)
        {
            // Clamp to a supported code
            bool known = Array.Exists(SupportedLangs, l => l == langCode);
            if (!known) langCode = "en";

            string uri = $"pack://application:,,,/WhisperVoice;component/Resources/Strings.{langCode}.xaml";
            var dict = new ResourceDictionary
            {
                Source = new Uri(uri, UriKind.Absolute)
            };

            // Replace the first merged dictionary (the strings file) in-place.
            // Using Replace instead of Clear+Add keeps other merged dicts intact.
            var merged = Current.Resources.MergedDictionaries;
            if (merged.Count > 0)
                merged[0] = dict;
            else
                merged.Add(dict);
        }
    }
}