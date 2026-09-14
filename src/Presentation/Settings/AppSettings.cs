using Godot;
using Xiangqi.GameHost;
using Xiangqi.Core.Application;

namespace Xiangqi.Presentation.Settings;

/// <summary>
/// Owns user preferences and their Godot-side effects. Screens edit a detached copy and call
/// SaveAndApply only on confirmation, so navigation never leaves half-applied settings.
/// </summary>
public sealed class AppSettings
{
    private const string SettingsPath = "user://settings.cfg";

    public bool Fullscreen { get; set; }
    public bool VSync { get; set; } = true;
    public int MaxFps { get; set; } = 60;
    public bool EffectsEnabled { get; set; } = true;
    public bool ShowBoardCoordinates { get; set; } = true;
    private string _themeId = "classic";
    /// <summary>Stable package ID. Unknown values are retained until the catalog resolves them.</summary>
    public string ThemeId
    {
        get => _themeId;
        set => _themeId = string.IsNullOrWhiteSpace(value) ? "classic" : value.Trim();
    }
    /// <summary>One-release compatibility facade for the former integer board/skin setting.</summary>
    public BoardSkinKind BoardSkin
    {
        get => ThemeId.Equals("spring", StringComparison.OrdinalIgnoreCase)
            ? BoardSkinKind.Spring : BoardSkinKind.Classic;
        set => ThemeId = value == BoardSkinKind.Spring ? "spring" : "classic";
    }
    public float MasterVolume { get; set; } = 0.8f;
    public float MusicVolume { get; set; } = 0.7f;
    public float EffectsVolume { get; set; } = 0.85f;
    public string CustomPlayerId { get; set; } = string.Empty;
    public string CustomAvatarPath { get; set; } = string.Empty;
    public int AiSideChoice { get; set; } = 0;
    public int AiGameSeconds { get; set; } = 20 * 60;
    public int AiMoveSeconds { get; set; } = 60;
    public int AiThinkMilliseconds { get; set; } = 1500;
    public int AiDifficulty { get; set; } = (int)Xiangqi.Core.Application.AiDifficultyLevel.Medium;
    public int AiMaxDepth { get; set; }
    public int AiThreads { get; set; } = 1;
    public int AiHashMegabytes { get; set; } = 64;
    public int GomokuAiSideChoice { get; set; } = 0;
    public int GomokuAiBoardSize { get; set; } = 15;
    public bool GomokuAiForbiddenMoves { get; set; }
    public int GomokuAiThinkMilliseconds { get; set; } = 1500;
    public int GomokuAiDifficulty { get; set; } = (int)Xiangqi.Core.Application.AiDifficultyLevel.Medium;
    public int GomokuAiMaxDepth { get; set; }
    public int GomokuAiThreads { get; set; } = 1;
    public int GomokuAiMemoryMegabytes { get; set; } = 256;
    /// <summary>Shared wall-clock budget for one classic or Gomoku replay position. Search
    /// strength, depth policy and engine resources remain fixed by replay orchestration.</summary>
    public int ReplayAnalysisMilliseconds { get; set; } = AiDifficultyProfiles.ReplayThinkMilliseconds;
    /// <summary>
    /// Preferred gallery order by stable module ID. Missing modules are retained on disk by the
    /// catalog store but ignored while unavailable; newly discovered modules append deterministically.
    /// </summary>
    public string[] GameModuleOrder { get; set; } = [];
    public bool HasCustomProfile => !string.IsNullOrWhiteSpace(CustomPlayerId) ||
                                    !string.IsNullOrWhiteSpace(CustomAvatarPath);

    public static AppSettings Load()
    {
        var result = new AppSettings();
        var config = new ConfigFile();
        if (config.Load(SettingsPath) != Error.Ok) return result;

        result.Fullscreen = config.GetValue("display", "fullscreen", result.Fullscreen).AsBool();
        result.VSync = config.GetValue("display", "vsync", result.VSync).AsBool();
        result.MaxFps = config.GetValue("display", "max_fps", result.MaxFps).AsInt32();
        result.EffectsEnabled = config.GetValue("display", "effects", result.EffectsEnabled).AsBool();
        result.ShowBoardCoordinates = config.GetValue(
            "board", "show_coordinates", result.ShowBoardCoordinates).AsBool();
        var legacySkin = (BoardSkinKind)config.GetValue(
            "board", "skin", (int)BoardSkinKind.Classic).AsInt32();
        result.ThemeId = config.HasSectionKey("board", "theme_id")
            ? config.GetValue("board", "theme_id", "classic").AsString()
            : legacySkin == BoardSkinKind.Spring ? "spring" : "classic";
        result.MasterVolume = (float)config.GetValue("audio", "master", result.MasterVolume).AsDouble();
        result.MusicVolume = (float)config.GetValue("audio", "music", result.MusicVolume).AsDouble();
        result.EffectsVolume = (float)config.GetValue("audio", "effects", result.EffectsVolume).AsDouble();
        result.CustomPlayerId = config.GetValue("profile", "player_id", string.Empty).AsString();
        result.CustomAvatarPath = config.GetValue("profile", "avatar_path", string.Empty).AsString();
        result.AiSideChoice = config.GetValue("ai", "side", 0).AsInt32();
        result.AiGameSeconds = config.GetValue("ai", "game_seconds", 1200).AsInt32();
        result.AiMoveSeconds = config.GetValue("ai", "move_seconds", 60).AsInt32();
        result.AiThinkMilliseconds = config.GetValue("ai", "think_milliseconds", 1500).AsInt32();
        result.AiDifficulty = config.GetValue("ai", "difficulty", result.AiDifficulty).AsInt32();
        result.AiMaxDepth = config.GetValue("ai", "max_depth", 0).AsInt32();
        result.AiThreads = config.GetValue("ai", "threads", 1).AsInt32();
        result.AiHashMegabytes = config.GetValue("ai", "hash_megabytes", 64).AsInt32();
        result.GomokuAiSideChoice = config.GetValue("gomoku_ai", "side", 0).AsInt32();
        result.GomokuAiBoardSize = config.GetValue("gomoku_ai", "board_size", 15).AsInt32();
        result.GomokuAiForbiddenMoves = config.GetValue("gomoku_ai", "forbidden_moves", false).AsBool();
        result.GomokuAiThinkMilliseconds = config.GetValue("gomoku_ai", "think_milliseconds", 1500).AsInt32();
        result.GomokuAiDifficulty = config.GetValue("gomoku_ai", "difficulty", result.GomokuAiDifficulty).AsInt32();
        result.GomokuAiMaxDepth = config.GetValue("gomoku_ai", "max_depth", 0).AsInt32();
        result.GomokuAiThreads = config.GetValue("gomoku_ai", "threads", 1).AsInt32();
        result.GomokuAiMemoryMegabytes = config.GetValue("gomoku_ai", "memory_megabytes", 256).AsInt32();
        result.ReplayAnalysisMilliseconds = config.GetValue("replay_analysis", "think_milliseconds",
            AiDifficultyProfiles.ReplayThinkMilliseconds).AsInt32();
        result.GameModuleOrder = config.GetValue("gallery", "module_order", string.Empty).AsString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        result.Clamp();
        return result;
    }

    public AppSettings Copy()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.GameModuleOrder = [.. GameModuleOrder];
        return copy;
    }

    public void UpdateFrom(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Fullscreen = source.Fullscreen;
        VSync = source.VSync;
        MaxFps = source.MaxFps;
        EffectsEnabled = source.EffectsEnabled;
        ShowBoardCoordinates = source.ShowBoardCoordinates;
        ThemeId = source.ThemeId;
        MasterVolume = source.MasterVolume;
        MusicVolume = source.MusicVolume;
        EffectsVolume = source.EffectsVolume;
        CustomPlayerId = source.CustomPlayerId;
        CustomAvatarPath = source.CustomAvatarPath;
        AiSideChoice = source.AiSideChoice;
        AiGameSeconds = source.AiGameSeconds;
        AiMoveSeconds = source.AiMoveSeconds;
        AiThinkMilliseconds = source.AiThinkMilliseconds;
        AiDifficulty = source.AiDifficulty; AiMaxDepth = source.AiMaxDepth;
        AiThreads = source.AiThreads; AiHashMegabytes = source.AiHashMegabytes;
        GomokuAiSideChoice = source.GomokuAiSideChoice;
        GomokuAiBoardSize = source.GomokuAiBoardSize;
        GomokuAiForbiddenMoves = source.GomokuAiForbiddenMoves;
        GomokuAiThinkMilliseconds = source.GomokuAiThinkMilliseconds;
        GomokuAiDifficulty = source.GomokuAiDifficulty; GomokuAiMaxDepth = source.GomokuAiMaxDepth;
        GomokuAiThreads = source.GomokuAiThreads; GomokuAiMemoryMegabytes = source.GomokuAiMemoryMegabytes;
        ReplayAnalysisMilliseconds = source.ReplayAnalysisMilliseconds;
        GameModuleOrder = [.. source.GameModuleOrder];
    }

    public void SaveAndApply()
    {
        Clamp();
        var config = new ConfigFile();
        config.SetValue("display", "fullscreen", Fullscreen);
        config.SetValue("display", "vsync", VSync);
        config.SetValue("display", "max_fps", MaxFps);
        config.SetValue("display", "effects", EffectsEnabled);
        config.SetValue("board", "show_coordinates", ShowBoardCoordinates);
        config.SetValue("board", "theme_id", ThemeId);
        config.SetValue("board", "skin", (int)BoardSkin);
        config.SetValue("audio", "master", MasterVolume);
        config.SetValue("audio", "music", MusicVolume);
        config.SetValue("audio", "effects", EffectsVolume);
        config.SetValue("profile", "player_id", CustomPlayerId);
        config.SetValue("profile", "avatar_path", CustomAvatarPath);
        config.SetValue("ai", "side", AiSideChoice);
        config.SetValue("ai", "game_seconds", AiGameSeconds);
        config.SetValue("ai", "move_seconds", AiMoveSeconds);
        config.SetValue("ai", "think_milliseconds", AiThinkMilliseconds);
        config.SetValue("ai", "difficulty", AiDifficulty);
        config.SetValue("ai", "max_depth", AiMaxDepth);
        config.SetValue("ai", "threads", AiThreads);
        config.SetValue("ai", "hash_megabytes", AiHashMegabytes);
        config.SetValue("gomoku_ai", "side", GomokuAiSideChoice);
        config.SetValue("gomoku_ai", "board_size", GomokuAiBoardSize);
        config.SetValue("gomoku_ai", "forbidden_moves", GomokuAiForbiddenMoves);
        config.SetValue("gomoku_ai", "think_milliseconds", GomokuAiThinkMilliseconds);
        config.SetValue("gomoku_ai", "difficulty", GomokuAiDifficulty);
        config.SetValue("gomoku_ai", "max_depth", GomokuAiMaxDepth);
        config.SetValue("gomoku_ai", "threads", GomokuAiThreads);
        config.SetValue("gomoku_ai", "memory_megabytes", GomokuAiMemoryMegabytes);
        config.SetValue("replay_analysis", "think_milliseconds", ReplayAnalysisMilliseconds);
        config.SetValue("gallery", "module_order", string.Join(',', GameModuleOrder));
        var error = config.Save(SettingsPath);
        if (error != Error.Ok) GD.PushWarning($"无法保存设置：{error}");
        Apply();
    }

    public void Apply()
    {
        DisplayServer.WindowSetMode(Fullscreen
            ? DisplayServer.WindowMode.Fullscreen
            : DisplayServer.WindowMode.Windowed);
        DisplayServer.WindowSetVsyncMode(VSync
            ? DisplayServer.VSyncMode.Enabled
            : DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = MaxFps;

        SetBusVolume("Master", MasterVolume, create: false);
        SetBusVolume("Music", MusicVolume, create: true);
        SetBusVolume("SFX", EffectsVolume, create: true);
    }

    private void Clamp()
    {
        if (MaxFps is not (0 or 60 or 120 or 144)) MaxFps = 60;
        if (ThemeId.Length > 64 || !ThemeId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            ThemeId = "classic";
        MasterVolume = Mathf.Clamp(MasterVolume, 0, 1);
        MusicVolume = Mathf.Clamp(MusicVolume, 0, 1);
        EffectsVolume = Mathf.Clamp(EffectsVolume, 0, 1);
        CustomPlayerId = CustomPlayerId.Trim();
        if (CustomPlayerId.Length > 32) CustomPlayerId = CustomPlayerId[..32];
        if (CustomPlayerId.Any(char.IsControl)) CustomPlayerId = string.Empty;
        CustomAvatarPath = CustomAvatarPath.Trim();
        AiSideChoice = Math.Clamp(AiSideChoice, 0, 2);
        AiGameSeconds = Math.Clamp(AiGameSeconds, 0, 180 * 60);
        AiMoveSeconds = Math.Clamp(AiMoveSeconds, 0, 300);
        AiThinkMilliseconds = Math.Clamp(AiThinkMilliseconds, 100, 120_000);
        AiDifficulty = Math.Clamp(AiDifficulty, 0, (int)Xiangqi.Core.Application.AiDifficultyLevel.Custom);
        AiMaxDepth = Math.Clamp(AiMaxDepth, 0, 128);
        AiThreads = Math.Clamp(AiThreads, 1, 256);
        AiHashMegabytes = Math.Clamp(AiHashMegabytes, 16, 4096);
        GomokuAiSideChoice = Math.Clamp(GomokuAiSideChoice, 0, 2);
        GomokuAiBoardSize = Math.Clamp(GomokuAiBoardSize, 9, 22);
        GomokuAiThinkMilliseconds = Math.Clamp(GomokuAiThinkMilliseconds, 100, 120_000);
        GomokuAiDifficulty = Math.Clamp(GomokuAiDifficulty, 0, (int)Xiangqi.Core.Application.AiDifficultyLevel.Custom);
        GomokuAiMaxDepth = Math.Clamp(GomokuAiMaxDepth, 0, 128);
        GomokuAiThreads = Math.Clamp(GomokuAiThreads, 1, 256);
        GomokuAiMemoryMegabytes = Math.Clamp(GomokuAiMemoryMegabytes, 16, 4096);
        ReplayAnalysisMilliseconds = Math.Clamp(ReplayAnalysisMilliseconds, 100, 120_000);
        GameModuleOrder = GameModuleOrder.Take(256).Select(value =>
        {
            try { return new GameModuleId(value).Value; }
            catch (ArgumentException) { return string.Empty; }
        }).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void SetBusVolume(string busName, float value, bool create)
    {
        var index = AudioServer.GetBusIndex(busName);
        if (index < 0 && create)
        {
            AudioServer.AddBus();
            index = AudioServer.BusCount - 1;
            AudioServer.SetBusName(index, busName);
        }
        if (index >= 0) AudioServer.SetBusVolumeLinear(index, Mathf.Clamp(value, 0, 1));
    }
}
