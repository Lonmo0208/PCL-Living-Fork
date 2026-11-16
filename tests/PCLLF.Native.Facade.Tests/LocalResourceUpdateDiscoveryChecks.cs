using System.Net.Http;
using Newtonsoft.Json.Linq;
using PCLCS;
using Local = PCLCS.LocalResourceFileData<PCLCS.ResourceProjectModel,PCLCS.ResourceVersionModel>;

namespace PCLLF.Native.Facade.Tests;

internal static class LocalResourceUpdateDiscoveryChecks {
    internal static void Run(string fixtureRoot) {
        foreach (string scenario in new[] { "mr-narrower", "mr-broader", "mr-wrong-loader", "mr-wrong-game", "mr-secondary-file", "cross-platform-dates", "cf-broader", "cf-wrong-loader", "dual-neo-tie", "dual-fabric-newer", "cf-dual-fabric-newer" }) {
            string folder=Path.Combine(fixtureRoot,"mod-update-discovery",scenario); Directory.CreateDirectory(Path.Combine(folder,"Cache"));
            string file=Path.Combine(folder,"installed.jar"); File.WriteAllText(file,"owned update fixture");
            string installedHash=new('a',40), newHash=new('b',40), neoHash=new('d',40), fabricHash=new('e',40);
            var versionServices=new ResourceVersionServices { MirrorUrl=url=>url,DebugMode=()=>false,ImageRoot=()=>"" };
            ResourceVersionModel ReadVersion(JObject raw,ResourceTypes kind)=>ResourceVersionRules.FromPlatformJson(raw,kind,()=>new ResourceVersionModel(versionServices));
            var projectServices=new ResourceProjectServices { StoreCache=_=>{},ReadVersion=(raw,kind)=>ReadVersion(raw,kind),AllDrops=()=>new List<int>() };
            var services=new LocalResourceFileServices<ResourceProjectModel,ResourceVersionModel> {
                ReadHashCache=key=>key.EndsWith("-C",StringComparison.Ordinal)?"17":installedHash,
                WriteHashCache=(_,_)=>{},ReadBytes=_=>new byte[] {1,2,3},Sha1=_=>installedHash,
                ReadProject=raw=>new ResourceProjectModel(raw,projectServices),ReadVersion=raw=>ResourceVersionRules.FromCacheJson<ResourceVersionModel,ResourceReleaseTypes>(raw,()=>new ResourceVersionModel(versionServices)),
                HideUpdates=()=>false,DisableInstanceUpdates=()=>false
            };
            var entry=new Local(new FileInfo(file),services);
            // The native cache API uses hashed keys, so pin the two computed content hashes only.
            Native.Set(entry,"_ModrinthHash",installedHash); Native.Set(entry,"_CurseForgeHash",(uint?)17);
            bool cfOnly=scenario.StartsWith("cf-",StringComparison.Ordinal);
            // A dual-loader instance (NeoForge platform with a real fabric-loader inside) must prefer the
            // NeoForge candidate unless the Fabric candidate's version number is higher.
            bool dual=scenario.StartsWith("dual-",StringComparison.Ordinal)||scenario=="cf-dual-fabric-newer";
            var oldLoaders=scenario=="mr-narrower"?new JArray("fabric","quilt"):new JArray("fabric");
            var newLoaders=scenario=="mr-broader"?new JArray("fabric","quilt"):scenario=="mr-wrong-loader"?new JArray("forge"):new JArray("fabric");
            JObject oldMr=Mr("old",oldLoaders,installedHash,"2026-10-01T00:00:00Z","1.20.1");
            if(scenario=="mr-secondary-file") {
                ((JArray)oldMr["files"]!).AddFirst(new JObject { ["filename"]="unrelated.jar",["url"]="https://example.invalid/unrelated.jar",["size"]=3,["hashes"]=new JObject { ["sha1"]=new string('c',40) } });
            }
            JObject nextMr=Mr("next",newLoaders,newHash,"2026-10-02T00:00:00Z",scenario=="mr-wrong-game"?"1.21.1":"1.20.1");
            // Modrinth dual case: the NeoForge candidate and the Fabric candidate carry their own versions;
            // the tie keeps the later date on the Fabric side so a date-based pick would be visibly wrong.
            JObject? dualNeoMr=null, dualFabricMr=null;
            if (scenario is "dual-neo-tie" or "dual-fabric-newer") {
                dualNeoMr=Mr("1.0",new JArray("neoforge"),neoHash,"2026-10-02T00:00:00Z","1.20.1");
                dualFabricMr=scenario=="dual-neo-tie"
                    ? Mr("1.0",new JArray("fabric"),fabricHash,"2026-10-03T00:00:00Z","1.20.1")
                    : Mr("1.1",new JArray("fabric"),fabricHash,"2026-10-02T00:00:00Z","1.20.1");
            }
            JObject oldCf=Cf("100001",new JArray("1.20.1",cfOnly?"Forge":"Fabric"),installedHash,scenario=="cross-platform-dates"?"2026-10-03T00:00:00Z":"2026-10-01T00:00:00Z");
            JObject nextCf=Cf("100002",scenario=="cf-broader"?new JArray("1.20.1","Forge","NeoForge"):new JArray("1.20.1","NeoForge"),newHash,"2026-10-04T00:00:00Z");
            var target=new McVersion("1.20.1",true);
            if (dual) { target.ModLoaderVersions[ModLoaders.NeoForge]="fixture-neo"; target.ModLoaderVersions[ModLoaders.Fabric]="fixture-fabric"; }
            else target.ModLoaderVersions[cfOnly?ModLoaders.Forge:ModLoaders.Fabric]="fixture";
            var warnings=new List<Exception>(); var queued=new List<Action>(); var requests=new List<(string Url,string? Body)>();
            JToken Request(string url,HttpMethod? method,string? body,string? contentType) {
                requests.Add((url,body));
                if(url=="https://api.modrinth.com/v2/version_files")return cfOnly?new JObject():new JObject { [installedHash]=oldMr };
                if(url.StartsWith("https://api.modrinth.com/v2/projects?",StringComparison.Ordinal))return new JArray(new JObject {
                    ["id"]="fixture-project",["slug"]="fixture",["title"]="Fixture",["description"]="Fixture",["updated"]="2026-10-02T00:00:00Z",["downloads"]=1,
                    ["icon_url"]="",["project_type"]="mod",["categories"]=new JArray(),["game_versions"]=new JArray("1.20.1") });
                if(url=="https://api.modrinth.com/v2/version_files/update_individual") {
                    if(dualNeoMr is not null) {
                        bool fabricQuery=body is not null && body.Contains("\"loaders\": [\"fabric\"]",StringComparison.Ordinal);
                        return new JObject { [installedHash]=fabricQuery?dualFabricMr!:dualNeoMr };
                    }
                    return new JObject { [installedHash]=nextMr };
                }
                if(url=="https://api.curseforge.com/v1/fingerprints/432")return new JObject { ["data"]=new JObject { ["exactMatches"]=cfOnly||scenario=="cross-platform-dates"?new JArray(new JObject { ["id"]=123,["file"]=oldCf }):new JArray() } };
                if(url=="https://api.curseforge.com/v1/mods") {
                    JArray indexes;
                    if (scenario=="cf-dual-fabric-newer") indexes=new JArray(
                        new JObject { ["gameVersion"]="1.20.1",["modLoader"]=6,["fileId"]=100002 },
                        new JObject { ["gameVersion"]="1.20.1",["modLoader"]=4,["fileId"]=100003 });
                    else if (cfOnly) indexes=new JArray(new JObject { ["gameVersion"]="1.20.1",["modLoader"]=1,["fileId"]=100002 });
                    else indexes=new JArray();
                    return new JObject { ["data"]=new JArray(new JObject {
                        ["id"]=123,["slug"]="fixture",["name"]="Fixture",["summary"]="Fixture",["dateReleased"]="2026-10-03T00:00:00Z",["downloadCount"]=1,["logo"]=new JObject(),["categories"]=new JArray(),
                        ["links"]=new JObject { ["websiteUrl"]="https://www.curseforge.com/minecraft/mc-mods/fixture" },
                        ["latestFilesIndexes"]=indexes }) };
                }
                if(url=="https://api.curseforge.com/v1/mods/files") {
                    if (scenario=="cf-dual-fabric-newer") return new JObject { ["data"]=new JArray(
                        // NeoForge 1.0 is published later; Fabric 1.1 still wins on version number.
                        Cf("100002",new JArray("1.20.1","NeoForge"),neoHash,"2026-10-05T00:00:00Z","Fixture 1.0"),
                        Cf("100003",new JArray("1.20.1","Fabric"),fabricHash,"2026-10-03T00:00:00Z","Fixture 1.1")) };
                    return new JObject { ["data"]=new JArray(nextCf) };
                }
                throw new InvalidOperationException("Unexpected update request: "+url);
            }
            var host=new LocalResourceLoaderHost<Local,ResourceProjectModel,ResourceVersionModel> {
                Request=Request,ProjectCache=()=>new(),CreateProject=raw=>new ResourceProjectModel(raw,projectServices),ReadVersion=ReadVersion,
                StartThread=(action,_)=> { queued.Add(action); if(queued.Count==2) { if(scenario=="cross-platform-dates") { queued[1]();queued[0](); } else { queued[0]();queued[1](); } } return Thread.CurrentThread; },
                TempPath=()=>folder+Path.DirectorySeparatorChar,DebugMode=()=>false,CacheVersion=()=>Versions.LocalModCacheVersion,
                Info=_=>{},Warning=_=>{},WarnQuiet=_=>{},Trace=_=>{},Warn=(error,_)=>warnings.Add(error),IsUpdateFilter=()=>false,RefreshBars=()=>{},RefreshUi=()=>{}
            };
            new LocalResourceLoaderEngine<Local,ResourceProjectModel,ResourceVersionModel>(host).LoadOnline(new LocalResourceOnlineTask<Local> {
                Input=()=>(new List<Local> {entry},new JObject { ["version"]=Versions.LocalModCacheVersion },target),IsCanceled=()=>false,IsCanceledWithThread=_=>false });
            Native.Check(warnings.Count==0,"Update discovery failed for "+scenario+": "+string.Join(";",warnings.Select(error=>error.Message)));
            bool expected=scenario is not ("mr-wrong-loader" or "mr-wrong-game" or "cf-wrong-loader");
            Native.Check(entry.HasUpdate==expected,"Update detection for "+scenario+" expected "+expected+" but found "+entry.HasUpdate);
            string? expectedHash=scenario switch {
                "dual-neo-tie" => neoHash,
                "dual-fabric-newer" or "cf-dual-fabric-newer" => fabricHash,
                _ => newHash
            };
            if(expected)Native.Check(entry.UpdateFile?.Hash==expectedHash,"Update candidate for "+scenario+" is not the expected loader's file (got "+(entry.UpdateFile?.FileName??"none")+")");
            if(scenario is "dual-neo-tie" or "dual-fabric-newer") {
                var bodies=requests.Where(request=>request.Url.EndsWith("update_individual",StringComparison.Ordinal)).Select(request=>request.Body??"").ToList();
                Native.Check(bodies.Count==2
                        && bodies.Any(body=>body.Contains("\"loaders\": [\"neoforge\"]",StringComparison.Ordinal))
                        && bodies.Any(body=>body.Contains("\"loaders\": [\"fabric\"]",StringComparison.Ordinal)),
                    "A dual-loader instance did not query NeoForge and Fabric updates separately for "+scenario+".");
            }
            if(scenario=="mr-narrower")Native.Check(JObject.Parse(requests.Single(request=>request.Url.EndsWith("update_individual",StringComparison.Ordinal)).Body!)["hashes"]![0]!["loaders"]!.Values<string>().SequenceEqual(new[] {"fabric"}),"The update query uses the installed artifact's old loader set instead of the selected instance.");
        }
        Console.WriteLine("PASS native mod update discovery: selected loader/version, changed support sets, secondary hash files, cross-platform dates, incompatible candidate rejection and dual-loader NeoForge preference.");
    }
    private static JObject Mr(string id,JArray loaders,string hash,string date,string game)=>new() {
        ["id"]=id,["project_id"]="fixture-project",["name"]=id,["version_number"]=id,["date_published"]=date,["downloads"]=1,["version_type"]="release",
        ["loaders"]=loaders,["game_versions"]=new JArray(game),["dependencies"]=new JArray(),["files"]=new JArray(new JObject {
            ["filename"]=id+".jar",["url"]="https://example.invalid/"+id+".jar",["size"]=3,["hashes"]=new JObject { ["sha1"]=hash } })
    };
    private static JObject Cf(string id,JArray games,string hash,string date,string? displayName=null)=>new() {
        ["id"]=id,["modId"]=123,["gameId"]=432,["displayName"]=displayName??id,["fileFingerprint"]=17,["fileName"]=id+".jar",["fileLength"]=3,["fileDate"]=date,
        ["releaseType"]=1,["downloadCount"]=1,["downloadUrl"]="https://example.invalid/"+id+".jar",["hashes"]=new JArray(new JObject { ["algo"]=1,["value"]=hash }),
        ["gameVersions"]=games,["dependencies"]=new JArray()
    };
}
