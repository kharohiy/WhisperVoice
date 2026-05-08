using System.Net.Http;
using System.Windows.Controls;
using WhisperVoice.Services;
using WhisperVoice.ViewModels;

namespace WhisperVoice.Views
{
    public partial class ModelsManagerControl : System.Windows.Controls.UserControl
    {
        private static readonly HttpClient _http = new();

        public ModelsViewModel ViewModel { get; }

        /// <summary>
        /// Factory. Pass modelsDir = Path.Combine(BaseDir, "models") to match SettingsWindow.
        /// </summary>
        public static ModelsManagerControl Create(string modelsDir,
            string remoteConfigUrl = "https://raw.githubusercontent.com/kharohiy/WhisperVoice/main/models.json")
        {
            var vm = new ModelsViewModel(
                new ModelConfigService(_http),
                new ModelDownloadService(_http),
                remoteConfigUrl,
                modelsDir);
            return new ModelsManagerControl(vm);
        }

        // Parameterless ctor required by XAML designer
        public ModelsManagerControl() : this(new ModelsViewModel(
            new ModelConfigService(new HttpClient()),
            new ModelDownloadService(new HttpClient()),
            string.Empty, string.Empty)) { }

        private ModelsManagerControl(ModelsViewModel vm)
        {
            ViewModel   = vm;
            DataContext = vm;
            InitializeComponent();
        }
    }
}
