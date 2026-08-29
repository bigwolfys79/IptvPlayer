using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IptvPlayer.ViewModels;

namespace IptvPlayer.Services
{
    public class ChannelRepository : IChannelRepository
    {
        private readonly List<ChannelViewModel> _channels = new();

        // Никаких демо-каналов по умолчанию — список наполняется только
        // из добавленных пользователем плейлистов/каналов.

        public Task<List<ChannelViewModel>> GetAllChannelsAsync()
        {
            return Task.FromResult(new List<ChannelViewModel>(_channels));
        }

        public Task<ChannelViewModel?> GetChannelByIdAsync(int id)
        {
            return Task.FromResult(_channels.FirstOrDefault(c => c.Id == id));
        }

        public Task AddChannelAsync(ChannelViewModel channel)
        {
            _channels.Add(channel);
            return Task.CompletedTask;
        }

        public Task Clear()
        {
            _channels.Clear();
            return Task.CompletedTask;
        }
    }
}