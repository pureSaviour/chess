using System.Globalization;
using System.Text;
using Xiangqi.Core.Domain.Go;
using Xiangqi.Core.Domain.Grid;

namespace Xiangqi.Core.Application.Go;

/// <summary>
/// Immutable SGF-compatible Go main line plus the mutually confirmed dead-stone set. Replay creates
/// a fresh aggregate, executes every move and re-runs scoring/resignation; mismatched declared
/// results fail. The value owns no file or live game state.
/// </summary>
public sealed record GoGameRecord(IReadOnlyList<GoAction> Moves, IReadOnlySet<GridPoint> DeadStones,
    GoStone? Winner, GoEndReason? EndReason, GoScore? Score, IReadOnlyDictionary<string, string> Tags)
{
    public static GoGameRecord FromGame(GoGame game, IReadOnlyDictionary<string, string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (game.Phase != GoPhase.Finished) throw new InvalidOperationException("只有已结束的围棋对局可以生成棋谱。");
        var snapshot = game.Snapshot;
        var dead = snapshot.EndReason == GoEndReason.AreaScore
            ? new HashSet<GridPoint>(snapshot.DeadStones) : [];
        return new(game.Actions.Where(x => x.Kind is GoActionKind.Place or GoActionKind.Pass).ToArray(),
            dead, snapshot.Winner, snapshot.EndReason, snapshot.Score,
            tags ?? new Dictionary<string, string>());
    }

    public GoGame Replay()
    {
        var game = new GoGame();
        foreach (var recorded in Moves)
        {
            if (recorded.Kind is not (GoActionKind.Place or GoActionKind.Pass))
                throw new InvalidDataException($"第 {recorded.Sequence} 手类型无效。");
            var result = LocalGoMatchController.Apply(game, recorded.Kind, recorded.Point, recorded.Actor);
            if (!result.IsSuccess || result.Action.Sequence != recorded.Sequence)
                throw new InvalidDataException($"第 {recorded.Sequence} 手无效：{result.Rejection}。");
        }
        if (EndReason == GoEndReason.Resignation)
        {
            if (Winner is null || DeadStones.Count != 0 || Score is not null)
                throw new InvalidDataException("认输棋谱包含无效的终局数据。");
            var result = game.Resign(Winner == GoStone.Black ? GoStone.White : GoStone.Black);
            if (!result.IsSuccess) throw new InvalidDataException("认输结果与棋谱轮次不一致。");
        }
        else if(EndReason==GoEndReason.Timeout)
        {
            if(Winner is null||DeadStones.Count!=0||Score is not null||!game.DeclareTimeout(Winner==GoStone.Black?GoStone.White:GoStone.Black))
                throw new InvalidDataException("超时结果与棋谱不一致。");
        }
        else if (EndReason == GoEndReason.AreaScore)
        {
            if (game.Phase != GoPhase.Scoring) throw new InvalidDataException("数子棋谱未以双方连续虚着结束行棋。");
            foreach (var point in DeadStones.OrderBy(x => x.Row).ThenBy(x => x.Column))
            {
                if (game.Snapshot.DeadStones.Contains(point)) continue;
                var result = game.ToggleDead(point);
                if (!result.IsSuccess) throw new InvalidDataException($"死子标记无效：{point}。");
            }
            var first = game.ConfirmScore();
            var second = game.ConfirmScore();
            if (!first.IsSuccess || !second.IsSuccess) throw new InvalidDataException("终局确认无效。");
            var actual = game.Snapshot;
            if (!actual.DeadStones.SetEquals(DeadStones) || actual.Winner != Winner ||
                Score is null || actual.Score is null || Math.Abs(actual.Score.Value.Black - Score.Value.Black) > .001 ||
                Math.Abs(actual.Score.Value.White - Score.Value.White) > .001)
                throw new InvalidDataException("棋谱声明的死子或数子结果不一致。");
        }
        else if (Winner is not null || Score is not null || DeadStones.Count != 0)
            throw new InvalidDataException("未结束棋谱包含终局数据。");
        return game;
    }
}

/// <summary>
/// Bounded SGF FF[4]/GM[1] codec for 19-line Chinese-rules games. It writes UTF-8 metadata,
/// standard B/W moves (empty value for pass), KM[7.5], RU[Chinese], RE and a final AE dead-stone
/// node. Unknown properties and variations are ignored, while board size, rules, komi, coordinates,
/// move order and declared results are validated by a full domain replay. It performs no file I/O.
/// </summary>
public static class GoSgfCodec
{
    public const int MaximumCharacters = 1_000_000;
    private static readonly string[] CopiedTags = ["PB", "PW", "DT", "EV"];

    public static string Write(GoGameRecord record)
    {
        _ = record.Replay();
        var builder = new StringBuilder("(;FF[4]GM[1]CA[UTF-8]AP[Xiangqi-Gallery:1]SZ[19]RU[Chinese]KM[7.5]");
        foreach (var key in CopiedTags)
            if (record.Tags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                builder.Append(key).Append('[').Append(Escape(value, 128)).Append(']');
        if (record.EndReason is { }) builder.Append("RE[").Append(Result(record)).Append(']');
        foreach (var move in record.Moves)
            builder.Append(';').Append(move.Actor == GoStone.Black ? 'B' : 'W').Append('[')
                .Append(move.Kind == GoActionKind.Pass ? string.Empty : Coordinate(move.Point!.Value)).Append(']');
        if (record.DeadStones.Count > 0)
        {
            builder.Append(";AE");
            foreach (var point in record.DeadStones.OrderBy(x => x.Row).ThenBy(x => x.Column))
                builder.Append('[').Append(Coordinate(point)).Append(']');
        }
        return builder.Append(')').ToString();
    }

    public static bool TryParse(string text, out GoGameRecord? record, out string? error)
    {
        record = null; error = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumCharacters)
        { error = "SGF 为空或超过大小上限。"; return false; }
        try
        {
            var nodes = SplitNodes(text);
            if (nodes.Count == 0) throw new InvalidDataException("SGF 不包含节点。");
            var root = ReadProperties(nodes[0]);
            if (Value(root, "GM") is { } gm && gm != "1") throw new InvalidDataException("仅支持 SGF GM[1] 围棋棋谱。");
            if (Value(root, "SZ") is { } size && size != "19") throw new InvalidDataException("当前只支持标准 19 路围棋。");
            if (Value(root, "RU") is { } rules && !rules.Equals("Chinese", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("当前只支持中国围棋规则。");
            if (Value(root, "KM") is { } komi &&
                (!double.TryParse(komi, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedKomi) ||
                 Math.Abs(parsedKomi - GoGame.StandardKomi) > .001))
                throw new InvalidDataException("当前标准对局固定贴 7.5 目。");

            var moves = new List<GoAction>();
            var dead = new HashSet<GridPoint>();
            foreach (var node in nodes.Skip(1))
            {
                var properties = ReadProperties(node);
                var key = properties.ContainsKey("B") ? "B" : properties.ContainsKey("W") ? "W" : null;
                if (key is not null)
                {
                    var value = properties[key][0];
                    moves.Add(new(moves.Count + 1L, key == "B" ? GoStone.Black : GoStone.White,
                        string.IsNullOrEmpty(value) ? GoActionKind.Pass : GoActionKind.Place,
                        string.IsNullOrEmpty(value) ? null : ParseCoordinate(value)));
                }
                if (properties.TryGetValue("AE", out var removed))
                    foreach (var value in removed) dead.Add(ParseCoordinate(value));
            }
            ParseResult(Value(root, "RE"), out var winner, out var endReason, out var declaredMargin);
            GoScore? score = null;
            if (endReason == GoEndReason.AreaScore)
            {
                var verifier = new GoGame();
                foreach (var move in moves)
                {
                    var applied = LocalGoMatchController.Apply(verifier, move.Kind, move.Point, move.Actor);
                    if (!applied.IsSuccess) throw new InvalidDataException($"第 {move.Sequence} 手无效：{applied.Rejection}。");
                }
                if (verifier.Phase != GoPhase.Scoring) throw new InvalidDataException("数子结果缺少连续两次虚着。");
                foreach (var point in dead.OrderBy(x => x.Row).ThenBy(x => x.Column))
                    if (!verifier.Snapshot.DeadStones.Contains(point) && !verifier.ToggleDead(point).IsSuccess)
                        throw new InvalidDataException("死子标记无效。");
                score = verifier.CalculateScore();
                if (Math.Abs(score.Value.Margin - declaredMargin) > .001 || score.Value.Winner != winner)
                    throw new InvalidDataException("RE 与中国规则数子结果不一致。");
            }
            var tags = root.ToDictionary(x => x.Key, x => x.Value.FirstOrDefault() ?? string.Empty, StringComparer.Ordinal);
            var candidate = new GoGameRecord(moves, dead, winner, endReason, score, tags);
            _ = candidate.Replay(); record = candidate; return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or FormatException or OverflowException)
        { error = exception.Message; return false; }
    }

    private static string Result(GoGameRecord record) => record.EndReason switch
    {
        GoEndReason.Resignation => $"{(record.Winner == GoStone.Black ? "B" : "W")}+R",
        GoEndReason.Timeout => $"{(record.Winner == GoStone.Black ? "B" : "W")}+T",
        GoEndReason.AreaScore when record.Score is { } score && score.Winner is { } winner =>
            $"{(winner == GoStone.Black ? "B" : "W")}+{score.Margin.ToString("0.0", CultureInfo.InvariantCulture)}",
        GoEndReason.AreaScore => "0",
        _ => throw new InvalidDataException("棋谱没有终局结果。")
    };
    private static void ParseResult(string? value, out GoStone? winner, out GoEndReason? reason, out double margin)
    {
        winner = null; reason = null; margin = 0;
        if (string.IsNullOrWhiteSpace(value)) return;
        if (value == "0") { reason = GoEndReason.AreaScore; return; }
        if (value.Length < 3 || value[1] != '+' || value[0] is not ('B' or 'W'))
            throw new InvalidDataException("SGF RE 结果无效。");
        winner = value[0] == 'B' ? GoStone.Black : GoStone.White;
        if (value[2..].Equals("R", StringComparison.OrdinalIgnoreCase)) { reason = GoEndReason.Resignation; return; }
        if (value[2..].Equals("T", StringComparison.OrdinalIgnoreCase)) { reason = GoEndReason.Timeout; return; }
        if (!double.TryParse(value[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out margin) || margin <= 0)
            throw new InvalidDataException("SGF RE 目数无效。");
        reason = GoEndReason.AreaScore;
    }
    private static string Escape(string value, int maximum) => value.Length > maximum
        ? throw new InvalidDataException("SGF 标签过长。")
        : value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
    private static string Coordinate(GridPoint point) => string.Concat((char)('a' + point.Column), (char)('a' + point.Row));
    private static GridPoint ParseCoordinate(string value)
    {
        if (value.Length != 2) throw new InvalidDataException($"非法 SGF 坐标：{value}。");
        var point = new GridPoint(value[0] - 'a', value[1] - 'a');
        if (point.Column is < 0 or >= GoGame.StandardBoardSize || point.Row is < 0 or >= GoGame.StandardBoardSize)
            throw new InvalidDataException($"SGF 坐标越界：{value}。");
        return point;
    }
    private static string? Value(Dictionary<string, List<string>> properties, string key) =>
        properties.TryGetValue(key, out var values) ? values.FirstOrDefault() : null;
    private static List<string> SplitNodes(string text)
    {
        var result = new List<string>(); var depth = 0; var escaped = false; var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; if (depth < 0) throw new InvalidDataException("SGF 括号不匹配。"); continue; }
            if (depth == 1 && c == ';') { if (start >= 0) result.Add(text[start..i]); start = i + 1; }
        }
        if (start >= 0) { var end = text.LastIndexOf(')'); result.Add(text[start..(end >= start ? end : text.Length)]); }
        if (depth != 0) throw new InvalidDataException("SGF 括号不匹配。");
        return result;
    }
    private static Dictionary<string, List<string>> ReadProperties(string node)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal); var i = 0;
        while (i < node.Length)
        {
            while (i < node.Length && !char.IsAsciiLetterUpper(node[i])) i++;
            if (i >= node.Length) break;
            var start = i; while (i < node.Length && char.IsAsciiLetterUpper(node[i])) i++;
            var key = node[start..i]; var values = new List<string>();
            while (i < node.Length && node[i] == '[')
            {
                i++; var value = new StringBuilder(); var closed = false;
                while (i < node.Length)
                {
                    var c = node[i++];
                    if (c == ']') { closed = true; break; }
                    if (c == '\\' && i < node.Length) c = node[i++];
                    value.Append(c);
                }
                if (!closed) throw new InvalidDataException("SGF 属性未闭合。");
                values.Add(value.ToString());
            }
            if (values.Count > 0) result[key] = values;
        }
        return result;
    }
}
