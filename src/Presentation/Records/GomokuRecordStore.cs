using Godot;
using Xiangqi.Core.Application.Gomoku;
using Xiangqi.GameHost;

namespace Xiangqi.Presentation.Records;

/// <summary>Owns UTF-8 SGF files under the fixed Gomoku record directory.</summary>
public static class GomokuRecordStore
{
    private static readonly GameRecordStorage Storage = new(
        ProjectSettings.GlobalizePath("user://games"), new("gomoku.standard"), ".sgf", "gomoku",
        ProjectSettings.GlobalizePath("user://records/gomoku"));
    public sealed record Entry(string RecordPath, MatchRecordPresentation Presentation);
    public static string SaveRecent(GomokuGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, false, presentation);
    public static string SavePermanent(GomokuGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, true, presentation);
    public static IReadOnlyList<string> List(bool saved) => Storage.List(saved);
    public static GomokuGameRecord Load(string path)
    { var full=Storage.ValidatePath(path); if(!GomokuSgfCodec.TryParse(File.ReadAllText(full),out var record,out var error)) throw new InvalidDataException(error); return record!; }
    public static IReadOnlyList<Entry> ListEntries(bool saved)=>List(saved).Select(path=>new Entry(path,
        MatchRecordPresentationStore.Load(path,()=>new(File.GetLastWriteTime(path),"五子棋",new("黑方",true),[new("白方",false)],"未决")))).ToArray();
    private static string Save(GomokuGameRecord record,bool saved,MatchRecordPresentation? presentation) { var path=Storage.CreatePath(saved); Storage.WriteAtomic(path,System.Text.Encoding.UTF8.GetBytes(GomokuSgfCodec.Write(record))); if(presentation is not null)MatchRecordPresentationStore.Save(path,presentation); if(!saved)Storage.PruneRecent(10,MatchRecordPresentationStore.DeleteSidecar); return path; }
}
