using System.Diagnostics;

namespace MagicCircle;

/// <summary>タスクトレイに常駐し、フックとオーバーレイの寿命を管理する。</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly OverlayWindow _overlay;
    private readonly GestureEngine _engine;
    private readonly MouseHook _hook;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly Dictionary<OverlayStyleKind, ToolStripMenuItem> _designItems = new();
    private Icon? _icon;
    private GestureConfig _config;
    private SettingsForm? _settingsForm;

    public TrayContext()
    {
        var config = GestureConfig.Load(out var loadError);
        _config = config;

        _overlay = new OverlayWindow();
        _ = _overlay.Handle; // メッセージ投函先としてハンドルを先に作っておく

        _engine = new GestureEngine(config, _overlay);
        _engine.UpdateConfig(config);

        _enabledItem = new ToolStripMenuItem("魔法陣を有効にする")
        {
            CheckOnClick = true,
            Checked = true,
        };
        _enabledItem.CheckedChanged += (_, _) => _engine.Enabled = _enabledItem.Checked;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildDesignMenu());
        SyncDesignMenu();
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("ジェスチャー設定を開く…", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("gestures.json を直接開く", null, (_, _) => OpenConfigFile());
        menu.Items.Add("設定を再読み込み", null, (_, _) => ReloadConfig());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApp());

        _icon = LoadAppIcon(SystemInformation.SmallIconSize);
        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "魔法陣 — 右ドラッグでジェスチャー",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();

        _hook = new MouseHook(_engine);
        try
        {
            _hook.Install();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "魔法陣", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitApp();
            return;
        }

        if (loadError is not null)
            MessageBox.Show(loadError, "魔法陣", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        _tray.ShowBalloonTip(4000, "魔法陣を起動しました",
            $"右クリックを押しながら画面をなぞってください。({config.Gestures.Count} 個のジェスチャーを読み込み)",
            ToolTipIcon.Info);
    }

    // ---------------- デザイン切り替え ----------------

    /// <summary>オーバーレイの見た目を選ぶサブメニューを作る。</summary>
    private ToolStripMenuItem BuildDesignMenu()
    {
        var root = new ToolStripMenuItem("デザイン");
        foreach (var kind in OverlayStyle.All)
        {
            var selected = kind;
            var item = new ToolStripMenuItem(OverlayStyle.NameOf(kind));
            item.Click += (_, _) => ApplyDesign(selected);
            _designItems[kind] = item;
            root.DropDownItems.Add(item);
        }
        return root;
    }

    /// <summary>選んだ見た目を即座に反映し、gestures.json にも残す。</summary>
    private void ApplyDesign(OverlayStyleKind kind)
    {
        _config.Settings.DesignStyle = OverlayStyle.ToId(kind);
        _engine.UpdateConfig(_config); // 次に描くジェスチャーから新しい見た目になる
        SyncDesignMenu();

        // 保存できなくても、この起動中は選んだ見た目のまま動かす
        if (!_config.Save(out var error))
            MessageBox.Show(error, "魔法陣", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void SyncDesignMenu()
    {
        var current = OverlayStyle.ParseKind(_config.Settings.DesignStyle);
        foreach (var (kind, item) in _designItems)
            item.Checked = kind == current;
    }

    /// <summary>設定画面を開く。既に開いていれば前面に出すだけ。</summary>
    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            if (_settingsForm.WindowState == FormWindowState.Minimized)
                _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_config, _engine, AfterSettingsSaved);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void AfterSettingsSaved()
    {
        _engine.UpdateConfig(_config);
        _tray.ShowBalloonTip(2500, "設定を保存しました",
            $"{_config.Gestures.Count} 個のジェスチャーが有効です。", ToolTipIcon.Info);
    }

    private void OpenConfigFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(GestureConfig.ConfigPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"設定ファイルを開けませんでした。\n{GestureConfig.ConfigPath}\n{ex.Message}",
                "魔法陣", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ReloadConfig()
    {
        var config = GestureConfig.Load(out var error);
        _config = config;
        _engine.UpdateConfig(config);
        SyncDesignMenu();
        if (error is not null)
        {
            MessageBox.Show(error, "魔法陣", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _tray.ShowBalloonTip(2500, "設定を再読み込みしました",
            $"{config.Gestures.Count} 個のジェスチャーが有効です。", ToolTipIcon.Info);
    }

    private void ExitApp()
    {
        _settingsForm?.Close();
        _hook?.Uninstall();
        if (_tray is not null) _tray.Visible = false;
        ExitThread();
    }

    /// <summary>
    /// 埋め込んだ magiccircle.ico を読み込む。サイズを渡すと、それに一番近いフレームが選ばれる。
    /// （16/20/24/32/48/64/128px を同梱しているので、高 DPI でもぼやけない）
    /// </summary>
    internal static Icon LoadAppIcon(Size? size = null)
    {
        var assembly = typeof(TrayContext).Assembly;
        var name = Array.Find(assembly.GetManifestResourceNames(),
            n => n.EndsWith("magiccircle.ico", StringComparison.OrdinalIgnoreCase));

        if (name is not null)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is not null)
                return size is null ? new Icon(stream) : new Icon(stream, size.Value);
        }

        return SystemIcons.Application; // 埋め込みに失敗しても常駐は続ける
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hook?.Dispose();
            _tray?.Dispose();
            _overlay?.Dispose();
            // SystemIcons の共有インスタンスは破棄してはいけない
            if (!ReferenceEquals(_icon, SystemIcons.Application)) _icon?.Dispose();
            _icon = null;
        }
        base.Dispose(disposing);
    }
}
