using System.Collections.ObjectModel;
using System.Text;

namespace Xiangqi.Core.Application;

/// <summary>
/// A transport-neutral room-chat item. Seats are stable room positions rather than platform IDs:
/// <see cref="TargetSeat"/> is null for the room or a zero-based private recipient. The sender and
/// recipient can both read a directed item; other seats retain replicated history for continuity
/// but must not present it. This is visibility routing, not end-to-end encryption.
/// </summary>
public sealed record RoomChatEntry(string Id, int SenderSeat, int? TargetSeat, string Text);

/// <summary>
/// Owns a bounded, in-memory room message board shared by classic and three-player coordinators.
/// It validates text, seats and duplicate IDs, publishes accepted entries synchronously on the
/// caller thread, and never owns sockets, files, UI nodes or player authentication. Coordinators
/// must bind a live packet's claimed sender to the transport-authenticated seat before calling
/// <see cref="AcceptLive"/>. <see cref="ImportTrustedHistory"/> is only for history supplied by an
/// already authenticated room peer; it does not make an untrusted relay authoritative.
/// </summary>
public sealed class RoomChatManager
{
    public const int MaximumTextUtf8Bytes = 512;
    public const int DefaultCapacity = 200;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly int _seatCount;
    private readonly int _capacity;
    private readonly List<RoomChatEntry> _entries = [];
    private readonly HashSet<string> _ids = [];
    private readonly ReadOnlyCollection<RoomChatEntry> _readOnlyEntries;

    public RoomChatManager(int seatCount, int capacity = DefaultCapacity)
    {
        if (seatCount is < 2 or > 16) throw new ArgumentOutOfRangeException(nameof(seatCount));
        if (capacity is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(capacity));
        _seatCount = seatCount;
        _capacity = capacity;
        _readOnlyEntries = _entries.AsReadOnly();
    }

    /// <summary>A live read-only view. It changes after an accepted post/import and is not a historical snapshot.</summary>
    public IReadOnlyList<RoomChatEntry> Entries => _readOnlyEntries;

    /// <summary>Raised after storage, once per non-duplicate item, on the thread that called this manager.</summary>
    public event Action<RoomChatEntry>? MessageAdded;

    /// <summary>
    /// Creates and stores a local item. Empty/control-character/oversize text and invalid seats are
    /// rejected with a user-facing error. A successful item must then be delivered by its coordinator.
    /// </summary>
    public string? Post(int senderSeat, int? targetSeat, string text, out RoomChatEntry? entry)
    {
        entry = null;
        var error = Validate(senderSeat, targetSeat, text, out var normalized);
        if (error is not null) return error;
        entry = new RoomChatEntry(Guid.NewGuid().ToString("N"), senderSeat, targetSeat, normalized);
        Store(entry);
        return null;
    }

    /// <summary>
    /// Accepts a live item only when its sender matches the transport-authenticated seat. Returns
    /// false for malformed, spoofed or duplicate input; malformed input never raises an event.
    /// </summary>
    public bool AcceptLive(RoomChatEntry? entry, int authenticatedSenderSeat)
    {
        if (entry is null || entry.SenderSeat != authenticatedSenderSeat || !ValidId(entry.Id) ||
            Validate(entry.SenderSeat, entry.TargetSeat, entry.Text, out var normalized) is not null ||
            normalized != entry.Text || _ids.Contains(entry.Id)) return false;
        Store(entry);
        return true;
    }

    /// <summary>
    /// Merges a bounded room-history page received from an authenticated peer. Items remain subject
    /// to all structural limits and ID de-duplication. The caller controls peer trust and page limits.
    /// </summary>
    public int ImportTrustedHistory(IEnumerable<RoomChatEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var accepted = 0;
        foreach (var entry in entries.Take(_capacity))
        {
            if (entry is null || !ValidId(entry.Id) ||
                Validate(entry.SenderSeat, entry.TargetSeat, entry.Text, out var normalized) is not null ||
                normalized != entry.Text || _ids.Contains(entry.Id)) continue;
            Store(entry);
            accepted++;
        }
        return accepted;
    }

    /// <summary>Returns whether a seat may present an item. Null represents a local hot-seat observer.</summary>
    public static bool IsVisibleTo(RoomChatEntry entry, int? localSeat) => localSeat is null ||
        entry.TargetSeat is null || entry.SenderSeat == localSeat || entry.TargetSeat == localSeat;

    private string? Validate(int senderSeat, int? targetSeat, string? text, out string normalized)
    {
        normalized = text?.Trim() ?? string.Empty;
        if (senderSeat < 0 || senderSeat >= _seatCount ||
            targetSeat is { } target && (target < 0 || target >= _seatCount || target == senderSeat))
            return "消息目标无效。";
        if (normalized.Length == 0 || normalized.Any(char.IsControl)) return "消息不能为空或包含控制字符。";
        try
        {
            if (StrictUtf8.GetByteCount(normalized) > MaximumTextUtf8Bytes)
                return $"消息不能超过 {MaximumTextUtf8Bytes} 个 UTF-8 字节。";
        }
        catch (EncoderFallbackException) { return "消息包含无效字符。"; }
        return null;
    }

    private static bool ValidId(string? id) => id is { Length: 32 } && id.All(Uri.IsHexDigit);

    private void Store(RoomChatEntry entry)
    {
        if (!_ids.Add(entry.Id)) return;
        _entries.Add(entry);
        while (_entries.Count > _capacity)
        {
            _ids.Remove(_entries[0].Id);
            _entries.RemoveAt(0);
        }
        MessageAdded?.Invoke(entry);
    }
}
