using Godot;
using Xiangqi.Presentation.Settings;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

public partial class SettingsScreen : Control
{
    private readonly AppSettings _draft;
    private readonly IBoardThemeCatalog _themes;
    private readonly List<string> _themeIds = [];
    private readonly CheckButton _fullscreen = new() { Text = "全屏模式" };
    private readonly CheckButton _vsync = new() { Text = "垂直同步" };
    private readonly CheckButton _effects = new() { Text = "棋局特效" };
    private readonly CheckButton _coordinates = new() { Text = "显示棋盘路数" };
    private readonly OptionButton _fps = new();
    private readonly OptionButton _skin = new();
    private readonly Label _themeStatus = new();
    private readonly HSlider _master = VolumeSlider();
    private readonly HSlider _music = VolumeSlider();
    private readonly HSlider _sfx = VolumeSlider();
    private readonly LineEdit _playerId = new() { MaxLength = 32, PlaceholderText = "留空则使用 Steam 昵称 / 默认标识" };
    private readonly Label _avatarPath = new();
    private readonly FileDialog _avatarDialog = new()
    {
        Access = FileDialog.AccessEnum.Filesystem,
        FileMode = FileDialog.FileModeEnum.OpenFile,
        Filters = new[] { "*.png, *.jpg, *.jpeg, *.webp ; 图片" }
    };

    public SettingsScreen(AppSettings current, IBoardThemeCatalog themes,
        Action<AppSettings> save, Action cancel)
    {
        _draft = current.Copy();
        _themes = themes ?? throw new ArgumentNullException(nameof(themes));
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(110, 46);
        AddChild(margin);
        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 18);
        margin.AddChild(page);

        var header = new HBoxContainer();
        var back = UiStyle.CreateButton("‹ 取消");
        back.CustomMinimumSize = new Vector2(120, 44);
        back.Pressed += cancel;
        header.AddChild(back);
        var title = UiStyle.CreateTitle("设置", 36);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        header.AddChild(new Control { CustomMinimumSize = new Vector2(120, 0) });
        page.AddChild(header);

        var panel = UiStyle.CreatePanel();
        panel.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(panel);
        var panelMargin = new MarginContainer();
        foreach (var key in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            panelMargin.AddThemeConstantOverride(key, 30);
        panel.AddChild(panelMargin);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 36);
        grid.AddThemeConstantOverride("v_separation", 20);
        panelMargin.AddChild(grid);

        AddSection(grid, "画面", "窗口显示和动画表现");
        _fullscreen.ButtonPressed = _draft.Fullscreen;
        grid.AddChild(_fullscreen);
        _vsync.ButtonPressed = _draft.VSync;
        grid.AddChild(_vsync);
        AddRow(grid, "帧率上限", _fps);
        foreach (var (label, value) in new[] { ("不限制", 0), ("60 FPS", 60), ("120 FPS", 120), ("144 FPS", 144) })
            _fps.AddItem(label, value);
        var fpsIndex = Array.IndexOf(new[] { 0, 60, 120, 144 }, _draft.MaxFps);
        _fps.Select(Math.Max(0, fpsIndex));
        _effects.ButtonPressed = _draft.EffectsEnabled;
        grid.AddChild(_effects);
        grid.AddChild(new Control());
        PopulateThemes(_draft.ThemeId);
        AddRow(grid, "棋盘皮肤", _skin);
        var themeTools = new HBoxContainer();
        var rescan = UiStyle.CreateButton("重新扫描主题");
        rescan.Pressed += () =>
        {
            var selected = SelectedThemeId();
            _themes.Rescan();
            PopulateThemes(selected);
        };
        _themeStatus.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        UiStyle.SetMuted(_themeStatus, 12);
        themeTools.AddChild(rescan); themeTools.AddChild(_themeStatus);
        AddRow(grid, "主题目录", themeTools);
        _coordinates.ButtonPressed = _draft.ShowBoardCoordinates;
        grid.AddChild(_coordinates);
        grid.AddChild(new Control());

        AddSection(grid, "声音", "音频总线音量；后续素材会自动遵循这些设置");
        _master.Value = _draft.MasterVolume * 100;
        _music.Value = _draft.MusicVolume * 100;
        _sfx.Value = _draft.EffectsVolume * 100;
        AddRow(grid, "主音量", _master);
        AddRow(grid, "音乐音量", _music);
        AddRow(grid, "音效音量", _sfx);

        AddSection(grid, "玩家资料", "仅自定义内容会发给对局中的另一位玩家");
        _playerId.Text = _draft.CustomPlayerId;
        AddRow(grid, "自定义 ID", _playerId);
        var avatarRow = new HBoxContainer();
        _avatarPath.Text = string.IsNullOrWhiteSpace(_draft.CustomAvatarPath)
            ? "使用平台默认头像" : Path.GetFileName(_draft.CustomAvatarPath);
        _avatarPath.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        UiStyle.SetMuted(_avatarPath, 12);
        var chooseAvatar = UiStyle.CreateButton("选择头像");
        chooseAvatar.Pressed += () => _avatarDialog.PopupCenteredRatio(0.72f);
        var clearAvatar = UiStyle.CreateButton("清除");
        clearAvatar.Pressed += () =>
        {
            _draft.CustomAvatarPath = string.Empty;
            _avatarPath.Text = "使用平台默认头像";
        };
        avatarRow.AddChild(_avatarPath);
        avatarRow.AddChild(chooseAvatar);
        avatarRow.AddChild(clearAvatar);
        AddRow(grid, "自定义头像", avatarRow);
        _avatarDialog.FileSelected += path =>
        {
            _draft.CustomAvatarPath = path;
            _avatarPath.Text = Path.GetFileName(path);
        };
        AddChild(_avatarDialog);

        var saveButton = UiStyle.CreateButton("保存并应用", true);
        saveButton.Pressed += () =>
        {
            _draft.Fullscreen = _fullscreen.ButtonPressed;
            _draft.VSync = _vsync.ButtonPressed;
            _draft.MaxFps = _fps.GetItemId(_fps.Selected);
            _draft.EffectsEnabled = _effects.ButtonPressed;
            _draft.ThemeId = SelectedThemeId();
            _draft.ShowBoardCoordinates = _coordinates.ButtonPressed;
            _draft.MasterVolume = (float)_master.Value / 100f;
            _draft.MusicVolume = (float)_music.Value / 100f;
            _draft.EffectsVolume = (float)_sfx.Value / 100f;
            _draft.CustomPlayerId = _playerId.Text;
            save(_draft);
        };
        page.AddChild(saveButton);
    }

    private void PopulateThemes(string selectedId)
    {
        _skin.Clear();
        _themeIds.Clear();
        foreach (var theme in _themes.Themes)
        {
            _themeIds.Add(theme.Id);
            _skin.AddItem(theme.DisplayName);
        }
        var selected = _themeIds.FindIndex(id => id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
        _skin.Select(Math.Max(0, selected));
        var errors = _themes.Diagnostics.Count(diagnostic => diagnostic.IsError);
        _themeStatus.Text = errors == 0
            ? $"已载入 {_themeIds.Count} 个 · 对局中不热重载"
            : $"已载入 {_themeIds.Count} 个 · {errors} 个包被跳过（详见日志）";
        foreach (var diagnostic in _themes.Diagnostics)
            GD.PushWarning($"[Theme] {diagnostic.PackagePath}: {diagnostic.Message}");
    }

    private string SelectedThemeId() => _skin.Selected >= 0 && _skin.Selected < _themeIds.Count
        ? _themeIds[_skin.Selected] : "classic";

    private static HSlider VolumeSlider() => new()
    {
        MinValue = 0,
        MaxValue = 100,
        Step = 1,
        CustomMinimumSize = new Vector2(420, 32),
        SizeFlagsHorizontal = SizeFlags.ExpandFill
    };

    private static void AddSection(GridContainer grid, string title, string detail)
    {
        var heading = UiStyle.CreateTitle(title, 21);
        grid.AddChild(heading);
        var description = new Label { Text = detail, HorizontalAlignment = HorizontalAlignment.Right };
        UiStyle.SetMuted(description, 13);
        grid.AddChild(description);
    }

    private static void AddRow(GridContainer grid, string name, Control control)
    {
        var label = new Label { Text = name, VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeColorOverride("font_color", UiStyle.Text);
        grid.AddChild(label);
        grid.AddChild(control);
    }
}
