using System;
using System.Collections.Generic;
using System.Text;

namespace Plugin.Medius.RT.Cryptography
{
    public interface ICipherFactory
    {
        ICipher CreateNew(CipherContext context);
        ICipher CreateNew(CipherContext context, byte[] publicKey);
        ICipher CreateNew(RsaKeyPair rsaKeyPair);
    }
}
