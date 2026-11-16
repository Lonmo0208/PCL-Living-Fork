using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using MeloongCore.Extensions;
using Newtonsoft.Json.Linq;

namespace PCLCS;

public sealed partial class LocalResourceLoaderEngine<TLocal, TProject, TVersion>
    where TLocal : LocalResourceFileData<TProject, TVersion>
    where TProject : ResourceProjectModelData where TVersion : ResourceVersionModelData {
    private void LoadModrinth(List<TLocal> mods, string vanillaVersion, ModLoaders targetLoaders, LocalResourceOnlineTask<TLocal> task, Thread currentTaskThread) {
        var hashes = mods.Select(m => m.ModrinthHash).ToList();
        JObject versions = (JObject)host.Request("https://api.modrinth.com/v2/version_files", HttpMethod.Post,
            "{\"hashes\": [\"" + string.Join("\",\"", hashes) + "\"], \"algorithm\": \"sha1\"}", "application/json");
        host.Info($"从 Modrinth 获取到 {versions.Count} 个本地 Mod 的对应信息");
        if (versions.Count == 0) return;
        Dictionary<string, List<TLocal>> mapping = new();
        Dictionary<TLocal, List<string>> loaders = new();
        Dictionary<TLocal, TVersion> installed = new();
        foreach (TLocal entry in mods) {
            if (!versions.ContainsKey(entry.ModrinthHash)) continue;
            JToken? matchingFile = versions[entry.ModrinthHash]!["files"]?.FirstOrDefault(file=>(string?)file["hashes"]?["sha1"]==entry.ModrinthHash);
            if (matchingFile is null) continue;
            string projectId = versions[entry.ModrinthHash]!["project_id"]!.ToString();
            string key = ResourceProjectKey.Get(ResourcePlatforms.Modrinth, projectId);
            if (host.ProjectCache().TryGetValue(key, out TProject? cached) && cached.Platform == ResourcePlatforms.Modrinth && entry.Project is null) entry.Project = cached;
            if (!mapping.ContainsKey(projectId)) mapping[projectId] = new List<TLocal>();
            mapping[projectId].Add(entry);
            JObject matchedVersion=(JObject)versions[entry.ModrinthHash]!.DeepClone();
            matchedVersion["files"]=new JArray(matchingFile.DeepClone());
            TVersion file = host.ReadVersion(matchedVersion, ResourceTypes.Mod);
            installed[entry]=file;
            loaders[entry] = targetLoaders==ModLoaders.None ? versions[entry.ModrinthHash]!["loaders"]!.Select(l => l.ToString().Lower()).Distinct().OrderBy(l => l).ToList()
                : Enum.GetValues<ModLoaders>().Where(loader=>loader!=ModLoaders.None && loader!=ModLoaders.All && (loader & targetLoaders)!=0)
                    .Select(loader=>loader.ToString().ToLowerInvariant()).ToList();
            if (entry.ProjectVersion is null || entry.ProjectVersion.ReleaseDate < file.ReleaseDate) entry.ProjectVersion = file;
            else entry.ProjectVersion.Version = file.Version;
        }
        if (task.IsCanceledWithThread(currentTaskThread)) return;
        host.Info($"需要从 Modrinth 获取 {mapping.Count} 个本地 Mod 的工程信息");
        if (!mapping.Any()) return;
        JArray projects = (JArray)host.Request("https://api.modrinth.com/v2/projects?ids=[\"" + string.Join("\",\"", mapping.Keys) + "\"]", null, null, null);
        foreach (JToken json in projects) {
            TProject project = host.CreateProject((JObject)json);
            foreach (TLocal entry in mapping[project.Id!]) entry.Project = project;
        }
        host.Info("已从 Modrinth 获取本地 Mod 信息，继续获取更新信息");
        var toUpdate = mapping.SelectMany(l => l.Value).ToList();
        if (!toUpdate.Any()) return;
        // 双加载器实例（NeoForge + Fabric）要分别问 NeoForge 与 Fabric 的最新版本，再按
        // “NeoForge 优先、Fabric 版本号更高才用 Fabric”的规则挑选——Modrinth 的单次查询只会
        // 返回两个加载器里发布时间最晚的那一个，版本号高低它不管。
        bool dualLoader = HasBothNeoAndFabric(targetLoaders);
        string UpdateBody(IEnumerable<TLocal> entries, Func<TLocal, IEnumerable<string>> loaderPicker) =>
            "{\"algorithm\": \"sha1\", \"hashes\": [" + string.Join(",", entries.Select(m =>
                "{\"hash\": \"" + m.ModrinthHash + "\", \"loaders\": [\"" + string.Join("\",\"", loaderPicker(m)) + "\"], \"game_versions\": [\"" + vanillaVersion + "\"]}")) + "]}";
        JObject updates = (JObject)host.Request("https://api.modrinth.com/v2/version_files/update_individual", HttpMethod.Post,
            UpdateBody(toUpdate, m => dualLoader && loaders[m].Any(loader => loader != "fabric") ? loaders[m].Where(loader => loader != "fabric") : loaders[m]), "application/json");
        JObject? fabricUpdates = dualLoader
            ? (JObject)host.Request("https://api.modrinth.com/v2/version_files/update_individual", HttpMethod.Post,
                UpdateBody(toUpdate, _ => new[] { "fabric" }), "application/json")
            : null;
        // 单个候选的校验，返回版本号与工程 ID（更新日志链接要用）。
        (TVersion Version, string ProjectId)? Candidate(JObject source, TLocal entry, TVersion current) {
            if (!source.ContainsKey(entry.ModrinthHash)) return null;
            JObject raw = (JObject)source[entry.ModrinthHash]!;
            if (raw["game_versions"]?.Any(version => version.ToString() == vanillaVersion) != true) return null;
            TVersion candidate = host.ReadVersion(raw, ResourceTypes.Mod);
            if (!candidate.DownloadAvailable) return null;
            if (!SupportsUpdateLoader(candidate.ModLoaders, targetLoaders, current.ModLoaders)) return null;
            return (candidate, raw["project_id"]?.ToString() ?? "");
        }
        foreach (TLocal entry in mods) {
            if (!installed.TryGetValue(entry, out TVersion? current)) continue;
            (TVersion Version, string ProjectId)? preferred = Candidate(updates, entry, current);
            (TVersion Version, string ProjectId)? selected = preferred;
            if (fabricUpdates is not null) {
                (TVersion Version, string ProjectId)? fabric = Candidate(fabricUpdates, entry, current);
                TVersion? chosen = ChooseDualLoaderUpdate(preferred?.Version, fabric?.Version);
                selected = chosen is null ? null : chosen == fabric?.Version ? fabric : preferred;
            }
            if (selected is null) continue;
            TVersion update = selected.Value.Version;
            host.Trace(() => $"本地文件 {current.FileName} 在 Modrinth 上的最新版为 {update.FileName}");
            if (current.ReleaseDate >= update.ReleaseDate || (current.Hash ?? "") == (update.Hash ?? "")) continue;
            string changelog = $"https://modrinth.com/mod/{selected.Value.ProjectId}/changelog?g={vanillaVersion}";
            if (entry.UpdateFile is not null && (update.Hash ?? "") == (entry.UpdateFile.Hash ?? "")) {
                entry.ChangelogUrls.Add(changelog);
                update.DownloadUrls!.AddRange(entry.UpdateFile.DownloadUrls!);
                entry.UpdateFile = update;
            } else if (entry.UpdateFile is null || update.ReleaseDate >= entry.UpdateFile.ReleaseDate) {
                entry.ChangelogUrls = new List<string> { changelog };
                entry.UpdateFile = update;
            }
        }
        host.Info("从 Modrinth 获取本地 Mod 信息结束");
    }
}
