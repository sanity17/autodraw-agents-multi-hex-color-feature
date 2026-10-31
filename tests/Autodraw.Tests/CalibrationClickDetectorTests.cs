using Autodraw;
using Xunit;

namespace Autodraw.Tests;

public class CalibrationClickDetectorTests
{
    [Fact]
    public void TheClickThatOpensCalibrationIsNotCaptured()
    {
        var detector = new ClickEdgeDetector(leftDown: true, rightDown: false);

        Assert.Equal(ClickEdge.None, detector.Update(leftDown: true, rightDown: false));
        Assert.Equal(ClickEdge.None, detector.Update(leftDown: false, rightDown: false));
    }

    [Fact]
    public void ReleaseThenPressEmitsExactlyOneLeftClick()
    {
        var detector = new ClickEdgeDetector();

        Assert.Equal(ClickEdge.None, detector.Update(leftDown: false, rightDown: false));
        Assert.Equal(ClickEdge.Left, detector.Update(leftDown: true, rightDown: false));
        // Still held: no repeat.
        Assert.Equal(ClickEdge.None, detector.Update(leftDown: true, rightDown: false));
        Assert.Equal(ClickEdge.None, detector.Update(leftDown: false, rightDown: false));
        Assert.Equal(ClickEdge.Left, detector.Update(leftDown: true, rightDown: false));
    }

    [Fact]
    public void RightClickIsReportedIndependently()
    {
        var detector = new ClickEdgeDetector();

        Assert.Equal(ClickEdge.None, detector.Update(leftDown: false, rightDown: false));
        Assert.Equal(ClickEdge.Right, detector.Update(leftDown: false, rightDown: true));
        Assert.Equal(ClickEdge.None, detector.Update(leftDown: false, rightDown: true));
    }

    [Fact]
    public void PaletteSequenceCapturesEverySwatchAndTheFinishingRightClick()
    {
        var detector = new ClickEdgeDetector();
        var clicks = new List<ClickEdge>();

        void Sample(bool left, bool right)
        {
            var click = detector.Update(left, right);
            if (click != ClickEdge.None) clicks.Add(click);
        }

        Sample(false, false);
        Sample(true, false);   // swatch 1
        Sample(false, false);
        Sample(true, false);   // swatch 2
        Sample(false, false);
        Sample(true, false);   // swatch 3
        Sample(false, false);
        Sample(false, true);   // finish

        Assert.Equal(new[] { ClickEdge.Left, ClickEdge.Left, ClickEdge.Left, ClickEdge.Right }, clicks);
    }

    [Fact]
    public void SimultaneousButtonsReportOneClickPerRisingEdge()
    {
        var detector = new ClickEdgeDetector(leftDown: false, rightDown: false);

        // Both go down together: only the left edge is reported for this sample.
        Assert.Equal(ClickEdge.Left, detector.Update(leftDown: true, rightDown: true));
        Assert.Equal(ClickEdge.None, detector.Update(leftDown: true, rightDown: true));
        Assert.Equal(ClickEdge.None, detector.Update(leftDown: false, rightDown: true));
        Assert.Equal(ClickEdge.None, detector.Update(leftDown: false, rightDown: false));
        Assert.Equal(ClickEdge.Right, detector.Update(leftDown: false, rightDown: true));
    }
}
