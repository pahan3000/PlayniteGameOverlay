using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace PlayniteGameOverlay
{
    public class GameStateManager
    {
        private readonly Logger _logger;

        public GameStateManager(Logger logger)
        {
            _logger = logger;
        }

        public Process FindProcessById(int processId)
        {
            try
            {
                return Process.GetProcessById(processId);
            }
            catch
            {
                return null;
            }
        }

        public void ReturnToGame(GameOverlayData gameData)
        {
            if (gameData != null)
            {
                var proc = FindProcessById(gameData.ProcessId);
                if (proc != null)
                {
                    // Restore the window if it's minimized
                    if (WindowHelper.IsIconic(proc.MainWindowHandle))
                    {
                        WindowHelper.ShowWindow(proc.MainWindowHandle, WindowHelper.SW_RESTORE);
                    }
                    // Bring the game window to the front
                    WindowHelper.SetForegroundWindow(proc.MainWindowHandle);
                }
            }
        }

        public async void CloseGame(GameOverlayData gameData, CloseBehavior behavior = CloseBehavior.CloseAndEnd)
        {
            if (gameData == null)
                return;

            // Always unfreeze first. CloseMainWindow on an NtSuspended process
            // never returns and freezes Playnite's UI thread. Resuming then
            // immediately killing a DX12/UE5 process often causes "GPU Crash dump".
            try { WindowHelper.SoftResumeFromOverlay(); } catch { }
            try { WindowHelper.ForceClearAllSuspendState(); } catch { }
            // Settle so the GPU/driver can leave the suspended frame cleanly
            try { await Task.Delay(350); } catch { }

            var proc = FindProcessById(gameData.ProcessId);
            if (proc == null)
                return;

            try
            {
                if (proc.HasExited)
                {
                    try { proc.Close(); } catch { }
                    return;
                }

                // Prefer a graceful WM_CLOSE so the engine can tear down the GPU device.
                // Hard Kill is a last resort — it commonly triggers GPU crash reporters
                // (UE5 "GPU Crash dump Triggered") after NtSuspend/Resume.
                bool success = false;
                try
                {
                    var closeTask = Task.Run(() =>
                    {
                        try { return proc.CloseMainWindow(); }
                        catch { return false; }
                    });
                    var completed = await Task.WhenAny(closeTask, Task.Delay(2500));
                    if (completed == closeTask)
                        success = closeTask.Result;
                    else
                        _logger.Log($"CloseMainWindow timed out for PID {gameData.ProcessId}.", "WARNING");
                }
                catch (Exception ex)
                {
                    _logger.Log($"CloseMainWindow failed: {ex.Message}", "WARNING");
                }

                switch (behavior)
                {
                    case CloseBehavior.CloseAndEnd:
                        // Wait longer for a graceful exit before Kill
                        for (int i = 0; i < 50 && !proc.HasExited; i++)
                        {
                            await Task.Delay(100);
                        }
                        if (!proc.HasExited)
                        {
                            _logger.Log($"Process {proc.ProcessName} still running after graceful close — kill as last resort.", "WARNING");
                            try { proc.Kill(); } catch { }
                            try { await Task.Delay(200); } catch { }
                        }
                        try { proc.Close(); } catch { }
                        break;
                    case CloseBehavior.CloseWindow:
                        if (!success && !proc.HasExited)
                        {
                            for (int i = 0; i < 20 && !proc.HasExited; i++)
                                await Task.Delay(100);
                            if (!proc.HasExited)
                            {
                                try { proc.Kill(); } catch { }
                            }
                        }
                        try { proc.Close(); } catch { }
                        break;
                    case CloseBehavior.EndTask:
                        // Still resume+settle above; Kill is intentional for this behavior
                        try { if (!proc.HasExited) proc.Kill(); } catch { }
                        try { proc.Close(); } catch { }
                        break;
                    default:
                        _logger.Log($"Unknown close behavior: {behavior}", "ERROR");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"CloseGame failed: {ex.Message}", "ERROR");
                try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            }
        }

        public string FormatPlaytime(TimeSpan playtime)
        {
            if (playtime.TotalMinutes < 120)
                return $"{(int)playtime.TotalMinutes} mins.";

            return $"{(int)playtime.TotalHours} hrs.";
        }

        public string GetFormattedSessionTime(DateTime gameStartTime)
        {
            var playTime = DateTime.Now - gameStartTime;
            return $"{playTime.Hours}:{playTime.Minutes:D2}:{playTime.Seconds:D2}";
        }

        public AchievementInfo GetMostRecentAchievement(GameOverlayData gameData)
        {
            if (gameData?.Achievements == null || gameData.Achievements.Count == 0)
                return null;

            return new AchievementInfo
            {
                MostRecent = gameData.Achievements.FindAll(a => a.IsUnlocked).OrderByDescending(a => a.UnlockDate).FirstOrDefault(),
                TotalCount = gameData.Achievements.Count,
                UnlockedCount = gameData.Achievements.Count(a => a.IsUnlocked)
            };
        }
    }

    public class AchievementInfo
    {
        public AchievementData MostRecent { get; set; }
        public int TotalCount { get; set; }
        public int UnlockedCount { get; set; }
    }
}