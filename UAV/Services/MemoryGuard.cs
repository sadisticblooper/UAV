using System;
using System.Threading.Tasks;

namespace UAV.Services;

public static class MemoryGuard
{
    public const long ThresholdBytes = 400L * 1024 * 1024; // 800 MB

    public static long GetAvailableBytes()
    {
#if ANDROID
        try
        {
            var activityManager =
                Android.App.Application.Context.GetSystemService(
                    Android.Content.Context.ActivityService)
                as Android.App.ActivityManager;

            if (activityManager != null)
            {
                var mi = new Android.App.ActivityManager.MemoryInfo();
                activityManager.GetMemoryInfo(mi);
                return mi.AvailMem; 
            }
        }
        catch { /* fall through to GC fallback */ }
#endif
       
        var gcInfo = GC.GetGCMemoryInfo();
        long totalAvailable = gcInfo.TotalAvailableMemoryBytes;
        long alreadyUsed    = GC.GetTotalMemory(false);
        return Math.Max(0L, totalAvailable - alreadyUsed);
    }

   
    public static bool IsMemoryLow() => GetAvailableBytes() < ThresholdBytes;

    public static async Task<bool> TryRecoverAsync(int retries = 3, int delayMs = 600)
    {
        for (int i = 0; i < retries; i++)
        {
            if (!IsMemoryLow()) return true;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive,
                       blocking: true, compacting: true);
            await Task.Delay(delayMs);
        }
        return !IsMemoryLow();
    }
    
    //returns numbr to actually know how much ram is left
    public static string GetStatusString()
    {
        long free = GetAvailableBytes();
        return free >= 1024L * 1024 * 1024
            ? $"{free / (1024.0 * 1024 * 1024):F1} GB free"
            : $"{free / (1024.0 * 1024):F0} MB free";
    }
}
