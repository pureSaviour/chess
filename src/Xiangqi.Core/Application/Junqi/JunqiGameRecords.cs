using System.Text;
using System.Text.RegularExpressions;
using Xiangqi.Core.Domain.Junqi;

namespace Xiangqi.Core.Application.Junqi;

public readonly record struct JunqiRecordedMove(JunqiSide Side, JunqiMove Move,
    JunqiBattleResult Battle, JunqiSide? FlagRevealedFor);

/// <summary>
/// Immutable AC2 record material. Layouts contain post-game secrets and must not be made available
/// to a live opponent. Replay constructs a new aggregate and rejects any declared combat result
/// that differs from authoritative rules.
/// </summary>
public sealed record JunqiGameRecord(string FirstPlayer, string SecondPlayer, int TimeLimitSeconds,
    int MandatoryAttackPly, IReadOnlyList<JunqiPlacement> FirstLayout,
    IReadOnlyList<JunqiPlacement> SecondLayout, IReadOnlyList<JunqiRecordedMove> Moves,
    JunqiSide? Winner, bool AbnormalEnd = false)
{
    public IReadOnlyList<JunqiSnapshot> Replay()
    {
        var game = new JunqiGame(FirstLayout, SecondLayout, mandatoryAttackPly: MandatoryAttackPly);
        var positions = new List<JunqiSnapshot> { game.Snapshot };
        foreach (var item in Moves)
        {
            if (game.Snapshot.SideToMove != item.Side) throw new InvalidDataException("军旗棋谱行棋方顺序错误。");
            var result = game.TryMove(item.Move);
            var battleMatches = result.Battle == item.Battle ||
                result.Battle == JunqiBattleResult.FlagCaptured && item.Battle == JunqiBattleResult.AttackerWins;
            if (!result.IsSuccess || !battleMatches || result.FlagRevealedFor != item.FlagRevealedFor)
                throw new InvalidDataException($"军旗棋谱着法 {item.Move.From}{item.Move.To} 或裁决结果无效。");
            positions.Add(game.Snapshot);
        }
        if (!AbnormalEnd && Winner is { } winner && (!game.Snapshot.IsFinished || game.Snapshot.Winner != winner))
            throw new InvalidDataException("军旗棋谱胜负与重放结果不一致。");
        if (!AbnormalEnd && game.Snapshot.IsFinished && Winner != game.Snapshot.Winner)
            throw new InvalidDataException("军旗棋谱缺少或错误声明了终局胜方。");
        return positions.AsReadOnly();
    }
}

/// <summary>
/// Strict codec for the AC2 INFO[1.0] two-player Junqi competition record. It uses the standard
/// A-L/0-4 coordinates, a-l piece codes and declared battle outcomes. File encoding is owned by
/// the presentation store; this codec only accepts and returns bounded .NET text.
/// </summary>
public static partial class JunqiAc2Codec
{
    public const string Version = "1.0";
    public const int MaximumTextLength = 4 * 1024 * 1024;
    private static readonly Regex Tags = TagsRegex();

    public static string Serialize(JunqiGameRecord record)
    {
        ArgumentNullException.ThrowIfNull(record); _ = record.Replay();
        ValidateText(record.FirstPlayer, nameof(record.FirstPlayer)); ValidateText(record.SecondPlayer, nameof(record.SecondPlayer));
        var builder = new StringBuilder("(\n");
        builder.Append(";INFO[1.0]PA[").Append(Escape(record.FirstPlayer)).Append("]PB[")
            .Append(Escape(record.SecondPlayer)).Append("]TL[").Append(record.TimeLimitSeconds)
            .Append("]SL[").Append(record.MandatoryAttackPly).AppendLine("]");
        builder.Append(";AA[").Append(Layout(record.FirstLayout, JunqiSide.First)).AppendLine("]");
        builder.Append(";AB[").Append(Layout(record.SecondLayout, JunqiSide.Second)).AppendLine("]");
        var exposed = new HashSet<JunqiSide>();
        foreach (var item in record.Moves)
        {
            if (item.FlagRevealedFor is { } reveal) exposed.Add(reveal);
            var prefix = item.Side == JunqiSide.First ? "MA" : "MB";
            var result = item.Side == JunqiSide.First ? "RA" : "RB";
            var flagSide = item.Side.Opponent(); var flag = item.Side == JunqiSide.First ? "FB" : "FA";
            builder.Append(';').Append(prefix).Append('[').Append(item.Move.From).Append(item.Move.To).Append(']')
                .Append(result).Append('[').Append(ResultCode(item.Battle)).Append(']')
                .Append(flag).Append('[').Append(exposed.Contains(flagSide) ? FlagCell(record, flagSide).ToString() : "00")
                .AppendLine("]");
        }
        if (record.AbnormalEnd) builder.AppendLine(";EA[2]EB[2]");
        else if (record.Winner is { } winner)
            builder.Append(";EA[").Append(winner == JunqiSide.First ? 1 : 0)
                .Append("]EB[").Append(winner == JunqiSide.Second ? 1 : 0).AppendLine("]");
        builder.AppendLine(")"); return builder.ToString();
    }

    public static bool TryParse(string? text, out JunqiGameRecord? record, out string? error)
    {
        record = null; error = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
        { error = "军旗棋谱为空或超过 4 MiB。"; return false; }
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '(' || trimmed[^1] != ')')
        { error = "军旗棋谱必须使用圆括号包围。"; return false; }
        string? pa = null, pb = null, aa = null, ab = null; var tl = 0; var sl = 0;
        JunqiSide? winner = null; var abnormal = false; var moves = new List<JunqiRecordedMove>();
        try
        {
            foreach (var raw in trimmed[1..^1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var values = Tags.Matches(raw).ToDictionary(x => x.Groups[1].Value,
                    x => Unescape(x.Groups[2].Value), StringComparer.Ordinal);
                if (values.Count == 0 || string.Concat(Tags.Matches(raw).Select(x => x.Value)) != raw.Trim())
                    throw new InvalidDataException("军旗棋谱存在未识别内容。");
                if (values.TryGetValue("INFO", out var info))
                {
                    if (info != Version || !values.TryGetValue("PA", out pa) || !values.TryGetValue("PB", out pb) ||
                        !values.TryGetValue("TL", out var rawTl) || !int.TryParse(rawTl, out tl) || tl < 0 ||
                        !values.TryGetValue("SL", out var rawSl) || !int.TryParse(rawSl, out sl) || sl <= 0)
                        throw new InvalidDataException("军旗 INFO[1.0] 头无效。");
                    ValidateText(pa, "PA"); ValidateText(pb, "PB");
                }
                else if (values.TryGetValue("AA", out var first)) aa = first;
                else if (values.TryGetValue("AB", out var second)) ab = second;
                else if (values.TryGetValue("MA", out var ma) || values.TryGetValue("MB", out ma))
                {
                    var side = values.ContainsKey("MA") ? JunqiSide.First : JunqiSide.Second;
                    var resultTag = side == JunqiSide.First ? "RA" : "RB";
                    var flagTag = side == JunqiSide.First ? "FB" : "FA";
                    if (ma.Length != 4 || !JunqiCell.TryParse(ma[..2], out var from) || !JunqiCell.TryParse(ma[2..], out var to) ||
                        !values.TryGetValue(resultTag, out var rawResult) || !TryResult(rawResult, out var battle) ||
                        !values.TryGetValue(flagTag, out var rawFlag) || rawFlag != "00" && !JunqiCell.TryParse(rawFlag, out _))
                        throw new InvalidDataException("军旗着法编码无效。");
                    moves.Add(new(side, new(from, to), battle, rawFlag == "00" ? null : side.Opponent()));
                }
                else if (values.TryGetValue("EA", out var ea) && values.TryGetValue("EB", out var eb))
                {
                    if (ea == "2" && eb == "2") abnormal = true;
                    else if (ea == "1" && eb == "0") winner = JunqiSide.First;
                    else if (ea == "0" && eb == "1") winner = JunqiSide.Second;
                    else throw new InvalidDataException("军旗胜负编码无效。");
                }
                else if (!values.ContainsKey("MARK")) throw new InvalidDataException("军旗棋谱属性组无效。");
            }
            if (pa is null || pb is null || aa is null || ab is null || sl <= 0)
                throw new InvalidDataException("军旗棋谱缺少头或布阵。");
            var candidate = new JunqiGameRecord(pa, pb, tl, sl,
                JunqiGame.Build(JunqiSide.First, aa, 1), JunqiGame.Build(JunqiSide.Second, ab, 26),
                moves.AsReadOnly(), winner, abnormal);
            _ = candidate.Replay(); record = candidate; return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
        { error = exception.Message; return false; }
    }

    private static string Layout(IReadOnlyList<JunqiPlacement> layout, JunqiSide side)
    {
        var byCell = layout.Where(x => x.Piece.Side == side).ToDictionary(x => x.Cell);
        return new string(JunqiTopology.Instance.SetupCells(side).Select(cell =>
            byCell.TryGetValue(cell, out var value) ? JunqiGame.Code(value.Piece.Kind) : throw new InvalidDataException("布阵不完整。")).ToArray());
    }
    private static JunqiCell FlagCell(JunqiGameRecord record, JunqiSide side) =>
        (side == JunqiSide.First ? record.FirstLayout : record.SecondLayout).Single(x => x.Piece.Kind == JunqiPieceKind.Flag).Cell;
    private static int ResultCode(JunqiBattleResult result) => result switch
    { JunqiBattleResult.DefenderWins => 0, JunqiBattleResult.AttackerWins or JunqiBattleResult.FlagCaptured => 1,
      JunqiBattleResult.BothRemoved => 2, JunqiBattleResult.MoveOnly => 3, _ => throw new ArgumentOutOfRangeException(nameof(result)) };
    private static bool TryResult(string value, out JunqiBattleResult result)
    {
        result = value switch { "0" => JunqiBattleResult.DefenderWins, "1" => JunqiBattleResult.AttackerWins,
            "2" => JunqiBattleResult.BothRemoved, "3" => JunqiBattleResult.MoveOnly, _ => (JunqiBattleResult)255 };
        return (byte)result != 255;
    }
    private static void ValidateText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl) || value.Contains('[') || value.Contains(']'))
            throw new ArgumentException("军旗棋谱文本字段无效。", name);
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("]", "\\]");
    private static string Unescape(string value) => value.Replace("\\]", "]").Replace("\\\\", "\\");
    [GeneratedRegex(@"([A-Z]+)\[((?:\\.|[^\]])*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex TagsRegex();
}
