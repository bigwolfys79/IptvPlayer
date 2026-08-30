using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.ViewModels;

/// <summary>
/// Favorites, reminders, scheduled recordings, live recording, archive pause, back-to-live.
/// </summary>
public partial class MainPageViewModel
{
    [RelayCommand]
    private void ToggleFavorite(ChannelViewModel channel)
    {
        channel.IsFavorite = !channel.IsFavorite;

        if (channel.IsFavorite)
        {
            if (!AppSettings.FavoriteChannels.Contains(channel.Name, StringComparer.OrdinalIgnoreCase))
            {
                AppSettings.FavoriteChannels.Add(channel.Name);
            }
        }
        else
        {
            AppSettings.FavoriteChannels.RemoveAll(
                n => string.Equals(n, channel.Name, StringComparison.OrdinalIgnoreCase));
        }

        SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
        FilterChannels();
    }

    [RelayCommand]
    private void ToggleReminder(EPGEntry entry)
    {
        var channel = SelectedChannel;
        if (channel == null || entry.StartTime <= DateTime.Now)
        {
            return;
        }

        var existing = AppSettings.ProgramReminders.FirstOrDefault(
            r => r.ChannelId == channel.Id && r.StartTime == entry.StartTime);

        if (existing != null)
        {
            AppSettings.ProgramReminders.Remove(existing);
            entry.HasReminder = false;
        }
        else
        {
            AppSettings.ProgramReminders.Add(new ProgramReminder
            {
                ChannelId = channel.Id,
                ChannelName = channel.Name,
                ProgramName = entry.ProgramName,
                StartTime = entry.StartTime
            });
            entry.HasReminder = true;
        }

        SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyReminderFlags()
    {
        var now = DateTime.Now;

        var activeReminders = AppSettings.ProgramReminders
            .Where(r => !r.Notified && r.StartTime > now)
            .Select(r => (r.ChannelId, r.StartTime))
            .ToHashSet();

        var activeRecords = AppSettings.ScheduledRecordings
            .Where(r => r.StartTime > now)
            .Select(r => (ChannelName: r.ChannelName, r.StartTime))
            .ToHashSet();

        foreach (var channel in Channels)
        {
            foreach (var entry in channel.EPGEntries)
            {
                entry.HasReminder = activeReminders.Contains((channel.Id, entry.StartTime));
                entry.HasScheduleRecord = activeRecords.Contains((channel.Name, entry.StartTime));
            }
        }
    }

    public async Task CheckRemindersAsync()
    {
        try
        {
            var now = DateTime.Now;
            var window = TimeSpan.FromMinutes(Math.Max(1, AppSettings.ReminderMinutes));
            var changed = false;

            for (var i = AppSettings.ProgramReminders.Count - 1; i >= 0; i--)
            {
                var reminder = AppSettings.ProgramReminders[i];

                if (reminder.StartTime <= now - window)
                {
                    AppSettings.ProgramReminders.RemoveAt(i);
                    changed = true;
                    continue;
                }

                if (!reminder.Notified && reminder.StartTime - now <= window)
                {
                    ReminderToastRequested?.Invoke(this, reminder);
                    reminder.Notified = true;
                    changed = true;
                }
            }

            if (changed)
            {
                await SaveSettingsAsync();
                ApplyReminderFlags();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckRemindersAsync: не удалось проверить напоминания.");
        }
    }

    [RelayCommand]
    private void ToggleScheduleRecord(EPGEntry entry)
    {
        var channel = SelectedChannel;
        if (channel == null || entry.StartTime <= DateTime.Now)
        {
            return;
        }

        var existing = AppSettings.ScheduledRecordings.FirstOrDefault(
            r => r.ChannelName == channel.Name && r.StartTime == entry.StartTime);

        if (existing != null)
        {
            AppSettings.ScheduledRecordings.Remove(existing);
            entry.HasScheduleRecord = false;
        }
        else
        {
            AppSettings.ScheduledRecordings.Add(new ScheduledRecording
            {
                ChannelName = channel.Name,
                ProgramName = entry.ProgramName,
                StartTime = entry.StartTime,
                DurationSec = Math.Max(60, (entry.EndTime - entry.StartTime).TotalSeconds)
            });
            entry.HasScheduleRecord = true;
        }

        SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
    }

    public void CheckScheduledRecordings()
    {
        if (AppSettings.ScheduledRecordings.Count == 0)
        {
            return;
        }

        var now = DateTime.Now;
        var changed = false;

        for (var i = AppSettings.ScheduledRecordings.Count - 1; i >= 0; i--)
        {
            var rec = AppSettings.ScheduledRecordings[i];
            var end = rec.StartTime.AddSeconds(rec.DurationSec);

            if (now >= end)
            {
                AppSettings.ScheduledRecordings.RemoveAt(i);
                changed = true;
                continue;
            }

            if (now < rec.StartTime)
            {
                continue;
            }

            if (Recording.IsRecordingChannel(rec.ChannelName))
            {
                AppSettings.ScheduledRecordings.RemoveAt(i);
                changed = true;
                continue;
            }

            var channel = Channels.FirstOrDefault(
                c => string.Equals(c.Name, rec.ChannelName, StringComparison.OrdinalIgnoreCase));
            if (channel == null || string.IsNullOrWhiteSpace(channel.StreamUrl))
            {
                AppSettings.ScheduledRecordings.RemoveAt(i);
                changed = true;
                continue;
            }

            var remaining = (int)Math.Max(60, (end - now).TotalSeconds);
            var started = Recording.Start(
                channel.StreamUrl,
                $"{rec.ChannelName} - {rec.ProgramName}",
                rec.ChannelName,
                remaining,
                AppSettings.RecordingsFolder);

            if (started != null)
            {
                AppSettings.ScheduledRecordings.RemoveAt(i);
                changed = true;
            }
        }

        if (changed)
        {
            SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
            ApplyReminderFlags();
        }
    }

    [RelayCommand]
    private void RemoveScheduledRecording(ScheduledRecording rec)
    {
        AppSettings.ScheduledRecordings.Remove(rec);
        SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
        ApplyReminderFlags();
    }

    [RelayCommand]
    private void ToggleRecording()
    {
        RecordError = null;

        var channel = SelectedChannel;
        if (channel == null || string.IsNullOrWhiteSpace(channel.StreamUrl))
        {
            return;
        }

        var existing = Recording.Active.FirstOrDefault(r => r.StreamUrl == channel.StreamUrl);
        if (existing != null)
        {
            Recording.Stop(existing.Id);
        }
        else
        {
            var started = Recording.Start(
                channel.StreamUrl, channel.Name, channel.Name,
                durationSec: null, AppSettings.RecordingsFolder);
            if (started == null)
            {
                RecordError = L.T("Ne_Udalos_Nachat_Zapis_Ffmpeg_Nedostupen");
            }
        }

        IsRecording = Recording.IsRecordingStream(channel.StreamUrl);
        RecordingChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleArchivePause()
    {
        Player.ToggleArchivePause(SelectedChannel);
    }

    [RelayCommand]
    private async Task BackToLiveAsync()
    {
        var channel = SelectedChannel;
        if (channel != null)
        {
            await PlayChannelAsync(channel);
        }
    }
}
