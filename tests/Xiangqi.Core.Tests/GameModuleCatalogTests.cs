using Xiangqi.GameHost;

internal static class GameModuleCatalogTests
{
    public static readonly (string Name, Action Run)[] All =
    [
        ("玩法目录按默认顺序发布模块", DefaultOrder),
        ("玩法目录规范化陈旧与重复的用户顺序", NormalizePreferredOrder),
        ("玩法目录可前后移动模块且限制边界", MoveModule),
        ("玩法目录拒绝重复模块和未声明能力", RejectInvalidModules),
        ("玩法记录存储隔离路径并原子写入和淘汰", RecordStorageIsolation),
        ("玩法记录存储无损且幂等导入旧目录", LegacyRecordImport)
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

    private static void RecordStorageIsolation()
    {
        var root = NewTemporaryRoot();
        try
        {
            var storage = new GameRecordStorage(Path.Combine(root, "games"), new("games.first"), ".pgn", "first");
            var stamp = new DateTimeOffset(2026, 9, 12, 10, 11, 12, TimeSpan.Zero);
            var first = storage.CreatePath(false, stamp); storage.WriteAtomic(first, "one"u8);
            var second = storage.CreatePath(false, stamp); storage.WriteAtomic(second, "two"u8);
            var third = storage.CreatePath(false, stamp); storage.WriteAtomic(third, "three"u8);
            Equal(3, storage.List(false).Count);
            storage.PruneRecent(2);
            Equal(2, storage.List(false).Count);

            var outside = Path.Combine(root, "games", "games.second", "records", "recent", "other.pgn");
            Directory.CreateDirectory(Path.GetDirectoryName(outside)!); File.WriteAllText(outside, "other");
            Throws<UnauthorizedAccessException>(() => storage.ValidatePath(outside));
            Throws<UnauthorizedAccessException>(() => storage.ValidatePath(Path.Combine(Path.GetDirectoryName(first)!, "nested", "bad.pgn")));
        }
        finally { DeleteTemporaryRoot(root); }
    }

    private static void LegacyRecordImport()
    {
        var root = NewTemporaryRoot();
        try
        {
            var legacy = Path.Combine(root, "legacy");
            var sourceDirectory = Path.Combine(legacy, "recent"); Directory.CreateDirectory(sourceDirectory);
            var source = Path.Combine(sourceDirectory, "old.pgn"); File.WriteAllText(source, "legacy-record");
            File.WriteAllText(source + ".meta.json", "legacy-sidecar");

            var storage = new GameRecordStorage(Path.Combine(root, "games"), new("games.first"), ".pgn", "first", legacy);
            var imported = storage.List(false);
            Equal(1, imported.Count);
            Equal("legacy-record", File.ReadAllText(imported[0]));
            Equal("legacy-sidecar", File.ReadAllText(imported[0] + ".meta.json"));
            Equal(true, File.Exists(source));
            Equal(1, new GameRecordStorage(Path.Combine(root, "games"), new("games.first"), ".pgn", "first", legacy).List(false).Count);
        }
        finally { DeleteTemporaryRoot(root); }
    }

    private static IGameModule Module(string id, int order)
    {
        var capability = GameModuleCapabilities.LocalPlay;
        return new FakeModule(new(new(id), new Version(1, 0), id, "测试玩法",
            $"res://{id}.png", capability, order), [new("local", "联机模式", "本地", capability)]);
    }

    private static string Join(IEnumerable<IGameModule> modules) =>
        string.Join(',', modules.Select(module => module.Manifest.Id.Value));

    private static string NewTemporaryRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "xiangqi-game-host-tests", Guid.NewGuid().ToString("N"))).FullName;

    private static void DeleteTemporaryRoot(string root)
    {
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "xiangqi-game-host-tests"));
        var full = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete an unexpected test directory.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }

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
