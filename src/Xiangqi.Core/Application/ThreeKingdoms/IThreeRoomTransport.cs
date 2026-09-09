namespace Xiangqi.Core.Application.ThreeKingdoms;

/// <summary>固定席位身份；Id 由传输认证，Name 仅用于显示，不得参与鉴权。名册顺序对应魏吴蜀。</summary>
public sealed record ThreePeer(string Id, string Name);

/// <summary>
/// 可重连的三人全连接传输端口。满席后名册不可改写/补位；断线仅改变 Connected，不删除
/// 名册身份。Poll 和全部事件在调用线程执行；后台 I/O 必须排队后交给 Poll。
/// LAN 实现验证签名身份，Steam 实现验证平台 ID；房主权威不属于该层。
/// </summary>
public interface IThreeRoomTransport : IDisposable
{
    string LocalId { get; }
    IReadOnlyList<ThreePeer> Roster { get; }
    IReadOnlySet<string> Connected { get; }
    string Status { get; }
    event Action<string, byte[]>? Received;
    void Send(string peerId, byte[] packet);
    void Poll(double delta);
    /// <summary>同步平台侧房主所有权；无平台所有者概念的传输可保持默认空实现。</summary>
    void SetAuthority(string peerId) { }
}
