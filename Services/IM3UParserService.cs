using System.Collections.Generic;
using System.Threading.Tasks;
using IptvPlayer.ViewModels;

namespace IptvPlayer.Services
{
    public interface IM3UParserService
    {
        Task<List<ChannelViewModel>> ParseFromUrlAsync(string playlistUrl, CancellationToken ct = default, bool deriveGenreFromGroup = false);
        Task<List<ChannelViewModel>> ParseFromFileAsync(string filePath, bool deriveGenreFromGroup = false);
        List<ChannelViewModel> ParseContent(string content, bool deriveGenreFromGroup = false);
    }
}
