using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace MagicCircle;

/// <summary>コマンドヘルプの 1 項目（この方向へ進むと何になるか）。</summary>
internal readonly record struct HelpItem(char Direction, string Label);

/// <summary>
/// 仮想スクリーン全体を覆う、クリック透過のレイヤードウィンドウ。
/// 軌跡と魔法陣を UpdateLayeredWindow で毎フレーム描き直す。
/// </summary>
internal sealed class OverlayWindow : Form
{
    private enum Phase { Hidden, Drawing, Success, Miss }

    private const int IntroMs = 220;
    private const int SuccessMs = 620;
    private const int MissMs = 380;

    private const int HelpFadeMs = 160;
    private const int HelpMaxChars = 12;

    /// <summary>方向インデックス 0..7 に対応する記号（GestureRecognizer と同じ並び）。</summary>
    private const string Directions = "→↗↑↖←↙↓↘";

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Point> _points = new();

    private Rectangle _screen;
    private IntPtr _hdcMem, _hBitmap, _hOldBitmap;
    private Bitmap? _surface;
    private Graphics? _g;

    private Phase _phase = Phase.Hidden;
    private long _phaseStart;
    private long _strokeStart;
    private Point _origin;
    private Point _end;
    private string _code = "";
    private string? _name;
    private Rectangle _prevDirty = Rectangle.Empty;

    private OverlayStyle _style = OverlayStyle.Create(OverlayStyleKind.Arcane);
    private readonly Font _codeFont;
    private readonly Font _nameFont;
    private readonly Font _helpFont;

    private IReadOnlyList<HelpItem> _help = Array.Empty<HelpItem>();
    private long _lastMoveMs;

    /// <summary>手を止めたときにコマンドヘルプを出すか。</summary>
    public bool HelpEnabled { get; set; } = true;

    /// <summary>ヘルプを出すまでの静止時間（ミリ秒）。</summary>
    public int HelpDelayMs { get; set; } = 400;

    public OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "魔法陣オーバーレイ";

        _codeFont = CreateFont(22f, FontStyle.Bold);
        _nameFont = CreateFont(13f, FontStyle.Regular);
        _helpFont = CreateFont(10.5f, FontStyle.Regular);

        _timer.Tick += (_, _) => Render();
        SystemEvents_Init();
        RebuildSurface();
    }

    private static Font CreateFont(float size, FontStyle style)
    {
        foreach (var family in new[] { "Yu Gothic UI", "Meiryo UI", "Segoe UI" })
        {
            try { return new Font(family, size, style, GraphicsUnit.Point); }
            catch { /* 次の候補へ */ }
        }
        return new Font(SystemFonts.DefaultFont.FontFamily, size, style);
    }

    private void SystemEvents_Init() =>
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => RebuildSurface();

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT
                        | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    /// <summary>配色と見た目の種類を反映する。種類が変わったら描画スタイルごと差し替える。</summary>
    public void ApplyTheme(Color theme, Color accent, OverlayStyleKind kind)
    {
        if (_style.Kind != kind) _style = OverlayStyle.Create(kind);
        _style.ApplyColors(theme, accent);
    }

    // ---------------- 描画対象サーフェス ----------------

    private void RebuildSurface()
    {
        ReleaseSurface();

        _screen = Native.GetVirtualScreen();
        if (_screen.Width <= 0 || _screen.Height <= 0) return;

        Bounds = _screen;

        var screenDc = Native.GetDC(IntPtr.Zero);
        try
        {
            var header = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = _screen.Width,
                biHeight = -_screen.Height, // 上から下へのビットマップ
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Native.BI_RGB,
            };

            _hdcMem = Native.CreateCompatibleDC(screenDc);
            _hBitmap = Native.CreateDIBSection(screenDc, ref header, Native.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (_hBitmap == IntPtr.Zero) throw new InvalidOperationException("DIB セクションを作成できませんでした");

            _hOldBitmap = Native.SelectObject(_hdcMem, _hBitmap);
            _surface = new Bitmap(_screen.Width, _screen.Height, _screen.Width * 4,
                                  PixelFormat.Format32bppPArgb, bits);
            _g = Graphics.FromImage(_surface);
            _g.SmoothingMode = SmoothingMode.AntiAlias;
            _g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            _g.TextRenderingHint = TextRenderingHint.AntiAlias;
            _prevDirty = new Rectangle(0, 0, _screen.Width, _screen.Height);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void ReleaseSurface()
    {
        _g?.Dispose(); _g = null;
        _surface?.Dispose(); _surface = null;
        if (_hdcMem != IntPtr.Zero)
        {
            if (_hOldBitmap != IntPtr.Zero) Native.SelectObject(_hdcMem, _hOldBitmap);
            Native.DeleteDC(_hdcMem);
            _hdcMem = IntPtr.Zero;
            _hOldBitmap = IntPtr.Zero;
        }
        if (_hBitmap != IntPtr.Zero)
        {
            Native.DeleteObject(_hBitmap);
            _hBitmap = IntPtr.Zero;
        }
    }

    // ---------------- 外部から呼ぶ状態遷移 ----------------

    public void BeginStroke(Point screenPoint)
    {
        _points.Clear();
        _points.Add(screenPoint);
        _origin = screenPoint;
        _end = screenPoint;
        _code = "";
        _name = null;
        _phase = Phase.Drawing;
        _strokeStart = _clock.ElapsedMilliseconds;
        _phaseStart = _strokeStart;
        _lastMoveMs = _strokeStart;

        if (!Visible) Show();
        BringToFront_NoActivate();
        _timer.Start();
        Render();
    }

    /// <summary>
    /// 他の常時最前面ウィンドウ（ランチャーや仮想キーボード等）より手前に出し直す。
    /// TopMost 同士は後から前面に来たものが上になるため、描き始めるたびに取り直す。
    /// </summary>
    private void BringToFront_NoActivate()
    {
        if (!IsHandleCreated) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    public void AddPoint(Point screenPoint)
    {
        if (_phase != Phase.Drawing) return;
        _points.Add(screenPoint);
        _end = screenPoint;
        _lastMoveMs = _clock.ElapsedMilliseconds; // 動いている間はヘルプを出さない
    }

    public void SetCandidate(string code, string? name)
    {
        _code = code;
        _name = name;
    }

    /// <summary>現在の軌跡に続けられる方向とコマンド名。</summary>
    public void SetHelp(IReadOnlyList<HelpItem> items) => _help = items;

    /// <summary>いま表示対象になっているコマンドヘルプ。</summary>
    public IReadOnlyList<HelpItem> Help => _help;

    public void Finish(string code, string? name, bool success)
    {
        if (_phase == Phase.Hidden) return;
        _code = code;
        _name = name;
        _phase = success ? Phase.Success : Phase.Miss;
        _phaseStart = _clock.ElapsedMilliseconds;
        Render();
    }

    public void CancelStroke()
    {
        if (_phase == Phase.Hidden) return;
        _phase = Phase.Hidden;
        _points.Clear();
        _timer.Stop();
        ClearAndPush();
        Hide();
    }

    // ---------------- 描画 ----------------

    private void Render()
    {
        if (_g is null || _surface is null) return;

        long now = _clock.ElapsedMilliseconds;
        long phaseT = now - _phaseStart;

        if (_phase == Phase.Success && phaseT > SuccessMs) { CancelStroke(); return; }
        if (_phase == Phase.Miss && phaseT > MissMs) { CancelStroke(); return; }
        if (_phase == Phase.Hidden) { CancelStroke(); return; }

        var g = _g;
        // 前フレームで描いた範囲だけを透明に戻す
        if (!_prevDirty.IsEmpty)
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            using var clear = new SolidBrush(Color.FromArgb(0, 0, 0, 0));
            g.FillRectangle(clear, _prevDirty);
            g.CompositingMode = CompositingMode.SourceOver;
        }

        var dirty = Rectangle.Empty;
        float spin = (now % 12000) / 12000f * 360f;

        switch (_phase)
        {
            case Phase.Drawing:
            {
                float intro = Ease(Math.Min(1f, (now - _strokeStart) / (float)IntroMs));
                float radius = _style.SigilRadius * intro;
                _style.DrawSigil(g, ToLocal(_origin), radius, spin, 0.85f * intro);
                Union(ref dirty, CircleBounds(ToLocal(_origin), radius * 1.35f + 20));

                Union(ref dirty, DrawTrail(g, 1f));
                Union(ref dirty, DrawHead(g, ToLocal(_end), 1f));

                // 手を止めている間だけ、次に進める方向のコマンド名を周囲に出す
                long idle = now - _lastMoveMs;
                if (HelpEnabled && _help.Count > 0 && idle >= HelpDelayMs)
                {
                    float helpAlpha = Math.Min(1f, (idle - HelpDelayMs) / (float)HelpFadeMs);
                    Union(ref dirty, DrawHelp(g, ToLocal(_end), Ease(helpAlpha)));
                }

                Union(ref dirty, DrawLabel(g, ToLocal(_end), _code, _name, 1f, false));
                break;
            }
            case Phase.Success:
            {
                float t = phaseT / (float)SuccessMs;
                float fade = 1f - Ease(t);
                Union(ref dirty, DrawTrail(g, fade * 0.8f));

                float burstR = _style.SigilRadius * (0.7f + _style.BurstScale * Ease(t));
                _style.DrawBurst(g, ToLocal(_end), burstR, spin * 1.6f, fade);
                Union(ref dirty, CircleBounds(ToLocal(_end), burstR * 1.9f + 20));

                var labelAnchor = new Point(ToLocal(_end).X, ToLocal(_end).Y + (int)burstR + 8);
                Union(ref dirty, DrawLabel(g, labelAnchor, _code, _name, fade, true));
                break;
            }
            case Phase.Miss:
            {
                float t = phaseT / (float)MissMs;
                float fade = 1f - Ease(t);
                Union(ref dirty, DrawTrail(g, fade * 0.5f));
                Union(ref dirty, DrawLabel(g, ToLocal(_end), _code, "未登録のジェスチャー", fade, false));
                break;
            }
        }

        dirty.Inflate(24, 24);
        dirty.Intersect(new Rectangle(0, 0, _screen.Width, _screen.Height));
        _prevDirty = dirty;

        Push();
    }

    private Point ToLocal(Point screenPoint) =>
        new(screenPoint.X - _screen.X, screenPoint.Y - _screen.Y);

    private static float Ease(float t) => 1f - (float)Math.Pow(1 - Math.Clamp(t, 0f, 1f), 3);

    private static void Union(ref Rectangle acc, Rectangle r)
    {
        if (r.IsEmpty) return;
        acc = acc.IsEmpty ? r : Rectangle.Union(acc, r);
    }

    private static Rectangle CircleBounds(Point center, float radius) =>
        Rectangle.FromLTRB((int)(center.X - radius), (int)(center.Y - radius),
                           (int)(center.X + radius), (int)(center.Y + radius));

    private static Color Fade(Color color, float alpha, float scale = 1f) =>
        Color.FromArgb((int)Math.Clamp(color.A * alpha * scale, 0, 255), color.R, color.G, color.B);

    private Rectangle DrawTrail(Graphics g, float alpha)
    {
        if (_points.Count < 2 || alpha <= 0.01f) return Rectangle.Empty;

        var pts = new PointF[_points.Count];
        for (int i = 0; i < _points.Count; i++)
        {
            var p = ToLocal(_points[i]);
            pts[i] = new PointF(p.X, p.Y);
        }

        using var path = new GraphicsPath();
        if (pts.Length >= 3) path.AddCurve(pts, 0.35f);
        else path.AddLines(pts);

        _style.DrawTrail(g, path, alpha);

        var b = path.GetBounds();
        int pad = _style.TrailPadding;
        return Rectangle.FromLTRB((int)b.Left - pad, (int)b.Top - pad,
                                  (int)b.Right + pad, (int)b.Bottom + pad);
    }

    private Rectangle DrawHead(Graphics g, Point center, float alpha)
    {
        _style.DrawHead(g, center, alpha);
        return CircleBounds(center, _style.HeadRadius + 4);
    }

    /// <summary>
    /// 現在地の周りに 8 方位のコマンドヘルプを配置する。
    /// ラベルは方向ベクトル上に置き、内側の辺が半径 <see cref="HelpRadius"/> に来るよう押し出す。
    /// </summary>
    private Rectangle DrawHelp(Graphics g, Point center, float alpha)
    {
        if (alpha <= 0.02f) return Rectangle.Empty;

        var bounds = Rectangle.Empty;

        foreach (var item in _help)
        {
            int index = Directions.IndexOf(item.Direction);
            if (index < 0) continue;

            string text = Shorten(item.Label, HelpMaxChars);
            var arrow = item.Direction.ToString();
            var textSize = g.MeasureString(text, _helpFont);
            var arrowSize = g.MeasureString(arrow, _helpFont);

            float w = arrowSize.Width + textSize.Width + 22;
            float h = Math.Max(textSize.Height, arrowSize.Height) + 12;

            double angle = index * Math.PI / 4;
            float dx = (float)Math.Cos(angle);
            float dy = -(float)Math.Sin(angle); // 画面座標は下向きが +Y

            // 斜め方向でも中心の魔法陣に被らないよう、箱の半分だけ余分に押し出す
            float push = Math.Abs(dx) * w / 2 + Math.Abs(dy) * h / 2;
            float radius = _style.HelpRadius + push;

            float x = center.X + dx * radius - w / 2;
            float y = center.Y + dy * radius - h / 2;
            x = Math.Clamp(x, 4, Math.Max(4, _screen.Width - w - 4));
            y = Math.Clamp(y, 4, Math.Max(4, _screen.Height - h - 4));

            var box = new RectangleF(x, y, w, h);
            _style.DrawPanel(g, box, alpha, false);

            using (var arrowBrush = new SolidBrush(Fade(_style.ArrowColor, alpha)))
            using (var textBrush = new SolidBrush(Fade(_style.PrimaryTextColor, alpha, 0.92f)))
            {
                g.DrawString(arrow, _helpFont, arrowBrush, box.X + 8, box.Y + 6);
                g.DrawString(text, _helpFont, textBrush, box.X + 12 + arrowSize.Width, box.Y + 6);
            }

            Union(ref bounds, Rectangle.Round(box));
        }

        return bounds;
    }

    private static string Shorten(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..(maxChars - 1)] + "…";

    private Rectangle DrawLabel(Graphics g, Point anchor, string code, string? name, float alpha, bool centered)
    {
        if (alpha <= 0.02f) return Rectangle.Empty;

        string codeText = code.Length > 0 ? code : "…";
        string nameText = name ?? "";

        var codeSize = g.MeasureString(codeText, _codeFont);
        var nameSize = nameText.Length > 0 ? g.MeasureString(nameText, _nameFont) : SizeF.Empty;

        float w = Math.Max(codeSize.Width, nameSize.Width) + 28;
        float h = codeSize.Height + (nameText.Length > 0 ? nameSize.Height + 4 : 0) + 18;

        float x = centered ? anchor.X - w / 2 : anchor.X + 28;
        float y = centered ? anchor.Y : anchor.Y + 24;

        x = Math.Clamp(x, 8, Math.Max(8, _screen.Width - w - 8));
        y = Math.Clamp(y, 8, Math.Max(8, _screen.Height - h - 8));

        var box = new RectangleF(x, y, w, h);
        _style.DrawPanel(g, box, alpha, true);

        using (var codeBrush = new SolidBrush(Fade(_style.PrimaryTextColor, alpha)))
        using (var nameBrush = new SolidBrush(Fade(_style.SecondaryTextColor, alpha)))
        {
            var format = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(codeText, _codeFont, codeBrush,
                new RectangleF(box.X, box.Y + 8, box.Width, codeSize.Height), format);
            if (nameText.Length > 0)
            {
                g.DrawString(nameText, _nameFont, nameBrush,
                    new RectangleF(box.X, box.Y + 8 + codeSize.Height + 2, box.Width, nameSize.Height), format);
            }
            format.Dispose();
        }

        return Rectangle.Round(box);
    }

    // ---------------- 画面への転送 ----------------

    private void ClearAndPush()
    {
        if (_g is null) return;
        _g.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.FromArgb(0, 0, 0, 0)))
            _g.FillRectangle(clear, new Rectangle(0, 0, _screen.Width, _screen.Height));
        _g.CompositingMode = CompositingMode.SourceOver;
        _prevDirty = Rectangle.Empty;
        Push();
    }

    private void Push()
    {
        if (!IsHandleCreated || _hdcMem == IntPtr.Zero) return;

        Native.GdiFlush();

        var screenDc = Native.GetDC(IntPtr.Zero);
        try
        {
            var dst = new Native.POINT(_screen.X, _screen.Y);
            var src = new Native.POINT(0, 0);
            var size = new Native.SIZE(_screen.Width, _screen.Height);
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA,
            };
            Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, _hdcMem, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _codeFont.Dispose();
            _nameFont.Dispose();
            _helpFont.Dispose();
            ReleaseSurface();
        }
        base.Dispose(disposing);
    }
}
