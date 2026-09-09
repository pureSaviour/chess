using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xiangqi.Core.Domain;
using Xiangqi.Core.Domain.ThreeKingdoms;
using Xiangqi.Core.Domain.Topology;

namespace Xiangqi.Core.Application.ThreeKingdoms;

/// <summary>
/// Three Kingdoms uses the PGN envelope but declares a non-classic Variant and CellId moves.
/// There is no external standard for this 135-cell topology, so classic-Xiangqi compatibility
/// is intentionally not claimed.
/// </summary>
public sealed record ThreeGameRecord(KingdomSnapshot InitialPosition,
    IReadOnlyList<(Kingdom Actor, KingdomMove Move)> Moves, AdvisorConquestPolicy AdvisorPolicy,
    IReadOnlyDictionary<string, string> Tags)
{
    public IReadOnlyList<KingdomSnapshot> Replay()
    {
        var game = ThreeKingdomsGame.FromPosition(InitialPosition, AdvisorPolicy);
        var states = new List<KingdomSnapshot>(Moves.Count + 1) { game.Snapshot };
        foreach (var entry in Moves)
        {
            var result = game.Move(entry.Actor, entry.Move);
            if (!result.Success) throw new InvalidDataException(result.Error);
            states.Add(game.Snapshot);
        }
        return states.AsReadOnly();
    }
}

public static partial class ThreeKingdomsPgnCodec
{
    private sealed record PositionDto(int Turn, int Ply, int Policy, bool[] Alive, int?[] Territories, PieceDto[] Pieces);
    private sealed record PieceDto(int Cell, int Id, string Kind, int Birth, int? Owner, bool Left);

    public static string Serialize(ThreeGameRecord record)
    {
        ArgumentNullException.ThrowIfNull(record); _ = record.Replay();
        var dto = new PositionDto((int)record.InitialPosition.Turn, record.InitialPosition.Ply, (int)record.AdvisorPolicy,
            record.InitialPosition.Alive.ToArray(), record.InitialPosition.TerritoryOwners.Select(x => x is null ? null : (int?)x).ToArray(),
            record.InitialPosition.Pieces.Select(x => new PieceDto(x.Cell.Value, x.Piece.Id, x.Piece.Kind.Value,
                (int)x.Piece.BirthRegion, x.Piece.Owner is null ? null : (int?)x.Piece.Owner, x.Piece.HasLeftBirthRegion)).ToArray());
        var setup = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(dto));
        var result = record.Tags.TryGetValue("Result", out var value) ? value : "*";
        var output = new StringBuilder()
            .AppendLine("[Game \"Three Kingdoms Xiangqi\"]")
            .AppendLine("[Variant \"Xiangqi-ThreeKingdoms\"]")
            .AppendLine("[Format \"CellId\"]")
            .Append("[TKFEN \"").Append(setup).AppendLine("\"]")
            .Append("[Result \"").Append(result).AppendLine("\"]").AppendLine();
        for (var i = 0; i < record.Moves.Count; i++)
        {
            if (i % 3 == 0) output.Append(i / 3 + 1).Append(". ");
            var entry = record.Moves[i];
            output.Append(entry.Actor switch { Kingdom.Wei => 'w', Kingdom.Wu => 'u', Kingdom.Shu => 's', _ => '?' }).Append(entry.Move.From.Value)
                .Append('-').Append(entry.Move.To.Value).Append(' ');
        }
        return output.Append(result).AppendLine().ToString();
    }

    public static bool TryParse(string? text, out ThreeGameRecord? record, out string? error)
    {
        record = null; error = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > XiangqiPgnCodec.MaximumTextLength)
        { error = "棋谱为空或过长。"; return false; }
        var tags = TagRegex().Matches(text).Cast<Match>().ToDictionary(x => x.Groups[1].Value, x => x.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
        if (!tags.TryGetValue("Variant", out var variant) || variant != "Xiangqi-ThreeKingdoms" ||
            !tags.TryGetValue("TKFEN", out var encoded))
        { error = "不是受支持的三国象棋棋谱。"; return false; }
        try
        {
            var dto = JsonSerializer.Deserialize<PositionDto>(Convert.FromBase64String(encoded)) ?? throw new InvalidDataException("空局面。");
            if (dto.Alive.Length != 3 || dto.Territories.Length != 3 || dto.Pieces.Length > 135) throw new InvalidDataException("局面数组无效。");
            var pieces = dto.Pieces.Select(x => new KingdomPlacement(new BoardCellId(x.Cell),
                new KingdomPiece(x.Id, new PieceKind(x.Kind), (Kingdom)x.Birth, x.Owner is null ? null : (Kingdom?)x.Owner, x.Left))).ToArray();
            var initial = new KingdomSnapshot(Array.AsReadOnly(pieces), Array.AsReadOnly(dto.Alive),
                Array.AsReadOnly(dto.Territories.Select(x => x is null ? null : (Kingdom?)x).ToArray()),
                (Kingdom)dto.Turn, dto.Ply, KingdomEndReason.None, null, null, null);
            _ = ThreeKingdomsGame.FromPosition(initial, (AdvisorConquestPolicy)dto.Policy);
            var body = TagRegex().Replace(text, " "); var moves = new List<(Kingdom, KingdomMove)>();
            foreach (Match match in MoveRegex().Matches(body))
            {
                var actor = match.Groups[1].Value[0] switch { 'w' => Kingdom.Wei, 'u' => Kingdom.Wu, 's' => Kingdom.Shu, _ => throw new InvalidDataException("无效国号。") };
                moves.Add((actor, new KingdomMove(new(int.Parse(match.Groups[2].Value)), new(int.Parse(match.Groups[3].Value)))));
            }
            var candidate = new ThreeGameRecord(initial, Array.AsReadOnly(moves.ToArray()), (AdvisorConquestPolicy)dto.Policy, tags);
            _ = candidate.Replay(); record = candidate; return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or InvalidDataException or InvalidOperationException)
        { error = exception.Message; return false; }
    }

    [GeneratedRegex("""\[\s*([A-Za-z][A-Za-z0-9_]*)\s+"([^"\r\n]*)"\s*\]""")]
    private static partial Regex TagRegex();
    [GeneratedRegex(@"\b([wus])(\d{1,3})-(\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex MoveRegex();
}
