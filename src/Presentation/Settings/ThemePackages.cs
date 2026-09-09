using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Godot;

namespace Xiangqi.Presentation.Settings;

/// <summary>
/// Secure XML reader. It uses the platform XML implementation, prohibits DTD/entities, enforces
/// bounded input, validates vocabulary and normalizes every resource beneath the package root.
/// It does not instantiate Godot resources.
/// </summary>
public sealed partial class ThemeManifestReader
{
    public const long MaxManifestBytes = 256 * 1024;
    public const long MaxShaderBytes = 256 * 1024;
    public const long MaxPackageBytes = 64 * 1024 * 1024;
    private static readonly string[] PaletteNames =
    [
        "boardFill", "boardEdge", "gridInk", "pieceFill", "neutralPieceFill", "neutralInk",
        "legalMove", "selection", "moveFrom", "moveTo", "classicRed", "classicBlack",
        "wei", "wu", "shu"
    ];

    public ThemeManifestDto Read(string manifestPath)
    {
        var file = new FileInfo(manifestPath);
        if (!file.Exists) throw new ThemePackageException("缺少 theme.xml。");
        if (file.Length > MaxManifestBytes) throw new ThemePackageException("theme.xml 超过 256 KiB 限制。");
        var root = file.Directory!.FullName;
        ValidatePackage(root);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxManifestBytes,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
        XDocument document;
        using (var stream = File.OpenRead(file.FullName))
        using (var reader = XmlReader.Create(stream, settings))
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);

        var element = document.Root ?? throw new ThemePackageException("清单没有根元素。");
        RequireName(element, "theme");
        if (document.Descendants().Count() > 128 || document.Descendants().Any(e => Depth(e) > 8))
            throw new ThemePackageException("XML 元素数量或嵌套深度超过限制。");
        EnsureAttributes(element, "schemaVersion", "id", "displayName", "inherits");
        if (Required(element, "schemaVersion") != "1") throw new ThemePackageException("仅支持 schemaVersion=1。");
        var id = Required(element, "id");
        if (!IdPattern().IsMatch(id)) throw new ThemePackageException("主题 id 格式无效。");
        var displayName = Required(element, "displayName").Trim();
        if (displayName.Length is < 1 or > 80 || displayName.Any(char.IsControl))
            throw new ThemePackageException("displayName 长度或字符无效。");
        var inherits = Optional(element, "inherits");
        if (inherits is not null && !IdPattern().IsMatch(inherits)) throw new ThemePackageException("inherits 格式无效。");

        Dictionary<string, Color> colors = new(StringComparer.Ordinal);
        Dictionary<ThemeBoardMode, ThemeBoardAssets> boards = new();
        Dictionary<ThemeEffectSlot, ThemeEffectDefinition> effects = new();
        ThemePieceAssets? pieces = null;
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "supports":
                    EnsureAttributes(child, "classic", "threeKingdoms");
                    _ = Boolean(child, "classic", true); _ = Boolean(child, "threeKingdoms", true);
                    break;
                case "palette":
                    EnsureAttributes(child, PaletteNames);
                    foreach (var attribute in child.Attributes())
                    {
                        if (!ColorPattern().IsMatch(attribute.Value))
                            throw Error(child, $"颜色 {attribute.Name.LocalName} 必须是 #RRGGBB 或 #RRGGBBAA。");
                        colors[attribute.Name.LocalName] = new Color(attribute.Value);
                    }
                    break;
                case "board":
                    EnsureAttributes(child, "mode", "texture", "shader");
                    var mode = Required(child, "mode") switch
                    {
                        "classic" => ThemeBoardMode.Classic,
                        "threeKingdoms" => ThemeBoardMode.ThreeKingdoms,
                        _ => throw Error(child, "board.mode 只能是 classic 或 threeKingdoms。")
                    };
                    if (boards.ContainsKey(mode)) throw Error(child, $"board.mode {mode} 重复。");
                    boards[mode] = new(ParseResource(root, Optional(child, "texture"), false),
                        ParseResource(root, Optional(child, "shader"), true));
                    break;
                case "pieces":
                    EnsureAttributes(child, "atlas", "layout", "shader");
                    if (pieces is not null) throw Error(child, "pieces 只能出现一次。");
                    var layout = Required(child, "layout");
                    if (layout != "semantic-v1") throw Error(child, "首版只支持 semantic-v1 atlas。");
                    pieces = new(ParseResource(root, Required(child, "atlas"), false)!, layout,
                        ParseResource(root, Optional(child, "shader"), true));
                    break;
                case "effect":
                    EnsureAttributes(child, "slot", "shader", "durationMs", "loop");
                    if (!Enum.TryParse<ThemeEffectSlot>(Required(child, "slot"), true, out var slot))
                        throw Error(child, "未知 effect.slot。");
                    if (effects.ContainsKey(slot)) throw Error(child, $"effect.slot {slot} 重复。");
                    var duration = Integer(child, "durationMs", 0, 0, 60_000);
                    effects[slot] = new(slot, ParseResource(root, Optional(child, "shader"), true),
                        duration, Boolean(child, "loop", false));
                    break;
                default:
                    throw Error(child, $"未知元素 {child.Name.LocalName}。");
            }
            if (child.HasElements) throw Error(child, $"{child.Name.LocalName} 不允许子元素。");
        }
        return new(id, displayName, inherits, colors, boards, pieces, effects, root);
    }

    private static void ValidatePackage(string root)
    {
        long total = 0;
        var pending = new Stack<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ThemePackageException("主题包不允许符号链接或重解析点。");
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!names.Add(relative)) throw new ThemePackageException($"主题包存在大小写冲突路径：{relative}");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else
                {
                    total += new FileInfo(path).Length;
                    if (total > MaxPackageBytes) throw new ThemePackageException("主题包超过 64 MiB 限制。");
                }
            }
        }
    }

    private static ThemeResourceReference? ParseResource(string root, string? relative, bool shader)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        relative = relative.Replace('\\', '/');
        if (relative.Contains(':') || relative.StartsWith('/') || relative.Split('/').Any(p => p is "" or "." or ".."))
            throw new ThemePackageException($"资源路径 {relative} 不是安全的包内相对路径。");
        var absolute = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(absolute))
            throw new ThemePackageException($"资源不存在或越出主题包：{relative}");
        var info = new FileInfo(absolute);
        if (shader && (info.Extension.ToLowerInvariant() != ".gdshader" || info.Length > MaxShaderBytes))
            throw new ThemePackageException($"shader 必须是 256 KiB 内的 .gdshader：{relative}");
        if (!shader && !new[] { ".png", ".jpg", ".jpeg", ".webp" }.Contains(info.Extension.ToLowerInvariant()))
            throw new ThemePackageException($"纹理格式不受支持：{relative}");
        return new(root, relative, absolute);
    }

    private static int Depth(XElement element) => element.Ancestors().Count() + 1;
    private static void RequireName(XElement element, string name)
    {
        if (element.Name.Namespace != XNamespace.None || element.Name.LocalName != name)
            throw Error(element, $"根元素必须是无命名空间的 <{name}>。");
    }
    private static void EnsureAttributes(XElement element, params string[] allowed)
    {
        if (element.Attributes().Count() > 24) throw Error(element, "属性数量超过限制。");
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        var unknown = element.Attributes().FirstOrDefault(a => a.IsNamespaceDeclaration || !set.Contains(a.Name.LocalName));
        if (unknown is not null) throw Error(element, $"未知属性 {unknown.Name.LocalName}。");
        if (element.Attributes().Any(a => a.Value.Length > 1024)) throw Error(element, "属性文本超过限制。");
    }
    private static string Required(XElement e, string name) =>
        Optional(e, name) ?? throw Error(e, $"缺少属性 {name}。");
    private static string? Optional(XElement e, string name) => e.Attribute(name)?.Value.Trim() is { Length: > 0 } s ? s : null;
    private static bool Boolean(XElement e, string name, bool fallback) => Optional(e, name) switch
    {
        null => fallback, "true" => true, "false" => false, _ => throw Error(e, $"{name} 必须是 true 或 false。")
    };
    private static int Integer(XElement e, string name, int fallback, int min, int max)
    {
        var text = Optional(e, name);
        if (text is null) return fallback;
        if (!int.TryParse(text, out var value) || value < min || value > max)
            throw Error(e, $"{name} 必须在 {min}..{max} 之间。");
        return value;
    }
    private static ThemePackageException Error(XElement e, string message) =>
        new($"theme.xml 第 {(e as IXmlLineInfo)?.LineNumber ?? 0} 行：{message}");

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
    [GeneratedRegex("^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();
}

public sealed record ThemeManifestDto(
    string Id, string DisplayName, string? Inherits,
    IReadOnlyDictionary<string, Color> Colors,
    IReadOnlyDictionary<ThemeBoardMode, ThemeBoardAssets> Boards,
    ThemePieceAssets? Pieces,
    IReadOnlyDictionary<ThemeEffectSlot, ThemeEffectDefinition> Effects,
    string PackageRoot);

public sealed class ThemePackageException(string message) : Exception(message);

/// <summary>
/// Dual-directory catalog. Built-ins are scanned first and cannot be shadowed by user packages.
/// Bad packages are isolated as diagnostics; classic emergency colors remain available.
/// </summary>
public sealed class ThemePackageCatalog : IBoardThemeCatalog
{
    private const int MaxThemes = 128;
    private readonly string _builtInRoot;
    private readonly string _userRoot;
    private BoardThemeCatalog _current = BoardThemeCatalog.Default;
    public IReadOnlyList<IBoardTheme> Themes => _current.Themes;
    public IReadOnlyList<ThemeDiagnostic> Diagnostics => _current.Diagnostics;

    public ThemePackageCatalog(string builtInRoot, string userRoot)
    {
        _builtInRoot = Path.GetFullPath(builtInRoot);
        _userRoot = Path.GetFullPath(userRoot);
        Rescan();
    }

    public static ThemePackageCatalog CreateDefault() => new(
        ResolveBuiltInRoot(), ProjectSettings.GlobalizePath("user://themes"));

    internal static string ResolveBuiltInRoot()
    {
        var developmentRoot = ProjectSettings.GlobalizePath("res://assets/themes");
        if (File.Exists(Path.Combine(developmentRoot, "classic", "theme.xml"))) return developmentRoot;

        // Exported res:// content lives in the PCK and cannot be parsed by System.Xml as a normal
        // path. Materialize the bounded, read-only built-in package set into user data first;
        // player packages remain in the separate user://themes directory.
        var materialized = ProjectSettings.GlobalizePath("user://.builtin-themes-schema1");
        Directory.CreateDirectory(materialized);
        CopyVirtualDirectory("res://assets/themes", materialized);
        return materialized;
    }

    private static void CopyVirtualDirectory(string source, string destination)
    {
        foreach (var fileName in DirAccess.GetFilesAt(source))
        {
            var bytes = Godot.FileAccess.GetFileAsBytes(source.PathJoin(fileName));
            File.WriteAllBytes(Path.Combine(destination, fileName), bytes);
        }
        foreach (var directoryName in DirAccess.GetDirectoriesAt(source))
        {
            var childDestination = Path.Combine(destination, directoryName);
            Directory.CreateDirectory(childDestination);
            CopyVirtualDirectory(source.PathJoin(directoryName), childDestination);
        }
    }

    public IBoardTheme Resolve(string? id) => _current.Resolve(id);
    public IBoardTheme Resolve(BoardSkinKind kind) => _current.Resolve(kind);

    public void Rescan()
    {
        var diagnostics = new List<ThemeDiagnostic>();
        var manifests = new Dictionary<string, ThemeManifestDto>(StringComparer.OrdinalIgnoreCase);
        ScanRoot(_builtInRoot, false, manifests, diagnostics);
        var builtInIds = manifests.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ScanRoot(_userRoot, true, manifests, diagnostics, builtInIds);

        var compiled = new Dictionary<string, IBoardTheme>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in manifests.Keys.ToArray())
        {
            try { Compile(id, manifests, compiled, new HashSet<string>(StringComparer.OrdinalIgnoreCase)); }
            catch (Exception error) { diagnostics.Add(new(manifests[id].PackageRoot, error.Message)); }
        }
        if (!compiled.ContainsKey("classic"))
        {
            compiled["classic"] = BoardThemeCatalog.EmergencyClassic;
            diagnostics.Add(new(_builtInRoot, "内置 classic 加载失败，已使用紧急经典配色。"));
        }
        try { _current = new BoardThemeCatalog(compiled.Values, diagnostics); }
        catch (Exception error)
        {
            diagnostics.Add(new(null, $"目录发布失败：{error.Message}"));
            _current = new BoardThemeCatalog([BoardThemeCatalog.EmergencyClassic], diagnostics);
        }
    }

    private static void ScanRoot(string root, bool user,
        IDictionary<string, ThemeManifestDto> manifests, IList<ThemeDiagnostic> diagnostics,
        ISet<string>? protectedIds = null)
    {
        try { Directory.CreateDirectory(root); }
        catch (Exception error) { diagnostics.Add(new(root, $"无法访问主题目录：{error.Message}")); return; }
        var reader = new ThemeManifestReader();
        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (manifests.Count >= MaxThemes) { diagnostics.Add(new(root, $"主题数量超过 {MaxThemes} 个限制。")); break; }
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new ThemePackageException("主题目录不允许重解析点。");
                var manifest = reader.Read(Path.Combine(directory, "theme.xml"));
                if (user && protectedIds?.Contains(manifest.Id) == true)
                    throw new ThemePackageException($"玩家主题不能覆盖内置主题 {manifest.Id}。");
                if (!manifests.TryAdd(manifest.Id, manifest))
                    throw new ThemePackageException($"主题 id {manifest.Id} 重复。");
            }
            catch (Exception error) { diagnostics.Add(new(directory, error.Message)); }
        }
    }

    private static IBoardTheme Compile(string id,
        IReadOnlyDictionary<string, ThemeManifestDto> manifests,
        IDictionary<string, IBoardTheme> compiled, ISet<string> visiting)
    {
        if (compiled.TryGetValue(id, out var ready)) return ready;
        if (!manifests.TryGetValue(id, out var manifest)) throw new ThemePackageException($"找不到继承主题 {id}。");
        if (!visiting.Add(id)) throw new ThemePackageException($"主题继承存在环：{string.Join(" -> ", visiting)} -> {id}");
        IBoardTheme? parent = manifest.Inherits is null ? null : Compile(manifest.Inherits, manifests, compiled, visiting);
        var palette = BuildPalette(manifest, parent?.Palette);
        var boards = parent?.Boards.ToDictionary(p => p.Key, p => p.Value) ?? new();
        foreach (var pair in manifest.Boards) boards[pair.Key] = pair.Value;
        var effects = parent?.Effects.ToDictionary(p => p.Key, p => p.Value) ?? new();
        foreach (var pair in manifest.Effects) effects[pair.Key] = pair.Value;
        var pieces = manifest.Pieces ?? parent?.Pieces;
        BoardSkinKind? legacy = id.Equals("classic", StringComparison.OrdinalIgnoreCase) ? BoardSkinKind.Classic :
            id.Equals("spring", StringComparison.OrdinalIgnoreCase) ? BoardSkinKind.Spring : null;
        var result = new BoardThemeDefinition(id, legacy, manifest.DisplayName, palette, boards, pieces, effects);
        visiting.Remove(id);
        compiled[id] = result;
        return result;
    }

    private static BoardThemePalette BuildPalette(ThemeManifestDto manifest, BoardThemePalette? parent)
    {
        Color Get(string name, Func<BoardThemePalette, Color> inherited) =>
            manifest.Colors.TryGetValue(name, out var color) ? color : parent is not null ? inherited(parent) :
                throw new ThemePackageException($"基础主题 {manifest.Id} 缺少颜色 {name}。");
        return new(
            Get("boardFill", p => p.BoardFill), Get("boardEdge", p => p.BoardEdge),
            Get("gridInk", p => p.GridInk), Get("pieceFill", p => p.PieceFill),
            Get("neutralPieceFill", p => p.NeutralPieceFill), Get("neutralInk", p => p.NeutralInk),
            Get("legalMove", p => p.LegalMove), Get("selection", p => p.SelectionFeedback),
            Get("moveFrom", p => p.MoveOriginFeedback), Get("moveTo", p => p.MoveDestinationFeedback),
            [Get("classicRed", p => p.ClassicPieceColors[0]), Get("classicBlack", p => p.ClassicPieceColors[1])],
            [Get("wei", p => p.KingdomPieceColors[0]), Get("wu", p => p.KingdomPieceColors[1]),
                Get("shu", p => p.KingdomPieceColors[2])]);
    }
}
