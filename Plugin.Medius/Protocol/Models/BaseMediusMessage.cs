using Plugin.Medius.RT.Cryptography;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using Plugin.Medius.RT.Shared.Logging;

namespace Plugin.Medius.RT.Models
{
    [AttributeUsage(AttributeTargets.Class)]
    public class MediusMessageAttribute : Attribute
    {
        public NetMessageTypes MessageClass;
        public byte MessageType;

        public MediusMessageAttribute(NetMessageTypes msgClass, MediusDmeMessageIds msgType)
        {
            MessageClass = msgClass;
            MessageType = (byte)msgType;
        }

        public MediusMessageAttribute(NetMessageTypes msgClass, MediusMGCLMessageIds msgType)
        {
            MessageClass = msgClass;
            MessageType = (byte)msgType;
        }

        public MediusMessageAttribute(NetMessageTypes msgClass, MediusLobbyMessageIds msgType)
        {
            MessageClass = msgClass;
            MessageType = (byte)msgType;
        }

        public MediusMessageAttribute(NetMessageTypes msgClass, MediusLobbyExtMessageIds msgType)
        {
            MessageClass = msgClass;
            MessageType = (byte)msgType;
        }
    }

    public abstract class BaseMediusMessage
    {
        /// <summary>
        /// Message class.
        /// </summary>
        public abstract NetMessageTypes PacketClass { get; }

        /// <summary>
        /// Message type.
        /// </summary>
        public abstract byte PacketType { get; }

        /// <summary>
        /// When true, skips encryption when sending this particular message instance.
        /// </summary>
        public virtual bool SkipEncryption { get; set; } = false;

        public BaseMediusMessage()
        {

        }

        #region Serialization

        /// <summary>
        /// Deserializes the message from plaintext.
        /// </summary>
        /// <param name="reader"></param>
        public virtual void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {

        }

        /// <summary>
        /// Serialize contents of the message.
        /// </summary>
        public virtual void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {

        }

        #endregion

        #region Logging

        /// <summary>
        /// Whether or not this message passes the log filter.
        /// </summary>
        public virtual bool CanLog()
        {
            switch (this.PacketClass)
            {
                case NetMessageTypes.MessageClassDME: return LogSettings.Singleton?.IsLog((MediusDmeMessageIds)this.PacketType) ?? false;
                case NetMessageTypes.MessageClassLobby: return LogSettings.Singleton?.IsLog((MediusLobbyMessageIds)this.PacketType) ?? false;
                case NetMessageTypes.MessageClassLobbyReport: return LogSettings.Singleton?.IsLog((MediusMGCLMessageIds)this.PacketType) ?? false;
                case NetMessageTypes.MessageClassLobbyExt: return LogSettings.Singleton?.IsLog((MediusLobbyExtMessageIds)this.PacketType) ?? false;
                default: return true;
            }
        }

        #endregion

        #region Dynamic Instantiation

        /// <summary>
        /// Models by (class, type). Usually one; two when SCE-RT versions gave a
        /// type to different messages in each direction (Killzone 2's 3.03 sends
        /// lobby-ext 0x87 as a join request, later libraries answer with it), so
        /// <see cref="Instantiate"/> picks by who sent it.
        /// </summary>
        private static Dictionary<(NetMessageTypes, byte), List<Type>> _messageClassById = null;
        private static readonly object _messageClassByIdLock = new();

        private static void Initialize()
        {
            lock (_messageClassByIdLock)
            {
                if (_messageClassById != null)
                    return;

                var byId = new Dictionary<(NetMessageTypes, byte), List<Type>>();
                var assembly = System.Reflection.Assembly.GetAssembly(typeof(BaseMediusMessage));
                foreach (Type classType in assembly.GetTypes())
                {
                    var attrs = (MediusMessageAttribute[])classType.GetCustomAttributes(typeof(MediusMessageAttribute), true);
                    if (attrs == null || attrs.Length == 0)
                        continue;
                    var key = (attrs[0].MessageClass, attrs[0].MessageType);
                    if (!byId.TryGetValue(key, out var list))
                        byId[key] = list = new List<Type>();
                    list.Add(classType);
                }
                _messageClassById = byId;
            }
        }

        /// <summary>
        /// Whether a model can be sent that way: requests come from clients,
        /// responses from servers, anything else either way.
        /// </summary>
        private static bool Fits(Type classType, bool fromClient) =>
            fromClient ? !typeof(IMediusResponse).IsAssignableFrom(classType) || typeof(IMediusRequest).IsAssignableFrom(classType)
                       : !typeof(IMediusRequest).IsAssignableFrom(classType) || typeof(IMediusResponse).IsAssignableFrom(classType);

        /// <summary>Reads a Medius message; <paramref name="fromClient"/> says which side sent it, to pick between models sharing a type.</summary>
        public static BaseMediusMessage Instantiate(Plugin.Medius.RT.Shared.Stream.MessageReader reader, bool? fromClient = null)
        {
            BaseMediusMessage msg;
            Type classType = null;

            // Init
            Initialize();

            NetMessageTypes msgClass = reader.Read<NetMessageTypes>();
            var msgType = reader.ReadByte();

            if (_messageClassById.TryGetValue((msgClass, msgType), out var candidates))
                classType = (fromClient is { } side ? candidates.Find(t => Fits(t, side)) : null) ?? candidates[0];

            // Instantiate
            if (classType == null)
                msg = new RawMediusMessage(msgClass, msgType);
            else
                msg = (BaseMediusMessage)Activator.CreateInstance(classType);

            // Deserialize
            msg.Deserialize(reader);
            return msg;
        }

        #endregion

    }
}
