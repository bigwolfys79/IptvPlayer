using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.ViewModels;

namespace IptvPlayer.Services
{
    public class ChannelRepository : IChannelRepository
    {
        private readonly object _gate = new();
        private readonly List<ChannelViewModel> _channels = new();
        private Dictionary<int, ChannelViewModel> _byId = new();
        private bool _indexDirty;

        public Task<List<ChannelViewModel>> GetAllChannelsAsync()
        {
            lock (_gate)
            {
                return Task.FromResult(_channels.ToList());
            }
        }

        public Task<ChannelViewModel?> GetChannelByIdAsync(int id)
        {
            lock (_gate)
            {
                if (_indexDirty)
                {
                    _byId = _channels.ToDictionary(c => c.Id);
                    _indexDirty = false;
                }
                return Task.FromResult(_byId.GetValueOrDefault(id));
            }
        }

        public Task AddChannelAsync(ChannelViewModel channel)
        {
            lock (_gate)
            {
                _channels.Add(channel);
                _indexDirty = true;
            }
            return Task.CompletedTask;
        }

        public Task Clear()
        {
            lock (_gate)
            {
                _channels.Clear();
                _byId = new Dictionary<int, ChannelViewModel>();
                _indexDirty = false;
            }
            return Task.CompletedTask;
        }
    }
}
