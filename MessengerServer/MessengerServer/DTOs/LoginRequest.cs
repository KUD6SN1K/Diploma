namespace MessengerServer.DTOs
{
    public class LoginRequest
    {
        public string Username { get; set; }
        public string Password { get; set; }   // base64(hash+alt)
    }
}
