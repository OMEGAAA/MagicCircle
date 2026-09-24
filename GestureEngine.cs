namespace MagicCircle;

/// <summary>
/// 右ドラッグの軌跡を集めて、離した瞬間にストロークコードへ変換し、対応するキーを送出する。
/// </summary>
internal sealed class GestureEngine : IGestureSink
{
    /// <summary>軌跡として記録する最小移動量（px）。細かすぎる点を間引く。</summary>
    private const double PointSpacing = 3.0;

    /// <summary>オーバーレイを出し始める移動量（px）。ただの右クリックで魔法陣が光らないようにする。</summary>
    private const double OverlayThreshold = 12.0;

    /// <summary>方向記号の並び（GestureRecognizer と同じ順序）。</summary>
    private const string Directions = "→↗↑↖←↙↓↘";

    private readonly OverlayWindow _overlay;
    private readonly List<Point> _points = new();

    private GestureConfig _config;
    private bool _capturing;
    private bool _overlayShown;
    private bool _swallowLeftUp;
    private bool _swallowMiddleUp;
    private double _pathLength;
    private Point _origin;
    private Point _last;

    public GestureEngine(GestureConfig config, OverlayWindow overlay)
    {
        _config = config;
        _overlay = overlay;
    }

    public bool Enabled { get; set; } = true;

    /// <summary>設定画面が「描いて登録」を待っている間だけ入る。null なら通常動作。</summary>
    private Action<string?>? _recordCallback;

    public bool IsRecording => _recordCallback is not null;

    /// <summary>
    /// 次に描かれたジェスチャーをショートカットとして実行せず、コードだけ返す。
    /// 中止された場合は null が渡る。
    /// </summary>
    public void BeginRecording(Action<string?> onDone) => _recordCallback = onDone;

    public void CancelRecording()
    {
        var callback = _recordCallback;
        _recordCallback = null;
        callback?.Invoke(null);
    }

    /// <summary>直近に発動したジェスチャーの説明（トレイの通知などに使う）。</summary>
    public string? LastResult { get; private set; }

    public void UpdateConfig(GestureConfig config)
    {
        _config = config;
        _overlay.ApplyTheme(
            GestureConfig.ParseColor(config.Settings.ThemeColor, Color.FromArgb(0x8A, 0x6B, 0xFF)),
            GestureConfig.ParseColor(config.Settings.AccentColor, Color.FromArgb(0x4D, 0xE1, 0xFF)),
            OverlayStyle.ParseKind(config.Settings.DesignStyle));
        _overlay.HelpEnabled = config.Settings.ShowCommandHelp;
        _overlay.HelpDelayMs = Math.Max(0, config.Settings.CommandHelpDelayMs);
    }

    /// <summary>
    /// 今の軌跡 <paramref name="code"/> に続けられる方向を集めて、周囲に出すヘルプを組み立てる。
    /// その方向でちょうど確定するものがあればその名前を、さらに続きがあれば件数を添える。
    /// </summary>
    private void UpdateHelp(string code)
    {
        var items = new List<HelpItem>(8);

        foreach (var direction in Directions)
        {
            string prefix = code + direction;
            GestureEntry? exact = null;
            GestureEntry? onlyOne = null;
            int count = 0;

            foreach (var (key, entry) in _config.Map)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                count++;
                onlyOne = entry;
                if (key.Length == prefix.Length) exact = entry;
            }

            if (count == 0) continue;

            string label = exact is not null
                ? (count > 1 ? $"{exact.Name} ＋{count - 1}" : exact.Name)
                : (count == 1 ? $"…{onlyOne!.Name}" : $"…＋{count}");

            items.Add(new HelpItem(direction, label));
        }

        _overlay.SetHelp(items);
    }

    public bool OnRightDown(Point screenPoint)
    {
        // 無効化中でも「描いて登録」の待ち受け中は取り込む
        if (!Enabled && !IsRecording) return false;

        _capturing = true;
        _overlayShown = false;
        _pathLength = 0;
        _points.Clear();
        _points.Add(screenPoint);
        _origin = screenPoint;
        _last = screenPoint;
        return true; // 右ボタン押下は握り潰し、ジェスチャーでなければ離した時に流し直す
    }

    public bool OnMouseMove(Point screenPoint)
    {
        if (!_capturing) return false;

        double dx = screenPoint.X - _last.X;
        double dy = screenPoint.Y - _last.Y;
        double step = Math.Sqrt(dx * dx + dy * dy);
        if (step < PointSpacing) return false;

        _pathLength += step;
        _points.Add(screenPoint);
        _last = screenPoint;

        if (_config.Settings.ShowOverlay)
        {
            if (!_overlayShown && _pathLength >= OverlayThreshold)
            {
                _overlay.BeginStroke(_origin);
                foreach (var p in _points) _overlay.AddPoint(p);
                _overlayShown = true;
            }
            else if (_overlayShown)
            {
                _overlay.AddPoint(screenPoint);
            }

            if (_overlayShown)
            {
                var code = Encode();
                _overlay.SetCandidate(code, _config.Match(code)?.Name);
                // ヘルプは確定済みのストロークだけを前提にする（描きかけの方向で候補を絞らない）
                UpdateHelp(Encode(fallbackToLongest: false));
            }
        }

        return false; // カーソル自体は普通に動かす
    }

    public bool OnRightUp(Point screenPoint)
    {
        if (!_capturing) return false;
        _capturing = false;

        if (_pathLength > 0)
        {
            _points.Add(screenPoint);
            _last = screenPoint;
        }

        // 動いていなければ、握り潰した右クリックを本来のアプリへ流し直す
        // （ただし「描いて登録」の待ち受け中は、設定画面でメニューが開かないよう握り潰したままにする）
        if (_pathLength < _config.Settings.MinGestureDistance)
        {
            _overlay.CancelStroke();
            if (!IsRecording) MouseHook.ReplayRightClick();
            return true;
        }

        var code = Encode();

        if (_recordCallback is not null)
        {
            var callback = _recordCallback;
            _recordCallback = null;
            LastResult = $"{code} → 設定画面へ登録";
            if (_overlayShown) _overlay.Finish(code, "設定画面へ取り込み", true);
            Post(() => callback(code));
            return true;
        }

        var entry = _config.Match(code);

        if (entry is null)
        {
            LastResult = $"{code} → 未登録";
            if (_overlayShown) _overlay.Finish(code, null, false);
            return true;
        }

        if (_overlayShown) _overlay.Finish(code, entry.Name, true);

        if (!KeySender.Send(entry.Keys, out var error))
        {
            LastResult = $"{code} → {entry.Name}: {error}";
            ShowError($"「{entry.Name}」のキー指定を実行できませんでした。\n{error}");
        }
        else
        {
            LastResult = $"{code} → {entry.Name} ({entry.Keys})";
        }

        return true;
    }

    /// <summary>
    /// 右ドラッグ中の左／中クリックはジェスチャーの中止に使う。
    /// このクリックは中止操作なので、下のアプリには渡さない（押下と解放の両方を握り潰す）。
    /// </summary>
    public bool OnCancelButtonDown(bool isLeft)
    {
        if (!_capturing) return false;

        _capturing = false;
        _overlay.CancelStroke();

        if (_recordCallback is not null)
        {
            var callback = _recordCallback;
            _recordCallback = null;
            Post(() => callback(null));
        }

        if (isLeft) _swallowLeftUp = true;
        else _swallowMiddleUp = true;
        return true;
    }

    public bool OnCancelButtonUp(bool isLeft)
    {
        if (isLeft)
        {
            if (!_swallowLeftUp) return false;
            _swallowLeftUp = false;
        }
        else
        {
            if (!_swallowMiddleUp) return false;
            _swallowMiddleUp = false;
        }
        return true;
    }

    private string Encode(bool fallbackToLongest = true) => GestureRecognizer.Encode(
        _points,
        Math.Max(4, _config.Settings.SegmentSampleDistance),
        Math.Max(4, _config.Settings.MinSegmentLength),
        fallbackToLongest);

    /// <summary>
    /// フックのコールバック内で重い処理やモーダルダイアログを行うとフック自体が外されるため、
    /// メッセージループへ投げてから実行する。
    /// </summary>
    private void Post(Action action)
    {
        try
        {
            _overlay.BeginInvoke(action);
        }
        catch
        {
            // ウィンドウが未作成などで投げられない場合は握り潰す（LastResult には残る）
        }
    }

    private void ShowError(string message) =>
        Post(() => MessageBox.Show(message, "魔法陣", MessageBoxButtons.OK, MessageBoxIcon.Warning));
}
