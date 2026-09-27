namespace IptvPlayer.Services;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

public enum OnlineCinemaDownloadState
{
    Running,
    Paused,
    Completed,
    Failed
}


// One row in the downloads menu: a single MP4 transfer with its own
// pause/resume/cancel cycle. The .part file is kept while paused or failed,
// so Resume/Retry continue from it (Range request). The CDN link token is
// hour-bound, so Resume asks the FreshUrlProvider for a fresh link and the
// .part keeps its offset — same file, same quality
public class OnlineCinemaDownloadItem : ObservableObject
{
    private readonly IOnlineCinemaDownloadService _service;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private bool _pauseRequested;
    private bool _cancelRequested;

    public OnlineCinemaDownloadItem(IOnlineCinemaDownloadService service, ILogger logger,
        OnlineCinemaDownloadRequest request)
    {
        _service = service;
        _logger = logger;
        Request = request;
        _url = request.Url;
        Url = request.Url;
        PauseCommand = new RelayCommand(Pause);
        ResumeCommand = new RelayCommand(Resume);
        CancelCommand = new RelayCommand(Cancel);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        RetryCommand = new RelayCommand(Retry);
        RemoveCommand = new RelayCommand(Remove);
    }

    public OnlineCinemaDownloadRequest Request { get; }

    public string FileName => Request.FileName;

    public string TargetPath => Request.TargetPath;

    // Fresh link per resume — the token in the path expires
    private string _url;

    public string Url
    {
        get => _url;
        private set => SetProperty(ref _url, value);
    }

    private OnlineCinemaDownloadState _state = OnlineCinemaDownloadState.Running;

    public OnlineCinemaDownloadState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsNotFinished));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsRemovable));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(PercentIndeterminate));
            }
        }
    }

    private int _percent;

    public int Percent
    {
        get => _percent;
        private set
        {
            if (SetProperty(ref _percent, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(PercentIndeterminate));
            }
        }
    }

    public bool PercentIndeterminate => Percent < 0;

    public bool IsRunning => State == OnlineCinemaDownloadState.Running;

    public bool IsPaused => State == OnlineCinemaDownloadState.Paused;

    public bool IsCompleted => State == OnlineCinemaDownloadState.Completed;

    public bool IsFailed => State == OnlineCinemaDownloadState.Failed;

    // Button visibility helpers for the downloads menu template
    public bool IsNotFinished => State is OnlineCinemaDownloadState.Running or OnlineCinemaDownloadState.Paused;

    public bool IsFinished => State is OnlineCinemaDownloadState.Completed or OnlineCinemaDownloadState.Failed;

    public bool IsActive => IsNotFinished;

    public bool IsRemovable => IsFinished;

    // Percent while running, plain status word otherwise
    public string StateText => State switch
    {
        OnlineCinemaDownloadState.Paused => L.T("OnlineCinema_Status_Pauza"),
        OnlineCinemaDownloadState.Completed => L.T("OnlineCinema_Status_Gotovo"),
        OnlineCinemaDownloadState.Failed => L.T("OnlineCinema_Status_Oshibka"),
        _ => Percent < 0 ? "…" : $"{Percent}%"
    };

    public IRelayCommand PauseCommand { get; }

    public IRelayCommand ResumeCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand OpenFolderCommand { get; }

    public IRelayCommand RetryCommand { get; }

    public IRelayCommand RemoveCommand { get; }

    // Manager listens: removes this item from the list
    public event EventHandler? RemoveRequested;

    internal void Start()
    {
        _cancelRequested = false;
        _pauseRequested = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _ = RunAsync();
    }

    // Initial state for entries restored from the settings after a restart
    internal void MarkPaused() => State = OnlineCinemaDownloadState.Paused;

    internal void MarkCompleted() => State = OnlineCinemaDownloadState.Completed;

    private async Task RunAsync()
    {
        State = OnlineCinemaDownloadState.Running;
        Percent = -1;
        var progress = new Progress<int>(p => Percent = p < 0 ? -1 : Math.Clamp(p, 0, 100));
        try
        {
            // Thread pool: the copy loop must not hop onto the UI thread on
            // every chunk; Progress<int> marshals the reports back
            await Task.Run(() => Request.IsHls
                ? _service.DownloadHlsAsync(Url, TargetPath, progress, _cts!.Token)
                : _service.DownloadAsync(Url, TargetPath, progress, _cts!.Token));
            State = OnlineCinemaDownloadState.Completed;
        }
        catch (OperationCanceledException)
        {
            if (_cancelRequested)
            {
                TryDeletePartFile();
                RemoveRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (_pauseRequested)
            {
                State = OnlineCinemaDownloadState.Paused;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Онлайн-кинотеатр: скачивание не удалось — {Url}.", Url);
            State = OnlineCinemaDownloadState.Failed;
        }
    }

    private void Pause()
    {
        if (State == OnlineCinemaDownloadState.Running)
        {
            _pauseRequested = true;
            _cts?.Cancel();
        }
    }

    private async void Resume()
    {
        if (State != OnlineCinemaDownloadState.Paused)
        {
            return;
        }

        State = OnlineCinemaDownloadState.Running;
        Percent = -1;
        // The old link may be expired (hour-bound token) — take a fresh one;
        // the .part keeps its offset, so the download continues from it
        if (Request.FreshUrl != null)
        {
            try
            {
                var fresh = await Request.FreshUrl();
                if (!string.IsNullOrEmpty(fresh))
                {
                    Url = fresh;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Онлайн-кинотеатр: не удалось обновить ссылку для докачки.");
            }
        }

        if (State == OnlineCinemaDownloadState.Running)
        {
            Start();
        }
    }

    private async void Retry()
    {
        if (State != OnlineCinemaDownloadState.Failed)
        {
            return;
        }

        State = OnlineCinemaDownloadState.Running;
        Percent = -1;
        if (Request.FreshUrl != null)
        {
            try
            {
                var fresh = await Request.FreshUrl();
                if (!string.IsNullOrEmpty(fresh))
                {
                    Url = fresh;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Онлайн-кинотеатр: не удалось обновить ссылку для повтора.");
            }
        }

        if (State == OnlineCinemaDownloadState.Running)
        {
            Start();
        }
    }

    private void Cancel()
    {
        if (State is OnlineCinemaDownloadState.Paused)
        {
            _cancelRequested = true;
            TryDeletePartFile();
            RemoveRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (State == OnlineCinemaDownloadState.Running)
        {
            _cancelRequested = true;
            _cts?.Cancel();
        }
    }

    private void OpenFolder()
    {
        var folder = Path.GetDirectoryName(TargetPath);
        // Explorer wants /select,<path> in one argument; a quote is an
        // invalid path character on Windows, but guard explicitly anyway —
        // nothing user-influenced must reach the command line unvalidated
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || TargetPath.Contains('"'))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{TargetPath}\"",
            UseShellExecute = true
        });
    }

    private void Remove()
    {
        if (State == OnlineCinemaDownloadState.Failed)
        {
            TryDeletePartFile();
        }

        RemoveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TryDeletePartFile()
    {
        try
        {
            if (File.Exists(TargetPath + ".part"))
            {
                File.Delete(TargetPath + ".part");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Онлайн-кинотеатр: не удалось удалить .part файл.");
        }
    }
}


// Everything needed to (re)create a download row, including enough context
// to refresh the expired CDN link on resume
public class OnlineCinemaDownloadRequest
{
    public string Url { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public string Quality { get; set; } = string.Empty;

    public string Origin { get; set; } = string.Empty;

    public string LeafData { get; set; } = string.Empty;

    public string EmbedUrl { get; set; } = string.Empty;

    // Film page URL — enough to re-resolve a fresh HLS link for nextembed
    // streams (no cinemar leaf payload exists for them)
    public string PageUrl { get; set; } = string.Empty;

    // true → HLS remux via bundled ffmpeg (nextembed streams); false → direct MP4
    public bool IsHls { get; set; }

    // Returns a fresh CDN link for the same quality
    public Func<Task<string?>>? FreshUrl { get; set; }
    public string EpisodeKey { get; set; } = string.Empty;
}
