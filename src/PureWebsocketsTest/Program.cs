using System;
using System.Threading;
using PureWebSockets;

namespace CoreWebsocketsTest
{
    public class Program
    {
        private static PureWebSocket _ws;
        private static int _sendCount = 0;
        private static Timer _timer;
        public static void Main(string[] args)
        {
            _timer = new Timer(OnTick, null, 2000, 1);

            RESTART:
            _ws = new PureWebSocket("wss://echo.websocket.org", new ReconnectStrategy(10000, 60000), null, 100);
            _ws.DebugMode = false;
            _ws.SendDelay = 100;
            _ws.OnStateChanged += Ws_OnStateChanged;
            _ws.OnMessage += Ws_OnMessage;
            _ws.OnClosed += Ws_OnClosed;
            _ws.OnSendFailed += Ws_OnSendFailed;
            var res = _ws.ConnectAsync().Result;
            
            Console.ReadLine();
            _ws.Dispose(true);
            goto RESTART;
        }

        private static void Ws_OnSendFailed(string data, Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{DateTime.Now} Send Failed: {ex.Message}");
            Console.ResetColor();
            Console.WriteLine("");
        }

        private static void OnTick(object state)
        {
            if (!_ws.IsConnected) return;

            if (_ws.SendQueueLength >= _ws.SendQueueLimit)
            {
                _timer.Dispose();
                _timer = new Timer(OnTick, null, 60000, 1);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"{DateTime.Now} Max Send Count Reached: {_sendCount}");
                Console.ResetColor();
                Console.WriteLine("");
                _sendCount = 0;
                return;
            }
            if (_ws.Send(_sendCount + " | " + DateTime.Now.Ticks.ToString()))
            {
                _sendCount++;
            }
            else
            {
                Ws_OnSendFailed("", new Exception("Send Returned False"));
            }
        }

        private static void Ws_OnClosed()
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"{DateTime.Now} Connection Closed");
            Console.ResetColor();
            Console.WriteLine("");
            Console.ReadLine();
        }

        private static void Ws_OnMessage(string message)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"{DateTime.Now} New message: {message}");
            Console.ResetColor();
            Console.WriteLine("");
        }

        private static void Ws_OnStateChanged(bool connected)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{DateTime.Now} Status changed to {connected}");
            Console.ResetColor();
            Console.WriteLine("");
        }
    }
}