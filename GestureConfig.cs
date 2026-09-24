using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace MagicCircle;

internal sealed class GestureEntry
{
    /// <summary>矢印表記のストロークコード。例: "↓→"（"26" のようなテンキー表記も可）。</summary>
    [JsonPropertyName("gesture")] public string Gesture { get; set; } = "";

    /// <summary>画面に表示する名前。</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>送出するキー。例: "ctrl+shift+t"。"|" 区切りで連続送出。</summary>
    [JsonPropertyName("keys")] public string Keys { get; set; } = "";
}

internal sealed class GestureSettings
{
    /// <summary>方向を判定する間隔（px）。小さいほど細かい曲がりを拾う。</summary>
    [JsonPropertyName("segmentSampleDistance")] public double SegmentSampleDistance { get; set; } = 26;

    /// <summary>これより短い区間は雑音として捨てる（px）。</summary>
    [JsonPropertyName("minSegmentLength")] public double MinSegmentLength { get; set; } = 48;

    /// <summary>これ未満の移動量なら通常の右クリックとして扱う（px）。</summary>
    [JsonPropertyName("minGestureDistance")] public double MinGestureDistance { get; set; } = 32;

    /// <summary>オーバーレイの見た目。"arcane"（魔法陣）／"minimal"（ミニマル）／"hud"（HUD）。</summary>
    [JsonPropertyName("designStyle")] public string DesignStyle { get; set; } = "arcane";

    /// <summary>魔法陣・軌跡の主色。</summary>
    [JsonPropertyName("themeColor")] public string ThemeColor { get; set; } = "#8A6BFF";

    /// <summary>軌跡の芯やハイライトに使う色。</summary>
    [JsonPropertyName("accentColor")] public string AccentColor { get; set; } = "#4DE1FF";

    /// <summary>オーバーレイ描画を行うか。false にすると軌跡も魔法陣も描かない。</summary>
    [JsonPropertyName("showOverlay")] public bool ShowOverlay { get; set; } = true;

    /// <summary>なぞる手を止めたときに、次の方向のコマンド名を周囲へ表示するか。</summary>
    [JsonPropertyName("showCommandHelp")] public bool ShowCommandHelp { get; set; } = true;

    /// <summary>コマンドヘルプを出すまでの静止時間（ミリ秒）。0 にすると描き始めた直後から出る。</summary>
    [JsonPropertyName("commandHelpDelayMs")] public int CommandHelpDelayMs { get; set; } = 400;
}

internal sealed class GestureConfig
{
    [JsonPropertyName("settings")] public GestureSettings Settings { get; set; } = new();
    [JsonPropertyName("gestures")] public List<GestureEntry> Gestures { get; set; } = new();

    /// <summary>正規化済みストロークコード → 定義。</summary>
    [JsonIgnore] public Dictionary<string, GestureEntry> Map { get; private set; } = new();

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 既定のエンコーダは "+" などを + に逃がしてしまい手編集しづらいので、
        // ローカル設定ファイル向けに最小限のエスケープにする（日本語もそのまま出る）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "gestures.json");

    /// <summary>設定を読み込む。存在しなければ既定値で作成する。</summary>
    public static GestureConfig Load(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(ConfigPath))
            {
                var created = CreateDefault();
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(created, WriteOptions));
                created.BuildMap();
                return created;
            }

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<GestureConfig>(json, ReadOptions) ?? CreateDefault();
            config.Settings ??= new GestureSettings();
            config.Gestures ??= new List<GestureEntry>();
            config.BuildMap();
            return config;
        }
        catch (Exception ex)
        {
            error = $"{Path.GetFileName(ConfigPath)} を読み込めませんでした: {ex.Message}\n既定のジェスチャーで動作します。";
            var fallback = CreateDefault();
            fallback.BuildMap();
            return fallback;
        }
    }

    /// <summary>現在の内容を gestures.json へ書き出す。コメントは失われる。</summary>
    public bool Save(out string? error)
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, WriteOptions));
            BuildMap();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{Path.GetFileName(ConfigPath)} を保存できませんでした: {ex.Message}";
            return false;
        }
    }

    private void BuildMap()
    {
        Map = new Dictionary<string, GestureEntry>();
        foreach (var entry in Gestures)
        {
            var key = GestureRecognizer.Normalize(entry.Gesture);
            if (key.Length == 0) continue;
            Map[key] = entry; // 重複定義は後勝ち
        }
    }

    public GestureEntry? Match(string code) =>
        code.Length > 0 && Map.TryGetValue(code, out var entry) ? entry : null;

    public static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }

    public static GestureConfig CreateDefault() => new()
    {
        Settings = new GestureSettings(),
        Gestures = new List<GestureEntry>
        {
            new() { Gesture = "←",   Name = "戻る",             Keys = "alt+left" },
            new() { Gesture = "→",   Name = "進む",             Keys = "alt+right" },
            new() { Gesture = "↑",   Name = "ウィンドウ最大化", Keys = "win+up" },
            new() { Gesture = "↓",   Name = "ウィンドウ最小化", Keys = "win+down" },
            new() { Gesture = "↓→", Name = "タブを閉じる",     Keys = "ctrl+w" },
            new() { Gesture = "↑→", Name = "新しいタブ",       Keys = "ctrl+t" },
            new() { Gesture = "↑←", Name = "閉じたタブを復元", Keys = "ctrl+shift+t" },
            new() { Gesture = "↓↑", Name = "再読み込み",       Keys = "f5" },
            new() { Gesture = "↑↓", Name = "貼り付け",         Keys = "ctrl+v" },
            new() { Gesture = "↓←", Name = "コピー",           Keys = "ctrl+c" },
            new() { Gesture = "←→", Name = "タスクビュー",     Keys = "win+tab" },
            new() { Gesture = "→←", Name = "デスクトップ表示", Keys = "win+d" },
            new() { Gesture = "↖",   Name = "範囲スクリーンショット", Keys = "win+shift+s" },
            new() { Gesture = "↗",   Name = "検索",             Keys = "win+s" },
            new() { Gesture = "↘",   Name = "元に戻す",         Keys = "ctrl+z" },
            new() { Gesture = "→↓←↑", Name = "設定を開く",     Keys = "win+i" },
        }
    };
}
