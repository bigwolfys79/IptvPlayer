using System;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer.Dialogs
{
    /// <summary>
    /// Раздел EPG настроек: напоминания о передачах, периодичность обновления
    /// программы передач и глубина архива (сколько дней назад хранить
    /// передачи). Источники XMLTV здесь не редактируются — они настраиваются
    /// для каждого плейлиста в диалоге «Плейлист». Сохраняет в каноническую
    /// копию AppSettings (ViewModel.AppSettings), как SettingsDialog; при
    /// изменении глубины архива форсирует перезагрузку EPG фоном — кэш
    /// источников спарсен со старым окном.
    /// </summary>
    public sealed partial class EpgSettingsDialog : UserControl
    {
        private readonly MainPageViewModel _viewModel;
        private readonly ISettingsService _settingsService;

        private int _initialArchiveDaysBack;

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


            TitleText.Visibility = Visibility.Collapsed;

            var dialog = new ThemedContentDialog
            {
                XamlRoot = xamlRoot,
                Title = L.T("Nastroyki_EPG_Lbl"),
                Content = this
            };
            _hostDialog = dialog;
            await dialog.ShowAsync();
        }

        private async Task LoadAsync()
        {
            var settings = await _settingsService.LoadAsync();

            TitleText.Text = L.T("Nastroyki_EPG_Lbl");
            RemindersHeader.Text = L.T("Napominaniya_EPG_Lbl");
            RemindersHint.Text = L.T("Za_Skolko_Minut_Do_Nachala_Peredachi_Lbl");
            EpgRefreshHeader.Text = L.T("EPG_Programma_Peredach_Lbl");
            EpgRefreshHint.Text = L.T("Kak_Chasto_Pri_Zapuske_Perekachivat_XMLTV_Lbl");
            EpgArchiveHeader.Text = L.T("Glubina_Arkhiva_Lbl");
            EpgArchiveHint.Text = L.T("Skolko_Dney_Nazad_Khranit_Peredachi_Lbl");
            CancelButton.Content = L.T("Otmena_Lbl");
            SaveButton.Content = L.T("Sokhranit_Lbl");


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


            EpgArchiveCombo.Items.Clear();
            foreach (var (label, days) in new[]
                     {
                         (L.T("1_Den_Nazad"), 1),
                         (L.T("3_Dnya_Nazad"), 3),
                         (L.T("7_Dney_Nazad"), 7),
                     })
            {
                EpgArchiveCombo.Items.Add(new ComboBoxItem { Content = label, Tag = days });
                if (days == settings.EpgArchiveDaysBack)
                {
                    EpgArchiveCombo.SelectedIndex = EpgArchiveCombo.Items.Count - 1;
                }
            }
            if (EpgArchiveCombo.SelectedIndex < 0)
            {
                EpgArchiveCombo.SelectedIndex = 1;
            }
            _initialArchiveDaysBack = settings.EpgArchiveDaysBack;
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {


            var appSettings = _viewModel.AppSettings;

            if (ReminderMinutesCombo.SelectedItem is ComboBoxItem { Tag: int reminderMinutes })
            {
                appSettings.ReminderMinutes = reminderMinutes;
            }
            if (EpgRefreshCombo.SelectedItem is ComboBoxItem { Tag: int refreshDays })
            {
                appSettings.EpgRefreshDays = refreshDays;
            }
            if (EpgArchiveCombo.SelectedItem is ComboBoxItem { Tag: int archiveDays })
            {
                appSettings.EpgArchiveDaysBack = archiveDays;
            }

            await _settingsService.SaveAsync(appSettings);

            if (appSettings.EpgArchiveDaysBack != _initialArchiveDaysBack)
            {

                _initialArchiveDaysBack = appSettings.EpgArchiveDaysBack;
                _ = RefreshEpgInBackgroundAsync();
            }

            CloseDialog();
        }

        private async Task RefreshEpgInBackgroundAsync()
        {
            try
            {
                await _viewModel.EpgViewModel.RefreshEPGAsync();
            }
            catch
            {


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
