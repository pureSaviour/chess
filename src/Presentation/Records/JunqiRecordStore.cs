using System.Text;
using Godot;
using Xiangqi.Core.Application.Junqi;
using Xiangqi.GameHost;

namespace Xiangqi.Presentation.Records;

/// <summary>Owns bounded AC2 record files under user://; recent records are capped at ten.</summary>
public static class JunqiRecordStore
{
    static JunqiRecordStore() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    private static readonly GameRecordStorage Storage = new(
        ProjectSettings.GlobalizePath("user://games"), new("junqi.standard"), ".ac2", "junqi",
        ProjectSettings.GlobalizePath("user://records/junqi"));
    public sealed record Entry(string RecordPath, MatchRecordPresentation Presentation);
    public static string SaveRecent(JunqiGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, false, presentation);
    public static string SavePermanent(JunqiGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, true, presentation);
    /// <summary>Revalidates a recent entry and writes a new saved record plus shared sidecar.</summary>
    public static string SavePermanent(Entry entry) => SavePermanent(Load(entry.RecordPath), entry.Presentation);
    public static IReadOnlyList<string> List(bool saved)
    {
        return Storage.List(saved);
    }
    public static JunqiGameRecord Load(string path)
    {
        var full = Storage.ValidatePath(path);
        var bytes = File.ReadAllBytes(full);
        if (bytes.Length > JunqiAc2Codec.MaximumTextLength) throw new InvalidDataException("军旗棋谱超过 4 MiB。");
        if (!JunqiAc2Codec.TryParse(Gb2312().GetString(bytes), out var record, out var error)) throw new InvalidDataException(error);
        return record!;
    }
    public static IReadOnlyList<Entry> ListEntries(bool saved) => List(saved).Select(path => new Entry(path,
        MatchRecordPresentationStore.Load(path, () => new(File.GetLastWriteTime(path), "军旗",
            new("先手", true), [new("后手", false)], "未决")))).ToArray();
    private static string Save(JunqiGameRecord record, bool permanent, MatchRecordPresentation? presentation)
    {
        var path = Storage.CreatePath(permanent);
        Storage.WriteAtomic(path, Gb2312().GetBytes(JunqiAc2Codec.Serialize(record)));
        if (presentation is not null) MatchRecordPresentationStore.Save(path, presentation);
        if (!permanent) Storage.PruneRecent(10, MatchRecordPresentationStore.DeleteSidecar);
        return path;
    }
    private static Encoding Gb2312() => Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
}
