using System;
using System.Collections.Generic;
using IptvPlayer.ViewModels;

namespace IptvPlayer.Services;


public sealed class ChannelHistory
{
    private readonly List<ChannelViewModel> _entries = new();

    public const int MaxEntries = 20;

    public IReadOnlyList<ChannelViewModel> Entries => _entries;

    public bool CanGoBack => _entries.Count > 0;


    public void Record(ChannelViewModel channel)
    {
        if (channel == null)
        {
            return;
        }

        if (_entries.Count > 0 &&
            ReferenceEquals(_entries[^1], channel))
        {
            return;
        }

        _entries.Add(channel);
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(0);
        }
    }


    public ChannelViewModel? Pop()
    {
        if (_entries.Count == 0)
        {
            return null;
        }

        var last = _entries[^1];
        _entries.RemoveAt(_entries.Count - 1);
        return last;
    }


    public void Clear() => _entries.Clear();
}
