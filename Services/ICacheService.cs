using System.Threading.Tasks;

namespace IptvPlayer.Services
{
    public interface ICacheService
    {
        Task<T> GetAsync<T>(string key);
        Task SetAsync<T>(string key, T value);
        Task RemoveAsync(string key);
        void Clear();
    }
}