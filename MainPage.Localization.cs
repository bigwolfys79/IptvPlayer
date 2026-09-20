using IptvPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer;

// Runtime localization for x:Uid elements: PrimaryLanguageOverride is
// unavailable in unpackaged builds, so these are applied via L.T
public sealed partial class MainPage
{
    private void ApplyXamlLocalization()
    {
        // Top bar / lists
        ChannelsHeaderText.Text = L.T("Kanaly.Text");
        OverlayChannelsHeaderText.Text = L.T("Kanaly.Text");
        ChannelSearchBox.PlaceholderText = L.T("Poisk.PlaceholderText");
        ToolTipService.SetToolTip(AddChannelButton, L.T("Dobavit_Kanal_Vruchnuyu.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(ResetFiltersButton, L.T("Sbrosit_Filtry.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(PosterViewToggleButton, L.T("Vid_Spisok_Postery.ToolTipService.ToolTip"));

        // Windowed overlay bar
        ToolTipService.SetToolTip(WindowedSleepTimerCancelButton, L.T("Otmenit_Taymer.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(VideoOverlayPrevChannelButton, L.T("Predydushchiy_Kanal_Backspace.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(VideoOverlayRecordButton, L.T("Zapisat_Kanal.ToolTipService.ToolTip"));
        VideoOverlayBackToLiveButton.Content = L.T("V_Efir.Content");
        ToolTipService.SetToolTip(VideoOverlayPauseButton, L.T("Pauza_Arkhiv.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(WindowedArchiveSeekPanel, L.T("Peremotka_Arkhiva_Otpustite_Polzunok_Dlya_Perekhoda.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(WindowedVodSeekPanel, L.T("Peremotka_Video_Otpustite_Polzunok.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(WindowedVodSeasonCombo, L.T("Sezon_2.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(WindowedVodEpisodeCombo, L.T("Seriya.ToolTipService.ToolTip"));
        WindowedVodQualityButton.Content = L.T("Avto.Content");
        ToolTipService.SetToolTip(VideoOverlayMuteButton, L.T("Bez_Zvuka_M.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(VideoOverlayVolumeSlider, L.T("Gromkost_Ili_Koleso_Myshi_Nad_Video.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(VideoOverlayStretchButton, L.T("Rezhim_Otobrazheniya_V.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(VideoOverlayUpscalerButton, L.T("Kachestvo_Kartinki.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(VideoOverlayEpgButton, L.T("Pokazat_EPG.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(VideoOverlaySettingsButton, L.T("Nastroyki.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(VideoOverlaySleepTimerButton, L.T("Taymer_Sna.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(VideoOverlayAlwaysOnTopButton, L.T("Poverkh_Vsekh_Okon_Vkl.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(VideoOverlayFullScreenButton, L.T("Razvernut_Pleer_Na_Ves_Ekran.ToolTipService.ToolTip"));

        // Upscaler menus (windowed + overlay)
        UpscalerOffItem.Text = L.T("Usilitel_Vyklyuch.Text");
        UpscalerSharpItem.Text = L.T("Usilitel_Rezkost.Text");
        UpscalerDenoiseItem.Text = L.T("Usilitel_Chistka.Text");
        UpscalerSdItem.Text = L.T("Usilitel_SD.Text");
        VideoOverlayFrameServerItem.Text = L.T("Usilitel_Render_Eks.Text");
        OverlayUpscalerOffItem.Text = L.T("Usilitel_Vyklyuch.Text");
        OverlayUpscalerSharpItem.Text = L.T("Usilitel_Rezkost.Text");
        OverlayUpscalerDenoiseItem.Text = L.T("Usilitel_Chistka.Text");
        OverlayUpscalerSdItem.Text = L.T("Usilitel_SD.Text");
        OverlayFrameServerItem.Text = L.T("Usilitel_Render_Eks.Text");

        // Settings menu
        SettingsPlaybackItem.Text = L.T("Vosproizvedenie.Text");
        SettingsInterfaceItem.Text = L.T("Interfeys.Text");
        SettingsPlaylistItem.Text = L.T("Pleylist.Text");
        SettingsRecordingItem.Text = L.T("Zapisi.Text");
        SettingsParentalItem.Text = L.T("Roditelskiy_Kontrol.Text");
        SettingsDiagnosticsItem.Text = L.T("Diagnostika.Text");
        SwitchPlaylistSubMenu.Text = L.T("Smenit_Pleylist.Text");
        SettingsLicenseItem.Text = L.T("Litsenziya.Text");
        SettingsAboutItem.Text = L.T("O_Programme.Text");

        // Fullscreen overlay bar
        ToolTipService.SetToolTip(OverlaySleepTimerCancelButton, L.T("Otmenit_Taymer.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(OverlayPrevChannelButton, L.T("Predydushchiy_Kanal_Backspace.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(OverlayRecordButton, L.T("Zapisat_Kanal.ToolTipService.ToolTip"));
        OverlayBackToLiveButton.Content = L.T("V_Efir.Content");
        ToolTipService.SetToolTip(OverlayPauseButton, L.T("Pauza_Arkhiv.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(OverlayArchiveSeekPanel, L.T("Peremotka_Arkhiva_Otpustite_Polzunok_Dlya_Perekhoda.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(OverlayVodSeekPanel, L.T("Peremotka_Video_Otpustite_Polzunok.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(OverlayVodSeasonCombo, L.T("Sezon_2.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(OverlayVodEpisodeCombo, L.T("Seriya.ToolTipService.ToolTip"));
        OverlayVodQualityButton.Content = L.T("Avto.Content");
        ToolTipService.SetToolTip(OverlayMuteButton, L.T("Bez_Zvuka_M.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(OverlayStretchButton, L.T("Rezhim_Otobrazheniya_V.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(OverlayUpscalerButton, L.T("Kachestvo_Kartinki.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(OverlaySleepTimerButton, L.T("Taymer_Sna.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(OverlayEpgButton, L.T("Pokazat_Skryt_EPG.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(OverlayAlwaysOnTopButton, L.T("Poverkh_Vsekh_Okon_Vkl.ToolTipService.ToolTip"));
        ToolTipService.SetToolTip(ExitFullScreenButton, L.T("Vyyti_Iz_Polnoekrannogo_Rezhima.ToolTipService.ToolTip"));

        // EPG panel / program actions
        EpgHeaderText.Text = L.T("Programma_Peredach.Text");
        EpgHeaderHintText.Text = L.T("Klik_Po_Peredache_Smotret_S_Nachala.Text");
        EmptyChannelEpgText.Text = L.T("Programma_Nedostupna.Text");
        EmptyEpgTitle.Text = L.T("EPG_Dannye_Nedostupny.Text");
        EmptyEpgHint.Text = L.T("Vyberite_Istochnik_EPG_Ili_Obnovite_Dannye.Text");
        EmptyEpgRefreshButton.Content = L.T("Obnovit_EPG.Content");
    }
}
