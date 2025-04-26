#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

// This partial class contains methods from ILightningClient that are not 
// meaningfully implemented or supported by the Boltz reverse swap flow.
public partial class BoltzLightningClient
{
    // Required by ILightningClient - General purpose listener (Not Supported by this client)
    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellationToken = default)
    {
        _logger.LogError("General purpose listening (Listen without paymentHash) is not supported by BoltzLightningClient.");
        return Task.FromException<ILightningInvoiceListener>(
            new NotSupportedException("BoltzLightningClient does not support general purpose listening. Use Listen(paymentHash) after CreateInvoice."));
    }

    // Required by ILightningClient (Not Supported by this client)
    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellationToken = default)
    {
        _logger.LogError("GetDepositAddress is not supported by BoltzLightningClient.");
        return Task.FromException<BitcoinAddress>(new NotSupportedException("BoltzLightningClient does not provide a general deposit address."));
    }

    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("GetInfo is not meaningfully implemented for Boltz client.");
        // We could potentially fetch Boltz API status or pair info here, but it doesn't map directly
        // Returning a NotSupportedException might be more accurate.
        throw new NotSupportedException("GetInfo does not map directly to Boltz functionality.");
        // Or return a default/empty object if needed for UI compatibility:
        // return Task.FromResult(new LightningNodeInformation()); 
    }

    public Task<LightningNodeBalance> GetBalance(CancellationToken cancellationToken = default)
    {
        // Boltz doesn't hold a balance for the user in the same way a node does.
        _logger.LogDebug("GetBalance called on Boltz client - returning zero balance.");
        return Task.FromResult(new LightningNodeBalance());
    }

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellationToken = default)
    {
        _logger.LogError("Pay (SendPayment) is not supported via Boltz reverse swaps.");
        // Reverse swaps are for receiving Lightning payments, not sending.
        return Task.FromException<PayResponse>(new NotSupportedException("Cannot send payments using the Boltz reverse swap client."));
    }

    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellationToken = default)
    {
        _logger.LogError("Pay (SendPayment with params) is not supported via Boltz reverse swaps.");
        return Task.FromException<PayResponse>(new NotSupportedException("Cannot send payments using the Boltz reverse swap client."));
    }

    // Keep the parameter overload for interface compatibility, even if unimplemented
    public Task<PayResponse> Pay(string bolt11, PayInvoiceParams? payParams = null, CancellationToken cancellationToken = default)
    {
        _logger.LogError("Pay (SendPayment) is not supported via Boltz reverse swaps.");
        return Task.FromException<PayResponse>(new NotSupportedException("Cannot send payments using the Boltz reverse swap client."));
    }

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellationToken = default)
    {        
        _logger.LogWarning("OpenChannel is not applicable to the Boltz client.");
        return Task.FromException<OpenChannelResponse>(new NotSupportedException("OpenChannel is not applicable to the Boltz client."));
    }

    // Updated return type to match interface
    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("ConnectTo is not applicable to the Boltz client.");
        // We connect to the Boltz API/WebSocket, not arbitrary nodes.
        return Task.FromResult(ConnectionResult.Ok); // Indicate success even if it's a no-op?
        // Or throw NotSupportedException:
        // return Task.FromException<ConnectionResult>(new NotSupportedException("ConnectTo is not applicable to the Boltz client."));
    }

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ListChannels called on Boltz client - returning empty list.");
        // Boltz manages swaps, not persistent Lightning channels.
        return Task.FromResult(Array.Empty<LightningChannel>());
    }
    
    public Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning($"GetPayment({paymentHash}) not supported/implemented for Boltz client.");
        // Payments are typically outgoing, which we don't support here.
        return Task.FromResult<LightningPayment>(null!); // Or throw?
    }

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ListPayments called on Boltz client - returning empty list.");
        return Task.FromResult(Array.Empty<LightningPayment>());
    }

    public Task<LightningPayment[]> ListPayments(ListPaymentsParams? request, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ListPayments (with params) called on Boltz client - returning empty list.");
        return Task.FromResult(Array.Empty<LightningPayment>());
    }

    public Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }

    public Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = new CancellationToken())
    {
        throw new NotImplementedException();
    }
}
