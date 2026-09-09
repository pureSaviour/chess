using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Domain;
using Xiangqi.Core.Domain.ThreeKingdoms;
using Xiangqi.Presentation.UI;

namespace Xiangqi.Presentation.Screens;

/// <summary>Presentation-only route selected by a three-player mode card option; it carries no room data.</summary>
public enum ThreeGalleryEntry { Local, Lan, Steam }

public partial class ThreeGalleryScreen : Control
{
    private readonly VBoxContainer _page = new();
    private readonly Action _back;
    private readonly Action<TimeControl, AdvisorConquestPolicy> _local;
    private readonly Action<int, TimeControl, AdvisorConquestPolicy> _host;
    private readonly Action<string, int> _join;
    private readonly Action<TimeControl, AdvisorConquestPolicy> _steam;
    public ThreeGalleryScreen(Action back, Action<TimeControl, AdvisorConquestPolicy> local,
        Action<int, TimeControl, AdvisorConquestPolicy> host, Action<string, int> join,
        Action<TimeControl, AdvisorConquestPolicy> steam, ThreeGalleryEntry initialEntry)
    {
        _back = back; _local = local; _host = host; _join = join; _steam = steam;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = UiStyle.CreatePageMargin(54, 34); AddChild(margin); margin.AddChild(_page);
        _page.AddThemeConstantOverride("separation", 16);
        Settings(initialEntry switch
        {
            ThreeGalleryEntry.Local => 0,
            ThreeGalleryEntry.Lan => 1,
            ThreeGalleryEntry.Steam => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(initialEntry))
        });
    }
    private void Clear(string title, Action back)
    {
        foreach (var c in _page.GetChildren()) { _page.RemoveChild(c); c.QueueFree(); }
        _page.AddChild(UiStyle.CreateTitle(title)); Add("返回", back);
    }
    private void Add(string text, Action action)
    { var b = UiStyle.CreateButton(text); b.Pressed += action; _page.AddChild(b); }
    private void Settings(int mode)
    {
        Clear(mode == 0 ? "三国象棋 · 本地房间" : mode == 1 ? "三国象棋 · 局域网房间" : "三国象棋 · Steam 好友房间", _back);
        var grid = new GridContainer { Columns = 2 }; grid.AddThemeConstantOverride("h_separation", 20); grid.AddThemeConstantOverride("v_separation", 16); _page.AddChild(grid);
        var game = new SpinBox { MinValue = 0, MaxValue = 240, Value = 15, Suffix = "分钟（0 不限）", CustomMinimumSize = new(380, 42) };
        var move = new SpinBox { MinValue = 0, MaxValue = 3600, Value = 60, Suffix = "秒（0 不限）" };
        var policy = new OptionButton(); policy.AddItem("共享国土：收编后士解除九宫限制"); policy.AddItem("共生死：被收编方的士直接消失");
        grid.AddChild(new Label { Text = "每国局时" }); grid.AddChild(game);
        grid.AddChild(new Label { Text = "每步用时" }); grid.AddChild(move);
        grid.AddChild(new Label { Text = "士收编策略" }); grid.AddChild(policy);
        TimeControl Time() => new((int)game.Value * 60, (int)move.Value);
        AdvisorConquestPolicy Policy() => (AdvisorConquestPolicy)policy.Selected;
        if (mode == 0) Add("开始本地三人对决", () => _local(Time(), Policy()));
        else if (mode == 2) Add("创建三席房间并邀请好友", () => _steam(Time(), Policy()));
        else
        {
            var address = new LineEdit { Text = "127.0.0.1", PlaceholderText = "房主局域网 IPv4" };
            var port = new SpinBox { MinValue = 1024, MaxValue = 65535, Value = 48028 };
            grid.AddChild(new Label { Text = "房主地址" }); grid.AddChild(address);
            grid.AddChild(new Label { Text = "端口" }); grid.AddChild(port);
            Add("创建房间", () => _host((int)port.Value, Time(), Policy()));
            Add("加入房间（采用房主规则）", () => _join(address.Text.Trim(), (int)port.Value));
        }
        _page.AddChild(new Label { Text = "魏 → 吴 → 蜀 · 满三席开始 · 断线仍计时\n悔棋与和棋需所有其他存活玩家同意；任一存活玩家可判断线者负。", AutowrapMode = TextServer.AutowrapMode.WordSmart });
    }
}
