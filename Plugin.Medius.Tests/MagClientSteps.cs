using Plugin.Medius.Extensions.Mag;
using Plugin.Medius.Extensions.Zipper;

namespace Plugin.Medius.Tests;

/// <summary>
/// The retail MAG client's side of the MAPS plugin protocol, as reusable
/// steps for tests here and in Plugin.MagGameServer's tests.
/// </summary>
internal static class MagClientSteps
{
    /// <summary>Connects to MAPS and authenticates with a PSN ticket for <paramref name="onlineId"/>.</summary>
    public static async Task LoginAsync(MediusTestClient client, int mapsPort, string onlineId, ulong userId)
    {
        await client.ConnectAsync(mapsPort);
        await client.HandshakeAsync(LobbyFlowFixture.MagAppId);
        byte[] ticket = PsnTicketTests.BuildTicket(onlineId, userId);
        SendTicketChunk(client, ticket, last: true, transId: 1);
        ZipperBitReader auth = await ExpectPluginAsync(client, MagIds.Retail.NpAuthenticateResponse);
        MapsEnvelope.Read(auth);
        Assert.True(auth.ReadBool());
    }

    /// <summary>Creates the logged-in player's soldier <paramref name="name"/> in <paramref name="faction"/>, universe 7 (the test title's).</summary>
    public static async Task CreateCharacterAsync(MediusTestClient client, string name, byte faction, int transId = 2)
    {
        SendPlugin(client, w =>
        {
            w.WriteU16(MagIds.Retail.CreateCharacterRequest);
            new MapsEnvelope { TransId = transId }.Write(w);
            MagCharacter.WriteShortString(w, name);
            w.WriteU8(faction);
            w.WriteU8(3);
            w.WriteU8(4);
            w.WriteU32(7);
            for (int i = 0; i < 36; i++)
                w.WriteU8(0);
        });
        ZipperBitReader created = await ExpectPluginAsync(client, MagIds.Retail.CreateCharacterResponse);
        MapsEnvelope.Read(created);
        Assert.Equal(0, created.ReadI32());
    }

    public static void SendTicketChunk(MediusTestClient client, byte[] chunk, bool last, int transId) =>
        SendPlugin(client, w =>
        {
            w.WriteU16(MagIds.Retail.NpAuthenticateRequest);
            new MapsEnvelope { TransId = transId }.Write(w);
            w.WriteU16((ushort)chunk.Length);
            for (int i = 0; i < MapsNpAuthenticateChunk.ChunkBufferLength; i++)
                w.WriteU8(i < chunk.Length ? chunk[i] : (byte)0);
            w.WriteU16(0);
            w.WriteBool(last);
        });

    public static void SendPlugin(MediusTestClient client, Action<ZipperBitWriter> write)
    {
        ZipperBitWriter writer = new();
        write(writer);
        client.Send(new RtClientAppToPlugin { Records = [new PluginRecord(0, writer.ToArray())] });
    }

    /// <summary>Waits for one plugin record from plugin 31 and checks its type.</summary>
    public static async Task<ZipperBitReader> ExpectPluginAsync(MediusTestClient client, ushort type)
    {
        RtServerPluginToApp message = await client.ExpectAsync<RtServerPluginToApp>();
        PluginRecord record = Assert.Single(message.Records);
        Assert.Equal(31, record.PluginId);
        ZipperBitReader reader = new(record.Data);
        Assert.Equal(type, reader.ReadU16());
        return reader;
    }

    /// <summary>A NUL-padded fixed-width char field.</summary>
    public static string ReadFixed(ZipperBitReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        int end = Array.IndexOf(bytes, (byte)0);
        return System.Text.Encoding.ASCII.GetString(bytes, 0, end < 0 ? length : end);
    }
}
