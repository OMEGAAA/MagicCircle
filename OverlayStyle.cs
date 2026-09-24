using System.Drawing.Drawing2D;

namespace MagicCircle;

/// <summary>オーバーレイの見た目の種類。</summary>
internal enum OverlayStyleKind
{
    /// <summary>二重円・目盛り・六芒星で構成した魔法陣。</summary>
    Arcane,

    /// <summary>細い円と軌跡だけの静かな線画。</summary>
    Minimal,

    /// <summary>角ブラケットと分割リングの SF 風 HUD。</summary>
    Hud,
}

/// <summary>
/// 魔法陣・軌跡・パネルの描き方一式。配置やアニメーションの進行は
/// <see cref="OverlayWindow"/> が持ち、実際の筆致だけをこのクラスで差し替える。
/// </summary>
internal abstract class OverlayStyle
{
    /// <summary>陣と軌跡の主色。</summary>
    protected Color Theme { get; private set; } = Color.FromArgb(0x8A, 0x6B, 0xFF);

    /// <summary>軌跡の芯やハイライトに使う色。</summary>
    protected Color Accent { get; private set; } = Color.FromArgb(0x4D, 0xE1, 0xFF);

    public abstract OverlayStyleKind Kind { get; }

    /// <summary>トレイメニューに出す名前。</summary>
    public abstract string DisplayName { get; }

    /// <summary>陣の基準半径（px）。</summary>
    public virtual float SigilRadius => 58f;

    /// <summary>コマンドヘルプを並べる半径（px）。</summary>
    public virtual float HelpRadius => 86f;

    /// <summary>先端の光の半径（px）。</summary>
    public virtual float HeadRadius => 24f;

    /// <summary>成功時に陣が広がる量（基準半径に対する倍率）。</summary>
    public virtual float BurstScale => 1.9f;

    /// <summary>軌跡の周りに必要な余白（px）。再描画範囲の計算に使う。</summary>
    public virtual int TrailPadding => 14;

    /// <summary>パネルの角丸半径（px）。</summary>
    public virtual float PanelCorner => 10f;

    /// <summary>パネル上の主テキスト色。</summary>
    public virtual Color PrimaryTextColor => Color.White;

    /// <summary>パネル上の副テキスト色。</summary>
    public virtual Color SecondaryTextColor => Accent;

    /// <summary>コマンドヘルプの矢印の色。</summary>
    public virtual Color ArrowColor => Accent;

    public void ApplyColors(Color theme, Color accent)
    {
        Theme = theme;
        Accent = accent;
    }

    /// <summary>描いている最中の陣。</summary>
    public abstract void DrawSigil(Graphics g, PointF center, float radius, float spin, float alpha);

    /// <summary>成功時に弾ける陣。</summary>
    public abstract void DrawBurst(Graphics g, PointF center, float radius, float spin, float alpha);

    /// <summary>なぞった軌跡。<paramref name="path"/> は補間済み。</summary>
    public abstract void DrawTrail(Graphics g, GraphicsPath path, float alpha);

    /// <summary>軌跡の先端。</summary>
    public abstract void DrawHead(Graphics g, PointF center, float alpha);

    /// <summary>ラベルやコマンドヘルプの下敷き。<paramref name="strong"/> はコマンド名パネル。</summary>
    public abstract void DrawPanel(Graphics g, RectangleF box, float alpha, bool strong);

    // ---------------- 生成 ----------------

    public static OverlayStyle Create(OverlayStyleKind kind) => kind switch
    {
        OverlayStyleKind.Minimal => new MinimalStyle(),
        OverlayStyleKind.Hud => new HudStyle(),
        _ => new ArcaneStyle(),
    };

    /// <summary>設定ファイルの文字列を種類へ。未知の値は既定の魔法陣に倒す。</summary>
    public static OverlayStyleKind ParseKind(string? id) => (id ?? "").Trim().ToLowerInvariant() switch
    {
        "minimal" => OverlayStyleKind.Minimal,
        "hud" => OverlayStyleKind.Hud,
        _ => OverlayStyleKind.Arcane,
    };

    /// <summary>設定ファイルへ書き出す文字列。</summary>
    public static string ToId(OverlayStyleKind kind) => kind switch
    {
        OverlayStyleKind.Minimal => "minimal",
        OverlayStyleKind.Hud => "hud",
        _ => "arcane",
    };

    /// <summary>トレイメニューに並べる順序。</summary>
    public static readonly OverlayStyleKind[] All =
    {
        OverlayStyleKind.Arcane, OverlayStyleKind.Minimal, OverlayStyleKind.Hud,
    };

    public static string NameOf(OverlayStyleKind kind) => Create(kind).DisplayName;

    // ---------------- 共通ヘルパ ----------------

    protected static Color Fade(Color color, float alpha, float scale = 1f) =>
        Color.FromArgb((int)Math.Clamp(color.A * alpha * scale, 0, 255), color.R, color.G, color.B);

    protected static void Stroke(Graphics g, GraphicsPath path, float width, Color color)
    {
        using var pen = new Pen(color, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawPath(pen, path);
    }

    protected static void Ellipse(Graphics g, Pen pen, float radius) =>
        g.DrawEllipse(pen, -radius, -radius, radius * 2, radius * 2);

    protected static void Polygon(Graphics g, Pen pen, float radius, int sides, float offsetDeg)
    {
        var pts = new PointF[sides];
        for (int i = 0; i < sides; i++)
        {
            double a = (i * 360.0 / sides + offsetDeg) * Math.PI / 180.0;
            pts[i] = new PointF((float)(Math.Cos(a) * radius), (float)(Math.Sin(a) * radius));
        }
        g.DrawPolygon(pen, pts);
    }

    /// <summary>原点中心の座標系に切り替える。呼び出し側は戻り値を Restore すること。</summary>
    protected static GraphicsState Center(Graphics g, PointF center, float spin)
    {
        var state = g.Save();
        g.TranslateTransform(center.X, center.Y);
        g.RotateTransform(spin);
        return state;
    }

    protected static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

// ==================================================================
// 魔法陣
// ==================================================================

/// <summary>二重円・目盛り・六芒星・逆回転する外弧で組んだ、既定の魔法陣。</summary>
internal sealed class ArcaneStyle : OverlayStyle
{
    public override OverlayStyleKind Kind => OverlayStyleKind.Arcane;
    public override string DisplayName => "魔法陣";

    public override void DrawSigil(Graphics g, PointF center, float radius, float spin, float alpha)
    {
        if (radius < 2f || alpha <= 0.01f) return;

        // 外側の弧（逆回転）
        var outer = Center(g, center, -spin * 0.6f);
        using (var arcPen = new Pen(Fade(Accent, alpha, 0.75f), 2f))
        {
            float ar = radius * 1.1f;
            for (int i = 0; i < 3; i++)
                g.DrawArc(arcPen, -ar, -ar, ar * 2, ar * 2, i * 120f + 10f, 76f);
        }
        g.Restore(outer);

        var state = Center(g, center, spin);

        using (var glow = new Pen(Fade(Theme, alpha, 0.35f), 7f))
            Ellipse(g, glow, radius);

        using (var pen = new Pen(Fade(Color.White, alpha, 0.85f), 1.6f))
        using (var soft = new Pen(Fade(Accent, alpha, 0.7f), 1.2f))
        {
            Ellipse(g, pen, radius);

            float inner = radius * 0.78f;
            Ellipse(g, soft, inner);

            // 目盛り
            for (int i = 0; i < 24; i++)
            {
                double a = i * Math.PI / 12;
                float x1 = (float)(Math.Cos(a) * inner), y1 = (float)(Math.Sin(a) * inner);
                float x2 = (float)(Math.Cos(a) * radius), y2 = (float)(Math.Sin(a) * radius);
                g.DrawLine(soft, x1, y1, x2, y2);
            }

            // 六芒星
            float sr = radius * 0.62f;
            Polygon(g, pen, sr, 3, 0);
            Polygon(g, pen, sr, 3, 60);

            Ellipse(g, soft, radius * 0.3f);

            // 節点
            using var dot = new SolidBrush(Fade(Accent, alpha, 0.9f));
            for (int i = 0; i < 6; i++)
            {
                double a = i * Math.PI / 3 + Math.PI / 6;
                float x = (float)(Math.Cos(a) * radius * 0.9f), y = (float)(Math.Sin(a) * radius * 0.9f);
                g.FillEllipse(dot, x - 2.5f, y - 2.5f, 5, 5);
            }
        }

        g.Restore(state);
    }

    public override void DrawBurst(Graphics g, PointF center, float radius, float spin, float alpha)
    {
        DrawSigil(g, center, radius, spin, alpha);
        if (alpha <= 0.01f) return;

        using var pen = new Pen(Fade(Accent, alpha * 0.7f, 0.8f), 2.5f);
        float r = radius * 1.25f;
        g.DrawEllipse(pen, center.X - r, center.Y - r, r * 2, r * 2);
    }

    public override void DrawTrail(Graphics g, GraphicsPath path, float alpha)
    {
        Stroke(g, path, 20f, Fade(Theme, alpha, 0.16f));
        Stroke(g, path, 11f, Fade(Theme, alpha, 0.45f));
        Stroke(g, path, 4.5f, Fade(Accent, alpha, 0.9f));
        Stroke(g, path, 1.6f, Fade(Color.White, alpha, 0.85f));
    }

    public override void DrawHead(Graphics g, PointF center, float alpha)
    {
        float r = HeadRadius;
        using var path = new GraphicsPath();
        path.AddEllipse(center.X - r, center.Y - r, r * 2, r * 2);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Fade(Color.White, alpha, 0.9f),
            SurroundColors = new[] { Color.FromArgb(0, Accent) },
            CenterPoint = center,
        };
        g.FillPath(brush, path);
    }

    public override void DrawPanel(Graphics g, RectangleF box, float alpha, bool strong)
    {
        using var back = new SolidBrush(Fade(Color.FromArgb(strong ? 215 : 205, 12, 10, 26), alpha));
        using var edge = new Pen(Fade(Theme, alpha, strong ? 0.8f : 0.75f), strong ? 1.4f : 1.2f);
        using var path = RoundedRect(box, strong ? PanelCorner : 8f);
        g.FillPath(back, path);
        g.DrawPath(edge, path);
    }
}

// ==================================================================
// ミニマル
// ==================================================================

/// <summary>細い円と小さな目盛りだけの静かな線画。装飾を削って軌跡そのものを見せる。</summary>
internal sealed class MinimalStyle : OverlayStyle
{
    public override OverlayStyleKind Kind => OverlayStyleKind.Minimal;
    public override string DisplayName => "ミニマル";

    public override float SigilRadius => 44f;
    public override float HelpRadius => 76f;
    public override float HeadRadius => 12f;
    public override float BurstScale => 2.6f;
    public override int TrailPadding => 10;
    public override float PanelCorner => 5f;
    public override Color SecondaryTextColor => Color.FromArgb(0xC2, 0xC6, 0xD0);

    public override void DrawSigil(Graphics g, PointF center, float radius, float spin, float alpha)
    {
        if (radius < 2f || alpha <= 0.01f) return;

        // 明るい背景でも輪郭が消えないぶんだけの、ごく薄い下敷き
        var state = Center(g, center, 0f);
        using (var halo = new Pen(Fade(Theme, alpha, 0.18f), 5f))
            Ellipse(g, halo, radius);

        using (var ring = new Pen(Fade(Color.White, alpha, 0.72f), 1.4f))
            Ellipse(g, ring, radius);

        using (var dot = new SolidBrush(Fade(Accent, alpha, 0.9f)))
            g.FillEllipse(dot, -3f, -3f, 6f, 6f);
        g.Restore(state);

        // 四方の短い目盛りだけがゆっくり回る
        var ticks = Center(g, center, spin * 0.25f);
        using (var tick = new Pen(Fade(Accent, alpha, 0.8f), 1.6f))
        {
            for (int i = 0; i < 4; i++)
            {
                double a = i * Math.PI / 2;
                float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
                g.DrawLine(tick, cos * radius * 1.08f, sin * radius * 1.08f,
                                 cos * radius * 1.26f, sin * radius * 1.26f);
            }
        }
        g.Restore(ticks);
    }

    public override void DrawBurst(Graphics g, PointF center, float radius, float spin, float alpha)
    {
        if (alpha <= 0.01f) return;

        // 陣は描き直さず、細いリングが二重に広がるだけ
        using (var outer = new Pen(Fade(Color.White, alpha, 0.7f), 1.4f))
            g.DrawEllipse(outer, center.X - radius, center.Y - radius, radius * 2, radius * 2);

        float inner = radius * 0.62f;
        using (var pen = new Pen(Fade(Accent, alpha, 0.85f), 2f))
            g.DrawEllipse(pen, center.X - inner, center.Y - inner, inner * 2, inner * 2);

        using (var core = new SolidBrush(Fade(Accent, alpha, 0.9f)))
            g.FillEllipse(core, center.X - 3.5f, center.Y - 3.5f, 7f, 7f);
    }

    public override void DrawTrail(Graphics g, GraphicsPath path, float alpha)
    {
        Stroke(g, path, 10f, Fade(Theme, alpha, 0.14f));
        Stroke(g, path, 3.2f, Fade(Accent, alpha, 0.8f));
        Stroke(g, path, 1.3f, Fade(Color.White, alpha, 0.85f));
    }

    public override void DrawHead(Graphics g, PointF center, float alpha)
    {
        float r = HeadRadius;
        using (var glowPath = new GraphicsPath())
        {
            glowPath.AddEllipse(center.X - r, center.Y - r, r * 2, r * 2);
            using var glow = new PathGradientBrush(glowPath)
            {
                CenterColor = Fade(Color.White, alpha, 0.55f),
                SurroundColors = new[] { Color.FromArgb(0, Accent) },
                CenterPoint = center,
            };
            g.FillPath(glow, glowPath);
        }

        using var dot = new SolidBrush(Fade(Color.White, alpha, 0.9f));
        g.FillEllipse(dot, center.X - 2.5f, center.Y - 2.5f, 5f, 5f);
    }

    public override void DrawPanel(Graphics g, RectangleF box, float alpha, bool strong)
    {
        using var back = new SolidBrush(Fade(Color.FromArgb(strong ? 224 : 208, 24, 24, 28), alpha));
        using var edge = new Pen(Fade(Color.White, alpha, strong ? 0.26f : 0.18f), 1f);
        using var path = RoundedRect(box, PanelCorner);
        g.FillPath(back, path);
        g.DrawPath(edge, path);
    }
}

// ==================================================================
// HUD
// ==================================================================

/// <summary>角ブラケットと分割リングで組んだ、照準器のような SF 風の見た目。</summary>
internal sealed class HudStyle : OverlayStyle
{
    public override OverlayStyleKind Kind => OverlayStyleKind.Hud;
    public override string DisplayName => "HUD";

    public override float SigilRadius => 62f;
    public override float HelpRadius => 94f;
    public override float HeadRadius => 14f;
    public override float BurstScale => 1.6f;
    public override int TrailPadding => 12;
    public override float PanelCorner => 0f;

    public override void DrawSigil(Graphics g, PointF center, float radius, float spin, float alpha)
    {
        if (radius < 2f || alpha <= 0.01f) return;

        // 角ブラケット（回転させず、常に画面に正対させる）
        var frame = Center(g, center, 0f);
        using (var bracket = new Pen(Fade(Accent, alpha, 0.95f), 2.2f))
        {
            float s = radius * 1.12f;
            float arm = radius * 0.44f;
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    g.DrawLine(bracket, sx * s, sy * s, sx * (s - arm), sy * s);
                    g.DrawLine(bracket, sx * s, sy * s, sx * s, sy * (s - arm));
                }
            }
        }

        // 中心のクロスヘア（中央は空ける）
        using (var cross = new Pen(Fade(Color.White, alpha, 0.75f), 1.3f))
        {
            float gap = radius * 0.1f, len = radius * 0.28f;
            g.DrawLine(cross, gap, 0, len, 0);
            g.DrawLine(cross, -gap, 0, -len, 0);
            g.DrawLine(cross, 0, gap, 0, len);
            g.DrawLine(cross, 0, -gap, 0, -len);
            g.DrawRectangle(cross, -2.5f, -2.5f, 5f, 5f);
        }
        g.Restore(frame);

        // 外周の分割リング（回転）
        var ring = Center(g, center, spin * 0.5f);
        using (var seg = new Pen(Fade(Theme, alpha, 0.9f), 3.4f))
        {
            for (int i = 0; i < 8; i++)
                g.DrawArc(seg, -radius, -radius, radius * 2, radius * 2, i * 45f + 6f, 33f);
        }
        g.Restore(ring);

        // 内側のリングと目盛り（逆回転）
        var scale = Center(g, center, -spin * 0.35f);
        float inner = radius * 0.7f;
        using (var thin = new Pen(Fade(Accent, alpha, 0.6f), 1.1f))
        {
            Ellipse(g, thin, inner);
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI / 6;
                float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
                float outerR = inner * (i % 3 == 0 ? 1.18f : 1.09f);
                g.DrawLine(thin, cos * inner, sin * inner, cos * outerR, sin * outerR);
            }
        }
        g.Restore(scale);
    }

    public override void DrawBurst(Graphics g, PointF center, float radius, float spin, float alpha)
    {
        if (alpha <= 0.01f) return;

        DrawSigil(g, center, radius, spin, alpha);

        var state = Center(g, center, 0f);
        using (var edge = new Pen(Fade(Color.White, alpha, 0.55f), 1.6f))
        {
            float s = radius * 1.3f;
            g.DrawRectangle(edge, -s, -s, s * 2, s * 2);
        }
        g.Restore(state);
    }

    public override void DrawTrail(Graphics g, GraphicsPath path, float alpha)
    {
        Stroke(g, path, 9f, Fade(Theme, alpha, 0.22f));
        Stroke(g, path, 3.4f, Fade(Accent, alpha, 0.95f));
        Stroke(g, path, 1.2f, Fade(Color.White, alpha, 0.9f));
    }

    public override void DrawHead(Graphics g, PointF center, float alpha)
    {
        var state = Center(g, center, 45f);
        using (var box = new Pen(Fade(Accent, alpha, 0.9f), 1.6f))
        {
            float r = HeadRadius * 0.62f;
            g.DrawRectangle(box, -r, -r, r * 2, r * 2);
        }
        using (var core = new SolidBrush(Fade(Color.White, alpha, 0.9f)))
            g.FillRectangle(core, -2.5f, -2.5f, 5f, 5f);
        g.Restore(state);
    }

    public override void DrawPanel(Graphics g, RectangleF box, float alpha, bool strong)
    {
        using var back = new SolidBrush(Fade(Color.FromArgb(strong ? 220 : 206, 6, 16, 22), alpha));
        using var edge = new Pen(Fade(Accent, alpha, strong ? 0.9f : 0.7f), strong ? 1.5f : 1.1f);
        using var path = CutRect(box, strong ? 9f : 6f);
        g.FillPath(back, path);
        g.DrawPath(edge, path);

        // 左端の見出しバー
        using var bar = new SolidBrush(Fade(Theme, alpha, strong ? 0.95f : 0.7f));
        g.FillRectangle(bar, box.X, box.Y + 3f, 2.5f, box.Height - 6f);
    }

    /// <summary>左上と右下の角を落とした矩形。</summary>
    private static GraphicsPath CutRect(RectangleF r, float cut)
    {
        var path = new GraphicsPath();
        path.AddLines(new[]
        {
            new PointF(r.X + cut, r.Y),
            new PointF(r.Right, r.Y),
            new PointF(r.Right, r.Bottom - cut),
            new PointF(r.Right - cut, r.Bottom),
            new PointF(r.X, r.Bottom),
            new PointF(r.X, r.Y + cut),
        });
        path.CloseFigure();
        return path;
    }
}
