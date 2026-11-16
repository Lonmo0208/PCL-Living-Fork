using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PCLCS;

// A version JSON may carry two loaders at once: a NeoForge platform with a real fabric-loader
// inside it (NeoFobric, or a manual fabric-loader-on-NeoForge install). The Fabric test used to be
// the first branch of an else-if chain, so such a version showed only "Fabric". These cases pin the
// decoupled detection: Fabric is independent, Forge and NeoForge stay mutually exclusive with
// NeoForge winning.
internal static class McVersionLoaderTests {
    internal static IEnumerable<(string Name, Action Run)> Cases() {
        yield return ("dual-loader version reports NeoForge and Fabric", DualLoader);
        yield return ("NeoForge-only version reports only NeoForge", NeoForgeOnly);
        yield return ("NeoForge version with FML Forge traces does not add Forge", NeoForgeWithForgeTraces);
        yield return ("Fabric-only version reports only Fabric", FabricOnly);
        yield return ("Quilt version still reports Fabric", QuiltAsFabric);
        yield return ("Forge-only version reports only Forge", ForgeOnly);
    }

    private static void DualLoader() {
        McVersion version = Build("26.3", mc: "26.3", neo: "26.3.0.108-26.3-NeoFobric", fabric: "0.20.1.1");
        Equal("26.3", version.VanillaName);
        Equal(true, version.ModLoaders.HasFlagF(ModLoaders.NeoForge));
        Equal(true, version.ModLoaders.HasFlagF(ModLoaders.Fabric));
        Equal(false, version.ModLoaders.HasFlagF(ModLoaders.Forge));
        Equal("26.3.0.108-26.3-NeoFobric", version.ModLoaderVersions[ModLoaders.NeoForge]);
        Equal("0.20.1.1", version.ModLoaderVersions[ModLoaders.Fabric]);
    }

    private static void NeoForgeOnly() {
        McVersion version = Build("1.21.1-NeoForge", mc: "1.21.1", neo: "21.1.264");
        Equal(true, version.ModLoaders.HasFlagF(ModLoaders.NeoForge));
        Equal(false, version.ModLoaders.HasFlagF(ModLoaders.Fabric));
        Equal("21.1.264", version.ModLoaderVersions[ModLoaders.NeoForge]);
    }

    private static void NeoForgeWithForgeTraces() {
        JObject json = BuildJson("1.20.1-neoforge", mc: "1.20.1", neo: "47.1.99");
        ((JArray)json["libraries"]!).Add(new JObject { ["name"] = "net.minecraftforge:minecraftforge:1.20.1-47.1.99" });
        McVersion version = new("1.20.1-neoforge", json);
        Equal(true, version.ModLoaders.HasFlagF(ModLoaders.NeoForge));
        Equal(false, version.ModLoaders.HasFlagF(ModLoaders.Forge));
        Equal("47.1.99", version.ModLoaderVersions[ModLoaders.NeoForge]);
    }

    private static void FabricOnly() {
        McVersion version = Build("fabric-loader-0.20.1.1-26.3", fabric: "0.20.1.1", inherit: "26.3");
        Equal("26.3", version.VanillaName);
        Equal(true, version.ModLoaders.HasFlagF(ModLoaders.Fabric));
        Equal(false, version.ModLoaders.HasFlagF(ModLoaders.NeoForge));
        Equal("0.20.1.1", version.ModLoaderVersions[ModLoaders.Fabric]);
    }

    private static void QuiltAsFabric() {
        McVersion version = Build("quilt-loader-0.27.0-1.21.1", quilt: "0.27.0", inherit: "1.21.1");
        Equal(true, version.ModLoaders.HasFlagF(ModLoaders.Fabric));
        Equal(false, version.ModLoaders.HasFlagF(ModLoaders.NeoForge));
        Equal("0.27.0", version.ModLoaderVersions[ModLoaders.Fabric]);
    }

    private static void ForgeOnly() {
        McVersion version = Build("1.20.1-forge-47.2.0", forge: "47.2.0", inherit: "1.20.1");
        Equal(true, version.ModLoaders.HasFlagF(ModLoaders.Forge));
        Equal(false, version.ModLoaders.HasFlagF(ModLoaders.Fabric));
        Equal(false, version.ModLoaders.HasFlagF(ModLoaders.NeoForge));
        Equal("47.2.0", version.ModLoaderVersions[ModLoaders.Forge]);
    }

    private static McVersion Build(string id, string? mc = null, string? neo = null, string? fabric = null,
            string? quilt = null, string? forge = null, string? inherit = null) =>
        new(id, BuildJson(id, mc, neo, fabric, quilt, forge, inherit));

    private static JObject BuildJson(string id, string? mc = null, string? neo = null, string? fabric = null,
            string? quilt = null, string? forge = null, string? inherit = null) {
        JArray game = [];
        if (mc is not null) {
            game.Add("--fml.mcVersion");
            game.Add(mc);
        }
        if (neo is not null) {
            game.Add("--fml.neoForgeVersion");
            game.Add(neo);
        }
        JArray libraries = [];
        if (neo is not null) libraries.Add(new JObject { ["name"] = "net.neoforged:neoforge:" + neo });
        if (fabric is not null) libraries.Add(new JObject { ["name"] = "net.fabricmc:fabric-loader:" + fabric });
        if (quilt is not null) libraries.Add(new JObject { ["name"] = "org.quiltmc:quilt-loader:" + quilt });
        // Forge's own version lives in the library suffix; the platform part is fictitious but the shape matches.
        if (forge is not null) libraries.Add(new JObject { ["name"] = "net.minecraftforge:forge:1.20.1-" + forge });
        JObject json = new() {
            ["id"] = id,
            ["type"] = "release",
            ["releaseTime"] = "2026-11-15T00:00:00+00:00",
            ["arguments"] = new JObject { ["game"] = game },
            ["libraries"] = libraries,
        };
        if (inherit is not null) json["inheritsFrom"] = inherit;
        return json;
    }

    private static void Equal<T>(T expected, T actual) {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], got [{actual}].");
    }
}
