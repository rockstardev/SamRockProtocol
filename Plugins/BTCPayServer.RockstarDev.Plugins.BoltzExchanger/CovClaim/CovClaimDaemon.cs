#nullable enable
using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger.CovClaim
{
    public class CovClaimDaemon : IHostedService, IDisposable
    {
        // Dependencies
        private readonly ILogger<CovClaimDaemon> _logger;
        private readonly IOptions<DataDirectories> _dataDirectories;
        private readonly IHttpClientFactory _httpClientFactory; // For downloading

        private readonly ILogger<CovClaimDaemonRestClient> _loggerRestClient;

        public CovClaimDaemonRestClient CovClaimClient { get; private set; }

        // Process Management
        private Process? _process;
        private CancellationTokenSource? _stopCts;
        private readonly TaskCompletionSource<bool> _daemonReadyTcs = new(); // Signals when the daemon API is likely ready

        // Configuration & Paths
        private const string RequiredVersion = "0.0.1"; // Define the version we want to download
        private string DataDir => Path.Combine(_dataDirectories.Value.DataDir, "Plugins", "CovClaim");
        private string BinDir => Path.Combine(DataDir, "bin", $"windows_{Architecture}");
        private string DaemonBinary => Path.Combine(BinDir, "covclaim.exe");
        private string ConfigFile => Path.Combine(DataDir, ".env"); // Daemon expects config in its working dir

        private string Architecture => RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "amd64",
            _ => throw new NotSupportedException("Unsupported architecture")
        };

        // State
        public bool IsReady => _daemonReadyTcs.Task.IsCompletedSuccessfully && _daemonReadyTcs.Task.Result;
        public Task DaemonReadyTask => _daemonReadyTcs.Task;

        public CovClaimDaemon(
            ILogger<CovClaimDaemon> logger,
            IOptions<DataDirectories> dataDirectories,
            IHttpClientFactory httpClientFactory,
            ILogger<CovClaimDaemonRestClient> loggerRestClient)
        {
            _logger = logger;
            _dataDirectories = dataDirectories;
            _httpClientFactory = httpClientFactory;
            _loggerRestClient = loggerRestClient;
        }

        // --- IHostedService Implementation ---

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting CovClaimDaemon Hosted Service...");
            _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                // Ensure data directories exist
                Directory.CreateDirectory(DataDir);
                Directory.CreateDirectory(BinDir);

                // 1. Check/Download Executable
                if (!File.Exists(DaemonBinary))
                {
                    _logger.LogInformation("CovClaimDaemon executable not found at {DaemonBinary}. Attempting download...", DaemonBinary);
                    await DownloadBinaryAsync(RequiredVersion, _stopCts.Token);
                }
                // TODO: Add version check here if needed

                // 2. Check/Create .env file
                if (!File.Exists(ConfigFile))
                {
                    _logger.LogInformation("CovClaimDaemon .env file not found at {ConfigFile}. Creating default...", ConfigFile);
                    await CreateConfigFileAsync(_stopCts.Token);
                }

                // 3. Start the process
                _logger.LogInformation("Attempting to start CovClaimDaemon process from {DaemonBinary}...", DaemonBinary);
                var startInfo = new ProcessStartInfo
                {
                    FileName = DaemonBinary,
                    // Arguments = BuildArguments(), // Use .env file instead for this daemon
                    WorkingDirectory = DataDir, // Daemon expects .env in its working directory
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8, // Ensure correct encoding
                    StandardErrorEncoding = Encoding.UTF8
                };

                _process = new Process { StartInfo = startInfo };

                _process.OutputDataReceived += Process_OutputDataReceived;
                _process.ErrorDataReceived += Process_ErrorDataReceived;

                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                _logger.LogInformation("CovClaimDaemon process started (PID: {PID}). Waiting for readiness signal...", _process.Id);

                // Handle process exit
                _ = Task.Run(async () =>
                {
                    await _process.WaitForExitAsync(_stopCts.Token);
                    _logger.LogWarning("CovClaimDaemon process exited (PID: {PID}, Exit Code: {ExitCode}).", _process.Id, _process.ExitCode);
                    _daemonReadyTcs.TrySetResult(false); // Mark as not ready if process exits unexpectedly
                    // Optionally attempt restart here? For now, just log.
                }, _stopCts.Token);


                // Optional: Add a timeout for readiness check, similar to previous implementation
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), _stopCts.Token);
                var completedTask = await Task.WhenAny(_daemonReadyTcs.Task, timeoutTask);

                if (completedTask == timeoutTask && !_daemonReadyTcs.Task.IsCompleted)
                {
                    _logger.LogWarning(
                        "CovClaimDaemon did not signal readiness within the timeout period. Assuming ready or attempting API ping might be needed elsewhere.");
                    // We can still proceed, but readiness is uncertain. Let's tentatively set it ready.
                    _daemonReadyTcs.TrySetResult(true);
                }
                else if (_daemonReadyTcs.Task.IsFaulted)
                {
                    _logger.LogError("CovClaimDaemon failed to become ready.", _daemonReadyTcs.Task.Exception);
                    // Propagate the exception or handle it
                    await _daemonReadyTcs.Task; // Re-throws the exception captured by the TCS
                }
                else
                {
                    _logger.LogInformation("CovClaimDaemon signaled readiness.");
                }

                CovClaimClient = new CovClaimDaemonRestClient(_loggerRestClient, _httpClientFactory, "http://127.0.0.1:35791/covenant");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start CovClaimDaemon.");
                _daemonReadyTcs.TrySetException(ex); // Signal failure
                DisposeProcess(); // Clean up if start fails critically
                throw; // Re-throw to indicate service startup failure
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping CovClaimDaemon Hosted Service...");
            _stopCts?.Cancel();

            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _logger.LogInformation("Attempting to stop CovClaimDaemon process (PID: {PID})...", _process.Id);

                    // Give it a moment to shut down gracefully after cancellation signal (if it supports it)
                    // bool exited = await _process.WaitForExitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    // if (exited)
                    // {
                    //      _logger.LogInformation("CovClaimDaemon process exited gracefully.");
                    // }
                    // else
                    //{
                    _logger.LogInformation("Forcing CovClaimDaemon process kill (PID: {PID})...", _process.Id);
                    _process.Kill(entireProcessTree: true); // Kill the process and any children
                    await _process.WaitForExitAsync(cancellationToken); // Wait for confirmation
                    _logger.LogInformation("CovClaimDaemon process stopped forcefully.");
                    //}
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is NotSupportedException)
                {
                    _logger.LogWarning("Could not stop or kill CovClaimDaemon process (it might have already exited).");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while stopping CovClaimDaemon process.");
                }
            }
            else
            {
                _logger.LogInformation("CovClaimDaemon process was not running or already exited.");
            }

            DisposeProcess(); // Ensure cleanup
            _logger.LogInformation("CovClaimDaemon Hosted Service stopped.");
        }

        // --- Process Output Handlers ---

        private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                _logger.LogInformation("[covclaimd STDOUT] {Data}", e.Data); // Log as STDOUT
                CheckForReadinessSignal(e.Data);
            }
        }

        private void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                // Log stderr messages as Information or Warning, not Error by default
                _logger.LogInformation("[covclaimd STDERR] {Data}", e.Data); // Log as STDERR (use LogWarning if preferred)

                CheckForReadinessSignal(e.Data);
            }
        }

        // Helper method to check for readiness signal in log lines
        private void CheckForReadinessSignal(string logLine)
        {
             // Adjust this string based on the *actual* output of your covclaim daemon
             // Example: "Started API server on: 127.0.0.1:35791"
             if (logLine.Contains("Started API server on", StringComparison.OrdinalIgnoreCase))
             {
                 if (!_daemonReadyTcs.Task.IsCompleted) // Avoid logging multiple times if signal appears in both streams
                 {
                     _logger.LogInformation("CovClaimDaemon REST API detected as ready.");
                 }
                 _daemonReadyTcs.TrySetResult(true); // OK to call multiple times
             }
             // Add checks for specific FATAL error messages here if needed to fail the TCS
             // else if (logLine.Contains("CRITICAL ERROR PATTERN", StringComparison.OrdinalIgnoreCase))
             // {
             //     _daemonReadyTcs.TrySetException(new Exception($"CovClaimDaemon critical error: {logLine}"));
             // }
        }

        // --- Helper Methods ---

        private async Task DownloadBinaryAsync(string version, CancellationToken cancellationToken)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new NotSupportedException("Automatic download only supported on Windows.");
            }

            var client = _httpClientFactory.CreateClient("CovClaimDownload");
            var releaseBaseUrl = $"https://github.com/rockstardev/Aqua.BTCPayPlugin/releases/download/v{version}/";
            string archiveName = $"covclaim-windows-{Architecture}-v{version}.tar.gz";
            var downloadUrl = releaseBaseUrl + archiveName;
            //downloadUrl = "https://github.com/BoltzExchange/boltz-client/releases/download/v2.5.1/boltz-client-linux-amd64-v2.5.1.tar.gz";

            _logger.LogInformation("Downloading CovClaimDaemon ({Version}) from {DownloadUrl}...", version, downloadUrl);

            try
            {
                using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var gzip = new GZipStream(stream, CompressionMode.Decompress);

                // Extract directly to the final bin directory
                _logger.LogInformation("Extracting {ArchiveName} to {BinDir}...", archiveName, BinDir);
                await TarFile.ExtractToDirectoryAsync(gzip, BinDir, true);

                // Check if the executable exists after extraction
                if (!File.Exists(DaemonBinary))
                {
                    throw new FileNotFoundException($"Executable {DaemonBinary} not found after extraction from {archiveName}.");
                }

                _logger.LogInformation("CovClaimDaemon binary downloaded and extracted successfully.");

                // Make executable (Linux/macOS only - not needed for Windows .exe)
                // if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                // {
                //     File.SetUnixFileMode(DaemonBinary, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                // }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download or extract CovClaimDaemon.");
                throw; // Re-throw to prevent startup if download fails
            }
        }

        private async Task CreateConfigFileAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Ensure the directory exists
                Directory.CreateDirectory(DataDir);
                await File.WriteAllTextAsync(ConfigFile, EnvFileTemplate, Encoding.UTF8, cancellationToken);
                _logger.LogInformation("Created default .env file at {ConfigFile}", ConfigFile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create default .env file at {ConfigFile}.", ConfigFile);
                throw; // Re-throw to prevent startup if config creation fails
            }
        }

        // --- Cleanup ---

        private void DisposeProcess()
        {
            if (_process != null)
            {
                try
                {
                    // Detach handlers to prevent issues during disposal
                    _process.OutputDataReceived -= Process_OutputDataReceived;
                    _process.ErrorDataReceived -= Process_ErrorDataReceived;
                }
                catch { } // Ignore errors detaching

                _process.Dispose();
                _process = null;
                _logger.LogDebug("CovClaimDaemon process object disposed.");
            }
        }

        public void Dispose()
        {
            _logger.LogDebug("Disposing CovClaimDaemon hosted service.");
            DisposeProcess();
            _stopCts?.Dispose();
            // _httpClient is disposed by the factory if created via factory, otherwise manage here
        }

        // --- .env Template ---
        // Minimal template - users MUST configure this properly, especially RPC details
        private static readonly string EnvFileTemplate = @"
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
API_PORT=35791

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
ESPLORA_POLL_INTERVAL=10

# Max reqs/second for the Esplora endpoint; useful when hitting rate limits
# Set to 0 to disable
ESPLORA_MAX_REQUESTS_PER_SECOND=4

# Used in combination with the Esplora backend to broadcast lowball transactions
# Set to empty string to disable
BOLTZ_ENDPOINT=https://api.boltz.exchange/v2
";
    }
}
