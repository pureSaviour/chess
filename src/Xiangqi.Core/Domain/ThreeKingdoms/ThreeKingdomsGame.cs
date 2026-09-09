using Xiangqi.Core.Domain.Topology;

namespace Xiangqi.Core.Domain.ThreeKingdoms;

/// <summary>收编士的策略：默认共享国土；另一模式仅移除被征服方的士，供房间规则扩展使用。</summary>
public enum AdvisorConquestPolicy : byte { SharedTerritory, DieWithGeneral }
public enum KingdomEndReason : byte { None, LastSurvivor, Draw, NoMoves }
public readonly record struct KingdomMove(BoardCellId From, BoardCellId To);

/// <summary>
/// 棋子实例保留出生国、稳定 ID 与兵出境标记；Owner 是当前控制权，null 表示超时/认输后
/// 留下的中立障碍。障碍不可移动或吃掉，但仍阻挡车、马腿、象眼并可作为炮架。
/// </summary>
public sealed record KingdomPiece(int Id, PieceKind Kind, Kingdom BirthRegion, Kingdom? Owner, bool HasLeftBirthRegion = false);
public sealed record KingdomPlacement(BoardCellId Cell, KingdomPiece Piece);
public sealed record KingdomSnapshot(
    IReadOnlyList<KingdomPlacement> Pieces, IReadOnlyList<bool> Alive, IReadOnlyList<Kingdom?> TerritoryOwners,
    Kingdom Turn, int Ply, KingdomEndReason EndReason, Kingdom? Winner, KingdomMove? LastMove, Kingdom? LastActor)
{
    public bool IsFinished => EndReason != KingdomEndReason.None;
}
public sealed record KingdomMoveResult(bool Success, string? Error, KingdomPiece? Captured = null, Kingdom? Conquered = null);

/// <summary>
/// 三国棋局唯一权威聚合。负责占位、轮次、棋规、收编、淘汰和撤销；所有 UI/网络动作都必须
/// 经过此处，不能直接编辑快照。与经典 ChessGame 并列，共用棋种值对象和拓扑端口，
/// 不引入红黑 Opponent() 或任何引擎依赖。将军仅是提示，不限制送将和不应将。
/// </summary>
public sealed class ThreeKingdomsGame : IReadOnlyThreeBoard
{
    private readonly IBoardTopology _topology;
    private readonly ThreePieceRuleRegistry _rules;
    public IBoardTopology Topology => _topology;
    public KingdomPiece? At(BoardCellId cell) => _topology.Contains(cell) ? _pieces[cell.Value] : null;
    public bool Owns(Kingdom owner, Kingdom region) => Enum.IsDefined(region) && _territories[(int)region] == owner;
    public bool HasConquered(Kingdom owner) => _territories.Count(t => t == owner) > 1;
    private KingdomPiece?[] _pieces = new KingdomPiece?[135];
    private bool[] _alive = [true, true, true];
    private Kingdom?[] _territories = [Kingdom.Wei, Kingdom.Wu, Kingdom.Shu];
    private KingdomSnapshot? _undo;
    private Kingdom _turn;
    private int _ply;
    private KingdomEndReason _end;
    private Kingdom? _winner;
    private KingdomMove? _lastMove;
    private Kingdom? _lastActor;
    public AdvisorConquestPolicy AdvisorPolicy { get; }
    public KingdomSnapshot Snapshot { get; private set; } = null!;

    public ThreeKingdomsGame(AdvisorConquestPolicy policy = AdvisorConquestPolicy.SharedTerritory)
        : this(StandardPieces(), Kingdom.Wei, policy) { }

    /// <summary>显式局面工厂构造供规则测试及未来棋谱导入。拒绝无效点、重复占位和实例 ID。</summary>
    public ThreeKingdomsGame(IEnumerable<KingdomPlacement> placement, Kingdom turn,
        AdvisorConquestPolicy policy = AdvisorConquestPolicy.SharedTerritory, ThreePieceRuleRegistry? rules = null)
    {
        _rules = rules ?? ThreePieceRuleRegistry.Standard;
        if (!Enum.IsDefined(turn) || !Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(turn));
        _topology = ThreeKingdomsTopology.Instance;
        AdvisorPolicy = policy;
        _turn = turn;
        var ids = new HashSet<int>();
        foreach (var p in placement)
        {
            if (!_topology.Contains(p.Cell) || _pieces[p.Cell.Value] is not null || !ids.Add(p.Piece.Id) ||
                !Enum.IsDefined(p.Piece.BirthRegion) || (p.Piece.Owner is { } owner && !Enum.IsDefined(owner)))
                throw new ArgumentException("Invalid placement.");
            _pieces[p.Cell.Value] = p.Piece;
        }
        if (!HasLegalMove(_turn)) NextTurn();
        Publish();
    }

    internal ThreeKingdomsGame Fork() => new(this);

    /// <summary>Recreates a validated puzzle/replay position without exposing mutable arrays.</summary>
    public static ThreeKingdomsGame FromPosition(KingdomSnapshot position,
        AdvisorConquestPolicy policy = AdvisorConquestPolicy.SharedTerritory)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.Alive.Count != 3 || position.TerritoryOwners.Count != 3 || !Enum.IsDefined(position.Turn) ||
            position.Ply < 0 || position.TerritoryOwners.Any(owner => owner is { } value && !Enum.IsDefined(value)))
            throw new ArgumentException("Invalid Three Kingdoms position metadata.", nameof(position));
        var game = new ThreeKingdomsGame(position.Pieces, position.Turn, policy);
        game._alive = position.Alive.ToArray(); game._territories = position.TerritoryOwners.ToArray();
        game._turn = position.Turn; game._ply = position.Ply; game._end = position.EndReason;
        game._winner = position.Winner; game._lastMove = position.LastMove; game._lastActor = position.LastActor;
        game._undo = null; game.Publish();
        return game;
    }
    private ThreeKingdomsGame(ThreeKingdomsGame source)
    {
        _topology = source._topology; _rules = source._rules; AdvisorPolicy = source.AdvisorPolicy;
        _pieces = (KingdomPiece?[])source._pieces.Clone(); _alive = (bool[])source._alive.Clone();
        _territories = (Kingdom?[])source._territories.Clone(); _undo = source._undo;
        _turn = source._turn; _ply = source._ply; _end = source._end; _winner = source._winner;
        _lastMove = source._lastMove; _lastActor = source._lastActor; Snapshot = source.Snapshot;
    }
    public IReadOnlyList<BoardCellId> LegalDestinations(BoardCellId from)
    {
        if (!_topology.Contains(from) || _pieces[from.Value] is not { Owner: { } owner } || !_alive[(int)owner] || _end != 0)
            return Array.Empty<BoardCellId>();
        var destinations = new List<BoardCellId>();
        for (var i = 0; i < _topology.CellCount; i++) if (CanMove(from, new(i))) destinations.Add(new(i));
        return destinations.AsReadOnly();
    }

    public KingdomMoveResult Move(Kingdom actor, KingdomMove move)
    {
        if (_end != 0) return new(false, "对局已经结束。");
        if (actor != _turn || !Enum.IsDefined(actor) || !_alive[(int)actor]) return new(false, "尚未轮到该国。");
        if (!_topology.Contains(move.From) || !_topology.Contains(move.To) || _pieces[move.From.Value]?.Owner != actor)
            return new(false, "请选择本国控制的棋子。");
        if (!CanMove(move.From, move.To)) return new(false, "该着法不符合三国棋规。");
        _undo = Snapshot;
        var piece = _pieces[move.From.Value]!;
        var captured = _pieces[move.To.Value];
        _pieces[move.From.Value] = null;
        _pieces[move.To.Value] = piece with
        {
            HasLeftBirthRegion = piece.HasLeftBirthRegion || KingdomSquare.FromCell(move.To).Region != piece.BirthRegion
        };
        Kingdom? conquered = null;
        if (captured is { Kind: var kind, Owner: { } victim } && kind == BuiltInPieceKinds.General)
        {
            conquered = victim;
            _alive[(int)victim] = false;
            for (var i = 0; i < _pieces.Length; i++)
                if (_pieces[i] is { } remaining && remaining.Owner == victim)
                    _pieces[i] = AdvisorPolicy == AdvisorConquestPolicy.DieWithGeneral && remaining.Kind == BuiltInPieceKinds.Advisor
                        ? null : remaining with { Owner = actor };
            for (var i = 0; i < 3; i++) if (_territories[i] == victim) _territories[i] = actor;
        }
        _ply++;
        _lastMove = move;
        _lastActor = actor;
        NextTurn();
        Publish();
        return new(true, null, captured, conquered);
    }

    /// <summary>仅最后合法落子的存活玩家可申请一手撤销；期间发生淘汰等非走子事件也会使资格失效。</summary>
    public bool CanUndo(Kingdom actor) => Enum.IsDefined(actor) && _alive[(int)actor] && _end == 0 &&
        _undo is not null && _lastActor == actor;

    public bool Undo(Kingdom actor)
    {
        if (!CanUndo(actor)) return false;
        var state = _undo!;
        _pieces = new KingdomPiece?[135];
        foreach (var p in state.Pieces) _pieces[p.Cell.Value] = p.Piece;
        _alive = state.Alive.ToArray();
        _territories = state.TerritoryOwners.ToArray();
        _turn = state.Turn; _ply = state.Ply; _end = state.EndReason; _winner = state.Winner;
        _lastMove = null; _lastActor = null; _undo = null;
        Publish();
        return true;
    }

    /// <summary>超时、认输、断线投票判负共用同一淘汰规则。移除将，余子成为不可捕获的障碍。</summary>
    public bool Eliminate(Kingdom loser)
    {
        if (!Enum.IsDefined(loser) || !_alive[(int)loser] || _end != 0) return false;
        _alive[(int)loser] = false;
        for (var i = 0; i < _pieces.Length; i++)
            if (_pieces[i] is { } piece && piece.Owner == loser)
                _pieces[i] = piece.Kind == BuiltInPieceKinds.General ? null : piece with { Owner = null };
        for (var i = 0; i < 3; i++) if (_territories[i] == loser) _territories[i] = null;
        _undo = null; _lastMove = null; _lastActor = null;
        if (!_alive[(int)_turn] || !HasLegalMove(_turn)) NextTurn();
        else ResolveSurvivors();
        Publish();
        return true;
    }

    public void DeclareDraw()
    {
        if (_end != 0) return;
        _end = KingdomEndReason.Draw; _winner = null; _undo = null;
        Publish();
    }

    public bool IsThreatened(Kingdom kingdom)
    {
        var king = Array.FindIndex(_pieces, p => p is not null && p.Owner == kingdom && p.Kind == BuiltInPieceKinds.General);
        if (king < 0) return false;
        for (var i = 0; i < _pieces.Length; i++)
            if (_pieces[i] is { Owner: { } owner } && owner != kingdom && CanMove(new(i), new(king))) return true;
        return false;
    }

    private bool CanMove(BoardCellId from, BoardCellId to)
    {
        if (from == to || _pieces[from.Value] is not { Owner: { } owner } piece) return false;
        var target = _pieces[to.Value];
        if (target is not null && (target.Owner == owner || target.Owner is null)) return false;
        return _rules.TryGet(piece.Kind, out var rule) && rule.CanMove(this, piece, from, to);
    }

    private void NextTurn()
    {
        if (ResolveSurvivors()) return;
        for (var n = 1; n <= 3; n++)
        {
            var next = (Kingdom)(((int)_turn + n) % 3);
            if (!_alive[(int)next]) continue;
            if (HasLegalMove(next)) { _turn = next; return; }
        }
        _end = KingdomEndReason.NoMoves;
    }
    private bool HasLegalMove(Kingdom side)
    {
        for (var from = 0; from < _pieces.Length; from++)
            if (_pieces[from]?.Owner == side)
                for (var to = 0; to < _pieces.Length; to++)
                    if (CanMove(new(from), new(to))) return true;
        return false;
    }
    private bool ResolveSurvivors()
    {
        if (_alive.Count(a => a) > 1) return false;
        _end = KingdomEndReason.LastSurvivor;
        _winner = (Kingdom)Array.FindIndex(_alive, a => a);
        return true;
    }
    private void Publish() => Snapshot = new(
        Array.AsReadOnly(_pieces.Select((p, i) => p is null ? null : new KingdomPlacement(new(i), p)).OfType<KingdomPlacement>().ToArray()),
        Array.AsReadOnly((bool[])_alive.Clone()), Array.AsReadOnly((Kingdom?[])_territories.Clone()),
        _turn, _ply, _end, _winner, _lastMove, _lastActor);
    private static IEnumerable<KingdomPlacement> StandardPieces()
    {
        PieceKind[] row = [BuiltInPieceKinds.Chariot, BuiltInPieceKinds.Horse, BuiltInPieceKinds.Elephant,
            BuiltInPieceKinds.Advisor, BuiltInPieceKinds.General, BuiltInPieceKinds.Advisor,
            BuiltInPieceKinds.Elephant, BuiltInPieceKinds.Horse, BuiltInPieceKinds.Chariot];
        var id = 0;
        for (var k = 0; k < 3; k++)
        for (var f = 0; f < 9; f++)
        {
            var country = (Kingdom)k;
            yield return new(new KingdomSquare(country, f, 0).Cell, new(id++, row[f], country, country));
            if (f is 1 or 7) yield return new(new KingdomSquare(country, f, 2).Cell, new(id++, BuiltInPieceKinds.Cannon, country, country));
            if (f % 2 == 0) yield return new(new KingdomSquare(country, f, 3).Cell, new(id++, BuiltInPieceKinds.Soldier, country, country));
        }
    }
}
