using System;
using System.Collections.Generic;
using System.Linq;
using IptvPlayer.ViewModels;

namespace IptvPlayer.Services;


public static class ChannelOverrideMatcher
{


    // Apply move/remove overrides to channels
    public static List<ChannelViewModel> Apply(
        IEnumerable<ChannelViewModel> channels,
        IReadOnlyList<PlaylistDatabaseService.ChannelOverride> overrides)
    {
        if (overrides.Count == 0)
        {
            return channels.ToList();
        }

        var deleted = new HashSet<string>(StringComparer.Ordinal);
        var movedByTvgId = new Dictionary<string, string>(StringComparer.Ordinal);
        var movedByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var movedByUrl = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var o in overrides)
        {
            if (o.IsDeleted)
            {
                if (!string.IsNullOrEmpty(o.StreamUrl))
                {
                    deleted.Add(o.StreamUrl);
                }
                continue;
            }

            if (string.IsNullOrEmpty(o.NewGroup))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(o.StreamUrl))
            {
                movedByUrl[o.StreamUrl] = o.NewGroup;
            }
            if (!string.IsNullOrEmpty(o.TvgId))
            {
                movedByTvgId[o.TvgId] = o.NewGroup;
            }
            var normalized = EpgNameNormalizer.Normalize(o.ChannelName);
            if (!string.IsNullOrEmpty(normalized))
            {
                movedByName[normalized] = o.NewGroup;
            }
        }

        var result = new List<ChannelViewModel>();
        foreach (var channel in channels)
        {
            var url = channel.StreamUrl;
            if (!string.IsNullOrEmpty(url) && deleted.Contains(url))
            {
                continue;
            }

            string? newGroup = null;
            if (string.IsNullOrEmpty(url) || !movedByUrl.TryGetValue(url, out newGroup))
            {
                newGroup = null;
                if (string.IsNullOrEmpty(channel.TvgId) || !movedByTvgId.TryGetValue(channel.TvgId, out newGroup))
                {
                    newGroup = null;
                    movedByName.TryGetValue(EpgNameNormalizer.Normalize(channel.Name), out newGroup);
                }
            }

            if (newGroup != null)
            {
                channel.Group = newGroup;
            }

            result.Add(channel);
        }

        return result;
    }
}
