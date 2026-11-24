using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

using Dalamud.Bindings.ImGui;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    private string compactSlot = "Body";
    private bool compactAdvanced;
    private bool compactInitialWidthChecked;


    // Use the actual Dalamud theme.
    // No hardcoded colors or plugin-global ImGui overrides.

    private List<ModelEntry> CompactModels(string slot)
    {
        return models
            .Where(m => m.Slot == slot)
            .Where(m => !outfitOnly || m.IsOutfit)
            .Where(m => m.Materials.Any(IsOutfitMaterial))
            .ToList();
    }

    private List<MaterialEntry> CompactMaterials(string slot)
    {
        return CompactModels(slot)
            .SelectMany(m => m.Materials)
            .Where(IsOutfitMaterial)
            .Distinct()
            .ToList();
    }



    private static string FriendlyMaterialName(string gamePath)
    {
        string file = DisplayName(gamePath);

        file = Regex.Replace(
            file,
            @"^mt_[^_]+_(?:met|top|glv|dwn|sho|ear|nek|wrs|rir|ril)_",
            "",
            RegexOptions.IgnoreCase);

        if (file.EndsWith(".mtrl",
            StringComparison.OrdinalIgnoreCase))
        {
            file = file[..^5];
        }

        file = file.Replace('_', ' ').Trim();

        if (string.IsNullOrWhiteSpace(file))
            return "Outfit material";

        return char.ToUpperInvariant(file[0]) + file[1..];
    }

    // A shared material requires the same game resource
    // AND the same resolved resource, referenced by
    // distinct outfit equipment slots.
    //
    // Matching filenames or shared ID textures alone
    // are never sufficient evidence.

    private static string NormalizeResourcePath(string path)
    {
        return path.Trim().Replace('\\', '/');
    }

    private string[] VerifiedSharedSlots(
        MaterialEntry material,
        string selectedSlot)
    {
        if (string.IsNullOrWhiteSpace(material.GamePath) ||
            string.IsNullOrWhiteSpace(material.ActualPath))
        {
            return Array.Empty<string>();
        }

        string gamePath =
            NormalizeResourcePath(material.GamePath);

        string resolvedPath =
            NormalizeResourcePath(material.ActualPath);

        return models
            .Where(model =>
                model.IsOutfit &&
                model.Slot != selectedSlot &&
                model.Slot != "Other / Unassigned" &&
                SlotOrder.Contains(model.Slot))
            .Where(model => model.Materials.Any(candidate =>
                IsOutfitMaterial(candidate) &&
                !string.IsNullOrWhiteSpace(candidate.GamePath) &&
                !string.IsNullOrWhiteSpace(candidate.ActualPath) &&
                string.Equals(
                    NormalizeResourcePath(candidate.GamePath),
                    gamePath,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    NormalizeResourcePath(candidate.ActualPath),
                    resolvedPath,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(model => model.Slot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(slot => Array.IndexOf(SlotOrder, slot))
            .ToArray();
    }
    private void DrawCompactMaterial(
        MaterialEntry material,
        int index)
    {
        ImGui.Separator();

        ImGui.TextUnformatted(compactSlot);

        ImGui.TextDisabled(
            "Material: " +
            FriendlyMaterialName(material.GamePath));

        var sharedSlots = VerifiedSharedSlots(
            material, compactSlot);

        if (sharedSlots.Length > 0)
        {
            ImGui.TextDisabled(
                "Shared with: " +
                string.Join(", ", sharedSlots));
        }

        if (compactAdvanced && showPaths)
            ImGui.TextWrapped(material.GamePath);

        ImGui.TextDisabled(
            "Dye rows: not yet verified.");
    }

    private void DrawCompactAdvanced(
        List<ModelEntry> selectedModels)
    {
        if (!compactAdvanced)
            return;

        ImGui.Separator();
        ImGui.TextDisabled("Advanced");

        if (ImGui.TreeNode(
            "Display / sampling options"))
        {
            ImGui.Checkbox(
                "Show paths", ref showPaths);

            ImGui.SameLine();

            ImGui.Checkbox(
                "Outfit only", ref outfitOnly);

            ImGui.Checkbox(
                "Body / other dependencies",
                ref showDependencies);

            ImGui.SameLine();

            ImGui.Checkbox(
                "Raw resources", ref showRaw);

            ImGui.Checkbox(
                "All texture rows", ref showAllRows);

            if (!showAllRows)
            {
                ImGui.SliderFloat(
                    "Min. clean texture coverage (%)",
                    ref minimumCoverage,
                    0f, 2f, "%.2f%%");
            }

            ImGui.SliderFloat(
                "Min. UV influence (%)",
                ref minimumUvPercent,
                0f, 10f, "%.2f%%");

            ImGui.Checkbox(
                "Flip UV V (rescan required)",
                ref flipUvV);

            ImGui.TreePop();
        }

        if (ImGui.TreeNode(
            "Troubleshooting / developer tools"))
        {
            ImGui.TextDisabled(
                $"Geometry: {uvProcessedModels}/" +
                $"{uvTotalModels} models, " +
                $"{uvMatchedMeshes} matched meshes, " +
                $"{uvUnmatchedMeshes} unmatched");

            ImGui.TextDisabled(
                $"Triangles rasterized: {uvTriangles:N0}");

            DrawMeshIssueDiagnostics();

            if (ImGui.TreeNode(
                "Experimental UV sampling (developer only)"))
            {
                ImGui.TextDisabled(
                    "256x256 UV mask coverage only; " +
                    "not verified dye-row coverage.");

                foreach (var material in
                    CompactMaterials(compactSlot))
                {
                    ImGui.Separator();

                    ImGui.TextUnformatted(
                        FriendlyMaterialName(material.GamePath));

                    DrawUvReport(material);
                }

                ImGui.TreePop();
            }

            DrawMaterialAudit(selectedModels);
            DrawActiveGeometryAudit(selectedModels);
            DrawSpatialGeometryAudit(selectedModels);

            if (ImGui.TreeNode("Connection details"))
            {
                ImGui.TextWrapped(
                    $"Penumbra: {treeStatus}");

                ImGui.TextWrapped(
                    $"Resource paths: {pathStatus}");

                ImGui.TextWrapped(
                    $"Glamourer: {glamourerStatus}");

                ImGui.TreePop();
            }

            if (ImGui.SmallButton(
                "Copy full diagnostics"))
            {
                ImGui.SetClipboardText(
                    MakeDiagnostics());
            }

            if (ImGui.TreeNode(
                "Detailed models and textures"))
            {
                for (int i = 0;
                     i < selectedModels.Count;
                     i++)
                {
                    DrawModel(selectedModels[i], i);
                }

                ImGui.TreePop();
            }

            if (showRaw)
                DrawRaw();

            ImGui.TreePop();
        }
    }

    private static float PreferredCompactWidth(
        float screenWidth)
    {
        float available = MathF.Max(
            240f, screenWidth - 32f);

        float preferred = Math.Clamp(
            screenWidth * 0.48f,
            620f,
            960f);

        return MathF.Min(
            preferred, available);
    }

    private static void FitCompactWindow(
        float requiredHeight)
    {
        var display = ImGui.GetIO().DisplaySize;

        // Fit the content height, not the user's chosen width.
        // Only constrain width if it exceeds the viewport.
        float width = MathF.Min(
            ImGui.GetWindowSize().X,
            MathF.Max(240f, display.X - 24f));

        float maxHeight = MathF.Max(
            220f, display.Y - 48f);

        float height = MathF.Min(
            MathF.Max(
                requiredHeight + 12f, 280f),
            maxHeight);

        ImGui.SetWindowSize(
            new Vector2(width, height),
            ImGuiCond.Always);

        var position = ImGui.GetWindowPos();

        float maxX = MathF.Max(
            4f, display.X - width - 4f);

        float maxY = MathF.Max(
            4f, display.Y - height - 4f);

        ImGui.SetWindowPos(
            new Vector2(
                Math.Clamp(
                    position.X, 4f, maxX),
                Math.Clamp(
                    position.Y, 4f, maxY)),
            ImGuiCond.Always);
    }

    private void DrawCompact()
    {
        if (!windowOpen)
            return;

        // Keep the existing incremental geometry workflow.
        ProcessGeometryStep();

        var display = ImGui.GetIO().DisplaySize;

        ImGui.SetNextWindowSize(
            new Vector2(
                PreferredCompactWidth(display.X),
                MathF.Min(
                    560f,
                    MathF.Max(
                        300f, display.Y - 64f))),
            ImGuiCond.FirstUseEver);

        bool open = windowOpen;

        // Use the client's colors, rounding, borders,
        // buttons and scrollbars without palette overrides.
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing,
            new Vector2(7f, 5f));

        bool visible = ImGui.Begin(
            "Advanced Dye Row Inspector###ADRIMain",
            ref open,
            ImGuiWindowFlags.None);

        windowOpen = open;

        // Migrate the former saved 520px width once.
        // Subsequent user resizing stays untouched.
        if (visible && !compactInitialWidthChecked)
        {
            compactInitialWidthChecked = true;

            var current = ImGui.GetWindowSize();

            if (display.X > 1000f &&
                current.X >= 500f &&
                current.X <= 550f)
            {
                float width =
                    PreferredCompactWidth(display.X);

                float height = MathF.Min(
                    MathF.Max(current.Y, 440f),
                    MathF.Max(
                        220f, display.Y - 48f));

                ImGui.SetWindowSize(
                    new Vector2(width, height),
                    ImGuiCond.Always);

                var pos = ImGui.GetWindowPos();

                ImGui.SetWindowPos(
                    new Vector2(
                        Math.Clamp(
                            pos.X, 4f,
                            MathF.Max(
                                4f, display.X - width - 4f)),
                        Math.Clamp(
                            pos.Y, 4f,
                            MathF.Max(
                                4f, display.Y - height - 4f))),
                    ImGuiCond.Always);
            }
        }

        if (!visible)
        {
            ImGui.End();
            ImGui.PopStyleVar();
            return;
        }

        ImGui.TextDisabled(
            $"v{PluginBuildVersion}  |  UV coverage-mask analysis");

        ImGui.Spacing();

        if (ImGui.Button("Scan outfit"))
        {
            Scan();
        }

        ImGui.SameLine();



        if (ImGui.SmallButton(
            compactAdvanced ? "Hide details" : "Advanced"))
        {
            compactAdvanced = !compactAdvanced;
        }

        ImGui.SameLine();

        bool fitWindowRequested =
            ImGui.SmallButton("Fit window");

        ImGui.SameLine();

        // ImGui collapse is reversible from the title bar.
        // It does not unload ADRI or modify the character.
        if (ImGui.SmallButton("Minimize"))
        {
            ImGui.SetWindowCollapsed(
                true, ImGuiCond.Always);
        }

        ImGui.Spacing();

        if (!scanned)
        {
            ImGui.TextDisabled(
                "Scan your outfit to inspect its dye rows.");

            if (fitWindowRequested)
                FitCompactWindow(ImGui.GetCursorPosY() + 20f);

            ImGui.End();
            ImGui.PopStyleVar();
            return;
        }

        ImGui.TextUnformatted(actorName);

        ImGui.SameLine();

        if (pendingUvModels.Count > 0)
        {
            ImGui.TextDisabled(
                $"Scanning geometry {uvProcessedModels}/{uvTotalModels}");
        }
        else
        {
            ImGui.TextDisabled(
                $"{uvMatchedModels}/{uvTotalModels} UV models  |  " +
                $"{uvTriangles:N0} triangles");
        }

        if (uvUnmatchedMeshes > 0)
        {
            ImGui.TextDisabled(
                $"{uvUnmatchedMeshes} excluded meshes; " +
                "see Advanced for the breakdown.");
        }

        ImGui.Separator();

        var availableSlots = SlotOrder
            .Where(slot => CompactModels(slot).Count > 0)
            .ToArray();

        if (availableSlots.Length == 0)
        {
            ImGui.TextWrapped(
                "No outfit materials matched. " +
                "Open Advanced for resource diagnostics.");

            DrawCompactAdvanced(new List<ModelEntry>());

            if (fitWindowRequested)
                FitCompactWindow(ImGui.GetCursorPosY() + 20f);

            ImGui.End();
            ImGui.PopStyleVar();
            return;
        }

        // Retain the user's selected slot across rescans.
        // Prefer Body only when the previous slot is absent.
        if (!availableSlots.Contains(compactSlot))
            compactSlot = availableSlots.Contains("Body")
                ? "Body"
                : availableSlots[0];

        ImGui.TextUnformatted("Equipment");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(210f);

        if (ImGui.BeginCombo(
            "##ADRIEquipment", compactSlot))
        {
            foreach (string slot in availableSlots)
            {
                bool selected = slot == compactSlot;

                if (ImGui.Selectable(
                    $"{slot}##slot_{slot}", selected))
                {
                    compactSlot = slot;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        var selectedModels = CompactModels(compactSlot);
        var materials = CompactMaterials(compactSlot);

        ImGui.SameLine();
        ImGui.TextDisabled(
            $"{materials.Count} material(s)");

        ImGui.Spacing();

        float resultsStartY = ImGui.GetCursorPosY();
        float resultsContentHeight = 0f;

        if (ImGui.BeginChild(
            "##ADRICompactResults",
            new Vector2(0f, 0f),
            true))
        {
            if (materials.Count == 0)
            {
                ImGui.TextDisabled(
                    "No outfit materials available for this slot.");
            }
            else
            {
                for (int i = 0; i < materials.Count; i++)
                {
                    DrawCompactMaterial(materials[i], i);
                }
            }

            DrawCompactAdvanced(selectedModels);

            // Child-local position includes content below
            // the currently visible scroll area.
            resultsContentHeight = ImGui.GetCursorPosY();
        }

        ImGui.EndChild();

        if (fitWindowRequested)
        {
            float requiredHeight =
                resultsStartY +
                resultsContentHeight +
                ImGui.GetStyle().WindowPadding.Y * 2f +
                8f;

            FitCompactWindow(requiredHeight);
        }

        ImGui.End();

        ImGui.PopStyleVar();
    }
}