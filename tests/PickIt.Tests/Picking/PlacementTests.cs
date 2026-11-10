using PickIt.Revit.UI.Picking;

namespace PickIt.Tests.Picking;

/// <summary>The ribbon icon's bitmap scale and the menu popup's placement: pure parts of App.cs and PickMenuWindow.</summary>
public class PlacementTests
{
    [Theory]
    [InlineData(96, 1)]   // 100 %
    [InlineData(120, 2)]  // 125 %
    [InlineData(144, 2)]  // 150 %
    [InlineData(168, 2)]  // 175 %
    [InlineData(192, 2)]  // 200 %
    [InlineData(216, 3)]  // 225 %
    [InlineData(240, 3)]  // 250 %
    [InlineData(288, 3)]  // 300 %
    [InlineData(72, 1)]   // below 100 %: never less than one
    [InlineData(97, 2)]
    [InlineData(193, 3)]
    public void IconScaleFor_IsAWholeNumber_OneTwoOrThree(int dpi, int expected)
    {
        Assert.Equal(expected, PickerGeometry.IconScaleFor(dpi));
    }

    // The work area of a monitor left of and above the primary: -1920, -200 … 0, 840.
    private static readonly PixelRect Work = new(-1920, -200, 1920, 1040);

    private const int Width = 248;
    private const int Height = 300;
    private const int Margin = 10; // the popup's transparent shadow margin, in window pixels

    [Fact]
    public void MoveInside_LeavesAWindowThatFits_WhereItIs()
    {
        Assert.Equal((-1000, 100), PickerGeometry.MoveInside(Work, -1000, 100, Width, Height, Margin));
        // The shadow margin may hang over the edge: the card is what stays inside.
        Assert.Equal((-1930, -210), PickerGeometry.MoveInside(Work, -1930, -210, Width, Height, Margin));
        Assert.Equal((-238, 550), PickerGeometry.MoveInside(Work, -238, 550, Width, Height, Margin));
    }

    [Fact]
    public void MoveInside_PullsAWindowBack_OnANegativeOriginWorkArea()
    {
        Assert.Equal((-1930, 100), PickerGeometry.MoveInside(Work, -3000, 100, Width, Height, Margin));  // off the left
        Assert.Equal((-1000, -210), PickerGeometry.MoveInside(Work, -1000, -900, Width, Height, Margin)); // off the top
        Assert.Equal((-238, 100), PickerGeometry.MoveInside(Work, 500, 100, Width, Height, Margin));      // off the right
        Assert.Equal((-1000, 550), PickerGeometry.MoveInside(Work, -1000, 2000, Width, Height, Margin));  // off the bottom
        Assert.Equal((-1930, 550), PickerGeometry.MoveInside(Work, -5000, 5000, Width, Height, Margin));  // a corner
    }

    [Fact]
    public void MoveInside_AWindowLargerThanTheWorkArea_KeepsItsTopLeftCornerInView()
    {
        Assert.Equal((-1930, -210), PickerGeometry.MoveInside(Work, -500, 100, 3000, 2000, Margin));
        Assert.Equal((-1930, -210), PickerGeometry.MoveInside(Work, -4000, -4000, 3000, 2000, Margin));
        // Only too wide: the height still fits and is left alone.
        Assert.Equal((-1930, 100), PickerGeometry.MoveInside(Work, -500, 100, 3000, Height, Margin));
    }

    [Fact]
    public void MoveInside_WithoutAWorkArea_ChangesNothing()
    {
        Assert.Equal((-7000, 9000), PickerGeometry.MoveInside(default, -7000, 9000, Width, Height, Margin));
    }
}
