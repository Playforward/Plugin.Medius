using Org.BouncyCastle.Math;
using Plugin.Medius.Extensions;
using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Cryptography;
using System.Net;

namespace Plugin.Medius;

/// <summary>What every component shares: settings, titles, lobby state.</summary>
public sealed class MediusContext
{
    private readonly Dictionary<int, MediusTitle> _titlesByAppId;
    private readonly Dictionary<MediusTitle, IMediusTitleExtension> _extensions;

    internal MediusContext(MediusSettings settings, IReadOnlyList<MediusTitle> titles,
        Dictionary<MediusTitle, IMediusTitleExtension> extensions, LobbyState lobby)
    {
        Settings = settings;
        Titles = titles;
        Lobby = lobby;
        _extensions = extensions;
        _titlesByAppId = titles.SelectMany(t => t.AppIds.Select(id => (id, t))).ToDictionary(x => x.id, x => x.t);
        ServerKey = new RsaKeyPair(new BigInteger(settings.ServerKey.N, 10), new BigInteger(settings.ServerKey.E, 10), new BigInteger(settings.ServerKey.D, 10));
        PublicAddress = settings.ResolvePublicAddress();
    }

    public MediusSettings Settings { get; }
    public IReadOnlyList<MediusTitle> Titles { get; }
    public LobbyState Lobby { get; }
    public RsaKeyPair ServerKey { get; }
    public IPAddress PublicAddress { get; }

    public MediusTitle? TitleByAppId(int appId) => _titlesByAppId.GetValueOrDefault(appId);

    public IMediusTitleExtension? ExtensionOf(MediusTitle? title) =>
        title is not null && _extensions.TryGetValue(title, out IMediusTitleExtension? extension) ? extension : null;
}
