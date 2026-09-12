using System.Security.Cryptography;
using System.Text.Json;

namespace Xiangqi.GameHost;

/// <summary>
/// Version-one data-only ruleset supported by the restricted runtime. Players alternately place a
/// mark on an empty rectangular grid; the host, not package code, owns turn and line-win decisions.
/// Values are immutable and contain no paths, delegates, scripts or platform handles.
/// </summary>
public sealed record LinePlacementDefinition(
    int BoardWidth,
    int BoardHeight,
    int WinLength,
    string FirstPlayerLabel,
    string SecondPlayerLabel,
    int FirstPlayer);

/// <summary>One accepted placement with a zero-based player and monotonically increasing ply.</summary>
public sealed record LinePlacementMove(int Row, int Column, int Player, int Ply);

/// <summary>
/// Immutable public state returned by LinePlacementGame. Cells are row-major values: -1 empty,
/// 0 first player, 1 second player. The returned array is a defensive copy.
/// </summary>
public sealed record LinePlacementSnapshot(
    int Width,
    int Height,
    IReadOnlyList<int> Cells,
    int CurrentPlayer,
    int Ply,
    bool Finished,
    int? Winner);

/// <summary>
/// Engine-independent authoritative aggregate for line-placement-v1. Calls are synchronous and
/// must be serialized by the owner; invalid/out-of-turn mutation is rejected before state changes.
/// It owns only a small managed board and requires no disposal.
/// </summary>
public sealed class LinePlacementGame
{
    private readonly int[] _cells;
    private readonly List<LinePlacementMove> _moves = [];

    public LinePlacementGame(LinePlacementDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        ValidateDefinition(definition);
        _cells = Enumerable.Repeat(-1, checked(definition.BoardWidth * definition.BoardHeight)).ToArray();
        CurrentPlayer = definition.FirstPlayer;
    }

    public LinePlacementDefinition Definition { get; }
    public int CurrentPlayer { get; private set; }
    public bool Finished { get; private set; }
    public int? Winner { get; private set; }
    public IReadOnlyList<LinePlacementMove> Moves => _moves.ToArray();

    public LinePlacementSnapshot Snapshot() => new(Definition.BoardWidth, Definition.BoardHeight,
        _cells.ToArray(), CurrentPlayer, _moves.Count, Finished, Winner);

    /// <summary>Places for the current player, returning false without mutation for an occupied cell.</summary>
    public bool TryPlace(int row, int column)
    {
        if (Finished) throw new InvalidOperationException("对局已经结束。");
        if (row < 0 || row >= Definition.BoardHeight || column < 0 || column >= Definition.BoardWidth)
            throw new ArgumentOutOfRangeException(nameof(row), "落点超出棋盘。");
        var index = row * Definition.BoardWidth + column;
        if (_cells[index] != -1) return false;
        var player = CurrentPlayer;
        _cells[index] = player;
        _moves.Add(new(row, column, player, _moves.Count + 1));
        if (HasWinningLine(row, column, player))
        {
            Finished = true;
            Winner = player;
        }
        else if (_moves.Count == _cells.Length)
        {
            Finished = true;
        }
        else CurrentPlayer = 1 - CurrentPlayer;
        return true;
    }

    private bool HasWinningLine(int row, int column, int player)
    {
        foreach (var (rowStep, columnStep) in new[] { (1, 0), (0, 1), (1, 1), (1, -1) })
        {
            var count = 1 + Count(row, column, rowStep, columnStep, player) +
                        Count(row, column, -rowStep, -columnStep, player);
            if (count >= Definition.WinLength) return true;
        }
        return false;
    }

    private int Count(int row, int column, int rowStep, int columnStep, int player)
    {
        var result = 0;
        for (row += rowStep, column += columnStep;
             row >= 0 && row < Definition.BoardHeight && column >= 0 && column < Definition.BoardWidth &&
             _cells[row * Definition.BoardWidth + column] == player;
             row += rowStep, column += columnStep) result++;
        return result;
    }

    internal static void ValidateDefinition(LinePlacementDefinition definition)
    {
        if (definition.BoardWidth is < 3 or > 25 || definition.BoardHeight is < 3 or > 25)
            throw new InvalidDataException("声明式棋盘尺寸必须为 3..25。");
        if (definition.WinLength < 3 || definition.WinLength > Math.Max(definition.BoardWidth, definition.BoardHeight))
            throw new InvalidDataException("连线长度超出棋盘范围。");
        if (definition.FirstPlayer is not (0 or 1)) throw new InvalidDataException("先手玩家无效。");
        ValidateLabel(definition.FirstPlayerLabel);
        ValidateLabel(definition.SecondPlayerLabel);
    }

    private static void ValidateLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 24 || value.Any(char.IsControl))
            throw new InvalidDataException("玩家标签无效。");
    }
}

/// <summary>
/// Fully materialized restricted module. Runtime JSON and card image bytes have been copied from a
/// revalidated signed directory, so later consumers never receive a package-controlled path.
/// </summary>
public sealed record LoadedDeclarativeGameModule(
    GamePackageManifest PackageManifest,
    LinePlacementDefinition Definition,
    ReadOnlyMemory<byte> CardImage,
    string CardImageExtension);

/// <summary>
/// Loads the only executable restricted ABI currently supported: content/runtime.json with kind
/// line-placement-v1. It accepts no assembly/script/native payload, grants no network/process/file
/// API, and materializes bounded inputs before returning. Failures are InvalidDataException and do
/// not return a partial module. The loader is synchronous, thread-safe and owns no resources.
/// </summary>
public sealed class DeclarativeGameModuleLoader
{
    public const string RuntimePath = "content/runtime.json";
    private const int MaximumRuntimeBytes = 64 * 1024;
    private const int MaximumCardBytes = 8 * 1024 * 1024;
    private readonly GamePackageReader _packageReader;

    public DeclarativeGameModuleLoader(Version hostApiVersion) => _packageReader = new(hostApiVersion);

    /// <summary>
    /// Revalidates and materializes one active version. Expected evidence comes from the host-owned
    /// version journal; any mismatch or unsupported ABI fails before a playable module is returned.
    /// </summary>
    public LoadedDeclarativeGameModule Load(string installationRoot, InstalledGameModuleVersion expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var installed = _packageReader.InspectInstalledDirectory(installationRoot, cancellationToken);
        var manifest = installed.Manifest;
        if (!installed.SignatureValid || installed.PublisherKeyFingerprint is null ||
            !string.Equals(installed.PublisherKeyFingerprint, expected.PublisherKeyFingerprint,
                StringComparison.OrdinalIgnoreCase) || manifest.Module.Version != expected.Version ||
            manifest.ExecutionMode != expected.ExecutionMode || manifest.Permissions != expected.Permissions)
            throw new InvalidDataException("活动玩法与版本日志中的签名证据不一致。");
        if (manifest.ExecutionMode != GamePackageExecutionMode.Sandboxed ||
            (manifest.Permissions & ~GamePackagePermissions.ModuleStorage) != 0)
            throw new InvalidDataException("声明式运行时只接受受限模式及可选的玩法独立存储权限。");
        if (manifest.Module.Capabilities != GameModuleCapabilities.LocalPlay || manifest.Routes.Count != 1 ||
            manifest.Routes[0].Capability != GameModuleCapabilities.LocalPlay)
            throw new InvalidDataException("line-placement-v1 仅支持一个本地对局入口。");

        var runtimeFile = FindFile(manifest, RuntimePath, MaximumRuntimeBytes);
        var cardFile = FindFile(manifest, manifest.Module.CardAssetPath, MaximumCardBytes);
        var extension = Path.GetExtension(cardFile.Path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            throw new InvalidDataException("声明式玩法卡片必须是 PNG、JPEG 或 WebP。");
        var runtimeBytes = ReadVerifiedFile(installationRoot, runtimeFile, cancellationToken);
        var cardBytes = ReadVerifiedFile(installationRoot, cardFile, cancellationToken);
        return new(manifest, ParseRuntime(runtimeBytes), cardBytes, extension);
    }

    private static LinePlacementDefinition ParseRuntime(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("runtime 必须是对象。");
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            { "schemaVersion", "kind", "boardWidth", "boardHeight", "winLength", "firstPlayerLabel", "secondPlayerLabel", "firstPlayer" };
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!allowed.Contains(property.Name) || !found.Add(property.Name))
                    throw new InvalidDataException($"runtime 包含未知或重复字段 {property.Name}。");
            if (!allowed.SetEquals(found)) throw new InvalidDataException("runtime 缺少必需字段。");
            if (Integer(root, "schemaVersion") != 1 || Text(root, "kind", 32) != "line-placement-v1")
                throw new InvalidDataException("不支持声明式运行时版本或类型。");
            var definition = new LinePlacementDefinition(Integer(root, "boardWidth"), Integer(root, "boardHeight"),
                Integer(root, "winLength"), Text(root, "firstPlayerLabel", 24),
                Text(root, "secondPlayerLabel", 24), Integer(root, "firstPlayer"));
            LinePlacementGame.ValidateDefinition(definition);
            return definition;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is JsonException or OverflowException)
        { throw new InvalidDataException("声明式 runtime.json 无效。", error); }
    }

    private static int Integer(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number : throw new InvalidDataException($"{name} 必须是整数。");

    private static string Text(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{name} 必须是字符串。");
        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl))
            throw new InvalidDataException($"{name} 文本无效。");
        return text;
    }

    private static GamePackageFile FindFile(GamePackageManifest manifest, string path, int maximum)
    {
        var file = manifest.Files.SingleOrDefault(value =>
            string.Equals(value.Path, path, StringComparison.OrdinalIgnoreCase));
        if (file is null || file.Length is <= 0 or > int.MaxValue || file.Length > maximum)
            throw new InvalidDataException($"声明式玩法缺少有界文件：{path}。");
        return file;
    }

    private static byte[] ReadVerifiedFile(string root, GamePackageFile file,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("声明式玩法文件路径无效。");
        var bytes = new byte[checked((int)file.Length)];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        if (stream.ReadByte() != -1 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes),
                Convert.FromHexString(file.Sha256)))
            throw new InvalidDataException($"声明式玩法文件在加载时发生变化：{file.Path}。");
        return bytes;
    }
}
