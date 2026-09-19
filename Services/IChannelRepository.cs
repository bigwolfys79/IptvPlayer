using System.Collections.Generic;
using System.Threading.Tasks;
using IptvPlayer.ViewModels;

namespace IptvPlayer.Services
{
    public interface IChannelRepository
    {
        Task<List<ChannelViewModel>> GetAllChannelsAsync();
        Task<ChannelViewModel?> GetChannelByIdAsync(int id);
        Task AddChannelAsync(ChannelViewModel channel);
        Task AddChannelsAsync(IEnumerable<ChannelViewModel> channels);


        Task Clear();
    }
}
