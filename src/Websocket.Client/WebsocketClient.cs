﻿using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client.Logging;

namespace Websocket.Client
{
    /// <summary>
    /// A simple websocket client with built-in reconnection and error handling
    /// </summary>
    public class WebsocketClient : IWebsocketClient
    {
        private static readonly ILog Logger = LogProvider.GetCurrentClassLogger();

        private readonly Uri _url;
        private Timer _lastChanceTimer;
        private readonly Func<ClientWebSocket> _clientFactory;

        private DateTime _lastReceivedMsg = DateTime.UtcNow; 

        private bool _disposing = false;
        private ClientWebSocket _client;
        private CancellationTokenSource _cancellation;
        private CancellationTokenSource _cancellationTotal;

        private readonly Subject<string> _messageReceivedSubject = new Subject<string>();
        private readonly Subject<ReconnectionType> _reconnectionSubject = new Subject<ReconnectionType>();
        private readonly Subject<DisconnectionType> _disconnectedSubject = new Subject<DisconnectionType>();

        private readonly BlockingCollection<string> _messagesToSendQueue = new BlockingCollection<string>();

        /// <inheritdoc />
        public WebsocketClient(Uri url, Func<ClientWebSocket> clientFactory = null)
        {
            Validations.Validations.ValidateInput(url, nameof(url));

            _url = url;
            _clientFactory = clientFactory ?? (() => new ClientWebSocket()
            {
                Options = {KeepAliveInterval = new TimeSpan(0, 0, 5, 0)}
            }); 
        }

        /// <summary>
        /// Stream with received message (raw format)
        /// </summary>
        public IObservable<string> MessageReceived => _messageReceivedSubject.AsObservable();

        /// <summary>
        /// Stream for reconnection event (triggered after the new connection) 
        /// </summary>
        public IObservable<ReconnectionType> ReconnectionHappened => _reconnectionSubject.AsObservable();

        /// <summary>
        /// Stream for disconnection event (triggered after the connection was lost) 
        /// </summary>
        public IObservable<DisconnectionType> DisconnectionHappened => _disconnectedSubject.AsObservable();

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if no message comes from server.
        /// Default 60000 ms (1 minute)
        /// </summary>
        public int ReconnectTimeoutMs { get; set; } = 60 * 1000;

        /// <summary>
        /// Time range in ms, how long to wait before reconnecting if last reconnection failed.
        /// Default 60000 ms (1 minute)
        /// </summary>
        public int ErrorReconnectTimeoutMs { get; set; } = 60 * 1000;

        /// <summary>
        /// Get or set the name of the current websocket client instance.
        /// For logging purpose (in case you use more parallel websocket clients and want to distinguish between them)
        /// </summary>
        public string Name { get; set;}

        /// <summary>
        /// Returns true if Start() method was called at least once. False if not started or disposed
        /// </summary>
        public bool IsStarted { get; private set; }

        /// <summary>
        /// Returns true if client is running and connected to the server
        /// </summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Terminate the websocket connection and cleanup everything
        /// </summary>
        public void Dispose()
        {
            _disposing = true;
            Logger.Debug(L("Disposing.."));
            try
            {
                _lastChanceTimer?.Dispose();
                _cancellation?.Cancel();
                _cancellationTotal?.Cancel();
                _client?.Abort();
                _client?.Dispose();
                _cancellation?.Dispose();
                _cancellationTotal?.Dispose();
                _messagesToSendQueue?.Dispose();
            }
            catch (Exception e)
            {
                Logger.Error(e, L($"Failed to dispose client, error: {e.Message}"));
            }

            IsStarted = false;
            _disconnectedSubject.OnNext(DisconnectionType.Exit);
        }
       
        /// <summary>
        /// Start listening to the websocket stream on the background thread
        /// </summary>
        public async Task Start()
        {
            if (IsStarted)
            {
                Logger.Debug(L("Client already started, ignoring.."));
                return;
            }
            IsStarted = true;

            Logger.Debug(L("Starting.."));
            _cancellation = new CancellationTokenSource();
            _cancellationTotal = new CancellationTokenSource();

            await StartClient(_url, _cancellation.Token, ReconnectionType.Initial).ConfigureAwait(false);

            StartBackgroundThreadForSending();
        }

        /// <summary>
        /// Send message to the websocket channel. 
        /// It inserts the message to the queue and actual sending is done on an other thread
        /// </summary>
        /// <param name="message">Message to be sent</param>
        public Task Send(string message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            _messagesToSendQueue.Add(message);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Send message to the websocket channel. 
        /// It doesn't use a sending queue, 
        /// beware of issue while sending two messages in the exact same time 
        /// on the full .NET Framework platform
        /// </summary>
        /// <param name="message">Message to be sent</param>
        public Task SendInstant(string message)
        {
            Validations.Validations.ValidateInput(message, nameof(message));

            return SendInternal(message);
        }

        /// <summary>
        /// Force reconnection. 
        /// Closes current websocket stream and perform a new connection to the server.
        /// </summary>
        public async Task Reconnect()
        {
            if (!IsStarted)
            {
                Logger.Debug(L("Client not started, ignoring reconnection.."));
                return;
            }
            await Reconnect(ReconnectionType.ByUser).ConfigureAwait(false);
        }

        private async Task SendFromQueue()
        {
            try
            {
                foreach (var message in _messagesToSendQueue.GetConsumingEnumerable(_cancellationTotal.Token))
                {
                    try
                    {
                        await SendInternal(message).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Logger.Error(L($"Failed to send message: '{message}'. Error: {e.Message}"));
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // task was canceled, ignore
            }
            catch (Exception e)
            {
                if (_cancellationTotal.IsCancellationRequested || _disposing)
                {
                    // disposing/canceling, do nothing and exit
                    return;
                }

                StartBackgroundThreadForSending();
            }

        }

        private void StartBackgroundThreadForSending()
        {
#pragma warning disable 4014
            Task.Factory.StartNew(_ => SendFromQueue(), TaskCreationOptions.LongRunning, _cancellationTotal.Token);
#pragma warning restore 4014
        }

        private async Task SendInternal(string message)
        {
            Logger.Trace(L($"Sending:  {message}"));
            var buffer = Encoding.UTF8.GetBytes(message);
            var messageSegment = new ArraySegment<byte>(buffer);
            var client = await GetClient().ConfigureAwait(false);
            await client.SendAsync(messageSegment, WebSocketMessageType.Text, true, _cancellation.Token).ConfigureAwait(false);
        }

        private async Task StartClient(Uri uri, CancellationToken token, ReconnectionType type)
        {
            DeactivateLastChance();
            _client = _clientFactory();
            
            try
            {
                await _client.ConnectAsync(uri, token).ConfigureAwait(false);
                IsRunning = true;
                _reconnectionSubject.OnNext(type);
#pragma warning disable 4014
                Listen(_client, token);
#pragma warning restore 4014               
                ActivateLastChance();
            }
            catch (Exception e)
            {
                _disconnectedSubject.OnNext(DisconnectionType.Error);
                Logger.Error(e, L("Exception while connecting. " +
                               $"Waiting {ErrorReconnectTimeoutMs/1000} sec before next reconnection try."));
                await Task.Delay(ErrorReconnectTimeoutMs, token).ConfigureAwait(false);
                await Reconnect(ReconnectionType.Error).ConfigureAwait(false);
            }       
        }

        private async Task<ClientWebSocket> GetClient()
        {
            if (_client == null || (_client.State != WebSocketState.Open && _client.State != WebSocketState.Connecting))
            {
                await Reconnect(ReconnectionType.Lost).ConfigureAwait(false);
            }
            return _client;
        }

        private async Task Reconnect(ReconnectionType type)
        {
            IsRunning = false;
            if (_disposing)
                return;
            if(type != ReconnectionType.Error)
                _disconnectedSubject.OnNext(TranslateTypeToDisconnection(type));

            Logger.Debug(L("Reconnecting..."));
            _cancellation.Cancel();
            await Task.Delay(1000).ConfigureAwait(false);

            _cancellation = new CancellationTokenSource();
            await StartClient(_url, _cancellation.Token, type).ConfigureAwait(false);
        }

        private async Task Listen(ClientWebSocket client, CancellationToken token)
        {
            try
            {
                do
                {
                    WebSocketReceiveResult result = null;
                    var buffer = new byte[1000];
                    var message = new ArraySegment<byte>(buffer);
                    var resultMessage = new StringBuilder();
                    do
                    {
                        result = await client.ReceiveAsync(message, token).ConfigureAwait(false);
                        var receivedMessage = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        resultMessage.Append(receivedMessage);
                        if (result.MessageType != WebSocketMessageType.Text)
                            break;

                    } while (!result.EndOfMessage);

                    var received = resultMessage.ToString();
                    Logger.Trace(L($"Received:  {received}"));
                    _lastReceivedMsg = DateTime.UtcNow;
                    _messageReceivedSubject.OnNext(received);

                } while (client.State == WebSocketState.Open && !token.IsCancellationRequested);
            }
            catch (TaskCanceledException)
            {
                // task was canceled, ignore
            }
            catch (Exception e)
            {
                Logger.Error(e, L("Error while listening to websocket stream"));
            }
        }

        private void ActivateLastChance()
        {
            var timerMs = 1000 * 5;
            _lastChanceTimer = new Timer(LastChance, null, timerMs, timerMs);
        }

        private void DeactivateLastChance()
        {
            _lastChanceTimer?.Dispose();
            _lastChanceTimer = null;
        }

        private void LastChance(object state)
        {
            var timeoutMs = Math.Abs(ReconnectTimeoutMs);
            var diffMs = Math.Abs(DateTime.UtcNow.Subtract(_lastReceivedMsg).TotalMilliseconds);
            if (diffMs > timeoutMs)
            {
                Logger.Debug(L($"Last message received more than {timeoutMs:F} ms ago. Hard restart.."));

                DeactivateLastChance();
                _client?.Abort();
                _client?.Dispose();
#pragma warning disable 4014
                Reconnect(ReconnectionType.NoMessageReceived);
#pragma warning restore 4014
            }
        }

        private string L(string msg)
        {
            var name = Name ?? "CLIENT";
            return $"[WEBSOCKET {name}] {msg}";
        }

        private DisconnectionType TranslateTypeToDisconnection(ReconnectionType type)
        {
            // beware enum indexes must correspond to each other
            return (DisconnectionType) type;
        }
    }
}
