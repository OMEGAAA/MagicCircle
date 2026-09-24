using System.Runtime.InteropServices;

namespace MagicCircle;

/// <summary>"ctrl+shift+t" のようなキー指定を解釈して SendInput で送出する。</summary>
internal static class KeySender
{
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LWIN = 0x5B;

    private static readonly Dictionary<string, (ushort Vk, bool Extended)> KeyMap = BuildKeyMap();

    /// <summary>仮想キーコード → 代表的なキー名（設定画面のキー取り込みで使う）。</summary>
    private static readonly Dictionary<ushort, string> NameMap = BuildNameMap();

    /// <summary>キー指定が解釈できるかだけを調べる（送出はしない）。</summary>
    public static bool Validate(string? spec, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(spec)) { error = "キーが空です"; return false; }

        var chords = spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (chords.Length == 0) { error = "キーが空です"; return false; }

        foreach (var chord in chords)
            if (!TryBuild(chord, out _, out error)) return false;

        return true;
    }

    /// <summary>仮想キーコードに対応するキー名を返す。未知のキーなら false。</summary>
    public static bool TryGetName(ushort vk, out string name) => NameMap.TryGetValue(vk, out name!);

    /// <summary>
    /// キー指定を送出する。"|" で区切ると複数の組み合わせを順番に送る
    /// （"," や ";" 自体がキー名なので、区切り文字には使わない）。
    /// 解釈できない場合は false を返す。
    /// </summary>
    public static bool Send(string? spec, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(spec)) { error = "キーが空です"; return false; }

        var chords = spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var batches = new List<Native.INPUT[]>();

        foreach (var chord in chords)
        {
            if (!TryBuild(chord, out var inputs, out error)) return false;
            batches.Add(inputs);
        }

        foreach (var inputs in batches)
        {
            uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
            if (sent != inputs.Length)
            {
                error = $"SendInput が失敗しました (Win32 error {Marshal.GetLastWin32Error()})";
                return false;
            }
        }
        return true;
    }

    private static bool TryBuild(string chord, out Native.INPUT[] inputs, out string? error)
    {
        inputs = Array.Empty<Native.INPUT>();
        error = null;

        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) { error = "キーが空です"; return false; }

        var modifiers = new List<ushort>();
        (ushort Vk, bool Extended)? main = null;

        foreach (var raw in parts)
        {
            var token = raw.ToLowerInvariant();
            switch (token)
            {
                case "ctrl" or "control": modifiers.Add(VK_CONTROL); continue;
                case "shift": modifiers.Add(VK_SHIFT); continue;
                case "alt" or "menu": modifiers.Add(VK_MENU); continue;
                case "win" or "lwin" or "meta" or "super": modifiers.Add(VK_LWIN); continue;
            }

            if (!KeyMap.TryGetValue(token, out var key))
            {
                error = $"不明なキー名: {raw}";
                return false;
            }
            if (main is not null)
            {
                error = $"1 つの組み合わせに主キーを 2 つ以上指定できません: {chord}";
                return false;
            }
            main = key;
        }

        if (main is null) { error = $"主キーがありません: {chord}"; return false; }

        var list = new List<Native.INPUT>(modifiers.Count * 2 + 2);
        foreach (var mod in modifiers) list.Add(KeyInput(mod, false, false));
        list.Add(KeyInput(main.Value.Vk, main.Value.Extended, false));
        list.Add(KeyInput(main.Value.Vk, main.Value.Extended, true));
        for (int i = modifiers.Count - 1; i >= 0; i--) list.Add(KeyInput(modifiers[i], false, true));

        inputs = list.ToArray();
        return true;
    }

    private static Native.INPUT KeyInput(ushort vk, bool extended, bool up)
    {
        uint flags = 0;
        if (extended) flags |= Native.KEYEVENTF_EXTENDEDKEY;
        if (up) flags |= Native.KEYEVENTF_KEYUP;
        return new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = (IntPtr)Native.MAGIC_EXTRA_INFO,
                }
            }
        };
    }

    private static Dictionary<string, (ushort, bool)> BuildKeyMap()
    {
        var map = new Dictionary<string, (ushort, bool)>(StringComparer.OrdinalIgnoreCase);

        for (char c = 'a'; c <= 'z'; c++) map[c.ToString()] = ((ushort)(0x41 + (c - 'a')), false);
        for (char c = '0'; c <= '9'; c++) map[c.ToString()] = ((ushort)(0x30 + (c - '0')), false);
        for (int i = 1; i <= 24; i++) map["f" + i] = ((ushort)(0x6F + i), false);
        for (int i = 0; i <= 9; i++) map["numpad" + i] = ((ushort)(0x60 + i), false);

        map["enter"] = map["return"] = (0x0D, false);
        map["esc"] = map["escape"] = (0x1B, false);
        map["tab"] = (0x09, false);
        map["space"] = (0x20, false);
        map["backspace"] = map["bs"] = (0x08, false);
        map["capslock"] = (0x14, false);
        map["printscreen"] = map["prtsc"] = (0x2C, false);
        map["apps"] = map["menukey"] = (0x5D, true);

        map["insert"] = map["ins"] = (0x2D, true);
        map["delete"] = map["del"] = (0x2E, true);
        map["home"] = (0x24, true);
        map["end"] = (0x23, true);
        map["pageup"] = map["pgup"] = (0x21, true);
        map["pagedown"] = map["pgdn"] = (0x22, true);
        map["left"] = (0x25, true);
        map["up"] = (0x26, true);
        map["right"] = (0x27, true);
        map["down"] = (0x28, true);

        map["volumeup"] = (0xAF, true);
        map["volumedown"] = (0xAE, true);
        map["volumemute"] = map["mute"] = (0xAD, true);
        map["medianext"] = (0xB0, true);
        map["mediaprev"] = (0xB1, true);
        map["mediaplay"] = map["mediaplaypause"] = (0xB3, true);

        map[";"] = map["semicolon"] = (0xBA, false);
        map["="] = map["equal"] = map["plus"] = (0xBB, false);
        map[","] = map["comma"] = (0xBC, false);
        map["-"] = map["minus"] = (0xBD, false);
        map["."] = map["period"] = (0xBE, false);
        map["/"] = map["slash"] = (0xBF, false);
        map["`"] = map["backquote"] = (0xC0, false);
        map["["] = (0xDB, false);
        map["\\"] = map["backslash"] = (0xDC, false);
        map["]"] = (0xDD, false);
        map["'"] = map["quote"] = (0xDE, false);

        return map;
    }

    /// <summary>
    /// KeyMap を逆引きした表。別名は同じ仮想キーに複数登録されているので、
    /// 後から入った名前（＝各行の左端に書いた読みやすい名前）で上書きする。
    /// </summary>
    private static Dictionary<ushort, string> BuildNameMap()
    {
        var reverse = new Dictionary<ushort, string>();
        foreach (var (name, key) in KeyMap)
            reverse[key.Vk] = name;
        return reverse;
    }
}
