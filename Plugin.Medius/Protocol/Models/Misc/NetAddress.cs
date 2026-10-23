using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    public class NetAddress : IStreamSerializer
    {
        public static readonly NetAddress Empty = new NetAddress() { AddressType = NetAddressType.NetAddressNone };

        public NetAddressType AddressType;
        public string Address;
        public uint Port;

        public void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            AddressType = reader.Read<NetAddressType>();
            Address = reader.ReadString(Constants.NET_MAX_NETADDRESS_LENGTH);
            Port = reader.ReadUInt32();
        }

        public void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            writer.Write(AddressType);
            writer.Write(Address, Constants.NET_MAX_NETADDRESS_LENGTH);
            writer.Write(Port);
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"AddressType:{AddressType} " +
                $"Address:{Address} " +
                $"Port:{Port}";
        }
    }
}
