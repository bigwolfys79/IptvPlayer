using System.Collections.Generic;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.ViewModels;

namespace IptvPlayer.Services
{
    public interface IEPGService
    {
        Task<List<ChannelViewModel>> GetChannelsAsync();
        Task<List<EPGEntry>> GetEPGEntriesAsync(int channelId);


        Task<EPGEntry?> GetCurrentProgramAsync(int channelId);

        Task RefreshEPGAsync();


        Task ReloadSourcesAsync();
    }
}
