using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Application.ThreeKingdoms;
using Xiangqi.Core.Domain.ThreeKingdoms;
using Xiangqi.Core.Domain.Topology;
using Xiangqi.Presentation.Boards;
using Xiangqi.Presentation.Screens;
using Xiangqi.Presentation.Settings;

namespace Xiangqi.Presentation.Diagnostics;

/// <summary>仅通过 Main 的 debug 命令入口运行，验证实际引擎投影/控件并可保存验收截图。</summary>
public partial class ThreeBoardSmoke(AppSettings settings) : Control
{
    public override async void _Ready()
    {
        try
        {
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            settings = settings.Copy(); settings.BoardSkin = BoardSkinKind.Classic;
            settings.ShowBoardCoordinates = true; settings.EffectsEnabled = true;
            var themes = ThemePackageCatalog.CreateDefault();
            var controller = new LocalThreeMatchController(new TimeControl(900, 60), AdvisorConquestPolicy.SharedTerritory);
            var screen = new ThreeGameScreen(settings, () => GetTree().Quit(), controller, themes: themes); AddChild(screen);
            await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
            var board = Descendants(screen).OfType<ThreeBoardView>().Single();
            var projection = board.Projection;
            foreach (var own in Enum.GetValues<Kingdom>())
            {
                board.SetBottomKingdom(own);
                projection.Resize(board.Size);
                var homeY = Enumerable.Range(0, 9).Average(file =>
                    projection.Point(new BoardCellId(45 * (int)own + file)).Y);
                if (homeY <= projection.Center.Y)
                    throw new InvalidOperationException($"{own} home edge is not at the bottom of its own view.");
                for (var i = 0; i < 135; i++)
                    if (projection.Hit(projection.Point(new(i))) != new BoardCellId(i))
                        throw new InvalidOperationException($"Projection hit roundtrip differs in {own} view.");
                await Capture($"view-{own.ToString().ToLowerInvariant()}");
            }
            board.SetBottomKingdom(Kingdom.Wei);
            projection.Resize(board.Size);
            var hiddenBoundaries = ThreeKingdomsTopology.Instance.Edges.Count(ThreeBoardView.IsTerritoryBoundaryEdge);
            if (hiddenBoundaries != 15 ||
                ThreeBoardView.TerritoryBoundaryName(Kingdom.Wei, Kingdom.Shu) != "秦岭" ||
                ThreeBoardView.TerritoryBoundaryName(Kingdom.Wei, Kingdom.Wu) != "江淮" ||
                ThreeBoardView.TerritoryBoundaryName(Kingdom.Shu, Kingdom.Wu) != "三峡")
                throw new InvalidOperationException("Territory boundaries must suppress all 15 bridge grid lines and use approved names.");
            for (var kingdom = 0; kingdom < 3; kingdom++)
            {
                var center = projection.Point(new BoardCellId(45 * kingdom + 13));
                var palace = board.PalaceSegments(kingdom);
                if (palace.Count != 4 || palace.Any(line => line.From != center && line.To != center))
                    throw new InvalidOperationException("Every palace diagonal stroke must terminate at the logical center.");
            }
            double area = 0;
            foreach (var face in ThreeKingdomsTopology.Instance.Faces)
            {
                double signed = 0;
                for (var i = 0; i < face.Count; i++) signed += projection.Point(face[i]).Cross(projection.Point(face[(i + 1) % face.Count]));
                area += Math.Abs(signed) / 2;
            }
            var expected = 3 * Math.Sqrt(3) / 2 * projection.Radius * projection.Radius;
            if (Math.Abs(area - expected) > 1) throw new InvalidOperationException("Board faces do not tile the hexagon.");
            var edges = ThreeKingdomsTopology.Instance.Edges;
            for (var i = 0; i < edges.Count; i++)
            for (var j = i + 1; j < edges.Count; j++)
            {
                var a = edges[i]; var b = edges[j];
                if (a.A == b.A || a.A == b.B || a.B == b.A || a.B == b.B) continue;
                var p = projection.Point(a.A); var q = projection.Point(a.B);
                var r = projection.Point(b.A); var s = projection.Point(b.B);
                if ((q - p).Cross(r - p) * (q - p).Cross(s - p) < -0.01f &&
                    (s - r).Cross(p - r) * (s - r).Cross(q - r) < -0.01f)
                    throw new InvalidOperationException("Nonadjacent edges cross.");
            }
            await Capture("opening");
            controller.Submit(new(ThreeCommandKind.Move, Kingdom.Wei, 27, 36));
            if (!board.IsAnimating) throw new InvalidOperationException("A committed three-player move must start interpolation.");
            board.SetPosition(controller.Session.Game.Snapshot, new(72), controller.Session.Game.LegalDestinations(new(72)));
            await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
            if (board.IsAnimating || board.VisibleLastMove != new KingdomMove(new(27), new(36)))
                throw new InvalidOperationException("Move interpolation must land and reveal the latest-move feedback.");
            await Capture("feedback");
            settings.BoardSkin = BoardSkinKind.Spring;
            board.ApplyAppearance(settings);
            if (!board.HasThemedBoardMaterial)
                throw new InvalidOperationException("Spring theme must reach the three-player board consumer.");
            await Capture("spring");
            var dialog = Descendants(screen).OfType<InGameSettingsDialog>().Single();
            dialog.PopupFor(settings);
            await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
            await Capture("settings");
            GD.Print("[Xiangqi.Three.Smoke] PASS three bottom-facing views, named border gaps, palace centers, move interpolation, selection/landing feedback, 405 rotated hit roundtrips, 243 topology edges, complete hexagon area and room settings.");
            GetTree().Quit();
        }
        catch (Exception e) { GD.PushError(e.ToString()); GetTree().Quit(3); }
    }
    private async Task Capture(string name)
    {
        if (!OS.GetCmdlineUserArgs().Contains("--capture-three") || DisplayServer.GetName() == "headless") return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var path = ProjectSettings.GlobalizePath("res://build/three-qa"); System.IO.Directory.CreateDirectory(path);
        using var picture = GetViewport().GetTexture().GetImage();
        var error = picture.SavePng(System.IO.Path.Combine(path, name + ".png"));
        if (error != Error.Ok) throw new IOException($"Capture failed: {error}");
    }
    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var child in root.GetChildren())
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
}
