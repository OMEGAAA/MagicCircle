using System.Runtime.InteropServices;

namespace MagicCircle;

/// <summary>フックが受け取ったマウス操作の受け皿。true を返すとイベントを握り潰す。</summary>
internal interface IGestureSink
{
    bool OnRightDown(Point screenPoint);
    bool OnMouseMove(Point screenPoint);
    bool OnRightUp(Point screenPoint);

    /// <summary>右ドラッグ中に左／中ボタンが押された。ジェスチャーを中止する。</summary>
    bool OnCancelButtonDown(bool isLeft);

    /// <summary>中止に使われた左／中ボタンが離された。</summary>
    bool OnCancelButtonUp(bool isLeft);
}

/// <summary>
/// WH_MOUSE_LL による全画面のマウス監視。
/// フックコールバックはこのクラスを生成したスレッド（＝メッセージループを持つ UI スレッド）で呼ばれる。
/// </summary>
internal sealed class MouseHook : IDisposable
{
    private readonly IGestureSink _sink;
    private readonly Native.LowLevelMouseProc _proc; // GC 回収を防ぐためフィールドで保持する
    private IntPtr _hook = IntPtr.Zero;

    public MouseHook(IGestureSink sink)
    {
        _sink = sink;
        _proc = HookProc;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        var module = Native.GetModuleHandle(null);
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, module, 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException(
                $"マウスフックを設定できませんでした (Win32 error {Marshal.GetLastWin32Error()})");
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);

        // 自分が注入した入力は素通しする（右クリック再生時の自己ループ防止）
        if (data.dwExtraInfo.ToUInt64() == Native.MAGIC_EXTRA_INFO)
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        var pt = new Point(data.pt.x, data.pt.y);
        bool swallow = false;

        try
        {
            switch ((int)wParam)
            {
                case Native.WM_RBUTTONDOWN: swallow = _sink.OnRightDown(pt); break;
                case Native.WM_RBUTTONUP: swallow = _sink.OnRightUp(pt); break;
                case Native.WM_MOUSEMOVE: swallow = _sink.OnMouseMove(pt); break;
                case Native.WM_LBUTTONDOWN: swallow = _sink.OnCancelButtonDown(true); break;
                case Native.WM_LBUTTONUP: swallow = _sink.OnCancelButtonUp(true); break;
                case Native.WM_MBUTTONDOWN: swallow = _sink.OnCancelButtonDown(false); break;
                case Native.WM_MBUTTONUP: swallow = _sink.OnCancelButtonUp(false); break;
            }
        }
        catch
        {
            // フック内で例外を投げるとフックが外されるため、握り潰して素通しする
            swallow = false;
        }

        return swallow ? (IntPtr)1 : Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>握り潰した右クリックを、押された場所で本来のアプリへ流し直す。</summary>
    public static void ReplayRightClick()
    {
        var inputs = new[]
        {
            MouseInput(Native.MOUSEEVENTF_RIGHTDOWN),
            MouseInput(Native.MOUSEEVENTF_RIGHTUP),
        };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    private static Native.INPUT MouseInput(uint flags) => new()
    {
        type = Native.INPUT_MOUSE,
        U = new Native.InputUnion
        {
            mi = new Native.MOUSEINPUT
            {
                dx = 0,
                dy = 0,
                mouseData = 0,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = (IntPtr)Native.MAGIC_EXTRA_INFO,
            }
        }
    };

    public void Dispose() => Uninstall();
}
