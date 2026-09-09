using Godot;
using System.Xml;
using Xiangqi.Core.Application;
using Xiangqi.Core.Domain;
using Xiangqi.Presentation.Settings;

namespace Xiangqi.Presentation.Diagnostics;

/// <summary>
/// Engine-level regression for feedback lifetime, independent of transport. Uses the same
/// immutable snapshots and AnimateMove path as local, LAN, Steam and AI GameScreen instances.
/// Optional rendered captures go to build/feedback-qa; never changes persisted user settings.
/// </summary>
public partial class BoardFeedbackSmoke : Control
{
    public override async void _Ready()
    {
        try
        {
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            var packages = ThemePackageCatalog.CreateDefault();
            Require(packages.Diagnostics.Count == 0,
                "Bundled XML themes must scan without diagnostics: " +
                string.Join("; ", packages.Diagnostics.Select(d => d.Message)));
            Require(packages.Themes.Count >= 2, "The dual-directory catalog must load bundled classic and spring XML.");
            var spring = packages.Resolve("spring");
            Require(spring.Boards.ContainsKey(ThemeBoardMode.Classic) &&
                    spring.Boards.ContainsKey(ThemeBoardMode.ThreeKingdoms),
                "Spring XML must supply both board modes.");
            Require(spring.Effects.ContainsKey(ThemeEffectSlot.Selection),
                "Spring XML must supply its semantic selection shader.");
            Require(BoardView.PieceColumn("general") == 0 && BoardView.PieceColumn("soldier") == 6,
                "semantic-v1 atlas columns must remain stable.");
            VerifyUnsafeManifestRejected();
            var replacementPalette = spring.Palette with { NeutralPieceFill = Colors.Magenta };
            var catalog = new BoardThemeCatalog([
                packages.Resolve("classic"),
                new BoardThemeDefinition("spring", BoardSkinKind.Spring, spring.DisplayName, replacementPalette,
                    spring.Boards, spring.Pieces, spring.Effects)
            ]);
            var board = new BoardView(catalog) { Position = new Vector2(210, 42), Size = new Vector2(680, 730) };
            AddChild(board);
            var session = new GameSession();
            board.ApplyAppearance("spring", true);
            Require(board.ActivePalette.NeutralPieceFill == Colors.Magenta,
                "An injected catalog must replace the theme used by the real board consumer.");
            Require(board.ActivePalette.PieceFill != packages.Resolve("classic").Palette.PieceFill,
                "Spring theme must style pieces as well as the board background.");
            Require(catalog.Resolve((BoardSkinKind)999).Id == "classic",
                "Unknown persisted theme IDs must fall back to classic.");
            RequireThrows<ArgumentException>(() => new BoardThemeCatalog([]),
                "A catalog without the required classic fallback must be rejected.");
            RequireThrows<ArgumentException>(() => new BoardThemeCatalog([
                BoardThemeCatalog.Default.Resolve(BoardSkinKind.Classic),
                BoardThemeCatalog.Default.Resolve(BoardSkinKind.Classic)
            ]), "Duplicate theme IDs must be rejected.");
            var red = new ChessMove(new BoardPosition(0, 3), new BoardPosition(0, 4));
            var black = new ChessMove(new BoardPosition(0, 6), new BoardPosition(0, 5));

            board.SetPosition(session.Snapshot, red.From, session.GetLegalDestinations(red.From));
            await Pause();
            await Capture("01-selected");
            await Play(session, board, red);
            Require(board.VisibleLastMove == red, "Landed move must be marked.");
            Require(!board.IsProcessing(), "Idle white ring must not redraw the board on the CPU.");
            await Capture("02-landed");
            board.SetPosition(session.Snapshot, black.From, session.GetLegalDestinations(black.From));
            Require(board.VisibleLastMove == red, "Opponent selection must retain previous move.");
            await Pause();
            await Capture("03-opponent-selected");
            board.SetPosition(session.Snapshot);
            Require(board.VisibleLastMove == red, "Same-position refresh must retain previous move.");
            await Play(session, board, black);
            Require(board.VisibleLastMove == black, "Reply must replace previous move.");
            board.BottomSide = PieceSide.Black;
            board.ApplyAppearance(BoardSkinKind.Classic, true);
            board.SetFeedbackAnimated(false);
            await Pause();
            Require(board.VisibleLastMove == black, "Skin/orientation/settings must retain move.");
            Require(!board.IsProcessing(), "Static feedback must not run a CPU animation loop.");
            await Capture("04-flipped-static");
            Require(session.Undo(), "Undo must succeed.");
            board.SetPosition(session.Snapshot);
            Require(board.VisibleLastMove is null, "Undo must remove stale markers.");
            session.NewGame();
            board.SetPosition(session.Snapshot);
            Require(board.VisibleLastMove is null, "Restart must remove markers.");

            // Exercise capture feedback without adding a second move application path.
            board.SetFeedbackAnimated(true);
            await Play(session, board, red);
            await Play(session, board, black);
            var capture = new ChessMove(red.To, black.To);
            await Play(session, board, capture);
            Require(board.VisibleLastMove == capture, "Capture must mark the new landing square.");
            await Capture("05-capture");
            await VerifySemanticAtlas(session.Snapshot);
            await VerifyJieqiBackAndReveal(board);
            GD.Print("[Xiangqi.Feedback.Smoke] PASS themed pieces/feedback, Jieqi back/reveal, fallback, selection, landing, reply, refresh, undo, restart, capture, flip and idle CPU.");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError($"[Xiangqi.Feedback.Smoke] FAIL {error}");
            GetTree().Quit(2);
        }
    }

    private async Task Play(GameSession session, BoardView board, ChessMove move)
    {
        var before = session.Snapshot;
        var result = session.TryMove(move.From, move.To);
        Require(result.IsSuccess, $"Smoke move {move} must be legal.");
        board.AnimateMove(before, session.Snapshot, move, result.CapturedPiece is not null);
        Require(board.VisibleLastMove is null, "Previous feedback must disappear as next move starts.");
        await Pause();
    }

    private async Task Pause() => await ToSignal(GetTree().CreateTimer(0.55), SceneTreeTimer.SignalName.Timeout);

    private async Task Capture(string name)
    {
        if (!OS.GetCmdlineUserArgs().Contains("--capture-feedback") || DisplayServer.GetName() == "headless") return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var directory = ProjectSettings.GlobalizePath("res://build/feedback-qa");
        System.IO.Directory.CreateDirectory(directory);
        using var image = GetViewport().GetTexture().GetImage();
        Require(image.SavePng(System.IO.Path.Combine(directory, name + ".png")) == Error.Ok, "Capture must save.");
    }

    private static void VerifyUnsafeManifestRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xiangqi-theme-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "theme.xml"),
                "<!DOCTYPE theme [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]>" +
                "<theme schemaVersion='1' id='unsafe' displayName='unsafe'>&xxe;</theme>");
            RequireThrows<XmlException>(() => new ThemeManifestReader().Read(Path.Combine(directory, "theme.xml")),
                "DTD and external entities must be rejected by XmlReader.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private async Task VerifySemanticAtlas(GameSnapshot snapshot)
    {
        var root = Path.Combine(Path.GetTempPath(), "xiangqi-atlas-smoke-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "atlas-smoke");
        Directory.CreateDirectory(package);
        try
        {
            using (var image = Image.CreateEmpty(70, 50, false, Image.Format.Rgba8))
            {
                image.Fill(Colors.White);
                Require(image.SavePng(Path.Combine(package, "pieces.png")) == Error.Ok,
                    "Temporary semantic atlas must save.");
            }
            File.WriteAllText(Path.Combine(package, "theme.xml"),
                "<theme schemaVersion='1' id='atlas-smoke' displayName='Atlas Smoke' inherits='classic'>" +
                "<board mode='classic' texture='pieces.png'/>" +
                "<pieces atlas='pieces.png' layout='semantic-v1'/></theme>");
            var catalog = new ThemePackageCatalog(ThemePackageCatalog.ResolveBuiltInRoot(), root);
            var atlasBoard = new BoardView(catalog) { Visible = false, Size = new Vector2(400, 440) };
            AddChild(atlasBoard);
            atlasBoard.ApplyAppearance("atlas-smoke", false);
            atlasBoard.SetPosition(snapshot);
            Require(atlasBoard.UsingPieceAtlas, "A semantic-v1 package must reach the real classic piece renderer.");
            Require(atlasBoard.HasThemedBoardMaterial, "A package texture must reach the real classic board renderer.");
            RemoveChild(atlasBoard);
            atlasBoard.QueueFree();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        finally { Directory.Delete(root, true); }
    }

    private async Task VerifyJieqiBackAndReveal(BoardView board)
    {
        var covered = new ChessPiece(901, PieceSide.Red, BuiltInPieceKinds.Covered);
        var revealed = covered with { Kind = BuiltInPieceKinds.Horse };
        Require(BoardView.PieceLabel(covered).Length == 0,
            "A covered Jieqi piece must use the ornamented back and contain no 暗 label.");
        var from = new BoardPosition(1, 0); var to = new BoardPosition(2, 2);
        var before = new GameSnapshot([new PlacedPiece(from, covered)], PieceSide.Red,
            false, false, null, GameEndReason.None, 0);
        var after = new GameSnapshot([new PlacedPiece(to, revealed)], PieceSide.Black,
            false, false, null, GameEndReason.None, 1);
        board.SetCoveredIdentityHints(null, false);
        board.SetPosition(before);
        board.AnimateMove(before, after, new ChessMove(from, to), false);
        Require(board.HasRevealTransition,
            "Covered-to-visible movement must append a back-to-front reveal transition.");
        await ToSignal(GetTree().CreateTimer(0.95), SceneTreeTimer.SignalName.Timeout);
        Require(!board.IsAnimating, "Jieqi reveal must finish and release the board animation state.");
        await Capture("06-jieqi-revealed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}
