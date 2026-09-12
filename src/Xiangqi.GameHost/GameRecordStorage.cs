using System.Security.Cryptography;

namespace Xiangqi.GameHost;

/// <summary>
/// Owns the record directories for exactly one gameplay module. It provides bounded names,
/// path containment, same-directory atomic writes, recent-record pruning and a non-destructive
/// legacy import. It does not parse records, decide when a match may be saved, or interpret
/// sidecar metadata.
/// </summary>
public sealed class GameRecordStorage
{
    private const int MaximumImportedFiles = 10_000;
    private readonly string _recordsRoot;
    private readonly string? _legacyRoot;
    private readonly string _extension;
    private readonly string _filePrefix;
    private readonly object _migrationGate = new();
    private bool _migrationChecked;

    public GameRecordStorage(string gamesRoot, GameModuleId moduleId, string extension,
        string filePrefix, string? legacyRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamesRoot);
        _extension = ValidateExtension(extension);
        _filePrefix = ValidateFilePart(filePrefix, nameof(filePrefix));
        var absoluteGamesRoot = Path.GetFullPath(gamesRoot);
        _recordsRoot = Path.Combine(absoluteGamesRoot, moduleId.Value, "records");
        _legacyRoot = string.IsNullOrWhiteSpace(legacyRoot) ? null : Path.GetFullPath(legacyRoot);
    }

    /// <summary>Lists current module records newest first after importing legacy files once.</summary>
    public IReadOnlyList<string> List(bool saved)
    {
        EnsureLegacyImported();
        var directory = DirectoryPath(saved);
        if (!Directory.Exists(directory)) return [];
        return Directory.GetFiles(directory, $"*{_extension}", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
    }

    /// <summary>Allocates a collision-resistant path inside the selected module record directory.</summary>
    public string CreatePath(bool saved, DateTimeOffset? timestamp = null)
    {
        EnsureLegacyImported();
        var directory = DirectoryPath(saved);
        Directory.CreateDirectory(directory);
        var stamp = (timestamp ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss-fff");
        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            var discriminator = suffix == 0 ? string.Empty : $"-{suffix}";
            var path = Path.Combine(directory, $"{_filePrefix}-{stamp}{discriminator}{_extension}");
            if (!File.Exists(path) && !File.Exists(path + ".tmp")) return path;
        }
        throw new IOException("无法为玩法记录分配唯一文件名。");
    }

    /// <summary>
    /// Resolves a record path only when it is an immediate file in this module's recent or saved
    /// directory and has the configured extension. Symbolic/reparse files are rejected.
    /// </summary>
    public string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureLegacyImported();
        var full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(_extension, StringComparison.OrdinalIgnoreCase) ||
            (!IsImmediateChild(full, DirectoryPath(false)) && !IsImmediateChild(full, DirectoryPath(true))))
            throw new UnauthorizedAccessException("记录不在当前玩法的记录目录中。");
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("记录文件不能是重解析点。");
        return full;
    }

    /// <summary>Writes bytes through a same-directory temporary file and never overwrites.</summary>
    public void WriteAtomic(string path, ReadOnlySpan<byte> content)
    {
        var full = ValidatePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 81920, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(true);
            }
            File.Move(temporary, full, false);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    /// <summary>Deletes only excess recent records and delegates sidecar cleanup to the caller.</summary>
    public void PruneRecent(int limit, Action<string>? deleteSidecars = null)
    {
        if (limit is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(limit));
        foreach (var stale in List(false).Skip(limit))
        {
            ValidatePath(stale);
            File.Delete(stale);
            deleteSidecars?.Invoke(stale);
        }
    }

    private string DirectoryPath(bool saved) => Path.Combine(_recordsRoot, saved ? "saved" : "recent");

    private void EnsureLegacyImported()
    {
        if (_migrationChecked) return;
        lock (_migrationGate)
        {
            if (_migrationChecked) return;
            Directory.CreateDirectory(_recordsRoot);
            var marker = Path.Combine(_recordsRoot, ".legacy-import-v1");
            if (_legacyRoot is not null && !File.Exists(marker))
            {
                var imported = 0;
                foreach (var saved in new[] { false, true })
                {
                    var sourceDirectory = Path.Combine(_legacyRoot, saved ? "saved" : "recent");
                    if (!Directory.Exists(sourceDirectory)) continue;
                    var destinationDirectory = DirectoryPath(saved);
                    Directory.CreateDirectory(destinationDirectory);
                    foreach (var source in Directory.GetFiles(sourceDirectory, $"*{_extension}", SearchOption.TopDirectoryOnly))
                    {
                        if (++imported > MaximumImportedFiles) throw new InvalidDataException("旧记录数量超过迁移上限。");
                        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) continue;
                        var destination = ChooseLegacyDestination(source, destinationDirectory);
                        CopyAtomicIfMissing(source, destination);
                        var sourceSidecar = source + ".meta.json";
                        if (File.Exists(sourceSidecar) && (File.GetAttributes(sourceSidecar) & FileAttributes.ReparsePoint) == 0)
                            CopyAtomicIfMissing(sourceSidecar, destination + ".meta.json");
                    }
                }
                var markerTemporary = marker + ".tmp";
                File.WriteAllText(markerTemporary, "Legacy records copied; source files retained.");
                File.Move(markerTemporary, marker, false);
            }
            _migrationChecked = true;
        }
    }

    private string ChooseLegacyDestination(string source, string destinationDirectory)
    {
        var fileName = Path.GetFileName(source);
        var candidate = Path.Combine(destinationDirectory, fileName);
        if (!File.Exists(candidate) || FilesEqual(source, candidate)) return candidate;
        var name = Path.GetFileNameWithoutExtension(fileName);
        for (var suffix = 1; suffix < 10_000; suffix++)
        {
            candidate = Path.Combine(destinationDirectory, $"{name}-legacy-{suffix}{_extension}");
            if (!File.Exists(candidate) || FilesEqual(source, candidate)) return candidate;
        }
        throw new IOException("旧记录迁移时无法分配唯一文件名。");
    }

    private static void CopyAtomicIfMissing(string source, string destination)
    {
        if (File.Exists(destination)) return;
        var temporary = destination + ".importing";
        try
        {
            File.Copy(source, temporary, false);
            File.Move(temporary, destination, false);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length) return false;
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(leftStream), SHA256.HashData(rightStream));
    }

    private static bool IsImmediateChild(string path, string directory) =>
        string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);

    private static string ValidateExtension(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!value.StartsWith('.') || value.Length > 16 || value.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("记录扩展名无效。", nameof(value));
        return value.ToLowerInvariant();
    }

    private static string ValidateFilePart(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 32 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("记录文件名前缀无效。", parameter);
        return value;
    }
}
