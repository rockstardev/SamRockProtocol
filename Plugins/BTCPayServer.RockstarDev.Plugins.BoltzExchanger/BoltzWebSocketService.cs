#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

// Singleton service to manage WebSocket connection(s) to Boltz
public class BoltzWebSocketService : IHostedService, IDisposable
{
    // Store connections per endpoint URL
    private readonly ConcurrentDictionary<Uri, WebSocketConnection> _connections = new();
    private readonly ILogger<BoltzWebSocketService> _logger;
    private readonly ConcurrentDictionary<string, SwapSubscription> _subscriptions = new(); // Key: SwapID
    private Timer? _pingTimer;

    public BoltzWebSocketService(ILogger<BoltzWebSocketService> logger)
    {
        _logger = logger;
    }

    public void Dispose()
    {
        StopAsync(CancellationToken.None).Wait(); // Synchronous wait on dispose
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Boltz WebSocket Service starting.");
        // Defer connection until needed
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Boltz WebSocket Service stopping.");
        _pingTimer?.Dispose();
        _pingTimer = null;

        var cleanupTasks = _connections.Select(kvp => CleanupWebSocketAsync(kvp.Key, kvp.Value)).ToList();
        await Task.WhenAll(cleanupTasks);
        _connections.Clear();
        _subscriptions.Clear();
    }

    // Called by BoltzLightningClient to register interest in a swap
    public async Task SubscribeToSwapAsync(string swapId, Uri apiUrl, Func<SwapStatusUpdate, Task> onUpdate, CancellationToken cancellationToken = default)
    {
        if (_subscriptions.TryAdd(swapId, new SwapSubscription(apiUrl, onUpdate)))
        {
            _logger.LogInformation($"Adding subscription for Boltz swap {swapId} via {apiUrl}");
            var wsUri = GetWebSocketUri(apiUrl);
            var connection = await EnsureConnectedAsync(wsUri, cancellationToken);
            if (connection != null)
            {
                var subscribeMsg = new WebSocketRequest
                {
                    Operation = "subscribe",
                    Channel = "swap.update",
                    Args = new List<string> { swapId }
                };
                await SendWebSocketMessageAsync(connection, subscribeMsg, cancellationToken);
            }
            else
            {
                _logger.LogError($"Failed to ensure WebSocket connection for {wsUri} to subscribe to {swapId}");
                // If connection failed, remove subscription?
                _subscriptions.TryRemove(swapId, out _);
                throw new Exception($"Failed to connect to WebSocket endpoint {wsUri}");
            }
        }
        else
        {
            _logger.LogWarning($"Subscription request for already tracked swap {swapId}");
        }
    }

    // Called by BoltzLightningClient when it no longer needs updates
    public async Task UnsubscribeFromSwapAsync(string swapId, CancellationToken cancellationToken = default)
    {
        if (_subscriptions.TryRemove(swapId, out var subscription))
        {
            _logger.LogInformation($"Removing subscription for Boltz swap {swapId}");
            var wsUri = GetWebSocketUri(subscription.ApiUrl);
            if (_connections.TryGetValue(wsUri, out var connection) && connection.WebSocket.State == WebSocketState.Open)
            {
                var unsubscribeMsg = new WebSocketRequest
                {
                    Operation = "unsubscribe",
                    Channel = "swap.update",
                    Args = new List<string> { swapId }
                };
                await SendWebSocketMessageAsync(connection, unsubscribeMsg, cancellationToken);
            }
        }
    }

    // Keep EnsureConnected for internal/listener use? Maybe remove?
    public void EnsureConnected(CancellationToken cancellationToken) { }

    private static Uri GetWebSocketUri(Uri apiUrl)
    {
        var scheme = apiUrl.Scheme == "https" ? "wss" : "ws";
        return new UriBuilder(scheme, apiUrl.Host, apiUrl.Port, "/v2/ws").Uri;
    }

    private async Task<WebSocketConnection?> EnsureConnectedAsync(Uri wsUri, CancellationToken cancellationToken)
    {
        if (_connections.TryGetValue(wsUri, out var existingConnection))
        {
            if (existingConnection.WebSocket.State == WebSocketState.Open || existingConnection.WebSocket.State == WebSocketState.Connecting)
                return existingConnection;

            _logger.LogWarning(
                $"Existing WebSocket connection for {wsUri} found in bad state: {existingConnection.WebSocket.State}. Attempting to clean up and reconnect.");
            await CleanupWebSocketAsync(wsUri, existingConnection);
            _connections.TryRemove(wsUri, out _); // Remove the old one
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); // Link to request token if needed?
        var webSocket = new ClientWebSocket();
        _logger.LogInformation($"Attempting to connect to Boltz WebSocket: {wsUri}");
        try
        {
            // Configure common headers if necessary (e.g., User-Agent)
            await webSocket.ConnectAsync(wsUri, cts.Token);
            _logger.LogInformation($"Connected to Boltz WebSocket: {wsUri}");

            var listenTask = Task.Run(() => ReceiveLoopAsync(webSocket, wsUri, cts.Token), cts.Token); // Don't await here
            var newConnection = new WebSocketConnection(webSocket, listenTask, cts);

            if (_connections.TryAdd(wsUri, newConnection))
            {
                StartPingTimer(); // Ensure ping timer is running if at least one connection is active
                return newConnection;
            }

            // Race condition? Another thread added it. Clean up this attempt.
            _logger.LogWarning($"Race condition: WebSocket connection for {wsUri} added concurrently. Cleaning up duplicate attempt.");
            await CleanupWebSocketAsync(wsUri, newConnection, false); // Don't remove from dictionary
            return _connections.TryGetValue(wsUri, out var actualConnection) ? actualConnection : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to Boltz WebSocket: {Uri}", wsUri);
            webSocket.Dispose();
            cts.Dispose();
            return null;
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, Uri wsUri, CancellationToken cancellationToken)
    {
        var buffer = new ArraySegment<byte>(new byte[4096]);
        try
        {
            while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                try
                {
                    var result = await ws.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation($"Boltz WebSocket connection closed by server: {wsUri}");
                        break;
                    }

                    if (buffer.Array != null)
                    {
                        var message = Encoding.UTF8.GetString(buffer.Array, 0, result.Count);
                        _logger.LogDebug($"Received message from Boltz WS ({wsUri}): {message}");
                        HandleWebSocketMessage(message);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation($"Boltz WebSocket listener cancelled: {wsUri}");
                    break;
                }
                catch (WebSocketException wsex) when (wsex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                {
                    _logger.LogWarning($"Boltz WebSocket connection closed prematurely: {wsUri}");
                    break; // Let cleanup handle reconnect attempt if necessary
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error receiving from Boltz WebSocket {wsUri}.");
                    break; // Let cleanup handle reconnect attempt if necessary
                }
        }
        finally
        {
            _logger.LogInformation($"Exited Boltz WebSocket receive loop for {wsUri}.");
            // Clean up the specific connection when the loop exits
            if (_connections.TryGetValue(wsUri, out var connection) && connection.WebSocket == ws)
            {
                await CleanupWebSocketAsync(wsUri, connection);
                _connections.TryRemove(wsUri, out _);
                StopPingTimerIfIdle();
                // TODO: Implement retry logic here if needed?
                // Maybe resubscribe active subscriptions?
            }
        }
    }

    private void HandleWebSocketMessage(string message)
    {
        try
        {
            var response = JsonSerializer.Deserialize<WebSocketResponse>(message, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (response?.Event == "update" && response.Args != null)
                foreach (var update in response.Args)
                    if (_subscriptions.TryGetValue(update.Id, out var subscription))
                    {
                        _logger.LogInformation($"Received status update for swap {update.Id}: {update.Status}");
                        // Don't block the receive loop
                        _ = Task.Run(async () => await subscription.Callback(update));
                    }
                    else
                    {
                        _logger.LogDebug($"Received update for unsubscribed swap {update.Id}");
                    }
            else if (response?.Event == "pong")
                _logger.LogDebug("Received pong from Boltz WS");
            else if (response?.Event == "subscribe")
                _logger.LogInformation(
                    $"Subscription confirmed by Boltz WS for IDs: {string.Join(',', response.Args?.Select(a => a.Id) ?? new List<string>())}");
            else if (response?.Event == "unsubscribe")
                _logger.LogInformation(
                    $"Unsubscription confirmed by Boltz WS. Remaining Args: {string.Join(',', response.Args?.Select(a => a.Id) ?? new List<string>())}");
            else if (response?.Event == "error") _logger.LogError($"Received error from Boltz WS: {message}");
        }
        catch (JsonException jex)
        {
            _logger.LogError(jex, $"Error deserializing Boltz WebSocket message: {message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling Boltz WebSocket message: {message}");
        }
    }

    private async Task SendWebSocketMessageAsync(WebSocketConnection connection, object messageObject, CancellationToken cancellationToken)
    {
        if (connection.WebSocket.State != WebSocketState.Open)
        {
            _logger.LogWarning($"Attempted to send WebSocket message while not connected or in invalid state ({connection.WebSocket.State}).");
            return; // Or throw?
        }

        try
        {
            var message = JsonSerializer.Serialize(messageObject);
            _logger.LogDebug($"Sending message to Boltz WS ({connection.WebSocket.SubProtocol}): {message}"); // SubProtocol might not be set
            var bytes = Encoding.UTF8.GetBytes(message);
            // Use the connection's CancellationTokenSource for sending, linking to the operation token?
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(connection.Cts.Token, cancellationToken);
            await connection.WebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, linkedCts.Token);
        }
        catch (Exception ex) when (ex is ObjectDisposedException || ex is WebSocketException || ex is OperationCanceledException)
        {
            _logger.LogWarning(ex, $"Failed to send message to Boltz WebSocket ({connection.WebSocket.CloseStatusDescription}), likely connection issue.");
            // Trigger cleanup for this specific connection
            await CleanupWebSocketAsync(_connections.FirstOrDefault(kv => kv.Value == connection).Key, connection); // Need to find Uri
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error sending message to Boltz WebSocket.");
            // Potentially trigger cleanup
            await CleanupWebSocketAsync(_connections.FirstOrDefault(kv => kv.Value == connection).Key, connection);
        }
    }

    private void StartPingTimer()
    {
        if (_pingTimer == null)
        {
            _logger.LogInformation("Starting Boltz WebSocket ping timer.");
            _pingTimer = new Timer(async _ => await PingConnectionsAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    private void StopPingTimerIfIdle()
    {
        if (_pingTimer != null && _connections.IsEmpty)
        {
            _logger.LogInformation("Stopping Boltz WebSocket ping timer as no connections are active.");
            _pingTimer.Dispose();
            _pingTimer = null;
        }
    }

    private async Task PingConnectionsAsync()
    {
        var pingMsg = new { op = "ping" };
        foreach (var kvp in _connections)
            if (kvp.Value.WebSocket.State == WebSocketState.Open)
            {
                _logger.LogDebug($"Pinging Boltz WebSocket: {kvp.Key}");
                // Use a short timeout for ping send
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await SendWebSocketMessageAsync(kvp.Value, pingMsg, cts.Token);
            }
    }

    private async Task CleanupWebSocketAsync(Uri? wsUri, WebSocketConnection connection, bool removeFromDictionary = true)
    {
        if (connection == null) return;

        // Signal cancellation to listening task and send operations
        if (!connection.Cts.IsCancellationRequested)
            try { connection.Cts.Cancel(); }
            catch (ObjectDisposedException)
            {
                /* Already disposed */
            }

        if (connection.WebSocket != null)
        {
            if (connection.WebSocket.State == WebSocketState.Open || connection.WebSocket.State == WebSocketState.CloseReceived)
            {
                _logger.LogDebug($"Closing Boltz WebSocket connection: {wsUri?.ToString() ?? "Unknown"}");
                try
                {
                    // Use a short timeout for closing
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await connection.WebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Service stopping", cts.Token);
                    // Wait for the receive loop to finish after closing output
                    // await connection.ListenTask; // This might deadlock if CloseOutputAsync hangs
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Exception during WebSocket close for {wsUri?.ToString() ?? "Unknown"}.");
                }
            }

            try { connection.WebSocket.Dispose(); }
            catch (ObjectDisposedException)
            {
                /* Already disposed */
            }
        }

        try { connection.Cts.Dispose(); }
        catch (ObjectDisposedException)
        {
            /* Already disposed */
        }

        if (removeFromDictionary && wsUri != null)
        {
            _connections.TryRemove(wsUri, out _);
            StopPingTimerIfIdle();
        }

        _logger.LogDebug($"Cleaned up WebSocket resources for {wsUri?.ToString() ?? "Unknown"}");
    }

    private record WebSocketConnection(ClientWebSocket WebSocket, Task ListenTask, CancellationTokenSource Cts);

    private record SwapSubscription(Uri ApiUrl, Func<SwapStatusUpdate, Task> Callback);
}
