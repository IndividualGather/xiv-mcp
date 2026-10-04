using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace XivMcp.Fishing;

/// <summary>
/// An AutoHook fishing preset for a <see cref="FishGuide"/>, in AutoHook's own export format, so it can be handed to AutoHook's
/// <c>CreateAndSelectAnonymousPreset</c> IPC: "AH11_" followed by the preset's JSON, Brotli-compressed and in Base64. AutoHook
/// migrates presets of older versions when it imports them, so a preset written for version 11 keeps working after AutoHook updates.
/// Only what differs from AutoHook's defaults is written, as AutoHook's own exports do.
/// </summary>
public static class AutoHookPreset
{
    public const int SchemaVersion = 11;
    public const string Prefix = "AH11_";

    // AutoHook's HookType values are the action ids of the hooks.
    private const uint PrecisionHookset = 4179, PowerfulHookset = 4103;
    private const uint FishersIntuition = 568;
    private const uint AmbitiousLure = 37594, ModestLure = 37595;

    /// <summary>
    /// Whether the preset also catches the fish for Fisher's Intuition: they bite on the same bait as the first cast, so AutoHook
    /// hooks them until Fisher's Intuition is up, and only the target's casts while it lasts. Otherwise the player catches them.
    /// </summary>
    public static bool CatchesPredators(FishGuide g) =>
        g.Predators.Count > 0 && g.PredatorCasts.Count == g.Predators.Count && g.PredatorCasts.All(p => !p.Mooch && p.Bait == g.FirstBait);

    /// <summary>The preset's JSON (AutoHook's CustomPresetConfig).</summary>
    public static JsonObject Build(FishGuide g, string name)
    {
        var baits = new JsonArray();
        var mooches = new JsonArray();
        var fish = new JsonArray();
        var intuition = CatchesPredators(g);
        var lure = g.Lure?.ToLowerInvariant() switch { "ambitious" => AmbitiousLure, "modest" => ModestLure, _ => 0u };
        for (var i = 0; i < g.Path.Count; i++)
        {
            var step = g.Path[i];
            var isTarget = step.Fish == g.Fish;
            // Reel in when the target has not bitten by the end of its window, rather than wait for something else.
            var timeout = isTarget && step.Bite is { } b ? Math.Ceiling(b.Max) + 1 : 0;
            var stepLure = isTarget ? lure : 0;
            var hook = new JsonObject { ["BaitFish"] = new JsonObject { ["Id"] = (int)step.Bait } };
            if (intuition)
            {
                // Without Fisher's Intuition the target does not bite: the first cast hooks the fish that give it.
                hook["NormalHook"] = i == 0 ? Hookset(g.PredatorCasts, 0, 0, 0) : Hookset([step], 0, timeout, stepLure);
                hook["IntuitionHook"] = Hookset([step], FishersIntuition, timeout, stepLure, custom: true);
            }
            else
            {
                hook["NormalHook"] = Hookset([step], 0, timeout, stepLure);
                hook["IntuitionHook"] = Hookset([step], FishersIntuition, timeout, stepLure);
            }
            (step.Mooch ? mooches : baits).Add(hook);
            if (!isTarget) fish.Add(new JsonObject { ["Fish"] = new JsonObject { ["Id"] = (int)step.Fish }, ["Mooch"] = new JsonObject { ["Enabled"] = true } });
        }
        if (intuition)
            foreach (var p in g.Predators)
            {
                var cfg = new JsonObject { ["Fish"] = new JsonObject { ["Id"] = (int)p.Fish } };
                // Identical Cast makes the next bite the same fish, when several are needed.
                if (p.Count > 1) cfg["IdenticalCast"] = new JsonObject { ["Enabled"] = true };
                fish.Add(cfg);
            }
        fish.Add(new JsonObject { ["Fish"] = new JsonObject { ["Id"] = (int)g.Fish } });

        return new JsonObject
        {
            ["PresetName"] = name,
            ["ListOfBaits"] = baits,
            ["ListOfMooch"] = mooches,
            ["ListOfFish"] = fish,
            ["AutoCastsCfg"] = new JsonObject
            {
                ["EnableAll"] = true,
                ["CastLine"] = new JsonObject { ["Enabled"] = true },
                ["CastPatience"] = new JsonObject { ["Enabled"] = g.BigFish },
                ["CastCollect"] = new JsonObject { ["Enabled"] = g.Collectable },
                ["CastSnagging"] = new JsonObject { ["Enabled"] = g.Snagging },
            },
            ["ExtraCfg"] = new JsonObject
            {
                ["Enabled"] = true,
                ["ForceBaitSwap"] = true,
                ["ForcedBaitId"] = (int)g.FirstBait,
            },
        };
    }

    /// <summary>The preset as AutoHook's import string.</summary>
    public static string Export(JsonObject preset) => Prefix + Compress(preset.ToJsonString());

    /// <summary>The JSON of an import string written by <see cref="Export"/> (for tests and the log).</summary>
    public static string Decode(string export)
    {
        if (!export.StartsWith(Prefix, StringComparison.Ordinal)) throw new FormatException($"Not an AutoHook {Prefix} preset.");
        using var input = new MemoryStream(Convert.FromBase64String(export[Prefix.Length..]));
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Which bites to hook and with what: only the tugs of the fish wanted on this cast (others are let go), each with its hookset
    /// under Patience. When a wanted fish's tug is unknown, every bite is hooked. <paramref name="custom"/> makes AutoHook use this
    /// set while the status is up (here: Fisher's Intuition) instead of the normal one; <paramref name="lure"/> casts that lure.
    /// </summary>
    private static JsonObject Hookset(IReadOnlyList<CatchStep> wanted, uint requiredStatus, double timeout, uint lure, bool custom = false)
    {
        var anyBite = wanted.Any(w => w.Tug is null);
        JsonObject Bite(Tug tug, uint defaultType)
        {
            var match = wanted.FirstOrDefault(w => w.Tug == tug);
            var type = match?.Hookset is { } h ? (h == Fishing.Hookset.Precision ? PrecisionHookset : PowerfulHookset) : defaultType;
            return new JsonObject { ["HooksetEnabled"] = anyBite || match is not null, ["HooksetType"] = type };
        }

        var set = new JsonObject
        {
            ["RequiredStatus"] = requiredStatus,
            ["PatienceWeak"] = Bite(Tug.Light, PrecisionHookset),
            ["PatienceStrong"] = Bite(Tug.Medium, PowerfulHookset),
            ["PatienceLegendary"] = Bite(Tug.Heavy, PowerfulHookset),
        };
        if (custom) set["UseCustomStatusHook"] = true;
        if (timeout > 0) set["TimeoutMax"] = timeout;
        if (lure != 0)
        {
            // As AutoHook's own solver sets a lure up: cast it until the special fish bites.
            var type = new JsonObject { ["Enabled"] = true, ["Special"] = new JsonObject { ["Enabled"] = true, ["CancelAttempt"] = true } };
            set["CastLures"] = new JsonObject { ["Enabled"] = true, ["Id"] = lure, [lure == AmbitiousLure ? "Ambitious" : "Modest"] = type };
        }
        return set;
    }

    private static string Compress(string json)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize))
            brotli.Write(Encoding.UTF8.GetBytes(json));
        return Convert.ToBase64String(output.ToArray());
    }
}
