namespace Xiangqi.Core.Domain;

public static class StandardPosition
{
    public static BoardState Create()
    {
        var board = new BoardState();
        var nextId = 1;

        void Add(PieceSide side, PieceKind kind, int file, int rank) =>
            board.Add(new BoardPosition(file, rank), new ChessPiece(nextId++, side, kind));

        AddBackRank(PieceSide.Red, 0);
        Add(PieceSide.Red, BuiltInPieceKinds.Cannon, 1, 2);
        Add(PieceSide.Red, BuiltInPieceKinds.Cannon, 7, 2);
        for (var file = 0; file < BoardState.FileCount; file += 2)
            Add(PieceSide.Red, BuiltInPieceKinds.Soldier, file, 3);

        AddBackRank(PieceSide.Black, 9);
        Add(PieceSide.Black, BuiltInPieceKinds.Cannon, 1, 7);
        Add(PieceSide.Black, BuiltInPieceKinds.Cannon, 7, 7);
        for (var file = 0; file < BoardState.FileCount; file += 2)
            Add(PieceSide.Black, BuiltInPieceKinds.Soldier, file, 6);

        return board;

        void AddBackRank(PieceSide side, int rank)
        {
            var kinds = new[]
            {
                BuiltInPieceKinds.Chariot, BuiltInPieceKinds.Horse, BuiltInPieceKinds.Elephant,
                BuiltInPieceKinds.Advisor, BuiltInPieceKinds.General, BuiltInPieceKinds.Advisor,
                BuiltInPieceKinds.Elephant, BuiltInPieceKinds.Horse, BuiltInPieceKinds.Chariot
            };
            for (var file = 0; file < kinds.Length; file++) Add(side, kinds[file], file, rank);
        }
    }
}

