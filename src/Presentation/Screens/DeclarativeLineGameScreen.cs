using System.Text.Json;
using Godot;
using Xiangqi.GameHost;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

/// <summary>
/// Host-owned presentation for line-placement-v1. It renders immutable snapshots and submits only
/// row/column intentions to LinePlacementGame; package data never supplies callbacks, scripts,
/// colors, nodes or file paths. This screen owns its buttons only and performs no background work.
/// </summary>
public partial class DeclarativeLineGameScreen : Control
{
    private readonly LoadedDeclarativeGameModule _module;
    private LinePlacementGame _game;
    private readonly GameRecordStorage? _records;
    private readonly Label _status;
    private readonly Button[] _cells;
    private bool _recorded;

    public DeclarativeLineGameScreen(LoadedDeclarativeGameModule module, string gamesRoot, Action back)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
        ArgumentNullException.ThrowIfNull(back);
        _game = new(module.Definition);
        if (module.PackageManifest.Permissions.HasFlag(GamePackagePermissions.ModuleStorage))
            _records = new(gamesRoot, module.PackageManifest.Module.Id, ".json", "match");

        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(38, 24);
        AddChild(margin);
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 12);
        margin.AddChild(root);
        var heading = new HBoxContainer();
        root.AddChild(heading);
        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        heading.AddChild(titles);
        titles.AddChild(UiStyle.CreateTitle(module.PackageManifest.Module.DisplayName));
        var subtitle = new Label
        {
            Text = $"受限声明式玩法 · {module.Definition.BoardWidth}×{module.Definition.BoardHeight} · 连成 {module.Definition.WinLength} 子获胜"
        };
        UiStyle.SetMuted(subtitle, 14);
        titles.AddChild(subtitle);
        var restart = UiStyle.CreateButton("重新开始");
        restart.CustomMinimumSize = new Vector2(118, 44);
        restart.Pressed += Restart;
        heading.AddChild(restart);
        var exit = UiStyle.CreateButton("返回画廊");
        exit.CustomMinimumSize = new Vector2(118, 44);
        exit.Pressed += back;
        heading.AddChild(exit);

        _status = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _status.AddThemeFontSizeOverride("font_size", 18);
        root.AddChild(_status);
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        root.AddChild(scroll);
        var center = new CenterContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        scroll.AddChild(center);
        var grid = new GridContainer { Columns = module.Definition.BoardWidth };
        grid.AddThemeConstantOverride("h_separation", 3);
        grid.AddThemeConstantOverride("v_separation", 3);
        center.AddChild(grid);
        _cells = new Button[checked(module.Definition.BoardWidth * module.Definition.BoardHeight)];
        for (var row = 0; row < module.Definition.BoardHeight; row++)
        for (var column = 0; column < module.Definition.BoardWidth; column++)
        {
            var capturedRow = row;
            var capturedColumn = column;
            var button = new Button { CustomMinimumSize = new Vector2(38, 38) };
            button.AddThemeFontSizeOverride("font_size", 24);
            button.Pressed += () => Place(capturedRow, capturedColumn);
            _cells[row * module.Definition.BoardWidth + column] = button;
            grid.AddChild(button);
        }
        Refresh();
    }

    private void Place(int row, int column)
    {
        try
        {
            if (_game.TryPlace(row, column))
            {
                Refresh();
                if (_game.Finished) SaveRecentOnce();
            }
        }
        catch (Exception error) { _status.Text = error.Message; }
    }

    private void Restart()
    {
        _game = new(_module.Definition);
        _recorded = false;
        Refresh();
    }

    private void Refresh()
    {
        var snapshot = _game.Snapshot();
        for (var index = 0; index < snapshot.Cells.Count; index++)
        {
            var value = snapshot.Cells[index];
            _cells[index].Text = value switch { 0 => "●", 1 => "○", _ => string.Empty };
            _cells[index].Disabled = snapshot.Finished || value != -1;
            if (value == 0) _cells[index].AddThemeColorOverride("font_color", new Color("#E14B3F"));
            else if (value == 1) _cells[index].AddThemeColorOverride("font_color", new Color("#E9D7B4"));
        }
        _status.Text = snapshot.Finished
            ? snapshot.Winner is { } winner
                ? $"{PlayerLabel(winner)} 获胜 · 共 {snapshot.Ply} 手"
                : $"和棋 · 共 {snapshot.Ply} 手"
            : $"第 {snapshot.Ply + 1} 手 · 请 {PlayerLabel(snapshot.CurrentPlayer)} 落子";
    }

    private string PlayerLabel(int player) => player == 0
        ? _module.Definition.FirstPlayerLabel : _module.Definition.SecondPlayerLabel;

    private void SaveRecentOnce()
    {
        if (_records is null || _recorded) return;
        _recorded = true;
        try
        {
            var snapshot = _game.Snapshot();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                moduleId = _module.PackageManifest.Module.Id.Value,
                moduleVersion = _module.PackageManifest.Module.Version.ToString(),
                runtime = "line-placement-v1",
                definition = _module.Definition,
                moves = _game.Moves,
                winner = snapshot.Winner,
                finished = snapshot.Finished
            });
            var path = _records.CreatePath(saved: false);
            _records.WriteAtomic(path, bytes);
            _records.PruneRecent(10);
        }
        catch (Exception error)
        {
            _status.Text += $" · 最近对局保存失败：{error.Message}";
        }
    }
}
