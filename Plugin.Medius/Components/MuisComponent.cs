using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;

namespace Plugin.Medius.Components;

/// <summary>
/// Universe information: the first server a stock Medius client talks to.
/// It answers which universes (server clusters) the game has and the MAS
/// address of each.
/// </summary>
public sealed class MuisComponent(MediusContext context) : MediusComponent(context)
{
    public override string Name => MediusComponentNames.Muis;

    protected override void HandleMedius(ScertConnection connection, BaseMediusMessage message)
    {
        if (message is not MediusGetUniverseInformationRequest request || connection.Title is not { } title)
        {
            Unhandled(connection, message);
            return;
        }

        List<MediusUniverse> universes = title.Universes.Count > 0
            ? title.Universes
            : [new MediusUniverse { Name = title.Descriptor.Name, Description = title.Descriptor.Name }];

        for (int i = 0; i < universes.Count; i++)
        {
            MediusUniverse universe = universes[i];
            if (request.InfoType.HasFlag(MediusUniverseVariableInformationInfoFilter.INFO_SVO_URL))
                connection.SendApp(new MediusUniverseVariableSvoURLResponse { MessageID = new MessageId(), URL = universe.SvoUrl });

            connection.SendApp(new MediusUniverseVariableInformationResponse
            {
                MessageID = request.MessageID,
                StatusCode = MediusCallbackStatus.MediusSuccess,
                InfoFilter = request.InfoType,
                UniverseID = universe.Id,
                UniverseName = universe.Name,
                UniverseDescription = universe.Description,
                ExtendedInfo = universe.ExtendedInfo,
                SvoURL = universe.SvoUrl,
                DNS = string.IsNullOrEmpty(universe.Host) ? Context.PublicAddress.ToString() : universe.Host,
                Port = universe.Port != 0 ? universe.Port : Context.Settings.PortOf(MediusComponentNames.Mas),
                EndOfList = i == universes.Count - 1,
            });
        }

        if (request.InfoType.HasFlag(MediusUniverseVariableInformationInfoFilter.INFO_NEWS))
        {
            connection.SendApp(new MediusUniverseNewsResponse
            {
                MessageID = request.MessageID,
                StatusCode = MediusCallbackStatus.MediusSuccess,
                News = title.News,
                EndOfList = true,
            });
        }
    }
}
