public class RegisterRequest
{
    public string Username { get; set; }
    public string Password { get; set; }         // plaintext from client (hash will be computed on client side)
    public string DisplayName { get; set; }
    public string EccPublicKey { get; set; }
}