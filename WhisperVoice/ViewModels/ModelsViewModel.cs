using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WhisperVoice.Services;

namespace WhisperVoice.ViewModels
{
    public enum ManagerState { Loading, Automatic, ManualFallback }

    public sealed class ModelsViewModel : INotifyPropertyChanged
    {
        private readonly IModelConfigService _cfgSvc;
        private readonly IModelDownloadService _dlSvc;
        private readonly string _remoteUrl;

        public string ModelsDir { get; }

        private string _manualTutorialUrl = string.Empty;

        // ── State ──────────────────────────────────────────────────────────
        private ManagerState _state = ManagerState.Loading;
        public ManagerState State
        {
            get => _state;
            private set { _state = value; OnPC(); OnPC(nameof(IsLoading)); OnPC(nameof(IsAutomatic)); OnPC(nameof(IsManualFallback)); }
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

        private CancellationTokenSource? _cts;

        // ── Commands ───────────────────────────────────────────────────────
        public ICommand DownloadModelCommand    { get; }
        public ICommand CancelDownloadCommand   { get; }
        public ICommand SearchModelsCommand     { get; }
        public ICommand ReadTutorialCommand     { get; }
        public ICommand OpenModelsFolderCommand { get; }
        public ICommand RefreshCommand          { get; }

        /// <summary>Fired after a .bin file finishes downloading. Arg = full path.</summary>
        public event Action<string>? ModelFileDownloaded;

        public ModelsViewModel(IModelConfigService cfgSvc, IModelDownloadService dlSvc,
            string remoteUrl, string modelsDir)
        {
            _cfgSvc    = cfgSvc;
            _dlSvc     = dlSvc;
            _remoteUrl = remoteUrl;
            ModelsDir  = modelsDir;

            DownloadModelCommand    = new RelayCommand<ModelItemViewModel>(OnDownload, m => m?.CanDownload == true);
            CancelDownloadCommand   = new RelayCommand(OnCancel, () => _cts is not null);
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
                State = ManagerState.Automatic;
                StatusMessage = string.Empty;
            }
            catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
            catch (Exception ex)
            {
                StatusMessage = $"Could not load model list: {ex.Message}";
                State = ManagerState.ManualFallback;
            }
        }

        private async void OnDownload(ModelItemViewModel? item)
        {
            if (item is null || !item.CanDownload) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            item.IsDownloading    = true;
            item.DownloadProgress = -1;
            Directory.CreateDirectory(ModelsDir);
            var dest = Path.Combine(ModelsDir, item.Id + ".bin");
            // Progress<T> marshals back to the UI thread automatically (captured SyncCtx)
            var progress = new Progress<double>(p => item.DownloadProgress = p);
            try
            {
                await _dlSvc.DownloadAsync(item.Model.Url, dest, progress, ct);
                item.IsDownloaded  = true;
                item.IsDownloading = false;
                StatusMessage = string.Empty;
                ModelFileDownloaded?.Invoke(dest);
            }
            catch (OperationCanceledException)
            {
                item.IsDownloading    = false;
                item.DownloadProgress = 0;
            }
            catch (Exception ex)
            {
                item.IsDownloading    = false;
                item.DownloadProgress = 0;
                StatusMessage = $"Download failed: {ex.Message}";
                State = ManagerState.ManualFallback;
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void OnCancel() => _cts?.Cancel();

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
                FileName = "explorer.exe",
                Arguments = $"\"{ModelsDir}\"",
                UseShellExecute = true
            });
        }

        private static void OpenUrl(string url)
        {
            if (!string.IsNullOrWhiteSpace(url))
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { WhisperVoice.DiagnosticLogger.Instance.Error("ModelsViewModel", ex, "Failed to open URL"); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPC([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
