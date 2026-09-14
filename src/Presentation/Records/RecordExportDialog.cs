using Godot;

namespace Xiangqi.Presentation.Records;

/// <summary>
/// Native save-file UI for one validated record format. The owning record screen supplies the
/// source record and a synchronous exporter; this dialog owns no gameplay state and writes only
/// after the user selects a filesystem destination. Errors are reported without changing the
/// source record, and the node is released with its screen.
/// </summary>
public partial class RecordExportDialog : FileDialog
{
    private readonly string _extension;
    private readonly Action<string, string> _export;
    private readonly AcceptDialog _result = new() { Title = "导出棋谱" };
    private string? _sourcePath;

    public RecordExportDialog(string extension, string formatLabel, Action<string, string> export)
    {
        if (string.IsNullOrWhiteSpace(extension) || extension[0] != '.')
            throw new ArgumentException("导出扩展名必须以句点开头。", nameof(extension));
        _extension = extension;
        _export = export ?? throw new ArgumentNullException(nameof(export));
        Title = $"导出{formatLabel}棋谱";
        FileMode = FileModeEnum.SaveFile;
        Access = AccessEnum.Filesystem;
        UseNativeDialog = true;
        Filters = [$"*{extension} ; {formatLabel}"];
        FileSelected += OnFileSelected;
        AddChild(_result);
    }

    public void Open(string sourcePath)
    {
        _sourcePath = sourcePath;
        CurrentFile = Path.GetFileNameWithoutExtension(sourcePath) + _extension;
        PopupCenteredRatio(0.78f);
    }

    private void OnFileSelected(string destination)
    {
        try
        {
            if (_sourcePath is null) throw new InvalidOperationException("没有可导出的棋谱。");
            if (!Path.GetExtension(destination).Equals(_extension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"导出文件必须使用 {_extension} 扩展名。");
            _export(_sourcePath, destination);
            _result.DialogText = $"已导出到：{destination}";
        }
        catch (Exception error)
        {
            _result.DialogText = $"导出失败：{error.Message}";
        }
        _result.PopupCentered(new Vector2I(680, 170));
    }

    public override void _ExitTree() => FileSelected -= OnFileSelected;
}
