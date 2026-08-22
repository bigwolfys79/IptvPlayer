using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IptvPlayer.Dialogs
{
    /// <summary>
    /// Раздел EPG настроек: источники XMLTV, напоминания о передачах и
    /// периодичность обновления программы передач. Сохраняет в каноническую
    /// копию AppSettings (ViewModel.AppSettings), как SettingsDialog; при
    /// изменении списка источников форсирует перезагрузку EPG фоном.
    /// </summary>
    public sealed partial class EpgSettingsDialog : UserControl
    {
        private readonly MainPageViewModel _viewModel;
        private readonly ISettingsService _settingsService;

        private readonly ObservableCollection<EPGSource> _epgSources = new();
        private List<(string Url, bool IsEnabled)> _initialEpgSources = new();

        // Контейнер-ContentDialog создаётся в ShowAsync; кнопки внутри
        // UserControl закрывают его через эту ссылку (искать родителя по
        // визуальному дереву нельзя — им оказывается ContentPresenter
        // шаблона диалога, а не сам ContentDialog).
        private ContentDialog? _hostDialog;

        public EpgSettingsDialog(MainPageViewModel viewModel, ISettingsService settingsService)
        {
            _viewModel = viewModel;
            _settingsService = settingsService;
            InitializeComponent();
        }

        public async Task ShowAsync(XamlRoot xamlRoot)
        {
            await LoadAsync();
            // Заголовок показывает сам ContentDialog — внутренний TitleText
            // не нужен, иначе «Настройки EPG» читается дважды.
            TitleText.Visibility = Visibility.Collapsed;

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = L.T("Настройки EPG", "EPG settings"),
                Content = this
            };
            _hostDialog = dialog;
            await dialog.ShowAsync();
        }

        private async Task LoadAsync()
        {
            var settings = await _settingsService.LoadAsync();

            TitleText.Text = L.T("Настройки EPG", "EPG settings");
            EpgSourcesHeader.Text = L.T("Источники EPG", "EPG sources");

            // Вкладка правит ОБЩИЙ список источников: он действует только для
            // плейлистов без собственных (PlaylistSource.EpgSources пуст).
            // Свои источники плейлист настраивается в диалоге «Плейлист».
            EpgSourcesHint.Text = L.T(
                "Общие источники EPG (XMLTV): действуют для плейлистов без своих источников. Свои источники задаются у каждого плейлиста в диалоге «Плейлист».",
                "Shared EPG (XMLTV) sources: used by playlists that have no sources of their own. Per-playlist sources are configured in the Playlist dialog.");
            EpgUrlBox.PlaceholderText = L.T("URL XMLTV", "XMLTV URL");
            AddEpgSourceButton.Content = L.T("Добавить", "Add");
            RemindersHeader.Text = L.T("Напоминания EPG", "EPG reminders");
            RemindersHint.Text = L.T(
                "За сколько минут до начала передачи показывать тост-напоминание.",
                "How many minutes before a programme starts to show a toast reminder.");
            EpgRefreshHeader.Text = L.T("EPG (программа передач)", "EPG (TV guide)");
            EpgRefreshHint.Text = L.T(
                "Как часто при запуске перекачивать XMLTV-источники.",
                "How often to re-download XMLTV sources on startup.");
            CancelButton.Content = L.T("Отмена", "Cancel");
            SaveButton.Content = L.T("Сохранить", "Save");

            _epgSources.Clear();
            foreach (var source in settings.EpgSources)
            {
                _epgSources.Add(new EPGSource { Url = source.Url, IsEnabled = source.IsEnabled });
            }
            _initialEpgSources = _epgSources.Select(s => (s.Url, s.IsEnabled)).ToList();
            UpdateEpgSourcesDisplay();

            // Напоминания: за 1/5/10/15/30 минут до начала передачи.
            ReminderMinutesCombo.Items.Clear();
            foreach (var minutes in new[] { 1, 5, 10, 15, 30 })
            {
                ReminderMinutesCombo.Items.Add(new ComboBoxItem
                {
                    Content = L.T($"За {minutes} мин до начала", $"{minutes} min before start"),
                    Tag = minutes
                });
                if (minutes == settings.ReminderMinutes)
                {
                    ReminderMinutesCombo.SelectedIndex = ReminderMinutesCombo.Items.Count - 1;
                }
            }
            if (ReminderMinutesCombo.SelectedIndex < 0)
            {
                ReminderMinutesCombo.SelectedIndex = 1;
            }

            // Периодичность обновления EPG при запуске: 1/3/7 дней или вручную.
            EpgRefreshCombo.Items.Clear();
            foreach (var (label, days) in new[]
                     {
                         (L.T("Каждый день", "Daily"), 1),
                         (L.T("Каждые 3 дня", "Every 3 days"), 3),
                         (L.T("Каждую неделю", "Weekly"), 7),
                         (L.T("Только вручную", "Manual only"), 0),
                     })
            {
                EpgRefreshCombo.Items.Add(new ComboBoxItem { Content = label, Tag = days });
                if (days == settings.EpgRefreshDays)
                {
                    EpgRefreshCombo.SelectedIndex = EpgRefreshCombo.Items.Count - 1;
                }
            }
            if (EpgRefreshCombo.SelectedIndex < 0)
            {
                EpgRefreshCombo.SelectedIndex = 0;
            }
        }

        private void UpdateEpgSourcesDisplay()
        {
            EpgSourcesContainer.Children.Clear();

            foreach (var source in _epgSources)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

                var checkBox = new CheckBox
                {
                    IsChecked = source.IsEnabled,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(checkBox, L.T("Использовать источник", "Use this source"));
                checkBox.Checked += (_, _) => source.IsEnabled = true;
                checkBox.Unchecked += (_, _) => source.IsEnabled = false;

                var textBox = new TextBox
                {
                    Text = source.Url,
                    Width = 290,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                };
                textBox.TextChanged += (_, _) => source.Url = textBox.Text;

                var removeButton = new Button
                {
                    Content = "✕",
                    Width = 32,
                    Height = 32,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(removeButton, L.T("Удалить источник", "Remove source"));
                removeButton.Click += (_, _) =>
                {
                    _epgSources.Remove(source);
                    UpdateEpgSourcesDisplay();
                };

                row.Children.Add(checkBox);
                row.Children.Add(textBox);
                row.Children.Add(removeButton);
                EpgSourcesContainer.Children.Add(row);
            }
        }

        private void AddEpgSourceButton_Click(object sender, RoutedEventArgs e)
        {
            var url = EpgUrlBox.Text?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            _epgSources.Add(new EPGSource { Url = url, IsEnabled = true });
            EpgUrlBox.Text = string.Empty;
            UpdateEpgSourcesDisplay();
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Пишем в каноническую копию AppSettings: избранное, напоминания
            // и плейлист могли измениться, пока диалог был открыт.
            var appSettings = _viewModel.AppSettings;
            appSettings.EpgSources = _epgSources.ToList();

            if (ReminderMinutesCombo.SelectedItem is ComboBoxItem { Tag: int reminderMinutes })
            {
                appSettings.ReminderMinutes = reminderMinutes;
            }
            if (EpgRefreshCombo.SelectedItem is ComboBoxItem { Tag: int refreshDays })
            {
                appSettings.EpgRefreshDays = refreshDays;
            }

            await _settingsService.SaveAsync(appSettings);

            if (EpgSourcesChanged())
            {
                // EPGService кэширует распарсенный EPG на сессию — без явного
                // Refresh новые источники не подхватятся. Fire-and-forget:
                // диалог закрывается сразу, перекачка идёт фоном.
                _initialEpgSources = _epgSources.Select(s => (s.Url, s.IsEnabled)).ToList();
                _ = RefreshEpgInBackgroundAsync();
            }

            CloseDialog();
        }

        private bool EpgSourcesChanged()
        {
            var current = _epgSources.Select(s => (s.Url, s.IsEnabled)).ToList();
            if (current.Count != _initialEpgSources.Count)
            {
                return true;
            }
            for (var i = 0; i < current.Count; i++)
            {
                if (current[i] != _initialEpgSources[i])
                {
                    return true;
                }
            }
            return false;
        }

        private async Task RefreshEpgInBackgroundAsync()
        {
            try
            {
                await _viewModel.EpgViewModel.RefreshEPGAsync();
            }
            catch
            {
                // RefreshEPGAsync логирует ошибку сама и ретбросит исключение;
                // здесь ретбросить некому (fire-and-forget) — просто гасим.
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CloseDialog();
        }

        private void CloseDialog()
        {
            _hostDialog?.Hide();
        }
    }
}
