using IptvPlayer.Services;

namespace IptvPlayer;

// Runtime localization for x:Uid elements: PrimaryLanguageOverride is
// unavailable in unpackaged builds, so these are applied via L.T
public sealed partial class HubPage
{
    private void ApplyXamlLocalization()
    {
        HubPlaylistsTitle.Text = L.T("Pleylisty.Text");
        HubPlaylistsSubtitle.Text = L.T("Upravlyayte_Pleylistami.Text");
        HubPortalTitle.Text = L.T("Portal.Text");
        HubPortalSubtitle.Text = L.T("Portal_Card_Subtitle.Text");
        HubVideoTitle.Text = L.T("Video_Card.Text");
        HubVideoSubtitle.Text = L.T("Video_Card_Subtitle.Text");
        HubSettingsTitle.Text = L.T("Nastroyki_Card.Text");
        HubLanguageHint.Text = L.T("Yazyk_Interfeys_Vosproizv.Text");
    }
}
