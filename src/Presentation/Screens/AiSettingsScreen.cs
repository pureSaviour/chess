using Godot;
using Xiangqi.Presentation.Settings;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

/// <summary>
/// Edits a detached AppSettings copy. It owns only the picker node; it neither starts nor probes
/// an engine. The composition root validates the saved path immediately before a match/replay.
/// </summary>
public partial class AiSettingsScreen : Control
{
    public AiSettingsScreen(AppSettings current, Action<AppSettings> save, Action back)
    {
        var draft = current.Copy();
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(60, 34); AddChild(margin);
        var root = new VBoxContainer(); root.AddThemeConstantOverride("separation", 14); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header);
        var title = UiStyle.CreateTitle("人机设置"); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var leave = UiStyle.CreateButton("返回画廊"); leave.Pressed += back; header.AddChild(leave);
        var panel = UiStyle.CreatePanel(UiStyle.PanelLight, 14); panel.CustomMinimumSize = new(760, 500); root.AddChild(panel);
        var content = new VBoxContainer(); content.AddThemeConstantOverride("separation", 16); panel.AddChild(content);
        content.AddChild(new Label { Text = "UCI 象棋引擎路径（对局与复盘共用）" });
        var pathRow = new HBoxContainer(); content.AddChild(pathRow);
        var path = new LineEdit { Text = draft.AiEnginePath, Editable = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "例如 pikafish.exe" }; pathRow.AddChild(path);
        var browse = UiStyle.CreateButton("浏览…"); pathRow.AddChild(browse);
        var picker = new FileDialog { Title = "选择 UCI 象棋引擎", FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Filters = ["*.exe ; Windows 可执行文件", "* ; 所有文件"] };
        picker.FileSelected += selected => path.Text = selected; browse.Pressed += () => picker.PopupCenteredRatio(0.75f); AddChild(picker);
        var grid = new GridContainer { Columns = 2 }; grid.AddThemeConstantOverride("h_separation", 20); grid.AddThemeConstantOverride("v_separation", 14); content.AddChild(grid);
        grid.AddChild(new Label { Text = "玩家执子" }); var side = new OptionButton(); side.AddItem("玩家执红"); side.AddItem("玩家执黑"); side.AddItem("随机猜先"); side.Select(draft.AiSideChoice); grid.AddChild(side);
        grid.AddChild(new Label { Text = "局时（分钟，0 不限）" }); var game = new SpinBox { MinValue = 0, MaxValue = 180, Value = draft.AiGameSeconds / 60.0, Step = 1 }; grid.AddChild(game);
        grid.AddChild(new Label { Text = "步时（秒，0 不限）" }); var move = new SpinBox { MinValue = 0, MaxValue = 300, Value = draft.AiMoveSeconds, Step = 5 }; grid.AddChild(move);
        grid.AddChild(new Label { Text = "对局每步思考（秒）" }); var think = new SpinBox { MinValue = .1, MaxValue = 120, Value = draft.AiThinkMilliseconds / 1000.0, Step = .1 }; grid.AddChild(think);
        grid.AddChild(new Label { Text = "复盘每步分析（秒）" }); var analysis = new SpinBox { MinValue = .1, MaxValue = 10, Value = draft.AiAnalysisMilliseconds / 1000.0, Step = .1 }; grid.AddChild(analysis);
        var saveButton = UiStyle.CreateButton("保存人机设置", true); content.AddChild(saveButton);
        saveButton.Pressed += () =>
        {
            draft.AiEnginePath = path.Text; draft.AiSideChoice = side.Selected;
            draft.AiGameSeconds = (int)Math.Round(game.Value * 60); draft.AiMoveSeconds = (int)move.Value;
            draft.AiThinkMilliseconds = (int)Math.Round(think.Value * 1000); draft.AiAnalysisMilliseconds = (int)Math.Round(analysis.Value * 1000);
            save(draft);
        };
    }
}
