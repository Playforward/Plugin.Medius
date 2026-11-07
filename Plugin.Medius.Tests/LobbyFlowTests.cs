using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using ServerShared.CommonModels;
using ServerShared.Controllers;
using ServerShared.Titles;
using System.Net;
using System.Net.Sockets;

namespace Plugin.Medius.Tests;

/// <summary>
/// Starts the plugin on real ServerShared listeners and walks a PS3 client
/// through what a game does on "go online": MUIS, MAS login, MLS lobby.
/// Not sealed: Plugin.MagGameServer's tests derive from it to register their
/// game server (<see cref="BeforeStart"/>) on the same set-up.
/// </summary>
public class LobbyFlowFixture : IDisposable
{
    public const int AppId = 99901;
    public const int MagAppId = 20231;

    public LobbyFlowFixture()
    {
        Root = Directory.CreateTempSubdirectory("medius-test").FullName;
        Directory.CreateDirectory(Path.Combine(Root, "medius"));
        File.WriteAllText(Path.Combine(Root, "medius", "title.json"), $$"""
            {
              "AppIds": [ {{AppId}} ],
              "Encrypt": true,
              "Universes": [ { "Name": "Test Universe", "Description": "test", "SvoUrl": "http://127.0.0.1/svo" } ],
              "Channels": [ { "Name": "Alpha" }, { "Name": "Bravo", "MaxPlayers": 4 } ],
              "Policy": "Be nice.",
              "Announcements": [ "Hello from the test server" ],
              "News": "No news",
              "Matchmaking": {
                "Supersets": [ { "Id": 1, "Name": "Co-op" }, { "Id": 2, "Name": "Versus", "Description": "two teams" } ],
                "PoolKey": [ { "Offset": 6, "Size": 2, "Name": "game type" } ],
                "Default": { "Name": "Match", "MinPlayers": 2, "MaxPlayers": 4, "Teams": 2 },
                "Pools": [ { "Superset": 1, "Name": "Co-op", "MinPlayers": 1, "MaxPlayers": 4, "Teams": 1, "GameOptions": "0A0B" } ]
              }
            }
            """);
        MagRoot = Path.Combine(Root, "mag");
        Directory.CreateDirectory(Path.Combine(MagRoot, "medius"));
        File.WriteAllText(Path.Combine(MagRoot, "medius", "title.json"), $$"""
            {
              "AppIds": [ {{MagAppId}} ],
              "Extension": "mag",
              "ExtensionSettings": { "Matchmaking": { "MinPlayers": { "GameTypeFactionExercise": 1, "GameType64": 2, "GameType128": 2, "GameType256": 2 } } },
              "Universes": [ { "Id": 7, "Name": "MAG Test", "SvoUrl": "http://127.0.0.1/mag" } ]
            }
            """);
        TitleRegistry.Publish(
        [
            new TitleDescriptor { TitleId = "medius-test", Name = "Medius Test", ProtocolFamily = "medius", ResourceDir = Root },
            new TitleDescriptor { TitleId = "mag-test", Name = "MAG Test", ProtocolFamily = "medius", ResourceDir = MagRoot },
        ]);

        MuisPort = FreeTcpPort();
        MasPort = FreeTcpPort();
        MlsPort = FreeTcpPort();
        NatPort = FreeUdpPort();
        MapsPort = FreeTcpPort();
        DmePort = FreeTcpPort();
        DmeUdpPort = FreeUdpPort();
        MagGamePort = FreeTcpPort();
        RudpPort = FreeUdpPort();
        foreach ((string name, int port, bool udp) in new[] { ("MUIS", MuisPort, false), ("MAS", MasPort, false), ("MLS", MlsPort, false), ("NAT", NatPort, true), ("MAPS", MapsPort, false), ("DME", DmePort, false), ("DME-UDP", DmeUdpPort, true), ("MAGGAME", MagGamePort, false), ("RUDP", RudpPort, true) })
            ServerController.Start(new ServerModel { Name = $"test-{name}", Port = port, IsUdp = udp });

        Plugin = new MediusPlugin
        {
            Config = new MediusSettings
            {
                PublicHost = "127.0.0.1",
                DataDir = Path.Combine(Root, "data"),
                Listeners =
                [
                    new() { Component = "MUIS", Port = MuisPort },
                    new() { Component = "MAS", Port = MasPort },
                    new() { Component = "MLS", Port = MlsPort },
                    new() { Component = "NAT", Port = NatPort, Udp = true },
                    new() { Component = "MAPS", Port = MapsPort },
                    new() { Component = "DME", Port = DmePort },
                    new() { Component = "DME", Port = DmeUdpPort, Udp = true },
                    new() { Component = "MAGGAME", Port = MagGamePort },
                    new() { Component = "RUDP", Port = RudpPort, Udp = true },
                ],
            },
        };
        BeforeStart();
        Plugin.Start();
    }

    /// <summary>Runs after the listeners exist and before the Medius plugin starts.</summary>
    protected virtual void BeforeStart() { }

    public string Root { get; }
    public int MuisPort { get; }
    public int MasPort { get; }
    public int MlsPort { get; }
    public int NatPort { get; }
    public int MapsPort { get; }
    public int DmePort { get; }
    public int DmeUdpPort { get; }
    public int MagGamePort { get; }
    public int RudpPort { get; }
    public string MagRoot { get; }
    public MediusPlugin Plugin { get; }

    public virtual void Dispose()
    {
        Plugin.Stop();
        ServerController.Stop(clear: true);
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }

    private static int FreeTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int FreeUdpPort()
    {
        using UdpClient udp = new(0);
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }
}

[CollectionDefinition(Name)]
public sealed class MediusServerCollection : ICollectionFixture<LobbyFlowFixture>
{
    public const string Name = "medius server";
}

[Collection(MediusServerCollection.Name)]
public sealed class LobbyFlowTests(LobbyFlowFixture fixture)
{
    private const int Ps3 = 113;

    [Fact]
    public async Task Muis_lists_the_universe_pointing_at_the_mas()
    {
        await using MediusTestClient client = new(Ps3);
        await client.ConnectAsync(fixture.MuisPort);
        await client.HandshakeAsync(LobbyFlowFixture.AppId);

        client.SendApp(new MediusGetUniverseInformationRequest
        {
            MessageID = new MessageId("1"),
            InfoType = MediusUniverseVariableInformationInfoFilter.INFO_UNIVERSES | MediusUniverseVariableInformationInfoFilter.INFO_ID | MediusUniverseVariableInformationInfoFilter.INFO_NAME
                | MediusUniverseVariableInformationInfoFilter.INFO_DNS | MediusUniverseVariableInformationInfoFilter.INFO_SVO_URL | MediusUniverseVariableInformationInfoFilter.INFO_NEWS,
        });

        MediusUniverseVariableSvoURLResponse svo = await client.ExpectAppAsync<MediusUniverseVariableSvoURLResponse>();
        Assert.Equal("http://127.0.0.1/svo", svo.URL);
        MediusUniverseVariableInformationResponse info = await client.ExpectAppAsync<MediusUniverseVariableInformationResponse>();
        // The client compares the two responses' MessageIDs and fails the universe on a mismatch
        // (RFOM 0x619220) [C].
        Assert.Equal("1", svo.MessageID.ToString());
        Assert.Equal(info.MessageID.ToString(), svo.MessageID.ToString());
        Assert.Equal(MediusCallbackStatus.MediusSuccess, info.StatusCode);
        Assert.Equal("Test Universe", info.UniverseName);
        Assert.Equal("127.0.0.1", info.DNS);
        Assert.Equal(fixture.MasPort, info.Port);
        Assert.True(info.EndOfList);
        MediusUniverseNewsResponse news = await client.ExpectAppAsync<MediusUniverseNewsResponse>();
        Assert.Equal("No news", news.News);
    }

    [Fact]
    public async Task Login_then_lobby_then_chat()
    {
        // MAS: session and ticket login
        await using MediusTestClient mas = new(Ps3);
        await mas.ConnectAsync(fixture.MasPort);
        await mas.HandshakeAsync(LobbyFlowFixture.AppId);

        mas.SendApp(new MediusSessionBeginRequest { MessageID = new MessageId("2"), ConnectionClass = MediusConnectionType.Ethernet });
        MediusSessionBeginResponse session = await mas.ExpectAppAsync<MediusSessionBeginResponse>();
        Assert.Equal(MediusCallbackStatus.MediusSuccess, session.StatusCode);
        Assert.False(string.IsNullOrEmpty(session.SessionKey));

        mas.SendApp(new MediusTicketLoginRequest { MessageID = new MessageId("3"), SessionKey = session.SessionKey, Username = "tester" });
        MediusTicketLoginResponse login = await mas.ExpectAppAsync<MediusTicketLoginResponse>();
        Assert.Equal(MediusCallbackStatus.MediusSuccess, login.StatusCode);
        Assert.True(login.AccountID > 0);
        NetAddress lobby = login.ConnectInfo.AddressList.AddressList[0];
        Assert.Equal((uint)fixture.MlsPort, lobby.Port);
        string accessKey = login.ConnectInfo.AccessKey;
        Assert.False(string.IsNullOrEmpty(accessKey));

        // MLS: resume the session by token, list channels, chat
        await using MediusTestClient mls = new(Ps3);
        await mls.ConnectAsync(fixture.MlsPort);
        await mls.HandshakeAsync(LobbyFlowFixture.AppId, session.SessionKey, accessKey, (uint)login.MediusWorldID);

        mls.SendApp(new MediusChannelList_ExtraInfoRequest { MessageID = new MessageId("4"), PageID = 1, PageSize = 10 });
        MediusChannelList_ExtraInfoResponse first = await mls.ExpectAppAsync<MediusChannelList_ExtraInfoResponse>();
        MediusChannelList_ExtraInfoResponse second = await mls.ExpectAppAsync<MediusChannelList_ExtraInfoResponse>();
        Assert.Equal("Alpha", first.LobbyName);
        Assert.Equal((ushort)1, first.PlayerCount);
        Assert.Equal("Bravo", second.LobbyName);
        Assert.True(second.EndOfList);

        mls.SendApp(new MediusGenericChatMessage { MessageID = new MessageId("5"), SessionKey = session.SessionKey, MessageType = MediusChatMessageType.Broadcast, Message = "hi" });
        MediusGenericChatFwdMessage1 chat = await mls.ExpectAppAsync<MediusGenericChatFwdMessage1>();
        Assert.Equal("tester", chat.OriginatorAccountName);
        Assert.Equal("hi", chat.Message);

        mls.SendApp(new MediusGetPolicyRequest { MessageID = new MessageId("6"), SessionKey = session.SessionKey, Policy = MediusPolicyType.Usage });
        MediusGetPolicyResponse policy = await mls.ExpectAppAsync<MediusGetPolicyResponse>();
        Assert.Equal("Be nice.", policy.Policy);
    }

    [Fact]
    public async Task Unknown_app_id_is_refused()
    {
        await using MediusTestClient client = new(Ps3);
        await client.ConnectAsync(fixture.MasPort);
        await Assert.ThrowsAnyAsync<Exception>(() => client.HandshakeAsync(12345));
    }

    [Fact]
    public async Task Nat_reports_the_sender_endpoint()
    {
        using UdpClient udp = new(0);
        await udp.SendAsync(new byte[] { 0, 0, 0, 1 }, 4, new IPEndPoint(IPAddress.Loopback, fixture.NatPort));
        UdpReceiveResult reply = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(6, reply.Buffer.Length);
        Assert.Equal(new byte[] { 127, 0, 0, 1 }, reply.Buffer[..4]);
        Assert.Equal(((IPEndPoint)udp.Client.LocalEndPoint!).Port, reply.Buffer[4] << 8 | reply.Buffer[5]);
    }
}
