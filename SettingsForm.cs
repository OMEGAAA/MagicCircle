namespace MagicCircle;

/// <summary>
/// ジェスチャーとキーの割り当てを GUI で編集する。
/// ジェスチャーは実際に右ドラッグして取り込み、キーは実際に押して取り込む。
/// 変更は「保存」を押すまで gestures.json に書き込まない。
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly GestureConfig _config;
    private readonly GestureEngine _engine;
    private readonly Action _afterSave;

    /// <summary>編集用の作業コピー。キャンセルすると捨てられる。</summary>
    private readonly List<GestureEntry> _items;

    private readonly ListView _list = new();
    private readonly TextBox _gestureBox = new();
    private readonly TextBox _nameBox = new();
    private readonly TextBox _keysBox = new();
    private readonly Button _drawButton = new();
    private readonly Button _captureButton = new();
    private readonly Label _status = new();

    private KeyboardCapture? _capture;
    private bool _suppressSelectionEvent;

    public SettingsForm(GestureConfig config, GestureEngine engine, Action afterSave)
    {
        _config = config;
        _engine = engine;
        _afterSave = afterSave;
        _items = config.Gestures
            .Select(g => new GestureEntry { Gesture = g.Gesture, Name = g.Name, Keys = g.Keys })
            .ToList();

        BuildUi();
        RefreshList();
    }

    private void BuildUi()
    {
        Text = "魔法陣 — ジェスチャー設定";
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(700, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = TrayContext.LoadAppIcon();

        // ---- 一覧 ----
        _list.Name = "list";
        _list.Bounds = new Rectangle(12, 12, 736, 250);
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.Columns.Add("ジェスチャー", 150);
        _list.Columns.Add("コマンド名", 300);
        _list.Columns.Add("キー", 270);
        _list.SelectedIndexChanged += (_, _) => LoadSelectionIntoEditor();
        Controls.Add(_list);

        // ---- 編集欄 ----
        var box = new GroupBox
        {
            Text = "編集",
            Bounds = new Rectangle(12, 272, 736, 200),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        Controls.Add(box);

        box.Controls.Add(new Label { Text = "ジェスチャー", Bounds = new Rectangle(12, 28, 80, 20) });
        _gestureBox.Name = "gestureBox";
        _gestureBox.Bounds = new Rectangle(96, 24, 190, 30);
        _gestureBox.ReadOnly = true;
        _gestureBox.Font = new Font(Font.FontFamily, 14f);
        box.Controls.Add(_gestureBox);

        _drawButton.Text = "描いて登録";
        _drawButton.Bounds = new Rectangle(294, 24, 100, 30);
        _drawButton.Click += (_, _) => ToggleRecording();
        box.Controls.Add(_drawButton);

        var undo = new Button { Text = "1つ消す", Bounds = new Rectangle(400, 24, 80, 30) };
        undo.Click += (_, _) =>
        {
            if (_gestureBox.Text.Length > 0)
                _gestureBox.Text = _gestureBox.Text[..^1];
        };
        box.Controls.Add(undo);

        var clearGesture = new Button { Text = "クリア", Bounds = new Rectangle(486, 24, 70, 30) };
        clearGesture.Click += (_, _) => _gestureBox.Text = "";
        box.Controls.Add(clearGesture);

        // 手入力用の方向ボタン
        int x = 96;
        foreach (var arrow in "↖↑↗←→↙↓↘")
        {
            var button = new Button
            {
                Text = arrow.ToString(),
                Bounds = new Rectangle(x, 60, 34, 28),
                Font = new Font(Font.FontFamily, 11f),
                Tag = arrow,
            };
            button.Click += (s, _) => _gestureBox.Text += ((Button)s!).Tag;
            box.Controls.Add(button);
            x += 38;
        }

        box.Controls.Add(new Label { Text = "コマンド名", Bounds = new Rectangle(12, 104, 80, 20) });
        _nameBox.Name = "nameBox";
        _nameBox.Bounds = new Rectangle(96, 100, 270, 26);
        box.Controls.Add(_nameBox);

        box.Controls.Add(new Label { Text = "キー", Bounds = new Rectangle(12, 140, 80, 20) });
        _keysBox.Name = "keysBox";
        _keysBox.Bounds = new Rectangle(96, 136, 190, 26);
        _keysBox.ReadOnly = true;
        box.Controls.Add(_keysBox);

        _captureButton.Text = "キーを押して登録";
        _captureButton.Bounds = new Rectangle(294, 136, 130, 26);
        _captureButton.Click += (_, _) => ToggleKeyCapture();
        box.Controls.Add(_captureButton);

        var clearKeys = new Button { Text = "クリア", Bounds = new Rectangle(430, 136, 70, 26) };
        clearKeys.Click += (_, _) => _keysBox.Text = "";
        box.Controls.Add(clearKeys);

        var add = new Button { Text = "新規追加", Bounds = new Rectangle(580, 24, 140, 30) };
        add.Click += (_, _) => AddEntry();
        box.Controls.Add(add);

        var update = new Button { Text = "選択行を更新", Bounds = new Rectangle(580, 60, 140, 30) };
        update.Click += (_, _) => UpdateEntry();
        box.Controls.Add(update);

        var delete = new Button { Text = "選択行を削除", Bounds = new Rectangle(580, 96, 140, 30) };
        delete.Click += (_, _) => DeleteEntry();
        box.Controls.Add(delete);

        // ---- 下段 ----
        _status.Bounds = new Rectangle(12, 484, 560, 40);
        _status.Anchor = AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right;
        _status.Text = "行を選ぶと編集できます。ジェスチャーは「描いて登録」で実際になぞって取り込めます。";
        Controls.Add(_status);

        var save = new Button { Text = "保存", Bounds = new Rectangle(578, 484, 80, 32) };
        save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        save.Click += (_, _) => SaveAndClose();
        Controls.Add(save);

        var cancel = new Button { Text = "キャンセル", Bounds = new Rectangle(666, 484, 82, 32) };
        cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        cancel.Click += (_, _) => Close();
        Controls.Add(cancel);

        CancelButton = cancel;
    }

    // ---------------- 一覧と編集欄 ----------------

    private void RefreshList(int selectIndex = -1)
    {
        _suppressSelectionEvent = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var item in _items)
            _list.Items.Add(new ListViewItem(new[] { item.Gesture, item.Name, item.Keys }));
        _list.EndUpdate();
        _suppressSelectionEvent = false;

        if (selectIndex >= 0 && selectIndex < _list.Items.Count)
        {
            _list.Items[selectIndex].Selected = true;
            _list.Items[selectIndex].EnsureVisible();
        }
    }

    private int SelectedIndex => _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;

    private void LoadSelectionIntoEditor()
    {
        if (_suppressSelectionEvent) return;
        int index = SelectedIndex;
        if (index < 0) return;

        _gestureBox.Text = _items[index].Gesture;
        _nameBox.Text = _items[index].Name;
        _keysBox.Text = _items[index].Keys;
    }

    /// <summary>編集欄の内容を検証して 1 件分にまとめる。</summary>
    private GestureEntry? ReadEditor(int ignoreIndex)
    {
        var gesture = GestureRecognizer.Normalize(_gestureBox.Text);
        if (gesture.Length == 0)
        {
            ShowStatus("ジェスチャーが空です。「描いて登録」か矢印ボタンで入力してください。", true);
            return null;
        }

        for (int i = 0; i < _items.Count; i++)
        {
            if (i == ignoreIndex) continue;
            if (GestureRecognizer.Normalize(_items[i].Gesture) == gesture)
            {
                ShowStatus($"ジェスチャー {gesture} は「{_items[i].Name}」で既に使われています。", true);
                return null;
            }
        }

        if (_nameBox.Text.Trim().Length == 0)
        {
            ShowStatus("コマンド名を入力してください。画面に表示される名前になります。", true);
            return null;
        }

        if (!KeySender.Validate(_keysBox.Text, out var error))
        {
            ShowStatus($"キーが正しくありません: {error}", true);
            return null;
        }

        return new GestureEntry
        {
            Gesture = gesture,
            Name = _nameBox.Text.Trim(),
            Keys = _keysBox.Text.Trim(),
        };
    }

    private void AddEntry()
    {
        var entry = ReadEditor(-1);
        if (entry is null) return;

        _items.Add(entry);
        RefreshList(_items.Count - 1);
        ShowStatus($"{entry.Gesture} 「{entry.Name}」を追加しました。保存するまでファイルには書き込まれません。", false);
    }

    private void UpdateEntry()
    {
        int index = SelectedIndex;
        if (index < 0) { ShowStatus("更新する行を選んでください。", true); return; }

        var entry = ReadEditor(index);
        if (entry is null) return;

        _items[index] = entry;
        RefreshList(index);
        ShowStatus($"{entry.Gesture} 「{entry.Name}」を更新しました。", false);
    }

    private void DeleteEntry()
    {
        int index = SelectedIndex;
        if (index < 0) { ShowStatus("削除する行を選んでください。", true); return; }

        var removed = _items[index];
        _items.RemoveAt(index);
        RefreshList(Math.Min(index, _items.Count - 1));
        ShowStatus($"{removed.Gesture} 「{removed.Name}」を削除しました。", false);
    }

    private void SaveAndClose()
    {
        _config.Gestures = _items;
        if (!_config.Save(out var error))
        {
            MessageBox.Show(error, "魔法陣", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _afterSave();
        Close();
    }

    private void ShowStatus(string message, bool isError)
    {
        _status.ForeColor = isError ? Color.Firebrick : SystemColors.ControlText;
        _status.Text = message;
    }

    // ---------------- 取り込み ----------------

    private void ToggleRecording()
    {
        if (_engine.IsRecording)
        {
            _engine.CancelRecording();
            return;
        }

        _drawButton.Text = "なぞって…";
        ShowStatus("画面のどこでも構いません。右ボタンを押しながらなぞってください（左クリックで中止）。", false);

        _engine.BeginRecording(code =>
        {
            _drawButton.Text = "描いて登録";
            if (string.IsNullOrEmpty(code))
            {
                ShowStatus("取り込みを中止しました。", false);
                return;
            }

            _gestureBox.Text = code;
            var used = _items.FirstOrDefault(i => GestureRecognizer.Normalize(i.Gesture) == code);
            ShowStatus(used is null
                ? $"{code} を取り込みました。コマンド名とキーを入れて「新規追加」を押してください。"
                : $"{code} を取り込みました（現在は「{used.Name}」に割り当て済み）。", false);
            Activate();
        });
    }

    private void ToggleKeyCapture()
    {
        if (_capture is { IsActive: true })
        {
            _capture.Stop();
            _captureButton.Text = "キーを押して登録";
            ShowStatus("キーの取り込みを中止しました。", false);
            return;
        }

        _capture = new KeyboardCapture(this, spec =>
        {
            _captureButton.Text = "キーを押して登録";
            if (spec is null)
            {
                ShowStatus("キーの取り込みを中止しました。", false);
                return;
            }

            _keysBox.Text = spec;
            ShowStatus($"キー {spec} を取り込みました。", false);
        });

        try
        {
            _capture.Start();
            _captureButton.Text = "入力待ち…";
            ShowStatus("使いたいキーの組み合わせを押してください（Esc で中止）。押している間 OS 側のショートカットは発動しません。", false);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, true);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_engine.IsRecording) _engine.CancelRecording();
        _capture?.Dispose();
        base.OnFormClosed(e);
    }
}
