using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using SamRockProtocol.Services;
using NicolasDorier.RateLimits;

namespace SamRockProtocol.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public class GreenfieldSamrockController : ControllerBase
{
    private readonly OtpService _otpService;

    public GreenfieldSamrockController(OtpService otpService)
    {
        _otpService = otpService;
    }

    [HttpPost("~/api/v1/stores/{storeId}/samrock/otps")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    [RateLimitsFilter("SamRockProtocol", Scope = RateLimitsScope.RemoteAddress)]
    public IActionResult CreateOtp(string storeId, [FromBody] CreateOtpRequest request)
    {
        request ??= new CreateOtpRequest();
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        var ttl = request.ExpiresInSeconds.HasValue ? TimeSpan.FromSeconds(Math.Max(1, request.ExpiresInSeconds.Value)) : (TimeSpan?)null;
        var model = _otpService.CreateOtp(storeId, request.Btc, request.Btcln, request.Lbtc, baseUrl, ttl);
        var resp = new CreateOtpResponse
        {
            Otp = model.Otp,
            ExpiresAt = model.Expires,
            SetupUrl = model.QrCode
        };
        return Created($"~/api/v1/stores/{storeId}/samrock/otps/{resp.Otp}", resp);
    }

    [HttpGet("~/api/v1/stores/{storeId}/samrock/otps/{otp}")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public IActionResult GetOtp(string storeId, string otp)
    {
        // Pending?
        if (_otpService.TryGet(otp, out var model))
        {
            if (!string.Equals(model.StoreId, storeId, StringComparison.Ordinal))
                return this.CreateAPIError(404, "otp-not-found", "The OTP was not found for this store");
            return Ok(new OtpStatusResponse
            {
                Otp = model.Otp,
                ExpiresAt = model.Expires,
                SetupUrl = model.QrCode,
                Status = "pending"
            });
        }
        // Completed or error?
        var status = _otpService.GetStatus(otp);
        if (status is not null)
        {
            if (!string.Equals(status.StoreId, storeId, StringComparison.Ordinal))
                return this.CreateAPIError(404, "otp-not-found", "The OTP was not found for this store");
            return Ok(new OtpStatusResponse
            {
                Otp = otp,
                ExpiresAt = status.Expires,
                Status = status.ImportSuccessful ? "success" : "error",
                ErrorMessage = status.ErrorMessage
            });
        }
        return this.CreateAPIError(404, "otp-not-found", "The OTP does not exist or has expired");
    }

    [HttpGet("~/api/v1/stores/{storeId}/samrock/otps/{otp}/qr")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public IActionResult GetOtpQr(string storeId, string otp)
    {
        if (_otpService.TryGet(otp, out var model))
        {
            if (!string.Equals(model.StoreId, storeId, StringComparison.Ordinal))
                return this.CreateAPIError(404, "otp-not-found", "The OTP was not found for this store");
            var png = _otpService.GenerateQrPng(model.QrCode);
            return File(png, "image/png");
        }
        return this.CreateAPIError(404, "otp-not-found", "The OTP does not exist or has expired");
    }

    [HttpDelete("~/api/v1/stores/{storeId}/samrock/otps/{otp}")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public IActionResult DeleteOtp(string storeId, string otp)
    {
        if (_otpService.TryGet(otp, out var model))
        {
            if (!string.Equals(model.StoreId, storeId, StringComparison.Ordinal))
                return this.CreateAPIError(404, "otp-not-found", "The OTP was not found for this store");
            _otpService.Remove(otp);
            return Ok();
        }
        // If it was already consumed, we can consider it deleted
        if (_otpService.GetStatus(otp) is { } status && string.Equals(status.StoreId, storeId, StringComparison.Ordinal))
            return Ok();
        return this.CreateAPIError(404, "otp-not-found", "The OTP does not exist or has expired");
    }

    public class CreateOtpRequest
    {
        public bool Btc { get; set; } = true;
        public bool Btcln { get; set; } = true;
        public bool Lbtc { get; set; } = false;
        public int? ExpiresInSeconds { get; set; }
    }

    public class CreateOtpResponse
    {
        public string Otp { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string SetupUrl { get; set; }
    }

    public class OtpStatusResponse
    {
        public string Otp { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string SetupUrl { get; set; }
        public string Status { get; set; }
        public string ErrorMessage { get; set; }
    }
}
