using Godot;
using Xiangqi.Core.Application.Gomoku;

namespace Xiangqi.Presentation.Records;

/// <summary>Owns UTF-8 SGF files under the fixed Gomoku record directory.</summary>
public static class GomokuRecordStore
{
    public sealed record Entry(string RecordPath, MatchRecordPresentation Presentation);
    public static string SaveRecent(GomokuGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, false, presentation);
    public static string SavePermanent(GomokuGameRecord record, MatchRecordPresentation? presentation = null) => Save(record, true, presentation);
    public static IReadOnlyList<string> List(bool saved) { var dir=DirectoryPath(saved); return Directory.Exists(dir)?Directory.GetFiles(dir,"*.sgf").OrderByDescending(File.GetLastWriteTimeUtc).ToArray():[]; }
    public static GomokuGameRecord Load(string path)
    { var full=Path.GetFullPath(path); if(!IsUnder(full,Path.GetFullPath(DirectoryPath(false)))&&!IsUnder(full,Path.GetFullPath(DirectoryPath(true)))) throw new UnauthorizedAccessException("棋谱不在五子棋记录目录。"); if(!GomokuSgfCodec.TryParse(File.ReadAllText(full),out var record,out var error)) throw new InvalidDataException(error); return record!; }
    public static IReadOnlyList<Entry> ListEntries(bool saved)=>List(saved).Select(path=>new Entry(path,
        MatchRecordPresentationStore.Load(path,()=>new(File.GetLastWriteTime(path),"五子棋",new("黑方",true),[new("白方",false)],"未决")))).ToArray();
    private static string Save(GomokuGameRecord record,bool saved,MatchRecordPresentation? presentation) { var dir=DirectoryPath(saved); Directory.CreateDirectory(dir); var path=Path.Combine(dir,$"gomoku-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.sgf"); var temporary=path+".tmp"; File.WriteAllText(temporary,GomokuSgfCodec.Write(record)); File.Move(temporary,path); if(presentation is not null)MatchRecordPresentationStore.Save(path,presentation); if(!saved) foreach(var stale in List(false).Skip(10)){File.Delete(stale);MatchRecordPresentationStore.DeleteSidecar(stale);} return path; }
    private static string DirectoryPath(bool saved)=>ProjectSettings.GlobalizePath($"user://records/gomoku/{(saved?"saved":"recent")}");
    private static bool IsUnder(string file,string directory){var relative=Path.GetRelativePath(directory,file);return !Path.IsPathRooted(relative)&&relative!=".."&&!relative.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal);}
}
