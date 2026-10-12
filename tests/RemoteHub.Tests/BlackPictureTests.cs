extern alias agent;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>A picture that stays black makes the helper wake the display.</summary>
public class BlackPictureTests
{
    private static A.FrameBuffer Frame(byte value)
    {
        var f = new A.FrameBuffer();
        f.Ensure(320, 200);
        Array.Fill(f.Pixels, value);
        return f;
    }

    [Fact]
    public void A_dark_picture_counts_as_black() => Assert.True(A.DesktopHelperSession.IsBlack(Frame(8)));

    [Fact]
    public void A_picture_with_something_on_it_is_not_black()
    {
        var f = Frame(0);
        var i = (156 * 320 + 170) * 4; // one sample point in the middle
        f.Pixels[i + 2] = 200;
        Assert.False(A.DesktopHelperSession.IsBlack(f));
    }

    [Fact]
    public void An_empty_picture_counts_as_black() => Assert.True(A.DesktopHelperSession.IsBlack(new A.FrameBuffer()));
}
