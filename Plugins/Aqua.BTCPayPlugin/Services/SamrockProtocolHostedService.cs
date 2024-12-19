using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aqua.BTCPayPlugin.Controllers;
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.Logging;

namespace Aqua.BTCPayPlugin.Services;

public class SamrockProtocolHostedService (
        EventAggregator eventAggregator,
        ILogger<PendingTransactionService> logger)
    : EventHostedServiceBase(eventAggregator, logger), IPeriodicTask
{
    private Dictionary<string, ImportWalletsViewModel> _samrockImportDictionary = new();
    
    public Task Do(CancellationToken cancellationToken)
    {
        PushEvent(new CheckForExpiryEvent());
        return Task.CompletedTask;
    }

    public class CheckForExpiryEvent { }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is CheckForExpiryEvent)
        {
            _samrockImportDictionary.Where(a => a.Value.Expires <= DateTimeOffset.UtcNow)
                .ToList()
                .ForEach(a => _samrockImportDictionary.Remove(a.Key));
        }
    }

    public void Add(string random21Charstring, ImportWalletsViewModel model)
    {
        _samrockImportDictionary.Add(random21Charstring, model);
    }

    public ImportWalletsViewModel Get(string storeId, string otp)
    {
        var import = _samrockImportDictionary[otp];
        if (import.StoreId != storeId)
        {
            return null;
        }

        return import;
    }
}