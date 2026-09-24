using System.Runtime.InteropServices;

namespace MagicCircle;

/// <summary>
/// 設定画面で「押したキーの組み合わせ」を取り込む。
/// 取り込み中はキー入力を握り潰すので、win+d のような OS のショートカットが暴発しない。
/// </summary>
internal sealed class KeyboardCapture : IDisposable
{
    private const uint VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const uint VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    private const uint VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    private const uint VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const uint VK_ESCAPE = 0x1B;

    private readonly Native.LowLevelKeyboardProc _proc; // GC 回収を防ぐため保持する
    private readonly Control _marshal;                  // UI スレッドへ結果を戻すための受け皿
    private readonly Action<string?> _completed;        // null = 中止

    private IntPtr _hook = IntPtr.Zero;
    private bool _ctrl, _shift, _alt, _win;

    /// <param name="marshal">結果を UI スレッドへ戻すためのコントロール。</param>
    /// <param name="completed">取り込んだキー指定。Esc で中止したときは null。</param>
    public KeyboardCapture(Control marshal, Action<string?> completed)
    {
        _marshal = marshal;
        _completed = completed;
        _proc = HookProc;
    }

    public bool IsActive => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _ctrl = _shift = _alt = _win = false;

        var module = Native.GetModuleHandle(null);
        _hook = Native.SetWindowsHookExKeyboard(Native.WH_KEYBOARD_LL, _proc, module, 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException(
                $"キーボードフックを設定できませんでした (Win32 error {Marshal.GetLastWin32Error()})");
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        int message = (int)wParam;
        bool isDown = message == Native.WM_KEYDOWN || message == Native.WM_SYSKEYDOWN;
        bool isUp = message == Native.WM_KEYUP || message == Native.WM_SYSKEYUP;

        if (!isDown && !isUp) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        // 修飾キーは状態だけ覚える。握り潰しているので GetKeyState では追えない
        switch (data.vkCode)
        {
            case VK_CONTROL or VK_LCONTROL or VK_RCONTROL: _ctrl = isDown; return (IntPtr)1;
            case VK_SHIFT or VK_LSHIFT or VK_RSHIFT: _shift = isDown; return (IntPtr)1;
            case VK_MENU or VK_LMENU or VK_RMENU: _alt = isDown; return (IntPtr)1;
            case VK_LWIN or VK_RWIN: _win = isDown; return (IntPtr)1;
        }

        if (!isDown) return (IntPtr)1; // 主キーの解放は捨てる

        if (data.vkCode == VK_ESCAPE)
        {
            Finish(null);
            return (IntPtr)1;
        }

        if (!KeySender.TryGetName((ushort)data.vkCode, out var name))
            return (IntPtr)1; // 扱えないキーは無視して押し直してもらう

        var parts = new List<string>(5);
        if (_ctrl) parts.Add("ctrl");
        if (_shift) parts.Add("shift");
        if (_alt) parts.Add("alt");
        if (_win) parts.Add("win");
        parts.Add(name);

        Finish(string.Join("+", parts));
        return (IntPtr)1;
    }

    /// <summary>フックを外してから、UI スレッドで結果を返す。</summary>
    private void Finish(string? spec)
    {
        Stop();
        try
        {
            if (_marshal.IsHandleCreated) _marshal.BeginInvoke(new Action(() => _completed(spec)));
        }
        catch
        {
            // ウィンドウが閉じられた直後などは結果を捨てる
        }
    }

    public void Dispose() => Stop();
}
