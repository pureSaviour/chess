using Xiangqi.Core.Application;
using Xiangqi.GameHost;
using Xiangqi.Presentation.GameModules;
using Xiangqi.Presentation.Screens;

namespace Xiangqi.Presentation;

public partial class Main
{
    /// <summary>
    /// Built-in composition root. Downloaded modules will enter through the same catalog after
    /// package verification; they must not be added by extending the gallery screen constructor.
    /// </summary>
    private IReadOnlyList<GameModuleRegistration> CreateBuiltInGameModules()
    {
        static GameModuleRoute Route(string id, string group, string label,
            GameModuleCapabilities capability, int order) => new(id, group, label, capability, order);
        static (GameModuleRoute Route, Action Activate) Entry(GameModuleRoute route, Action activate) => (route, activate);

        const GameModuleCapabilities common = GameModuleCapabilities.LocalPlay |
            GameModuleCapabilities.LanPlay | GameModuleCapabilities.SteamPlay |
            GameModuleCapabilities.RecentRecords | GameModuleCapabilities.SavedRecords;

        var classicCapabilities = common | GameModuleCapabilities.RecordImport |
            GameModuleCapabilities.PositionImport | GameModuleCapabilities.ComputerPlay |
            GameModuleCapabilities.ComputerSettings;
        var classic = new GameModuleRegistration(new(
            new("xiangqi.classic"), new Version(1, 0), "经典象棋", "楚河汉界 · 红黑争锋",
            "res://assets/gallery/classic-xiangqi-card.png", classicCapabilities, 0),
        [
            Entry(Route("local", "联机模式", "本地联机", GameModuleCapabilities.LocalPlay, 0), () => ShowClassicGallery(ClassicGalleryEntry.Local)),
            Entry(Route("lan", "联机模式", "局域网联机", GameModuleCapabilities.LanPlay, 1), () => ShowClassicGallery(ClassicGalleryEntry.Lan)),
            Entry(Route("steam", "联机模式", "Steam 邀请好友", GameModuleCapabilities.SteamPlay, 2), () => ShowClassicGallery(ClassicGalleryEntry.Steam)),
            Entry(Route("recent", "对局记录", "最近对局", GameModuleCapabilities.RecentRecords, 0), () => ShowClassicRecords(false)),
            Entry(Route("saved", "对局记录", "保存对局", GameModuleCapabilities.SavedRecords, 1), () => ShowClassicRecords(true)),
            Entry(Route("record-import", "棋谱导入", "棋谱导入", GameModuleCapabilities.RecordImport, 0), () => ShowClassicImport(false)),
            Entry(Route("position-import", "棋谱导入", "残局导入", GameModuleCapabilities.PositionImport, 1), () => ShowClassicImport(true)),
            Entry(Route("computer", "人机模式", "人机对决", GameModuleCapabilities.ComputerPlay, 0), StartConfiguredAiMatch),
            Entry(Route("computer-settings", "人机模式", "人机设置", GameModuleCapabilities.ComputerSettings, 1), ShowAiSettings)
        ]);

        var three = new GameModuleRegistration(new(
            new("xiangqi.three-kingdoms"), new Version(1, 0), "三国象棋", "魏 · 吴 · 蜀 · 合纵连横",
            "res://assets/gallery/three-kingdoms-card.png", common, 100),
        [
            Entry(Route("local", "联机模式", "本地三人对决", GameModuleCapabilities.LocalPlay, 0), () => ShowThreeGallery(ThreeGalleryEntry.Local)),
            Entry(Route("lan", "联机模式", "局域网三人联机", GameModuleCapabilities.LanPlay, 1), () => ShowThreeGallery(ThreeGalleryEntry.Lan)),
            Entry(Route("steam", "联机模式", "邀请 Steam 好友", GameModuleCapabilities.SteamPlay, 2), () => ShowThreeGallery(ThreeGalleryEntry.Steam)),
            Entry(Route("recent", "对局记录", "最近对局", GameModuleCapabilities.RecentRecords, 0), () => ShowThreeRecords(false)),
            Entry(Route("saved", "对局记录", "保存对局", GameModuleCapabilities.SavedRecords, 1), () => ShowThreeRecords(true))
        ]);

        var jieqi = new GameModuleRegistration(new(
            new("xiangqi.jieqi"), new Version(1, 0), "揭棋", "暗子初行 · 揭开玄机",
            "res://assets/gallery/jieqi-card.png", common, 200),
        [
            Entry(Route("local", "联机模式", "本地联机", GameModuleCapabilities.LocalPlay, 0), () => ShowJieqiGallery(ClassicGalleryEntry.Local)),
            Entry(Route("lan", "联机模式", "局域网联机", GameModuleCapabilities.LanPlay, 1), () => ShowJieqiGallery(ClassicGalleryEntry.Lan)),
            Entry(Route("steam", "联机模式", "Steam 邀请好友", GameModuleCapabilities.SteamPlay, 2), () => ShowJieqiGallery(ClassicGalleryEntry.Steam)),
            Entry(Route("recent", "对局记录", "最近对局", GameModuleCapabilities.RecentRecords, 0), () => ShowJieqiRecords(false)),
            Entry(Route("saved", "对局记录", "保存对局", GameModuleCapabilities.SavedRecords, 1), () => ShowJieqiRecords(true))
        ]);

        var junqi = new GameModuleRegistration(new(
            new("junqi.standard"), new Version(1, 0), "军旗", "铁路纵横 · 夺旗制胜",
            "res://assets/gallery/junqi-card.svg", common, 300),
        [
            Entry(Route("local", "联机模式", "本地联机", GameModuleCapabilities.LocalPlay, 0), () => ShowJunqiGallery(ClassicGalleryEntry.Local)),
            Entry(Route("lan", "联机模式", "局域网联机", GameModuleCapabilities.LanPlay, 1), () => ShowJunqiGallery(ClassicGalleryEntry.Lan)),
            Entry(Route("steam", "联机模式", "Steam 邀请好友", GameModuleCapabilities.SteamPlay, 2), () => ShowJunqiGallery(ClassicGalleryEntry.Steam)),
            Entry(Route("recent", "对局记录", "最近对局", GameModuleCapabilities.RecentRecords, 0), () => ShowJunqiRecords(false)),
            Entry(Route("saved", "对局记录", "保存对局", GameModuleCapabilities.SavedRecords, 1), () => ShowJunqiRecords(true))
        ]);

        var gomokuCapabilities = common | GameModuleCapabilities.RecordImport |
            GameModuleCapabilities.PositionImport | GameModuleCapabilities.ComputerPlay |
            GameModuleCapabilities.ComputerSettings;
        var gomoku = new GameModuleRegistration(new(
            new("gomoku.standard"), new Version(1, 0), "五子棋", "纵横十九路 · 黑白连珠",
            "res://assets/gallery/gomoku-card.svg", gomokuCapabilities, 400),
        [
            Entry(Route("local", "联机模式", "本地联机", GameModuleCapabilities.LocalPlay, 0), () => ShowGomokuGallery(ClassicGalleryEntry.Local)),
            Entry(Route("lan", "联机模式", "局域网联机", GameModuleCapabilities.LanPlay, 1), () => ShowGomokuGallery(ClassicGalleryEntry.Lan)),
            Entry(Route("steam", "联机模式", "Steam 邀请好友", GameModuleCapabilities.SteamPlay, 2), () => ShowGomokuGallery(ClassicGalleryEntry.Steam)),
            Entry(Route("recent", "对局记录", "最近对局", GameModuleCapabilities.RecentRecords, 0), () => ShowGomokuRecords(false)),
            Entry(Route("saved", "对局记录", "保存对局", GameModuleCapabilities.SavedRecords, 1), () => ShowGomokuRecords(true)),
            Entry(Route("record-import", "棋谱导入", "棋谱导入", GameModuleCapabilities.RecordImport, 0), () => ShowGomokuImport(false)),
            Entry(Route("position-import", "棋谱导入", "残局导入", GameModuleCapabilities.PositionImport, 1), () => ShowGomokuImport(true)),
            Entry(Route("computer", "人机模式", "人机对决", GameModuleCapabilities.ComputerPlay, 0), StartConfiguredGomokuAiMatch),
            Entry(Route("computer-settings", "人机模式", "人机设置", GameModuleCapabilities.ComputerSettings, 1), ShowGomokuAiSettings)
        ]);
        var go = new GameModuleRegistration(new(
            new("go.chinese"), new Version(1, 0), "围棋", "十九路纵横 · 气尽提子 · 数子定胜",
            "res://assets/gallery/go-card.svg", common, 500),
        [
            Entry(Route("local", "联机模式", "本地联机", GameModuleCapabilities.LocalPlay, 0), () => ShowGoGallery(ClassicGalleryEntry.Local)),
            Entry(Route("lan", "联机模式", "局域网联机", GameModuleCapabilities.LanPlay, 1), () => ShowGoGallery(ClassicGalleryEntry.Lan)),
            Entry(Route("steam", "联机模式", "Steam 邀请好友", GameModuleCapabilities.SteamPlay, 2), () => ShowGoGallery(ClassicGalleryEntry.Steam)),
            Entry(Route("recent", "对局记录", "最近对局", GameModuleCapabilities.RecentRecords, 0), () => ShowGoRecords(false)),
            Entry(Route("saved", "对局记录", "保存对局", GameModuleCapabilities.SavedRecords, 1), () => ShowGoRecords(true))
        ]);
        return [classic, three, jieqi, junqi, gomoku, go];
    }
}
