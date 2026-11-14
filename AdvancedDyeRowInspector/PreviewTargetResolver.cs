using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Newtonsoft.Json.Linq;

using NativeGameObject =
    FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    private sealed class PreviewTargetEvidence
    {
        public string ModelPath = "";
        public string MaterialPath = "";
        public int ModelSlot;
        public int MaterialSlot;
        public uint Width;
        public uint Height;
        public string Format = "";
        public bool Compatible;
    }

    // No native pointers are retained between frames.
    private readonly List<PreviewTargetEvidence>
        previewTargets = new();

    private string previewTargetStatus = "Not checked.";

    private static bool ReadPreviewPointer(
        JObject node,
        out nint pointer)
    {
        pointer = nint.Zero;

        string value = JsonString(node, "ObjectAddress");

        if (!long.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out long address) ||
            address <= 0)
        {
            return false;
        }

        pointer = (nint)address;
        return pointer != nint.Zero;
    }

    private static void CollectPreviewPairs(
        JObject node,
        JObject? currentModel,
        List<(JObject Model, JObject Material)> pairs,
        ref int visited,
        int depth)
    {
        if (++visited > 4000 || depth > 40)
        {
            throw new InvalidOperationException(
                "Preview resource-tree safety limit exceeded.");
        }

        string game = JsonString(node, "GamePath");
        string actual = JsonString(node, "ActualPath");

        string effective = string.IsNullOrWhiteSpace(game)
            ? actual
            : game;

        string kind = Kind(effective);

        if (kind == "Model")
            currentModel = node;

        if (kind == "Material" && currentModel != null)
            pairs.Add((currentModel, node));

        if (node.GetValue(
            "Children",
            StringComparison.OrdinalIgnoreCase) is not JArray children)
        {
            return;
        }

        foreach (var child in children.OfType<JObject>())
        {
            CollectPreviewPairs(
                child,
                currentModel,
                pairs,
                ref visited,
                depth + 1);
        }
    }

    private unsafe void ResolvePreviewTargets(
        MaterialEntry[] materials,
        List<ModelEntry> selectedModels)
    {
        previewTargets.Clear();

        if (!scanned)
        {
            previewTargetStatus = "Scan an outfit first.";
            return;
        }

        var actor = Objects.LocalPlayer;

        if (actor == null ||
            actor.Address == nint.Zero ||
            actor.ObjectIndex.ToString() != actorIndex ||
            actor.Name.TextValue != actorName)
        {
            previewTargetStatus =
                "Character differs from scan. Rescan first.";
            return;
        }

        if (!treeIpc.HasFunction)
        {
            previewTargetStatus =
                "Penumbra runtime resource API unavailable.";
            return;
        }

        if (materials.Length == 0 || selectedModels.Count == 0)
        {
            previewTargetStatus =
                "No selected outfit material available.";
            return;
        }

        try
        {
            var gameObject = (NativeGameObject*)actor.Address;

            var drawObject = (CharacterBase*)gameObject->DrawObject;

            if (drawObject == null)
            {
                previewTargetStatus =
                    "Character has no loaded draw object.";
                return;
            }

            if (drawObject->SlotCount <= 0 ||
                drawObject->SlotCount > 24 ||
                drawObject->Models == null ||
                drawObject->ColorTableTextures == null)
            {
                previewTargetStatus =
                    "Unsupported draw-object slot layout.";
                return;
            }

            // Request the live resource tree only on click.
            // No colorset texture is changed here.
            var result = treeIpc.InvokeFunc(
                true,
                new ushort[] { (ushort)actor.ObjectIndex });

            if (result == null ||
                result.Length == 0 ||
                result[0] == null)
            {
                previewTargetStatus =
                    "No runtime resource snapshot returned.";
                return;
            }

            JObject root = result[0]!;

            if (root.GetValue(
                "Nodes",
                StringComparison.OrdinalIgnoreCase) is not JArray nodes)
            {
                previewTargetStatus =
                    "Runtime resource tree has no nodes.";
                return;
            }

            var pairs =
                new List<(JObject Model, JObject Material)>();

            int visited = 0;

            foreach (var node in nodes.OfType<JObject>())
            {
                CollectPreviewPairs(
                    node,
                    null,
                    pairs,
                    ref visited,
                    0);
            }

            foreach (var pair in pairs)
            {
                string modelGame =
                    JsonString(pair.Model, "GamePath");

                string modelActual =
                    JsonString(pair.Model, "ActualPath");

                string materialGame =
                    JsonString(pair.Material, "GamePath");

                string materialActual =
                    JsonString(pair.Material, "ActualPath");

                bool selectedModel = selectedModels.Any(m =>
                    SameRuntimeResource(
                        m.GamePath,
                        m.ActualPath,
                        modelGame,
                        modelActual));

                bool selectedMaterial = materials.Any(m =>
                    SameRuntimeResource(
                        m.GamePath,
                        m.ActualPath,
                        materialGame,
                        materialActual));

                if (!selectedModel || !selectedMaterial)
                    continue;

                if (!ReadPreviewPointer(
                        pair.Model, out nint modelAddress) ||
                    !ReadPreviewPointer(
                        pair.Material, out nint materialAddress))
                {
                    continue;
                }

                // Cross-check Penumbra's pointers against
                // the current draw object's native model
                // and material arrays.
                for (int slot = 0;
                     slot < drawObject->SlotCount;
                     slot++)
                {
                    var renderModel = drawObject->Models[slot];

                    if (renderModel == null ||
                        (nint)renderModel != modelAddress)
                    {
                        continue;
                    }

                    if (renderModel->Materials == null ||
                        renderModel->MaterialCount <= 0 ||
                        renderModel->MaterialCount >
                            CharacterBase.MaterialsPerSlot)
                    {
                        continue;
                    }

                    for (int index = 0;
                         index < renderModel->MaterialCount;
                         index++)
                    {
                        if ((nint)renderModel->Materials[index] !=
                            materialAddress)
                        {
                            continue;
                        }

                        bool duplicate = previewTargets.Any(t =>
                            t.ModelSlot == slot &&
                            t.MaterialSlot == index);

                        if (duplicate)
                            continue;

                        int colorTableIndex =
                            slot * CharacterBase.MaterialsPerSlot +
                            index;

                        var texture = drawObject
                            ->ColorTableTextures[colorTableIndex];

                        var evidence = new PreviewTargetEvidence
                        {
                            ModelPath = modelGame,
                            MaterialPath = materialGame,
                            ModelSlot = slot,
                            MaterialSlot = index
                        };

                        if (texture != null)
                        {
                            evidence.Width = texture->ActualWidth;
                            evidence.Height = texture->ActualHeight;
                            evidence.Format =
                                texture->TextureFormat.ToString();

                            evidence.Compatible =
                                texture->ActualWidth == 8 &&
                                texture->ActualHeight == 32 &&
                                texture->TextureFormat ==
                                    TextureFormat.R16G16B16A16_FLOAT;
                        }
                        else
                        {
                            evidence.Format =
                                "No live colorset texture";
                        }

                        previewTargets.Add(evidence);
                    }
                }
            }

            int compatible =
                previewTargets.Count(t => t.Compatible);

            previewTargetStatus =
                $"Resolved {previewTargets.Count} material slot(s); " +
                $"{compatible} compatible 8x32 colorset texture(s).";
        }
        catch (Exception ex)
        {
            previewTargets.Clear();

            previewTargetStatus =
                "Preview target inspection failed: " +
                ex.GetType().Name + ": " + ex.Message;
        }
    }

    private void DrawPreviewTargetResolver(
        MaterialEntry[] materials,
        List<ModelEntry> selectedModels)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Live colorset preview readiness");

        if (ImGui.SmallButton(
            "Check preview targets##ADRITargets"))
        {
            ResolvePreviewTargets(materials, selectedModels);
        }

        ImGui.TextDisabled(previewTargetStatus);

        foreach (var target in previewTargets)
        {
            ImGui.BulletText(
                $"Model slot {target.ModelSlot}, " +
                $"material {target.MaterialSlot}");

            ImGui.Indent();

            ImGui.TextWrapped(
                $"{target.Width}x{target.Height} - " +
                target.Format);

            ImGui.TextDisabled(
                target.Compatible
                    ? "Compatible with a Dawntrail colorset preview."
                    : "Not currently a compatible preview target.");

            ImGui.Unindent();
        }

        ImGui.TextDisabled(
            "Read-only validation. Does not modify live textures.");

        ImGui.TextDisabled(
            "Native pointers are checked and discarded immediately.");
    }

    private void AppendPreviewTargetDiagnostics(
        StringBuilder sb)
    {
        sb.AppendLine("[DEV6 PREVIEW TARGET RESOLUTION]");
        sb.AppendLine(previewTargetStatus);

        foreach (var target in previewTargets)
        {
            sb.AppendLine(
                $"SLOT: {target.ModelSlot}, " +
                $"MATERIAL: {target.MaterialSlot}");

            sb.AppendLine(
                $"SIZE: {target.Width}x{target.Height}");

            sb.AppendLine("FORMAT: " + target.Format);

            sb.AppendLine(
                "COMPATIBLE: " + target.Compatible);

            sb.AppendLine(
                "MODEL: " + target.ModelPath);

            sb.AppendLine(
                "MATERIAL PATH: " + target.MaterialPath);
        }

        sb.AppendLine();
    }
}