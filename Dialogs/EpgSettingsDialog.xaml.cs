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

        // XamlRoot хост-диалога: вложенный диалог подтверждения нужен ПОСЛЕ
        // Hide хоста, когда собственный XamlRoot этого UserControl уже null.
        private XamlRoot? _hostXamlRoot;

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

            var dialog = new ThemedContentDialog
            {
                XamlRoot = xamlRoot,
                Title = L.T("Nastroyki_EPG_Lbl"),
                Content = this
            };
            _hostDialog = dialog;
            _hostXamlRoot = xamlRoot;
            await dialog.ShowAsync();
        }

        private async Task LoadAsync()
        {
            var settings = await _settingsService.LoadAsync();

            TitleText.Text = L.T("Nastroyki_EPG_Lbl");
            EpgSourcesHeader.Text = L.T("Istochniki_EPG_Lbl");

            // Вкладка правит ОБЩИЙ список источников: он действует только для
            // плейлистов без собственных (PlaylistSource.EpgSources пуст).
            // Свои источники плейлист настраивается в диалоге «Плейлист».
            EpgSourcesHint.Text = L.T("Obshchie_Istochniki_EPG_XMLTV_Deystvuyut_Dlya");
            EpgUrlBox.PlaceholderText = L.T("URL_XMLTV");
            AddEpgSourceButton.Content = L.T("Dobavit_Lbl");
            RemindersHeader.Text = L.T("Napominaniya_EPG_Lbl");
            RemindersHint.Text = L.T("Za_Skolko_Minut_Do_Nachala_Peredachi_Lbl");
            EpgRefreshHeader.Text = L.T("EPG_Programma_Peredach_Lbl");
            EpgRefreshHint.Text = L.T("Kak_Chasto_Pri_Zapuske_Perekachivat_XMLTV_Lbl");
            CancelButton.Content = L.T("Otmena_Lbl");
            SaveButton.Content = L.T("Sokhranit_Lbl");

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
                    Content = string.Format(L.T("Za_0_Min_Do_Nachala"), minutes, minutes),
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
                         (L.T("Kazhdyy_Den"), 1),
                         (L.T("Kazhdye_3_Dnya"), 3),
                         (L.T("Kazhduyu_Nedelyu"), 7),
                         (L.T("Tolko_Vruchnuyu"), 0),
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
                ToolTipService.SetToolTip(checkBox, L.T("Ispolzovat_Istochnik_Lbl"));
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
                    Width = 32,
                    Height = 32,
                    VerticalAlignment = VerticalAlignment.Center
                };
                // Иконка-корзина вместо текстового «✕» (шрифт без глифа
                // показывал «?»), удаление — через окно подтверждения.
                removeButton.Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 };
                ToolTipService.SetToolTip(removeButton, L.T("Udalit_Istochnik_Lbl"));
                removeButton.Click += async (_, _) => await RemoveEpgSourceWithConfirmAsync(source);

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

        /// <summary>
        /// Удаление источника с подтверждением отдельным окном. Хост-диалог
        /// прячется — два ContentDialog одновременно показать нельзя — и
        /// показывается снова после ответа.
        /// </summary>
        private async Task RemoveEpgSourceWithConfirmAsync(EPGSource source)
        {
            var root = _hostXamlRoot;
            if (root == null)
            {
                return;
            }

            _hostDialog?.Hide();
            await Task.Delay(50);

            bool confirmed;
            try
            {
                var dialog = new ThemedContentDialog
                {
                    XamlRoot = root,
                    Title = L.T("Udalit_Istochnik_EPG_Lbl"),
                    Content = string.Format(L.T("Udalit_Istochnik_EPG_Vopros_0"), source.Url),
                    PrimaryButtonText = L.T("Udalit_Lbl"),
                    CloseButtonText = L.T("Otmena_Lbl"),
                    DefaultButton = ContentDialogButton.Close
                };
                confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            finally
            {
                if (_hostDialog != null)
                {
                    _ = _hostDialog.ShowAsync();
                }
            }

            if (!confirmed || _epgSources.Contains(source) == false)
            {
                return;
            }

            _epgSources.Remove(source);
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
