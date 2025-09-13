using System;
using System.Collections.Generic;

namespace SamRockProtocol.Models;

public class SamRockProtocolRequestLegacy
{
    public BtcChainSetupModel BtcChain { get; set; }
    public BtcLnSetupModel BtcLn { get; set; }
    public LiquidChainSetupModel LiquidChain { get; set; }

    public static string TypeToString(AddressTypes type)
    {
        switch (type)
        {
            case AddressTypes.P2TR:
                return "-[taproot]";
            case AddressTypes.P2WPKH:
                return "";
            case AddressTypes.P2SH_P2WPKH:
                return "-[p2sh]";
            case AddressTypes.P2PKH:
                return "-[legacy]";
            default:
                throw new InvalidOperationException();
        }
    }
}

public class BtcChainSetupModel
{
    public string Xpub { get; set; }
    public string Fingerprint { get; set; }
    public string DerivationPath { get; set; }
    public AddressTypes Type { get; set; }

    public override string ToString()
    {
        return $"{Xpub}{SamRockProtocolRequestLegacy.TypeToString(Type)}";
    }
}

public class LiquidChainSetupModel
{
    public string Xpub { get; set; }
    public string Fingerprint { get; set; }
    public string DerivationPath { get; set; }
    public AddressTypes Type { get; set; }
    public string BlindingKey { get; set; }

    public override string ToString()
    {
        return $"{Xpub}{SamRockProtocolRequestLegacy.TypeToString(Type)}-[slip77={BlindingKey}]";
    }
}

public class BtcLnSetupModel
{
    public bool UseLiquidBoltz { get; set; }
    public string[] LiquidAddresses { get; set; }
    public string CtDescriptor { get; set; }
}

public enum AddressTypes
{
    P2PKH,
    P2SH_P2WPKH,
    P2WPKH,
    P2TR
}

public class SamRockProtocolSetupResponse
{
    public Dictionary<SamRockProtocolKeys, SamRockProtocolResponse> Results { get; init; } = new();
}

public enum SamRockProtocolKeys
{
    BTC,
    BTC_LN,
    LBTC
}
