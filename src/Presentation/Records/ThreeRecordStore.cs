using Godot;
using Xiangqi.Core.Application.ThreeKingdoms;
using Xiangqi.GameHost;

namespace Xiangqi.Presentation.Records;

public static class ThreeRecordStore
{
    private static readonly GameRecordStorage Storage = new(
        ProjectSettings.GlobalizePath("user://games"), new("xiangqi.three-kingdoms"), ".pgn", "three",
        ProjectSettings.GlobalizePath("user://records/three"));
    public sealed record Entry(string RecordPath, MatchRecordPresentation Presentation);
    public static string SaveRecent(ThreeGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, false, presentation);
    public static string SavePermanent(ThreeGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, true, presentation);
    /// <summary>Revalidates a recent entry and writes a new saved record plus shared sidecar.</summary>
    public static string SavePermanent(Entry entry) => SavePermanent(Load(entry.RecordPath), entry.Presentation);
    public static IReadOnlyList<string> List(bool saved)
    {
        return Storage.List(saved);
    }
    public static ThreeGameRecord Load(string path)
    {
        path = Storage.ValidatePath(path);
        var text = File.ReadAllText(path);
        if (!ThreeKingdomsPgnCodec.TryParse(text, out var record, out var error)) throw new InvalidDataException(error);
        return record!;
    }
    public static IReadOnlyList<Entry> ListEntries(bool saved) => List(saved).Select(path => new Entry(path,
        MatchRecordPresentationStore.Load(path, () => new(File.GetLastWriteTime(path), "三国象棋",
            new("魏国", true), [new("蜀国", false), new("吴国", false)], "未决")))).ToArray();
    private static string Save(ThreeGameRecord record, bool saved, MatchRecordPresentation? presentation)
    {
        var path = Storage.CreatePath(saved);
        Storage.WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(ThreeKingdomsPgnCodec.Serialize(record)));
        if (presentation is not null) MatchRecordPresentationStore.Save(path, presentation);
        if (!saved) Storage.PruneRecent(10, MatchRecordPresentationStore.DeleteSidecar);
        return path;
    }
}
