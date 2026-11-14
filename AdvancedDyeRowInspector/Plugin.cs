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

public sealed class Plugin : IDalamudPlugin
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

    private sealed class DecodeResult
    {
        public bool Success;
        public string Detail = "";
        public string[] Rows = Array.Empty<string>();
        public int Width;
        public int Height;
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

        Log.Information("Advanced Dye Row Inspector DEV2 initialized.");
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
        string key = game + "\n" + actual;

        if (decodeCache.TryGetValue(key, out var cached))
            return cached;

        var result = new DecodeResult();
        decodeCache[key] = result;

        if (decodedCount >= 24)
        {
            result.Detail =
                "Scan safety limit reached (24 ID textures).";
            return result;
        }

        decodedCount++;

        try
        {
            TexFile? tex = null;

            // Use the actual Penumbra-resolved file where possible.
            // Never silently substitute an unmodded texture.
            if (Path.IsPathRooted(actual))
            {
                if (!File.Exists(actual))
                {
                    result.Detail = "Resolved file is missing.";
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
                    result.Detail =
                        "Resolved file is not a supported .tex.";
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
                    result.Detail = "Game resource is not a .tex file.";
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
            long pixels = (long)width * height;

            if (pixels <= 0 || pixels > 8_388_608)
            {
                result.Detail = "Invalid or oversized texture dimensions.";
                return result;
            }

            // Lumina TexFile.ImageData is the first mip
            // converted to BGRA8.
            byte[] image = tex.ImageData;

            if (image.Length < pixels * 4)
            {
                result.Detail = "Unexpected decoded image length.";
                return result;
            }

            var a = new bool[16];
            var b = new bool[16];
            int approximate = 0;

            for (int i = 0; i < (int)pixels; i++)
            {
                int offset = i * 4;

                byte green = image[offset + 1];
                byte red = image[offset + 2];

                // Dawntrail red channel:
                // 00, 11, 22 ... FF for the 16 row pairs.
                int pair = Math.Clamp(
                    (red + 8) / 17,
                    0, 15);

                int expectedRed = pair * 17;

                if (Math.Abs(red - expectedRed) > 3)
                    approximate++;

                // Green: 255 = A, 0 = B.
                // Intermediate values blend A/B.
                if (green > 0)
                    a[pair] = true;

                if (green < 255)
                    b[pair] = true;
            }

            var rows = new List<string>();

            for (int i = 0; i < 16; i++)
            {
                if (a[i])
                    rows.Add($"{i + 1}A");

                if (b[i])
                    rows.Add($"{i + 1}B");
            }

            result.Success = true;
            result.Rows = rows.ToArray();
            result.Width = width;
            result.Height = height;
            result.ApproximatePixels = approximate;

            result.Detail =
                "Texture-referenced only; visible mesh UV usage is unverified.";

            if (approximate > 0)
            {
                result.Detail +=
                    " Some red values required nearest-pair quantization.";
            }
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

    // ---------------------------------------------------------
    // PENUMBRA RESOURCE TREE
    // ---------------------------------------------------------

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
            .SelectMany(x => x.Materials)
            .SelectMany(x => x.Textures)
            .Where(x => x.IsId && x.Decoded?.Success == true)
            .SelectMany(x => x.Decoded!.Rows)
            .Distinct()
            .OrderBy(x =>
            {
                if (x.Length < 2)
                    return int.MaxValue;

                return int.TryParse(x[..^1], out int n)
                    ? n * 2 + (x.EndsWith("B") ? 1 : 0)
                    : int.MaxValue;
            });
    }

    private string MakeDiagnostics()
    {
        var sb = new StringBuilder();

        sb.AppendLine("Advanced Dye Row Inspector — DEV2");
        sb.AppendLine("Version: 0.0.2.0");
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
                                "    Texture-referenced rows: " +
                                string.Join(", ", texture.Decoded.Rows));
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
                ImGui.TextWrapped(
                    "Texture-referenced rows: " +
                    (result.Rows.Length > 0
                        ? string.Join(", ", result.Rows)
                        : "(none detected)"));

                ImGui.TextDisabled(
                    $"{result.Width}x{result.Height} | " +
                    "Texture-wide, NOT UV-confirmed.");

                if (result.ApproximatePixels > 0)
                {
                    ImGui.TextDisabled(
                        $"Quantized red-channel pixels: " +
                        result.ApproximatePixels);
                }
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

        if (showPaths)
            DrawPath("Resolved", material.ActualPath);

        var ids = material.Textures.Where(x => x.IsId).ToList();

        if (ids.Count == 0)
        {
            ImGui.TextDisabled(
                "No ID texture associated by this resource tree.");
        }
        else
        {
            var rows = ids
                .Where(x => x.Decoded?.Success == true)
                .SelectMany(x => x.Decoded!.Rows)
                .Distinct()
                .ToArray();

            if (rows.Length > 0)
            {
                ImGui.TextWrapped(
                    "Referenced rows: " + string.Join(", ", rows));
                ImGui.TextDisabled(
                    "Texture-wide references; not confirmed visible.");

                if (ImGui.SmallButton(
                    $"Copy Rows##copy_{index}_{material.GetHashCode()}"))
                {
                    ImGui.SetClipboardText(string.Join(", ", rows));
                }
            }
            else
            {
                ImGui.TextDisabled(
                    "Row data unavailable or no rows detected.");
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

        if (!ImGui.TreeNode(
            $"{name}##model_{index}"))
            return;

        DrawPath("Model", model.GamePath);

        if (showPaths)
            DrawPath("Resolved", model.ActualPath);

        if (model.Materials.Count == 0)
        {
            ImGui.TextDisabled(
                "No materials linked beneath this model in the resource tree.");
        }
        else
        {
            ImGui.TextDisabled(
                $"{model.Materials.Count} linked material(s)");

            for (int i = 0; i < model.Materials.Count; i++)
                DrawMaterial(model.Materials[i], i);
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
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(
            new Vector2(800, 620),
            ImGuiCond.FirstUseEver);

        bool open = windowOpen;

        bool visible = ImGui.Begin(
            "Advanced Dye Row Inspector###ADRIMain",
            ref open,
            ImGuiWindowFlags.NoCollapse);

        windowOpen = open;

        if (!visible)
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("Advanced Dye Row Inspector");
        ImGui.SameLine();
        ImGui.TextDisabled("DEV2 — 0.0.2");

        ImGui.Separator();

        ImGui.TextUnformatted("Actor: Current Character");
        ImGui.TextWrapped("Name: " + actorName);
        ImGui.TextUnformatted("Object index: " + actorIndex);

        if (ImGui.Button("Scan Current Appearance"))
            Scan();

        ImGui.SameLine();

        if (ImGui.Button("Refresh"))
            Scan();

        ImGui.SameLine();

        if (ImGui.Button("Copy Diagnostics"))
            ImGui.SetClipboardText(MakeDiagnostics());

        ImGui.SameLine();

        var linkedRows = LinkedRows().ToArray();

        if (linkedRows.Length > 0)
        {
            if (ImGui.Button("Copy All Rows"))
            {
                ImGui.SetClipboardText(
                    string.Join(", ", linkedRows));
            }
        }

        ImGui.Spacing();

        ImGui.Checkbox("Outfit only", ref outfitOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Show paths", ref showPaths);
        ImGui.SameLine();
        ImGui.Checkbox("Raw diagnostics", ref showRaw);

        ImGui.Separator();

        ImGui.TextWrapped("Resource tree: " + treeStatus);
        ImGui.TextWrapped("Resource paths: " + pathStatus);
        ImGui.TextWrapped("Glamourer: " + glamourerStatus);
        ImGui.TextWrapped(status);

        ImGui.Spacing();

        ImGui.TextDisabled(
            "Row values come from actual decoded ID textures.");
        ImGui.TextDisabled(
            "Rows may include unused texture regions. UV visibility is not verified.");

        ImGui.Separator();

        if (!scanned)
        {
            ImGui.TextWrapped(
                "Click Scan Current Appearance to inspect your outfit.");
            ImGui.End();
            return;
        }

        if (ImGui.BeginChild(
            "##ADRIResults",
            new Vector2(0, 0),
            true))
        {
            foreach (string slot in SlotOrder)
            {
                var entries = models
                    .Where(x => x.Slot == slot)
                    .Where(x => !outfitOnly || x.IsOutfit)
                    .ToList();

                if (entries.Count == 0)
                    continue;

                if (!ImGui.CollapsingHeader(
                    $"{slot} ({entries.Count} models)###slot_{slot}"))
                    continue;

                ImGui.Indent();

                for (int i = 0; i < entries.Count; i++)
                    DrawModel(entries[i], i);

                ImGui.Unindent();
                ImGui.Spacing();
            }

            if (!outfitOnly && orphans.Count > 0)
            {
                if (ImGui.CollapsingHeader(
                    $"Unlinked materials ({orphans.Count})"))
                {
                    ImGui.TextDisabled(
                        "No model relationship was established.");

                    for (int i = 0; i < orphans.Count; i++)
                        DrawMaterial(orphans[i], i);
                }
            }

            if (showRaw || models.Count == 0)
                DrawRaw();
        }

        ImGui.EndChild();
        ImGui.End();
    }
}