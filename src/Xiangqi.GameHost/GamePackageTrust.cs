namespace Xiangqi.GameHost;

/// <summary>Execution boundary selected by a package author and enforced by the host.</summary>
public enum GamePackageExecutionMode
{
    ResourceOnly,
    Sandboxed,
    FullTrust
}

/// <summary>
/// Requested host capabilities. FullTrust means arbitrary in-process code and is never represented
/// as a collection of restricted permissions; these flags are meaningful only for resource/sandbox modules.
/// </summary>
[Flags]
public enum GamePackagePermissions
{
    None = 0,
    ModuleStorage = 1 << 0,
    RoomNetwork = 1 << 1,
    PlayerProfile = 1 << 2,
    ExternalProcess = 1 << 3,
    NativeCode = 1 << 4,
    UnrestrictedFileSystem = 1 << 5
}

public sealed record GamePackageTrustRequest(
    GameModuleId ModuleId,
    Version Version,
    GamePackageExecutionMode ExecutionMode,
    GamePackagePermissions Permissions,
    string PackageSha256,
    string Publisher,
    string? PublisherKeyFingerprint,
    bool SignatureValid);

public enum GamePackageTrustDisposition
{
    Reject,
    RequireRestrictedModuleConfirmation,
    RequireFullTrustConfirmation,
    AllowPreviouslyConfirmedPackage
}

/// <summary>
/// Pure trust gate evaluated before extracting executable content. A prior approval is tied to the
/// exact SHA-256 package hash; publisher, version or permission changes therefore require a new
/// decision. This policy does not claim that a signature or user confirmation makes code safe.
/// </summary>
public static class GamePackageTrustPolicy
{
    private const GamePackagePermissions SandboxForbidden = GamePackagePermissions.ExternalProcess |
        GamePackagePermissions.NativeCode | GamePackagePermissions.UnrestrictedFileSystem;

    public static GamePackageTrustDisposition Evaluate(GamePackageTrustRequest request,
        IReadOnlySet<string>? approvedPackageHashes = null, bool developerMode = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Version);
        ValidateText(request.Publisher, 128, "发布者");
        var hash = NormalizeHash(request.PackageSha256, "包 SHA-256");
        if (request.PublisherKeyFingerprint is { } fingerprint) NormalizeHash(fingerprint, "发布者密钥指纹");
        if (request.SignatureValid && request.PublisherKeyFingerprint is null)
            throw new ArgumentException("有效签名必须包含发布者密钥指纹。", nameof(request));

        var intrinsicallyRejected = request.ExecutionMode switch
        {
            GamePackageExecutionMode.ResourceOnly => request.Permissions != GamePackagePermissions.None,
            GamePackageExecutionMode.Sandboxed => (request.Permissions & SandboxForbidden) != 0,
            GamePackageExecutionMode.FullTrust => !request.SignatureValid && !developerMode,
            _ => true
        };
        if (intrinsicallyRejected) return GamePackageTrustDisposition.Reject;

        if (approvedPackageHashes?.Any(value =>
                string.Equals(value, hash, StringComparison.OrdinalIgnoreCase)) == true)
            return GamePackageTrustDisposition.AllowPreviouslyConfirmedPackage;

        return request.ExecutionMode switch
        {
            GamePackageExecutionMode.ResourceOnly =>
                GamePackageTrustDisposition.RequireRestrictedModuleConfirmation,
            GamePackageExecutionMode.Sandboxed =>
                GamePackageTrustDisposition.RequireRestrictedModuleConfirmation,
            GamePackageExecutionMode.FullTrust =>
                GamePackageTrustDisposition.RequireFullTrustConfirmation,
            _ => GamePackageTrustDisposition.Reject
        };
    }

    private static string NormalizeHash(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException($"{field} 无效。", nameof(value));
        return value.ToUpperInvariant();
    }

    private static void ValidateText(string value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException($"{field}无效。", nameof(value));
    }
}
