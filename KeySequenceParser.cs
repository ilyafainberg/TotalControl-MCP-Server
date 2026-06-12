// -----------------------------------------------------------------------------
//  TotalControl — Key sequence parser
//
//  Licensed under the MIT License. See LICENSE in the project root for details.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
// -----------------------------------------------------------------------------

using System.Text;

namespace TotalControl;

/// <summary>
/// Parses agent-supplied key sequence strings into a stream of Win32 INPUT events.
///
/// Syntax:
///   • Plain text  → typed via KEYEVENTF_UNICODE (works in any focused control,
///                   respects current IME, no shift-state guessing).
///   • {Key}       → named virtual key. Case-insensitive.
///                   Supported: Enter, Tab, Esc/Escape, Space, Backspace, Delete,
///                              Up, Down, Left, Right, Home, End, PageUp, PageDown,
///                              Insert, CapsLock, NumLock, Win, Apps,
///                              F1..F24, VolumeUp/Down/Mute, Play, NextTrack, PrevTrack
///   • {Mod+Mod+Key}     → chord. Modifiers: Ctrl, Alt, Shift, Win.
///                          Example: {Ctrl+S}, {Ctrl+Shift+T}, {Win+R}, {Alt+F4}
///   • {Key N}     → repeat N times. Example: {Tab 5}, {Backspace 10}
///   • {{ and }}   → literal '{' and '}'.
///   • +text       → no special meaning here (unlike SendKeys). Use {Shift+a} for chorded shift.
/// </summary>
internal static class KeySequenceParser
{
    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = Win32.VK.RETURN,    ["return"] = Win32.VK.RETURN,
        ["tab"] = Win32.VK.TAB,         ["esc"] = Win32.VK.ESCAPE,    ["escape"] = Win32.VK.ESCAPE,
        ["space"] = Win32.VK.SPACE,     ["backspace"] = Win32.VK.BACK, ["bksp"] = Win32.VK.BACK,
        ["delete"] = Win32.VK.DELETE,   ["del"] = Win32.VK.DELETE,    ["insert"] = Win32.VK.INSERT, ["ins"] = Win32.VK.INSERT,
        ["up"] = Win32.VK.UP,           ["down"] = Win32.VK.DOWN,
        ["left"] = Win32.VK.LEFT,       ["right"] = Win32.VK.RIGHT,
        ["home"] = Win32.VK.HOME,       ["end"] = Win32.VK.END,
        ["pageup"] = Win32.VK.PRIOR,    ["pgup"] = Win32.VK.PRIOR,
        ["pagedown"] = Win32.VK.NEXT,   ["pgdn"] = Win32.VK.NEXT,
        ["capslock"] = Win32.VK.CAPITAL,["numlock"] = Win32.VK.NUMLOCK, ["scrolllock"] = Win32.VK.SCROLL,
        ["printscreen"] = Win32.VK.SNAPSHOT, ["prtsc"] = Win32.VK.SNAPSHOT, ["pause"] = Win32.VK.PAUSE,
        ["win"] = Win32.VK.LWIN,        ["lwin"] = Win32.VK.LWIN, ["rwin"] = Win32.VK.RWIN, ["apps"] = Win32.VK.APPS,
        ["volumeup"] = Win32.VK.VOLUME_UP, ["volumedown"] = Win32.VK.VOLUME_DOWN, ["volumemute"] = Win32.VK.VOLUME_MUTE,
        ["play"] = Win32.VK.MEDIA_PLAY_PAUSE, ["nexttrack"] = Win32.VK.MEDIA_NEXT_TRACK, ["prevtrack"] = Win32.VK.MEDIA_PREV_TRACK,
    };

    // Extended-key VKs (require KEYEVENTF_EXTENDEDKEY for correct behavior)
    private static readonly HashSet<ushort> ExtendedKeys = new()
    {
        Win32.VK.INSERT, Win32.VK.DELETE, Win32.VK.HOME, Win32.VK.END,
        Win32.VK.PRIOR, Win32.VK.NEXT, Win32.VK.UP, Win32.VK.DOWN, Win32.VK.LEFT, Win32.VK.RIGHT,
        Win32.VK.LWIN, Win32.VK.RWIN, Win32.VK.APPS, Win32.VK.SNAPSHOT,
    };

    /// <summary>
    /// Convert an agent-supplied key-sequence string into a flat list of Win32
    /// INPUT events ready for <see cref="Win32.SendInput"/>.
    ///
    /// Algorithm: single-pass scan. Plain characters become Unicode key events,
    /// '{' opens a token, '}' closes it. '{{' and '}}' escape literal braces.
    /// </summary>
    public static List<Win32.INPUT> Parse(string text)
    {
        var inputs = new List<Win32.INPUT>(text.Length * 2);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '{')
            {
                // '{{' is a literal '{'.
                if (i + 1 < text.Length && text[i + 1] == '{') { AppendChar(inputs, '{'); i += 2; continue; }
                int end = text.IndexOf('}', i + 1);
                if (end < 0) throw new FormatException($"Unmatched '{{' at position {i}.");
                string token = text.Substring(i + 1, end - i - 1).Trim();
                ParseToken(token, inputs);
                i = end + 1;
            }
            else if (c == '}')
            {
                // '}}' is a literal '}'. Bare '}' is an error so we catch unbalanced input early.
                if (i + 1 < text.Length && text[i + 1] == '}') { AppendChar(inputs, '}'); i += 2; continue; }
                throw new FormatException($"Unescaped '}}' at position {i}. Use '}}}}' for a literal.");
            }
            else
            {
                AppendChar(inputs, c);
                i++;
            }
        }
        return inputs;
    }

    /// <summary>
    /// Parse the body of a single {…} token into key-down / key-up events.
    /// Handles modifier chords ({Ctrl+S}), repeat counts ({Tab 5}), named keys,
    /// function keys (F1..F24), and bare characters under modifiers.
    /// </summary>
    private static void ParseToken(string token, List<Win32.INPUT> inputs)
    {
        if (token.Length == 0) throw new FormatException("Empty {} token.");

        // Optional trailing repeat count, e.g. "{Tab 5}" → press Tab five times.
        int repeat = 1;
        var spaceIdx = token.LastIndexOf(' ');
        if (spaceIdx > 0 && int.TryParse(token.AsSpan(spaceIdx + 1), out var n) && n > 0 && n <= 1000)
        {
            repeat = n;
            token = token.Substring(0, spaceIdx).TrimEnd();
        }

        // Split on '+' so the last segment is the main key and any earlier
        // segments are modifiers (Ctrl/Alt/Shift/Win, in any order).
        var parts = token.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = new List<ushort>();
        ushort? mainVk = null;
        string? mainText = null;

        for (int p = 0; p < parts.Length; p++)
        {
            var part = parts[p];
            bool isLast = p == parts.Length - 1;
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers.Add(Win32.VK.CONTROL); continue;
                case "alt":  case "menu":    modifiers.Add(Win32.VK.MENU);    continue;
                case "shift":                modifiers.Add(Win32.VK.SHIFT);   continue;
                case "win":  case "lwin":    modifiers.Add(Win32.VK.LWIN);    continue;
                case "rwin":                 modifiers.Add(Win32.VK.RWIN);    continue;
            }

            // A modifier name in a non-final position is fine (handled above);
            // any other non-final segment is malformed.
            if (!isLast)
                throw new FormatException($"Unexpected modifier '{part}' in token '{token}'.");

            // Resolution order for the main key: named → function key → single char → multi-char-under-modifier.
            if (NamedKeys.TryGetValue(part, out var vk))         { mainVk = vk; }
            else if (TryParseFunctionKey(part, out vk))           { mainVk = vk; }
            else if (part.Length == 1)                            { mainText = part; }
            else if (part.Length > 1 && modifiers.Count > 0)      { mainText = part; }
            else throw new FormatException($"Unknown key '{part}' in token '{token}'.");
        }

        if (mainVk is null && mainText is null)
            throw new FormatException($"Token '{token}' has no main key.");

        // Emit `repeat` chord iterations. Modifiers are released in reverse
        // order so that nested combinations behave symmetrically.
        for (int r = 0; r < repeat; r++)
        {
            foreach (var mod in modifiers) AppendVk(inputs, mod, down: true);

            if (mainVk is ushort vkVal)
            {
                AppendVk(inputs, vkVal, down: true);
                AppendVk(inputs, vkVal, down: false);
            }
            else
            {
                // Type the literal text under whatever modifiers are held.
                foreach (var ch in mainText!) AppendChar(inputs, ch);
            }

            for (int m = modifiers.Count - 1; m >= 0; m--) AppendVk(inputs, modifiers[m], down: false);
        }
    }

    /// <summary>Maps "F1".."F24" to the corresponding virtual-key code.</summary>
    private static bool TryParseFunctionKey(string s, out ushort vk)
    {
        vk = 0;
        if (s.Length >= 2 && (s[0] == 'F' || s[0] == 'f') && int.TryParse(s.AsSpan(1), out var fn) && fn >= 1 && fn <= 24)
        {
            vk = (ushort)(Win32.VK.F1 + (fn - 1));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Emit a key-down + key-up pair for a single Unicode code unit. Using
    /// KEYEVENTF_UNICODE avoids any per-layout / per-IME shift-state guessing:
    /// the OS injects the exact character into the focused control.
    /// </summary>
    private static void AppendChar(List<Win32.INPUT> inputs, char ch)
    {
        inputs.Add(new Win32.INPUT { type = Win32.INPUT_KEYBOARD, U = new Win32.INPUTUNION { ki = new Win32.KEYBDINPUT { wScan = ch, dwFlags = (uint)Win32.KeyEventF.Unicode } } });
        inputs.Add(new Win32.INPUT { type = Win32.INPUT_KEYBOARD, U = new Win32.INPUTUNION { ki = new Win32.KEYBDINPUT { wScan = ch, dwFlags = (uint)(Win32.KeyEventF.Unicode | Win32.KeyEventF.KeyUp) } } });
    }

    /// <summary>
    /// Emit a single virtual-key event (down or up). Sets the EXTENDEDKEY flag
    /// for keys (arrows, Ins/Del/Home/End, Win, etc.) that require it so apps
    /// receiving WM_KEYDOWN can distinguish them from their NumPad twins.
    /// </summary>
    private static void AppendVk(List<Win32.INPUT> inputs, ushort vk, bool down)
    {
        uint flags = down ? 0u : (uint)Win32.KeyEventF.KeyUp;
        if (ExtendedKeys.Contains(vk)) flags |= (uint)Win32.KeyEventF.ExtendedKey;
        inputs.Add(new Win32.INPUT { type = Win32.INPUT_KEYBOARD, U = new Win32.INPUTUNION { ki = new Win32.KEYBDINPUT { wVk = vk, dwFlags = flags } } });
    }
}
