namespace Xiangqi.GameHost;

/// <summary>
/// Stable, storage-safe identity of a gameplay module. The value is persisted in gallery order,
/// record roots and package manifests; display names must never be used as an identity.
/// </summary>
public readonly record struct GameModuleId
{
    public const int MaximumLength = 96;

    public GameModuleId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim();
        if (value.Length > MaximumLength || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
            throw new ArgumentException("玩法 ID 只能包含 ASCII 字母、数字、点和连字符，并且必须以字母或数字开头。", nameof(value));
        Value = value.ToLowerInvariant();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

/// <summary>
/// Host-facing capabilities used for discovery and compatibility checks. These flags describe
/// available entry points; they do not grant file, network, process or platform permissions.
/// </summary>
[Flags]
public enum GameModuleCapabilities
{
    None = 0,
    LocalPlay = 1 << 0,
    LanPlay = 1 << 1,
    SteamPlay = 1 << 2,
    RecentRecords = 1 << 3,
    SavedRecords = 1 << 4,
    RecordImport = 1 << 5,
    PositionImport = 1 << 6,
    ComputerPlay = 1 << 7,
    ComputerSettings = 1 << 8
}

/// <summary>
/// Immutable gallery route advertised by a module. Activation remains a host concern so the
/// contract contains no Godot node, platform handle, transport or mutable match object.
/// </summary>
public sealed record GameModuleRoute(
    string Id,
    string Group,
    string Label,
    GameModuleCapabilities Capability,
    int Order = 0);

/// <summary>
/// Validated metadata exposed before a module is activated. Package trust and requested runtime
/// permissions will be added to the install contract; neither is inferred from these fields.
/// </summary>
public sealed record GameModuleManifest(
    GameModuleId Id,
    Version Version,
    string DisplayName,
    string Summary,
    string CardAssetPath,
    GameModuleCapabilities Capabilities,
    int DefaultOrder);

/// <summary>
/// Discovery boundary shared by built-in and future downloaded gameplay modules. It deliberately
/// describes only identity and routes: each module retains its own domain model, controller,
/// protocol, record codec and presentation composition root.
/// </summary>
public interface IGameModule
{
    GameModuleManifest Manifest { get; }
    IReadOnlyList<GameModuleRoute> Routes { get; }
}

/// <summary>
/// Immutable validated module directory. Construction rejects ambiguous identities and routes;
/// ordering tolerates stale user preferences and appends newly installed modules deterministically.
/// Thread-safe after construction and owns no disposable resources.
/// </summary>
public sealed class GameModuleCatalog
{
    private readonly IReadOnlyList<IGameModule> _defaultOrder;
    private readonly IReadOnlyDictionary<GameModuleId, IGameModule> _byId;

    public GameModuleCatalog(IEnumerable<IGameModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var materialized = modules.ToArray();
        if (materialized.Length > 256) throw new ArgumentException("玩法模块数量超过 256。", nameof(modules));

        var byId = new Dictionary<GameModuleId, IGameModule>();
        foreach (var module in materialized)
        {
            ArgumentNullException.ThrowIfNull(module);
            ValidateManifest(module.Manifest);
            if (!byId.TryAdd(module.Manifest.Id, module))
                throw new ArgumentException($"玩法模块 ID 重复：{module.Manifest.Id}。", nameof(modules));
            ValidateRoutes(module);
        }

        _byId = byId;
        _defaultOrder = materialized.OrderBy(module => module.Manifest.DefaultOrder)
            .ThenBy(module => module.Manifest.Id.Value, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<IGameModule> Modules => _defaultOrder;

    public IReadOnlyList<IGameModule> Order(IEnumerable<string>? preferredIds)
    {
        var result = new List<IGameModule>(_defaultOrder.Count);
        var included = new HashSet<GameModuleId>();
        foreach (var value in preferredIds ?? [])
        {
            GameModuleId id;
            try { id = new GameModuleId(value); }
            catch (ArgumentException) { continue; }
            if (_byId.TryGetValue(id, out var module) && included.Add(id)) result.Add(module);
        }
        foreach (var module in _defaultOrder)
            if (included.Add(module.Manifest.Id)) result.Add(module);
        return result;
    }

    public IReadOnlyList<string> Move(IEnumerable<string>? preferredIds, GameModuleId id, int offset)
    {
        if (offset is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(offset));
        if (!_byId.ContainsKey(id)) throw new KeyNotFoundException($"未安装玩法模块：{id}。");

        var persisted = new List<GameModuleId>();
        var included = new HashSet<GameModuleId>();
        foreach (var value in preferredIds ?? [])
        {
            GameModuleId parsed;
            try { parsed = new GameModuleId(value); }
            catch (ArgumentException) { continue; }
            if (included.Add(parsed)) persisted.Add(parsed);
        }
        foreach (var module in _defaultOrder)
            if (included.Add(module.Manifest.Id)) persisted.Add(module.Manifest.Id);

        var installedSlots = persisted.Select((value, index) => (value, index))
            .Where(item => _byId.ContainsKey(item.value)).ToArray();
        var installedIndex = Array.FindIndex(installedSlots, item => item.value == id);
        var target = Math.Clamp(installedIndex + offset, 0, installedSlots.Length - 1);
        if (target != installedIndex)
        {
            var sourceSlot = installedSlots[installedIndex].index;
            var targetSlot = installedSlots[target].index;
            (persisted[sourceSlot], persisted[targetSlot]) = (persisted[targetSlot], persisted[sourceSlot]);
        }
        return persisted.Select(value => value.Value).ToArray();
    }

    private static void ValidateManifest(GameModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(manifest.Version);
        ValidateText(manifest.DisplayName, 64, "玩法名称");
        ValidateText(manifest.Summary, 160, "玩法简介");
        if (string.IsNullOrWhiteSpace(manifest.CardAssetPath) || manifest.CardAssetPath.Length > 256 ||
            manifest.CardAssetPath.Any(char.IsControl))
            throw new ArgumentException("玩法卡片资源路径无效。", nameof(manifest));
    }

    private static void ValidateRoutes(IGameModule module)
    {
        ArgumentNullException.ThrowIfNull(module.Routes);
        if (module.Routes.Count is 0 or > 64)
            throw new ArgumentException($"玩法 {module.Manifest.Id} 的入口数量必须为 1..64。", nameof(module));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in module.Routes)
        {
            ArgumentNullException.ThrowIfNull(route);
            ValidateRouteId(route.Id);
            ValidateText(route.Group, 32, "入口分组");
            ValidateText(route.Label, 64, "入口名称");
            if (route.Capability == GameModuleCapabilities.None ||
                !module.Manifest.Capabilities.HasFlag(route.Capability))
                throw new ArgumentException($"玩法 {module.Manifest.Id} 的入口 {route.Id} 声明了未启用的能力。", nameof(module));
            if (!ids.Add(route.Id))
                throw new ArgumentException($"玩法 {module.Manifest.Id} 的入口 ID 重复：{route.Id}。", nameof(module));
        }
    }

    private static void ValidateRouteId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
            throw new ArgumentException("玩法入口 ID 无效。", nameof(value));
    }

    private static void ValidateText(string value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException($"{field}无效。", nameof(value));
    }
}
