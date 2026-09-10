using System.Text;
using System.Text.RegularExpressions;
using Xiangqi.Core.Domain.Gomoku;
using Xiangqi.Core.Domain.Grid;

namespace Xiangqi.Core.Application.Gomoku;

/// <summary>Immutable SGF-compatible Gomoku main line. Replay creates a fresh aggregate and
/// validates every setup point and move; this value owns no files or mutable game state.</summary>
public sealed record GomokuGameRecord(int BoardSize, GomokuRuleSet Rules, IReadOnlyList<PlacedStone> Setup,
    StoneColor SideToMove, IReadOnlyList<GomokuMove> Moves, IReadOnlyDictionary<string, string> Tags)
{
    public GomokuGame Replay()
    {
        var game = new GomokuGame(BoardSize, Rules, Setup, SideToMove);
        foreach (var recorded in Moves)
        {
            var result = game.TryPlace(recorded.Point, recorded.Color);
            if (!result.IsSuccess) throw new InvalidDataException($"第 {recorded.Sequence} 手无效：{result.Rejection}。");
        }
        return game;
    }
}

/// <summary>
/// Bounded SGF FF[4]/GM[4] text codec. It supports the standard main-line properties SZ, RU,
/// AB, AW, PL, B and W used by Gomoku/Renju; variations and unknown properties are ignored.
/// Parsing performs no file I/O and never returns a record until a full rules replay succeeds.
/// </summary>
public static partial class GomokuSgfCodec
{
    public const int MaximumCharacters = 1_000_000;

    public static string Write(GomokuGameRecord record)
    {
        _ = record.Replay();
        var builder = new StringBuilder($"(;FF[4]GM[4]CA[UTF-8]AP[Xiangqi-Gallery:1]SZ[{record.BoardSize}]RU[{(record.Rules == GomokuRuleSet.RenjuForbiddenMoves ? "Renju" : "Gomoku")}]PL[{Color(record.SideToMove)}]");
        AppendSetup(builder, "AB", record.Setup.Where(x => x.Color == StoneColor.Black).Select(x => x.Point));
        AppendSetup(builder, "AW", record.Setup.Where(x => x.Color == StoneColor.White).Select(x => x.Point));
        foreach (var move in record.Moves) builder.Append(';').Append(Color(move.Color)).Append('[').Append(Coordinate(move.Point)).Append(']');
        return builder.Append(')').ToString();
    }

    public static bool TryParse(string text, out GomokuGameRecord? record, out string? error)
    {
        record = null; error = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumCharacters) { error = "SGF 为空或超过大小上限。"; return false; }
        try
        {
            var nodes = SplitNodes(text);
            if (nodes.Count == 0) throw new InvalidDataException("SGF 不包含节点。");
            var root = ReadProperties(nodes[0]);
            if (Value(root, "GM") is { } gm && gm != "4") throw new InvalidDataException("仅支持 SGF GM[4] 五子棋/连珠。");
            var size = int.TryParse(Value(root, "SZ"), out var parsed) ? parsed : 15;
            _ = new SquareGrid(size);
            var rules = string.Equals(Value(root, "RU"), "Renju", StringComparison.OrdinalIgnoreCase)
                ? GomokuRuleSet.RenjuForbiddenMoves : GomokuRuleSet.Freestyle;
            var setup = new List<PlacedStone>();
            AddSetup(root, "AB", StoneColor.Black, size, setup); AddSetup(root, "AW", StoneColor.White, size, setup);
            var side = string.Equals(Value(root, "PL"), "W", StringComparison.OrdinalIgnoreCase) ? StoneColor.White : StoneColor.Black;
            var moves = new List<GomokuMove>();
            foreach (var node in nodes.Skip(1))
            {
                var properties = ReadProperties(node);
                var key = properties.ContainsKey("B") ? "B" : properties.ContainsKey("W") ? "W" : null;
                if (key is null) continue;
                var color = key == "B" ? StoneColor.Black : StoneColor.White;
                moves.Add(new(moves.Count + 1, color, ParseCoordinate(properties[key][0], size)));
            }
            var tags = root.ToDictionary(x => x.Key, x => x.Value.FirstOrDefault() ?? "", StringComparer.Ordinal);
            var candidate = new GomokuGameRecord(size, rules, setup, side, moves, tags); _ = candidate.Replay();
            record = candidate; return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or ArgumentOutOfRangeException or FormatException)
        { error = exception.Message; return false; }
    }

    private static void AppendSetup(StringBuilder builder, string property, IEnumerable<GridPoint> points)
    { var values = points.ToArray(); if (values.Length == 0) return; builder.Append(property); foreach (var point in values) builder.Append('[').Append(Coordinate(point)).Append(']'); }
    private static string Color(StoneColor color) => color == StoneColor.Black ? "B" : "W";
    private static string Coordinate(GridPoint point) => string.Concat((char)('a' + point.Column), (char)('a' + point.Row));
    private static GridPoint ParseCoordinate(string value, int size)
    {
        if (value.Length != 2) throw new InvalidDataException($"非法 SGF 坐标：{value}。");
        var point = new GridPoint(value[0] - 'a', value[1] - 'a');
        if (point.Column < 0 || point.Row < 0 || point.Column >= size || point.Row >= size) throw new InvalidDataException($"SGF 坐标越界：{value}。");
        return point;
    }
    private static void AddSetup(Dictionary<string, List<string>> properties, string key, StoneColor color, int size, List<PlacedStone> setup)
    { if (!properties.TryGetValue(key, out var values)) return; setup.AddRange(values.Select(value => new PlacedStone(ParseCoordinate(value, size), color))); }
    private static string? Value(Dictionary<string, List<string>> properties, string key) => properties.TryGetValue(key, out var value) ? value.FirstOrDefault() : null;

    private static List<string> SplitNodes(string text)
    {
        var result = new List<string>(); var depth = 0; var escaped = false; var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i]; if (escaped) { escaped = false; continue; } if (c == '\\') { escaped = true; continue; }
            if (c == '(') { depth++; continue; } if (c == ')') { depth--; if (depth < 0) throw new InvalidDataException("SGF 括号不匹配。"); continue; }
            if (depth != 1) continue;
            if (c == ';') { if (start >= 0) result.Add(text[start..i]); start = i + 1; }
        }
        if (start >= 0) { var end = text.LastIndexOf(')'); result.Add(text[start..(end >= start ? end : text.Length)]); }
        if (depth != 0) throw new InvalidDataException("SGF 括号不匹配。"); return result;
    }

    private static Dictionary<string, List<string>> ReadProperties(string node)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal); var i = 0;
        while (i < node.Length)
        {
            while (i < node.Length && !char.IsAsciiLetterUpper(node[i])) i++; if (i >= node.Length) break;
            var start = i; while (i < node.Length && char.IsAsciiLetterUpper(node[i])) i++; var key = node[start..i];
            var values = new List<string>();
            while (i < node.Length && node[i] == '[')
            {
                i++; var value = new StringBuilder(); var closed = false;
                while (i < node.Length) { var c = node[i++]; if (c == ']' ) { closed = true; break; } if (c == '\\' && i < node.Length) c = node[i++]; value.Append(c); }
                if (!closed) throw new InvalidDataException("SGF 属性未闭合。"); values.Add(value.ToString());
            }
            if (values.Count > 0) result[key] = values;
        }
        return result;
    }
}
