using System.Security.Cryptography;

namespace Xiangqi.Core.Domain.Junqi;

public enum JunqiSide : byte { First, Second }
public static class JunqiSideExtensions
{
    public static JunqiSide Opponent(this JunqiSide side) => side == JunqiSide.First ? JunqiSide.Second : JunqiSide.First;
}

public readonly record struct JunqiCell(int Row, int Column)
{
    public bool IsInside => Row is >= 0 and < JunqiTopology.RowCount && Column is >= 0 and < JunqiTopology.ColumnCount;
    public override string ToString() => IsInside ? $"{(char)('A' + Row)}{Column}" : $"({Row},{Column})";
    public static bool TryParse(string? value, out JunqiCell cell)
    {
        cell = default;
        if (value is not { Length: 2 } || value[0] is < 'A' or > 'L' || value[1] is < '0' or > '4') return false;
        cell = new JunqiCell(value[0] - 'A', value[1] - '0');
        return true;
    }
}

public enum JunqiCellKind : byte { Station, Camp, Headquarters }
public enum JunqiPieceKind : byte
{
    FieldMarshal, General, MajorGeneral, BrigadierGeneral, Colonel, Major, Captain, Lieutenant,
    Engineer, Landmine, Bomb, Flag
}
public readonly record struct JunqiPiece(int Id, JunqiSide Side, JunqiPieceKind Kind);
public readonly record struct JunqiPlacement(JunqiCell Cell, JunqiPiece Piece);
public enum JunqiBattleResult : byte { MoveOnly, AttackerWins, DefenderWins, BothRemoved, FlagCaptured }
public enum JunqiEndReason : byte { None, FlagCaptured, MandatoryAttack, NoLegalMove, Resignation, Timeout }
public readonly record struct JunqiMove(JunqiCell From, JunqiCell To);
public readonly record struct JunqiMoveResult(bool IsSuccess, JunqiMove Move, JunqiBattleResult Battle,
    JunqiSide? FlagRevealedFor, string? Error);

/// <summary>
/// Immutable authoritative position. It may contain both layouts and must never be sent directly
/// to an opponent. Use <see cref="ForPlayer"/> for UI or network output.
/// </summary>
public sealed record JunqiSnapshot(IReadOnlyList<JunqiPlacement> Pieces, JunqiSide SideToMove,
    bool IsFinished, JunqiSide? Winner, JunqiEndReason EndReason, int PlyNumber, int QuietPlyCount,
    IReadOnlySet<JunqiSide> ExposedFlags)
{
    public JunqiPlayerSnapshot ForPlayer(JunqiSide viewer) => new(
        Pieces.Select(x => new JunqiPublicPlacement(x.Cell, x.Piece.Id, x.Piece.Side,
            x.Piece.Side == viewer || IsFinished || x.Piece.Kind == JunqiPieceKind.Flag && ExposedFlags.Contains(x.Piece.Side)
                ? x.Piece.Kind : null)).ToArray(), SideToMove, IsFinished, Winner, EndReason, PlyNumber,
        QuietPlyCount, ExposedFlags.ToHashSet());

    /// <summary>Creates an omniscient post-game analysis view; never use it for a live network peer.</summary>
    public JunqiPlayerSnapshot ForReplay() => new(Pieces.Select(x => new JunqiPublicPlacement(
        x.Cell, x.Piece.Id, x.Piece.Side, x.Piece.Kind)).ToArray(), SideToMove, IsFinished,
        Winner, EndReason, PlyNumber, QuietPlyCount, ExposedFlags.ToHashSet());
}

public readonly record struct JunqiPublicPlacement(JunqiCell Cell, int PieceId, JunqiSide Side, JunqiPieceKind? Kind);
public sealed record JunqiPlayerSnapshot(IReadOnlyList<JunqiPublicPlacement> Pieces, JunqiSide SideToMove,
    bool IsFinished, JunqiSide? Winner, JunqiEndReason EndReason, int PlyNumber, int QuietPlyCount,
    IReadOnlySet<JunqiSide> ExposedFlags);

/// <summary>
/// Static 12x5 two-player Luzhanqi graph. Nodes are stoppable cells; front lines and mountains are
/// represented by connectivity rather than extra cells. Roads permit one edge per turn. Rail edges
/// form the long-distance network used by ordinary pieces and the turning path used by engineers.
/// The topology owns immutable value collections and no engine resources.
/// </summary>
public sealed class JunqiTopology
{
    public const int RowCount = 12;
    public const int ColumnCount = 5;
    private readonly Dictionary<JunqiCell, JunqiCell[]> _roads;
    private readonly Dictionary<JunqiCell, JunqiCell[]> _rails;
    private static readonly HashSet<JunqiCell> Camps =
    [
        new(2,1), new(2,3), new(3,2), new(4,1), new(4,3),
        new(7,1), new(7,3), new(8,2), new(9,1), new(9,3)
    ];
    private static readonly HashSet<JunqiCell> Headquarters = [new(0,1), new(0,3), new(11,1), new(11,3)];
    public static JunqiTopology Instance { get; } = new();

    private JunqiTopology()
    {
        var roads = Cells.ToDictionary(x => x, _ => new HashSet<JunqiCell>());
        for (var row = 0; row < RowCount; row++)
        for (var column = 0; column < ColumnCount; column++)
        {
            var cell = new JunqiCell(row, column);
            AddRoad(cell, new(row, column + 1));
            if (row != 5) AddRoad(cell, new(row + 1, column));
            if (Camps.Contains(cell))
                foreach (var dr in new[] { -1, 1 }) foreach (var dc in new[] { -1, 1 }) AddRoad(cell, new(row + dr, column + dc));
        }
        foreach (var column in new[] { 0, 2, 4 }) AddRoad(new(5, column), new(6, column));
        _roads = roads.ToDictionary(x => x.Key, x => x.Value.OrderBy(c => c.Row).ThenBy(c => c.Column).ToArray());

        var rails = Cells.ToDictionary(x => x, _ => new HashSet<JunqiCell>());
        foreach (var row in new[] { 1, 5, 6, 10 })
            for (var column = 0; column + 1 < ColumnCount; column++) AddRail(new(row, column), new(row, column + 1));
        foreach (var column in new[] { 0, 4 })
            for (var row = 1; row < 10; row++) AddRail(new(row, column), new(row + 1, column));
        foreach (var column in new[] { 0, 2, 4 }) AddRail(new(5, column), new(6, column));
        _rails = rails.ToDictionary(x => x.Key, x => x.Value.OrderBy(c => c.Row).ThenBy(c => c.Column).ToArray());

        void AddRoad(JunqiCell a, JunqiCell b) { if (!b.IsInside) return; roads[a].Add(b); roads[b].Add(a); }
        void AddRail(JunqiCell a, JunqiCell b) { rails[a].Add(b); rails[b].Add(a); }
    }

    public IEnumerable<JunqiCell> Cells
    {
        get { for (var row = 0; row < RowCount; row++) for (var column = 0; column < ColumnCount; column++) yield return new(row, column); }
    }
    public JunqiCellKind Kind(JunqiCell cell)
    {
        if (!cell.IsInside) throw new ArgumentOutOfRangeException(nameof(cell));
        return Camps.Contains(cell) ? JunqiCellKind.Camp : Headquarters.Contains(cell) ? JunqiCellKind.Headquarters : JunqiCellKind.Station;
    }
    public IReadOnlyList<JunqiCell> RoadNeighbors(JunqiCell cell) => _roads.TryGetValue(cell, out var value) ? value : [];
    public IReadOnlyList<JunqiCell> RailNeighbors(JunqiCell cell) => _rails.TryGetValue(cell, out var value) ? value : [];
    public bool IsRail(JunqiCell cell) => _rails.TryGetValue(cell, out var value) && value.Length > 0;
    public IReadOnlyList<JunqiCell> SetupCells(JunqiSide side)
    {
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        return Cells.Where(c => (side == JunqiSide.Second ? c.Row <= 5 : c.Row >= 6) &&
            Kind(c) != JunqiCellKind.Camp).ToArray();
    }
}

/// <summary>
/// Authoritative two-player Junqi aggregate. It validates complete private setups, graph movement,
/// combat, flag exposure, the configured mandatory-attack limit and terminal state atomically.
/// It does not render, serialize, authenticate a player, transmit secrets or choose a clock policy.
/// All calls are synchronous and single-threaded; returned snapshots copy owned state.
/// </summary>
public sealed class JunqiGame
{
    public const int StandardMandatoryAttackPly = 31;
    private readonly Dictionary<JunqiCell, JunqiPiece> _pieces;
    private readonly HashSet<JunqiSide> _exposedFlags = [];
    private readonly List<(JunqiMove Move, JunqiBattleResult Battle, JunqiSide? FlagReveal)> _history = [];
    private readonly int _mandatoryAttackPly;
    private JunqiSide _sideToMove;
    private bool _finished;
    private JunqiSide? _winner;
    private JunqiEndReason _endReason;
    private int _quiet;

    public JunqiGame(IEnumerable<JunqiPlacement> first, IEnumerable<JunqiPlacement> second,
        JunqiSide firstToMove = JunqiSide.First, int mandatoryAttackPly = StandardMandatoryAttackPly)
    {
        if (!Enum.IsDefined(firstToMove)) throw new ArgumentOutOfRangeException(nameof(firstToMove));
        if (mandatoryAttackPly <= 0) throw new ArgumentOutOfRangeException(nameof(mandatoryAttackPly));
        _mandatoryAttackPly = mandatoryAttackPly;
        var all = ValidateSetup(first, JunqiSide.First).Concat(ValidateSetup(second, JunqiSide.Second)).ToArray();
        if (all.Select(x => x.Cell).Distinct().Count() != all.Length) throw new ArgumentException("双方布阵棋位重叠。");
        if (all.Select(x => x.Piece.Id).Distinct().Count() != all.Length) throw new ArgumentException("棋子 ID 重复。");
        _pieces = all.ToDictionary(x => x.Cell, x => x.Piece);
        _sideToMove = firstToMove;
    }

    private JunqiGame(IEnumerable<JunqiPlacement> position, JunqiSide sideToMove, int mandatoryAttackPly)
    {
        if (!Enum.IsDefined(sideToMove)) throw new ArgumentOutOfRangeException(nameof(sideToMove));
        if (mandatoryAttackPly <= 0) throw new ArgumentOutOfRangeException(nameof(mandatoryAttackPly));
        var values = position.ToArray();
        if (values.Any(x => !x.Cell.IsInside || !Enum.IsDefined(x.Piece.Side) || !Enum.IsDefined(x.Piece.Kind)) ||
            values.Select(x => x.Cell).Distinct().Count() != values.Length ||
            values.Select(x => x.Piece.Id).Distinct().Count() != values.Length)
            throw new ArgumentException("局面包含无效或重复的棋位。", nameof(position));
        _pieces = values.ToDictionary(x => x.Cell, x => x.Piece);
        _sideToMove = sideToMove;
        _mandatoryAttackPly = mandatoryAttackPly;
    }

    public static JunqiGame CreateStandard(int mandatoryAttackPly = StandardMandatoryAttackPly)
    {
        const string first = "abccddeeffggghhhiiijjkklj";
        const string second = "jlkkjjiiihhhgggffeeddccba";
        return new JunqiGame(Build(JunqiSide.First, first, 1), Build(JunqiSide.Second, second, 26),
            mandatoryAttackPly: mandatoryAttackPly);
    }

    /// <summary>
    /// Creates a validated puzzle/test position without applying initial-deployment constraints.
    /// It is not a network setup bypass: room controllers must construct standard complete layouts.
    /// </summary>
    public static JunqiGame FromPosition(IEnumerable<JunqiPlacement> position,
        JunqiSide sideToMove = JunqiSide.First, int mandatoryAttackPly = StandardMandatoryAttackPly) =>
        new(position, sideToMove, mandatoryAttackPly);

    public JunqiSnapshot Snapshot => new(_pieces.OrderBy(x => x.Key.Row).ThenBy(x => x.Key.Column)
        .Select(x => new JunqiPlacement(x.Key, x.Value)).ToArray(), _sideToMove, _finished, _winner,
        _endReason, _history.Count, _quiet, _exposedFlags.ToHashSet());
    public IReadOnlyList<(JunqiMove Move, JunqiBattleResult Battle, JunqiSide? FlagReveal)> History => _history.AsReadOnly();

    public IReadOnlyList<JunqiCell> LegalDestinations(JunqiCell from)
    {
        if (_finished || !_pieces.TryGetValue(from, out var piece) || piece.Side != _sideToMove ||
            piece.Kind is JunqiPieceKind.Landmine or JunqiPieceKind.Flag ||
            JunqiTopology.Instance.Kind(from) == JunqiCellKind.Headquarters) return [];
        return JunqiTopology.Instance.Cells.Where(to => CanReach(piece, from, to)).ToArray();
    }

    public JunqiMoveResult TryMove(JunqiMove move)
    {
        JunqiMoveResult Reject(string error) => new(false, move, default, null, error);
        if (_finished) return Reject("对局已结束。");
        if (!move.From.IsInside || !move.To.IsInside || move.From == move.To) return Reject("棋位无效。");
        if (!_pieces.TryGetValue(move.From, out var attacker)) return Reject("起点没有棋子。");
        if (attacker.Side != _sideToMove) return Reject("不是该棋子行棋。");
        if (attacker.Kind is JunqiPieceKind.Landmine or JunqiPieceKind.Flag ||
            JunqiTopology.Instance.Kind(move.From) == JunqiCellKind.Headquarters) return Reject("该棋子不能移动。");
        if (_pieces.TryGetValue(move.To, out var defender) && defender.Side == attacker.Side) return Reject("落点被己方棋子占据。");
        if (_pieces.ContainsKey(move.To) && JunqiTopology.Instance.Kind(move.To) == JunqiCellKind.Camp) return Reject("行营中的棋子不能被攻击。");
        if (!CanReach(attacker, move.From, move.To)) return Reject("棋子无法沿道路或铁路到达。");

        var commandersBefore = Enum.GetValues<JunqiSide>().ToDictionary(side => side,
            side => _pieces.Values.Any(p => p.Side == side && p.Kind == JunqiPieceKind.FieldMarshal));
        _pieces.Remove(move.From);
        var battle = JunqiBattleResult.MoveOnly;
        if (!_pieces.TryGetValue(move.To, out defender)) _pieces[move.To] = attacker;
        else
        {
            battle = Resolve(attacker, defender);
            switch (battle)
            {
                case JunqiBattleResult.AttackerWins: _pieces[move.To] = attacker; break;
                case JunqiBattleResult.DefenderWins: break;
                case JunqiBattleResult.BothRemoved: _pieces.Remove(move.To); break;
                case JunqiBattleResult.FlagCaptured:
                    _pieces[move.To] = attacker; _finished = true; _winner = attacker.Side; _endReason = JunqiEndReason.FlagCaptured; break;
            }
        }
        JunqiSide? revealed = null;
        foreach (var side in Enum.GetValues<JunqiSide>())
            if (commandersBefore[side] && !_exposedFlags.Contains(side) &&
                !_pieces.Values.Any(p => p.Side == side && p.Kind == JunqiPieceKind.FieldMarshal))
            { _exposedFlags.Add(side); revealed ??= side; }
        _quiet = battle == JunqiBattleResult.MoveOnly ? _quiet + 1 : 0;
        _history.Add((move, battle, revealed));
        var movingSide = _sideToMove;
        _sideToMove = _sideToMove.Opponent();
        if (!_finished && _quiet >= _mandatoryAttackPly)
        { _finished = true; _winner = movingSide.Opponent(); _endReason = JunqiEndReason.MandatoryAttack; }
        else if (!_finished && !HasAnyMove(_sideToMove))
        { _finished = true; _winner = movingSide; _endReason = JunqiEndReason.NoLegalMove; }
        return new(true, move, battle, revealed, null);
    }

    public bool Resign(JunqiSide side)
    {
        if (_finished || !Enum.IsDefined(side)) return false;
        _finished = true; _winner = side.Opponent(); _endReason = JunqiEndReason.Resignation; return true;
    }


    /// <summary>Applies an application-authorized clock loss without adding a board move.</summary>
    public bool DeclareTimeout(JunqiSide loser)
    {
        if (_finished || !Enum.IsDefined(loser)) return false;
        _finished = true; _winner = loser.Opponent(); _endReason = JunqiEndReason.Timeout; return true;
    }

    private bool HasAnyMove(JunqiSide side)
    {
        var previous = _sideToMove; _sideToMove = side;
        try { return _pieces.Any(x => x.Value.Side == side && LegalDestinations(x.Key).Count > 0); }
        finally { _sideToMove = previous; }
    }

    private bool CanReach(JunqiPiece piece, JunqiCell from, JunqiCell to)
    {
        if (!to.IsInside || from == to) return false;
        if (JunqiTopology.Instance.RoadNeighbors(from).Contains(to)) return true;
        if (!JunqiTopology.Instance.IsRail(from) || !JunqiTopology.Instance.IsRail(to)) return false;
        if (piece.Kind == JunqiPieceKind.Engineer) return EngineerRailPath(from, to);
        if (from.Row != to.Row && from.Column != to.Column) return false;
        var dr = Math.Sign(to.Row - from.Row); var dc = Math.Sign(to.Column - from.Column); var current = from;
        while (current != to)
        {
            var next = new JunqiCell(current.Row + dr, current.Column + dc);
            if (!JunqiTopology.Instance.RailNeighbors(current).Contains(next)) return false;
            current = next;
            if (current != to && _pieces.ContainsKey(current)) return false;
        }
        return true;
    }

    private bool EngineerRailPath(JunqiCell from, JunqiCell to)
    {
        var queue = new Queue<JunqiCell>(); var seen = new HashSet<JunqiCell> { from }; queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in JunqiTopology.Instance.RailNeighbors(current))
            {
                if (next != to && _pieces.ContainsKey(next) || !seen.Add(next)) continue;
                if (next == to) return true;
                queue.Enqueue(next);
            }
        }
        return false;
    }

    private static JunqiBattleResult Resolve(JunqiPiece attacker, JunqiPiece defender)
    {
        if (defender.Kind == JunqiPieceKind.Flag) return JunqiBattleResult.FlagCaptured;
        if (attacker.Kind == JunqiPieceKind.Bomb || defender.Kind == JunqiPieceKind.Bomb) return JunqiBattleResult.BothRemoved;
        if (defender.Kind == JunqiPieceKind.Landmine)
            return attacker.Kind == JunqiPieceKind.Engineer ? JunqiBattleResult.AttackerWins : JunqiBattleResult.DefenderWins;
        var a = Rank(attacker.Kind); var d = Rank(defender.Kind);
        return a == d ? JunqiBattleResult.BothRemoved : a > d ? JunqiBattleResult.AttackerWins : JunqiBattleResult.DefenderWins;
    }

    private static int Rank(JunqiPieceKind kind) => kind switch
    {
        JunqiPieceKind.FieldMarshal => 9, JunqiPieceKind.General => 8, JunqiPieceKind.MajorGeneral => 7,
        JunqiPieceKind.BrigadierGeneral => 6, JunqiPieceKind.Colonel => 5, JunqiPieceKind.Major => 4,
        JunqiPieceKind.Captain => 3, JunqiPieceKind.Lieutenant => 2, JunqiPieceKind.Engineer => 1, _ => 0
    };

    private static IReadOnlyList<JunqiPlacement> ValidateSetup(IEnumerable<JunqiPlacement> source, JunqiSide side)
    {
        ArgumentNullException.ThrowIfNull(source);
        var values = source.ToArray();
        if (values.Length != 25) throw new ArgumentException("每方必须布置 25 枚棋子。");
        var allowed = JunqiTopology.Instance.SetupCells(side).ToHashSet();
        if (values.Any(x => x.Piece.Side != side || !allowed.Contains(x.Cell)) || values.Select(x => x.Cell).Distinct().Count() != 25)
            throw new ArgumentException("布阵棋位或阵营无效。");
        var expected = Enum.GetValues<JunqiPieceKind>().ToDictionary(k => k, Count);
        var actual = values.GroupBy(x => x.Piece.Kind).ToDictionary(x => x.Key, x => x.Count());
        if (expected.Any(x => actual.GetValueOrDefault(x.Key) != x.Value)) throw new ArgumentException("布阵棋子数量不符合标准。");
        var front = side == JunqiSide.First ? 6 : 5;
        var rear = side == JunqiSide.First ? new HashSet<int> { 10, 11 } : [0, 1];
        if (values.Any(x => x.Piece.Kind == JunqiPieceKind.Bomb && x.Cell.Row == front) ||
            values.Any(x => x.Piece.Kind == JunqiPieceKind.Landmine && !rear.Contains(x.Cell.Row)) ||
            values.Any(x => x.Piece.Kind == JunqiPieceKind.Flag && JunqiTopology.Instance.Kind(x.Cell) != JunqiCellKind.Headquarters))
            throw new ArgumentException("炸弹、地雷或军旗布置不合法。");
        return values;
    }

    public static IReadOnlyList<JunqiPlacement> Build(JunqiSide side, string codes, int firstId)
    {
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        if (firstId <= 0) throw new ArgumentOutOfRangeException(nameof(firstId));
        var cells = JunqiTopology.Instance.SetupCells(side);
        if (codes is null || codes.Length != cells.Count) throw new ArgumentException("布阵编码必须包含 25 枚棋子。", nameof(codes));
        return codes.Select((code, index) => new JunqiPlacement(cells[index],
            new JunqiPiece(firstId + index, side, KindFromCode(code)))).ToArray();
    }

    /// <summary>Creates one cryptographically shuffled legal standard setup without sharing its identities.</summary>
    public static IReadOnlyList<JunqiPlacement> CreateRandomLayout(JunqiSide side, int firstId)
    {
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        if (firstId <= 0) throw new ArgumentOutOfRangeException(nameof(firstId));
        var cells = JunqiTopology.Instance.SetupCells(side).ToList();
        var result = new List<JunqiPlacement>(25); var nextId = firstId;
        var headquarters = cells.Where(c => JunqiTopology.Instance.Kind(c) == JunqiCellKind.Headquarters).ToList();
        var flagCell = TakeRandom(headquarters); cells.Remove(flagCell);
        result.Add(new(flagCell, new(nextId++, side, JunqiPieceKind.Flag)));
        var rearRows = side == JunqiSide.First ? new HashSet<int> { 10, 11 } : [0, 1];
        var rear = cells.Where(c => rearRows.Contains(c.Row)).ToList();
        for (var i = 0; i < 3; i++) { var cell = TakeRandom(rear); rear.Remove(cell); cells.Remove(cell); result.Add(new(cell, new(nextId++, side, JunqiPieceKind.Landmine))); }
        var front = side == JunqiSide.First ? 6 : 5;
        var bombCells = cells.Where(c => c.Row != front).ToList();
        for (var i = 0; i < 2; i++) { var cell = TakeRandom(bombCells); bombCells.Remove(cell); cells.Remove(cell); result.Add(new(cell, new(nextId++, side, JunqiPieceKind.Bomb))); }
        var remainingKinds = Enum.GetValues<JunqiPieceKind>().Where(k => k is not (JunqiPieceKind.Flag or JunqiPieceKind.Landmine or JunqiPieceKind.Bomb))
            .SelectMany(k => Enumerable.Repeat(k, Count(k))).ToList();
        Shuffle(cells); Shuffle(remainingKinds);
        for (var i = 0; i < cells.Count; i++) result.Add(new(cells[i], new(nextId++, side, remainingKinds[i])));
        return result.OrderBy(x => x.Cell.Row).ThenBy(x => x.Cell.Column).ToArray();

        static T TakeRandom<T>(IReadOnlyList<T> values) => values[RandomNumberGenerator.GetInt32(values.Count)];
        static void Shuffle<T>(IList<T> values)
        {
            for (var i = values.Count - 1; i > 0; i--)
            { var j = RandomNumberGenerator.GetInt32(i + 1); (values[i], values[j]) = (values[j], values[i]); }
        }
    }

    public static char Code(JunqiPieceKind kind) => (char)('a' + (int)kind);
    public static JunqiPieceKind KindFromCode(char code)
    {
        if (code is < 'a' or > 'l') throw new ArgumentOutOfRangeException(nameof(code));
        return (JunqiPieceKind)(code - 'a');
    }
    private static int Count(JunqiPieceKind kind) => kind switch
    {
        JunqiPieceKind.FieldMarshal or JunqiPieceKind.General or JunqiPieceKind.Flag => 1,
        JunqiPieceKind.MajorGeneral or JunqiPieceKind.BrigadierGeneral or JunqiPieceKind.Colonel or
        JunqiPieceKind.Major or JunqiPieceKind.Bomb => 2,
        _ => 3
    };
}
