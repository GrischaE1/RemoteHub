using System.Windows.Input;

namespace RemoteHub.Services;

/// <summary>
/// Keys (always combined with Ctrl+Alt) for switching sessions. While an RDP session has the keyboard, RemoteHub
/// catches them with a low-level keyboard hook (<see cref="Rdp.SwitchKeyHook"/>) before the RDP control sees them: the
/// control's focus-release hotkeys (OnFocusReleased) never fired in the lab (R-RDP-12), and with "Send Windows key
/// combinations" its own keyboard hook sends every Ctrl+Alt combination to the remote session. The focus-release
/// hotkeys stay set as a fallback.
/// Single-key presets switch to the next session; holding Shift as well goes to the previous one.
///
/// Note: on German and many other layouts AltGr = Ctrl+Alt. Only the LEFT Alt key counts as the hotkey;
/// AltGr+key is treated as typing (e.g. AltGr+&lt; = "|") and passed on to the session.
/// </summary>
public static class SwitchHotkeys
{
    public sealed record Preset(string Id, string Label, Key Prev, Key Next, string Hint)
    {
        public int PrevVk => KeyInterop.VirtualKeyFromKey(Prev);
        public int NextVk => KeyInterop.VirtualKeyFromKey(Next);
        /// <summary>One key for both directions: Shift reverses.</summary>
        public bool ShiftReverses => Prev == Next;
    }

    // Key.OemBackslash = VK_OEM_102: the "< > |" key left of Y on German (ISO) keyboards.
    // Key.Oem5 = VK_OEM_5: the "^ °" key left of 1 on German keyboards.
    public static readonly IReadOnlyList<Preset> All =
    [
        new("Less", "Ctrl+Alt+<  (Shift = previous)  – German keyboard, left of Y", Key.OemBackslash, Key.OemBackslash,
            "Ctrl+Alt+< next session  ·  Ctrl+Alt+Shift+< previous"),
        new("Caret", "Ctrl+Alt+^  (Shift = previous)  – German keyboard, left of 1", Key.Oem5, Key.Oem5,
            "Ctrl+Alt+^ next session  ·  Ctrl+Alt+Shift+^ previous"),
        new("F1F2", "Ctrl+Alt+F1 / F2", Key.F1, Key.F2, "Ctrl+Alt+F1 / F2 previous / next session"),
        new("PgUpPgDn", "Ctrl+Alt+PageUp / PageDown", Key.PageUp, Key.PageDown, "Ctrl+Alt+PageUp / PageDown previous / next session"),
        new("LeftRight", "Ctrl+Alt+Left / Right", Key.Left, Key.Right, "Ctrl+Alt+Left / Right previous / next session"),
    ];

    public static Preset Default => All[0];

    public static Preset Get(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Default;

    /// <summary>The preset in use (set at start-up and when changed in the menu).</summary>
    public static Preset Current { get; set; } = Default;

    /// <summary>
    /// Whether a key press is the switch hotkey of <paramref name="preset"/>: -1 previous session, +1 next, 0 not the
    /// hotkey. Ctrl and the LEFT Alt key must be held; right Alt alone is AltGr, which types a character (AltGr+&lt; = "|").
    /// </summary>
    public static int Match(int vk, bool ctrl, bool leftAlt, bool shift, Preset preset)
    {
        if (!ctrl || !leftAlt) return 0;
        if (preset.ShiftReverses) return vk == preset.NextVk ? (shift ? -1 : 1) : 0;
        return vk == preset.PrevVk ? -1 : vk == preset.NextVk ? 1 : 0;
    }
}
