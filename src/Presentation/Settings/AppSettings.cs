using Godot;

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
    public float MasterVolume { get; set; } = 0.8f;
    public float MusicVolume { get; set; } = 0.7f;
    public float EffectsVolume { get; set; } = 0.85f;
    public string CustomPlayerId { get; set; } = string.Empty;
    public string CustomAvatarPath { get; set; } = string.Empty;
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
        result.MasterVolume = (float)config.GetValue("audio", "master", result.MasterVolume).AsDouble();
        result.MusicVolume = (float)config.GetValue("audio", "music", result.MusicVolume).AsDouble();
        result.EffectsVolume = (float)config.GetValue("audio", "effects", result.EffectsVolume).AsDouble();
        result.CustomPlayerId = config.GetValue("profile", "player_id", string.Empty).AsString();
        result.CustomAvatarPath = config.GetValue("profile", "avatar_path", string.Empty).AsString();
        return result;
    }

    public AppSettings Copy() => (AppSettings)MemberwiseClone();

    public void SaveAndApply()
    {
        Clamp();
        var config = new ConfigFile();
        config.SetValue("display", "fullscreen", Fullscreen);
        config.SetValue("display", "vsync", VSync);
        config.SetValue("display", "max_fps", MaxFps);
        config.SetValue("display", "effects", EffectsEnabled);
        config.SetValue("audio", "master", MasterVolume);
        config.SetValue("audio", "music", MusicVolume);
        config.SetValue("audio", "effects", EffectsVolume);
        config.SetValue("profile", "player_id", CustomPlayerId);
        config.SetValue("profile", "avatar_path", CustomAvatarPath);
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
        MasterVolume = Mathf.Clamp(MasterVolume, 0, 1);
        MusicVolume = Mathf.Clamp(MusicVolume, 0, 1);
        EffectsVolume = Mathf.Clamp(EffectsVolume, 0, 1);
        CustomPlayerId = CustomPlayerId.Trim();
        if (CustomPlayerId.Length > 32) CustomPlayerId = CustomPlayerId[..32];
        if (CustomPlayerId.Any(char.IsControl)) CustomPlayerId = string.Empty;
        CustomAvatarPath = CustomAvatarPath.Trim();
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
