using IptvPlayer.Services;
using IptvPlayer.ViewModels;

namespace IptvPlayer.Tests;

public class ChannelHistoryTests
{
    private static ChannelViewModel Channel(int id, string name) => new() { Id = id, Name = name };

    [Fact]
    public void Record_ThenPop_ReturnsPreviousChannel()
    {
        var history = new ChannelHistory();
        var a = Channel(1, "Первый");
        var b = Channel(2, "НТВ");

        history.Record(a);
        history.Record(b);

        Assert.True(history.CanGoBack);
        Assert.Same(b, history.Pop());
        Assert.Same(a, history.Pop());
        Assert.Null(history.Pop());
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void Record_SameChannelTwiceInRow_DoesNotDuplicate()
    {
        var history = new ChannelHistory();
        var a = Channel(1, "Первый");

        history.Record(a);
        history.Record(a);

        Assert.Same(a, history.Pop());
        Assert.Null(history.Pop());
    }

    [Fact]
    public void Record_SameChannelAfterAnother_IsRecordedAgain()
    {
        // А → Б → А: «назад» должен вернуть Б, затем А.
        var history = new ChannelHistory();
        var a = Channel(1, "Первый");
        var b = Channel(2, "НТВ");

        history.Record(a);
        history.Record(b);
        history.Record(a);

        Assert.Same(a, history.Pop());
        Assert.Same(b, history.Pop());
        Assert.Same(a, history.Pop());
    }

    [Fact]
    public void Record_Null_IsIgnored()
    {
        var history = new ChannelHistory();
        history.Record(null!);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void Record_CapsAtMaxEntries()
    {
        var history = new ChannelHistory();
        var first = Channel(1, "Канал 1");
        history.Record(first);
        for (var i = 2; i <= ChannelHistory.MaxEntries + 10; i++)
        {
            history.Record(Channel(i, $"Канал {i}"));
        }

        Assert.Equal(ChannelHistory.MaxEntries, history.Entries.Count);
        // Самый старый вытеснен — до первого канала «назад» не дойти.
        while (history.CanGoBack)
        {
            Assert.NotSame(first, history.Pop());
        }
    }

    [Fact]
    public void Clear_EmptiesHistory()
    {
        var history = new ChannelHistory();
        history.Record(Channel(1, "Первый"));
        history.Clear();
        Assert.False(history.CanGoBack);
    }
}
