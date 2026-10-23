using Org.BouncyCastle.Math;
using System;
using System.Collections.Generic;
using System.Text;

namespace Plugin.Medius.RT.Cryptography
{
    // Horizon serialized this with a Newtonsoft converter; the plugin's
    // settings carry the key as decimal strings instead (see MediusKeySettings).
    public class RsaKeyPair
    {
        public BigInteger N { get; protected set; }
        public BigInteger E { get; protected set; }
        public BigInteger D { get; protected set; }

        public RsaKeyPair() { }
        public RsaKeyPair(BigInteger n, BigInteger e, BigInteger d)
        {
            N = n;
            E = e;
            D = d;
        }

        public PS2_RSA ToPS2() => new PS2_RSA(N, E, D);
        public PS3_RSA ToPS3() => new PS3_RSA(N, E, D);
    }
}
