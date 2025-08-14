using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
#if BOLTZ_SUPPORT
using BTCPayServer.Plugins.Boltz;
using Grpc.Core;
#endif
using Microsoft.Extensions.Logging;
using SamRockProtocol.Models;

namespace SamRockProtocol.Services;

/// <summary>
/// Class to wrap Boltz functionality, while we wait for Boltz Client to support Windows for easier debugging and development.
/// </summary>
public class BoltzWrapper(
#if BOLTZ_SUPPORT
    BoltzService boltzService,
#endif
    ILogger<BoltzWrapper> logger
    )
{
    public async Task SetBoltz(string storeId, string ctDescriptor, SamrockProtocolSetupResponse result)
    {
#if BOLTZ_SUPPORT
        try
        {
            var boltzSettings = await boltzService.InitializeStore(storeId, BoltzMode.Standalone);
            var boltzClient = boltzService.Daemon.GetClient(boltzSettings);

            // 1) Normalize descriptor
            var normalized = NormalizeDescriptor(ctDescriptor);

            // 2) Derive stable, non-leaky suffix
            // Use a server-wide secret or per-store secret; here we derive from storeId for example
            var hmacKey = SHA256.HashData(Encoding.UTF8.GetBytes("samrock-secret-" + storeId));
            var suffix = ShortHexHmac(normalized, hmacKey, 16); // 16 hex = 64 bits

            var name = $"samrock-{suffix}"; // lowercase is safer for some backends

            Boltzrpc.Wallet wallet;

            try
            {
                wallet = await boltzClient.GetWallet(name);
            }
            catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                try
                {
                    wallet = await boltzClient.ImportWallet(
                        new Boltzrpc.WalletParams
                        {
                            Name = name, Currency = Boltzrpc.Currency.Lbtc,
                        },
                        new Boltzrpc.WalletCredentials { CoreDescriptor = ctDescriptor, }
                    );
                }
                catch (RpcException ex2) when (ex2.StatusCode == StatusCode.InvalidArgument && ex2.Status.Detail.Contains("has the same credentials"))
                {
                    logger.LogWarning("Collision with existing wallet, likely in another store. Error: " + ex2.Status.Detail);
                    result.Results[SamrockProtocolKeys.BTC_LN] = new SamRockProtocolResponse(false, 
                        $"Collision with existing wallet, likely in another store. Error: " + ex2.Status.Detail, ex2);
                    
                    // If we can't import due to a collision, we don't want to proceed
                    return;
                }
            }

            boltzSettings.StandaloneWallet = new BoltzSettings.Wallet
            {
                Id = wallet.Id, Name = wallet.Name,
            };
            await boltzService.Set(storeId, boltzSettings);
            result.Results[SamrockProtocolKeys.BTC_LN] = new SamRockProtocolResponse(true, null, null);
        }
        catch (RpcException ex)
        {
            logger.LogError(ex, "Failed to import wallet.");
            result.Results[SamrockProtocolKeys.BTC_LN] = new SamRockProtocolResponse(false, $"Failed to import wallet. {ex.Status.Detail}", ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to import wallet.");
            result.Results[SamrockProtocolKeys.BTC_LN] = new SamRockProtocolResponse(false, "Failed to import wallet.", ex);
        }
#else
        // Boltz support is disabled
        logger.LogWarning("Boltz support is not enabled. Cannot set up Boltz wallet.");
        result.Results[SamrockProtocolKeys.BTC_LN] = new SamRockProtocolResponse(false, "Boltz support is not enabled in this build.", null);
#endif
    }
    
    // Normalize descriptor: trim, remove checksum suffix, collapse spaces
    static string NormalizeDescriptor(string d)
    {
        var s = d.Trim();
        var hashIdx = s.LastIndexOf('#');
        if (hashIdx >= 0) s = s.Substring(0, hashIdx);
        // optional: collapse runs of whitespace inside descriptor body
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
        return s;
    }

// Hash helper - use HMAC to avoid leaking info across stores
    static string ShortHexHmac(string payload, byte[] secretKey, int hexLen)
    {
        using var h = new HMACSHA256(secretKey);
        var bytes = h.ComputeHash(Encoding.UTF8.GetBytes(payload));
        // take first hexLen chars, lowercase
        return Convert.ToHexString(bytes).ToLowerInvariant().Substring(0, hexLen);
    }
}
