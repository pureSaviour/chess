using Godot;
using Xiangqi.Core.Application.Jieqi;

namespace Xiangqi.Presentation.Records;

/// <summary>Owns Jieqi PGN files; recent records are capped at ten and saved records are never evicted.</summary>
public static class JieqiRecordStore
{
    public sealed record Entry(string RecordPath, MatchRecordPresentation Presentation);
    public static string SaveRecent(JieqiGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, false, presentation);
    public static string SavePermanent(JieqiGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, true, presentation);
    public static IReadOnlyList<string> List(bool saved)
    {
        var directory = DirectoryPath(saved);
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.pgn").OrderByDescending(File.GetLastWriteTimeUtc).ToArray() : [];
    }
    public static JieqiGameRecord Load(string path)
    {
        var full = Path.GetFullPath(path);
        var recent = Path.GetFullPath(DirectoryPath(false)); var saved = Path.GetFullPath(DirectoryPath(true));
        if (!IsUnder(full, recent) && !IsUnder(full, saved))
            throw new UnauthorizedAccessException("棋谱不在揭棋记录目录中。");
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
        var directory = DirectoryPath(permanent); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"jieqi-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.pgn");
        var temporary = path + ".tmp"; File.WriteAllText(temporary, JieqiPgnCodec.Serialize(record)); File.Move(temporary, path);
        if (presentation is not null) MatchRecordPresentationStore.Save(path, presentation);
        if (!permanent) foreach (var stale in List(false).Skip(10))
        { File.Delete(stale); MatchRecordPresentationStore.DeleteSidecar(stale); }
        return path;
    }
    private static string DirectoryPath(bool saved) => ProjectSettings.GlobalizePath($"user://records/jieqi/{(saved ? "saved" : "recent")}");
    private static bool IsUnder(string file, string directory)
    {
        var relative = Path.GetRelativePath(directory, file);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
