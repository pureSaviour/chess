using Godot;
using Xiangqi.Core.Application.Go;
using Xiangqi.GameHost;

namespace Xiangqi.Presentation.Records;

/// <summary>
/// Owns validated UTF-8 SGF FF[4]/GM[1] files below the fixed Go record root. Recent records are
/// capped at ten; saved records and user-selected exports are retained. Every copy/export reloads
/// and replays the source first, while private display sidecars never enter the SGF export.
/// </summary>
public static class GoRecordStore
{
    private static readonly GameRecordStorage Storage = new(
        ProjectSettings.GlobalizePath("user://games"), new("go.chinese"), ".sgf", "go",
        ProjectSettings.GlobalizePath("user://records/go"));
    public sealed record Entry(string RecordPath, MatchRecordPresentation Presentation);
    public static string SaveRecent(GoGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, false, presentation);
    public static string SavePermanent(GoGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, true, presentation);
    public static string SavePermanent(Entry entry) => SavePermanent(Load(entry.RecordPath), entry.Presentation);
    public static IReadOnlyList<string> List(bool saved) => Storage.List(saved);
    public static GoGameRecord Load(string path)
    {
        var full = Storage.ValidatePath(path);
        if (!GoSgfCodec.TryParse(File.ReadAllText(full), out var record, out var error)) throw new InvalidDataException(error);
        return record!;
    }
    public static IReadOnlyList<Entry> ListEntries(bool saved) => List(saved).Select(path => new Entry(path,
        MatchRecordPresentationStore.Load(path, () => new(File.GetLastWriteTime(path), "围棋",
            new("黑方", true), [new("白方", false)], "未决")))).ToArray();
    public static void Export(string recordPath, string destinationPath) =>
        RecordFileExporter.WriteUtf8(destinationPath, ".sgf", GoSgfCodec.Write(Load(recordPath)));
    private static string Save(GoGameRecord record, bool saved, MatchRecordPresentation? presentation)
    {
        var path = Storage.CreatePath(saved);
        Storage.WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(GoSgfCodec.Write(record)));
        if (presentation is not null) MatchRecordPresentationStore.Save(path, presentation);
        if (!saved) Storage.PruneRecent(10, MatchRecordPresentationStore.DeleteSidecar);
        return path;
    }
}
