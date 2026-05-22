using System.Security.Cryptography;
using System.Text;

public class KeyManager
{
    private readonly Guid _userId;
    private const string ConnectionString = "Data Source=localstorage.db";

    public KeyManager(Guid userId) => _userId = userId;

    public void SavePrivateKey(string privateKeyBase64)
    {
        byte[] encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(privateKeyBase64),
            Encoding.UTF8.GetBytes(_userId.ToString()),
            DataProtectionScope.CurrentUser);

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS keypairs (
                user_id TEXT PRIMARY KEY,
                private_key_encrypted BLOB NOT NULL,
                public_key TEXT NOT NULL
            );
            INSERT OR REPLACE INTO keypairs (user_id, private_key_encrypted, public_key)
            VALUES (@uid, @enc, @pub)";
        cmd.Parameters.AddWithValue("@uid", _userId.ToString());
        cmd.Parameters.AddWithValue("@enc", encrypted);
        cmd.Parameters.AddWithValue("@pub", ""); // you can store pubkey too, but not needed
        cmd.ExecuteNonQuery();
    }

    public string LoadPrivateKey(Guid userId)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT private_key_encrypted FROM keypairs WHERE user_id = @uid";
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        var result = cmd.ExecuteScalar();
        if (result == null || result == DBNull.Value)
            throw new Exception("Private key not found for this user.");

        byte[] encrypted = (byte[])result;
        byte[] decrypted = ProtectedData.Unprotect(
            encrypted,
            Encoding.UTF8.GetBytes(userId.ToString()),
            DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }
}