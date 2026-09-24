namespace Bridge.Network;

/// <summary>
/// Abstraction over the raw byte transport so the protocol/pairing/session
/// layers never touch a Socket directly. TCP is the only implementation for
/// the MVP (see TcpTransport); QUIC/BLE implementations can be dropped in
/// later without changing anything above this interface.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    Task SendFrameAsync(byte[] data, CancellationToken ct = default);
    Task<byte[]> ReceiveFrameAsync(CancellationToken ct = default);
    bool IsConnected { get; }
}
