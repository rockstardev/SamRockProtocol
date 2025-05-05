#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

/// <summary>
///     Manages WebSocket connections to the Boltz API for real-time swap updates.
///     Handles message fragmentation, subscriptions, and dispatching updates.
/// </summary>
public class BoltzWebSocketService : IHostedService, IAsyncDisposable
{
    // Connection management: Key = WebSocket URI
    private readonly ConcurrentDictionary<Uri, WebSocketConnection> _connections = new();
    private readonly ILogger<BoltzWebSocketService> _logger;
    private readonly TimeSpan _pingInterval = TimeSpan.FromSeconds(30);
    private readonly CancellationTokenSource _stoppingCts = new(); // Overall service cancellation

    // NEW: Map Swap ID to the URI it was subscribed on
    private readonly ConcurrentDictionary<string, Uri> _swapIdToUriMap = new();

    // Subscription management: Key = Swap ID, Value = Callback delegate
    private readonly ConcurrentDictionary<string, Func<SwapStatusUpdate, Task>> _swapSubscriptions = new();

    // Timer for sending keep-alive pings
    private Timer? _pingTimer;

    public BoltzWebSocketService(ILogger<BoltzWebSocketService> logger)
    {
        _logger = logger;
    }

    public async ValueTask DisposeAsync()
    {
        _pingTimer?.Dispose();
        _stoppingCts.Cancel();
        _stoppingCts.Dispose();

        var cleanupTasks = _connections.Values.ToList().Select(conn =>
        {
            conn.Cts.Cancel(); // Ensure listener stops
            conn.WebSocket.Dispose();
            conn.Cts.Dispose();
            return conn.ListenTask; // Await the listening task completion if needed
        });

        try
        {
            await Task.WhenAll(cleanupTasks);
        }
        catch (OperationCanceledException)
        {
            // Expected if tasks were cancelled
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during WebSocket connection disposal.");
        }

        _connections.Clear();
        _swapSubscriptions.Clear();
        _swapIdToUriMap.Clear();
        GC.SuppressFinalize(this);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Boltz WebSocket Service starting.");
        // Combine external cancellation with the service's stopping token
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingCts.Token);
        StartPingTimer(linkedCts.Token); // Pass linked token
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Boltz WebSocket Service stopping.");

        _stoppingCts.Cancel(); // Signal cancellation to all operations
        _pingTimer?.Dispose();

        var cleanupTasks = _connections.Keys.ToList().Select(uri => CleanupConnectionAsync(uri, false));
        await Task.WhenAll(cleanupTasks);

        _connections.Clear();
        _swapSubscriptions.Clear();
        _swapIdToUriMap.Clear();

        _logger.LogInformation("Boltz WebSocket Service stopped.");
    }

    /// <summary>
    ///     Subscribes to status updates for a specific swap ID.
    ///     Ensures a WebSocket connection is established to the necessary endpoint.
    /// </summary>
    public async Task SubscribeToSwapStatusAsync(Uri wsApiUri, string swapId, Func<SwapStatusUpdate, Task> onUpdateCallback,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(swapId))
            throw new ArgumentNullException(nameof(swapId));
        if (onUpdateCallback == null)
            throw new ArgumentNullException(nameof(onUpdateCallback));

        // Add or update subscription callback
        _swapSubscriptions[swapId] = onUpdateCallback;

        // Ensure connection exists and subscribe
        var connection = await EnsureConnectedAsync(wsApiUri, cancellationToken);
        if (connection != null)
        {
            await SendSubscriptionMessageAsync(connection, new[] { swapId }, "subscribe", cancellationToken);
            _logger.LogInformation($"Subscribed to swap updates for {swapId} on {wsApiUri}");

            // *** ADD MAPPING HERE ***
            _swapIdToUriMap[swapId] = wsApiUri;
            _logger.LogDebug($"Mapped swap {swapId} to URI {wsApiUri}");
        }
        else
        {
            _logger.LogError($"Failed to establish connection to {wsApiUri} for swap {swapId}. Subscription might not be active.");
            // Optionally remove subscription here or let it retry on next event?
            // _swapSubscriptions.TryRemove(swapId, out _);
        }
    }

    /// <summary>
    ///     Unsubscribes from status updates for a specific swap ID.
    /// </summary>
    public async Task UnsubscribeFromSwapStatusAsync(string swapId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation($"Attempting to unsubscribe from swap ID: {swapId}");

        // *** USE MAPPING TO FIND URI ***
        if (!_swapIdToUriMap.TryGetValue(swapId, out var wsApiUri))
        {
            _logger.LogWarning($"Cannot unsubscribe swap {swapId}: No URI mapping found. Was it ever subscribed?");
            // Still remove from _swapSubscriptions just in case mapping failed but subscription exists
            _swapSubscriptions.TryRemove(swapId, out _);
            return;
        }

        // Remove the callback first to prevent race conditions if messages arrive during unsubscribe
        var removed = _swapSubscriptions.TryRemove(swapId, out _);
        _swapIdToUriMap.TryRemove(swapId, out _); // Remove mapping regardless of subscription success

        if (!removed)
        {
            _logger.LogWarning($"Swap {swapId} was not found in the active subscriptions list during unsubscribe.");
            // Don't send unsubscribe message if we weren't actually tracking a callback
            return;
        }

        if (_connections.TryGetValue(wsApiUri, out var connection) && connection.WebSocket.State == WebSocketState.Open)
            await SendSubscriptionMessageAsync(connection, new[] { swapId }, "unsubscribe", cancellationToken);

        // Consider cleanup if no more subscriptions for this URI?
        // CheckIfConnectionNeeded(wsApiUri);
    }

    // Ensures a WebSocket connection is established and listening.
    private async Task<WebSocketConnection?> EnsureConnectedAsync(Uri wsUri, CancellationToken cancellationToken)
    {
        // Use combined token for connection attempt
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingCts.Token);
        var combinedToken = linkedCts.Token;

        if (_connections.TryGetValue(wsUri, out var existingConnection))
        {
            if (existingConnection.WebSocket.State == WebSocketState.Open || existingConnection.WebSocket.State == WebSocketState.Connecting)
                return existingConnection;

            _logger.LogWarning(
                $"Existing connection to {wsUri} found in state {existingConnection.WebSocket.State}. Attempting to clean up and reconnect.");
            await CleanupConnectionAsync(wsUri);
        }

        _logger.LogInformation($"Attempting to connect to Boltz WebSocket: {wsUri}");
        var webSocket = new ClientWebSocket();
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(60); // Configure keep-alive

        try
        {
            await webSocket.ConnectAsync(wsUri, combinedToken);
            _logger.LogInformation($"Connected to Boltz WebSocket: {wsUri}");

            var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingCts.Token); // Link new CTS to overall stopping token
            Task listenTask = null!; // Declare beforehand
            var newConnection = new WebSocketConnection(webSocket, null!, connectionCts); // Temp assign Task

            // Assign the listen task *after* the connection object is created
            // Pass the connection object itself to the listener loop
            listenTask = Task.Run(() => ReceiveLoopAsync(newConnection, wsUri), connectionCts.Token);

            // Update the connection record with the actual listen task
            newConnection = newConnection with { ListenTask = listenTask };

            if (_connections.TryAdd(wsUri, newConnection))
            {
                _logger.LogInformation($"WebSocket connection added for {wsUri}.");
                StartPingTimer(combinedToken); // Ensure ping timer is running
                return newConnection;
            }

            _logger.LogWarning($"Failed to add new connection for {wsUri} (already added?). Cleaning up.");
            await CleanupConnectionAsync(wsUri, newConnection, false); // Cleanup without removing from dict as it wasn't added
            return _connections.TryGetValue(wsUri, out var raceConditionWinner) ? raceConditionWinner : null; // Return the one that won the race
        }
        catch (OperationCanceledException) when (combinedToken.IsCancellationRequested)
        {
            _logger.LogWarning($"Connection attempt to {wsUri} cancelled.");
            webSocket.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to connect to Boltz WebSocket: {wsUri}");
            webSocket.Dispose();
            return null;
        }
    }

    // The core loop receiving messages for a single connection.
    private async Task ReceiveLoopAsync(WebSocketConnection connection, Uri wsUri)
    {
        var buffer = new byte[8192]; // Increased buffer size
        var connectionId = connection.WebSocket.GetHashCode(); // For logging
        var receiveCts = connection.Cts; // Use the CancellationTokenSource specific to this connection

        _logger.LogDebug($"ReceiveLoopAsync started for {wsUri} (ID: {connectionId}).");

        try
        {
            while (connection.WebSocket.State == WebSocketState.Open && !receiveCts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                try
                {
                    do
                    {
                        var segment = new ArraySegment<byte>(buffer);
                        result = await connection.WebSocket.ReceiveAsync(segment, receiveCts.Token);

                        if (receiveCts.Token.IsCancellationRequested) break;

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogInformation(
                                $"WS {wsUri} (ID: {connectionId}) received close: {result.CloseStatus?.ToString()} / {result.CloseStatusDescription}");
                            // Don't cleanup here, let the outer loop detect state change or cancellation
                            return; // Exit loop gracefully
                        }

                        if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            _logger.LogWarning($"WS {wsUri} (ID: {connectionId}) received unexpected binary message. Draining.");
                            while (!result.EndOfMessage && !receiveCts.Token.IsCancellationRequested)
                            {
                                result = await connection.WebSocket.ReceiveAsync(segment, receiveCts.Token);
                                if (result.MessageType == WebSocketMessageType.Close) goto CloseReceivedInBinaryDrain; // Handle close during drain
                            }

                            goto NextMessageFragmentLoop;
                        }

                        if (result.Count > 0) await ms.WriteAsync(buffer, 0, result.Count, receiveCts.Token);
                    } while (!result.EndOfMessage && !receiveCts.Token.IsCancellationRequested);

                    if (receiveCts.Token.IsCancellationRequested) break; // Check again after loop

                    // Process the complete message if we received text data
                    if (ms.Length > 0 && result.MessageType == WebSocketMessageType.Text) // Check type just in case
                    {
                        ms.Position = 0;
                        using var reader = new StreamReader(ms, Encoding.UTF8);
                        var completeMessage = await reader.ReadToEndAsync(receiveCts.Token);

                        // Handle message processing asynchronously without blocking the receive loop
                        _ = Task.Run(() => HandleWebSocketMessageAsync(completeMessage), receiveCts.Token);
                    }

                    CloseReceivedInBinaryDrain:
                    if (result.MessageType == WebSocketMessageType.Close) // Check if we exited drain loop due to Close
                    {
                        _logger.LogInformation(
                            $"WS {wsUri} (ID: {connectionId}) received close during binary drain: {result.CloseStatus?.ToString()} / {result.CloseStatusDescription}");
                        return; // Exit loop gracefully
                    }

                    NextMessageFragmentLoop: ;
                }
                catch (OperationCanceledException) when (receiveCts.IsCancellationRequested)
                {
                    _logger.LogDebug($"Receive operation cancelled for {wsUri} (ID: {connectionId}).");
                    break; // Exit main while loop
                }
                catch (WebSocketException wsex) when (wsex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                {
                    _logger.LogWarning(wsex, $"WebSocket connection closed prematurely for {wsUri} (ID: {connectionId}).");
                    break; // Exit main while loop, let cleanup handle
                }
                catch (WebSocketException wsex)
                {
                    _logger.LogError(wsex, $"WebSocketException in ReceiveLoopAsync for {wsUri} (ID: {connectionId}).");
                    break; // Exit main while loop, let cleanup handle
                }
                catch (Exception ex)
                {
                    if (ex is ObjectDisposedException ode && ode.ObjectName == typeof(ClientWebSocket).FullName)
                        _logger.LogWarning($"WebSocket was disposed during ReceiveLoopAsync for {wsUri} (ID: {connectionId}). Likely closed externally.");
                    else
                        _logger.LogError(ex, $"Unexpected error in ReceiveLoopAsync for {wsUri} (ID: {connectionId}).");

                    break; // Exit main while loop, let cleanup handle
                }
                // MemoryStream 'ms' disposed by using statement here
            }
        }
        finally
        {
            _logger.LogDebug($"ReceiveLoopAsync finished for {wsUri} (ID: {connectionId}). State: {connection.WebSocket.State}");
            // Ensure connection is cleaned up if the loop exits unexpectedly or normally after close/cancel
            await CleanupConnectionAsync(wsUri, connection);
        }
    }

    // Parses the message and dispatches swap updates to subscribers.
    private async Task HandleWebSocketMessageAsync(string message)
    {
        try
        {
            using var jsonDoc = JsonDocument.Parse(message);
            var root = jsonDoc.RootElement;

            if (!root.TryGetProperty("event", out var eventElement) || eventElement.ValueKind != JsonValueKind.String)
                return; // Ignore messages without a string event property

            var eventType = eventElement.GetString();

            switch (eventType)
            {
                case "update":
                    if (root.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array && argsElement.GetArrayLength() > 0)
                    {
                        // Deserialize the first argument which should be the SwapStatusUpdate
                        var updateData = argsElement[0].Deserialize<SwapStatusUpdate>();
                        if (updateData?.Id != null)
                        {
                            if (_swapSubscriptions.TryGetValue(updateData.Id, out var callback))
                                try
                                {
                                    await callback(updateData);
                                }
                                catch (Exception callbackEx)
                                {
                                    _logger.LogError(callbackEx, $"Error executing callback for swap update {updateData.Id}");
                                }
                            // else: No active subscription for this swap ID, just ignore.
                        }
                        else
                        {
                            _logger.LogWarning($"Received swap update message with missing/invalid data: {message}");
                        }
                    }

                    break;

                case "pong":
                    _logger.LogDebug("Received pong from Boltz WS.");
                    break;

                case "subscribe":
                case "unsubscribe":
                    // Confirmation messages - log for diagnostics
                    var args = root.TryGetProperty("args", out var argsArr) ? string.Join(',', argsArr.EnumerateArray().Select(a => a.ToString())) : "<none>";
                    _logger.LogInformation($"Received '{eventType}' confirmation from Boltz WS. Args: [{args}]");
                    break;

                case "error":
                    _logger.LogError($"Received error message from Boltz WS: {message}");
                    break;

                default:
                    _logger.LogWarning($"Received unhandled Boltz WS event type '{eventType}': {message}");
                    break;
            }
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

    // Sends a message (subscribe/unsubscribe) to the WebSocket.
    private async Task SendSubscriptionMessageAsync(WebSocketConnection connection, IEnumerable<string> swapIds, string operation,
        CancellationToken cancellationToken)
    {
        var message = new
        {
            op = operation,
            channel = "swap.update",
            args = swapIds
        };
        await SendWebSocketMessageInternalAsync(connection, message, cancellationToken);
    }

    // Sends a ping message.
    private async Task SendPingAsync(WebSocketConnection connection, CancellationToken cancellationToken)
    {
        var message = new { op = "ping" };
        await SendWebSocketMessageInternalAsync(connection, message, cancellationToken);
    }

    // Internal method to serialize and send any message object.
    private async Task SendWebSocketMessageInternalAsync(WebSocketConnection connection, object messageObject, CancellationToken cancellationToken)
    {
        if (connection.WebSocket.State != WebSocketState.Open)
        {
            _logger.LogWarning(
                $"Attempted to send WebSocket message while not connected or in invalid state ({connection.WebSocket.State}). Message: {JsonSerializer.Serialize(messageObject)}");
            return;
        }

        try
        {
            var messageJson = JsonSerializer.Serialize(messageObject);
            var messageBytes = Encoding.UTF8.GetBytes(messageJson);
            var segment = new ArraySegment<byte>(messageBytes);

            // Use combined token for send operation
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Cts.Token, _stoppingCts.Token);

            _logger.LogDebug($"Sending WS message: {messageJson}");
            await connection.WebSocket.SendAsync(segment, WebSocketMessageType.Text, true, linkedCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || connection.Cts.IsCancellationRequested ||
                                                 _stoppingCts.IsCancellationRequested)
        {
            _logger.LogWarning("WebSocket send operation cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error sending WebSocket message: {JsonSerializer.Serialize(messageObject)}");
            // Consider cleaning up connection on send failure?
            // await CleanupConnectionAsync(GetUriForConnection(connection), connection);
        }
    }

    // Cleans up a single WebSocket connection.
    private async Task CleanupConnectionAsync(Uri wsUri, bool removeFromDictionary = true)
    {
        if (_connections.TryGetValue(wsUri, out var connection)) await CleanupConnectionAsync(wsUri, connection, removeFromDictionary);
    }

    private async Task CleanupConnectionAsync(Uri wsUri, WebSocketConnection connection, bool removeFromDictionary = true)
    {
        if (connection == null) return;

        if (removeFromDictionary)
        {
            _connections.TryRemove(wsUri, out _);
            _logger.LogDebug($"Removed connection entry for {wsUri}.");
        }

        // Signal cancellation *before* closing
        if (!connection.Cts.IsCancellationRequested)
            try { connection.Cts.Cancel(); }
            catch (ObjectDisposedException)
            {
                /* Already disposed */
            }

        // Close the WebSocket gracefully if possible
        if (connection.WebSocket.State == WebSocketState.Open || connection.WebSocket.State == WebSocketState.CloseReceived)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // Timeout for close
                await connection.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing connection", cts.Token);
                _logger.LogDebug($"WebSocket closed gracefully for {wsUri}.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"WebSocket close timed out for {wsUri}. Aborting.");
                connection.WebSocket.Abort();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception during WebSocket graceful close for {wsUri}. Aborting.");
                connection.WebSocket.Abort(); // Ensure closed on error
            }
        }
        else if (connection.WebSocket.State != WebSocketState.Closed) // If not open/closeReceived and not already closed, Abort
        {
            _logger.LogWarning($"WebSocket state for {wsUri} is {connection.WebSocket.State}. Aborting connection.");
            connection.WebSocket.Abort();
        }

        // Dispose resources
        connection.WebSocket.Dispose();
        try { connection.Cts.Dispose(); }
        catch (ObjectDisposedException)
        {
            /* Already disposed */
        }

        // Ensure listener task completes (optional, might already be done due to cancellation/close)
        try
        {
            if (connection.ListenTask != null && !connection.ListenTask.IsCompleted)
                await connection.ListenTask.WaitAsync(TimeSpan.FromSeconds(2)); // Wait briefly
        }
        catch (TimeoutException)
        {
            _logger.LogWarning($"Listener task for {wsUri} did not complete quickly after cleanup signal.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error waiting for listener task completion for {wsUri}");
        }

        StopPingTimerIfIdle();
        _logger.LogInformation($"Cleaned up connection for {wsUri}.");
    }

    // Starts the ping timer if not already running and connections exist.
    private void StartPingTimer(CancellationToken cancellationToken)
    {
        if (_pingTimer == null && !_connections.IsEmpty)
        {
            _logger.LogInformation("Starting Boltz WebSocket ping timer.");
            _pingTimer = new Timer(async _ => await PingConnectionsAsync(cancellationToken), null, _pingInterval, _pingInterval);
        }
    }

    // Stops the ping timer if running and no connections remain.
    private void StopPingTimerIfIdle()
    {
        if (_pingTimer != null && _connections.IsEmpty)
        {
            _logger.LogInformation("Stopping Boltz WebSocket ping timer as no connections are active.");
            _pingTimer.Dispose();
            _pingTimer = null;
        }
    }

    // Sends a ping to all active connections.
    private async Task PingConnectionsAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        var activeConnections = _connections.ToList(); // Copy to avoid modification during iteration
        _logger.LogDebug($"Pinging {activeConnections.Count} active Boltz WebSocket connections.");

        foreach (var kvp in activeConnections)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (kvp.Value.WebSocket.State == WebSocketState.Open)
            {
                try
                {
                    // Use a short timeout specific to the ping send operation
                    using var pingTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    pingTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await SendPingAsync(kvp.Value, pingTimeoutCts.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug("Ping loop cancelled.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error sending ping to {kvp.Key}");
                    // Optionally cleanup connection if ping fails repeatedly?
                }
            }
            else
            {
                _logger.LogWarning($"Connection to {kvp.Key} is not open ({kvp.Value.WebSocket.State}) during ping. Cleaning up.");
                await CleanupConnectionAsync(kvp.Key, kvp.Value); // Cleanup stale connection found during ping
            }
        }
    }

    // Record to hold connection state
    private record WebSocketConnection(ClientWebSocket WebSocket, Task ListenTask, CancellationTokenSource Cts);

    // Helper to find URI - consider removing if only used in cleanup
    // private Uri? GetUriForConnection(WebSocketConnection connection)
    // {
    //     if (connection == null) return null;
    //     return _connections.FirstOrDefault(kv => kv.Value == connection).Key;
    // }
}
