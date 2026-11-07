using Plugin.Medius.Extensions.Mag;

namespace Plugin.Medius.Tests;

/// <summary>The message ids of the client builds the tests play, from the generated table.</summary>
internal static class MagIds
{
    /// <summary>Retail 1.00 (protocol 1740), what the test title serves by default.</summary>
    public static MagMessageIds Retail => MagClientVersion.Retail.Messages;

    /// <summary>Patch 2.12 (protocol 1793).</summary>
    public static MagMessageIds Patch212 => MagClientVersion.Patch212.Messages;
}
