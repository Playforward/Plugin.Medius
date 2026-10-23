using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Cryptography;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;

namespace Plugin.Medius.Components;

/// <summary>
/// Requests both the MAS and the MLS answer the same way: policies,
/// announcements, server time, and the empty lists (buddies, clans, ignore)
/// a fresh server has nothing in yet.
/// </summary>
internal static class LobbyCommon
{
    public static bool TryHandle(MediusContext context, ScertConnection connection, BaseMediusMessage message)
    {
        MediusTitle? title = connection.Title;
        switch (message)
        {
            case MediusGetPolicyRequest request:
                SendText(title?.Policy ?? "", Constants.POLICY_MAXLEN - 1, (chunk, last) => connection.SendApp(new MediusGetPolicyResponse
                {
                    MessageID = request.MessageID,
                    StatusCode = MediusCallbackStatus.MediusSuccess,
                    Policy = chunk,
                    EndOfText = last,
                }));
                return true;

            case MediusGetAnnouncementsRequest request:
                SendAnnouncements(connection, request.MessageID, title, all: false);
                return true;
            case MediusGetAllAnnouncementsRequest request:
                SendAnnouncements(connection, request.MessageID, title, all: true);
                return true;

            case MediusGetServerTimeRequest request:
                connection.SendApp(new MediusGetServerTimeResponse
                {
                    MessageID = request.MessageID,
                    StatusCode = MediusCallbackStatus.MediusSuccess,
                    Local_server_timezone = MediusTimeZone.MediusTimeZone_GMT,
                });
                return true;

            case MediusGetMyIPRequest request:
                connection.SendApp(new MediusGetMyIPResponse
                {
                    MessageID = request.MessageID,
                    StatusCode = MediusCallbackStatus.MediusSuccess,
                    IP = connection.Remote?.Address.MapToIPv4() ?? System.Net.IPAddress.Any,
                });
                return true;

            case MediusTextFilterRequest request:
                connection.SendApp(new MediusTextFilterResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusPass, Text = request.Text });
                return true;

            case MediusGetBuddyList_ExtraInfoRequest request:
                connection.SendApp(new MediusGetBuddyList_ExtraInfoResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true });
                return true;

            case MediusGetIgnoreListRequest request:
                connection.SendApp(new MediusGetIgnoreListResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true });
                return true;

            case MediusGetMyClansRequest request:
                connection.SendApp(new MediusGetMyClansResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true });
                return true;

            case MediusGetLocationsRequest request:
                connection.SendApp(new MediusGetLocationsResponse
                {
                    MessageID = request.MessageID,
                    StatusCode = MediusCallbackStatus.MediusSuccess,
                    LocationId = 0,
                    LocationName = title?.Descriptor.Name ?? "Default",
                    EndOfList = true,
                });
                return true;
        }
        return false;
    }

    /// <summary>The server's public key in the RSA_KEY layout (16 little-endian words, low word first).</summary>
    public static RSA_KEY PublicKeyOf(RsaKeyPair key)
    {
        byte[] bytes = key.N.ToByteArrayUnsigned().Reverse().ToArray();
        Array.Resize(ref bytes, Constants.RSA_SIZE_DWORD * 4);
        return new RSA_KEY(bytes);
    }

    private static void SendAnnouncements(ScertConnection connection, MessageId messageId, MediusTitle? title, bool all)
    {
        List<string> announcements = title?.Announcements ?? [];
        if (!all && announcements.Count > 1)
            announcements = announcements.Take(1).ToList();
        if (announcements.Count == 0)
        {
            connection.SendApp(new MediusGetAnnouncementsResponse { MessageID = messageId, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true });
            return;
        }
        for (int i = 0; i < announcements.Count; i++)
        {
            connection.SendApp(new MediusGetAnnouncementsResponse
            {
                MessageID = messageId,
                StatusCode = MediusCallbackStatus.MediusSuccess,
                AnnouncementID = i + 1,
                Announcement = Truncate(announcements[i], Constants.ANNOUNCEMENT_MAXLEN - 1),
                EndOfList = i == announcements.Count - 1,
            });
        }
    }

    private static void SendText(string text, int chunkSize, Action<string, bool> send)
    {
        if (text.Length == 0)
        {
            send("", true);
            return;
        }
        for (int i = 0; i < text.Length; i += chunkSize)
            send(text.Substring(i, Math.Min(chunkSize, text.Length - i)), i + chunkSize >= text.Length);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
