using XivMcp.Maps;
using Xunit;

namespace XivMcp.Tests;

public class WorldDataCenterTests
{
    // Since the November 2026 patch the World sheet's "DataCenter" column holds a coarser group (Chaos for every European
    // world, Aether for every North American one); the logical data center sits in the column after it.
    [Theory]
    [InlineData(6u, 7u, 7u)] // Odin, Lich, Phoenix: Light
    [InlineData(6u, 6u, 6u)] // Cerberus, Louisoix: Chaos
    [InlineData(4u, 5u, 5u)] // Ultros: Primal
    [InlineData(4u, 4u, 4u)] // Gilgamesh: Aether
    public void TakesTheLogicalDataCenter(uint groupColumn, uint nextColumn, uint expected) =>
        Assert.Equal(expected, WorldDataCenter.Pick(groupColumn, nextColumn, isDataCenter: id => id is >= 1 and <= 12));

    [Fact]
    public void FallsBackWhenTheNextColumnIsNoDataCenter()
    {
        Assert.Equal(6u, WorldDataCenter.Pick(6, 0, isDataCenter: _ => true));
        Assert.Equal(6u, WorldDataCenter.Pick(6, 99, isDataCenter: id => id <= 12));
    }
}
