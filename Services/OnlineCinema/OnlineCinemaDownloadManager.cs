namespace IptvPlayer.Services;

using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

// Owns every download row shown in the downloads menu. Downloads are
// independent of playback — switching films or playlists never touches them.
// The list survives app restarts: the manager raises Changed on every
// structural/state change so the host can persist entries, and restores them
// via Start (fresh download), RestorePaused / RestoreCompleted
public class OnlineCinemaDownloadManager : ObservableObject
{
    private readonly IOnlineCinemaDownloadService _service;
    private readonly ILogger _logger;

    public OnlineCinemaDownloadManager(IOnlineCinemaDownloadService service, ILogger<OnlineCinemaDownloadManager> logger)
    {
        _service = service;
        _logger = logger;
        Items.CollectionChanged += (_, _) => HasItems = Items.Count > 0;
    }

    public ObservableCollection<OnlineCinemaDownloadItem> Items { get; } = new();

    private bool _hasItems;

    public bool HasItems
    {
        get => _hasItems;
        private set => SetProperty(ref _hasItems, value);
    }

    // Anything changed (row added/removed, state changed) — persist the list
    public event EventHandler? Changed;

    // Queues a fresh download; null when the same target file is already
    // queued or downloading (a second click on the same episode must not
    // fork the .part)
    public OnlineCinemaDownloadItem? Start(OnlineCinemaDownloadRequest request)
    {
        if (Items.Any(i => i.TargetPath.Equals(request.TargetPath, StringComparison.OrdinalIgnoreCase) &&
                           i.State is not OnlineCinemaDownloadState.Failed))
        {
            return null;
        }

        var item = new OnlineCinemaDownloadItem(_service, _logger, request);
        WireItem(item);
        Items.Insert(0, item);
        item.Start();
        return item;
    }

    // Unfinished entry from the previous session — Paused, resumable
    public OnlineCinemaDownloadItem RestorePaused(OnlineCinemaDownloadRequest request)
    {
        var item = new OnlineCinemaDownloadItem(_service, _logger, request);
        WireItem(item);
        item.MarkPaused();
        Items.Insert(0, item);
        return item;
    }

    // Finished entry from the previous session — Completed, openable
    public OnlineCinemaDownloadItem RestoreCompleted(OnlineCinemaDownloadRequest request)
    {
        var item = new OnlineCinemaDownloadItem(_service, _logger, request);
        WireItem(item);
        item.MarkCompleted();
        Items.Insert(0, item);
        return item;
    }

    // The downloads menu "clear" button: drops only the finished rows,
    // the files stay on disk
    public void ClearCompleted()
    {
        foreach (var item in Items.Where(i => i.IsCompleted).ToList())
        {
            Items.Remove(item);
        }
    }

    private void WireItem(OnlineCinemaDownloadItem item)
    {
        item.RemoveRequested += (_, _) => Items.Remove(item);
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OnlineCinemaDownloadItem.State))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
    }
}
