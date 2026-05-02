using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace UAV.Services;

public readonly struct AssetDescriptor
{
    public readonly long   PathId;
    public readonly int    BundleSlot;
    public readonly int    SubFileIndex;
    public readonly long   ByteSize;
    public readonly string Type;   
    public readonly string Name;

    public AssetDescriptor(long pathId, int bundleSlot, int subFileIndex,
                           long byteSize, string type, string name)
    {
        PathId       = pathId;
        BundleSlot   = bundleSlot;
        SubFileIndex = subFileIndex;
        ByteSize     = byteSize;
        Type         = string.Intern(type);
        Name         = name;
    }
}

internal sealed class BundleSlot
{
    public readonly string  DisplayName;
    public          string  FilePath;
    public readonly string? SafUri;

    public AssetsFileInstance?[] OpenFiles   = Array.Empty<AssetsFileInstance?>();
    public AssetsManager?        OpenManager;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BundleSlot(string displayName, string filePath, string? safUri = null)
    {
        DisplayName = displayName;
        FilePath    = filePath;
        SafUri      = safUri;
    }

    public async Task<AssetsFileInstance?> GetOrOpenSubFileAsync(int subFileIndex)
    {
        await _lock.WaitAsync();
        try
        {
            if (OpenManager == null)
            {
#if ANDROID
                if (SafUri != null)
                {
                    OpenManager = new AssetsManager();
                    using var stream = AndroidDownloadService.OpenSafStream(SafUri);
                    var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    var bundle = OpenManager.LoadBundleFile(ms, FilePath, unpackIfPacked: true);
                    int count  = bundle.file.BlockAndDirInfo.DirectoryInfos.Count;
                    OpenFiles  = new AssetsFileInstance?[count];
                    for (int i = 0; i < count; i++)
                    {
                        try { OpenFiles[i] = OpenManager.LoadAssetsFileFromBundle(bundle, i); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BundleRegistry] Failed to load sub-file {i}: {ex.Message}"); }
                    }
                    return subFileIndex >= 0 && subFileIndex < OpenFiles.Length
                        ? OpenFiles[subFileIndex] : null;
                }
#endif
                OpenManager = new AssetsManager();
                var bundleFile = OpenManager.LoadBundleFile(FilePath);
                int fileCount  = bundleFile.file.BlockAndDirInfo.DirectoryInfos.Count;
                OpenFiles      = new AssetsFileInstance?[fileCount];
                for (int i = 0; i < fileCount; i++)
                {
                    try { OpenFiles[i] = OpenManager.LoadAssetsFileFromBundle(bundleFile, i); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BundleRegistry] Failed to load sub-file {i} in bundle: {ex.Message}"); }
                }
            }
            return subFileIndex >= 0 && subFileIndex < OpenFiles.Length
                ? OpenFiles[subFileIndex] : null;
        }
        finally { _lock.Release(); }
    }

    public void Release()
    {
        _lock.Wait();
        try
        {
            try { OpenManager?.UnloadAll(true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BundleRegistry] Release failed: {ex.Message}"); }
            OpenFiles   = Array.Empty<AssetsFileInstance?>();
            OpenManager = null;
        }
        finally { _lock.Release(); }
    }
}


internal static class FontTypeGuard
{
    private static readonly HashSet<string> BlockedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Font", "TMP_FontAsset", "TMP_SpriteAsset", "TextMeshProFont", "FontDef", "GUISkin",
    };

    public static bool IsBlocked(string typeName) => BlockedTypes.Contains(typeName);
}

public sealed class BundleRegistry : IDisposable
{
    private readonly List<BundleSlot>  _slots       = new();
    private          AssetDescriptor[] _descriptors = Array.Empty<AssetDescriptor>();
    private          int               _count       = 0;
    private          int               _scansSinceLastGc = 0;

    public ReadOnlySpan<AssetDescriptor> All   => _descriptors.AsSpan(0, _count);
    public int                           Count => _count;
    public List<string> ScanErrors { get; } = new();
    public List<string> SkippedFontBundles { get; } = new();

    public void Clear()
    {
        foreach (var s in _slots) s.Release();
        _slots.Clear();
        _descriptors = Array.Empty<AssetDescriptor>();
        _count = 0;
        _scansSinceLastGc = 0;
        ScanErrors.Clear();
        SkippedFontBundles.Clear();
    }

    public void ScanFile(string filePath, string displayName, string? safUri = null)
    {
        int slotIndex   = _slots.Count;
        int countBefore = _count;

        //  dedicated manager for scanning because why not
        var mgr = new AssetsManager();
        try
        {
            bool any = false;
            try
            {
#if ANDROID
                if (safUri != null)
                {
                    MemoryStream? ms = null;
                    try
                    {
                        using var stream = AndroidDownloadService.OpenSafStream(safUri);
                        ms = new MemoryStream();
                        stream.CopyTo(ms);
                        ms.Position = 0;
                        var bundle = mgr.LoadBundleFile(ms, filePath, unpackIfPacked: true);
                        var dirs   = bundle.file.BlockAndDirInfo.DirectoryInfos;
                        for (int i = 0; i < dirs.Count; i++)
                        {
                            try
                            {
                                var af = mgr.LoadAssetsFileFromBundle(bundle, i);
                                if (af?.file?.AssetInfos == null) continue;
                                ScanSubFile(af, mgr, slotIndex, i);
                                any = true;
                            }
                            catch (InvalidOperationException ioe) when (ioe.Message.StartsWith("FONT_BUNDLE:"))
                            {
                                _count = countBefore;
                                SkippedFontBundles.Add(displayName);
                                return;
                            }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BundleRegistry] Failed to scan sub-file {i}: {ex.Message}"); }
                        }
                    }
                    finally { ms?.Dispose(); }
                }
                else
#endif
                {
                    var bundle = mgr.LoadBundleFile(filePath);
                    var dirs   = bundle.file.BlockAndDirInfo.DirectoryInfos;
                    for (int i = 0; i < dirs.Count; i++)
                    {
                        try
                        {
                            var af = mgr.LoadAssetsFileFromBundle(bundle, i);
                            if (af?.file?.AssetInfos == null) continue;
                            ScanSubFile(af, mgr, slotIndex, i);
                            any = true;
                        }
                        catch (InvalidOperationException ioe) when (ioe.Message.StartsWith("FONT_BUNDLE:"))
                        {
                            _count = countBefore;
                            SkippedFontBundles.Add(displayName);
                            return;
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BundleRegistry] Failed to scan sub-file {i}: {ex.Message}"); }
                    }
                }
            }
            catch (InvalidOperationException fontEx) when (fontEx.Message.StartsWith("FONT_BUNDLE:"))
            {
                _count = countBefore;
                SkippedFontBundles.Add(displayName);
                return;
            }
            catch (Exception bundleEx)
            {
                try
                {
                    var af = mgr.LoadAssetsFile(filePath);
                    if (af?.file?.AssetInfos != null)
                    {
                        ScanSubFile(af, mgr, slotIndex, 0);
                        any = true;
                    }
                }
                catch (InvalidOperationException fontEx2) when (fontEx2.Message.StartsWith("FONT_BUNDLE:"))
                {
                    _count = countBefore;
                    SkippedFontBundles.Add(displayName);
                    return;
                }
                catch (Exception assetsEx)
                {
                    ScanErrors.Add($"{displayName}: {bundleEx.Message} | {assetsEx.Message}");
                    _count = countBefore;
                    return;
                }
            }

            if (!any || _count == countBefore) { _count = countBefore; return; }
        }
        catch (Exception ex)
        {
            ScanErrors.Add($"{displayName}: {ex.Message}");
            _count = countBefore;
            return;
        }
        finally
        {
            try { mgr.UnloadAll(true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BundleRegistry] Manager unload failed: {ex.Message}"); }

            _scansSinceLastGc++;
            if (_scansSinceLastGc >= 10)   // gc count here
            {
                _scansSinceLastGc = 0;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive,
                           blocking: true, compacting: true);
            }
            else
            {
                GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
            }
        }

        _slots.Add(new BundleSlot(displayName, filePath, safUri));
    }

    private void ScanSubFile(AssetsFileInstance af, AssetsManager mgr, int slotIndex, int subFile)
    {
        var infos = af.file.AssetInfos;

        var typeCache = new Dictionary<int, (string TypeName, bool HasName)>();

        EnsureCapacity(_count + infos.Count);

        int gcCounter = 0;
        foreach (var info in infos)
        {
            string type    = "Unknown";
            string name    = "";
            bool   hasName = false;

            if (!typeCache.TryGetValue(info.TypeId, out var tc))
            {
                try
                {
                    var probe = mgr.GetBaseField(af, info);
                    tc = (probe.TypeName ?? "Unknown", probe["m_Name"] is { IsDummy: false });
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BundleRegistry] Type probe failed for {info.TypeId}: {ex.Message}"); tc = ("Unknown", false); }
                typeCache[info.TypeId] = tc;
            }

            if (FontTypeGuard.IsBlocked(tc.TypeName))
                throw new InvalidOperationException("FONT_BUNDLE:" + tc.TypeName);

            type    = tc.TypeName;
            hasName = tc.HasName;

            if (hasName)
            {
                try
                {
                    var bf        = mgr.GetBaseField(af, info);
                    var nameField = bf["m_Name"];
                    name = nameField.IsDummy ? "" : (nameField.AsString ?? "");
                    bf   = null!; // drop ref so GC can collect
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BundleRegistry] Name field read failed: {ex.Message}"); }

                if (++gcCounter % 2000 == 0)
                    GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
            }

            _descriptors[_count++] = new AssetDescriptor(
                info.PathId, slotIndex, subFile, info.ByteSize, type, name);
        }
    }

    public Task<AssetsFileInstance?> GetLiveFileAsync(in AssetDescriptor desc)
    {
        if (desc.BundleSlot < 0 || desc.BundleSlot >= _slots.Count)
            return Task.FromResult<AssetsFileInstance?>(null);
        return _slots[desc.BundleSlot].GetOrOpenSubFileAsync(desc.SubFileIndex);
    }

    public AssetsManager? GetOpenManager(int slot)
        => slot >= 0 && slot < _slots.Count ? _slots[slot].OpenManager : null;

    public string GetBundleName(int slot)
        => slot >= 0 && slot < _slots.Count ? _slots[slot].DisplayName : "";

    public void ReleaseSlot(int slot)
    {
        if (slot >= 0 && slot < _slots.Count)
            _slots[slot].Release();
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _descriptors.Length) return;
        int newSize = Math.Max(needed, Math.Max(1024, _descriptors.Length * 2));
        Array.Resize(ref _descriptors, newSize);
    }

    public void Dispose() => Clear();
}
