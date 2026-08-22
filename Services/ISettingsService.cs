using System.Threading.Tasks;
using IptvPlayer.Models;

namespace IptvPlayer.Services;

public interface ISettingsService
{
    Task<AppSettings> LoadAsync();
    Task SaveAsync(AppSettings settings);
}
