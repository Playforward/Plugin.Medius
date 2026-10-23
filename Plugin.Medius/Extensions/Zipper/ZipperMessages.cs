using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.RT.Shared.Stream;

namespace Plugin.Medius.Extensions.Zipper;

/// <summary>
/// RT ids SCE-RT added for server plugins (past Horizon's table): the
/// client talks to a plugin (MAG's MAPS, its lobby and game-server plugins)
/// with 0x3D and hears back on 0x3E.
/// </summary>
public static class ZipperRtIds
{
    public const RT_MSG_TYPE ClientAppToPlugin = (RT_MSG_TYPE)0x3D;
    public const RT_MSG_TYPE ServerPluginToApp = (RT_MSG_TYPE)0x3E;
}

/// <summary>One plugin message inside an RT plugin frame.</summary>
public sealed record PluginRecord(byte PluginId, byte[] Data)
{
    public ushort MessageType => Data.Length >= 2 ? (ushort)(Data[0] << 8 | Data[1]) : (ushort)0;
}

/// <summary>
/// The body of RT 0x3D and 0x3E: one or more records
/// <c>[length:u16le][pluginId:u8][data]</c>. Confirmed against the retail
/// MAG launcher: it sends <c>02 00 00</c> before a 2-byte hello, and its RT
/// library parses incoming frames that way (EBOOT 0x1b14cc: u16 read
/// little-endian at 0x1b7040, plugin id byte at +2, next record at
/// +3+length). In the length, bit 15 marks a record with an extra u16 and
/// bit 14 a fragment; the low 14 bits are the size. Neither flag is
/// produced here, so a record carries at most 16383 bytes.
/// </summary>
public static class PluginRecords
{
    public const int MaxRecordLength = 0x3FFF;

    public static List<PluginRecord> Read(byte[] body)
    {
        List<PluginRecord> records = [];
        int position = 0;
        while (body.Length - position >= 3)
        {
            int raw = body[position] | body[position + 1] << 8;
            if ((raw & 0xC000) != 0)
                throw new NotSupportedException($"plugin record with flags 0x{raw & 0xC000:X4} (extended or fragmented) is not handled yet");
            int length = raw & MaxRecordLength;
            byte pluginId = body[position + 2];
            position += 3;
            if (length > body.Length - position)
                throw new InvalidDataException($"plugin record of {length} bytes, {body.Length - position} left");
            records.Add(new PluginRecord(pluginId, body.AsSpan(position, length).ToArray()));
            position += length;
        }
        return records;
    }

    public static void Write(MessageWriter writer, IEnumerable<PluginRecord> records)
    {
        foreach (PluginRecord record in records)
        {
            if (record.Data.Length > MaxRecordLength)
                throw new ArgumentException($"plugin record of {record.Data.Length} bytes exceeds {MaxRecordLength}");
            writer.Write((byte)record.Data.Length);
            writer.Write((byte)(record.Data.Length >> 8));
            writer.Write(record.PluginId);
            writer.Write(record.Data);
        }
    }
}

/// <summary>Plugin messages from the client; each record's data is a Zipper bit-stream, type first.</summary>
[ScertMessage(ZipperRtIds.ClientAppToPlugin)]
public sealed class RtClientAppToPlugin : BaseScertMessage
{
    public override RT_MSG_TYPE Id => ZipperRtIds.ClientAppToPlugin;

    public List<PluginRecord> Records { get; set; } = [];

    public override void Deserialize(MessageReader reader) =>
        Records = PluginRecords.Read(reader.ReadBytes((int)(reader.BaseStream.Length - reader.BaseStream.Position)));

    public override void Serialize(MessageWriter writer) => PluginRecords.Write(writer, Records);

    public override string ToString() =>
        $"{base.ToString()} {string.Join(", ", Records.Select(r => $"plugin:{r.PluginId} type:0x{r.MessageType:X2} {r.Data.Length}B"))}";
}

/// <summary>
/// Plugin messages to the client. Its Medius library hands each record's
/// data and plugin id to the game's callback (MediusMessageCallback, mage_g
/// 0x1cd25c), which remembers the id and answers to it.
/// </summary>
[ScertMessage(ZipperRtIds.ServerPluginToApp)]
public sealed class RtServerPluginToApp : BaseScertMessage
{
    public override RT_MSG_TYPE Id => ZipperRtIds.ServerPluginToApp;

    public List<PluginRecord> Records { get; set; } = [];

    public static RtServerPluginToApp Single(byte pluginId, byte[] data) => new() { Records = [new PluginRecord(pluginId, data)] };

    public override void Deserialize(MessageReader reader) =>
        Records = PluginRecords.Read(reader.ReadBytes((int)(reader.BaseStream.Length - reader.BaseStream.Position)));

    public override void Serialize(MessageWriter writer) => PluginRecords.Write(writer, Records);

    public override string ToString() =>
        $"{base.ToString()} {string.Join(", ", Records.Select(r => $"plugin:{r.PluginId} type:0x{r.MessageType:X2} {r.Data.Length}B"))}";
}
