/*
 * Author: ByronP
 * Date: 8/19/2017
 * Coinigy Inc. Coinigy.com
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebsocketClientLite.PCL;

namespace PureWebSockets
{
    public class PureWebSocket : IDisposable
    {
        private string Url { get; }
        private bool IgnoreCertErrors { get; }
        private MessageWebSocketRx _ws;
        private readonly BlockingCollection<KeyValuePair<DateTime, string>> _sendQueue = new BlockingCollection<KeyValuePair<DateTime, string>>();
        private readonly ReconnectStrategy _reconnectStrategy;
        private bool _disconnectCalled;
        private bool _senderRunning;
        private bool _reconnecting;
        private CancellationTokenSource _tokenSource = new CancellationTokenSource();
        private Task _senderTask;
        private IObservable<string> _messageObserver;

        public bool IsConnected => _ws.IsConnected;
        public TimeSpan SendCacheItemTimeout { get; set; }
        public ushort SendDelay { get; set; }
        public int SendQueueLength => _sendQueue.Count;
        public int SendQueueLimit { get; set; }
        public bool DebugMode { get; set; }
        public Dictionary<string, string> RequestHeaders { get; set; }
        public int DisconnectWait { get; set; }

        public event Message OnMessage;
        public event StateChanged OnStateChanged;
        public event Opened OnOpened;
        public event Closed OnClosed;
        public event Error OnError;
        public event SendFailed OnSendFailed;
        public event Fatality OnFatality;

        public PureWebSocket(string url, Dictionary<string, string> requestHeaders = null, int queueLimit = 1000, bool ignoreCertErrors = false)
        {
            Log("Creating new instance.");
            SendQueueLimit = queueLimit;
            Url = url;
            RequestHeaders = requestHeaders;
            IgnoreCertErrors = ignoreCertErrors;
            InitializeClient();

            SendCacheItemTimeout = TimeSpan.FromMinutes(30);
            SendDelay = 80;
            DisconnectWait = 20000;
        }

        public PureWebSocket(string url, TimeSpan sendCacheItemTimeout, Dictionary<string, string> requestHeaders = null, int queueLimit = 1000, bool ignoreCertErrors = false)
        {
            Log("Creating new instance.");
            SendQueueLimit = queueLimit;
            Url = url;
            RequestHeaders = requestHeaders;
            IgnoreCertErrors = ignoreCertErrors;
            InitializeClient();

            SendCacheItemTimeout = sendCacheItemTimeout;
            SendDelay = 80;
        }

        public PureWebSocket(string url, ReconnectStrategy reconnectStrategy, Dictionary<string, string> requestHeaders = null, int queueLimit = 1000, bool ignoreCertErrors = false)
        {
            Log("Creating new instance.");
            SendQueueLimit = queueLimit;
            Url = url;
            _reconnectStrategy = reconnectStrategy;
            RequestHeaders = requestHeaders;
            IgnoreCertErrors = ignoreCertErrors;
            InitializeClient();

            SendCacheItemTimeout = TimeSpan.FromMinutes(30);
            SendDelay = 80;
        }

        public PureWebSocket(string url, TimeSpan sendCacheItemTimeout, ReconnectStrategy reconnectStrategy, int queueLimit = 1000, bool ignoreCertErrors = false)
        {
            Log("Creating new instance.");
            SendQueueLimit = queueLimit;
            Url = url;
            _reconnectStrategy = reconnectStrategy;
            IgnoreCertErrors = ignoreCertErrors;
            InitializeClient();

            SendCacheItemTimeout = sendCacheItemTimeout;
            SendDelay = 80;
        }

        public PureWebSocket(string url, TimeSpan sendCacheItemTimeout, ReconnectStrategy reconnectStrategy, Dictionary<string, string> requestHeaders = null, int queueLimit = 1000, bool ignoreCertErrors = false)
        {
            Log("Creating new instance.");
            SendQueueLimit = queueLimit;
            Url = url;
            _reconnectStrategy = reconnectStrategy;
            RequestHeaders = requestHeaders;
            IgnoreCertErrors = ignoreCertErrors;
            InitializeClient();

            SendCacheItemTimeout = sendCacheItemTimeout;
            SendDelay = 80;
        }

        private void InitializeClient()
        {
            _ws = new MessageWebSocketRx();
        }

        public async Task<bool> ConnectAsync()
        {
            Log("Connect called.");
            try
            {
                _disconnectCalled = false;
                if (Url.ToLower().StartsWith("https://") || Url.ToLower().StartsWith("wss://"))
                    _messageObserver = await _ws.CreateObservableMessageReceiver(new Uri(Url), null, RequestHeaders, null, IgnoreCertErrors, ISocketLite.PCL.Model.TlsProtocolVersion.Tls12, false);
                else
                    _messageObserver = await _ws.CreateObservableMessageReceiver(new Uri(Url), null, RequestHeaders, null, IgnoreCertErrors, ISocketLite.PCL.Model.TlsProtocolVersion.None, false);
                Log("Starting tasks.");

                var subscribeToMessagesReceived = _messageObserver.Subscribe(
                msg =>
                {
                    OnMessage?.Invoke(msg);
                },
                ex =>
                {
                    Log(ex.Message);
                    OnError?.Invoke(ex);
                },
                () =>
                {
                    Log($"Subscription Completed");
                    OnClosed?.Invoke();
                });
                StartSender();

                Task.Run(() =>
                {
                    while (!_ws.IsConnected)
                    {

                    }
                }).Wait(15000);
                Log($"Connect result: {_ws.IsConnected}");
                if (_ws.IsConnected)
                    OnOpened?.Invoke();
                return _ws.IsConnected;
            }
            catch (Exception ex)
            {
                Log($"Connect threw exception: {ex.Message}.");
                OnError?.Invoke(ex);
                throw;
            }
        }

        public bool Send(string data)
        {
            try
            {
                if (!_ws.IsConnected || SendQueueLength >= SendQueueLimit)
                {
                    Log(SendQueueLength >= SendQueueLimit ? $"Could not add item to send queue: queue limit reached, Data {data}" : $"Could not add item to send queue: Connected {_ws.IsConnected}, Queue Count {SendQueueLength}, Data {data}");
                    return false;
                }
                Task.Run(() =>
                {
                    Log($"Adding item to send queue: Data {data}");
                    _sendQueue.Add(new KeyValuePair<DateTime, string>(DateTime.UtcNow, data));
                }).Wait(100, _tokenSource.Token);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Send threw exception: {ex.Message}.");
                OnError?.Invoke(ex);
                throw;
            }
        }

        private async Task DoReconnect()
        {
            Log("Entered reconnect.");
            _tokenSource.Cancel();
            _reconnecting = true;
            if (!Task.WaitAll(new[] { _senderTask }, 15000))
            {
                Log("Reconnect fatality, tasks failed to stop before the timeout.");
                // exit everything as dead...
                OnFatality?.Invoke("Fatal network error. Network services fail to shut down.");
                _reconnecting = false;
                _disconnectCalled = true;
                _tokenSource.Cancel();
                return;
            }
            Log("Disposing of current websocket.");
            _ws.Dispose();

            OnStateChanged?.Invoke(false);

            _tokenSource = new CancellationTokenSource();

            var connected = false;
            while (!_disconnectCalled && !_disposedValue && !connected && !_tokenSource.IsCancellationRequested)
                try
                {
                    Log("Creating new websocket.");
                    InitializeClient();

                    Log("Attempting connect.");
                    connected = await ConnectAsync();//_ws.ConnectAsync(new Uri(Url), _tokenSource.Token).Wait(15000);

                    Log($"Connect result: {connected}");
                }
                catch (Exception ex)
                {
                    Log($"Reconnect threw an error: {ex.Message}.");
                    Log("Disposing of current websocket.");
                    _ws.Dispose();
                    Log("Processing reconnect strategy.");
                    Thread.Sleep(_reconnectStrategy.GetReconnectInterval());
                    _reconnectStrategy.ProcessValues();
                    if (_reconnectStrategy.AreAttemptsComplete())
                    {
                        Log("Reconnect strategy has reached max connection attempts, going to fatality.");
                        // exit everything as dead...
                        OnFatality?.Invoke("Fatal network error. Max reconnect attemps reached.");
                        _reconnecting = false;
                        _disconnectCalled = true;
                        _tokenSource.Cancel();
                        _messageObserver = null;
                        _ws = null;

                        return;
                    }
                }
            if (connected)
            {
                Log("Reconnect success, restarting tasks.");
                _reconnecting = false;
                //if (!_monitorRunning)
                //    StartMonitor();
                //if (!_listenerRunning)
                //    StartListener();
                if (!_senderRunning)
                    StartSender();
            }
            else
            {
                Log("Reconnect failed.");
            }
        }

        private void StartSender()
        {
            Log("Starting sender.");
            _senderTask = Task.Run(async () =>
            {
                Log("Entering sender loop.");
                _senderRunning = true;
                try
                {
                    while (!_disposedValue && !_reconnecting)
                    {
                        if (_ws.IsConnected && !_reconnecting)
                        {
                            var msg = _sendQueue.Take(_tokenSource.Token);
                            if (msg.Key.Add(SendCacheItemTimeout) < DateTime.UtcNow)
                            {
                                Log($"Message expired skipping: {msg.Key} {msg.Value}.");
                                continue;
                            }
                            var buffer = Encoding.UTF8.GetBytes(msg.Value);
                            try
                            {
                                Log($"Sending message: {msg.Key} {msg.Value}.");
                                await _ws.SendTextAsync(Encoding.UTF8.GetString(buffer));//new ArraySegment<byte>(buffer));
                            }
                            catch (Exception ex)
                            {
                                Log($"Sender threw sending exception: {ex.Message}.");
                                // Most likely socket error
                                OnSendFailed?.Invoke(msg.Value, ex);
                                await _ws.CloseAsync();
                                break;
                            }
                        }
                        // limit to N ms per iteration
                        Thread.Sleep(SendDelay);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Sender threw exception: {ex.Message}.");
                    OnSendFailed?.Invoke("", ex);
                    OnError?.Invoke(ex);
                }
                _senderRunning = false;
                Log("Exiting sender.");
                return Task.CompletedTask;
            });
        }

        public void Disconnect()
        {
            try
            {
                Log("Disconnect called, closing websocket.");
                _disconnectCalled = true;
                _ws.CloseAsync();//WebSocketCloseStatus.NormalClosure, "NORMAL SHUTDOWN", _tokenSource.Token).Wait(DisconnectWait);
            }
            catch (Exception ex)
            {
                Log($"Disconnect threw exception: {ex.Message}.");
                // ignored
            }
        }

        #region IDisposable Support

        private bool _disposedValue; // To detect redundant calls

        protected virtual void Dispose(bool disposing, bool waitForSendsToComplete)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // dispose managed state (managed objects).
                    if (_sendQueue.Count > 0 && _senderRunning)
                    {
                        var i = 0;
                        while (_sendQueue.Count > 0 && _senderRunning)
                        {
                            i++;
                            Task.Delay(1000).Wait();
                            if (i > 25)
                                break;
                        }
                    }
                    Disconnect();
                    _tokenSource.Cancel();
                    Thread.Sleep(500);
                    _tokenSource.Dispose();
                    _ws.Dispose();
                    GC.Collect();
                }

                _disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            Log("Dispose called.");
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
        }

        public void Dispose(bool waitForSendsToComplete)
        {
            Log($"Dispose called, with waitForSendsToComplete = {waitForSendsToComplete}.");
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true, waitForSendsToComplete);
        }

        #endregion

        internal void Log(string message, [CallerMemberName] string memberName = "")
        {
            if (DebugMode)
                Task.Run(() => Console.WriteLine($"{DateTime.Now:O} PureWebSocket.{memberName}: {message}"));
        }
    }
}