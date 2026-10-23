// Stand-ins for the parts of Horizon's Server.Common the vendored protocol
// code calls into. Horizon's versions pull in DotNetty, HpTimeStamps and
// Microsoft.Extensions.Logging; these keep only what the models use.

namespace Plugin.Medius.RT.Shared
{
    public static class Utils
    {
        public static byte[] FromString(string str) => Convert.FromHexString(str);

        public static uint GetUnixTime() => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}

namespace Plugin.Medius.RT.Shared.Logging
{
    /// <summary>
    /// Horizon filtered message logging per id; here every message may log and
    /// Serilog's level decides whether it is written.
    /// </summary>
    public sealed class LogSettings
    {
        public static LogSettings Singleton { get; set; } = new();

        public bool IsLog<T>(T _) where T : Enum => true;
    }
}
