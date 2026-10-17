extern alias agent;
using Watch = agent::RemoteHub.Agent.LocalPointerWatch;
using Native = agent::RemoteHub.Agent.InputNative;

namespace RemoteHub.Tests;

/// <summary>
/// The banner's click-through for the admin: which mouse events count as the user's own and make it clickable again.
/// </summary>
public class BannerPointerTests
{
    private const uint Move = 0x0200, LeftDown = 0x0201, Injected = 0x01, LowerIlInjected = 0x02;
    private static readonly (int X, int Y) Admin = (900, 20);

    [Fact]
    public void The_devices_own_mouse_counts()
    {
        Assert.True(Watch.FromUsersMouse(Move, 901, 20, 0, IntPtr.Zero, Admin));
        Assert.True(Watch.FromUsersMouse(LeftDown, 900, 20, 0, IntPtr.Zero, Admin), "a click where the admin's pointer is");
    }

    [Fact]
    public void The_agents_own_input_does_not_count()
    {
        Assert.False(Watch.FromUsersMouse(Move, 500, 500, Injected, Native.Tag, Admin));
    }

    [Fact]
    public void A_cursor_placed_by_another_program_does_not_count()
    {
        // A full-screen RDP client puts the cursor where the remote pointer is after each of the admin's moves.
        Assert.False(Watch.FromUsersMouse(Move, 640, 12, Injected, IntPtr.Zero, Admin));
        Assert.False(Watch.FromUsersMouse(LeftDown, 640, 12, LowerIlInjected, IntPtr.Zero, Admin));
    }

    [Fact]
    public void A_move_to_where_the_admins_pointer_already_is_does_not_count()
    {
        Assert.False(Watch.FromUsersMouse(Move, Admin.X, Admin.Y, 0, IntPtr.Zero, Admin));
    }
}
