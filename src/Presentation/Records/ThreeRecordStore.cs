using Godot;
using Xiangqi.Core.Application.ThreeKingdoms;

namespace Xiangqi.Presentation.Records;

public static class ThreeRecordStore
{
    public sealed record Entry(string RecordPath, MatchRecordPresentation Presentation);
    public static string SaveRecent(ThreeGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, false, presentation);
    public static string SavePermanent(ThreeGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, true, presentation);
    public static IReadOnlyList<string> List(bool saved)
    {
        var path = DirectoryPath(saved);
        return Directory.Exists(path) ? Directory.GetFiles(path, "*.pgn").OrderByDescending(File.GetLastWriteTimeUtc).ToArray() : [];
    }
    public static ThreeGameRecord Load(string path)
    {
        var text = File.ReadAllText(path);
        if (!ThreeKingdomsPgnCodec.TryParse(text, out var record, out var error)) throw new InvalidDataException(error);
        return record!;
    }
    public static IReadOnlyList<Entry> ListEntries(bool saved) => List(saved).Select(path => new Entry(path,
        MatchRecordPresentationStore.Load(path, () => new(File.GetLastWriteTime(path), "三国象棋",
            new("魏国", true), [new("蜀国", false), new("吴国", false)], "未决")))).ToArray();
    private static string Save(ThreeGameRecord record, bool saved, MatchRecordPresentation? presentation)
    {
        var directory = DirectoryPath(saved); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"three-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.pgn");
        File.WriteAllText(path, ThreeKingdomsPgnCodec.Serialize(record));
        if (presentation is not null) MatchRecordPresentationStore.Save(path, presentation);
        if (!saved) foreach (var stale in List(false).Skip(10))
        { File.Delete(stale); MatchRecordPresentationStore.DeleteSidecar(stale); }
        return path;
    }
    private static string DirectoryPath(bool saved) => ProjectSettings.GlobalizePath($"user://records/three/{(saved ? "saved" : "recent")}");
}
