using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PCLCS;

// LF LocalResourceLoaders.vb at 50b028da5fcad2226d74d901a41d8c43a249bfb2; base matches
// PCL 5ef81e490ae46b1100d61e33d0183094c7a80ca8, LF LICENCE. Fixed CE stable
// 1a7653127d578efdb2a9388999a3fdc67d9e49d5 ModLocalComp.cs reviewed only:
// retain LF's two worker threads, Loader thread-identity cancellation and cache schema.
public sealed partial class LocalResourceLoaderEngine<TLocal, TProject, TVersion>
    where TLocal : LocalResourceFileData<TProject, TVersion>
    where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData {
    private readonly LocalResourceLoaderHost<TLocal, TProject, TVersion> host;
    public LocalResourceLoaderEngine(LocalResourceLoaderHost<TLocal, TProject, TVersion> host) { this.host = host; }
    public void LoadFiles(LocalResourceFileTask<TLocal> task) {
        try {
            host.SetShowProgress(false);
            if (host.IsUpdatingFolder(task.Input())) {
                host.Info($"等待 Mod 更新完成后才能继续加载 Mod 列表：{task.Input()}");
                try {
                    host.SetLoadText("正在更新 Mod");
                    while (host.IsUpdatingFolder(task.Input())) {
                        if (task.IsCanceled()) return;
                        Thread.Sleep(100);
                    }
                } finally { host.SetLoadText("正在加载 Mod 列表"); }
                host.ReloadFolder();
            }
            List<string> modFiles = new();
            if (DirectoryUtils.Exists(task.Input())) {
                foreach (string file in DirectoryUtils.EnumerateFiles(task.Input(), true)) {
                    if (!PathUtils.IsSame(PathUtils.RemoveLastPart(file), task.Input())) {
                        // Read the actual selected version only when testing a nested file, as LF did.
                        McVersion? version = host.CurrentVersion();
                        if (!(version is not null && version.ModLoaders.HasFlagF(ModLoaders.Forge) && version.Vanilla.Major < 13 &&
                            PathUtils.GetLastPart(PathUtils.RemoveLastPart(file)) == $"1.{version.Vanilla.Major}.{version.Vanilla.Build}")) continue;
                    }
                    if (IsModFile(file)) modFiles.Add(file);
                }
            }
            string cachePath = host.TempPath() + @"Cache\LocalMod.json";
            JObject cache = new();
            try {
                string? content = FileUtils.TryReadAsString(cachePath);
                if (!string.IsNullOrWhiteSpace(content)) {
                    cache = (JObject)content!.DeserializeJson()!;
                    if (!cache.ContainsKey("version") || cache["version"]!.ToObject<int>() != host.CacheVersion()) {
                        host.Warning("本地 Mod 信息缓存版本已过期，将弃用这些缓存信息"); cache = new JObject();
                    }
                }
            } catch (Exception ex) { host.Warn(ex, "读取本地 Mod 信息缓存失败，已重置"); cache = new JObject(); }
            cache["version"] = host.CacheVersion();
            List<TLocal> mods = new();
            foreach (string file in modFiles) {
                if (task.IsCanceled()) return;
                TLocal entry = host.CreateLocal(file);
                mods.Add(entry);
            }
            host.Info($"共发现 {mods.Count} 个 Mod");
            mods = mods.OrderBy(m => m.File.Name).ToList();
            LocalResourceCompatibilityContext? compatibility = host.ReadCompatibility();
            if (compatibility is not null && PathUtils.IsSame(task.Input(), compatibility.Folder)) {
                try {
                    if (compatibility.ReadIds is null) compatibility.Inspect(task.Input(), task.CreateCancellationToken());
                    else {
                        var identities = compatibility.ReadIds(task.Input(), task.CreateCancellationToken());
                        foreach (var mod in mods)
                            if (identities.TryGetValue(mod.StorageKey,out var ids)) mod.DeclaredModIds = ids;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { host.Warn(ex, "检查 Mod 列表的加载器兼容性失败，仍显示本地文件"); }
            }
            if (task.IsCanceled()) return;
            task.SetOutput(mods);
            host.StartOnline(mods, cache, host.CurrentVersion() ?? throw new NullReferenceException());
        } catch (Exception ex) { host.Warn(ex, "Mod 列表加载失败"); throw; }
    }
    private static bool IsModFile(string? fullName) {
        if (fullName is null) return false;
        if (new[] { "disabled", "old" }.Contains(PathUtils.GetExtension(fullName))) fullName = PathUtils.GetFileNameWithoutExtension(fullName);
        return new[] { "jar", "zip", "litemod" }.Contains(PathUtils.GetExtension(fullName));
    }
    public void LoadOnline(LocalResourceOnlineTask<TLocal> task) {
        List<TLocal> mods = new();
        JObject cache = task.Input().Cache;
        ModLoaders loaders = task.Input().Target.ModLoaders;
        foreach (TLocal entry in task.Input().ModList) {
            if (task.IsCanceled()) return;
            string prefix = entry.ModrinthHash + task.Input().Target.VanillaName;
            string key = prefix + ((int)loaders).ToString(CultureInfo.InvariantCulture);
            JToken? cached = cache[key] ?? cache[prefix + loaders];
            if (cached is not null) {
                // VB '& enum' persisted its numeric value. Also preserve caches
                // written by the early C# migration using enum names.
                if (cache[key] is null) cache[key] = cached.DeepClone();
                entry.FromJson((JObject)cached);
                if (entry.OnlineDataLoaded && DateTime.Now - cached["Project"]!["CacheTime"]!.ToObject<DateTime>() < new TimeSpan(6, 0, 0)) continue;
            }
            mods.Add(entry);
        }
        host.Info($"有 {mods.Count(m => m.Project is null)} 个 Mod 需要联网获取信息，{mods.Count(m => m.Project is not null)} 个 Mod 需要更新信息");
        if (!mods.Any()) return;
        McVersion target = task.Input().Target;
        string vanillaVersion = target.VanillaName;
        host.Info($"正在检查 Mod 信息的版本：{vanillaVersion}，加载器 {loaders}");
        int endedThreadCount = 0;
        bool failed = false;
        Thread currentTaskThread = Thread.CurrentThread;
        host.StartThread(() => {
            try { LoadModrinth(mods, vanillaVersion, loaders, task, currentTaskThread); }
            catch (Exception ex) { host.Warn(ex, "从 Modrinth 获取本地 Mod 信息失败"); failed = true; }
            finally { endedThreadCount += 1; }
        }, "Mod List Detail Loader Modrinth");
        host.StartThread(() => {
            try { LoadCurseForge(mods, vanillaVersion, loaders, task, currentTaskThread); }
            catch (Exception ex) { host.Warn(ex, "从 CurseForge 获取本地 Mod 信息失败"); failed = true; }
            finally { endedThreadCount += 1; }
        }, "Mod List Detail Loader CurseForge");
        // Preserve LF's counter/update publication and return-with-workers cancellation behavior.
        while (endedThreadCount != 2) { Thread.Sleep(10); if (task.IsCanceled()) return; }
        mods = mods.Where(m => m.Project is not null).ToList();
        host.Info($"联网获取本地 Mod 信息完成，为 {mods.Count} 个 Mod 更新缓存");
        if (!mods.Any()) return;
        if (task.IsCanceled()) return;
        foreach (TLocal entry in mods) {
            entry.OnlineDataLoaded = !failed;
            cache[entry.ModrinthHash + vanillaVersion + ((int)loaders).ToString(CultureInfo.InvariantCulture)] = entry.ToJson();
        }
        FileUtils.Write(host.TempPath() + @"Cache\LocalMod.json", cache.ToString(host.DebugMode() ? Formatting.Indented : Formatting.None));
        if (task.IsCanceled()) return;
        if (host.IsUpdateFilter()) host.RefreshUi(); else host.RefreshBars();
    }
    private static ModLoaders UpdateLoaders(ModLoaders target,ModLoaders installed) => target==ModLoaders.None ? installed : target & ModLoaders.All;
    private static bool SupportsUpdateLoader(ModLoaders candidate,ModLoaders target,ModLoaders installed) {
        ModLoaders expected=UpdateLoaders(target,installed);
        return expected==ModLoaders.None ? candidate==ModLoaders.None : (candidate & expected)!=0;
    }
    /// <summary>
    /// 双加载器实例（NeoForge + Fabric，例如 NeoFobric）的更新选择：默认取 NeoForge 候选，只有 Fabric
    /// 候选的版本号更高时才改用 Fabric；任一方缺失时直接使用另一方。版本号相同按 NeoForge 处理。
    /// </summary>
    private static bool HasBothNeoAndFabric(ModLoaders loaders) =>
        (loaders & (ModLoaders.NeoForge | ModLoaders.Fabric)) == (ModLoaders.NeoForge | ModLoaders.Fabric);
    private static TVersion? ChooseDualLoaderUpdate(TVersion? neoForgeCandidate, TVersion? fabricCandidate) {
        if (neoForgeCandidate is null) return fabricCandidate;
        if (fabricCandidate is null) return neoForgeCandidate;
        string? neoForgeVersion = ComparableVersion(neoForgeCandidate);
        string? fabricVersion = ComparableVersion(fabricCandidate);
        // 任一方拿不到版本号就无法比较，按“优先 NeoForge”处理。
        if (neoForgeVersion is null || fabricVersion is null) return neoForgeCandidate;
        return McUtils.CompareVersion(fabricVersion, neoForgeVersion) > 0 ? fabricCandidate : neoForgeCandidate;
    }
    /// <summary>候选的版本号：Modrinth 自带 version_number；CurseForge 文件没有该字段，从显示名/文件名里取。</summary>
    private static string? ComparableVersion(ResourceVersionModelData version) {
        if (!string.IsNullOrWhiteSpace(version.Version) && version.Version != "未知版本") return version.Version;
        string? text = version.Display ?? version.FileName;
        if (string.IsNullOrEmpty(text)) return null;
        // CurseForge 的显示名默认就是文件显示名（如 "journeymap-1.21.1-6.0.0-fabric"）：取最后一个
        // 数字点号串，并跳过与游戏版本完全相同的片段。取不到就返回 null（调用方按 NeoForge 优先）。
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"(?<![0-9A-Za-z.])v?([0-9]+(?:\.[0-9]+)+[A-Za-z0-9.+-]*)");
        for (int i = matches.Count - 1; i >= 0; i--) {
            string candidate = matches[i].Groups[1].Value.TrimEnd('.', '-', '+');
            if (candidate.Length == 0) continue;
            if (version.GameVersions is not null && version.GameVersions.Contains(candidate)) continue;
            return candidate;
        }
        return null;
    }
    /// <summary>保留每个加载器各自的“最新”候选：按发布时间更晚者胜出。</summary>
    private static void KeepNewestCandidate<TKey>(Dictionary<TKey, TVersion> candidates, TKey key, TVersion file) where TKey : notnull {
        if (candidates.TryGetValue(key, out TVersion? existing) && existing.ReleaseDate >= file.ReleaseDate) return;
        candidates[key] = file;
    }
}
