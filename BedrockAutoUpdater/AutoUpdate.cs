using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Vellum;
using Vellum.Automation;
using Vellum.Extension;

namespace BedrockUpdater
{
    public class AutoUpdate : IPlugin
    {
        private const string VersionPattern = @"^.+\sVersion\s+(\d+(.\d+){0,3})\s*$";
        private const string DownloadPageUrl = "https://www.minecraft.net/download/server/bedrock";

        private static readonly string DownloadUrlPattern =
            $@"https:\/\/minecraft\.azureedge\.net\/bin-{(Environment.OSVersion.Platform == PlatformID.Win32NT ? "win" : "linux")}\/bedrock-server-(\d+\.\d+\.\d+(?>\.\d+)?)\.zip";

        private readonly Dictionary<byte, string> hooks = new Dictionary<byte, string>();
        private readonly CancellationTokenSource stopSignal = new CancellationTokenSource();

        private readonly ManualResetEventAsync onBackupPluginIdle = new ManualResetEventAsync();
        private readonly ManualResetEventAsync idleSignal = new ManualResetEventAsync();

        private int playerCount;

        private Version currentVersion;
        private AutoUpdateConfig config;

        public PluginType PluginType => PluginType.EXTERNAL;
        public Dictionary<byte, string> GetHooks() => hooks;

        private IHost host;
        private ProcessManager bds;
        private BackupManager backupManager;

        private void Log(string message) => Console.WriteLine($"[AutoUpdate] {message}");

        private void LogAndSend(string message)
        {
            bds.SendTellraw(message);
            Log(message);
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            Log($"Plugin started. Current BDS version is {currentVersion}.");

            using var httpClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("accept", "text/html,application/xhtml+xml,application/xml");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "Mozilla/5.0");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("accept-language", "en-US,en");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("accept-encoding", "gzip,deflate,br");

            while (!cancellationToken.IsCancellationRequested)
            {
                Version version;
                string file = default;
                try
                {
                    (version, file) = await DownloadUpdateAsync(httpClient, cancellationToken);
                    if (version != default)
                    {
                        switch (config?.InstallationMode)
                        {
                            case "immediate":
                                LogAndSend($"Downloaded new version {version}. Server will shutdown in 60 seconds to install update.");
                                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
                                await InstallUpdateAsync(version, file, cancellationToken);
                                break;

                            case "scheduled":
                                var t = TimeSpan.Parse(config.InstallationTime);
                                LogAndSend($"Downloaded new version {version}. Server will shutdown at {t} to install update.");
                                await WaitUntilAsync(t.Add(TimeSpan.FromSeconds(-60)), cancellationToken);
                                LogAndSend("Server will shutdown in 60 seconds to install update.");
                                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
                                await InstallUpdateAsync(version, file, cancellationToken);
                                break;
                            case "idle":
                            default:
                                LogAndSend($"Downloaded new version {version}. Server will shutdown to install update when all players are offline.");
                                await idleSignal.WaitAsync(cancellationToken);
                                await InstallUpdateAsync(version, file, cancellationToken);
                                break;
                        }
                    }
                }
                catch (Exception e)
                {
                    Log("Unhandled execption: " + e);
                }
                finally
                {
                    if (file != null && File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }

                await Task.Delay(TimeSpan.FromMinutes(config.UpdateCheckInterval), cancellationToken);
            }
        }

        private async Task<(Version version, string fileName)> DownloadUpdateAsync(HttpClient httpClient, CancellationToken cancellationToken)
        {
            Log("Checking for update...");

            using var resposne = await httpClient.GetAsync(DownloadPageUrl, cancellationToken);
            if (!resposne.IsSuccessStatusCode)
            {
                Log($"Update check failed. Server returned status code {resposne.StatusCode}.");
                return default;
            }

            var content = await resposne.Content.ReadAsStringAsync();
            var url = Regex.Match(content, DownloadUrlPattern);

            if (!url.Success)
            {
                Log($"Update check failed. Cannot find download url in Bedrock Dedicated Server download page.");
                return default;
            }

            var targetVersion = Version.Parse(url.Groups[1].Value);
            if (targetVersion > currentVersion)
            {
                Log($"Found latest version {targetVersion}. Downloading...");
                bool success = false;
                var tmpFile = Path.GetTempFileName();

                try
                {
                    using var stream = await httpClient.GetStreamAsync(url.Value);
                    using var fs = File.OpenWrite(tmpFile);

                    await stream.CopyToAsync(fs, cancellationToken);
                    success = true;

                    return (targetVersion, tmpFile);
                }
                catch (Exception e)
                {
                    Log($"Failed to download version {targetVersion}: {e.Message}");
                    return default;
                }
                finally
                {
                    if (!success && File.Exists(tmpFile))
                    {
                        File.Delete(tmpFile);
                    }
                }
            }
            else
            {
                Log($"Latest version is {targetVersion}, nothing to do.");
            }

            return default;
        }

        private async Task InstallUpdateAsync(Version targetVersion, string updateFile, CancellationToken cancellationToken)
        {
            var installDir = Path.GetDirectoryName(Path.GetFullPath(host.RunConfig.BdsBinPath));

            Log("Waiting for backup manager to finish its works...");
            await onBackupPluginIdle.WaitAsync(cancellationToken);
            Log($"Installing BDS version {targetVersion} into '{installDir}'...");

            bool shouldStopAndStart = bds.IsRunning;
            if (shouldStopAndStart)
            {
                Log($"Shutting down active BDS instance...");
                bds.SendInput("stop");
                bds.Process.WaitForExit();
                bds.Close();
            }

            Log($"Copying files...");

            using var archive = new ZipArchive(File.OpenRead(updateFile), ZipArchiveMode.Read, false);
            foreach (var entry in archive.Entries.Where(x =>
                 !string.IsNullOrEmpty(x.Name) && config?.IgnoreFiles?.Contains(x.Name) != true))
            {
                var path = Path.Combine(installDir, entry.FullName);
                Log($"Copying {path}...");
                var dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                entry.ExtractToFile(Path.Combine(installDir, entry.FullName), true);
            }

            Log($"Successfully installed BDS version {targetVersion}.");

            if (shouldStopAndStart) bds.Start();
        }

        private async Task WaitUntilAsync(TimeSpan timeToStart, CancellationToken cancellationToken)
        {
            var now = DateTime.Now.TimeOfDay;
            if (timeToStart <= now)
            {
                timeToStart = timeToStart.Add(TimeSpan.FromDays(1));

            }

            Log($"Update installation is scheduled after {timeToStart - now}.");
            await Task.Delay(timeToStart - now, cancellationToken);
        }

        private void OnVersionDetermined(object sender, MatchedEventArgs e)
        {
            var firstRun = currentVersion is null;

            var versionString = e.Matches[0].Groups[1].Value;
            currentVersion = Version.Parse(versionString);

            if (firstRun)
            {
                _ = RunAsync(stopSignal.Token);
            }
        }

        public void Initialize(IHost host)
        {
            this.host = host;

            bds = (ProcessManager)host.GetPluginByName("ProcessManager");
            backupManager = (BackupManager)host.GetPluginByName("BackupManager");

            config = host.LoadPluginConfiguration<AutoUpdateConfig>(GetType());

            // Trigger signal when backup manager finished backup.
            onBackupPluginIdle.Set();
            backupManager.RegisterHook((byte)BackupManager.Hook.BEGIN, (s, e) => onBackupPluginIdle.Reset());
            backupManager.RegisterHook((byte)BackupManager.Hook.END, (s, e) => onBackupPluginIdle.Set());

            idleSignal.Set();
            bds.RegisterMatchHandler(CommonRegex.PlayerConnected, (s, e) =>
            {
                Interlocked.Increment(ref playerCount);
                idleSignal.Reset();
            });
            bds.RegisterMatchHandler(CommonRegex.PlayerDisconnected, (s, e) =>
            {
                if (Interlocked.Decrement(ref playerCount) <= 0)
                {
                    idleSignal.Set();
                }
            });

            bds.Process.EnableRaisingEvents = true;
            bds.Process.Exited += (s, e) =>
            {
                playerCount = 0;
                idleSignal.Set();
            };

            bds.RegisterMatchHandler(VersionPattern, OnVersionDetermined);
        }

        public void RegisterHook(byte id, IPlugin.HookHandler callback)
        {

        }

        public void Unload()
        {
            stopSignal.Cancel();
        }

        public static object GetDefaultRunConfiguration()
        {
            return new AutoUpdateConfig()
            {
                InstallationMode = "idle",
                UpdateCheckInterval = 60,
                InstallationTime = "04:00",
                IgnoreFiles = new[] { "server.properties", "whitelist.json", "permissions.json" }
            };
        }
    }
}
