using Propane.Core;
using Propane.Net;

namespace Propane.Dev;

/// <summary>
/// Dev-only convenience for fixtures and release-reference probes without a connection owner. Excluded from
/// ExportRelease; runtime callers are forbidden by the runtime-to-Dev gate. Production borrows an owner-scoped
/// PacketCodec so schema selection and native tuner validation stay explicit.
/// </summary>
public static class MessageCodec
{
    public static NetMessage Decode(byte[] data, bool fromServer) => Modes.ModeRegistry.Codec.Decode(data, fromServer);
    public static byte[] Encode(NetMessage message, bool fromServer) => Modes.ModeRegistry.Codec.Encode(message, fromServer);
    internal static void WriteBody(NetWriter writer, BodyState body) => PacketCodec.WriteBody(writer, body);
}
