using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Xiangqi.GameHost;

/// <summary>One server-advertised module version covered by the detached feed signature.</summary>
public sealed record GameModuleUpdateEntry(
    GameModuleId ModuleId,
    Version Version,
    Version MinimumHostApi,
    Uri PackageUri,
    long PackageBytes,
    string PackageSha256,
    string PublisherKeyFingerprint,
    bool Official);

/// <summary>Validated immutable snapshot of one signed update feed.</summary>
public sealed record GameModuleUpdateIndex(DateTimeOffset GeneratedAt,
    IReadOnlyList<GameModuleUpdateEntry> Modules);

/// <summary>
/// Verifies a detached ECDSA P-256/SHA-256 DER signature over the exact index JSON bytes, then
/// strictly parses schema 1. It performs no network or filesystem access and rejects unknown or
/// duplicate fields, duplicate module IDs, non-HTTPS URLs, oversized values and incompatible API.
/// </summary>
public sealed class GameUpdateFeedReader
{
    public const int MaximumIndexBytes = 1024 * 1024;
    public const int MaximumSignatureBytes = 16 * 1024;
    private readonly Version _hostApiVersion;

    public GameUpdateFeedReader(Version hostApiVersion) =>
        _hostApiVersion = hostApiVersion ?? throw new ArgumentNullException(nameof(hostApiVersion));

    public GameModuleUpdateIndex Read(ReadOnlySpan<byte> json, ReadOnlySpan<byte> signature,
        string feedPublicKey)
    {
        if (json.Length is 0 or > MaximumIndexBytes || signature.Length is 0 or > MaximumSignatureBytes)
            throw new InvalidDataException("更新索引或签名大小无效。");
        byte[] keyBytes;
        try { keyBytes = Convert.FromBase64String(feedPublicKey); }
        catch (FormatException error) { throw new InvalidDataException("更新索引公钥不是有效 Base64。", error); }
        using var key = ECDsa.Create();
        var consumed = 0;
        try { key.ImportSubjectPublicKeyInfo(keyBytes, out consumed); }
        catch (CryptographicException error) { throw new InvalidDataException("更新索引公钥无效。", error); }
        if (key.KeySize != 256 || consumed != keyBytes.Length ||
            !key.VerifyData(json, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new InvalidDataException("更新索引签名验证失败。");

        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8
            });
            var root = Object(document.RootElement, "index");
            Fields(root, "index", ["schemaVersion", "generatedAt", "modules"]);
            if (Integer(root, "schemaVersion") != 1) throw new InvalidDataException("不支持更新索引版本。");
            if (!DateTimeOffset.TryParse(Text(root, "generatedAt", 64), out var generatedAt))
                throw new InvalidDataException("更新索引时间无效。");
            var modules = Property(root, "modules");
            if (modules.ValueKind != JsonValueKind.Array || modules.GetArrayLength() > 256)
                throw new InvalidDataException("更新索引模块数量无效。");
            var result = new List<GameModuleUpdateEntry>(modules.GetArrayLength());
            var ids = new HashSet<GameModuleId>();
            foreach (var item in modules.EnumerateArray())
            {
                var entry = Object(item, "module");
                Fields(entry, "module", ["id", "version", "minimumHostApi", "packageUrl",
                    "packageBytes", "packageSha256", "publisherKeyFingerprint", "official"]);
                var id = new GameModuleId(Text(entry, "id", GameModuleId.MaximumLength));
                if (!ids.Add(id)) throw new InvalidDataException($"更新索引包含重复模块：{id}。");
                var version = VersionValue(entry, "version");
                var minimumHostApi = VersionValue(entry, "minimumHostApi");
                var uriText = Text(entry, "packageUrl", 2048);
                if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                    throw new InvalidDataException("更新包 URL 必须是无凭据、无片段的 HTTPS 地址。");
                var bytes = Long(entry, "packageBytes");
                if (bytes is <= 0 or > 128L * 1024 * 1024) throw new InvalidDataException("更新包大小超出限制。");
                var hash = GamePackageReader.NormalizeSha256(Text(entry, "packageSha256", 64));
                var fingerprint = GamePackageReader.NormalizeSha256(Text(entry, "publisherKeyFingerprint", 64));
                var official = Boolean(entry, "official");
                if (minimumHostApi <= _hostApiVersion)
                    result.Add(new(id, version, minimumHostApi, uri, bytes, hash, fingerprint, official));
            }
            return new(generatedAt, result);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("更新索引格式无效。", error);
        }
    }

    private static JsonElement Object(JsonElement value, string field) => value.ValueKind == JsonValueKind.Object
        ? value : throw new InvalidDataException($"{field} 必须是对象。");

    private static void Fields(JsonElement value, string field, IReadOnlyCollection<string> allowed)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!found.Add(property.Name)) throw new InvalidDataException($"{field} 包含重复字段 {property.Name}。");
            if (!allowed.Contains(property.Name)) throw new InvalidDataException($"{field} 包含未知字段 {property.Name}。");
        }
        foreach (var required in allowed)
            if (!found.Contains(required)) throw new InvalidDataException($"{field} 缺少字段 {required}。");
    }

    private static JsonElement Property(JsonElement parent, string name) => parent.TryGetProperty(name, out var value)
        ? value : throw new InvalidDataException($"缺少字段 {name}。");

    private static string Text(JsonElement parent, string name, int maximum)
    {
        var value = Property(parent, name);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{name} 必须是字符串。");
        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl))
            throw new InvalidDataException($"{name} 文本无效。");
        return text;
    }

    private static int Integer(JsonElement parent, string name) =>
        Property(parent, name).TryGetInt32(out var value) ? value : throw new InvalidDataException($"{name} 必须是整数。");

    private static long Long(JsonElement parent, string name) =>
        Property(parent, name).TryGetInt64(out var value) ? value : throw new InvalidDataException($"{name} 必须是整数。");

    private static bool Boolean(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"{name} 必须是布尔值。")
        };
    }

    private static Version VersionValue(JsonElement parent, string name)
    {
        var text = Text(parent, name, 32);
        return Version.TryParse(text, out var value) && value.Minor >= 0
            ? value : throw new InvalidDataException($"{name} 版本无效。");
    }
}

public sealed record GameModuleDownloadProgress(long CompletedBytes, long TotalBytes);

/// <summary>A fully downloaded package whose feed identity and package inspection agree.</summary>
public sealed record DownloadedGameModuleUpdate(string PackagePath, GameModuleUpdateEntry Entry,
    GamePackageInspection Inspection);

/// <summary>
/// Separates signed-feed authority from package publisher authority. An entry may call itself
/// official only when its publisher fingerprint is pinned by the host; every non-official package
/// requires an explicit user decision for the new package hash.
/// </summary>
public enum GameModuleUpdateDisposition
{
    Reject,
    AutomaticOfficialDownload,
    RequireThirdPartyConfirmation
}

public static class GameModuleUpdatePolicy
{
    public static GameModuleUpdateDisposition Evaluate(GameModuleUpdateEntry entry,
        IReadOnlySet<string> officialPublisherFingerprints)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(officialPublisherFingerprints);
        if (!entry.Official) return GameModuleUpdateDisposition.RequireThirdPartyConfirmation;
        return officialPublisherFingerprints.Any(value => string.Equals(value,
            entry.PublisherKeyFingerprint, StringComparison.OrdinalIgnoreCase))
            ? GameModuleUpdateDisposition.AutomaticOfficialDownload
            : GameModuleUpdateDisposition.Reject;
    }
}

/// <summary>
/// HTTPS update transport and resumable package downloader. HttpClient is injected and remains
/// caller-owned. Partial files are confined to the supplied download root; a 206 response must
/// start at the requested offset, while a 200 response safely restarts the internal partial file.
/// Completed bytes are revalidated by GamePackageReader before being returned and are not installed.
/// </summary>
public sealed class GameModuleUpdateClient
{
    private readonly HttpClient _http;
    private readonly GameUpdateFeedReader _feedReader;
    private readonly GamePackageReader _packageReader;
    private readonly string _downloadRoot;

    public GameModuleUpdateClient(HttpClient http, Version hostApiVersion, string downloadRoot)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _feedReader = new(hostApiVersion);
        _packageReader = new(hostApiVersion);
        _downloadRoot = Path.GetFullPath(downloadRoot);
    }

    public async Task<GameModuleUpdateIndex> FetchIndexAsync(Uri indexUri, string feedPublicKey,
        CancellationToken cancellationToken = default)
    {
        ValidateHttps(indexUri);
        var signatureUri = new Uri(indexUri.GetLeftPart(UriPartial.Path) + ".sig" + indexUri.Query,
            UriKind.Absolute);
        var index = await GetBoundedAsync(indexUri, GameUpdateFeedReader.MaximumIndexBytes, cancellationToken);
        var signature = await GetBoundedAsync(signatureUri, GameUpdateFeedReader.MaximumSignatureBytes, cancellationToken);
        return _feedReader.Read(index, signature, feedPublicKey);
    }

    public static IReadOnlyList<GameModuleUpdateEntry> FindUpdates(GameModuleUpdateIndex index,
        IReadOnlyDictionary<GameModuleId, Version> installed)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(installed);
        return index.Modules.Where(entry => !installed.TryGetValue(entry.ModuleId, out var version) ||
                                            IsNewer(entry.Version, version))
            .OrderBy(entry => entry.ModuleId.Value, StringComparer.Ordinal).ToArray();
    }

    public static bool IsNewer(Version candidate, Version current) =>
        CompareVersions(candidate, current) > 0;

    public async Task<DownloadedGameModuleUpdate> DownloadAsync(GameModuleUpdateEntry entry,
        IProgress<GameModuleDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateHttps(entry.PackageUri);
        Directory.CreateDirectory(_downloadRoot);
        RejectReparseDirectory(_downloadRoot);
        var stem = $"{entry.ModuleId.Value}-{entry.Version}-{entry.PackageSha256[..16]}";
        var partial = Path.Combine(_downloadRoot, stem + ".partial");
        var complete = Path.Combine(_downloadRoot, stem + ".xqgame");
        if (File.Exists(complete))
        {
            if (new FileInfo(complete).Length != entry.PackageBytes)
                throw new InvalidDataException("缓存包长度与索引不符。");
            var cached = InspectAndMatch(complete, entry, cancellationToken);
            progress?.Report(new(entry.PackageBytes, entry.PackageBytes));
            return new(complete, entry, cached);
        }

        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset < 0 || offset > entry.PackageBytes) offset = 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, entry.PackageUri);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (append && response.Content.Headers.ContentRange?.From != offset)
            throw new InvalidDataException("服务器断点响应起始位置不匹配。");
        if (!append && response.StatusCode != HttpStatusCode.OK)
            response.EnsureSuccessStatusCode();
        if (!append) offset = 0;
        var mode = append ? FileMode.Append : FileMode.Create;
        await using (var destination = new FileStream(partial, mode, FileAccess.Write, FileShare.None,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var buffer = new byte[64 * 1024];
            var total = offset;
            progress?.Report(new(total, entry.PackageBytes));
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (total > entry.PackageBytes - count) throw new InvalidDataException("下载内容超过索引声明大小。");
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                total += count;
                progress?.Report(new(total, entry.PackageBytes));
            }
            if (total != entry.PackageBytes) throw new InvalidDataException("下载内容长度与索引不符。");
        }

        var inspection = InspectAndMatch(partial, entry, cancellationToken);
        File.Move(partial, complete, overwrite: true);
        return new(complete, entry, inspection);
    }

    private GamePackageInspection InspectAndMatch(string path, GameModuleUpdateEntry entry,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != entry.PackageBytes)
            throw new InvalidDataException("玩法包大小与签名索引不一致。");
        var inspection = _packageReader.Inspect(path, cancellationToken);
        if (inspection.Manifest.Module.Id != entry.ModuleId ||
            CompareVersions(inspection.Manifest.Module.Version, entry.Version) != 0 ||
            !string.Equals(inspection.PackageSha256, entry.PackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(inspection.PublisherKeyFingerprint, entry.PublisherKeyFingerprint,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("玩法包身份、版本、哈希或发布者与签名索引不一致。");
        return inspection;
    }

    private static int CompareVersions(Version left, Version right)
    {
        static int Part(int value) => value < 0 ? 0 : value;
        var result = left.Major.CompareTo(right.Major);
        if (result != 0) return result;
        result = left.Minor.CompareTo(right.Minor);
        if (result != 0) return result;
        result = Part(left.Build).CompareTo(Part(right.Build));
        return result != 0 ? result : Part(left.Revision).CompareTo(Part(right.Revision));
    }

    private async Task<byte[]> GetBoundedAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("服务器响应超过大小限制。");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (destination.Length > maximumBytes - count) throw new InvalidDataException("服务器响应超过大小限制。");
            destination.Write(buffer, 0, count);
        }
        return destination.ToArray();
    }

    private static void ValidateHttps(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment)) throw new ArgumentException("更新地址必须是无凭据、无片段的 HTTPS URL。", nameof(uri));
    }

    private static void RejectReparseDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("下载目录不得是符号链接或重解析点。");
    }
}
