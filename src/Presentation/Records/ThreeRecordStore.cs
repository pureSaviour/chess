using Godot;
using Xiangqi.Core.Application.ThreeKingdoms;

namespace Xiangqi.Presentation.Records;

public static class ThreeRecordStore
{
    public static string SaveRecent(ThreeGameRecord record) => Save(record, false);
    public static string SavePermanent(ThreeGameRecord record) => Save(record, true);
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
    private static string Save(ThreeGameRecord record, bool saved)
    {
        var directory = DirectoryPath(saved); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"three-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.pgn");
        File.WriteAllText(path, ThreeKingdomsPgnCodec.Serialize(record));
        if (!saved) foreach (var stale in List(false).Skip(10)) File.Delete(stale);
        return path;
    }
    private static string DirectoryPath(bool saved) => ProjectSettings.GlobalizePath($"user://records/three/{(saved ? "saved" : "recent")}");
}
