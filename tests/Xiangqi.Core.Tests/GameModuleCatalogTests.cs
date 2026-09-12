using Xiangqi.GameHost;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

internal static class GameModuleCatalogTests
{
    public static readonly (string Name, Action Run)[] All =
    [
        ("玩法目录按默认顺序发布模块", DefaultOrder),
        ("玩法目录规范化陈旧与重复的用户顺序", NormalizePreferredOrder),
        ("玩法目录可前后移动模块且限制边界", MoveModule),
        ("玩法目录拒绝重复模块和未声明能力", RejectInvalidModules),
        ("玩法记录存储隔离路径并原子写入和淘汰", RecordStorageIsolation),
        ("玩法记录存储无损且幂等导入旧目录", LegacyRecordImport),
        ("混合信任策略区分沙箱与完全信任代码", PackageTrustPolicy),
        ("玩法包严格扫描并原子安装已确认内容", PackageInspectionAndInstall),
        ("玩法包验证发布者签名并绑定信任证据", SignedPackageInspection),
        ("玩法包拒绝篡改内容和越目录路径", RejectUnsafePackages)
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

    private static void PackageTrustPolicy()
    {
        const string hash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        const string fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
        GamePackageTrustRequest Request(GamePackageExecutionMode mode, GamePackagePermissions permissions,
            bool signed = true) => new(new("thirdparty.example"), new Version(1, 0), mode, permissions,
                hash, "第三方作者", signed ? fingerprint : null, signed);

        Equal(GamePackageTrustDisposition.RequireRestrictedModuleConfirmation,
            GamePackageTrustPolicy.Evaluate(Request(GamePackageExecutionMode.Sandboxed, GamePackagePermissions.ModuleStorage)));
        Equal(GamePackageTrustDisposition.Reject,
            GamePackageTrustPolicy.Evaluate(Request(GamePackageExecutionMode.Sandboxed, GamePackagePermissions.NativeCode)));
        Equal(GamePackageTrustDisposition.Reject,
            GamePackageTrustPolicy.Evaluate(Request(GamePackageExecutionMode.FullTrust, GamePackagePermissions.None, false)));
        Equal(GamePackageTrustDisposition.RequireFullTrustConfirmation,
            GamePackageTrustPolicy.Evaluate(Request(GamePackageExecutionMode.FullTrust, GamePackagePermissions.None)));
        Equal(GamePackageTrustDisposition.AllowPreviouslyConfirmedPackage,
            GamePackageTrustPolicy.Evaluate(Request(GamePackageExecutionMode.FullTrust, GamePackagePermissions.None),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hash }));
        Equal(GamePackageTrustDisposition.RequireFullTrustConfirmation,
            GamePackageTrustPolicy.Evaluate(Request(GamePackageExecutionMode.FullTrust, GamePackagePermissions.None, false),
                developerMode: true));
    }

    private static void PackageInspectionAndInstall()
    {
        var root = NewTemporaryRoot();
        try
        {
            var package = CreatePackage(root, GamePackageExecutionMode.ResourceOnly);
            var reader = new GamePackageReader(new Version(1, 0));
            var inspection = reader.Inspect(package);
            Equal("thirdparty.example", inspection.Manifest.Module.Id.Value);
            Equal(false, inspection.SignatureValid);
            Equal(GamePackageTrustDisposition.RequireRestrictedModuleConfirmation,
                GamePackageTrustPolicy.Evaluate(inspection.CreateTrustRequest()));

            var installRoot = Path.Combine(root, "installed");
            var installed = new GamePackageInstaller(reader).InstallConfirmed(
                package, installRoot, inspection.PackageSha256);
            Equal(true, File.Exists(Path.Combine(installed, "manifest.json")));
            Equal("card", File.ReadAllText(Path.Combine(installed, "content", "card.png")));
            Throws<IOException>(() => new GamePackageInstaller(reader).InstallConfirmed(
                package, installRoot, inspection.PackageSha256));
            Throws<InvalidDataException>(() => new GamePackageInstaller(reader).InstallConfirmed(
                package, Path.Combine(root, "wrong-hash"), new string('0', 64)));
        }
        finally { DeleteTemporaryRoot(root); }
    }

    private static void SignedPackageInspection()
    {
        var root = NewTemporaryRoot();
        try
        {
            using var publisherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var package = CreatePackage(root, GamePackageExecutionMode.FullTrust, publisherKey: publisherKey);
            var inspection = new GamePackageReader(new Version(1, 0)).Inspect(package);
            Equal(true, inspection.SignatureValid);
            Equal(64, inspection.PublisherKeyFingerprint?.Length ?? 0);
            Equal(GamePackageTrustDisposition.RequireFullTrustConfirmation,
                GamePackageTrustPolicy.Evaluate(inspection.CreateTrustRequest()));

            var invalidSignature = CreatePackage(root, GamePackageExecutionMode.FullTrust,
                publisherKey: publisherKey, damageSignature: true, fileName: "bad-signature.xqgame");
            Throws<InvalidDataException>(() => new GamePackageReader(new Version(1, 0)).Inspect(invalidSignature));

            var unsigned = CreatePackage(root, GamePackageExecutionMode.FullTrust,
                fileName: "unsigned-full-trust.xqgame");
            var unsignedInspection = new GamePackageReader(new Version(1, 0)).Inspect(unsigned);
            Throws<UnauthorizedAccessException>(() => new GamePackageInstaller(new(new Version(1, 0)))
                .InstallConfirmed(unsigned, Path.Combine(root, "unsigned-install"), unsignedInspection.PackageSha256));
            var developerInstall = new GamePackageInstaller(new(new Version(1, 0))).InstallConfirmed(unsigned,
                Path.Combine(root, "developer-install"), unsignedInspection.PackageSha256, developerMode: true);
            Equal(true, Directory.Exists(developerInstall));
        }
        finally { DeleteTemporaryRoot(root); }
    }

    private static void RejectUnsafePackages()
    {
        var root = NewTemporaryRoot();
        try
        {
            var wrongHash = CreatePackage(root, GamePackageExecutionMode.ResourceOnly,
                declaredHash: new string('0', 64), fileName: "wrong-hash.xqgame");
            Throws<InvalidDataException>(() => new GamePackageReader(new Version(1, 0)).Inspect(wrongHash));

            var traversal = CreatePackage(root, GamePackageExecutionMode.ResourceOnly,
                payloadPath: "../escape.png", fileName: "traversal.xqgame");
            Throws<InvalidDataException>(() => new GamePackageReader(new Version(1, 0)).Inspect(traversal));

            var executableResource = CreatePackage(root, GamePackageExecutionMode.ResourceOnly,
                payloadPath: "content/unsafe.dll", fileName: "resource-code.xqgame");
            Throws<InvalidDataException>(() => new GamePackageReader(new Version(1, 0)).Inspect(executableResource));

            var duplicateJson = CreatePackage(root, GamePackageExecutionMode.ResourceOnly,
                duplicateSchema: true, fileName: "duplicate-json.xqgame");
            Throws<InvalidDataException>(() => new GamePackageReader(new Version(1, 0)).Inspect(duplicateJson));

            var sizeLimited = CreatePackage(root, GamePackageExecutionMode.ResourceOnly,
                fileName: "size-limit.xqgame");
            Throws<InvalidDataException>(() => new GamePackageReader(new Version(1, 0),
                new GamePackageLimits(MaximumFileBytes: 3)).Inspect(sizeLimited));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => new GamePackageReader(new Version(1, 0))
                .Inspect(sizeLimited, cancellation.Token));
            Throws<OperationCanceledException>(() => new GamePackageInstaller(new(new Version(1, 0)))
                .InstallConfirmed(sizeLimited, Path.Combine(root, "cancelled-install"),
                    new string('0', 64), cancellationToken: cancellation.Token));
        }
        finally { DeleteTemporaryRoot(root); }
    }

    private static string CreatePackage(string root, GamePackageExecutionMode mode,
        ECDsa? publisherKey = null, bool damageSignature = false, string? declaredHash = null,
        string payloadPath = "content/card.png", string fileName = "module.xqgame",
        bool duplicateSchema = false)
    {
        var payload = "card"u8.ToArray();
        var payloadHash = declaredHash ?? Convert.ToHexString(SHA256.HashData(payload));
        var publicKey = publisherKey is null ? null :
            Convert.ToBase64String(publisherKey.ExportSubjectPublicKeyInfo());
        var manifest = new
        {
            schemaVersion = 1,
            id = "thirdparty.example",
            version = "1.0.0",
            displayName = "第三方玩法",
            summary = "用于验证安全安装流程",
            cardAsset = payloadPath,
            defaultOrder = 100,
            capabilities = new[] { "local-play" },
            routes = new[] { new { id = "local", group = "开始游戏", label = "本地对局", capability = "local-play", order = 0 } },
            minimumHostApi = "1.0",
            executionMode = mode switch
            {
                GamePackageExecutionMode.ResourceOnly => "resource-only",
                GamePackageExecutionMode.Sandboxed => "sandboxed",
                _ => "full-trust"
            },
            permissions = Array.Empty<string>(),
            publisher = "测试发布者",
            publisherPublicKey = publicKey,
            files = new[] { new { path = payloadPath, length = payload.LongLength, sha256 = payloadHash } }
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (duplicateSchema)
            manifestBytes = System.Text.Encoding.UTF8.GetBytes(
                System.Text.Encoding.UTF8.GetString(manifestBytes).Replace(
                    "{\"schemaVersion\":1,", "{\"schemaVersion\":1,\"schemaVersion\":1,",
                    StringComparison.Ordinal));
        byte[]? signature = null;
        if (publisherKey is not null)
        {
            signature = publisherKey.SignData(manifestBytes, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            if (damageSignature) signature[^1] ^= 0x01;
        }

        var packagePath = Path.Combine(root, fileName);
        using var stream = new FileStream(packagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteEntry(archive, "manifest.json", manifestBytes);
        if (signature is not null) WriteEntry(archive, "signature.sig", signature);
        WriteEntry(archive, payloadPath, payload);
        return packagePath;
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
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
