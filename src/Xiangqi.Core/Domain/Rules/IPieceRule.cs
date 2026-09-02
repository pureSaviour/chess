namespace Xiangqi.Core.Domain.Rules;

/// <summary>
/// Defines only a piece kind's local movement geometry and blocking behavior.
/// Cross-piece invariants (turn ownership, friendly occupancy and self-check) live in
/// <see cref="ChessGame"/>, so built-in and custom pieces receive identical validation.
/// </summary>
public interface IPieceRule
{
    PieceKind Kind { get; }
    bool CanMove(IReadOnlyBoard board, ChessPiece piece, BoardPosition from, BoardPosition to);
}

public sealed class PieceRuleRegistry
{
    private readonly Dictionary<PieceKind, IPieceRule> _rules = [];

    public PieceRuleRegistry Register(IPieceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!_rules.TryAdd(rule.Kind, rule))
            throw new InvalidOperationException($"A rule for '{rule.Kind}' is already registered.");
        return this;
    }

    public IPieceRule Get(PieceKind kind) =>
        _rules.TryGetValue(kind, out var rule)
            ? rule
            : throw new InvalidOperationException($"No movement rule registered for '{kind}'.");

    public static PieceRuleRegistry CreateStandard() => new PieceRuleRegistry()
        .Register(new GeneralRule())
        .Register(new AdvisorRule())
        .Register(new ElephantRule())
        .Register(new HorseRule())
        .Register(new ChariotRule())
        .Register(new CannonRule())
        .Register(new SoldierRule());
}
