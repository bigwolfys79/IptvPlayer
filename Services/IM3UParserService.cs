using System.Collections.Generic;
using System.Threading.Tasks;
using IptvPlayer.ViewModels;

namespace IptvPlayer.Services
{
    public interface IM3UParserService
    {
        Task<List<ChannelViewModel>> ParseFromUrlAsync(string playlistUrl);
        Task<List<ChannelViewModel>> ParseFromFileAsync(string filePath);
        List<ChannelViewModel> ParseContent(string content);
    }
}
