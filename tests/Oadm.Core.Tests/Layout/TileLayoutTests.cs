using Oadm.Sdk.Layout;

namespace Oadm.Core.Tests.Layout;

/// <summary>The shared tile grid rules of Live View and Dashboards (SDK <see cref="TileLayout"/>).</summary>
public sealed class TileLayoutTests
{
    private static readonly TileLayout Grid = new();

    [Fact]
    public void ADroppedTileTakesTheLargestFreeSquareAtTheDropPoint()
    {
        Assert.Equal(new TileRect(0, 0, 10, 10), Grid.Place([], 3, 3));
        Assert.Equal(new TileRect(10, 0, 10, 10), Grid.Place([new(0, 0, 10, 10)], 15, 4));
        Assert.Equal(new TileRect(0, 10, 10, 10), Grid.Place([new(0, 0, 10, 10), new(10, 0, 10, 10)], 5, 5)); // nearest free
        Assert.Equal(new TileRect(0, 17, 3, 3), Grid.Place([new(0, 0, 20, 17), new(3, 17, 17, 3)], 1, 18)); // a 3 x 3 gap
        Assert.Null(Grid.Place([new(0, 0, 20, 20)], 5, 5));
        Assert.Equal(new TileRect(0, 0, 10, 10), Grid.PlaceFirstFree([]));
    }

    [Fact]
    public void TheRulesOfTheGridAreItsOwn()
    {
        var dashboard = new TileLayout(maxTiles: 50);
        var sixteen = Enumerable.Range(0, 16).Select(i => new TileRect(i % 4 * 2, i / 4 * 2, 2, 2)).ToList();
        Assert.Null(Grid.Place(sixteen, 15, 15)); // 16 at most
        Assert.NotNull(dashboard.Place(sixteen, 15, 15));

        var small = new TileLayout(size: 10, minTileSize: 1, dropSize: 3);
        Assert.Equal(new TileRect(4, 4, 3, 3), small.Place([], 5, 5));
        Assert.True(small.Fits(new TileRect(9, 9, 1, 1)));
        Assert.False(Grid.Fits(new TileRect(19, 19, 1, 1)));
        Assert.False(Grid.Fits(new TileRect(15, 0, 6, 6)));
    }

    [Fact]
    public void MovingAndResizingStaysInsideTheGridAndNeverOverlaps()
    {
        TileRect[] others = [new(10, 0, 10, 10)];
        Assert.Null(Grid.Fit(others, 5, 0, 10, 10)); // onto the other tile
        Assert.Equal(new TileRect(0, 10, 10, 10), Grid.Fit(others, -3, 14, 10, 10)); // clamped inside
        Assert.Equal(new TileRect(0, 0, 2, 2), Grid.Fit(others, 0, 0, 0, 1)); // at least 2 x 2
        Assert.Equal(new TileRect(0, 0, 10, 20), Grid.Fit(others, 0, 0, 10, 40));
    }

    [Fact]
    public void DraggingAnEdgeOrACornerResizesAndTheOppositeEdgesStay()
    {
        var tile = new TileRect(4, 4, 6, 6);
        Assert.Equal(new TileRect(6, 5, 6, 6), Grid.Drag(tile, TileEdges.None, 2, 1)); // inside: move
        Assert.Equal(new TileRect(2, 4, 8, 6), Grid.Drag(tile, TileEdges.Left, -2, 9)); // left edge, the right edge stays
        Assert.Equal(new TileRect(4, 4, 6, 9), Grid.Drag(tile, TileEdges.Bottom, 5, 3));
        Assert.Equal(new TileRect(4, 0, 9, 10), Grid.Drag(tile, TileEdges.Top | TileEdges.Right, 3, -7)); // corner, top stops at the grid
        Assert.Equal(new TileRect(8, 4, 2, 6), Grid.Drag(tile, TileEdges.Left, 9, 0)); // never below 2 cells
        Assert.Equal(new TileRect(4, 4, 16, 6), Grid.Drag(tile, TileEdges.Right, 30, 0)); // stops at the edge of the grid
    }

    [Fact]
    public void ACopyTakesTheFirstFreePlaceOfItsSize()
    {
        TileRect[] tiles = [new(0, 0, 4, 3)];
        Assert.Equal(new TileRect(4, 0, 4, 3), Grid.PlaceSized(tiles, 4, 3));
        Assert.Equal(new TileRect(0, 3, 20, 2), Grid.PlaceSized(tiles, 20, 2)); // the first row is taken in part
        Assert.Null(Grid.PlaceSized([new(0, 0, 20, 19)], 2, 2)); // one row left: no 2 x 2
        Assert.Null(new TileLayout(maxTiles: 1).PlaceSized(tiles, 2, 2));
    }

    [Fact]
    public void TheFirstOverlapIsFound()
    {
        Assert.Equal(-1, TileLayout.FirstOverlap([new(0, 0, 2, 2), new(2, 0, 2, 2)]));
        Assert.Equal(2, TileLayout.FirstOverlap([new(0, 0, 2, 2), new(2, 0, 2, 2), new(1, 1, 2, 2)]));
    }
}
