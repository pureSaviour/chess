using Xiangqi.GameHost;

internal static class GameModuleCatalogTests
{
    public static readonly (string Name, Action Run)[] All =
    [
        ("玩法目录按默认顺序发布模块", DefaultOrder),
        ("玩法目录规范化陈旧与重复的用户顺序", NormalizePreferredOrder),
        ("玩法目录可前后移动模块且限制边界", MoveModule),
        ("玩法目录拒绝重复模块和未声明能力", RejectInvalidModules)
    ];

    private static void DefaultOrder()
    {
        var catalog = new GameModuleCatalog([Module("games.third", 30), Module("games.first", 10), Module("games.second", 20)]);
        Equal("games.first,games.second,games.third", Join(catalog.Modules));
    }

    private static void NormalizePreferredOrder()
    {
        var catalog = new GameModuleCatalog([Module("games.first", 10), Module("games.second", 20), Module("games.third", 30)]);
        var ordered = catalog.Order(["bad/id", "games.third", "games.third", "missing.game"]);
        Equal("games.third,games.first,games.second", Join(ordered));
    }

    private static void MoveModule()
    {
        var catalog = new GameModuleCatalog([Module("games.first", 10), Module("games.second", 20), Module("games.third", 30)]);
        Equal("games.second,games.first,games.third", string.Join(',',
            catalog.Move([], new GameModuleId("games.second"), -1)));
        Equal("games.first,games.second,games.third", string.Join(',',
            catalog.Move([], new GameModuleId("games.first"), -1)));
        Equal("games.second,missing.game,games.first,games.third", string.Join(',',
            catalog.Move(["games.first", "missing.game", "games.second"], new GameModuleId("games.second"), -1)));
        Throws<ArgumentOutOfRangeException>(() => catalog.Move([], new GameModuleId("games.first"), 2));
        Throws<KeyNotFoundException>(() => catalog.Move([], new GameModuleId("games.missing"), 1));
    }

    private static void RejectInvalidModules()
    {
        Throws<ArgumentException>(() => new GameModuleCatalog([Module("games.same", 0), Module("games.same", 1)]));
        var invalid = new FakeModule(new(new("games.invalid"), new Version(1, 0), "错误玩法", "测试",
            "res://invalid.png", GameModuleCapabilities.LocalPlay, 0),
            [new("steam", "联机模式", "Steam", GameModuleCapabilities.SteamPlay)]);
        Throws<ArgumentException>(() => new GameModuleCatalog([invalid]));
    }

    private static IGameModule Module(string id, int order)
    {
        var capability = GameModuleCapabilities.LocalPlay;
        return new FakeModule(new(new(id), new Version(1, 0), id, "测试玩法",
            $"res://{id}.png", capability, order), [new("local", "联机模式", "本地", capability)]);
    }

    private static string Join(IEnumerable<IGameModule> modules) =>
        string.Join(',', modules.Select(module => module.Manifest.Id.Value));

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed record FakeModule(GameModuleManifest Manifest,
        IReadOnlyList<GameModuleRoute> Routes) : IGameModule;
}
