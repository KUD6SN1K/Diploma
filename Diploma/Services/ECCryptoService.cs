using System;
using System.Security.Cryptography;

namespace Diploma.Services
{
    public static class ECCryptoService
    {
        public static (string publicKey, string privateKey) GenerateKeyPair()
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            string pub = Convert.ToBase64String(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
            string priv = Convert.ToBase64String(ecdh.ExportECPrivateKey());
            return (pub, priv);
        }

        // Encrypt using sender's static private key and recipient's static public key
        public static byte[] EncryptData(byte[] plainText, string senderPrivateKey, string recipientPublicKey)
        {
            byte[] privKeyBytes = Convert.FromBase64String(senderPrivateKey);
            byte[] pubKeyBytes = Convert.FromBase64String(recipientPublicKey);

            using var senderEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            senderEcdh.ImportECPrivateKey(privKeyBytes, out _);

            using var recipientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            recipientEcdh.ImportSubjectPublicKeyInfo(pubKeyBytes, out _);

            byte[] sharedSecret = senderEcdh.DeriveKeyMaterial(recipientEcdh.PublicKey);
            byte[] aesKey = DeriveAesKey(sharedSecret);
            return AesGcmEncrypt(plainText, aesKey);
        }

        // Decrypt using recipient's static private key and sender's static public key
        public static byte[] DecryptData(byte[] ciphertext, string recipientPrivateKey, string senderPublicKey)
        {
            byte[] privKeyBytes = Convert.FromBase64String(recipientPrivateKey);
            byte[] pubKeyBytes = Convert.FromBase64String(senderPublicKey);

            using var recipientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            recipientEcdh.ImportECPrivateKey(privKeyBytes, out _);

            using var senderEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            senderEcdh.ImportSubjectPublicKeyInfo(pubKeyBytes, out _);

            byte[] sharedSecret = recipientEcdh.DeriveKeyMaterial(senderEcdh.PublicKey);
            byte[] aesKey = DeriveAesKey(sharedSecret);
            return AesGcmDecrypt(ciphertext, aesKey);
        }

        private static byte[] DeriveAesKey(byte[] sharedSecret)
        {
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(sharedSecret);
        }

        private static byte[] AesGcmEncrypt(byte[] plainText, byte[] key)
        {
            byte[] nonce = new byte[12];
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[plainText.Length];
            RandomNumberGenerator.Fill(nonce);
            using var aesGcm = new AesGcm(key, tag.Length);
            aesGcm.Encrypt(nonce, plainText, ciphertext, tag);
            byte[] result = new byte[nonce.Length + ciphertext.Length + tag.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
            Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, result, nonce.Length + ciphertext.Length, tag.Length);
            return result;
        }

        private static byte[] AesGcmDecrypt(byte[] encryptedData, byte[] key)
        {
            byte[] nonce = new byte[12];
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[encryptedData.Length - nonce.Length - tag.Length];
            Buffer.BlockCopy(encryptedData, 0, nonce, 0, nonce.Length);
            Buffer.BlockCopy(encryptedData, nonce.Length, ciphertext, 0, ciphertext.Length);
            Buffer.BlockCopy(encryptedData, nonce.Length + ciphertext.Length, tag, 0, tag.Length);
            byte[] plainText = new byte[ciphertext.Length];
            using var aesGcm = new AesGcm(key, tag.Length);
            aesGcm.Decrypt(nonce, ciphertext, tag, plainText);
            return plainText;
        }
    }
}