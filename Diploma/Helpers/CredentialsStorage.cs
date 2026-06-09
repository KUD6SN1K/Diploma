using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Diploma.Helpers
{
    public static class CredentialsStorage
    {
        private const string ConnectionString = "Data Source=localstorage.db";

        public static void SaveCredentials(string username, string passwordBlobBase64)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(username + "|" + passwordBlobBase64);
            byte[] encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS saved_credentials (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    encrypted_data BLOB NOT NULL
                );
                INSERT OR REPLACE INTO saved_credentials (id, encrypted_data) VALUES (1, @data);";
            cmd.Parameters.AddWithValue("@data", encrypted);
            cmd.ExecuteNonQuery();
        }

        public static (string username, string passwordBlob)? LoadCredentials()
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS saved_credentials (id INTEGER PRIMARY KEY CHECK (id = 1), encrypted_data BLOB NOT NULL);";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "SELECT encrypted_data FROM saved_credentials WHERE id = 1;";
            var result = cmd.ExecuteScalar();
            if (result == null || result == DBNull.Value) return null;

            try
            {
                byte[] encrypted = (byte[])result;
                byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                string data = Encoding.UTF8.GetString(plain);
                var parts = data.Split('|', 2);
                if (parts.Length == 2) return (parts[0], parts[1]);
            }
            catch { }
            return null;
        }

        public static void DeleteCredentials()
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM saved_credentials;";
            cmd.ExecuteNonQuery();
        }
    }
}