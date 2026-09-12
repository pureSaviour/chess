using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Xiangqi.GameHost;

/// <summary>
/// Hard limits applied before a downloaded package can allocate or write significant data. The
/// defaults intentionally favor small board-game modules; callers may lower them, but raising them
/// should be treated as a host policy change rather than package metadata.
/// </summary>
public sealed record GamePackageLimits(
    long MaximumPackageBytes = 128L * 1024 * 1024,
    long MaximumExpandedBytes = 512L * 1024 * 1024,
    long MaximumFileBytes = 128L * 1024 * 1024,
    int MaximumFiles = 2048,
    int MaximumManifestBytes = 256 * 1024,
    int MaximumSignatureBytes = 16 * 1024,
    int MaximumCompressionRatio = 200);

/// <summary>A payload entry whose exact bytes are covered by the signed manifest.</summary>
public sealed record GamePackageFile(string Path, long Length, string Sha256);

/// <summary>
/// Strict version-one package manifest. PublisherPublicKey is a base64 SubjectPublicKeyInfo value;
/// when present, signature.sig must be an ECDSA P-256 DER signature over the exact manifest bytes.
/// </summary>
public sealed record GamePackageManifest(
    int SchemaVersion,
    GameModuleManifest Module,
    IReadOnlyList<GameModuleRoute> Routes,
    Version MinimumHostApi,
    GamePackageExecutionMode ExecutionMode,
    GamePackagePermissions Permissions,
    string Publisher,
    string? PublisherPublicKey,
    IReadOnlyList<GamePackageFile> Files);

/// <summary>
/// Immutable result of inspecting an archive. It contains only validated metadata and hashes; no
/// assembly, script, PCK or other package content has been loaded or executed.
/// </summary>
public sealed record GamePackageInspection(
    GamePackageManifest Manifest,
    string PackageSha256,
    string? PublisherKeyFingerprint,
    bool SignatureValid)
{
    public GamePackageTrustRequest CreateTrustRequest() => new(
        Manifest.Module.Id, Manifest.Module.Version, Manifest.ExecutionMode, Manifest.Permissions,
        PackageSha256, Manifest.Publisher, PublisherKeyFingerprint, SignatureValid);
}

/// <summary>
/// Reads and validates .xqgame ZIP archives without extracting or executing their payload. The
/// reader is synchronous, owns no retained handles, and is safe to call from a background thread.
/// All archive and JSON input is untrusted and failures are reported as InvalidDataException.
/// </summary>
public sealed class GamePackageReader
{
    public const string ManifestPath = "manifest.json";
    public const string SignaturePath = "signature.sig";

    private static readonly HashSet<string> ResourceOnlyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".svg", ".ogg", ".wav", ".mp3",
        ".json", ".txt", ".md", ".ttf", ".otf"
    };

    private readonly Version _hostApiVersion;
    private readonly GamePackageLimits _limits;

    public GamePackageReader(Version hostApiVersion, GamePackageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(hostApiVersion);
        _hostApiVersion = hostApiVersion;
        _limits = limits ?? new GamePackageLimits();
        ValidateLimits(_limits);
    }

    public GamePackageInspection Inspect(string packagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var stream = OpenPackage(packagePath);
            var packageHash = HashStream(stream, cancellationToken);
            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = IndexEntries(archive, cancellationToken);
            var manifestBytes = ReadSpecial(entries, ManifestPath, _limits.MaximumManifestBytes, required: true, cancellationToken)!;
            var signature = ReadSpecial(entries, SignaturePath, _limits.MaximumSignatureBytes, required: false, cancellationToken);
            var manifest = ParseManifest(manifestBytes);
            ValidatePayload(entries, manifest, cancellationToken);
            var (fingerprint, signatureValid) = VerifySignature(manifest, manifestBytes, signature);
            return new GamePackageInspection(manifest, packageHash, fingerprint, signatureValid);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                                      CryptographicException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("玩法包无效或无法安全读取。", error);
        }
    }

    internal FileStream OpenPackage(string packagePath)
    {
        var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > _limits.MaximumPackageBytes)
        {
            stream.Dispose();
            throw new InvalidDataException("玩法包大小超出允许范围。");
        }
        return stream;
    }

    internal IReadOnlyDictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive archive,
        CancellationToken cancellationToken = default)
    {
        if (archive.Entries.Count is 0 || archive.Entries.Count > _limits.MaximumFiles + 32)
            throw new InvalidDataException("玩法包文件数量超出允许范围。");

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = NormalizePackagePath(entry.FullName, allowDirectory: true);
            RejectLink(entry);
            if (!entries.TryAdd(path, entry))
                throw new InvalidDataException($"玩法包包含重复路径：{path}。");
            if (!IsDirectory(entry)) ValidateEntrySize(entry);
        }
        return entries;
    }

    private GamePackageManifest ParseManifest(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        var root = RequireObject(document.RootElement, "manifest");
        RequireProperties(root, "manifest", ["schemaVersion", "id", "version", "displayName", "summary",
            "cardAsset", "defaultOrder", "capabilities", "routes", "minimumHostApi", "executionMode",
            "permissions", "publisher", "publisherPublicKey", "files"]);

        var schema = RequireInt(root, "schemaVersion");
        if (schema != 1) throw new InvalidDataException($"不支持玩法包清单版本 {schema}。");
        var id = new GameModuleId(RequireText(root, "id", GameModuleId.MaximumLength));
        var version = RequireVersion(root, "version");
        var displayName = RequireText(root, "displayName", 64);
        var summary = RequireText(root, "summary", 160);
        var cardAsset = NormalizePackagePath(RequireText(root, "cardAsset", 256));
        var order = RequireInt(root, "defaultOrder");
        var capabilities = ParseFlags(root, "capabilities", CapabilityNames);
        var routes = ParseRoutes(root, capabilities);
        var minimumHostApi = RequireVersion(root, "minimumHostApi");
        if (minimumHostApi > _hostApiVersion)
            throw new InvalidDataException($"玩法包需要宿主 API {minimumHostApi}，当前为 {_hostApiVersion}。");
        var mode = ParseName(root, "executionMode", ExecutionModeNames);
        var permissions = ParseFlags(root, "permissions", PermissionNames);
        var publisher = RequireText(root, "publisher", 128);
        var publicKey = OptionalText(root, "publisherPublicKey", 4096);
        var files = ParseFiles(root);

        if (!files.Any(file => string.Equals(file.Path, cardAsset, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("卡片资源不在清单文件列表中。");
        if (mode == GamePackageExecutionMode.ResourceOnly &&
            files.Any(file => !ResourceOnlyExtensions.Contains(Path.GetExtension(file.Path))))
            throw new InvalidDataException("纯资源玩法包包含不允许的文件类型。");

        var module = new GameModuleManifest(id, version, displayName, summary, cardAsset, capabilities, order);
        _ = new GameModuleCatalog([new ManifestModule(module, routes)]);
        return new GamePackageManifest(schema, module, routes, minimumHostApi, mode, permissions,
            publisher, publicKey, files);
    }

    private IReadOnlyList<GameModuleRoute> ParseRoutes(JsonElement root, GameModuleCapabilities capabilities)
    {
        var array = RequireArray(root, "routes");
        if (array.GetArrayLength() is 0 or > 64) throw new InvalidDataException("入口数量必须为 1..64。");
        var routes = new List<GameModuleRoute>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            var route = RequireObject(item, "route");
            RequireProperties(route, "route", ["id", "group", "label", "capability", "order"]);
            routes.Add(new GameModuleRoute(RequireText(route, "id", 64), RequireText(route, "group", 32),
                RequireText(route, "label", 64), ParseName(route, "capability", CapabilityNames),
                RequireInt(route, "order")));
        }
        if ((routes.Aggregate(GameModuleCapabilities.None, (value, route) => value | route.Capability) & ~capabilities) != 0)
            throw new InvalidDataException("入口声明了清单未启用的能力。");
        return routes;
    }

    private IReadOnlyList<GamePackageFile> ParseFiles(JsonElement root)
    {
        var array = RequireArray(root, "files");
        if (array.GetArrayLength() is 0 || array.GetArrayLength() > _limits.MaximumFiles)
            throw new InvalidDataException("清单文件数量超出允许范围。");
        var result = new List<GamePackageFile>(array.GetArrayLength());
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in array.EnumerateArray())
        {
            var file = RequireObject(item, "file");
            RequireProperties(file, "file", ["path", "length", "sha256"]);
            var path = NormalizePackagePath(RequireText(file, "path", 512));
            if (path.Equals(ManifestPath, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(SignaturePath, StringComparison.OrdinalIgnoreCase) || !paths.Add(path))
                throw new InvalidDataException($"清单包含保留或重复路径：{path}。");
            var length = RequireLong(file, "length");
            if (length < 0 || length > _limits.MaximumFileBytes || total > _limits.MaximumExpandedBytes - length)
                throw new InvalidDataException("清单展开大小超出允许范围。");
            total += length;
            result.Add(new GamePackageFile(path, length, NormalizeSha256(RequireText(file, "sha256", 64))));
        }
        return result;
    }

    private void ValidatePayload(IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        GamePackageManifest manifest, CancellationToken cancellationToken)
    {
        var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entry) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDirectory(entry) || path.Equals(ManifestPath, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(SignaturePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!expected.TryGetValue(path, out var file)) throw new InvalidDataException($"玩法包包含未列入清单的文件：{path}。");
            if (entry.Length != file.Length) throw new InvalidDataException($"文件长度与清单不符：{path}。");
        }
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(file.Path, out var entry) || IsDirectory(entry))
                throw new InvalidDataException($"玩法包缺少清单文件：{file.Path}。");
            else if (!CryptographicOperations.FixedTimeEquals(HashEntry(entry, cancellationToken), Convert.FromHexString(file.Sha256)))
                throw new InvalidDataException($"文件哈希与清单不符：{file.Path}。");
        }
    }

    private (string? Fingerprint, bool Valid) VerifySignature(GamePackageManifest manifest,
        byte[] manifestBytes, byte[]? signature)
    {
        if (manifest.PublisherPublicKey is null)
        {
            if (signature is not null) throw new InvalidDataException("无发布者公钥的包不得包含签名。");
            return (null, false);
        }
        if (signature is null) throw new InvalidDataException("声明发布者公钥的包缺少签名。");
        byte[] keyBytes;
        try { keyBytes = Convert.FromBase64String(manifest.PublisherPublicKey); }
        catch (FormatException error) { throw new InvalidDataException("发布者公钥不是有效 Base64。", error); }
        if (keyBytes.Length is < 64 or > 1024) throw new InvalidDataException("发布者公钥长度无效。");
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(keyBytes, out var consumed);
        if (consumed != keyBytes.Length || key.KeySize != 256)
            throw new InvalidDataException("只支持 ECDSA P-256 发布者密钥。");
        var fingerprint = Convert.ToHexString(SHA256.HashData(keyBytes));
        var valid = key.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        if (!valid) throw new InvalidDataException("玩法包签名验证失败。");
        return (fingerprint, valid);
    }

    private FileStream OpenPackageForInstall(string packagePath) => OpenPackage(packagePath);

    private Dictionary<string, ZipArchiveEntry> IndexEntriesForInstall(ZipArchive archive,
        CancellationToken cancellationToken) =>
        new(IndexEntries(archive, cancellationToken), StringComparer.OrdinalIgnoreCase);

    internal (FileStream Stream, ZipArchive Archive, IReadOnlyDictionary<string, ZipArchiveEntry> Entries,
        GamePackageInspection Inspection) OpenValidatedForInstall(string packagePath, string expectedSha256,
        CancellationToken cancellationToken)
    {
        var stream = OpenPackageForInstall(packagePath);
        try
        {
            var hash = HashStream(stream, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash),
                    Convert.FromHexString(NormalizeSha256(expectedSha256))))
                throw new InvalidDataException("玩法包已在确认后发生变化。");
            stream.Position = 0;
            var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            try
            {
                var entries = IndexEntriesForInstall(archive, cancellationToken);
                var manifestBytes = ReadSpecial(entries, ManifestPath, _limits.MaximumManifestBytes, true, cancellationToken)!;
                var signature = ReadSpecial(entries, SignaturePath, _limits.MaximumSignatureBytes, false, cancellationToken);
                var manifest = ParseManifest(manifestBytes);
                ValidatePayload(entries, manifest, cancellationToken);
                var (fingerprint, valid) = VerifySignature(manifest, manifestBytes, signature);
                return (stream, archive, entries, new GamePackageInspection(manifest, hash, fingerprint, valid));
            }
            catch { archive.Dispose(); throw; }
        }
        catch { stream.Dispose(); throw; }
    }

    internal static string NormalizePackagePath(string value, bool allowDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Contains('\\') ||
            value.StartsWith('/') || value.Contains(':') || value.Any(char.IsControl))
            throw new InvalidDataException("玩法包路径无效。");
        var directory = value.EndsWith('/');
        if (directory && !allowDirectory) throw new InvalidDataException("文件路径不能指向目录。");
        var trimmed = directory ? value[..^1] : value;
        var parts = trimmed.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
            throw new InvalidDataException("玩法包路径包含空段、越目录段或不稳定名称。");
        return directory ? trimmed + "/" : trimmed;
    }

    /// <summary>Validates and normalizes an external SHA-256 hex value for identity comparisons.</summary>
    public static string NormalizeSha256(string value)
    {
        if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("SHA-256 值无效。");
        return value.ToUpperInvariant();
    }

    private void ValidateEntrySize(ZipArchiveEntry entry)
    {
        if (entry.Length < 0 || entry.Length > _limits.MaximumFileBytes)
            throw new InvalidDataException("玩法包单文件大小超出允许范围。");
        if (entry.Length > 0 && entry.CompressedLength == 0)
            throw new InvalidDataException("玩法包包含异常压缩条目。");
        if (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > _limits.MaximumCompressionRatio)
            throw new InvalidDataException("玩法包压缩比超出允许范围。");
    }

    private static byte[]? ReadSpecial(IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string path, int maximumBytes, bool required, CancellationToken cancellationToken)
    {
        if (!entries.TryGetValue(path, out var entry))
        {
            if (required) throw new InvalidDataException($"玩法包缺少 {path}。");
            return null;
        }
        if (IsDirectory(entry) || entry.Length > maximumBytes) throw new InvalidDataException($"{path} 大小无效。");
        using var source = entry.Open();
        using var destination = new MemoryStream((int)entry.Length);
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, count);
            if (destination.Length > maximumBytes) throw new InvalidDataException($"{path} 实际大小无效。");
        }
        if (destination.Length != entry.Length || destination.Length > maximumBytes)
            throw new InvalidDataException($"{path} 实际大小无效。");
        return destination.ToArray();
    }

    private static string HashStream(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static byte[] HashEntry(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        using var stream = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long read = 0;
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read > entry.Length - count) throw new InvalidDataException("压缩条目实际长度超过目录声明。");
            hash.AppendData(buffer, 0, count);
            read += count;
        }
        if (read != entry.Length) throw new InvalidDataException("压缩条目实际长度与目录声明不符。");
        return hash.GetHashAndReset();
    }

    private static void RejectLink(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if (unixType == 0xA000 || windowsAttributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("玩法包不得包含符号链接或重解析点。");
    }

    private static bool IsDirectory(ZipArchiveEntry entry) => entry.FullName.EndsWith('/');

    private static void ValidateLimits(GamePackageLimits limits)
    {
        if (limits.MaximumPackageBytes <= 0 || limits.MaximumExpandedBytes <= 0 || limits.MaximumFileBytes <= 0 ||
            limits.MaximumFiles <= 0 || limits.MaximumManifestBytes <= 0 || limits.MaximumSignatureBytes <= 0 ||
            limits.MaximumCompressionRatio <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    private static readonly IReadOnlyDictionary<string, GameModuleCapabilities> CapabilityNames =
        new Dictionary<string, GameModuleCapabilities>(StringComparer.Ordinal)
        {
            ["local-play"] = GameModuleCapabilities.LocalPlay,
            ["lan-play"] = GameModuleCapabilities.LanPlay,
            ["steam-play"] = GameModuleCapabilities.SteamPlay,
            ["recent-records"] = GameModuleCapabilities.RecentRecords,
            ["saved-records"] = GameModuleCapabilities.SavedRecords,
            ["record-import"] = GameModuleCapabilities.RecordImport,
            ["position-import"] = GameModuleCapabilities.PositionImport,
            ["computer-play"] = GameModuleCapabilities.ComputerPlay,
            ["computer-settings"] = GameModuleCapabilities.ComputerSettings
        };

    private static readonly IReadOnlyDictionary<string, GamePackagePermissions> PermissionNames =
        new Dictionary<string, GamePackagePermissions>(StringComparer.Ordinal)
        {
            ["module-storage"] = GamePackagePermissions.ModuleStorage,
            ["room-network"] = GamePackagePermissions.RoomNetwork,
            ["player-profile"] = GamePackagePermissions.PlayerProfile,
            ["external-process"] = GamePackagePermissions.ExternalProcess,
            ["native-code"] = GamePackagePermissions.NativeCode,
            ["unrestricted-file-system"] = GamePackagePermissions.UnrestrictedFileSystem
        };

    private static readonly IReadOnlyDictionary<string, GamePackageExecutionMode> ExecutionModeNames =
        new Dictionary<string, GamePackageExecutionMode>(StringComparer.Ordinal)
        {
            ["resource-only"] = GamePackageExecutionMode.ResourceOnly,
            ["sandboxed"] = GamePackageExecutionMode.Sandboxed,
            ["full-trust"] = GamePackageExecutionMode.FullTrust
        };

    private static JsonElement RequireObject(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{field} 必须是对象。");
        return element;
    }

    private static JsonElement RequireArray(JsonElement parent, string name)
    {
        var value = RequireProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{name} 必须是数组。");
        return value;
    }

    private static void RequireProperties(JsonElement element, string field, IReadOnlyCollection<string> allowed)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new InvalidDataException($"{field} 包含重复字段 {property.Name}。");
            if (!allowed.Contains(property.Name)) throw new InvalidDataException($"{field} 包含未知字段 {property.Name}。");
        }
        foreach (var required in allowed)
            if (!names.Contains(required)) throw new InvalidDataException($"{field} 缺少字段 {required}。");
    }

    private static JsonElement RequireProperty(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) throw new InvalidDataException($"缺少字段 {name}。");
        return value;
    }

    private static string RequireText(JsonElement parent, string name, int maximumLength)
    {
        var value = RequireProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{name} 必须是字符串。");
        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength || text.Any(char.IsControl))
            throw new InvalidDataException($"{name} 文本无效。");
        return text;
    }

    private static string? OptionalText(JsonElement parent, string name, int maximumLength)
    {
        var value = RequireProperty(parent, name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        return RequireText(parent, name, maximumLength);
    }

    private static int RequireInt(JsonElement parent, string name)
    {
        var value = RequireProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new InvalidDataException($"{name} 必须是 32 位整数。");
        return result;
    }

    private static long RequireLong(JsonElement parent, string name)
    {
        var value = RequireProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
            throw new InvalidDataException($"{name} 必须是整数。");
        return result;
    }

    private static Version RequireVersion(JsonElement parent, string name)
    {
        var text = RequireText(parent, name, 32);
        if (!Version.TryParse(text, out var version) || version.Major < 0 || version.Minor < 0)
            throw new InvalidDataException($"{name} 版本号无效。");
        return version;
    }

    private static T ParseName<T>(JsonElement parent, string name, IReadOnlyDictionary<string, T> names)
    {
        var text = RequireText(parent, name, 64);
        if (!names.TryGetValue(text, out var result)) throw new InvalidDataException($"{name} 包含未知值 {text}。");
        return result;
    }

    private static T ParseFlags<T>(JsonElement parent, string name, IReadOnlyDictionary<string, T> names)
        where T : struct, Enum
    {
        var array = RequireArray(parent, name);
        var bits = 0L;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{name} 只能包含字符串。");
            var text = item.GetString()!;
            if (!found.Add(text) || !names.TryGetValue(text, out var value))
                throw new InvalidDataException($"{name} 包含重复或未知值 {text}。");
            bits |= Convert.ToInt64(value);
        }
        return (T)Enum.ToObject(typeof(T), bits);
    }

    private sealed record ManifestModule(GameModuleManifest Manifest,
        IReadOnlyList<GameModuleRoute> Routes) : IGameModule;
}

/// <summary>
/// Installs an already confirmed package into &lt;root&gt;/&lt;module-id&gt;/&lt;version&gt;. It reopens and
/// revalidates the exact expected hash under a non-writable file share, extracts into a random
/// same-volume staging directory, verifies every payload hash while writing, then atomically moves
/// the completed directory. It never enables, loads, replaces, removes or executes a module.
/// </summary>
public sealed class GamePackageInstaller
{
    private readonly GamePackageReader _reader;

    public GamePackageInstaller(GamePackageReader reader) => _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public string InstallConfirmed(string packagePath, string installRoot, string expectedSha256,
        bool developerMode = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var fullRoot = Path.GetFullPath(installRoot);
        Directory.CreateDirectory(fullRoot);
        RejectReparseDirectory(fullRoot);
        var stagingRoot = Path.Combine(fullRoot, ".staging");
        Directory.CreateDirectory(stagingRoot);
        RejectReparseDirectory(stagingRoot);
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var opened = _reader.OpenValidatedForInstall(packagePath, expectedSha256, cancellationToken);
            using var stream = opened.Stream;
            using var archive = opened.Archive;
            var manifest = opened.Inspection.Manifest;
            var disposition = GamePackageTrustPolicy.Evaluate(opened.Inspection.CreateTrustRequest(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { expectedSha256 }, developerMode);
            if (disposition == GamePackageTrustDisposition.Reject)
                throw new UnauthorizedAccessException("玩法包不满足宿主信任策略，不能安装。");
            WriteSpecial(opened.Entries[GamePackageReader.ManifestPath],
                Path.Combine(staging, GamePackageReader.ManifestPath), cancellationToken);
            if (opened.Entries.TryGetValue(GamePackageReader.SignaturePath, out var signature))
                WriteSpecial(signature, Path.Combine(staging, GamePackageReader.SignaturePath), cancellationToken);
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ResolveContainedPath(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = opened.Entries[file.Path].Open();
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long written = 0;
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (written > file.Length - count) throw new InvalidDataException($"文件实际大小超过清单：{file.Path}。");
                    output.Write(buffer, 0, count);
                    hash.AppendData(buffer, 0, count);
                    written += count;
                }
                if (written != file.Length || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(),
                        Convert.FromHexString(file.Sha256)))
                    throw new InvalidDataException($"文件哈希与清单不符：{file.Path}。");
            }
            var destinationDirectory = Path.Combine(fullRoot, manifest.Module.Id.Value,
                manifest.Module.Version.ToString());
            if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
                throw new IOException("相同玩法版本已经安装。");
            var moduleRoot = Path.GetDirectoryName(destinationDirectory)!;
            Directory.CreateDirectory(moduleRoot);
            RejectReparseDirectory(moduleRoot);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destinationDirectory);
            return destinationDirectory;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void WriteSpecial(ZipArchiveEntry entry, string destination,
        CancellationToken cancellationToken)
    {
        using var input = entry.Open();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, count);
        }
    }

    private static string ResolveContainedPath(string root, string packagePath)
    {
        var normalized = GamePackageReader.NormalizePackagePath(packagePath);
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(root,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("玩法包路径越过安装目录。");
        return destination;
    }

    private static void RejectReparseDirectory(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("玩法安装目录不得是符号链接或重解析点。");
    }
}
