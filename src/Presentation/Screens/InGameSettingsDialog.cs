using Godot;
using Xiangqi.Presentation.Settings;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

/// <summary>
/// Non-destructive room settings surface. It edits a detached copy and commits only on confirm,
/// allowing visual/audio preferences to change without replacing the active GameSession.
/// </summary>
public partial class InGameSettingsDialog : ConfirmationDialog
{
    private readonly CheckButton _coordinates = new() { Text = "显示棋盘路数" };
    private readonly IBoardThemeCatalog _themes;
    private readonly List<string> _themeIds = [];
    private readonly CheckButton _effects = new() { Text = "棋局特效" };
    private readonly OptionButton _skin = new();
    private readonly HSlider _master = VolumeSlider();
    private readonly HSlider _sfx = VolumeSlider();
    private readonly Label _roomHeading = UiStyle.CreateTitle("房间信息", 19);
    private readonly Label _roomName = new() { Text = "连接与规则" };
    private readonly Label _roomDetail = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label _roomEditState = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private AppSettings? _draft;

    public event Action<AppSettings>? Saved;

    public InGameSettingsDialog(IBoardThemeCatalog? themes = null)
    {
        _themes = themes ?? BoardThemeCatalog.Default;
        Title = "房间设置";
        OkButtonText = "保存并应用";
        GetCancelButton().Text = "取消";
        Exclusive = true;
        MinSize = new Vector2I(520, 390);

        var margin = new MarginContainer();
        foreach (var key in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(key, 20);
        AddChild(margin);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 26);
        grid.AddThemeConstantOverride("v_separation", 16);
        margin.AddChild(grid);

        grid.AddChild(_roomHeading);
        UiStyle.SetMuted(_roomEditState, 12); grid.AddChild(_roomEditState);
        grid.AddChild(_roomName);
        UiStyle.SetMuted(_roomDetail, 13); grid.AddChild(_roomDetail);
        _roomHeading.Visible = _roomEditState.Visible = _roomName.Visible = _roomDetail.Visible = false;

        AddSection(grid, "棋盘", "仅改变本机显示，不影响棋局状态");
        PopulateThemes();
        AddRow(grid, "棋盘皮肤", _skin);
        grid.AddChild(_coordinates);
        grid.AddChild(new Control());
        grid.AddChild(_effects);
        grid.AddChild(new Control());

        AddSection(grid, "声音", "对局中即时生效");
        AddRow(grid, "主音量", _master);
        AddRow(grid, "音效音量", _sfx);
        Confirmed += SaveDraft;
    }

    public void PopupFor(AppSettings current)
    {
        _roomHeading.Visible = _roomEditState.Visible = _roomName.Visible = _roomDetail.Visible = false;
        MinSize = new Vector2I(520, 390);
        _draft = current.Copy();
        _coordinates.ButtonPressed = _draft.ShowBoardCoordinates;
        _effects.ButtonPressed = _draft.EffectsEnabled;
        SelectTheme(_draft.ThemeId);
        _master.Value = _draft.MasterVolume * 100;
        _sfx.Value = _draft.EffectsVolume * 100;
        PopupCentered(new Vector2I(560, 430));
    }

    /// <summary>
    /// Shows technical room metadata inside settings instead of the permanent match header.
    /// The caller supplies the authoritative preparation/editability state; this dialog does not
    /// mutate transport endpoints or match rules after their owning controller has started.
    /// </summary>
    public void PopupFor(AppSettings current, string roomHeading, string roomDetail, bool canEditRoom)
    {
        _roomHeading.Text = roomHeading;
        _roomDetail.Text = roomDetail;
        _roomEditState.Text = canEditRoom ? "准备阶段 · 参数可在玩法准备页更改" : "对局已开始 · 房间参数已锁定";
        PopupFor(current);
        _roomHeading.Visible = _roomEditState.Visible = _roomName.Visible = _roomDetail.Visible = true;
        MinSize = new Vector2I(560, 480);
        PopupCentered(new Vector2I(600, 510));
    }

    private void SaveDraft()
    {
        if (_draft is null) return;
        _draft.ShowBoardCoordinates = _coordinates.ButtonPressed;
        _draft.ThemeId = _skin.Selected >= 0 && _skin.Selected < _themeIds.Count
            ? _themeIds[_skin.Selected] : "classic";
        _draft.EffectsEnabled = _effects.ButtonPressed;
        _draft.MasterVolume = (float)_master.Value / 100f;
        _draft.EffectsVolume = (float)_sfx.Value / 100f;
        Saved?.Invoke(_draft);
    }

    private void PopulateThemes()
    {
        _skin.Clear(); _themeIds.Clear();
        foreach (var theme in _themes.Themes)
        {
            _themeIds.Add(theme.Id);
            _skin.AddItem(theme.DisplayName);
        }
    }

    private void SelectTheme(string id)
    {
        for (var index = 0; index < _themeIds.Count; index++)
        {
            if (!_themeIds[index].Equals(id, StringComparison.OrdinalIgnoreCase)) continue;
            _skin.Select(index);
            return;
        }
        _skin.Select(0);
    }

    private static HSlider VolumeSlider() => new()
    {
        MinValue = 0,
        MaxValue = 100,
        Step = 1,
        CustomMinimumSize = new Vector2(300, 30),
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
    };

    private static void AddSection(GridContainer grid, string title, string detail)
    {
        grid.AddChild(UiStyle.CreateTitle(title, 19));
        var label = new Label { Text = detail, HorizontalAlignment = HorizontalAlignment.Right };
        UiStyle.SetMuted(label, 12);
        grid.AddChild(label);
    }

    private static void AddRow(GridContainer grid, string name, Control control)
    {
        grid.AddChild(new Label { Text = name, VerticalAlignment = VerticalAlignment.Center });
        grid.AddChild(control);
    }
}
