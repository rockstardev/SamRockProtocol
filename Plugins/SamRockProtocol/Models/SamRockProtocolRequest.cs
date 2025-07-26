using System;
using System.Collections.Generic;

namespace SamRockProtocol.Models;

public class SamRockProtocolRequest
{
    public DescriptorModel BTC { get; set; }
    public DescriptorModel LBTC { get; set; }
    public LightningGenericModel BTCLN { get; set; }
}

public class DescriptorModel
{
    public string Descriptor { get; set; }
}

public class LightningGenericModel
{
    public string Type { get; set; }
    public object LBTC { get; set; }
}


