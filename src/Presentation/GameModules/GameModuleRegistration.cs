using Xiangqi.GameHost;

namespace Xiangqi.Presentation.GameModules;

/// <summary>
/// Presentation composition for one discovered module. The public module contract remains free of
/// Godot and callbacks; this host-owned registration binds validated route IDs to foreground UI
/// actions. Callbacks execute synchronously on the Godot main thread and own no screen resources.
/// </summary>
public sealed class GameModuleRegistration : IGameModule
{
    private readonly IReadOnlyDictionary<string, Action> _activators;

    public GameModuleRegistration(GameModuleManifest manifest,
        IEnumerable<(GameModuleRoute Route, Action Activate)> routes)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        ArgumentNullException.ThrowIfNull(routes);
        var materialized = routes.ToArray();
        Routes = materialized.Select(item => item.Route).ToArray();
        _activators = materialized.ToDictionary(item => item.Route.Id, item => item.Activate,
            StringComparer.Ordinal);
    }

    public GameModuleManifest Manifest { get; }
    public IReadOnlyList<GameModuleRoute> Routes { get; }

    public void Activate(string routeId)
    {
        if (!_activators.TryGetValue(routeId, out var activate))
            throw new KeyNotFoundException($"玩法 {Manifest.Id} 没有入口 {routeId}。");
        activate();
    }
}
