using System;
using System.ComponentModel;

namespace SamRockProtocol.Models;

public class ImportWalletsViewModel
{
    public string StoreId { get; set; }

    [DisplayName("Bitcoin")]
    public bool Btc { get; set; }

    [DisplayName("Lightning")]
    public bool BtcLn { get; set; }

    [DisplayName("Liquid Bitcoin")]
    public bool Lbtc { get; set; }

    public string QrCode { get; set; }
    public string Otp { get; set; }
    public DateTimeOffset Expires { get; set; }
    public bool LiquidSupportedOnServer { get; set; }
}

public class ImportResultViewModel
{
    public bool? OtpStatus { get; set; }
    public string ErrorMessage { get; set; }
}
