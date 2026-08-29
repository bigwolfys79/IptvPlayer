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

        /// <summary>
        /// Полностью очищает репозиторий — при переключении активного плейлиста:
        /// каналы предыдущего плейлиста больше не должны быть видны EPGService.
        /// </summary>
        Task Clear();
    }
}