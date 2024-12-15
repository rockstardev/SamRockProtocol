using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Aqua.BTCPayPlugin.Services;

public class MyPluginService
{
    public MyPluginService()
    {
    }

    public Task AddTestDataRecord()
    {
        return Task.CompletedTask;
        //
    }

    public Task<string> Get()
    {
        return Task.FromResult<string>("hello world");
    }
}

