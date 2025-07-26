using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace SamRockProtocol.Models;

public class SamRockProtocolRequest
{
    public string Version { get; set; }
    
    [JsonProperty("BTC")]
    public DescriptorModel BTC { get; set; }
    
    [JsonProperty("LBTC")]
    public DescriptorModel LBTC { get; set; }
    
    [JsonProperty("BTC-LN")]
    public LightningGenericModel BTCLN { get; set; }
    
    public static SamRockProtocolRequest Parse(string json, out Exception parsingException)
    {
        try
        {
            var model = JsonConvert.DeserializeObject<SamRockProtocolRequest>(json);
            parsingException = null;
            return model;
        }
        catch (Exception ex)
        {
            parsingException = ex;
            return null;
        }
    }
}

public class DescriptorModel
{
    [JsonProperty("Descriptor")]
    public string Descriptor { get; set; }
}

public class LightningGenericModel
{
    [JsonProperty("Type")]
    public string Type { get; set; }
    
    [JsonProperty("LBTC")]
    public DescriptorModel LBTC { get; set; }
}


