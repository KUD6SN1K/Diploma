using System;
using System.Security.Cryptography;

namespace Diploma
{
    public static class ECCryptoService
    {
        // Генерация пары ключей на эллиптической кривой NIST P-256
        public static (string publicKey, string privateKey) GenerateKeyPair()
        {
            using (var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            {
                string publicKey = Convert.ToBase64String(
                    ecdh.PublicKey.ExportSubjectPublicKeyInfo());
                string privateKey = Convert.ToBase64String(
                    ecdh.ExportECPrivateKey());
                return (publicKey, privateKey);
            }
        }

        // Шифрование сообщения открытым ключом получателя (ECIES)
        public static (byte[] ciphertext, string ephemeralPublicKey) EncryptData(
            byte[] plainText, string recipientPublicKeyBase64)
        {
            byte[] recipientPublicKeyBytes = Convert.FromBase64String(recipientPublicKeyBase64);

            // Генерация эфемерной пары ключей отправителя
            using (var ephemeralEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            {
                string ephemeralPublicKey = Convert.ToBase64String(
                    ephemeralEcdh.PublicKey.ExportSubjectPublicKeyInfo());

                // Импорт открытого ключа получателя
                using (var recipientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
                {
                    recipientEcdh.ImportSubjectPublicKeyInfo(recipientPublicKeyBytes, out _);

                    // Вычисление общего секрета
                    byte[] sharedSecret = ephemeralEcdh.DeriveKeyMaterial(recipientEcdh.PublicKey);

                    // Извлечение AES-ключа из общего секрета (HKDF)
                    byte[] aesKey = DeriveAesKey(sharedSecret);

                    // Шифрование данных AES-GCM
                    byte[] ciphertext = AesGcmEncrypt(plainText, aesKey);

                    return (ciphertext, ephemeralPublicKey);
                }
            }
        }

        // Расшифрование сообщения
        public static byte[] DecryptData(
            byte[] ciphertext, string ephemeralPublicKeyBase64, string recipientPrivateKeyBase64)
        {
            byte[] ephemeralPublicKeyBytes = Convert.FromBase64String(ephemeralPublicKeyBase64);
            byte[] recipientPrivateKeyBytes = Convert.FromBase64String(recipientPrivateKeyBase64);

            // Импорт закрытого ключа получателя
            using (var recipientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            {
                recipientEcdh.ImportECPrivateKey(recipientPrivateKeyBytes, out _);

                // Импорт эфемерного открытого ключа отправителя
                using (var ephemeralEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
                {
                    ephemeralEcdh.ImportSubjectPublicKeyInfo(ephemeralPublicKeyBytes, out _);

                    // Вычисление общего секрета
                    byte[] sharedSecret = recipientEcdh.DeriveKeyMaterial(ephemeralEcdh.PublicKey);

                    // Извлечение AES-ключа из общего секрета
                    byte[] aesKey = DeriveAesKey(sharedSecret);

                    // Расшифрование данных AES-GCM
                    return AesGcmDecrypt(ciphertext, aesKey);
                }
            }
        }

        // Извлечение 256-битного AES-ключа из общего секрета через SHA-256
        private static byte[] DeriveAesKey(byte[] sharedSecret)
        {
            using (var sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(sharedSecret);
            }
        }

        // Шифрование данных с помощью AES-GCM
        private static byte[] AesGcmEncrypt(byte[] plainText, byte[] key)
        {
            byte[] nonce = new byte[12]; // 96-битный nonce (AES-GCM рекомендует 12 байт)
            byte[] tag = new byte[16];   // 128-битный тег аутентификации
            byte[] ciphertext = new byte[plainText.Length];

            RandomNumberGenerator.Fill(nonce);

            using (var aesGcm = new AesGcm(key, tag.Length))
            {
                aesGcm.Encrypt(nonce, plainText, ciphertext, tag);
            }

            // Упаковываем: nonce + ciphertext + tag
            byte[] result = new byte[nonce.Length + ciphertext.Length + tag.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
            Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, result, nonce.Length + ciphertext.Length, tag.Length);

            return result;
        }

        // Расшифрование данных с помощью AES-GCM
        private static byte[] AesGcmDecrypt(byte[] encryptedData, byte[] key)
        {
            byte[] nonce = new byte[12];
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[encryptedData.Length - nonce.Length - tag.Length];

            Buffer.BlockCopy(encryptedData, 0, nonce, 0, nonce.Length);
            Buffer.BlockCopy(encryptedData, nonce.Length, ciphertext, 0, ciphertext.Length);
            Buffer.BlockCopy(encryptedData, nonce.Length + ciphertext.Length, tag, 0, tag.Length);

            byte[] plainText = new byte[ciphertext.Length];

            using (var aesGcm = new AesGcm(key, tag.Length))
            {
                aesGcm.Decrypt(nonce, ciphertext, tag, plainText);
            }

            return plainText;
        }
    }
}
