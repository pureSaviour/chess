using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text.Json;
#if !GODOT_ANDROID
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;
#endif

namespace Xiangqi.Presentation.AI;

public enum EnginePackageKind { Rapfi, Pikafish }

public readonly record struct EngineInstallProgress(string Stage, long CompletedBytes, long? TotalBytes);
public readonly record struct EngineInstallResult(string ExecutablePath, string Version, string InstallDirectory);

/// <summary>Installs one of two pinned official AI archives into a caller-provided application-data
/// root. It streams with size limits, verifies SHA-256 before extraction, rejects traversal and
/// oversized entries, and publishes a completed version directory only after all required files
/// exist. It never edits game settings or starts an engine; callers own consent and cancellation,
/// and later resolve only this installer's pinned package. Failed staging directories are removed, while completed installs
/// are reused and never overwritten.</summary>
public sealed class EnginePackageInstaller : IDisposable
{
    private const long MaximumDownloadBytes = 80L * 1024 * 1024;
    private const long MaximumExpandedBytes = 256L * 1024 * 1024;
    private const int MaximumEntries = 512;
    private readonly string _root;
    private readonly HttpClient _http;
    private bool _disposed;

    public EnginePackageInstaller(string installRoot, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(installRoot)) throw new ArgumentException("引擎安装目录不能为空。", nameof(installRoot));
        _root = Path.GetFullPath(installRoot);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Xiangqi-Engine-Installer/1.0");
    }

    public async Task<EngineInstallResult> InstallAsync(EnginePackageKind kind,
        IProgress<EngineInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("一键配置当前仅支持 Windows 桌面版。");
        var package = Describe(kind);
        Directory.CreateDirectory(_root);
        var final = Path.Combine(_root, package.Id, package.Version);
        var existing = FindExecutable(final, package);
        if (existing is not null && File.Exists(Path.Combine(final, "installation.json")) &&
            package.RequiredFiles.All(relative => File.Exists(Path.Combine(final, relative))))
            return new(existing, package.Version, final);

        var parent = Path.GetDirectoryName(final)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{package.Version}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var archivePath = Path.Combine(staging, "package.7z");
        try
        {
            await DownloadVerified(package, archivePath, progress, cancellationToken);
            await ExtractSafely(archivePath, staging, progress, cancellationToken);
            File.Delete(archivePath);
            if (kind == EnginePackageKind.Pikafish)
            {
                var sourceNetwork = Path.Combine(staging, "pikafish.nnue");
                var colocatedNetwork = Path.Combine(staging, "Windows", "pikafish.nnue");
                if (File.Exists(sourceNetwork)) File.Move(sourceNetwork, colocatedNetwork, overwrite: true);
            }
            var executable = FindExecutable(staging, package)
                ?? throw new InvalidDataException($"{package.DisplayName} 官方包中缺少兼容的 Windows 可执行文件。");
            foreach (var required in package.RequiredFiles)
                if (!File.Exists(Path.Combine(staging, required)))
                    throw new InvalidDataException($"{package.DisplayName} 官方包缺少必要文件：{required}");

            var target = Directory.Exists(final)
                ? Path.Combine(parent, $"{package.Version}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}") : final;
            var manifest = JsonSerializer.Serialize(new
            {
                package.Id, package.Version, package.SourceUrl, package.Sha256,
                InstalledAt = DateTimeOffset.UtcNow, Executable = Path.GetRelativePath(staging, executable)
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(staging, "installation.json"), manifest, cancellationToken);
            Directory.Move(staging, target);
            var published = Path.Combine(target, Path.GetRelativePath(staging, executable));
            progress?.Report(new("安装完成", 1, 1));
            return new(published, package.Version, target);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Resolves only the pinned package installed below this installer's managed root.
    /// Arbitrary executable paths and older/unverified package directories are never returned.</summary>
    public bool TryGetInstalled(EnginePackageKind kind, out EngineInstallResult result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var package = Describe(kind);
        var directory = Path.Combine(_root, package.Id, package.Version);
        var executable = FindExecutable(directory, package);
        if (executable is null || !File.Exists(Path.Combine(directory, "installation.json")) ||
            package.RequiredFiles.Any(relative => !File.Exists(Path.Combine(directory, relative))))
        {
            result = default;
            return false;
        }
        result = new(executable, package.Version, directory);
        return true;
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _http.Dispose(); }

    public static string LicenseNotice(EnginePackageKind kind) => kind switch
    {
        EnginePackageKind.Rapfi => "Rapfi 引擎按 GPLv3 发布。本操作会从官方 GitHub 下载固定版本到本机应用数据目录；继续即表示你同意遵守其许可证。",
        EnginePackageKind.Pikafish => "皮卡鱼程序按 GPLv3 发布；随包 pikafish.nnue 权重仅允许合法使用，个人非商业用途免费，商业用途需另行取得授权。继续即表示你已阅读并同意这些条款。",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private async Task DownloadVerified(Package package, string destination,
        IProgress<EngineInstallProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new("正在下载", 0, null));
        using var response = await _http.GetAsync(package.SourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var expectedLength = response.Content.Headers.ContentLength;
        if (expectedLength is <= 0 or > MaximumDownloadBytes) throw new InvalidDataException("引擎包下载大小无效或超过 80 MiB 上限。");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken); if (read == 0) break;
            total += read; if (total > MaximumDownloadBytes) throw new InvalidDataException("引擎包超过 80 MiB 下载上限。");
            hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(new("正在下载", total, expectedLength));
        }
        if (total != expectedLength) throw new InvalidDataException("引擎包下载长度不完整。");
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!actual.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"引擎包 SHA-256 校验失败：{actual}");
    }

    private static async Task ExtractSafely(string archivePath, string destination,
        IProgress<EngineInstallProgress>? progress, CancellationToken cancellationToken)
    {
#if GODOT_ANDROID
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Android 不支持外部引擎包安装。");
#else
        progress?.Report(new("正在校验并解压", 0, null));
        using var archive = SevenZipArchive.OpenArchive(archivePath, new ReaderOptions());
        var rootPrefix = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long total = 0; var count = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory) continue;
            if (++count > MaximumEntries) throw new InvalidDataException("引擎包文件数量超过上限。");
            if (entry.Size < 0 || entry.Size > MaximumExpandedBytes || total + entry.Size > MaximumExpandedBytes)
                throw new InvalidDataException("引擎包展开大小超过 256 MiB 上限。");
            if (string.IsNullOrWhiteSpace(entry.Key)) throw new InvalidDataException("引擎包包含空文件名。");
            var key = entry.Key.Replace('/', Path.DirectorySeparatorChar);
            key = key.Replace('\\', Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(destination, key));
            if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("引擎包包含越目录路径。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.OpenEntryStream();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
            if (output.Length != entry.Size) throw new InvalidDataException($"引擎包文件长度不符：{entry.Key}");
            total += output.Length; progress?.Report(new("正在解压", total, null));
        }
#endif
    }

    private static string? FindExecutable(string directory, Package package)
    {
        if (!Directory.Exists(directory)) return null;
        foreach (var relative in package.ExecutableCandidates)
        {
            var path = Path.Combine(directory, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static Package Describe(EnginePackageKind kind) => kind switch
    {
        EnginePackageKind.Rapfi => new("rapfi", "Rapfi", "250615",
            "https://github.com/dhbloo/rapfi/releases/download/250615/Rapfi-engine.7z",
            "1A3E24024062A153AC079060EE9589A37C6BDD1ECC54FED3908793C519594E05",
            Avx2.IsSupported ? ["pbrain-rapfi-windows-avx2.exe", "pbrain-rapfi-windows-sse.exe"] : ["pbrain-rapfi-windows-sse.exe"],
            ["config.toml", "mix9svqfreestyle_bsmix.bin.lz4"]),
        EnginePackageKind.Pikafish => new("pikafish", "皮卡鱼", "2026-01-02",
            "https://github.com/official-pikafish/Pikafish/releases/download/Pikafish-2026-01-02/Pikafish.2026-01-02.7z",
            "84257063905615919FB4EE6A70273A94843BB6EC04C45E3AC706098838BC1A49",
            Avx2.IsSupported ? [Path.Combine("Windows", "pikafish-avx2.exe"), Path.Combine("Windows", "pikafish-sse41-popcnt.exe")]
                : [Path.Combine("Windows", "pikafish-sse41-popcnt.exe")],
            [Path.Combine("Windows", "pikafish.nnue"), "NNUE-License.md", "Copying.txt"]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private sealed record Package(string Id, string DisplayName, string Version, string SourceUrl,
        string Sha256, string[] ExecutableCandidates, string[] RequiredFiles);
}
