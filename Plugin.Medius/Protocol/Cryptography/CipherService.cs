using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace Plugin.Medius.RT.Cryptography
{
    public class CipherService
    {
        private ICipherFactory _factory = null;
        private readonly ConcurrentDictionary<CipherContext, ICipher> _ciphers = new ConcurrentDictionary<CipherContext, ICipher>();

        public bool EnableEncryption { get; set; } = true;

        public CipherService(ICipherFactory factory)
        {
            _factory = factory;
        }

        public void GenerateCipher(CipherContext context)
        {
            _ciphers[context] = _factory.CreateNew(context);
        }

        public void GenerateCipher(CipherContext context, byte[] publicKey)
        {
            _ciphers[context] = _factory.CreateNew(context, publicKey);
        }

        public void GenerateCipher(RsaKeyPair rsaKeyPair)
        {
            _ciphers[CipherContext.RSA_AUTH] = _factory.CreateNew(rsaKeyPair);
        }

        public void SetCipher(CipherContext context, ICipher cipher)
        {
            _ciphers[context] = cipher;
        }

        public bool HasKey(CipherContext context)
        {
            return _ciphers.TryGetValue(context, out var value) && value != null;
        }

        public byte[] GetPublicKey(CipherContext context)
        {
            var cipher = _ciphers[context];
            if (cipher == null)
                throw new KeyNotFoundException($"The CipherContext {context} does not have a cipher associated with it.");

            return cipher.GetPublicKey();
        }

        public bool Encrypt(CipherContext context, byte[] input, out byte[] cipher, out byte[] hash)
        {
            cipher = null;
            hash = null;
            if (!EnableEncryption || !_ciphers.TryGetValue(context, out var c) || c == null)
                return false;

            return c.Encrypt(input, out cipher, out hash);
        }

        public bool Decrypt(byte[] input, byte[] hash, out byte[] plain)
        {
            var cipherContext = (CipherContext)(hash[3] >> 5);
            return Decrypt(cipherContext, input, hash, out plain);
        }

        public bool Decrypt(CipherContext context, byte[] input, byte[] hash, out byte[] plain)
        {
            if (!_ciphers.TryGetValue(context, out var cipher) || cipher == null)
                throw new KeyNotFoundException($"The CipherContext {context} does not have a cipher associated with it.");

            return cipher.Decrypt(input, hash, out plain);
        }

        /// <summary>
        /// Decrypts a message whose first <paramref name="clear"/> bytes were sent unencrypted, trying the
        /// hash over the encrypted part alone and then over the whole message. Returns how it matched
        /// ("payload" or "whole"), or null when neither did.
        /// </summary>
        public string DecryptAfter(int clear, byte[] input, byte[] hash, out byte[] plain)
        {
            plain = null;
            var context = (CipherContext)(hash[3] >> 5);
            if (clear <= 0 || clear >= input.Length || !_ciphers.TryGetValue(context, out var cipher) || cipher == null)
                return null;
            byte[] encrypted = input[clear..];
            bool payloadHash = cipher.Decrypt(encrypted, hash, out byte[] decrypted);
            if (decrypted == null)
                return null;
            plain = [.. input[..clear], .. decrypted];
            if (payloadHash)
                return "payload";
            if (cipher is PS3_RCQ && PS3_RCQ.Hash(plain, context).AsSpan().SequenceEqual(hash))
                return "whole";
            plain = null;
            return null;
        }

    }
}
