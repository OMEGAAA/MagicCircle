namespace MagicCircle;

/// <summary>
/// マウス軌跡を 8 方向のストロークコード（例: "↓→"）へ変換する。
/// </summary>
internal static class GestureRecognizer
{
    /// <summary>方向インデックス 0..7 に対応する記号。0=→ から反時計回り。</summary>
    private static readonly char[] Arrows = { '→', '↗', '↑', '↖', '←', '↙', '↓', '↘' };

    /// <summary>設定ファイル側の表記ゆれを正規化するための対応表。</summary>
    private static readonly Dictionary<char, char> Aliases = new()
    {
        ['→'] = '→', ['6'] = '→', ['r'] = '→', ['R'] = '→',
        ['↗'] = '↗', ['9'] = '↗',
        ['↑'] = '↑', ['8'] = '↑', ['u'] = '↑', ['U'] = '↑',
        ['↖'] = '↖', ['7'] = '↖',
        ['←'] = '←', ['4'] = '←', ['l'] = '←', ['L'] = '←',
        ['↙'] = '↙', ['1'] = '↙',
        ['↓'] = '↓', ['2'] = '↓', ['d'] = '↓', ['D'] = '↓',
        ['↘'] = '↘', ['3'] = '↘',
    };

    /// <summary>設定ファイルに書かれたジェスチャー文字列を矢印表記へ正規化する。</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (Aliases.TryGetValue(ch, out var arrow)) sb.Append(arrow);
            // 空白・区切り文字などは無視する
        }
        return sb.ToString();
    }

    /// <summary>軌跡の総移動距離（ピクセル）。</summary>
    public static double PathLength(IReadOnlyList<Point> points)
    {
        double total = 0;
        for (int i = 1; i < points.Count; i++)
        {
            double dx = points[i].X - points[i - 1].X;
            double dy = points[i].Y - points[i - 1].Y;
            total += Math.Sqrt(dx * dx + dy * dy);
        }
        return total;
    }

    /// <summary>
    /// 軌跡をストロークコードへ変換する。
    /// <paramref name="sampleDistance"/> ごとに方向を判定し、
    /// 連続する同方向をまとめたうえで <paramref name="minSegmentLength"/> 未満の区間を雑音として捨てる。
    /// </summary>
    /// <param name="fallbackToLongest">
    /// すべての区間が短かったときに、最長の区間を 1 つだけ採用するか。
    /// 発動時は true（わずかな動きでも意図を汲む）。
    /// コマンドヘルプでは false にして、まだ確定していない方向を含めない。
    /// </param>
    public static string Encode(IReadOnlyList<Point> points, double sampleDistance, double minSegmentLength,
                                bool fallbackToLongest = true)
    {
        if (points.Count < 2) return string.Empty;

        var segments = new List<(int Dir, double Length)>();
        var anchor = points[0];

        foreach (var pt in points)
        {
            double dx = pt.X - anchor.X;
            double dy = pt.Y - anchor.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < sampleDistance) continue;

            segments.Add((DirectionOf(dx, dy), dist));
            anchor = pt;
        }

        // 末尾の切り捨て分を最後の区間として加える（短いストロークを取りこぼさないため）
        var tail = points[^1];
        double tdx = tail.X - anchor.X, tdy = tail.Y - anchor.Y;
        double tailLen = Math.Sqrt(tdx * tdx + tdy * tdy);
        if (segments.Count == 0 && tailLen > 0)
        {
            segments.Add((DirectionOf(tdx, tdy), tailLen));
        }
        else if (tailLen > 0 && segments.Count > 0)
        {
            int tailDir = DirectionOf(tdx, tdy);
            if (tailDir == segments[^1].Dir) segments[^1] = (tailDir, segments[^1].Length + tailLen);
        }

        var merged = Merge(segments);

        // 雑音除去 →（区間が消えて隣接同士がつながる場合があるので）再マージ
        var filtered = merged.Where(s => s.Length >= minSegmentLength).ToList();
        if (fallbackToLongest && filtered.Count == 0 && merged.Count > 0)
        {
            // すべて短い場合は最長の区間だけを採用する
            filtered.Add(merged.OrderByDescending(s => s.Length).First());
        }
        merged = Merge(filtered);

        return string.Concat(merged.Select(s => Arrows[s.Dir]));
    }

    private static List<(int Dir, double Length)> Merge(List<(int Dir, double Length)> segments)
    {
        var result = new List<(int Dir, double Length)>();
        foreach (var seg in segments)
        {
            if (result.Count > 0 && result[^1].Dir == seg.Dir)
                result[^1] = (seg.Dir, result[^1].Length + seg.Length);
            else
                result.Add(seg);
        }
        return result;
    }

    /// <summary>画面座標の移動量を 8 方向（45 度刻み）へ量子化する。</summary>
    private static int DirectionOf(double dx, double dy)
    {
        // 画面座標は下向きが +Y なので、数学的な角度に直すため dy を反転する
        double angle = Math.Atan2(-dy, dx) * 180.0 / Math.PI; // -180..180
        int index = (int)Math.Round(angle / 45.0);
        return ((index % 8) + 8) % 8;
    }
}
