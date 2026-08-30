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

        /// <summary>
        /// Возвращает только текущую передачу для канала (без загрузки всех
        /// EPGEntries). Экономит память при большом числе каналов.
        /// </summary>
        Task<EPGEntry?> GetCurrentProgramAsync(int channelId);

        Task RefreshEPGAsync();

        /// <summary>
        /// Перечитывает EPG с текущими источниками активного плейлиста, не
        /// трогая дисковый кэш источников (в отличие от RefreshEPGAsync) —
        /// для переключения плейлистов: у каждого свой набор XMLTV-фидов.
        /// </summary>
        Task ReloadSourcesAsync();
    }
}