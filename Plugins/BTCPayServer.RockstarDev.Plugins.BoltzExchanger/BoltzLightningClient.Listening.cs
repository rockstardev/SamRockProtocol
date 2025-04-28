#nullable enable
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Events;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

// Simplified Event: Just carries the ID of the paid swap
public class BoltzSwapPaidEvent
{
    public string SwapId { get; }

    public BoltzSwapPaidEvent(string swapId)
    {
        SwapId = swapId;
    }
}

public partial class BoltzLightningClient
{
    // Stores listeners waiting for payment signals via the channel
    // Key: Listener instance hash code (or a unique ID), Value: Listener instance
    // Needed if we want HandleSwapUpdate to interact with listeners directly (e.g., for cleanup)
    // Alternatively, rely purely on the Channel mechanism as per Strike example.
    // Let's stick closer to the Strike example for now.

    // As per Strike example: Simple Listen method returns one listener instance.
    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        // Assumes _eventAggregator and _logger are available
        var listener = new BoltzListener(_boltzExchangerService, _logger);
        _logger.LogDebug("Created new BoltzListener instance.");
        return Task.FromResult<ILightningInvoiceListener>(listener);
    }

    // Internal listener implementation
    private class BoltzListener : ILightningInvoiceListener
    {
        private readonly BoltzExchangerService _service;
        private readonly ILogger _logger;
        private readonly IEventAggregatorSubscription _subscription;
        // Channel to queue IDs of paid swaps received via events
        private readonly Channel<string> _paidSwapIdChannel = Channel.CreateUnbounded<string>();
        private bool _disposed = false;

        public BoltzListener(BoltzExchangerService service, ILogger logger)
        {
            _service = service;
            _logger = logger;

            // Subscribe to the simplified paid event
            _subscription = _service.EventAggregator.Subscribe<BoltzSwapPaidEvent>(HandlePaidEvent);
            _logger.LogDebug("BoltzListener subscribed to BoltzSwapPaidEvent.");
        }

        // Event handler: Pushes the paid swap ID into the channel
        private void HandlePaidEvent(BoltzSwapPaidEvent paidEvent)
        {
            _logger.LogDebug($"BoltzListener received BoltzSwapPaidEvent for SwapId: {paidEvent.SwapId}");
            // TryWrite should always succeed for an unbounded channel unless it's completed
            if (!_paidSwapIdChannel.Writer.TryWrite(paidEvent.SwapId))
            {
                _logger.LogWarning($"Failed to write paid SwapId {paidEvent.SwapId} to listener channel (Channel closed?)");
            }
            else
            {
                 _logger.LogTrace($"Successfully wrote paid SwapId {paidEvent.SwapId} to listener channel.");
            }
        }

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
             _logger.LogInformation("BoltzListener waiting for next paid invoice...");
            if (_disposed)
            {
                 _logger.LogWarning("WaitInvoice called on disposed BoltzListener.");
                throw new ObjectDisposedException(nameof(BoltzListener));
            }

            try
            {
                // Wait indefinitely until a swap ID is available in the channel or cancellation occurs
                var paidSwapId = await _paidSwapIdChannel.Reader.ReadAsync(cancellation);
                 _logger.LogInformation($"BoltzListener received paid SwapId {paidSwapId} from channel.");

                // Now retrieve the actual invoice data from the client's cache
                if (_service.TryGetPaidInvoice(paidSwapId, out var swapDetails) && swapDetails.IsPaid)
                {
                    _logger.LogInformation($"Found paid invoice details for SwapId {paidSwapId}. Returning invoice.");
                    return swapDetails.OriginalInvoice; // Return the fully populated invoice
                }
                else
                {
                    // This case should be rare if HandleSwapUpdate correctly marks IsPaid and publishes
                    // before the event is processed here. But handle defensively.
                    _logger.LogError($"Listener received paid SwapId {paidSwapId} from channel, but couldn't find valid paid swap details in client cache!");
                    // Throw or loop again? Throwing is probably safer to indicate an inconsistent state.
                    throw new InvalidOperationException($"Inconsistent state: Paid swap event received for {paidSwapId}, but data not found or not marked paid.");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("WaitInvoice cancelled.");
                throw;
            }
            catch (ChannelClosedException)
            {
                 _logger.LogWarning("WaitInvoice failed: Listener channel was closed.");
                 // This happens if Dispose was called concurrently.
                 throw new ObjectDisposedException(nameof(BoltzListener), "Channel was closed during WaitInvoice.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error during WaitInvoice: {ex.Message}");
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _logger.LogDebug("Disposing BoltzListener.");
            _subscription?.Dispose(); // Unsubscribe from EventAggregator
            _paidSwapIdChannel.Writer.TryComplete(); // Complete the channel
        }
    }
}
