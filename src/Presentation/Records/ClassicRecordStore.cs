using Godot;
using System.Text.Json;
using Xiangqi.Core.Application;
using Xiangqi.GameHost;

namespace Xiangqi.Presentation.Records;

/// <summary>
/// Owns local PGN files below user://records/classic. Recent records are capped at ten; saved
/// records are user-owned and never evicted. Writes are atomic within the same directory.
/// </summary>
public static class ClassicRecordStore
{
    private const int RecentLimit = 10;
    private static readonly GameRecordStorage Storage = new(
        ProjectSettings.GlobalizePath("user://games"), new("xiangqi.classic"), ".pgn", "xiangqi",
        ProjectSettings.GlobalizePath("user://records/classic"));
    public sealed record Player(string Name, bool IsLocal, int AvatarWidth = 0, int AvatarHeight = 0, byte[]? AvatarRgba = null);
    public sealed record Presentation(DateTimeOffset PlayedAt, string Mode, Player Red, Player Black, string Result);
    public sealed record Entry(string PgnPath, Presentation Presentation);

    public static string SaveRecent(ClassicGameRecord record, Presentation? presentation = null) => Save(record, false, presentation);
    public static string SavePermanent(ClassicGameRecord record, Presentation? presentation = null) => Save(record, true, presentation);

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

    private static string Save(ClassicGameRecord record, bool permanent, Presentation? presentation)
    {
        var path = Storage.CreatePath(permanent);
        Storage.WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(XiangqiPgnCodec.Serialize(record)));
        if (presentation is not null)
            File.WriteAllText(MetadataPath(path), JsonSerializer.Serialize(Validate(presentation)));
        if (!permanent) Storage.PruneRecent(RecentLimit, stale =>
        {
            var metadata = MetadataPath(stale); if (File.Exists(metadata)) File.Delete(metadata);
        });
        return path;
    }

    private static Presentation LoadPresentation(string pgnPath)
    {
        try
        {
            var metadata = MetadataPath(pgnPath);
            if (File.Exists(metadata) && new FileInfo(metadata).Length <= 256 * 1024)
                return Validate(JsonSerializer.Deserialize<Presentation>(File.ReadAllText(metadata))!);
        }
        catch (Exception error) { GD.PushWarning($"忽略损坏的棋谱展示信息：{error.Message}"); }
        var record = Load(pgnPath);
        var red = record.Tags.GetValueOrDefault("Red", "红方"); var black = record.Tags.GetValueOrDefault("Black", "黑方");
        var mode = record.Tags.GetValueOrDefault("Event", "经典象棋"); var result = record.Tags.GetValueOrDefault("Result", "*");
        return new Presentation(File.GetLastWriteTime(pgnPath), mode, new(red, true), new(black, false), result);
    }

    private static Presentation Validate(Presentation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Player Check(Player player)
        {
            if (string.IsNullOrWhiteSpace(player.Name) || player.Name.Length > 64) throw new InvalidDataException("玩家名称无效。");
            var pixels = player.AvatarRgba ?? [];
            if (player.AvatarWidth is < 0 or > 64 || player.AvatarHeight is < 0 or > 64 ||
                pixels.Length > 64 * 64 * 4 || (pixels.Length > 0 && pixels.Length != player.AvatarWidth * player.AvatarHeight * 4))
                throw new InvalidDataException("头像数据无效。");
            return player with { Name = player.Name.Trim(), AvatarRgba = pixels };
        }
        if (value.Mode.Length > 64 || value.Result.Length > 32) throw new InvalidDataException("记录展示字段过长。");
        return value with { Red = Check(value.Red), Black = Check(value.Black) };
    }
    private static string MetadataPath(string pgnPath) => pgnPath + ".meta.json";

}
