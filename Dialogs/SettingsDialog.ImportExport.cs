using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace IptvPlayer.Dialogs;

/// <summary>Экспорт/импорт всех настроек в JSON и кнопка «О приложении».</summary>
public sealed partial class SettingsDialog
{
    private void SetImportExportStatus(string text)
    {
        ImportExportStatusText.Text = text;
        ImportExportStatusText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Пикеры WinUI 3 требуют HWND-владельца (InitializeWithWindow), иначе
    /// PickSingleFileAsync/PickSaveFileAsync падают с «Invalid window handle»
    /// (особенно в unpackaged-сборке). Получить окно из XamlRoot нельзя,
    /// поэтому берём главное окно приложения.
    /// </summary>
    private static void InitializePickerOwner(object picker)
    {
        if (App.MainWindow is { } window)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "iptvplayer-settings"
        };
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        InitializePickerOwner(picker);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return; // Пользователь отменил диалог.
        }

        try
        {
            // Экспортируем каноническую копию ViewModel: избранное и напоминания
            // могли измениться после открытия диалога настроек.
            var json = JsonSerializer.Serialize(
                _viewModel.AppSettings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(file.Path, json);
            _logger.LogInformation("Настройки экспортированы в {Path}.", file.Path);
            SetImportExportStatus(L.T(
                $"Настройки экспортированы: {file.Path}",
                $"Settings exported to: {file.Path}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось экспортировать настройки в {Path}.", file.Path);
            SetImportExportStatus(L.T(
                $"Не удалось экспортировать: {ex.Message}",
                $"Export failed: {ex.Message}"));
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".json");
        InitializePickerOwner(picker);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return; // Пользователь отменил диалог.
        }

        try
        {
            var json = await File.ReadAllTextAsync(file.Path);
            var imported = JsonSerializer.Deserialize<AppSettings>(json);
            if (imported is null)
            {
                SetImportExportStatus(L.T(
                    "Файл не похож на настройки IptvPlayer (пустой JSON).",
                    "The file does not look like IptvPlayer settings (empty JSON)."));
                return;
            }

            // Машинно-зависимое и сессионное остаётся текущим: геометрия окна
            // с другой машины (или монитор отключён) и взведённый таймер сна.
            imported.WindowPlacement = _viewModel.AppSettings.WindowPlacement;
            imported.SleepTimerMinutes = _viewModel.AppSettings.SleepTimerMinutes;

            // Обновляем обе копии: каноническую (её читает MainPage при
            // работе) и рабочую копию диалога (из неё LoadAsync наполнит
            // контролы). Сохраняем ДО перезагрузки UI.
            _viewModel.AppSettings = imported;
            await _settingsService.SaveAsync(imported);

            // Применяем сразу то, что обычно применяется на лету: тему, язык,
            // файловый лог, оверлей статистики и аудио фильтры играющего канала.
            _applyTheme(imported.Theme);
            L.SetLanguage(imported.Language);
            _applyLanguage();
            App.SetFileLoggingEnabled(imported.FileLoggingEnabled);
            _applyStatsOverlay(imported.StatsOverlayVisible);
            _streamService.ApplyAudioFilters(
                _viewModel.Player.Player, imported.AudioNormalization);

            // Перезаполняем контролы диалога импортированными значениями —
            // иначе «Готово» записал бы обратно устаревшие значения контролов.
            await LoadAsync();

            // Снимок источников для «Готово» НЕ обновляем: если импорт принёс
            // другой набор источников EPG, закрытие диалога должно запустить
            // фоновое обновление EPG (как при ручном редактировании).
            _logger.LogInformation("Настройки импортированы из {Path}.", file.Path);
            SetImportExportStatus(L.T(
                "Настройки импортированы и применены. Изменившиеся источники EPG обновятся после закрытия диалога.",
                "Settings imported and applied. Changed EPG sources refresh after closing the dialog."));
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Файл {Path} не является корректным JSON настроек.", file.Path);
            SetImportExportStatus(L.T(
                "Файл не похож на настройки IptvPlayer (некорректный JSON).",
                "The file does not look like IptvPlayer settings (invalid JSON)."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось импортировать настройки из {Path}.", file.Path);
            SetImportExportStatus(L.T(
                $"Не удалось импортировать: {ex.Message}",
                $"Import failed: {ex.Message}"));
        }
    }

    // ===================== О приложении =====================

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        AboutPanel.Visibility = AboutPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
