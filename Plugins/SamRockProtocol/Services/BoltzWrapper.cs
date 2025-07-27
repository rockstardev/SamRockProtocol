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

            // by including a part of the descriptor hash, we can know if the wallet already exists
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(ctDescriptor));
            var hashSubstring = Convert.ToHexString(hashBytes)[..8];
            var name = $"Samrock-{hashSubstring}";

            Boltzrpc.Wallet wallet;

            try
            {
                wallet = await boltzClient.GetWallet(name);
            }
            catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                wallet = await boltzClient.ImportWallet(
                    new Boltzrpc.WalletParams
                    {
                        Name = name, Currency = Boltzrpc.Currency.Lbtc,
                    },
                    new Boltzrpc.WalletCredentials { CoreDescriptor = ctDescriptor, }
                );
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
        await Task.CompletedTask;
#endif
    }
}
