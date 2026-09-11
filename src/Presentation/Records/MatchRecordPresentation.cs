using System.Text.Json;
using Godot;

namespace Xiangqi.Presentation.Records;

/// <summary>Bounded display-only identity copied into a record sidecar; it contains no source path or credential.</summary>
public sealed record RecordPlayer(string Name, bool IsLocal, int AvatarWidth = 0,
    int AvatarHeight = 0, byte[]? AvatarRgba = null);

/// <summary>Display metadata shared by record lists; gameplay codecs remain independent.</summary>
public sealed record MatchRecordPresentation(DateTimeOffset PlayedAt, string Mode,
    RecordPlayer Local, IReadOnlyList<RecordPlayer> Opponents, string Result);

/// <summary>Reads and atomically writes bounded display sidecars next to validated game records.</summary>
public static class MatchRecordPresentationStore
{
    public static string MetadataPath(string recordPath) => recordPath + ".meta.json";

    public static void Save(string recordPath, MatchRecordPresentation presentation)
    {
        var value = Validate(presentation);
        var path = MetadataPath(recordPath);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path);
    }

    public static MatchRecordPresentation Load(string recordPath, Func<MatchRecordPresentation> fallback)
    {
        try
        {
            var path = MetadataPath(recordPath);
            if (File.Exists(path) && new FileInfo(path).Length <= 512 * 1024)
                return Validate(JsonSerializer.Deserialize<MatchRecordPresentation>(File.ReadAllText(path))!);
        }
        catch (Exception error) { GD.PushWarning($"忽略损坏的棋谱展示信息：{error.Message}"); }
        return Validate(fallback());
    }

    public static void DeleteSidecar(string recordPath)
    {
        var path = MetadataPath(recordPath);
        if (File.Exists(path)) File.Delete(path);
    }

    private static MatchRecordPresentation Validate(MatchRecordPresentation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Opponents is null || value.Opponents.Count is < 1 or > 2 ||
            string.IsNullOrWhiteSpace(value.Mode) || value.Mode.Length > 64 ||
            value.Result is null || value.Result.Length > 32)
            throw new InvalidDataException("记录展示字段无效。");
        RecordPlayer Check(RecordPlayer player)
        {
            if (player is null) throw new InvalidDataException("玩家资料不能为空。");
            if (string.IsNullOrWhiteSpace(player.Name) || player.Name.Length > 64)
                throw new InvalidDataException("玩家名称无效。");
            var pixels = player.AvatarRgba ?? [];
            if (player.AvatarWidth is < 0 or > 64 || player.AvatarHeight is < 0 or > 64 ||
                pixels.Length > 64 * 64 * 4 ||
                (pixels.Length == 0 && (player.AvatarWidth != 0 || player.AvatarHeight != 0)) ||
                (pixels.Length > 0 && pixels.Length != player.AvatarWidth * player.AvatarHeight * 4))
                throw new InvalidDataException("头像数据无效。");
            return player with { Name = player.Name.Trim(), AvatarRgba = pixels };
        }
        return value with
        {
            Mode = value.Mode.Trim(),
            Local = Check(value.Local),
            Opponents = value.Opponents.Select(Check).ToArray()
        };
    }
}
