using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.Logging;
using NicolasDorier.RateLimits;
using SamRockProtocol.Controllers;

namespace SamRockProtocol.Services;

public class SamrockProtocolHostedService(
    EventAggregator eventAggregator,
    ILogger<PendingTransactionService> logger,
    RateLimitService rateLimitService)
    : EventHostedServiceBase(eventAggregator, logger), IPeriodicTask
{
    private readonly Dictionary<string, ImportWalletsViewModel> _samrockImportDictionary = new();
    private readonly Dictionary<string, SamrockResult> _samrockResults = new();

    private bool _rateLimitsConfigured;

    public Task Do(CancellationToken cancellationToken)
    {
        if (!_rateLimitsConfigured)
        {
            rateLimitService.SetZone("zone=SamrockProtocol rate=5r/min burst=3 nodelay");
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

    public void OtpUsed(string otp, bool importSuccessful)
    {
        if (_samrockImportDictionary.Remove(otp, out var value))
            _samrockResults.Add(otp, new SamrockResult { ImportSuccessful = importSuccessful, Expires = value.Expires });
    }

    public bool? OtpStatus(string otp)
    {
        if (_samrockResults.TryGetValue(otp, out var value)) return value.ImportSuccessful;

        return null;
    }

    private class SamrockResult
    {
        public bool ImportSuccessful { get; set; }
        public DateTimeOffset Expires { get; set; }
    }

    public class CheckForExpiryEvent
    {
    }
}
