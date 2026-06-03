using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows; // for Dispatcher

namespace Diploma.Services
{
    public class WebSocketService
    {
        private ClientWebSocket _socket;
        private readonly string _url;
        private readonly Action<string> _onMessage;
        private CancellationTokenSource _cts;

        public WebSocketService(string url, Action<string> onMessage)
        {
            _url = url;
            _onMessage = onMessage;
        }

        public async Task ConnectAsync()
        {
            _cts = new CancellationTokenSource();
            _socket = new ClientWebSocket();
            await _socket.ConnectAsync(new Uri(_url), _cts.Token);

            _ = Task.Run(async () =>
            {
                var buffer = new byte[8192];
                var sb = new StringBuilder();

                try
                {
                    while (_socket.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult result;

                        do
                        {
                            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                            if (result.MessageType == WebSocketMessageType.Close)
                                return;

                            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                        } while (!result.EndOfMessage);

                        var message = sb.ToString();
                        sb.Clear();

                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _onMessage(message);
                        }));
                    }
                }
                catch
                {
                }
            });
        }

        public async Task CloseAsync()
        {
            _cts?.Cancel();
            if (_socket?.State == WebSocketState.Open)
            {
                try
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                catch { }
            }
            _socket?.Dispose();
        }
    }
}