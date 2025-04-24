#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.CLightning;
using BTCPayServer.Lightning.LND;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Bcpg.OpenPgp;

// Added for convenience

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger.CovClaim
{
    public class CovClaimDaemon(
        IOptions<DataDirectories> dataDirectories,
        ILogger<CovClaimDaemon> logger,
        ILogger<CovClaimDaemonRestClient> clientLogger,
        BTCPayNetworkProvider btcPayNetworkProvider,
        IHttpClientFactory httpClientFactory)
    {
        private static readonly Version ClientVersion = new("0.0.1");

        private Stream? _downloadStream;
        private Task? _startTask;
        private CancellationTokenSource? _daemonCancel;
        private Task? _daemonTask;
        private readonly List<string> _output = new();
        private readonly HttpClient _httpClient = new();
        private const int MaxLogLines = 150;
        private string DataDir => Path.Combine(dataDirectories.Value.DataDir, "Plugins", "CovClaim");
        private string DaemonBinary => Path.Combine(DataDir, "bin", $"windows_{Architecture}", "covclaim.exe");
        private string ConfigFile => Path.Combine(DataDir, ".env");
        private readonly SemaphoreSlim _configSemaphore = new(1, 1);
        public bool Starting => _startTask is not null && !_startTask.IsCompleted;

        public readonly TaskCompletionSource<bool> InitialStart = new();
        public CovClaimDaemonRestClient? AdminClient { get; private set; }

        public string? Error { get; private set; }
        public string RecentOutput => string.Join("\n", _output);

        private string Architecture => RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "amd64",
            _ => throw new NotSupportedException("Unsupported architecture")
        };

        public async Task Wait(CancellationToken cancellationToken)
        {
            // this waited on lnd & boltz client, we can initialize right away
            while (true)
            {
                var client = new CovClaimDaemonRestClient(clientLogger, httpClientFactory);

                try
                {
                    //await client.GetInfo(cancellationToken);
                    logger.LogInformation("Client created");
                    AdminClient = client;
                    Error = null;
                    return;
                }
                catch (Exception e)
                {
                    // if (!cancellationToken.IsCancellationRequested)
                    // {
                    //     latestError = e.Status.Detail;
                    // }

                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
            }

            await Stop();
        }

        public async Task Download(Version version)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new NotSupportedException("Only windows is supported");
            }

            logger.LogInformation($"Downloading covclaim");

            string archiveName = $"covclaim-windows-{Architecture}-v{version}.tar.gz";
            await using var s = await _httpClient.GetStreamAsync(ReleaseUrl(version) + archiveName);

            _downloadStream = s;
            await using var gzip = new GZipStream(s, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, DataDir, true);
            _downloadStream = null;

            //await CheckBinaries(version);
        }

        private string ReleaseUrl(Version version)
        {
            return $"https://github.com/rockstardev/Aqua.BTCPayPlugin/releases/download/v{version}/";
        }

        private async Task<Stream> DownloadFile(string uri, string destination)
        {
            string path = Path.Combine(DataDir, destination);
            if (!File.Exists(path))
            {
                await using var s = await _httpClient.GetStreamAsync(uri);
                await using var fs = new FileStream(path, FileMode.Create);
                await s.CopyToAsync(fs);
            }

            return File.OpenRead(path);
        }

        // private async Task CheckBinaries(Version version)
        // {
        //     string releaseUrl = ReleaseUrl(version);
        //     string manifestName = $"boltz-client-manifest-v{version}.txt";
        //     string sigName = $"boltz-client-manifest-v{version}.txt.sig";
        //     string pubKey = "boltz.asc";
        //     string pubKeyUrl = "https://canary.boltz.exchange/pgp.asc";
        //
        //     await using var sigStream = await DownloadFile(releaseUrl + sigName, sigName);
        //     await using var pubKeyStream = await DownloadFile(pubKeyUrl, pubKey);
        //
        //     var keyRing = new PgpPublicKeyRing(PgpUtilities.GetDecoderStream(pubKeyStream));
        //     var pgpFactory = new PgpObjectFactory(PgpUtilities.GetDecoderStream(sigStream));
        //     PgpSignatureList sigList = (PgpSignatureList)pgpFactory.NextPgpObject();
        //     PgpSignature sig = sigList[0];
        //
        //     var manifest = await _httpClient.GetByteArrayAsync(releaseUrl + manifestName);
        //     string manifestPath = Path.Combine(DataDir, manifestName);
        //     await File.WriteAllBytesAsync(manifestPath, manifest);
        //     PgpPublicKey publicKey = keyRing.GetPublicKey(sig.KeyId);
        //     sig.InitVerify(publicKey);
        //     sig.Update(manifest);
        //     if (!sig.Verify())
        //     {
        //         throw new Exception("Signature verification failed.");
        //     }
        //
        //     CheckShaSums(DaemonBinary, manifestPath);
        //     CheckShaSums(DaemonCli, manifestPath);
        // }
        //
        // private void CheckShaSums(string fileToCheck, string manifestFile)
        // {
        //     // Compute the SHA256 hash of the file
        //     using var sha256 = SHA256.Create();
        //     using var stream = File.OpenRead(fileToCheck);
        //     byte[] hashBytes = sha256.ComputeHash(stream);
        //     string computedHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        //
        //     foreach (var line in File.ReadLines(manifestFile))
        //     {
        //         var split = line.Split();
        //         if (fileToCheck.Contains(split.Last()))
        //         {
        //             var expectedHash = split.First();
        //             if (computedHash == expectedHash)
        //             {
        //                 return;
        //             }
        //
        //             throw new Exception("SHA256 hash mismatch");
        //         }
        //     }
        //
        //     throw new Exception("File not found in manifest");
        // }

        public double DownloadProgress()
        {
            if (_downloadStream is null)
            {
                return 0;
            }

            return 100 * (double)_downloadStream.Position / _downloadStream.Length;
        }


        private async Task Configure()
        {
            try
            {
                await _configSemaphore.WaitAsync();

                if (!Path.Exists(ConfigFile))
                {
                    await File.WriteAllTextAsync(ConfigFile, envFileTemplate);
                }

                await Start(true);
            }
            catch (Exception e)
            {
                Error = e.Message;
            }
            finally
            {
                InitialStart.TrySetResult(true);
                _configSemaphore.Release();
            }
        }

        public async Task Init()
        {
            logger.LogDebug("Initializing");
            try
            {
                if (!Directory.Exists(DataDir))
                {
                    Directory.CreateDirectory(DataDir);
                }

                string? currentVersion = null;
                if (Path.Exists(DaemonBinary))
                {
                    var (code, stdout, _) = await RunCommand(DaemonBinary, "--version");
                    if (code != 0)
                    {
                        logger.LogInformation($"Failed to get current client version: {stdout}");
                    }
                    else
                    {
                        currentVersion = stdout.Split("\n").First().Split("-").First().TrimStart('v');
                    }
                }

                Version.TryParse(currentVersion, out var current);
                if (current == null || current.CompareTo(ClientVersion) < 0)
                {
                    if (current != null)
                    {
                        logger.LogInformation("Client version outdated");
                    }

                    await Download(ClientVersion);
                }
            }
            catch (Exception e)
            {
                Error = e.Message;
                logger.LogError(e, "Failed to initialize");
            }
        }

        private async Task Run(CancellationTokenSource daemonCancel, bool logOutput)
        {
            _output.Clear();
            using var process = NewProcess(DaemonBinary, $"--datadir {DataDir}");
            try
            {
                process.Start();
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to start client process");
                Error = $"Failed to start process {e.Message}";
                return;
            }

            MonitorStream(process.StandardOutput, daemonCancel.Token);
            MonitorStream(process.StandardError, daemonCancel.Token, true);

            try
            {
                await process.WaitForExitAsync(daemonCancel.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
            finally
            {
                var wasRunning = true;
                AdminClient = null;

                if (process.ExitCode != 0)
                {
                    if (logOutput || wasRunning)
                    {
                        Error = $"Process exited with code {process.ExitCode}";
                        logger.LogError(Error);

                        logger.LogInformation(RecentOutput);
                    }

                    if (wasRunning && !daemonCancel.IsCancellationRequested)
                    {
                        logger.LogInformation("Restarting in 10 seconds");
                        await Task.Delay(10000, daemonCancel.Token);
                        InitiateStart();
                    }
                }
            }
        }

        private async Task Start(bool logOutput = true)
        {
            await Stop();
            logger.LogInformation("Starting client process");
            var daemonCancel = new CancellationTokenSource();
            _daemonTask = Run(daemonCancel, logOutput);
            var wait = CancellationTokenSource.CreateLinkedTokenSource(daemonCancel.Token);
            wait.CancelAfter(TimeSpan.FromSeconds(60));
            _daemonCancel = daemonCancel;
            await Wait(wait.Token);
            // if (Running)
            // {
            //     AdminClient!.SwapUpdate += SwapUpdate!;
            // }
        }

        public void InitiateStart()
        {
            if (!Starting)
            {
                _startTask = Start();
            }
        }

        public async Task Stop()
        {
            if (_daemonTask is not null && !_daemonTask.IsCompleted)
            {
                var source = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                if (AdminClient is not null)
                {
                    try
                    {
                        logger.LogInformation("Stopping gracefully");
                        //await AdminClient.Stop(source.Token);
                    }
                    catch (Exception)
                    {
                        logger.LogInformation("Graceful stop timed out, killing client process");
                        logger.LogInformation(RecentOutput);
                        if (_daemonCancel is not null)
                        {
                            await _daemonCancel.CancelAsync();
                        }
                    }
                }
                else if (_daemonCancel is not null)
                {
                    await _daemonCancel.CancelAsync();
                }

                await _daemonTask.WaitAsync(CancellationToken.None);
                logger.LogInformation("Stopped");
            }

            // make sure to clear any leftover channels
            // BoltzClient.Clear();
        }

        private Process NewProcess(string fileName, string args)
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            //processStartInfo.EnvironmentVariables.Add("GRPC_GO_LOG_VERBOSITY_LEVEL", "99");
            //processStartInfo.EnvironmentVariables.Add("GRPC_GO_LOG_SEVERITY_LEVEL", "info");
            return new Process { StartInfo = processStartInfo };
        }

        async Task<(int, string, string)> RunCommand(string fileName, string args,
            CancellationToken cancellationToken = default)
        {
            using Process process = NewProcess(fileName, args);
            process.Start();
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            return (process.ExitCode, stdout, stderr);
        }

        private void MonitorStream(StreamReader streamReader, CancellationToken cancellationToken, bool logAll = false)
        {
            Task.Factory.StartNew(async () =>
            {
                while (!streamReader.EndOfStream)
                {
                    var line = await streamReader.ReadLineAsync(cancellationToken);
                    if (line != null)
                    {
                        {
                            if (line.Contains("ERROR") || line.Contains("WARN"))
                            {
                                logger.LogWarning(line);
                            }

                            if (_output.Count >= MaxLogLines && !logAll)
                            {
                                _output.RemoveAt(0);
                            }

                            _output.Add(line);
                        }
                    }
                }
            }, cancellationToken);
        }
    
        private static string envFileTemplate = @"
RUST_LOG=trace,hyper=info,tracing=info,reqwest=info

# The database that should be used
# SQLite and PostgreSQL are supported:
# - sqlite://./db.sqlite
# - postgresql://boltz:boltz@127.0.0.1:5432/covclaim
DATABASE_URL=sqlite://./db.sqlite

# When finding a lockup transaction, how many seconds to wait before broadcasting the covenant claim (0 for instantly)
SWEEP_TIME=120

# How often to broadcast claim transaction in seconds
SWEEP_INTERVAL=30

# Possible values: mainnet, testnet, regtest
NETWORK=mainnet

# Rest API configuration
API_HOST=127.0.0.1
API_PORT=1234

# Chain backend to use
# Options:
# - elements
# - esplora
CHAIN_BACKEND=esplora

# Configuration of the Elements daemon to connect to
ELEMENTS_HOST=127.0.0.1
ELEMENTS_PORT=18884
ELEMENTS_COOKIE=/home/michael/Git/TypeScript/boltz-backend/docker/regtest/data/core/cookies/.elements-cookie

# Configuration of the Esplora backend
ESPLORA_ENDPOINT=https://blockstream.info/liquid/api

# Poll interval for new blocks in seconds
ESPLORA_POLL_INTERVAL=5

# Max reqs/second for the Esplora endpoint; useful when hitting rate limits
# Set to 0 to disable
ESPLORA_MAX_REQUESTS_PER_SECOND=4

# Used in combination with the Esplora backend to broadcast lowball transactions
# Set to empty string to disable
BOLTZ_ENDPOINT=https://api.boltz.exchange/v2";
    }
}
