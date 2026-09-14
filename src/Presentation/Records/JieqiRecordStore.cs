using Godot;
using Xiangqi.Core.Application.Jieqi;
using Xiangqi.GameHost;

namespace Xiangqi.Presentation.Records;

/// <summary>Owns Jieqi PGN files; recent records are capped at ten and saved records are never evicted.</summary>
public static class JieqiRecordStore
{
    private static readonly GameRecordStorage Storage = new(
        ProjectSettings.GlobalizePath("user://games"), new("xiangqi.jieqi"), ".pgn", "jieqi",
        ProjectSettings.GlobalizePath("user://records/jieqi"));
    public sealed record Entry(string RecordPath, MatchRecordPresentation Presentation);
    public static string SaveRecent(JieqiGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, false, presentation);
    public static string SavePermanent(JieqiGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, true, presentation);
    /// <summary>Revalidates a recent entry and writes a new saved record plus shared sidecar.</summary>
    public static string SavePermanent(Entry entry) => SavePermanent(Load(entry.RecordPath), entry.Presentation);
    public static IReadOnlyList<string> List(bool saved)
    {
        return Storage.List(saved);
    }
    public static JieqiGameRecord Load(string path)
    {
        var full = Storage.ValidatePath(path);
        if (!JieqiPgnCodec.TryParse(File.ReadAllText(full), out var record, out var error)) throw new InvalidDataException(error);
        return record!;
    }
    public static IReadOnlyList<Entry> ListEntries(bool saved) => List(saved).Select(path =>
    {
        var record = Load(path);
        return new Entry(path, MatchRecordPresentationStore.Load(path, () => new(
            File.GetLastWriteTime(path), "揭棋", new(record.Tags.GetValueOrDefault("Red", "红方"), true),
            [new(record.Tags.GetValueOrDefault("Black", "黑方"), false)],
            record.Tags.GetValueOrDefault("Result", "*"))));
    }).ToArray();
    private static string Save(JieqiGameRecord record, bool permanent, MatchRecordPresentation? presentation)
    {
        var path = Storage.CreatePath(permanent);
        Storage.WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(JieqiPgnCodec.Serialize(record)));
        if (presentation is not null) MatchRecordPresentationStore.Save(path, presentation);
        if (!permanent) Storage.PruneRecent(10, MatchRecordPresentationStore.DeleteSidecar);
        return path;
    }
}
