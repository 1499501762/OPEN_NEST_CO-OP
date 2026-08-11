namespace OpenNestCoop.Net;

/// <summary>
/// 传输层抽象：Steam P2P（联机）与本地 TCP 回环（双开测试，不经 Steam）。
/// peerId 统一用 ulong：Steam 模式是真实 SteamID，本地模式是自增数字（1=host, 2+ = client）。
/// </summary>
public interface ITransport
{
    /// <summary>向指定 peer 发送数据。reliable=true 可靠（重传），false 不可靠。</summary>
    void Send(ulong peerId, byte[] data, bool reliable);

    /// <summary>取出一个待处理包。返回 false 表示当前无包。sender 是来源 peerId。</summary>
    bool Poll(out ulong sender, out byte[] data);
}
