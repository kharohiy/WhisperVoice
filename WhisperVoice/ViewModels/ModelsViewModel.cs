using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using WhisperVoice.Services;

namespace WhisperVoice.ViewModels
{
    public enum ManagerState { Loading, Automatic, ManualFallback }

    /// <summary>
    /// Owns UI state: model list, manager state, navigation commands.
    /// Download lifecycle is delegated to <see cref="ModelsDownloadViewModel"/>.
    /// </summary>
    public sealed class ModelsViewModel : INotifyPropertyChanged
    {
        private readonly IModelConfigService _cfgSvc;
        private readonly string _remoteUrl;
        private string _manualTutorialUrl = string.Empty;

        public string ModelsDir { get; }

        // ── Download sub-VM (SRP: owns fetch + cancel) ───────────────────────
        public ModelsDownloadViewModel Download { get; }

        // ── State ────────────────────────────────────────────────────────────
        private ManagerState _state = ManagerState.Loading;
        public ManagerState State
        {
            get => _state;
            private set
            {
                _state = value;
                OnPC();
                OnPC(nameof(IsLoading));
                OnPC(nameof(IsAutomatic));
                OnPC(nameof(IsManualFallback));
            }
        }
        public bool IsLoading        => _state == ManagerState.Loading;
        public bool IsAutomatic      => _state == ManagerState.Automatic;
        public bool IsManualFallback => _state == ManagerState.ManualFallback;

        private string _status = string.Empty;
        public string StatusMessage
        {
            get => _status;
            private set { _status = value; OnPC(); OnPC(nameof(HasError)); }
        }
        public bool HasError => !string.IsNullOrEmpty(_status) && _state != ManagerState.Loading;

        public ObservableCollection<ModelItemViewModel> Models { get; } = new();

        // ── Commands ─────────────────────────────────────────────────────────
        public ICommand SearchModelsCommand     { get; }
        public ICommand ReadTutorialCommand     { get; }
        public ICommand OpenModelsFolderCommand { get; }
        public ICommand RefreshCommand          { get; }

        // Surface download commands as pass-throughs for XAML bindings
        public ICommand DownloadModelCommand  => Download.DownloadCommand;
        public ICommand CancelDownloadCommand => Download.CancelCommand;

        /// <summary>Forwarded from <see cref="ModelsDownloadViewModel"/>. Arg = full path.</summary>
        public event Action<string>? ModelFileDownloaded;

        public ModelsViewModel(
            IModelConfigService cfgSvc,
            IModelDownloadService dlSvc,
            string remoteUrl,
            string modelsDir)
        {
            _cfgSvc    = cfgSvc;
            _remoteUrl = remoteUrl;
            ModelsDir  = modelsDir;

            Download = new ModelsDownloadViewModel(dlSvc, modelsDir);
            Download.ModelFileDownloaded += path =>
            {
                RescanAsync();
                ModelFileDownloaded?.Invoke(path);
            };
            Download.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ModelsDownloadViewModel.StatusMessage))
                    StatusMessage = Download.StatusMessage;
            };

            SearchModelsCommand     = new RelayCommand(() => OpenUrl("https://huggingface.co/models?search=ggml+whisper"));
            ReadTutorialCommand     = new RelayCommand(() => OpenUrl(_manualTutorialUrl));
            OpenModelsFolderCommand = new RelayCommand(OnOpenFolder);
            RefreshCommand          = new RelayCommand(async () => await RescanAsync());
        }

        public async Task LoadAsync()
        {
            State = ManagerState.Loading;
            StatusMessage = "Fetching model list…";
            Models.Clear();
            try
            {
                var cfg = await _cfgSvc.GetModelConfigAsync(_remoteUrl);
                _manualTutorialUrl = cfg.ManualTutorialUrl;
                foreach (var m in cfg.Models) Models.Add(new ModelItemViewModel(m));
                await RescanAsync();
                State         = ManagerState.Automatic;
                StatusMessage = string.Empty;
            }
            catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
            catch (Exception ex)
            {
                StatusMessage = $"Could not load model list: {ex.Message}";
                State         = ManagerState.ManualFallback;
            }
        }

        public Task RescanAsync()
        {
            foreach (var item in Models)
                item.IsDownloaded = File.Exists(Path.Combine(ModelsDir, item.Id + ".bin"));
            return Task.CompletedTask;
        }

        private void OnOpenFolder()
        {
            Directory.CreateDirectory(ModelsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"\"{ModelsDir}\"",
                UseShellExecute = true
            });
        }

        private static void OpenUrl(string url)
        {
            if (!string.IsNullOrWhiteSpace(url))
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception ex)
                {
                    WhisperVoice.DiagnosticLogger.Instance.Error(
                        "ModelsViewModel", ex, "Failed to open URL");
                }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPC([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}