using System;
using System.Linq;
using System.Threading.Tasks;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer.Dialogs;

/// <summary>
/// Раздел «Записи» настроек: идущие записи (кнопка «Стоп»), запланированные
/// (кнопка «Убрать») и папка, куда ffmpeg сохраняет файлы (пусто —
/// «Видео\IptvPlayer»). Открывается из меню шестерёнки.
/// </summary>
public sealed partial class RecordingSettingsDialog : UserControl
{
    private readonly MainPageViewModel _viewModel;
    private readonly ISettingsService _settingsService;

    private ContentDialog? _hostDialog;

    public RecordingSettingsDialog(MainPageViewModel viewModel, ISettingsService settingsService)
    {
        _viewModel = viewModel;
        _settingsService = settingsService;
        InitializeComponent();
    }

    public async Task ShowAsync(XamlRoot xamlRoot)
    {
        // Идущие записи могут завершиться (-t), пока диалог открыт —
        // пересобираем списки на каждое изменение состояния сервиса.
        _viewModel.Recording.RecordingsChanged += OnRecordingsChanged;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = L.T("Записи", "Recordings"),
            Content = this,
            CloseButtonText = L.T("Закрыть", "Close")
        };
        _hostDialog = dialog;
        LoadSection();
        await dialog.ShowAsync();

        _viewModel.Recording.RecordingsChanged -= OnRecordingsChanged;
    }

    private void OnRecordingsChanged(object? sender, EventArgs e)
    {
        if (_hostDialog == null)
        {
            return;
        }
        // RecordingsChanged может прийти не с UI-потока (Exited процесса).
        DispatcherQueue.TryEnqueue(LoadSection);
    }

    private void LoadSection()
    {
        ActiveHeader.Text = L.T("Идут сейчас", "Recording now");
        ActiveList.Children.Clear();
        foreach (var rec in _viewModel.Recording.Active)
        {
            var id = rec.Id;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = L.T(
                    $"● {rec.ChannelName} — с {rec.StartedAt:HH:mm}",
                    $"● {rec.ChannelName} — since {rec.StartedAt:HH:mm}"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            var stop = new Button { Content = L.T("Стоп", "Stop"), Height = 32 };
            stop.Click += (s, e) => _viewModel.Recording.Stop(id);
            row.Children.Add(stop);
            ActiveList.Children.Add(row);
        }
        if (ActiveList.Children.Count == 0)
        {
            ActiveList.Children.Add(new TextBlock
            {
                Text = L.T("Нет активных записей.", "No active recordings."),
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }

        ScheduledHeader.Text = L.T("Запланированные", "Scheduled");
        ScheduledList.Children.Clear();
        foreach (var rec in _viewModel.AppSettings.ScheduledRecordings.OrderBy(r => r.StartTime).Take(15))
        {
            var scheduled = rec;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = L.T(
                    $"{rec.ChannelName} — {rec.ProgramName}, {rec.StartTime:HH:mm}",
                    $"{rec.ChannelName} — {rec.ProgramName}, {rec.StartTime:HH:mm}"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            var remove = new Button { Content = L.T("Убрать", "Remove"), Height = 32 };
            remove.Click += (s, e) =>
            {
                _viewModel.RemoveScheduledRecordingCommand.Execute(scheduled);
                LoadSection();
            };
            row.Children.Add(remove);
            ScheduledList.Children.Add(row);
        }
        if (ScheduledList.Children.Count == 0)
        {
            ScheduledList.Children.Add(new TextBlock
            {
                Text = L.T("Нет запланированных записей.", "No scheduled recordings."),
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }

        FolderHeader.Text = L.T("Папка записей", "Recordings folder");
        FolderHint.Text = L.T(
            "Куда ffmpeg сохраняет записи (.ts). Пусто — «Видео\\IptvPlayer». Корень диска выбирается вводом пути вручную, например F:\\ — системный диалог выбора папки корень диска не отдаёт.",
            "Where ffmpeg saves recordings (.ts). Empty = \"Videos\\IptvPlayer\". A drive root can be typed in manually, e.g. F:\\ — the system folder picker does not allow selecting a drive root.");
        FolderBox.PlaceholderText = RecordingService.DefaultFolder;
        BrowseButton.Content = L.T("Обзор...", "Browse...");
        OpenFolderButton.Content = L.T("Открыть папку", "Open folder");
        SaveButton.Content = L.T("Сохранить", "Save");
    }

    /// <summary>FolderPicker требует HWND-владельца (InitializeWithWindow).</summary>
    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        if (App.MainWindow is { } window)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        }

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            FolderBox.Text = folder.Path;
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = string.IsNullOrWhiteSpace(FolderBox.Text)
            ? RecordingService.DefaultFolder
            : FolderBox.Text.Trim();
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось открыть папку записей {Folder}.", folder);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = string.IsNullOrWhiteSpace(FolderBox.Text) ? null : FolderBox.Text.Trim();
        if (folder != null)
        {
            try
            {
                // Папки может не быть (ввели путь руками) — создаём заранее,
                // иначе ffmpeg молча не сможет начать запись.
                System.IO.Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Не удалось создать папку записей {Folder}.", folder);
                FolderHint.Text = L.T(
                    $"Не удалось создать папку «{folder}» — проверьте путь и права.",
                    $"Could not create \"{folder}\" — check the path and permissions.");
                return;
            }
        }

        _viewModel.AppSettings.RecordingsFolder = folder;
        await _settingsService.SaveAsync(_viewModel.AppSettings);
        _hostDialog?.Hide();
    }
}
