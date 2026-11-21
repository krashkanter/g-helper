using GHelper.Helpers;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GHelper.AutoUpdate
{
    public class AutoUpdateControl
    {

        SettingsForm settings;

        // This fork publishes its own patched build of every upstream stable release
        // (.github/workflows/sync-upstream-release.yml), so updates come from the fork
        const string ForkRepo = "krashkanter/g-helper";
        const string UpstreamRepo = "seerge/g-helper";
        const string SyncWorkflow = "sync-upstream-release.yml";

        // How long a new upstream release may go without a patched build before it counts as failed
        static readonly TimeSpan BuildGracePeriod = TimeSpan.FromHours(6);

        public string versionUrl = $"https://github.com/{ForkRepo}/releases";
        public bool update = false;

        static long lastUpdate;

        public AutoUpdateControl(SettingsForm settingsForm)
        {
            settings = settingsForm;
            var appVersion = new Version(Assembly.GetExecutingAssembly().GetName().Version.ToString());
            settings.SetVersionLabel(Properties.Strings.VersionLabel + $": {appVersion.Major}.{appVersion.Minor}.{appVersion.Build}");
        }

        public void CheckForUpdates()
        {
            // Run update once per 12 hours
            if (Math.Abs(DateTimeOffset.Now.ToUnixTimeSeconds() - lastUpdate) < 43200) return;
            lastUpdate = DateTimeOffset.Now.ToUnixTimeSeconds();

            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                CheckForUpdatesAsync();
            });
        }

        public void Update()
        {
            if (update)
            {
                Task.Run(() =>
                {
                    CheckForUpdatesAsync(true);
                });
            } else
            {
                LoadReleases();
            }
        }

        public void LoadReleases()
        {
            try
            {
                Process.Start(new ProcessStartInfo(versionUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Failed to open releases page:" + ex.Message);
            }
        }

        async void CheckForUpdatesAsync(bool force = false)
        {

            if (AppConfig.Is("skip_updates")) return;

            try
            {

                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "G-Helper App");
                    var appVersion = new Version(Assembly.GetExecutingAssembly().GetName().Version.ToString());

                    var release = await GetLatestRelease(httpClient, ForkRepo);
                    if (release is null)
                    {
                        await CheckForkBuild(httpClient, appVersion);
                        return;
                    }

                    var config = release.Value;
                    var gitVersion = ParseTagVersion(config);
                    var tag = gitVersion.ToString();
                    var assets = config.GetProperty("assets");

                    string url = null;

                    for (int i = 0; i < assets.GetArrayLength(); i++)
                    {
                        if (assets[i].GetProperty("browser_download_url").ToString().Contains(".zip"))
                            url = assets[i].GetProperty("browser_download_url").ToString();
                    }

                    if (url is null)
                        url = assets[0].GetProperty("browser_download_url").ToString();

                    if (gitVersion.CompareTo(appVersion) > 0)
                    {
                        versionUrl = url;
                        update = true;
                        settings.SetVersionLabel(Properties.Strings.DownloadUpdate + $": {appVersion.Major}.{appVersion.Minor}.{appVersion.Build} → {tag}", true);

                        string[] args = Environment.GetCommandLineArgs();
                        if (force || args.Length > 1 && args[1] == "autoupdate")
                        {
                            AutoUpdate(url);
                            return;
                        }

                        if (AppConfig.GetString("skip_version") != tag)
                        {
                            DialogResult dialogResult = settings.ShowMessage(Properties.Strings.DownloadUpdate + ": G-Helper " + tag + "?", "Update", MessageBoxButtons.YesNo);
                            if (dialogResult == DialogResult.Yes)
                                AutoUpdate(url);
                            else
                                AppConfig.Set("skip_version", tag);
                        }

                    }
                    else
                    {
                        await CheckForkBuild(httpClient, appVersion);
                    }

                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Failed to check for updates:" + ex.Message);
            }

        }

        // Upstream has a newer stable release than this app, but the fork has not published a patched build of it:
        // the sync workflow failed (or never ran), so show "Update failed" instead of offering the update
        async Task CheckForkBuild(HttpClient httpClient, Version appVersion)
        {
            var upstream = await GetLatestRelease(httpClient, UpstreamRepo);
            var upstreamVersion = upstream is null ? null : ParseTagVersion(upstream.Value);

            if (upstreamVersion is null || upstreamVersion.CompareTo(appVersion) <= 0)
            {
                Logger.WriteLine($"Latest version {appVersion}");
                return;
            }

            var published = upstream.Value.GetProperty("published_at").GetDateTimeOffset();
            bool overdue = DateTimeOffset.UtcNow - published > BuildGracePeriod;

            if (!overdue && !await LastSyncRunFailed(httpClient))
            {
                Logger.WriteLine($"Upstream {upstreamVersion} released, patched build pending");
                return;
            }

            Logger.WriteLine($"Update failed: no patched build of upstream {upstreamVersion}");
            update = false;
            versionUrl = $"https://github.com/{ForkRepo}/actions/workflows/{SyncWorkflow}";
            settings.SetVersionLabel($"Update failed: {appVersion.Major}.{appVersion.Minor}.{appVersion.Build} → {upstreamVersion}", true);
        }

        static async Task<bool> LastSyncRunFailed(HttpClient httpClient)
        {
            var json = await httpClient.GetStringAsync($"https://api.github.com/repos/{ForkRepo}/actions/workflows/{SyncWorkflow}/runs?status=completed&per_page=1");
            var runs = JsonSerializer.Deserialize<JsonElement>(json).GetProperty("workflow_runs");
            return runs.GetArrayLength() > 0 && runs[0].GetProperty("conclusion").GetString() != "success";
        }

        static async Task<JsonElement?> GetLatestRelease(HttpClient httpClient, string repo)
        {
            using var response = await httpClient.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
            if (response.StatusCode == HttpStatusCode.NotFound) return null; // no stable release yet
            response.EnsureSuccessStatusCode();
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        }

        // Fork tags carry a suffix (v0.291-krashkanter), so take only the numeric part
        static Version ParseTagVersion(JsonElement release)
        {
            var match = Regex.Match(release.GetProperty("tag_name").GetString() ?? "", @"\d+(\.\d+)+");
            return match.Success ? new Version(match.Value) : new Version(0, 0);
        }

        public static string EscapeString(string input)
        {
            return Regex.Replace(Regex.Replace(input, @"\[|\]", "`$0"), @"\'", "''");
        }

        async void AutoUpdate(string requestUri)
        {

            Uri uri = new Uri(requestUri);
            string zipName = Path.GetFileName(uri.LocalPath);

            string exeLocation = Application.ExecutablePath;
            string exeDir = Path.GetDirectoryName(exeLocation);
            //exeDir = "C:\\Program Files\\GHelper";
            string exeName = Path.GetFileName(exeLocation);
            string zipLocation = exeDir + "\\" + zipName;

            using (HttpClient client = new HttpClient())
            {

                client.DefaultRequestHeaders.Add("User-Agent", "G-Helper App");
                Logger.WriteLine(requestUri);
                Logger.WriteLine(exeDir);
                Logger.WriteLine(zipName);
                Logger.WriteLine(exeName);

                try
                {
                    var bytes = await client.GetByteArrayAsync(uri);
                    File.WriteAllBytes(zipLocation, bytes);
                    Logger.WriteLine($"Downloaded {bytes.Length}b: {zipLocation} (exists={File.Exists(zipLocation)}, size={new FileInfo(zipLocation).Length})");
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(ex.Message);
                    if (!ProcessHelper.IsUserAdministrator())
                    {
                        ProcessHelper.RunAsAdmin("autoupdate");
                        Application.Exit();
                    } else
                    {
                        LoadReleases();
                    }
                    return;
                }

                string command = $"$ErrorActionPreference = \"Stop\"; Set-Location -Path '{EscapeString(exeDir)}'; Wait-Process -Name \"GHelper\"; Expand-Archive \"{zipName}\" -DestinationPath . -Force; Remove-Item \"{zipName}\" -Force; \".\\{exeName}\"; ";
                Logger.WriteLine(command);

                try
                {
                    var cmd = new Process();
                    cmd.StartInfo.WorkingDirectory = exeDir;
                    cmd.StartInfo.UseShellExecute = false;
                    cmd.StartInfo.CreateNoWindow = true;
                    cmd.StartInfo.FileName = "powershell";
                    cmd.StartInfo.Arguments = command;
                    if (ProcessHelper.IsUserAdministrator()) cmd.StartInfo.Verb = "runas";
                    cmd.Start();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(ex.Message);
                }

                Application.Exit();
            }

        }

    }
}
