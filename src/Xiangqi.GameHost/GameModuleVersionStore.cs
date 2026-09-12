using System.Text.Json;

namespace Xiangqi.GameHost;

/// <summary>Evidence retained for one installed module version; the path is derived, never persisted.</summary>
public sealed record InstalledGameModuleVersion(
    Version Version,
    string PackageSha256,
    string PublisherKeyFingerprint,
    GamePackageExecutionMode ExecutionMode,
    GamePackagePermissions Permissions);

/// <summary>Active, next-start pending and rollback slots for one module.</summary>
public sealed record GameModuleVersionState(
    GameModuleId ModuleId,
    InstalledGameModuleVersion? Active,
    InstalledGameModuleVersion? Pending,
    InstalledGameModuleVersion? Rollback);

/// <summary>
/// Host-owned version switch journal. Stage never changes Active; ApplyPendingAtStartup atomically
/// promotes pending versions and retains the former active version for rollback. It does not load,
/// delete or inspect package files and never touches module records. Public methods are serialized
/// by a lock; returned records are immutable snapshots.
/// </summary>
public sealed class GameModuleVersionStore
{
    private const int MaximumStateBytes = 256 * 1024;
    private readonly string _statePath;
    private readonly object _gate = new();
    private Dictionary<GameModuleId, GameModuleVersionState> _states;

    public GameModuleVersionStore(string moduleRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleRoot);
        var root = Path.GetFullPath(moduleRoot);
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("玩法模块根目录不得是符号链接或重解析点。");
        _statePath = Path.Combine(root, "versions.json");
        _states = Load(_statePath);
    }

    public IReadOnlyList<GameModuleVersionState> Snapshot
    {
        get { lock (_gate) return _states.Values.OrderBy(value => value.ModuleId.Value, StringComparer.Ordinal).ToArray(); }
    }

    public IReadOnlyDictionary<GameModuleId, Version> LatestKnownVersions()
    {
        lock (_gate)
        {
            return _states.Values.Select(state => (state.ModuleId, Version: Newer(state.Active, state.Pending)?.Version))
                .Where(item => item.Version is not null)
                .ToDictionary(item => item.ModuleId, item => item.Version!);
        }
    }

    public void Stage(GamePackageInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        if (!inspection.SignatureValid || inspection.PublisherKeyFingerprint is null)
            throw new InvalidDataException("在线更新必须具有有效发布者签名。");
        var manifest = inspection.Manifest;
        var installed = new InstalledGameModuleVersion(manifest.Module.Version, inspection.PackageSha256,
            inspection.PublisherKeyFingerprint, manifest.ExecutionMode, manifest.Permissions);
        lock (_gate)
        {
            _states.TryGetValue(manifest.Module.Id, out var current);
            if (current?.Active is { } active && Compare(installed.Version, active.Version) <= 0)
                throw new InvalidOperationException("更新版本必须高于当前启用版本；降级只能通过回滚入口。");
            _states[manifest.Module.Id] = new(manifest.Module.Id, current?.Active, installed, current?.Rollback);
            Save();
        }
    }

    /// <summary>Called once before module discovery. Returns IDs whose active version changed.</summary>
    public IReadOnlyList<GameModuleId> ApplyPendingAtStartup()
    {
        lock (_gate)
        {
            var changed = new List<GameModuleId>();
            foreach (var (id, state) in _states.ToArray())
            {
                if (state.Pending is null) continue;
                _states[id] = state with { Active = state.Pending, Pending = null, Rollback = state.Active };
                changed.Add(id);
            }
            if (changed.Count > 0) Save();
            return changed;
        }
    }

    public void RequestRollback(GameModuleId moduleId)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(moduleId, out var state) || state.Rollback is null)
                throw new InvalidOperationException("该玩法没有可回滚版本。");
            _states[moduleId] = state with { Pending = state.Rollback };
            Save();
        }
    }

    private void Save()
    {
        var model = new StateFile(1, _states.Values.Select(state => new StateEntry(
            state.ModuleId.Value, ToData(state.Active), ToData(state.Pending), ToData(state.Rollback))).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model, JsonOptions);
        if (bytes.Length > MaximumStateBytes) throw new InvalidOperationException("玩法版本状态超过大小限制。");
        var temporary = _statePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Dictionary<GameModuleId, GameModuleVersionState> Load(string path)
    {
        if (!File.Exists(path)) return [];
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumStateBytes) throw new InvalidDataException("玩法版本状态大小无效。");
        StateFile model;
        try { model = JsonSerializer.Deserialize<StateFile>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException(); }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        { throw new InvalidDataException("玩法版本状态格式无效。", error); }
        if (model.SchemaVersion != 1 || model.Modules is null || model.Modules.Length > 256)
            throw new InvalidDataException("玩法版本状态版本或数量无效。");
        var result = new Dictionary<GameModuleId, GameModuleVersionState>();
        foreach (var entry in model.Modules)
        {
            var id = new GameModuleId(entry.Id);
            if (!result.TryAdd(id, new(id, FromData(entry.Active), FromData(entry.Pending), FromData(entry.Rollback))))
                throw new InvalidDataException($"玩法版本状态包含重复 ID：{id}。");
        }
        return result;
    }

    private static VersionData? ToData(InstalledGameModuleVersion? value) => value is null ? null : new(
        value.Version.ToString(), value.PackageSha256, value.PublisherKeyFingerprint,
        value.ExecutionMode, value.Permissions);

    private static InstalledGameModuleVersion? FromData(VersionData? value)
    {
        if (value is null) return null;
        if (!Version.TryParse(value.Version, out var version) || version.Minor < 0 ||
            !Enum.IsDefined(value.ExecutionMode) || (value.Permissions & ~AllPermissions) != 0)
            throw new InvalidDataException("玩法版本状态包含非法版本、模式或权限。");
        return new(version, GamePackageReader.NormalizeSha256(value.PackageSha256),
            GamePackageReader.NormalizeSha256(value.PublisherKeyFingerprint), value.ExecutionMode, value.Permissions);
    }

    private static InstalledGameModuleVersion? Newer(InstalledGameModuleVersion? first,
        InstalledGameModuleVersion? second) => first is null ? second : second is null || Compare(first.Version, second.Version) >= 0 ? first : second;

    private static int Compare(Version left, Version right)
    {
        static int Part(int value) => value < 0 ? 0 : value;
        var result = left.Major.CompareTo(right.Major);
        if (result != 0) return result;
        result = left.Minor.CompareTo(right.Minor);
        if (result != 0) return result;
        result = Part(left.Build).CompareTo(Part(right.Build));
        return result != 0 ? result : Part(left.Revision).CompareTo(Part(right.Revision));
    }

    private const GamePackagePermissions AllPermissions = GamePackagePermissions.ModuleStorage |
        GamePackagePermissions.RoomNetwork | GamePackagePermissions.PlayerProfile |
        GamePackagePermissions.ExternalProcess | GamePackagePermissions.NativeCode |
        GamePackagePermissions.UnrestrictedFileSystem;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private sealed record StateFile(int SchemaVersion, StateEntry[] Modules);
    private sealed record StateEntry(string Id, VersionData? Active, VersionData? Pending, VersionData? Rollback);
    private sealed record VersionData(string Version, string PackageSha256, string PublisherKeyFingerprint,
        GamePackageExecutionMode ExecutionMode, GamePackagePermissions Permissions);
}
