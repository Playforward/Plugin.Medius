using Plugin.Medius.Components;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;

namespace Plugin.Medius.Extensions;

/// <summary>
/// What a game adds on top of stock Medius. Most games need none: SCE-RT's
/// lobby was the whole service. Some developers ran their own servers beside
/// it, speaking Medius' RT layer but their own messages (Zipper's MAPS for
/// MAG and SOCOM 4); an extension serves those, and gets first refusal on
/// every message its title's clients send to the stock components.
/// </summary>
public interface IMediusTitleExtension
{
    /// <summary>The name a title's <c>Extension</c> field selects this by.</summary>
    string Name { get; }

    /// <summary>Called once with the title (and its <c>ExtensionSettings</c>) before any traffic.</summary>
    void Initialize(MediusTitle title, MediusContext context);

    /// <summary>
    /// Components of the extension's own, by the name a listener is assigned
    /// in <see cref="MediusSettings.Listeners"/> (e.g. <c>MAPS</c>).
    /// </summary>
    IEnumerable<MediusComponent> CreateComponents(MediusContext context) => [];

    /// <summary>
    /// Sees an RT message from one of the title's clients before the
    /// component does. Return true when handled, and the component skips it.
    /// </summary>
    bool TryHandle(MediusComponent component, ScertConnection connection, BaseScertMessage message) => false;
}

/// <summary>Finds extensions by name among the types in this assembly.</summary>
public static class MediusExtensionRegistry
{
    private static readonly Dictionary<string, Type> Known = typeof(IMediusTitleExtension).Assembly.GetTypes()
        .Where(t => !t.IsAbstract && typeof(IMediusTitleExtension).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
        .ToDictionary(t => ((IMediusTitleExtension)Activator.CreateInstance(t)!).Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> Names => Known.Keys;

    public static IMediusTitleExtension? Create(string name) =>
        Known.TryGetValue(name, out Type? type) ? (IMediusTitleExtension)Activator.CreateInstance(type)! : null;
}
