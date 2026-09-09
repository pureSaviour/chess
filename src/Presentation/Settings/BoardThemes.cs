using Godot;

namespace Xiangqi.Presentation.Settings;

/// <summary>Immutable colors consumed by both board projections; never part of game state.</summary>
public sealed record BoardThemePalette(
    Color BoardFill, Color BoardEdge, Color GridInk, Color PieceFill,
    Color NeutralPieceFill, Color NeutralInk, Color LegalMove,
    Color SelectionFeedback, Color MoveOriginFeedback, Color MoveDestinationFeedback,
    IReadOnlyList<Color> ClassicPieceColors, IReadOnlyList<Color> KingdomPieceColors,
    string? BoardShaderPath = null);

public enum ThemeBoardMode { Classic, ThreeKingdoms }

public enum ThemeEffectSlot
{
    Selection, LegalMove, Move, Capture, Landing, MoveFrom, MoveTo, Check, Victory, Defeat
}

/// <summary>A validated file inside one package. AbsolutePath never escapes PackageRoot.</summary>
public sealed record ThemeResourceReference(string PackageRoot, string RelativePath, string AbsolutePath);

public sealed record ThemeBoardAssets(ThemeResourceReference? Texture, ThemeResourceReference? Shader);

/// <summary>
/// Semantic-v1 is a 7-column by 5-row atlas. Columns are general, advisor, elephant, horse,
/// chariot, cannon and soldier; rows are classic-red, classic-black, Wei, Wu and Shu.
/// </summary>
public sealed record ThemePieceAssets(ThemeResourceReference Atlas, string Layout, ThemeResourceReference? Shader);

public sealed record ThemeEffectDefinition(
    ThemeEffectSlot Slot, ThemeResourceReference? Shader, int DurationMs, bool Loop);

/// <summary>
/// Read-only compiled definition. It describes resources but does not load or own Godot objects,
/// nodes, threads, file watchers, rule state or network state.
/// </summary>
public interface IBoardTheme
{
    string Id { get; }
    BoardSkinKind? LegacyKind { get; }
    string DisplayName { get; }
    BoardThemePalette Palette { get; }
    IReadOnlyDictionary<ThemeBoardMode, ThemeBoardAssets> Boards { get; }
    ThemePieceAssets? Pieces { get; }
    IReadOnlyDictionary<ThemeEffectSlot, ThemeEffectDefinition> Effects { get; }
}

public sealed record ThemeDiagnostic(string? PackagePath, string Message, bool IsError = true);

/// <summary>
/// Synchronous lookup used by settings and views. Rescan is explicit and changes definitions for
/// future ApplyAppearance calls only; it never mutates a live match or hot-swaps an animation.
/// </summary>
public interface IBoardThemeCatalog
{
    IReadOnlyList<IBoardTheme> Themes { get; }
    IReadOnlyList<ThemeDiagnostic> Diagnostics { get; }
    IBoardTheme Resolve(string? id);
    IBoardTheme Resolve(BoardSkinKind kind);
    void Rescan();
}

/// <summary>Validated immutable catalog, also used as a deterministic fake by smoke tests.</summary>
public sealed class BoardThemeCatalog : IBoardThemeCatalog
{
    public static BoardThemeCatalog Default { get; } = new(CreateEmergencyBuiltIns());
    private readonly IReadOnlyDictionary<string, IBoardTheme> _themes;
    public IReadOnlyList<IBoardTheme> Themes { get; }
    public IReadOnlyList<ThemeDiagnostic> Diagnostics { get; }

    public BoardThemeCatalog(IEnumerable<IBoardTheme> themes, IReadOnlyList<ThemeDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(themes);
        var values = new Dictionary<string, IBoardTheme>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in themes)
        {
            ArgumentNullException.ThrowIfNull(theme);
            Validate(theme);
            if (!values.TryAdd(theme.Id, theme))
                throw new ArgumentException("Theme IDs must be unique ignoring case.", nameof(themes));
        }
        if (!values.ContainsKey("classic"))
            throw new ArgumentException("The classic fallback theme is required.", nameof(themes));
        _themes = values;
        Themes = values.Values.OrderBy(t => t.Id.Equals("classic", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(t => t.DisplayName, StringComparer.CurrentCulture).ToArray();
        Diagnostics = diagnostics ?? Array.Empty<ThemeDiagnostic>();
    }

    public IBoardTheme Resolve(string? id) =>
        id is not null && _themes.TryGetValue(id, out var theme) ? theme : _themes["classic"];

    public IBoardTheme Resolve(BoardSkinKind kind) => Resolve(kind == BoardSkinKind.Spring ? "spring" : "classic");
    public void Rescan() { }

    private static void Validate(IBoardTheme theme)
    {
        if (string.IsNullOrWhiteSpace(theme.Id) || theme.Id.Length > 64 ||
            !theme.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Theme IDs must be 1-64 ASCII letters, digits, '-' or '_'.");
        if (string.IsNullOrWhiteSpace(theme.DisplayName) || theme.DisplayName.Length > 80)
            throw new ArgumentException("Themes require a display name of at most 80 characters.");
        if (theme.Palette.ClassicPieceColors.Count < 2 || theme.Palette.KingdomPieceColors.Count < 3)
            throw new ArgumentException("Themes require two classic and three kingdom colors.");
        if (theme.Pieces is { Layout: not "semantic-v1" })
            throw new ArgumentException("Only semantic-v1 piece atlases are supported.");
    }

    internal static IBoardTheme EmergencyClassic => CreateEmergencyBuiltIns().First();

    private static IEnumerable<IBoardTheme> CreateEmergencyBuiltIns()
    {
        yield return new BoardThemeDefinition("classic", BoardSkinKind.Classic, "经典木色", new(
            new("#D8B675"), new("#53361F"), new("#5C3A21"), new("#F4DEAE"), new("#B9B3A4"), new("#59564F"),
            new("#4E9B62"), new("#F04444"), new("#F1E7CF"), new("#FFF2A6"),
            [new("#B8322B"), new("#20262B")], [new("#367CC6"), new("#B88B12"), new("#358450")]),
            new Dictionary<ThemeBoardMode, ThemeBoardAssets>(), null,
            new Dictionary<ThemeEffectSlot, ThemeEffectDefinition>());
        yield return new BoardThemeDefinition("spring", BoardSkinKind.Spring, "春日新绿", new(
            new("#E9E2B7"), new("#536333"), new("#46552B"), new("#FFF2C9"), new("#C7C2A8"), new("#5C6151"),
            new("#62A95E"), new("#F06A78"), new("#F7EED1"), new("#FFF0A8"),
            [new("#C33D45"), new("#263E35")], [new("#447FC3"), new("#A97916"), new("#397B4B")]),
            new Dictionary<ThemeBoardMode, ThemeBoardAssets>(), null,
            new Dictionary<ThemeEffectSlot, ThemeEffectDefinition>());
    }
}

public sealed record BoardThemeDefinition(
    string Id, BoardSkinKind? LegacyKind, string DisplayName, BoardThemePalette Palette,
    IReadOnlyDictionary<ThemeBoardMode, ThemeBoardAssets> Boards,
    ThemePieceAssets? Pieces,
    IReadOnlyDictionary<ThemeEffectSlot, ThemeEffectDefinition> Effects) : IBoardTheme;
