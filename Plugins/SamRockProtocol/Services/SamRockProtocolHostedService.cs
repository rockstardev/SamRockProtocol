using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.Logging;
using NicolasDorier.RateLimits;
using SamRockProtocol.Models;

namespace SamRockProtocol.Services;

public class SamRockProtocolHostedService(
    EventAggregator eventAggregator,
    ILogger<PendingTransactionService> logger,
    RateLimitService rateLimitService)
    : EventHostedServiceBase(eventAggregator, logger), IPeriodicTask
{
    private readonly Dictionary<string, ImportWalletsViewModel> _samrockImportDictionary = new();
    private readonly Dictionary<string, SamRockResult> _samrockResults = new();

    private bool _rateLimitsConfigured;

    public Task Do(CancellationToken cancellationToken)
    {
        if (!_rateLimitsConfigured)
        {
            rateLimitService.SetZone("zone=SamRockProtocol rate=12r/min burst=3 nodelay");
            _rateLimitsConfigured = true;
        }

        PushEvent(new CheckForExpiryEvent());
        return Task.CompletedTask;
    }

    protected override Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is CheckForExpiryEvent)
        {
            _samrockImportDictionary
                .Where(a => a.Value.Expires <= DateTimeOffset.UtcNow)
                .ToList()
                .ForEach(a => _samrockImportDictionary.Remove(a.Key));

            _samrockResults
                .Where(a => a.Value.Expires <= DateTimeOffset.UtcNow)
                .ToList()
                .ForEach(a => _samrockResults.Remove(a.Key));
        }

        return Task.CompletedTask;
    }

    public void Add(string random21Charstring, ImportWalletsViewModel model)
    {
        _samrockImportDictionary.Add(random21Charstring, model);
    }

    public void Remove(string random21Charstring)
    {
        _samrockImportDictionary.Remove(random21Charstring);
    }

    public bool TryGet(string otp, out ImportWalletsViewModel model)
    {
        if (_samrockImportDictionary.TryGetValue(otp, out var value))
        {
            model = value;
            return true;
        }

        model = null;
        return false;
    }

    public void OtpUsed(string otp, bool importSuccessful, string errorMessage = null)
    {
        if (_samrockImportDictionary.Remove(otp, out var value))
            _samrockResults.Add(otp, new SamRockResult { ImportSuccessful = importSuccessful, ErrorMessage = errorMessage, Expires = value.Expires, StoreId = value.StoreId });
    }

    public SamRockResult OtpStatus(string otp)
    {
        if (_samrockResults.TryGetValue(otp, out var value)) 
            return value;

        return null;
    }

    public class SamRockResult
    {
        public bool ImportSuccessful { get; set; }
        public string ErrorMessage { get; set; }
        public DateTimeOffset Expires { get; set; }
        public string StoreId { get; set; }
    }

    public class CheckForExpiryEvent
    {
    }
}
