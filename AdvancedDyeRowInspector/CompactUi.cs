using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

using Dalamud.Bindings.ImGui;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    private string compactSlot = "Legs";
    private bool compactAdvanced;
    private bool compactShowMoreRows;

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

    private string[] CompactRows(string slot)
    {
        if (pendingUvModels.Count > 0)
            return Array.Empty<string>();

        return CompactMaterials(slot)
            .Where(m => uvReports.TryGetValue(
                m, out var r) && r.CleanSamples > 0)
            .SelectMany(m => RankedUvRows(uvReports[m]))
            .Select(r => r.Row)
            .Distinct()
            .OrderBy(RowOrder)
            .ToArray();
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
        ImGui.SameLine();
        ImGui.TextDisabled("Experimental UV analysis");

        ImGui.TextDisabled(
            "Material: " + FriendlyMaterialName(material.GamePath));

        var sharedSlots = VerifiedSharedSlots(
            material, compactSlot);

        if (sharedSlots.Length > 0)
        {
            ImGui.TextDisabled(
                "Shared with: " + string.Join(", ", sharedSlots));
        }

        if (compactAdvanced && showPaths)
            ImGui.TextDisabled(material.GamePath);

        if (!uvReports.TryGetValue(
            material, out var report) ||
            report.CleanSamples <= 0 ||
            report.Triangles <= 0)
        {
            ImGui.TextDisabled(
                "UV analysis unavailable for this material.");

            ImGui.TextDisabled(
                "Texture-wide candidates remain in Advanced.");

            return;
        }

        ImGui.TextDisabled(
            $"UV sampled  |  {report.Triangles:N0} triangles  |  " +
            $"{report.Meshes} mesh(es)");

        if (report.Capped ||
            !string.IsNullOrWhiteSpace(report.Status))
        {
            ImGui.TextDisabled(
                "Partial UV results - review diagnostics.");
        }

        double cleanPercent = report.Samples > 0
            ? 100.0 * report.CleanSamples / report.Samples
            : 0.0;

        if (cleanPercent < 95.0)
        {
            ImGui.TextDisabled(
                $"ID sample quality: {cleanPercent:F1}% " +
                "matched the expected red-channel palette.");
        }

        var ranked = RankedUvRows(report).ToArray();

        if (ranked.Length == 0)
        {
            ImGui.TextDisabled(
                "No sampled rows above the current threshold.");
            return;
        }

        int shown = compactShowMoreRows
            ? ranked.Length
            : Math.Min(5, ranked.Length);

        for (int i = 0; i < shown; i++)
        {
            var row = ranked[i];

            ImGui.TextUnformatted(row.Row);
            ImGui.SameLine();

            ImGui.ProgressBar(
                Math.Clamp(
                    (float)(row.Percent / 100.0),
                    0f, 1f),
                new Vector2(
                    Math.Min(
                        270f,
                        Math.Max(
                            120f,
                            ImGui.GetContentRegionAvail().X)),
                    0f),
                $"{row.Percent:F1}%");
        }

        if (ranked.Length > 5)
        {
            string label = compactShowMoreRows
                ? "Show fewer rows"
                : $"Show {ranked.Length - 5} more rows";

            if (ImGui.SmallButton(
                $"{label}##more_{index}"))
            {
                compactShowMoreRows = !compactShowMoreRows;
            }
        }

        ImGui.SameLine();

        if (ImGui.SmallButton(
            $"Copy UV rows##copyuv_{index}"))
        {
            ImGui.SetClipboardText(
                string.Join(
                    ", ",
                    ranked
                        .Select(x => x.Row)
                        .OrderBy(RowOrder)));
        }

        ImGui.Spacing();
        ImGui.TextDisabled(
            "UV sample influence, not confirmed on-screen visibility.");
    }

    private void DrawCompactAdvanced(
        List<ModelEntry> selectedModels)
    {
        if (!compactAdvanced)
            return;

        ImGui.Separator();

        ImGui.TextDisabled("Advanced inspection");

        ImGui.Checkbox("Show paths", ref showPaths);
        ImGui.SameLine();
        ImGui.Checkbox("Outfit only", ref outfitOnly);

        ImGui.Checkbox(
            "Body / other dependencies", ref showDependencies);

        ImGui.SameLine();
        ImGui.Checkbox("Raw resources", ref showRaw);

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

        ImGui.TextDisabled(
            $"Geometry: {uvProcessedModels}/{uvTotalModels} models, " +
            $"{uvMatchedMeshes} matched meshes, " +
            $"{uvUnmatchedMeshes} unmatched");

        ImGui.TextDisabled(
            $"Triangles sampled: {uvTriangles:N0}");

        DrawMeshIssueDiagnostics();

        ImGui.TextWrapped(
            $"Penumbra: {treeStatus}");

        ImGui.TextWrapped(
            $"Resource paths: {pathStatus}");

        ImGui.TextWrapped(
            $"Glamourer: {glamourerStatus}");

        if (ImGui.Button("Copy full diagnostics"))
            ImGui.SetClipboardText(MakeDiagnostics());

        ImGui.Spacing();

        if (ImGui.TreeNode(
            "Detailed models, materials and textures"))
        {
            for (int i = 0; i < selectedModels.Count; i++)
                DrawModel(selectedModels[i], i);

            ImGui.TreePop();
        }

        if (showRaw)
            DrawRaw();

        ImGui.Spacing();
        ImGui.TextDisabled(
            "Theme follows the active Dalamud ImGui style.");
    }

    private void DrawCompact()
    {
        if (!windowOpen)
            return;

        // Keep the existing incremental geometry workflow.
        ProcessGeometryStep();

        ImGui.SetNextWindowSize(
            new Vector2(520f, 420f),
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
            ImGuiWindowFlags.NoCollapse);

        windowOpen = open;

        if (!visible)
        {
            ImGui.End();
            ImGui.PopStyleVar();
            return;
        }

        ImGui.TextDisabled(
            "v0.0.3.5  |  Experimental UV analysis");

        ImGui.Spacing();

        if (ImGui.Button("Scan outfit"))
        {
            Scan();
            compactShowMoreRows = false;
        }

        ImGui.SameLine();

        var selectedRows = CompactRows(compactSlot);

        if (selectedRows.Length > 0)
        {
            if (ImGui.Button("Copy slot rows"))
            {
                ImGui.SetClipboardText(
                    string.Join(", ", selectedRows));
            }

            ImGui.SameLine();
        }

        if (ImGui.SmallButton(
            compactAdvanced ? "Hide details" : "Advanced"))
        {
            compactAdvanced = !compactAdvanced;
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("Fit window"))
        {
            ImGui.SetWindowSize(
                new Vector2(520f, 420f),
                ImGuiCond.Always);
        }

        ImGui.Spacing();

        if (!scanned)
        {
            ImGui.TextDisabled(
                "Scan your outfit to inspect its dye rows.");

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

            ImGui.End();
            ImGui.PopStyleVar();
            return;
        }

        if (!availableSlots.Contains(compactSlot))
            compactSlot = availableSlots[0];

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
                    compactShowMoreRows = false;
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
        }

        ImGui.EndChild();
        ImGui.End();

        ImGui.PopStyleVar();
    }
}