using System.Linq;
using IptvPlayer.Models;

namespace IptvPlayer.Services.OnlineCinema;


// Keeps the source list in sync with the cinema master switch: enabled —
// a ready-to-open "online cinema" source always exists; disabled — it is
// removed. The cinema site is hardcoded (KinogoSite), so no user input
// beyond the toggle is needed
public static class OnlineCinemaPlaylistSync
{
    // True when the playlist list changed and settings need saving
    public static bool Sync(AppSettings settings)
    {
        var cinemaPlaylists = settings.Playlists.Where(p => p.IsOnlineCinema).ToList();

        if (settings.OnlineCinemaEnabled)
        {
            if (cinemaPlaylists.Count > 0)
            {
                return false;
            }

            var playlist = new PlaylistSource
            {
                Id = settings.Playlists.Count == 0
                    ? 1
                    : settings.Playlists.Max(p => p.Id) + 1,
                Name = L.T("OnlineCinema_Tip_Lbl"),
                Url = KinogoSite.BaseUrl,
                Type = "online-cinema"
            };

            if (settings.Playlists.Count == 0)
            {
                settings.ActivePlaylistId = playlist.Id;
            }
            settings.Playlists.Add(playlist);
            return true;
        }

        if (cinemaPlaylists.Count == 0)
        {
            return false;
        }

        settings.Playlists.RemoveAll(p => p.IsOnlineCinema);
        if (cinemaPlaylists.Any(p => p.Id == settings.ActivePlaylistId))
        {
            settings.ActivePlaylistId = settings.Playlists.FirstOrDefault()?.Id ?? 0;
        }
        return true;
    }
}
