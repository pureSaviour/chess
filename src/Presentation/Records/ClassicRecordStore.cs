using Godot;
using System.Text.Json;
using Xiangqi.Core.Application;
using Xiangqi.GameHost;

namespace Xiangqi.Presentation.Records;

/// <summary>
/// Owns validated UTF-8 PGN/ICCS files below the classic module record root. Recent records are
/// capped at ten; saved records and user-selected exports are never evicted. Export validates and
/// reserializes the source record but does not copy display-only sidecars.
/// </summary>
public static class ClassicRecordStore
{
    private const int RecentLimit = 10;
    private static readonly GameRecordStorage Storage = new(
        ProjectSettings.GlobalizePath("user://games"), new("xiangqi.classic"), ".pgn", "xiangqi",
        ProjectSettings.GlobalizePath("user://records/classic"));
    private sealed record LegacyPlayer(string Name, bool IsLocal, int AvatarWidth = 0,
        int AvatarHeight = 0, byte[]? AvatarRgba = null);
    private sealed record LegacyPresentation(DateTimeOffset PlayedAt, string Mode,
        LegacyPlayer Red, LegacyPlayer Black, string Result);
    public sealed record Entry(string RecordPath, MatchRecordPresentation Presentation);

    public static string SaveRecent(ClassicGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, false, presentation);
    public static string SavePermanent(ClassicGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, true, presentation);

    public static IReadOnlyList<string> List(bool saved)
    {
        return Storage.List(saved);
    }

    public static IReadOnlyList<Entry> ListEntries(bool saved) => List(saved)
        .Select(path => new Entry(path, LoadPresentation(path))).ToArray();

    public static ClassicGameRecord Load(string path)
    {
        var full = Storage.ValidatePath(path);
        var text = File.ReadAllText(full);
        if (!XiangqiPgnCodec.TryParse(text, out var record, out var error)) throw new InvalidDataException(error);
        return record!;
    }

    /// <summary>Revalidates a recent entry and writes a new saved record plus shared sidecar.</summary>
    public static string SavePermanent(Entry entry) =>
        SavePermanent(Load(entry.RecordPath), entry.Presentation);

    /// <summary>
    /// Revalidates a store-owned source and writes only the standard PGN/ICCS payload to the
    /// caller-selected .pgn destination. The source and its display sidecar remain unchanged.
    /// </summary>
    public static void Export(string recordPath, string destinationPath)
    {
        RecordFileExporter.WriteUtf8(destinationPath, ".pgn",
            XiangqiPgnCodec.Serialize(Load(recordPath)));
    }

    private static string Save(ClassicGameRecord record, bool permanent, MatchRecordPresentation? presentation)
    {
        var path = Storage.CreatePath(permanent);
        Storage.WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(XiangqiPgnCodec.Serialize(record)));
        if (presentation is not null) MatchRecordPresentationStore.Save(path, presentation);
        if (!permanent) Storage.PruneRecent(RecentLimit, MatchRecordPresentationStore.DeleteSidecar);
        return path;
    }

    private static MatchRecordPresentation LoadPresentation(string pgnPath)
    {
        // Releases before the shared sidecar schema stored Red/Black explicitly. Read that shape
        // first so existing user records retain which side was local.
        try
        {
            var metadata = MetadataPath(pgnPath);
            if (File.Exists(metadata) && new FileInfo(metadata).Length <= 256 * 1024)
            {
                var legacy = JsonSerializer.Deserialize<LegacyPresentation>(File.ReadAllText(metadata));
                if (legacy?.Red is not null && legacy.Black is not null)
                {
                    static RecordPlayer Convert(LegacyPlayer player) => new(player.Name, player.IsLocal,
                        player.AvatarWidth, player.AvatarHeight, player.AvatarRgba);
                    var local = legacy.Red.IsLocal ? Convert(legacy.Red) : Convert(legacy.Black);
                    var opponent = legacy.Red.IsLocal ? Convert(legacy.Black) : Convert(legacy.Red);
                    var redWon = legacy.Result.Contains("红方胜", StringComparison.Ordinal) || legacy.Result == "1-0";
                    var blackWon = legacy.Result.Contains("黑方胜", StringComparison.Ordinal) || legacy.Result == "0-1";
                    var draw = legacy.Result.Contains('和') || legacy.Result == "1/2-1/2";
                    var localWon = legacy.Red.IsLocal ? redWon : blackWon;
                    return new(legacy.PlayedAt, legacy.Mode, local, [opponent],
                        draw ? "和" : localWon ? "胜" : "败");
                }
            }
        }
        catch (Exception error) { GD.PushWarning($"忽略损坏的棋谱展示信息：{error.Message}"); }
        return MatchRecordPresentationStore.Load(pgnPath, () =>
        {
            var record = Load(pgnPath);
            var red = record.Tags.GetValueOrDefault("Red", "红方");
            var black = record.Tags.GetValueOrDefault("Black", "黑方");
            var result = record.Tags.GetValueOrDefault("Result", "*");
            return new(File.GetLastWriteTime(pgnPath),
                record.Tags.GetValueOrDefault("Event", "经典象棋"), new(red, true), [new(black, false)],
                result == "1-0" ? "胜" : result == "1/2-1/2" ? "和" : result == "0-1" ? "败" : "未决");
        });
    }
    private static string MetadataPath(string pgnPath) => pgnPath + ".meta.json";

}
