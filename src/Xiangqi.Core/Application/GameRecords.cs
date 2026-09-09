using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Xiangqi.Core.Domain;

namespace Xiangqi.Core.Application;

/// <summary>Immutable, verified main-line record for classic Xiangqi.</summary>
public sealed record ClassicGameRecord(
    GameSnapshot InitialPosition,
    IReadOnlyList<ChessMove> Moves,
    IReadOnlyDictionary<string, string> Tags)
{
    public const int MaximumPlyCount = 200_000;

    /// <summary>Replays through ChessGame, returning every position including the initial one.</summary>
    public IReadOnlyList<GameSnapshot> Replay()
    {
        var game = ChessGame.FromPosition(InitialPosition);
        var positions = new List<GameSnapshot>(Moves.Count + 1) { game.Snapshot };
        foreach (var move in Moves)
        {
            var result = game.TryMove(move);
            if (!result.IsSuccess)
                throw new InvalidDataException($"Illegal recorded move {XiangqiUciNotation.FormatMove(move)}: {result.Rejection}.");
            positions.Add(game.Snapshot);
        }
        return positions.AsReadOnly();
    }
}

/// <summary>
/// PGN text codec for Xiangqi. It uses the common Chinese-chess PGN tags, ICCS/UCCI coordinate
/// moves and a Xiangqi FEN setup. Import reads the main line and ignores comments, NAGs and RAV
/// branches; every accepted move is replayed through the authoritative rules.
/// </summary>
public static partial class XiangqiPgnCodec
{
    public const int MaximumTextLength = 4 * 1024 * 1024;
    private static readonly GameSnapshot Standard = ChessGame.CreateStandard().Snapshot;

    public static string Serialize(ClassicGameRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _ = record.Replay();
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Game"] = "Chinese Chess",
            ["Variant"] = "Xiangqi",
            ["Format"] = "ICCS",
            ["Result"] = record.Tags.TryGetValue("Result", out var result) ? result : "*"
        };
        foreach (var pair in record.Tags)
            if (IsSafeTagName(pair.Key) && pair.Value.Length <= 2048) tags[pair.Key] = pair.Value;
        if (!SamePosition(record.InitialPosition, Standard))
        {
            tags["SetUp"] = "1";
            tags["FEN"] = XiangqiUciNotation.ToFen(record.InitialPosition);
        }
        var output = new StringBuilder();
        foreach (var pair in tags.OrderBy(pair => TagOrder(pair.Key)).ThenBy(pair => pair.Key, StringComparer.Ordinal))
            output.Append('[').Append(pair.Key).Append(" \"").Append(Escape(pair.Value)).AppendLine("\"]");
        output.AppendLine();
        for (var i = 0; i < record.Moves.Count; i++)
        {
            if (i % 2 == 0) output.Append(i / 2 + 1).Append(". ");
            output.Append(XiangqiUciNotation.FormatMove(record.Moves[i])).Append(' ');
        }
        output.Append(tags["Result"]).AppendLine();
        return output.ToString();
    }

    public static bool TryParse(string? text, out ClassicGameRecord? record, out string? error)
    {
        record = null; error = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
        { error = "棋谱为空或超过 4 MiB。"; return false; }
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TagRegex().Matches(text))
        {
            if (tags.Count >= 128) { error = "棋谱标签过多。"; return false; }
            tags[match.Groups[1].Value] = Unescape(match.Groups[2].Value);
        }
        if (tags.TryGetValue("Format", out var format) && !format.Equals("ICCS", StringComparison.OrdinalIgnoreCase))
        { error = "当前仅支持 ICCS 坐标着法；WXF/中文纵线记谱需先由外部工具转换。"; return false; }
        var initial = Standard;
        if (tags.TryGetValue("FEN", out var fen) && !XiangqiUciNotation.TryParseFen(fen, out initial, out error)) return false;
        var body = TagRegex().Replace(text, " ");
        body = StripAnnotations(body);
        var moves = new List<ChessMove>();
        foreach (var raw in Regex.Split(body, @"\s+"))
        {
            var token = raw.Trim();
            if (token.Length == 0 || token is "*" or "1-0" or "0-1" or "1/2-1/2" ||
                Regex.IsMatch(token, @"^\d+\.(\.\.)?$")) continue;
            token = Regex.Replace(token, @"^\d+\.(\.\.)?", "");
            token = token.TrimEnd('+', '#', '!', '?');
            if (token.Length == 0) continue;
            if (!XiangqiUciNotation.TryParseMove(token, out var move))
            { error = $"无法识别着法“{token}”。"; return false; }
            if (moves.Count >= ClassicGameRecord.MaximumPlyCount)
            { error = "棋谱着法过多。"; return false; }
            moves.Add(move);
        }
        var candidate = new ClassicGameRecord(initial, Array.AsReadOnly(moves.ToArray()), tags);
        try { _ = candidate.Replay(); }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        { error = exception.Message; return false; }
        record = candidate;
        return true;
    }

    private static string StripAnnotations(string value)
    {
        value = Regex.Replace(value, @"\{[^}]*\}|;[^\r\n]*|\$\d+", " ", RegexOptions.Singleline);
        var output = new StringBuilder(value.Length); var depth = 0;
        foreach (var c in value)
        {
            if (c == '(') { depth++; continue; }
            if (c == ')') { if (depth > 0) depth--; continue; }
            if (depth == 0) output.Append(c);
        }
        return output.ToString();
    }
    private static bool SamePosition(GameSnapshot a, GameSnapshot b) => a.SideToMove == b.SideToMove &&
        a.Pieces.OrderBy(x => x.Position.Rank).ThenBy(x => x.Position.File)
            .Select(x => (x.Position, x.Piece.Side, x.Piece.Kind))
            .SequenceEqual(b.Pieces.OrderBy(x => x.Position.Rank).ThenBy(x => x.Position.File)
                .Select(x => (x.Position, x.Piece.Side, x.Piece.Kind)));
    private static bool IsSafeTagName(string value) => value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string Unescape(string value) => Regex.Unescape(value);
    private static int TagOrder(string key) => key switch { "Event" => 0, "Site" => 1, "Date" => 2, "Round" => 3, "Red" => 4, "Black" => 5, "Result" => 6, "Game" => 7, "Variant" => 8, "Format" => 9, "SetUp" => 10, "FEN" => 11, _ => 20 };

    [GeneratedRegex("""\[\s*([A-Za-z][A-Za-z0-9_]*)\s+"((?:\\.|[^"\\])*)"\s*\]""", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();
}

/// <summary>
/// An isolated what-if branch. It accepts moves for whichever side is to move and never writes
/// back to the authoritative GameSession. The UI may compare BasePly with live state to notify
/// the user that the real opponent moved while analysis continues.
/// </summary>
public sealed class ClassicTrialSession
{
    private readonly ChessGame _game;
    public int BasePly { get; }
    public GameSnapshot Snapshot => _game.Snapshot;
    public bool CanUndo => _game.CanUndo;
    public ClassicTrialSession(GameSnapshot authoritativePosition)
    {
        ArgumentNullException.ThrowIfNull(authoritativePosition);
        BasePly = authoritativePosition.PlyNumber;
        _game = ChessGame.FromPosition(authoritativePosition);
    }
    public IReadOnlyList<BoardPosition> GetLegalDestinations(BoardPosition from) => _game.GetLegalDestinations(from);
    public MoveResult TryMove(BoardPosition from, BoardPosition to) => _game.TryMove(new ChessMove(from, to));
    public bool Undo() => _game.Undo();
}
