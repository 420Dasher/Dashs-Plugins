using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

using Lumina.Data.Files;
using Newtonsoft.Json.Linq;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin : IDalamudPlugin
{
    [PluginService]
    private static IDalamudPluginInterface Pi { get; set; } = null!;

    [PluginService]
    private static ICommandManager Commands { get; set; } = null!;

    [PluginService]
    private static IObjectTable Objects { get; set; } = null!;

    [PluginService]
    private static IDataManager Data { get; set; } = null!;

    [PluginService]
    private static IPluginLog Log { get; set; } = null!;


    // Confirmed Penumbra API V5 signatures.
    private readonly ICallGateSubscriber<
        bool, ushort[], JObject?[]> treeIpc;

    private readonly ICallGateSubscriber<
        ushort[], Dictionary<string, HashSet<string>>?[]> pathsIpc;

    // Optional, read-only Glamourer state availability.
    private readonly ICallGateSubscriber<
        int, uint, (int, string?)> glamourerIpc;

    private readonly List<ModelEntry> models = new();
    private readonly List<MaterialEntry> orphans = new();
    private readonly List<FlatEntry> flat = new();

    private readonly Dictionary<string, DecodeResult> decodeCache =
        new(StringComparer.OrdinalIgnoreCase);

    private bool windowOpen;
    private bool scanned;

    private bool outfitOnly = true;
    private bool showPaths;
    private bool showRaw;
    private bool showDependencies;
    private bool showAllRows;
    private float minimumCoverage = 0.25f;

    private string actorName = "Not scanned";
    private string actorIndex = "-";
    private string treeStatus = "Not checked";
    private string pathStatus = "Not checked";
    private string glamourerStatus = "Not checked";
    private string status = "Scan your current appearance.";

    private DateTime scanTime;
    private int nodeCount;
    private int decodedCount;

    private static readonly string[] SlotOrder =
    {
        "Head", "Body", "Hands", "Legs", "Feet",
        "Earrings", "Necklace", "Bracelets",
        "Right Ring", "Left Ring",
        "Other / Unassigned"
    };

    private static readonly Dictionary<string, string> SlotNames = new()
    {
        ["met"] = "Head",
        ["top"] = "Body",
        ["glv"] = "Hands",
        ["dwn"] = "Legs",
        ["sho"] = "Feet",
        ["ear"] = "Earrings",
        ["nek"] = "Necklace",
        ["wrs"] = "Bracelets",
        ["rir"] = "Right Ring",
        ["ril"] = "Left Ring"
    };

    private sealed class ModelEntry
    {
        public string GamePath = "";
        public string ActualPath = "";
        public string Slot = "Other / Unassigned";
        public bool IsOutfit;
        public readonly List<MaterialEntry> Materials = new();
    }

    private sealed class MaterialEntry
    {
        public string GamePath = "";
        public string ActualPath = "";
        public string Slot = "Other / Unassigned";
        public readonly List<TextureEntry> Textures = new();
    }

    private sealed class TextureEntry
    {
        public string GamePath = "";
        public string ActualPath = "";
        public bool IsId;
        public DecodeResult? Decoded;
    }

    private sealed class FlatEntry
    {
        public string GamePath = "";
        public string ActualPath = "";
        public string Slot = "Other / Unassigned";
        public string Kind = "";
    }

    private sealed class RowStat
    {
        public string Row = "";
        public long ExactHits;
        public long NearHits;
        public long OffGridHits;
        public double ExactWeight;
        public double NearWeight;
        public double OffGridWeight;
        public double StrictPercent;
        public double OffGridPercent;

        public double TotalPercent => StrictPercent + OffGridPercent;
    }

    private sealed class DecodeResult
    {
        public bool Success;
        public string Detail = "";
        public string[] Rows = Array.Empty<string>();
        public RowStat[] Stats = Array.Empty<RowStat>();
        public int Width;
        public int Height;
        public int ExactPixels;
        public int NearPixels;
        public int ApproximatePixels;
    }

    public Plugin()
    {
        treeIpc = Pi.GetIpcSubscriber<
            bool, ushort[], JObject?[]>(
            "Penumbra.GetGameObjectResourceTrees.V5");

        pathsIpc = Pi.GetIpcSubscriber<
            ushort[], Dictionary<string, HashSet<string>>?[]>(
            "Penumbra.GetGameObjectResourcePaths.V5");

        glamourerIpc = Pi.GetIpcSubscriber<
            int, uint, (int, string?)>(
            "Glamourer.GetStateBase64");

        Commands.AddHandler("/adri", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Advanced Dye Row Inspector."
        });

        Pi.UiBuilder.Draw += Draw;
        Pi.UiBuilder.OpenMainUi += Open;
        Pi.UiBuilder.OpenConfigUi += Open;

        Log.Information("Advanced Dye Row Inspector DEV8 Stage 1 initialized.");
    }

    private void OnCommand(string command, string arguments)
    {
        windowOpen = !windowOpen;
    }

    private void Open()
    {
        windowOpen = true;
    }

    public void Dispose()
    {

        Commands.RemoveHandler("/adri");
        Pi.UiBuilder.Draw -= Draw;
        Pi.UiBuilder.OpenMainUi -= Open;
        Pi.UiBuilder.OpenConfigUi -= Open;

        models.Clear();
        orphans.Clear();
        flat.Clear();
        decodeCache.Clear();
    }

    private static string JsonString(JObject obj, string name)
    {
        return obj.GetValue(name, StringComparison.OrdinalIgnoreCase)?
            .ToString() ?? "";
    }

    private static string Kind(string path)
    {
        if (path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return "Model";

        if (path.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            return "Material";

        if (path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            return "Texture";

        return "Other";
    }

    private static bool IsIdTexture(string game, string actual)
    {
        return Regex.IsMatch(
            game + "|" + actual,
            @"(?:_id|_index)\.tex(?:$|\|)",
            RegexOptions.IgnoreCase);
    }

    private static string Slot(string path)
    {
        var match = Regex.Match(
            path,
            @"(?:^|[_/])(met|top|glv|dwn|sho|ear|nek|wrs|rir|ril)(?:[_\.]|$)",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            return "Other / Unassigned";

        return SlotNames.TryGetValue(
            match.Groups[1].Value.ToLowerInvariant(),
            out var slot)
                ? slot
                : "Other / Unassigned";
    }

    private static bool IsOutfit(string gamePath)
    {
        string path = gamePath.Replace('\\', '/');

        if (path.StartsWith(
                "chara/accessory/",
                StringComparison.OrdinalIgnoreCase))
            return true;

        if (!path.StartsWith(
                "chara/equipment/",
                StringComparison.OrdinalIgnoreCase))
            return false;

        // e0000 is commonly used for underlying body/add-on
        // equipment resources such as hands or nails.
        if (path.StartsWith(
                "chara/equipment/e0000/",
                StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static string DisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "(unnamed resource)";

        string cleaned = path.Replace('\\', '/');
        int last = cleaned.LastIndexOf('/');

        return last >= 0 ? cleaned[(last + 1)..] : cleaned;
    }

    // ---------------------------------------------------------
    // TEXTURE DECODER
    // ---------------------------------------------------------

    private DecodeResult Decode(string game, string actual)
    {
        // Cache by resolved resource identity.
        string key = !string.IsNullOrWhiteSpace(actual)
            ? actual
            : game;

        if (decodeCache.TryGetValue(key, out var cached))
            return cached;

        var result = new DecodeResult();
        decodeCache[key] = result;

        if (decodedCount >= 24)
        {
            result.Detail = "Scan safety limit reached (24 ID textures).";
            return result;
        }

        decodedCount++;

        try
        {
            TexFile? tex;

            if (Path.IsPathRooted(actual))
            {
                if (!File.Exists(actual))
                {
                    result.Detail = "Resolved texture file missing.";
                    return result;
                }

                var info = new FileInfo(actual);

                if (info.Length > 64L * 1024 * 1024)
                {
                    result.Detail = "File exceeds 64 MiB safety limit.";
                    return result;
                }

                if (!actual.EndsWith(
                    ".tex", StringComparison.OrdinalIgnoreCase))
                {
                    result.Detail = "Resolved resource is not a .tex.";
                    return result;
                }

                tex = Data.GameData.GetFileFromDisk<TexFile>(actual);
            }
            else
            {
                string lookup = string.IsNullOrWhiteSpace(actual)
                    ? game
                    : actual;

                if (!lookup.EndsWith(
                    ".tex", StringComparison.OrdinalIgnoreCase))
                {
                    result.Detail = "Unsupported game texture path.";
                    return result;
                }

                tex = Data.GetFile<TexFile>(lookup);
            }

            if (tex == null)
            {
                result.Detail = "Texture data unavailable.";
                return result;
            }

            int width = tex.Header.Width;
            int height = tex.Header.Height;

            long pixelCount = (long)width * height;

            if (pixelCount <= 0 || pixelCount > 8_388_608)
            {
                result.Detail = "Unsupported texture dimensions.";
                return result;
            }

            // Lumina returns BGRA8 data from the first mip.
            byte[] image = tex.ImageData;

            if (image.Length < pixelCount * 4)
            {
                result.Detail = "Unexpected decoded image length.";
                return result;
            }

            var stats = new RowStat[32];

            for (int i = 0; i < 32; i++)
            {
                stats[i] = new RowStat
                {
                    Row = $"{i / 2 + 1}{(i % 2 == 0 ? "A" : "B")}"
                };
            }

            int exactPixels = 0;
            int nearPixels = 0;
            int offGridPixels = 0;

            for (int i = 0; i < (int)pixelCount; i++)
            {
                int offset = i * 4;

                byte red = image[offset + 2];
                byte green = image[offset + 1];

                // Dawntrail ID row-pair red values:
                // 0, 17, 34, ... 255.
                int pair = Math.Clamp((red + 8) / 17, 0, 15);

                int distance = Math.Abs(red - pair * 17);

                // Descriptive pixel classes.
                // Not a claim of mesh visibility.
                int quality;

                if (distance == 0)
                {
                    quality = 0;
                    exactPixels++;
                }
                else if (distance <= 3)
                {
                    quality = 1;
                    nearPixels++;
                }
                else
                {
                    quality = 2;
                    offGridPixels++;
                }

                // 255 green = A; 0 green = B.
                // Intermediate values blend both.
                double a = green / 255.0;
                double b = 1.0 - a;

                AddSample(stats[pair * 2], a, quality);
                AddSample(stats[pair * 2 + 1], b, quality);
            }

            foreach (var stat in stats)
            {
                stat.StrictPercent =
                    100.0 * (stat.ExactWeight + stat.NearWeight)
                    / pixelCount;

                stat.OffGridPercent =
                    100.0 * stat.OffGridWeight / pixelCount;
            }

            result.Success = true;
            result.Stats = stats;

            result.Rows = stats
                .Where(x => x.TotalPercent > 0)
                .Select(x => x.Row)
                .ToArray();

            result.Width = width;
            result.Height = height;
            result.ExactPixels = exactPixels;
            result.NearPixels = nearPixels;
            result.ApproximatePixels = offGridPixels;

            result.Detail =
                "Texture-wide red/green channel statistics. " +
                "Mesh UV visibility remains unverified.";
        }
        catch (Exception ex)
        {
            result.Detail =
                "Texture decoding unavailable: " +
                ex.GetType().Name + ": " + ex.Message;

            Log.Warning($"ADRI texture decoding failed: {ex}");
        }

        return result;
    }

    private static void AddSample(
        RowStat stat, double influence, int quality)
    {
        if (influence <= 0)
            return;

        switch (quality)
        {
            case 0:
                stat.ExactHits++;
                stat.ExactWeight += influence;
                break;

            case 1:
                stat.NearHits++;
                stat.NearWeight += influence;
                break;

            default:
                stat.OffGridHits++;
                stat.OffGridWeight += influence;
                break;
        }
    }

    private RowStat[] SelectedRows(DecodeResult? result)
    {
        if (result == null || !result.Success)
            return Array.Empty<RowStat>();

        var ranked = result.Stats
            .Where(x => showAllRows
                ? x.TotalPercent > 0
                : x.StrictPercent >= minimumCoverage)
            .OrderByDescending(x => x.StrictPercent)
            .ThenByDescending(x => x.OffGridPercent);

        return showAllRows
            ? ranked.ToArray()
            : ranked.Take(10).ToArray();
    }

    private static bool IsOutfitMaterial(MaterialEntry material)
    {
        return IsOutfit(material.GamePath);
    }

    private int SharedTextureLinks(DecodeResult result)
    {
        return models
            .SelectMany(x => x.Materials)
            .Concat(orphans)
            .Count(m => m.Textures.Any(t =>
                t.IsId &&
                ReferenceEquals(t.Decoded, result)));
    }

    private static int RowOrder(string row)
    {
        if (row.Length < 2 ||
            !int.TryParse(row[..^1], out int number))
            return int.MaxValue;

        return number * 2 + (row.EndsWith("B") ? 1 : 0);
    }

    private void Visit(
        JObject node,
        ModelEntry? parentModel,
        MaterialEntry? parentMaterial,
        int depth)
    {
        if (++nodeCount > 4000 || depth > 40)
            throw new InvalidOperationException(
                "Resource-tree safety limit exceeded.");

        string game = JsonString(node, "GamePath");
        string actual = JsonString(node, "ActualPath");

        string effective = string.IsNullOrWhiteSpace(game)
            ? actual
            : game;

        string kind = Kind(effective);

        ModelEntry? model = parentModel;
        MaterialEntry? material = parentMaterial;

        if (kind == "Model")
        {
            model = new ModelEntry
            {
                GamePath = game,
                ActualPath = actual,
                Slot = Slot(game),
                IsOutfit = IsOutfit(game)
            };

            models.Add(model);
            material = null;
        }
        else if (kind == "Material")
        {
            material = new MaterialEntry
            {
                GamePath = game,
                ActualPath = actual,
                Slot = model?.Slot ?? Slot(game)
            };

            if (model != null)
                model.Materials.Add(material);
            else
                orphans.Add(material);
        }
        else if (kind == "Texture" && material != null)
        {
            var texture = new TextureEntry
            {
                GamePath = game,
                ActualPath = actual,
                IsId = IsIdTexture(game, actual)
            };

            if (texture.IsId)
                texture.Decoded = Decode(game, actual);

            material.Textures.Add(texture);
        }

        if (node.GetValue(
                "Children",
                StringComparison.OrdinalIgnoreCase) is JArray children)
        {
            foreach (var child in children)
            {
                if (child is JObject childObject)
                {
                    Visit(childObject, model, material, depth + 1);
                }
            }
        }
    }

    private void ReadTree(ushort index)
    {
        if (!treeIpc.HasFunction)
        {
            treeStatus = "V5 resource-tree IPC unavailable.";
            return;
        }

        try
        {
            var results = treeIpc.InvokeFunc(
                false, new ushort[] { index });

            if (results == null ||
                results.Length == 0 ||
                results[0] == null)
            {
                treeStatus = "No resource-tree snapshot returned.";
                return;
            }

            JObject root = results[0]!;

            var nodes = root.GetValue(
                "Nodes", StringComparison.OrdinalIgnoreCase) as JArray;

            if (nodes == null)
            {
                treeStatus = "Tree returned without usable Nodes.";
                return;
            }

            foreach (var node in nodes)
            {
                if (node is JObject obj)
                    Visit(obj, null, null, 0);
            }

            treeStatus =
                $"Connected: {nodeCount} nodes, " +
                $"{models.Count} model branches.";
        }
        catch (Exception ex)
        {
            models.Clear();
            orphans.Clear();

            treeStatus = "Tree scan failed: " + ex.Message;
            Log.Warning($"ADRI resource-tree failure: {ex}");
        }
    }

    // Keep the working DEV1 path scanner for diagnostics
    // and compatibility when resource trees are unavailable.
    private void ReadFlatPaths(ushort index)
    {
        if (!pathsIpc.HasFunction)
        {
            pathStatus = "Resource-path IPC unavailable.";
            return;
        }

        try
        {
            var results = pathsIpc.InvokeFunc(
                new ushort[] { index });

            if (results == null ||
                results.Length == 0 ||
                results[0] == null)
            {
                pathStatus = "No resource paths returned.";
                return;
            }

            var seen = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var item in results[0]!)
            {
                foreach (string game in item.Value)
                {
                    string kind = Kind(game);

                    if (kind == "Other")
                        continue;

                    string key = game + "\n" + item.Key;

                    if (!seen.Add(key))
                        continue;

                    flat.Add(new FlatEntry
                    {
                        GamePath = game,
                        ActualPath = item.Key,
                        Slot = Slot(game),
                        Kind = kind == "Texture" &&
                               IsIdTexture(game, item.Key)
                            ? "ID texture candidate"
                            : kind
                    });
                }
            }

            pathStatus = $"Connected: {flat.Count} relevant resources.";
        }
        catch (Exception ex)
        {
            pathStatus = "Path scan failed: " + ex.Message;
            Log.Warning($"ADRI path scan failure: {ex}");
        }
    }

    private void Scan()
    {

        scanned = true;
        scanTime = DateTime.Now;

        models.Clear();
        orphans.Clear();
        flat.Clear();
        decodeCache.Clear();

        runtimeMaterialEvidence.Clear();
        runtimeBindingStatus = "Not checked.";

        previewTargets.Clear();
        previewTargetStatus = "Not checked.";

        nodeCount = 0;
        decodedCount = 0;

        treeStatus = "Not checked";
        pathStatus = "Not checked";
        glamourerStatus = "Not checked";

        var actor = Objects.LocalPlayer;

        if (actor == null)
        {
            actorName = "Unavailable";
            actorIndex = "-";
            status = "Log into a character before scanning.";
            return;
        }

        actorName = actor.Name.TextValue;
        actorIndex = actor.ObjectIndex.ToString();

        try
        {
            if (glamourerIpc.HasFunction)
            {
                var result = glamourerIpc.InvokeFunc(
                    (int)actor.ObjectIndex, 0u);

                glamourerStatus = result.Item2 == null
                    ? $"No state returned (result {result.Item1})."
                    : "Appearance state available.";
            }
            else
            {
                glamourerStatus = "Not available (optional).";
            }
        }
        catch (Exception ex)
        {
            glamourerStatus = "IPC error: " + ex.Message;
        }

        ushort index = (ushort)actor.ObjectIndex;

        ReadFlatPaths(index);
        ReadTree(index);
        AnalyzeGeometry();

        int materials = models.Sum(x => x.Materials.Count);
        int linkedIds = models.Sum(x =>
            x.Materials.Sum(m => m.Textures.Count(t => t.IsId)));

        int successfulTextures = decodeCache.Values.Count(x => x.Success);

        status =
            $"{models.Count} tree models, " +
            $"{materials} linked materials, " +
            $"{linkedIds} linked ID textures, " +
            $"{successfulTextures} decoded texture sources.";

        if (models.Count == 0 && flat.Count > 0)
        {
            status += " Tree relationships unavailable; using DEV1 path diagnostics.";
            showRaw = true;
        }
        else if (models.Count == 0 && flat.Count == 0)
        {
            status = "No inspectable resources. Check IPC status.";
        }
    }

    // ---------------------------------------------------------
    // CLIPBOARD / DIAGNOSTICS
    // ---------------------------------------------------------

    private IEnumerable<string> LinkedRows()
    {
        return models
            .Where(x => !outfitOnly || x.IsOutfit)
            .SelectMany(x => x.Materials)
            .Where(m => !outfitOnly ||
                showDependencies || IsOutfitMaterial(m))
            .SelectMany(x => x.Textures)
            .Where(x => x.IsId && x.Decoded?.Success == true)
            .SelectMany(x => SelectedRows(x.Decoded))
            .Select(x => x.Row)
            .Distinct()
            .OrderBy(RowOrder);
    }

    private string MakeDiagnostics()
    {
        var sb = new StringBuilder();

        sb.AppendLine("Advanced Dye Row Inspector - DEV8 STAGE 2A UI FIX");
        sb.AppendLine("Version: 0.0.3.22");
        sb.AppendLine("Time: " + scanTime.ToString("O"));
        sb.AppendLine("Actor: " + actorName);
        sb.AppendLine("Object index: " + actorIndex);
        sb.AppendLine("Tree: " + treeStatus);
        sb.AppendLine("Paths: " + pathStatus);
        sb.AppendLine("Glamourer: " + glamourerStatus);
        sb.AppendLine("Status: " + status);
        sb.AppendLine();

        foreach (var model in models)
        {
            sb.AppendLine($"[{model.Slot}] MODEL");
            sb.AppendLine("Game: " + model.GamePath);
            sb.AppendLine("Resolved: " + model.ActualPath);
            sb.AppendLine("Outfit classification: " + model.IsOutfit);

            foreach (var material in model.Materials)
            {
                sb.AppendLine("  MATERIAL: " + material.GamePath);
                sb.AppendLine("  Resolved: " + material.ActualPath);

                foreach (var texture in material.Textures)
                {
                    sb.AppendLine("    TEXTURE: " + texture.GamePath);
                    sb.AppendLine("    Resolved: " + texture.ActualPath);
                    sb.AppendLine("    ID candidate: " + texture.IsId);

                    if (texture.Decoded != null)
                    {
                        sb.AppendLine(
                            "    Decode: " + texture.Decoded.Detail);

                        if (texture.Decoded.Success)
                        {
                            sb.AppendLine(
                                "    Texture-wide raw rows: " +
                                string.Join(", ", texture.Decoded.Rows));

                            sb.AppendLine(
                                $"    Exact pixels: {texture.Decoded.ExactPixels}; " +
                                $"near: {texture.Decoded.NearPixels}; " +
                                $"off-grid: {texture.Decoded.ApproximatePixels}");

                            foreach (var row in texture.Decoded.Stats
                                .Where(x => x.TotalPercent > 0)
                                .OrderByDescending(x => x.StrictPercent))
                            {
                                sb.AppendLine(
                                    $"      {row.Row}: " +
                                    $"clean {row.StrictPercent:F4}% " +
                                    $"off-grid {row.OffGridPercent:F4}% | " +
                                    $"exact hits {row.ExactHits}, " +
                                    $"near hits {row.NearHits}, " +
                                    $"off-grid hits {row.OffGridHits}");
                            }
                        }
                    }
                }
            }

            sb.AppendLine();
        }

        foreach (var material in orphans)
        {
            sb.AppendLine("[UNLINKED MATERIAL]");
            sb.AppendLine(material.GamePath);
            sb.AppendLine(material.ActualPath);
        }

        sb.AppendLine();
        AppendUvDiagnostics(sb);
        AppendMaterialAuditDiagnostics(sb);
        AppendRuntimeMaterialDiagnostics(sb);
        AppendPreviewTargetDiagnostics(sb);

        sb.AppendLine("[FLAT PENUMBRA RESOURCE SNAPSHOT]");

        foreach (var item in flat)
        {
            sb.AppendLine(
                $"{item.Slot} | {item.Kind} | {item.GamePath} | {item.ActualPath}");
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------
    // IMGUI
    // ---------------------------------------------------------

    private static void DrawPath(string label, string path)
    {
        ImGui.TextWrapped(label + ": " +
            (string.IsNullOrWhiteSpace(path) ? "(unavailable)" : path));
    }

    private void DrawTexture(TextureEntry texture)
    {
        ImGui.BulletText(
            texture.IsId
                ? "ID texture: " + DisplayName(texture.GamePath)
                : "Texture: " + DisplayName(texture.GamePath));

        ImGui.Indent();

        if (showPaths)
        {
            DrawPath("Game", texture.GamePath);
            DrawPath("Resolved", texture.ActualPath);
        }

        if (texture.IsId)
        {
            var result = texture.Decoded;

            if (result?.Success == true)
            {
                ImGui.TextDisabled(
                    $"{result.Width}x{result.Height} | " +
                    "Texture-wide, NOT mesh-UV confirmed.");

                int links = SharedTextureLinks(result);

                if (links > 1)
                {
                    ImGui.TextDisabled(
                        $"Shared decoded ID source: " +
                        $"{links} linked material nodes.");
                }

                int pixelCount = result.Width * result.Height;

                ImGui.TextDisabled(
                    $"Red-channel quality: " +
                    $"{result.ExactPixels:N0} exact, " +
                    $"{result.NearPixels:N0} near-palette, " +
                    $"{result.ApproximatePixels:N0} off-grid " +
                    $"of {pixelCount:N0} pixels.");

                var selected = SelectedRows(result);

                if (selected.Length == 0)
                {
                    ImGui.TextDisabled(
                        "No clean-channel rows above threshold.");
                }
                else
                {
                    ImGui.TextUnformatted(
                        $"Highest texture coverage ({selected.Length} shown):");

                    foreach (var row in selected)
                    {
                        ImGui.TextWrapped(
                            $"{row.Row}: " +
                            $"{row.StrictPercent:F2}% clean + " +
                            $"{row.OffGridPercent:F2}% off-grid estimate");
                    }
                }

                if (!showAllRows &&
                    result.Rows.Length > selected.Length)
                {
                    ImGui.TextDisabled(
                        $"{result.Rows.Length - selected.Length} " +
                        "additional rows available through Show all rows.");
                }

                ImGui.TextDisabled(
                    "Coverage is texture-wide, not visible-mesh coverage.");
            }
            else
            {
                ImGui.TextWrapped(
                    "Rows unavailable: " +
                    (result?.Detail ?? "No decoded data."));
            }
        }

        ImGui.Unindent();
    }

    private void DrawMaterial(MaterialEntry material, int index)
    {
        string name = DisplayName(material.GamePath);

        if (!ImGui.TreeNode(
            $"{name}##mat_{index}_{material.GetHashCode()}"))
            return;

        DrawPath("Material", material.GamePath);
        DrawUvReport(material);

        if (showPaths)
            DrawPath("Resolved", material.ActualPath);

        var ids = material.Textures.Where(x => x.IsId).ToList();

        if (ids.Count == 0)
        {
            ImGui.TextDisabled(
                "No ID texture linked by Penumbra resource tree.");
        }
        else
        {
            var rows = ids
                .Where(x => x.Decoded?.Success == true)
                .SelectMany(x => SelectedRows(x.Decoded))
                .Select(x => x.Row)
                .Distinct()
                .OrderBy(RowOrder)
                .ToArray();

            if (rows.Length > 0)
            {
                ImGui.TextWrapped(
                    "Highlighted rows: " + string.Join(", ", rows));

                ImGui.TextDisabled(
                    "Ranked by texture coverage; not UV-confirmed.");

                if (ImGui.SmallButton(
                    $"Copy Highlighted Rows##copy_{index}_{material.GetHashCode()}"))
                {
                    ImGui.SetClipboardText(string.Join(", ", rows));
                }
            }
            else
            {
                ImGui.TextDisabled(
                    "No rows above threshold, or decoder unavailable.");
            }
        }

        foreach (var texture in material.Textures)
        {
            if (texture.IsId || showPaths)
                DrawTexture(texture);
        }

        ImGui.TreePop();
    }

    private void DrawModel(ModelEntry model, int index)
    {
        string name = DisplayName(model.GamePath);

        if (!ImGui.TreeNode($"{name}##model_{index}"))
            return;

        DrawPath("Model", model.GamePath);
        DrawUvModelStatus(model);

        if (showPaths)
            DrawPath("Resolved", model.ActualPath);

        var outfitMaterials = model.Materials
            .Where(IsOutfitMaterial)
            .ToList();

        var dependencies = model.Materials
            .Where(m => !IsOutfitMaterial(m))
            .ToList();

        if (outfitMaterials.Count == 0)
        {
            ImGui.TextDisabled(
                "No equipment/accessory materials directly classified.");
        }
        else
        {
            ImGui.TextUnformatted(
                $"Outfit materials ({outfitMaterials.Count})");

            for (int i = 0; i < outfitMaterials.Count; i++)
                DrawMaterial(outfitMaterials[i], i);
        }

        if (dependencies.Count > 0)
        {
            if (!outfitOnly || showDependencies)
            {
                if (ImGui.TreeNode(
                    $"Body / other dependencies ({dependencies.Count})##deps_{index}"))
                {
                    ImGui.TextDisabled(
                        "Other linked materials; may include " +
                        "body meshes, connectors or garment dependencies.");

                    for (int i = 0; i < dependencies.Count; i++)
                        DrawMaterial(dependencies[i], i + 1000);

                    ImGui.TreePop();
                }
            }
            else
            {
                ImGui.TextDisabled(
                    $"{dependencies.Count} body/other materials hidden.");
            }
        }

        ImGui.TreePop();
    }

    private void DrawRaw()
    {
        if (!ImGui.CollapsingHeader(
            $"Raw resources ({flat.Count})###ADRIRaw"))
            return;

        ImGui.TextDisabled(
            "Path-only snapshot. Entries here are not model/material links.");

        foreach (string slot in SlotOrder)
        {
            var entries = flat
                .Where(x => x.Slot == slot)
                .Where(x => !outfitOnly ||
                    IsOutfit(x.GamePath))
                .ToList();

            if (entries.Count == 0)
                continue;

            if (!ImGui.TreeNode(
                $"{slot} ({entries.Count})##raw_{slot}"))
                continue;

            foreach (var entry in entries)
            {
                ImGui.BulletText(entry.Kind);
                DrawPath("Game", entry.GamePath);
                DrawPath("Resolved", entry.ActualPath);
                ImGui.Spacing();
            }

            ImGui.TreePop();
        }
    }

    private void Draw()
    {
        DrawCompact();
    }
}