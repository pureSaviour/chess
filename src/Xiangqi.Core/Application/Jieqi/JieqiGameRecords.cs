using System.Text;
using System.Text.RegularExpressions;
using Xiangqi.Core.Domain;
using Xiangqi.Core.Domain.Jieqi;

namespace Xiangqi.Core.Application.Jieqi;

/// <summary>Deterministic Jieqi main-line record: the seed and shuffle version reconstruct all hidden identities.</summary>
public sealed record JieqiGameRecord(ulong Seed, IReadOnlyList<ChessMove> Moves, IReadOnlyDictionary<string, string> Tags)
{
    public IReadOnlyList<JieqiSnapshot> Replay()
    {
        var game = JieqiGame.Create(Seed);
        var positions = new List<JieqiSnapshot>(Moves.Count + 1) { game.Snapshot };
        foreach (var move in Moves)
        {
            var result = game.TryMove(move);
            if (!result.IsSuccess) throw new InvalidDataException($"Illegal Jieqi move {XiangqiUciNotation.FormatMove(move)}: {result.Rejection}.");
            positions.Add(game.Snapshot);
        }
        return positions.AsReadOnly();
    }
}

/// <summary>PGN/ICCS-compatible Jieqi extension with explicit Variant, Seed and Shuffle tags.</summary>
public static class JieqiPgnCodec
{
    public const int MaximumTextLength = 4 * 1024 * 1024;
    public static string Serialize(JieqiGameRecord record)
    {
        ArgumentNullException.ThrowIfNull(record); _ = record.Replay();
        var tags = new Dictionary<string, string>(record.Tags, StringComparer.OrdinalIgnoreCase)
        {
            ["Game"] = "Chinese Chess", ["Variant"] = "Jieqi", ["Format"] = "ICCS",
            ["Seed"] = record.Seed.ToString("X16"), ["Shuffle"] = JieqiGame.ShuffleAlgorithm
        };
        if (!tags.ContainsKey("Result")) tags["Result"] = "*";
        var b = new StringBuilder();
        foreach (var pair in tags.OrderBy(x => x.Key, StringComparer.Ordinal))
            b.Append('[').Append(pair.Key).Append(" \"").Append(pair.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")) .AppendLine("\"]");
        b.AppendLine();
        for (var i = 0; i < record.Moves.Count; i++)
        {
            if (i % 2 == 0) b.Append(i / 2 + 1).Append(". ");
            b.Append(XiangqiUciNotation.FormatMove(record.Moves[i])).Append(' ');
        }
        b.Append(tags["Result"]).AppendLine();
        return b.ToString();
    }

    public static bool TryParse(string? text, out JieqiGameRecord? record, out string? error)
    {
        record = null; error = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
        { error = "棋谱为空或超过 4 MiB。"; return false; }
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, "\\[\\s*([A-Za-z][A-Za-z0-9_]*)\\s+\"((?:\\\\.|[^\"\\\\])*)\"\\s*\\]"))
        {
            if (tags.Count >= 128) { error = "棋谱标签过多。"; return false; }
            if (match.Groups[2].Value.Length > 2048) { error = "棋谱标签内容过长。"; return false; }
            try { tags[match.Groups[1].Value] = Regex.Unescape(match.Groups[2].Value); }
            catch (ArgumentException) { error = "棋谱标签包含无效转义。"; return false; }
        }
        if (!tags.TryGetValue("Variant", out var variant) || !variant.Equals("Jieqi", StringComparison.OrdinalIgnoreCase) ||
            !tags.TryGetValue("Shuffle", out var shuffle) || shuffle != JieqiGame.ShuffleAlgorithm ||
            !tags.TryGetValue("Seed", out var rawSeed) || !ulong.TryParse(rawSeed, System.Globalization.NumberStyles.HexNumber, null, out var seed))
        { error = "缺少受支持的 Jieqi Variant、Seed 或 Shuffle 标签。"; return false; }
        var body = Regex.Replace(text, "\\[[^\\r\\n]*\\]", " ");
        body = Regex.Replace(body, @"\{[^}]*\}|;[^\r\n]*|\$\d+|\([^)]*\)", " ");
        var moves = new List<ChessMove>();
        foreach (var raw in Regex.Split(body, @"\s+"))
        {
            var token = Regex.Replace(raw.Trim(), @"^\d+\.(\.\.)?", "").TrimEnd('+', '#', '!', '?');
            if (token.Length == 0 || token is "*" or "1-0" or "0-1" or "1/2-1/2") continue;
            if (!XiangqiUciNotation.TryParseMove(token, out var move)) { error = $"无法识别着法“{token}”。"; return false; }
            if (moves.Count >= ClassicGameRecord.MaximumPlyCount) { error = "棋谱着法过多。"; return false; }
            moves.Add(move);
        }
        var candidate = new JieqiGameRecord(seed, moves.AsReadOnly(), tags);
        try { _ = candidate.Replay(); }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        { error = exception.Message; return false; }
        record = candidate; return true;
    }
}
