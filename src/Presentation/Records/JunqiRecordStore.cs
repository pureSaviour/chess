using System.Text;
using Godot;
using Xiangqi.Core.Application.Junqi;

namespace Xiangqi.Presentation.Records;

/// <summary>Owns bounded AC2 record files under user://; recent records are capped at ten.</summary>
public static class JunqiRecordStore
{
    static JunqiRecordStore() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static string SaveRecent(JunqiGameRecord record) => Save(record, false);
    public static string SavePermanent(JunqiGameRecord record) => Save(record, true);
    public static IReadOnlyList<string> List(bool saved)
    {
        var directory = DirectoryPath(saved);
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.ac2").OrderByDescending(File.GetLastWriteTimeUtc).ToArray() : [];
    }
    public static JunqiGameRecord Load(string path)
    {
        var full = Path.GetFullPath(path); var recent = Path.GetFullPath(DirectoryPath(false)); var saved = Path.GetFullPath(DirectoryPath(true));
        if (!IsUnder(full, recent) && !IsUnder(full, saved)) throw new UnauthorizedAccessException("棋谱不在军旗记录目录中。");
        var bytes = File.ReadAllBytes(full);
        if (bytes.Length > JunqiAc2Codec.MaximumTextLength) throw new InvalidDataException("军旗棋谱超过 4 MiB。");
        if (!JunqiAc2Codec.TryParse(Gb2312().GetString(bytes), out var record, out var error)) throw new InvalidDataException(error);
        return record!;
    }
    private static string Save(JunqiGameRecord record, bool permanent)
    {
        var directory = DirectoryPath(permanent); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"junqi-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.ac2"); var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, Gb2312().GetBytes(JunqiAc2Codec.Serialize(record))); File.Move(temporary, path);
        if (!permanent) foreach (var stale in List(false).Skip(10)) File.Delete(stale);
        return path;
    }
    private static Encoding Gb2312() => Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    private static string DirectoryPath(bool saved) => ProjectSettings.GlobalizePath($"user://records/junqi/{(saved ? "saved" : "recent")}");
    private static bool IsUnder(string file, string directory)
    {
        var relative = Path.GetRelativePath(directory, file);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
