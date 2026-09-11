using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Application.ThreeKingdoms;
using Xiangqi.Core.Domain.ThreeKingdoms;
using Xiangqi.Presentation.Boards;
using Xiangqi.Presentation.Records;
using Xiangqi.Presentation.Settings;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

public partial class ThreeRecordScreen : Control
{
    private readonly List<ImageTexture> _textures = [];
    public ThreeRecordScreen(bool saved, Action back, Action<ThreeGameRecord> replay, Action<ThreeGameRecord> puzzle)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(60, 36); AddChild(margin); var root = new VBoxContainer(); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header); var title = UiStyle.CreateTitle(saved ? "三国象棋 · 保存对局" : "三国象棋 · 最近对局"); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        var leave = UiStyle.CreateButton("返回画廊"); leave.Pressed += back; header.AddChild(leave);
        root.AddChild(RecordEntryRow.CreateHeadings());
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(900, 300) }; root.AddChild(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; rows.AddThemeConstantOverride("separation", 10); scroll.AddChild(rows);
        foreach (var entry in ThreeRecordStore.ListEntries(saved))
            rows.AddChild(RecordEntryRow.Create(entry.Presentation,
                () => replay(ThreeRecordStore.Load(entry.RecordPath)), _textures));
        root.AddChild(new HSeparator()); root.AddChild(new Label { Text = "导入三国扩展 PGN（可作为残局开局）" });
        var source = new TextEdit { PlaceholderText = "粘贴 Variant=Xiangqi-ThreeKingdoms 的完整 PGN", CustomMinimumSize = new(0, 145) }; root.AddChild(source);
        var import = UiStyle.CreateButton("从导入局面的起始位置开局"); root.AddChild(import); var errorDialog = new AcceptDialog { Title = "无法导入" }; AddChild(errorDialog);
        import.Pressed += () => { if (ThreeKingdomsPgnCodec.TryParse(source.Text, out var record, out var error)) puzzle(record!); else { errorDialog.DialogText = error; errorDialog.PopupCentered(new(580, 170)); } };
    }
    public override void _ExitTree() { foreach (var texture in _textures) texture.Dispose(); _textures.Clear(); }
}

public partial class ThreeReplayScreen : Control
{
    public ThreeReplayScreen(AppSettings settings, IBoardThemeCatalog themes, ThreeGameRecord record, Action back)
    {
        var states = record.Replay(); var index = 0; SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(24, 18); AddChild(margin); var root = new VBoxContainer(); margin.AddChild(root);
        var header = new HBoxContainer(); root.AddChild(header); var title = UiStyle.CreateTitle("三国棋局复盘"); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title); var counter = new Label(); header.AddChild(counter);
        var leave = UiStyle.CreateButton("返回记录"); leave.Pressed += back; header.AddChild(leave);
        var center = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; root.AddChild(center); var board = new ThreeBoardView(themes) { MouseFilter = MouseFilterEnum.Ignore }; board.ApplyAppearance(settings); center.AddChild(board);
        var timeline = new HSlider { MinValue = 0, MaxValue = Math.Max(0, states.Count - 1), Step = 1,
            TickCount = Math.Clamp(states.Count, 2, 31), TicksOnBorders = true, CustomMinimumSize = new Vector2(0, 42) }; root.AddChild(timeline);
        var controls = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; root.AddChild(controls); var previous = UiStyle.CreateButton("◀ 上一步"); var next = UiStyle.CreateButton("下一步 ▶"); controls.AddChild(previous); controls.AddChild(next);
        void Render() { board.SetBottomKingdom(states[index].Turn); board.SetPosition(states[index]); counter.Text = $"第 {index}/{states.Count - 1} 手"; timeline.SetValueNoSignal(index); previous.Disabled = index == 0; next.Disabled = index == states.Count - 1; }
        previous.Pressed += () => { if (index > 0) index--; Render(); }; next.Pressed += () => { if (index + 1 < states.Count) index++; Render(); };
        timeline.ValueChanged += value => { index = Math.Clamp((int)Math.Round(value), 0, states.Count - 1); Render(); }; Render();
    }
}
