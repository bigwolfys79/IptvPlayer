using IptvPlayer.Services;

namespace IptvPlayer.Tests;

public class VideoPortalFilterTests
{
    private static PortalCatalogItem Item(long id) => new() { Name = $"item-{id}", ItemId = id };

    [Fact]
    public void FilterByCategoryIds_KeepsOnlyItemsFromCategory()
    {
        var items = new List<PortalCatalogItem> { Item(1), Item(2), Item(3) };
        var ids = new HashSet<long> { 1, 3 };

        var result = VideoPortalService.FilterByCategoryIds(items, ids);

        Assert.Equal(new[] { 1L, 3L }, result.Select(i => i.ItemId));
    }

    [Fact]
    public void FilterByCategoryIds_EmptyCategoryIds_ReturnsAllItems()
    {
        var items = new List<PortalCatalogItem> { Item(1), Item(2) };

        var result = VideoPortalService.FilterByCategoryIds(items, new HashSet<long>());

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FilterByCategoryIds_ItemWithoutId_Kept()
    {
        var items = new List<PortalCatalogItem> { new() { Name = "no-id", ItemId = 0 }, Item(5) };
        var ids = new HashSet<long> { 5 };

        var result = VideoPortalService.FilterByCategoryIds(items, ids);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void TryExtractItemId_FromRequestObject_ReturnsFid()
    {
        var id = VideoPortalService.TryExtractItemId("{\"cmd\":\"flick\",\"fid\":144314}");
        Assert.Equal(144314L, id);
    }

    [Fact]
    public void TryExtractItemId_WithoutFid_ReturnsNull()
    {
        Assert.Null(VideoPortalService.TryExtractItemId("{\"cmd\":\"flick\"}"));
    }

    [Fact]
    public void TryExtractItemId_InvalidOrNull_ReturnsNull()
    {
        Assert.Null(VideoPortalService.TryExtractItemId("not json"));
        Assert.Null(VideoPortalService.TryExtractItemId(null));
        Assert.Null(VideoPortalService.TryExtractItemId(""));
    }
}
