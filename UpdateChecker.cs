using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;

namespace PlayniteGameOverlay
{
    // Update checks: "Updates" section on the settings page (check now + automatic check interval)
    // and a background check at Playnite start that posts a Playnite notification.
    public partial class OverlaySettings
    {
        private string _updateCheckMode = "Weekly";
        private string _updateCheckModeBackup = "Weekly";

        /// <summary>Start | Daily | Weekly | Monthly | Never</summary>
        public string UpdateCheckMode
        {
            get => _updateCheckMode;
            set => SetValue(ref _updateCheckMode, UpdateChecker.NormalizeMode(value));
        }

        private void UpdateChecks_Load(OverlaySettings saved)
        {
            if (saved == null) return;
            UpdateCheckMode = saved.UpdateCheckMode;
        }

        private void UpdateChecks_Backup() { _updateCheckModeBackup = UpdateCheckMode; }

        private void UpdateChecks_Restore() { UpdateCheckMode = _updateCheckModeBackup; }
    }

    public sealed class UpdateCheckResult
    {
        public bool Success;
        public bool UpdateAvailable;
        public string LatestVersion;
        public string ReleaseUrl;
        public string Error;
    }

    public static class UpdateChecker
    {
        // GitHub repository that publishes the releases (tag like v1.0.1).
        // Change these two if the repository lives somewhere else.
        private const string RepoOwner = "pahan3000";
        private const string RepoName = "PlayniteGameOverlay";
        private const string DisplayName = "Playnite Overlay 3000";

        private static string _statePath;
        private static readonly object _stateLock = new object();

        private static string _installedVersion;

        /// <summary>Version from extension.yaml (what Playnite shows), falling back to the assembly version.</summary>
        public static string InstalledVersion
        {
            get
            {
                if (_installedVersion != null) return _installedVersion;
                string v = null;
                try
                {
                    var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    var yaml = Path.Combine(dir ?? "", "extension.yaml");
                    if (File.Exists(yaml))
                    {
                        var m = Regex.Match(File.ReadAllText(yaml), @"^\s*Version:\s*['""]?([0-9][0-9A-Za-z\.\-\+]*)", RegexOptions.Multiline);
                        if (m.Success) v = m.Groups[1].Value;
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(v))
                {
                    try
                    {
                        var ver = Assembly.GetExecutingAssembly().GetName().Version;
                        v = ver.Major + "." + ver.Minor + "." + Math.Max(ver.Build, 0);
                    }
                    catch { v = "0.0.0"; }
                }
                _installedVersion = v;
                return v;
            }
        }

        // Bound from the settings page.
        public static string InstalledVersionLabel { get { return "Installed version: " + InstalledVersion; } }
        public static string CheckButtonLabel { get { return "Check for " + DisplayName + " updates"; } }
        public static string AutoCheckLabel { get { return "Automatic " + DisplayName + " update checks"; } }

        public static string NormalizeMode(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "start": return "Start";
                case "daily": return "Daily";
                case "monthly": return "Monthly";
                case "never": return "Never";
                default: return "Weekly";
            }
        }

        private static TimeSpan? Interval(string mode)
        {
            switch (NormalizeMode(mode))
            {
                case "Start": return TimeSpan.Zero;
                case "Daily": return TimeSpan.FromDays(1);
                case "Monthly": return TimeSpan.FromDays(30);
                case "Never": return null;
                default: return TimeSpan.FromDays(7);
            }
        }

        // ---- remembered state (last check time, last version we notified about) ----

        private static void ReadState(out DateTime lastCheckUtc, out string notifiedVersion)
        {
            lastCheckUtc = DateTime.MinValue;
            notifiedVersion = "";
            try
            {
                lock (_stateLock)
                {
                    if (_statePath == null || !File.Exists(_statePath)) return;
                    var lines = File.ReadAllLines(_statePath);
                    long ticks;
                    if (lines.Length > 0 && long.TryParse(lines[0], out ticks) && ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
                        lastCheckUtc = new DateTime(ticks, DateTimeKind.Utc);
                    if (lines.Length > 1) notifiedVersion = lines[1].Trim();
                }
            }
            catch { }
        }

        private static void WriteState(DateTime lastCheckUtc, string notifiedVersion)
        {
            try
            {
                lock (_stateLock)
                {
                    if (_statePath == null) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(_statePath));
                    File.WriteAllLines(_statePath, new[] { lastCheckUtc.Ticks.ToString(), notifiedVersion ?? "" });
                }
            }
            catch { }
        }

        // ---- the check itself (blocking; call from a background thread) ----

        public static UpdateCheckResult CheckNow()
        {
            var result = new UpdateCheckResult();
            try
            {
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { } // TLS 1.2

                var url = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "PlayniteOverlay3000-UpdateCheck";
                req.Accept = "application/vnd.github+json";
                req.Timeout = 12000;
                req.ReadWriteTimeout = 12000;

                string body;
                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var rs = new StreamReader(resp.GetResponseStream()))
                        body = rs.ReadToEnd();
                }
                catch (WebException wex)
                {
                    var hr = wex.Response as HttpWebResponse;
                    if (hr != null && hr.StatusCode == HttpStatusCode.NotFound)
                    {
                        result.Error = "No releases were found for " + DisplayName + " yet.";
                        return result;
                    }
                    if (hr != null && (int)hr.StatusCode == 403)
                    {
                        result.Error = "GitHub is limiting requests right now. Try again later.";
                        return result;
                    }
                    result.Error = "Could not reach GitHub. Check your internet connection.";
                    return result;
                }

                var tag = Regex.Match(body, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                if (!tag.Success)
                {
                    result.Error = "The update server returned something unexpected.";
                    return result;
                }
                var latest = ParseVersion(tag.Groups[1].Value);
                var current = ParseVersion(InstalledVersion);
                if (latest == null)
                {
                    result.Error = "Could not read the latest version (" + tag.Groups[1].Value + ").";
                    return result;
                }

                var page = Regex.Match(body, "\"html_url\"\\s*:\\s*\"([^\"]+)\"");
                result.ReleaseUrl = page.Success
                    ? page.Groups[1].Value.Replace("\\/", "/")
                    : "https://github.com/" + RepoOwner + "/" + RepoName + "/releases/latest";
                result.LatestVersion = latest.ToString();
                result.UpdateAvailable = current != null && latest > current;
                result.Success = true;

                string notified; DateTime dummy;
                ReadState(out dummy, out notified);
                WriteState(DateTime.UtcNow, notified);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = "Update check failed: " + ex.Message;
            }
            return result;
        }

        private static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = Regex.Match(text, @"(\d+(?:\.\d+){0,3})");
            if (!m.Success) return null;
            var s = m.Groups[1].Value;
            if (s.IndexOf('.') < 0) s += ".0";
            Version v;
            return Version.TryParse(s, out v) ? v : null;
        }

        public static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // ---- automatic check at Playnite start ----

        public static void StartAutoCheck(PlayniteGameOverlay plugin, IPlayniteAPI api, ILogger log)
        {
            try { _statePath = Path.Combine(plugin.GetPluginUserDataPath(), "update-check.txt"); }
            catch { return; }

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(25)); // let Playnite finish starting first
                    var interval = Interval(plugin.Settings.UpdateCheckMode);
                    if (interval == null) return;

                    DateTime last; string notified;
                    ReadState(out last, out notified);
                    if (interval.Value > TimeSpan.Zero && DateTime.UtcNow - last < interval.Value) return;

                    var r = CheckNow();
                    if (!r.Success)
                    {
                        if (log != null) log.Info("Update check: " + r.Error);
                        return;
                    }
                    if (!r.UpdateAvailable || string.Equals(notified, r.LatestVersion, StringComparison.OrdinalIgnoreCase)) return;

                    DateTime lastNow; string dummy;
                    ReadState(out lastNow, out dummy);
                    WriteState(lastNow, r.LatestVersion);

                    var url = r.ReleaseUrl;
                    api.Notifications.Add(new NotificationMessage(
                        "PlayniteOverlay3000-update",
                        DisplayName + " " + r.LatestVersion + " is available (installed: " + InstalledVersion + "). Click to open the download page.",
                        NotificationType.Info,
                        () => OpenUrl(url)));
                }
                catch (Exception ex)
                {
                    if (log != null) log.Warn("Update check failed: " + ex.Message);
                }
            });
        }
    }

    // Button handlers of the "Updates" section (the view is a partial class).
    public partial class OverlaySettingsView
    {
        private bool _updateCheckRunning;

        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_updateCheckRunning) return;
            _updateCheckRunning = true;
            var btn = sender as Button;
            if (btn != null) btn.IsEnabled = false;
            UpdateOpenButton.Visibility = Visibility.Collapsed;
            UpdateStatusText.Visibility = Visibility.Visible;
            UpdateStatusText.Text = "Checking for updates…";
            try
            {
                var r = await Task.Run(() => UpdateChecker.CheckNow());
                if (!r.Success)
                {
                    UpdateStatusText.Text = r.Error ?? "Could not check for updates.";
                }
                else if (r.UpdateAvailable)
                {
                    UpdateStatusText.Text = "Version " + r.LatestVersion + " is available.";
                    UpdateOpenButton.Tag = r.ReleaseUrl;
                    UpdateOpenButton.Visibility = Visibility.Visible;
                }
                else
                {
                    UpdateStatusText.Text = "You have the latest version (" + UpdateChecker.InstalledVersion + ").";
                }
            }
            catch (Exception ex)
            {
                UpdateStatusText.Text = "Update check failed: " + ex.Message;
            }
            finally
            {
                _updateCheckRunning = false;
                if (btn != null) btn.IsEnabled = true;
            }
        }

        private void UpdateOpenButton_Click(object sender, RoutedEventArgs e)
        {
            var b = sender as Button;
            UpdateChecker.OpenUrl(b != null ? b.Tag as string : null);
        }
    }
}
