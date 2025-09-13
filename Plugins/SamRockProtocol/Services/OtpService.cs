using System;
using System.Linq;
using QRCoder;
using SamRockProtocol.Controllers;

namespace SamRockProtocol.Services;

public class OtpService
{
    private readonly SamRockProtocolHostedService _hostedService;

    public OtpService(SamRockProtocolHostedService hostedService)
    {
        _hostedService = hostedService;
    }

    public ImportWalletsViewModel CreateOtp(string storeId, bool btc, bool btcln, bool lbtc, string baseUrl, TimeSpan? ttl = null)
    {
        var expires = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromMinutes(5));
        var otp = OtpGenerator.Generate();
        var model = new ImportWalletsViewModel
        {
            StoreId = storeId,
            Btc = btc,
            BtcLn = btcln,
            Lbtc = lbtc,
            Expires = expires,
        };
        model.QrCode = BuildSetupUrl(model, otp, baseUrl);
        model.Otp = otp;
        _hostedService.Add(otp, model);
        return model;
    }

    public bool TryGet(string otp, out ImportWalletsViewModel model) => _hostedService.TryGet(otp, out model);

    public bool Remove(string otp)
    {
        _hostedService.Remove(otp);
        return true;
    }

    public SamRockProtocolHostedService.SamRockResult GetStatus(string otp) => _hostedService.OtpStatus(otp);

    public byte[] GenerateQrPng(string content, int pixelsPerModule = 10, QRCodeGenerator.ECCLevel eccLevel = QRCodeGenerator.ECCLevel.M)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(content, eccLevel);
        var pngQrCode = new PngByteQRCode(qrCodeData);
        return pngQrCode.GetGraphic(pixelsPerModule);
    }

    private static string BuildSetupUrl(ImportWalletsViewModel model, string otp, string baseUrl)
    {
        var setupParams = string.Join(
            ",",
            new[]
            {
                model.Btc ? "btc-chain" : null,
                model.Lbtc ? "liquid-chain" : null,
                model.BtcLn ? "btc-ln" : null
            }.Where(p => p != null));
        return $"{baseUrl}/plugins/{model.StoreId}/samrock/protocol?setup={Uri.EscapeDataString(setupParams)}&otp={Uri.EscapeDataString(otp)}";
    }
}
