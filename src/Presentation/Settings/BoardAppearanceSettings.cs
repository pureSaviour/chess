namespace Xiangqi.Presentation.Settings;

/// <summary>
/// Stable persisted IDs for complete board presentation themes. IDs remain compatible with the
/// previous board-skin setting while the resolved theme now also styles pieces and feedback.
/// </summary>
public enum BoardSkinKind
{
    Classic = 0,
    Spring = 1
}

public static class BoardSkinKindExtensions
{
    public static string DisplayName(this BoardSkinKind skin) => BoardThemeCatalog.Default.Resolve(skin).DisplayName;
}
