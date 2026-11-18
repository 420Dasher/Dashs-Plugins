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
            ImGui.TextDisabled(material.GamePath);

        if (!uvReports.TryGetValue(
            material, out var report) ||
            report.CleanSamples <= 0 ||
            report.Triangles <= 0)
        {
            ImGui.TextDisabled(
                "UV analysis unavailable for this material.");

            return;
        }

        if (report.Capped ||
            !string.IsNullOrWhiteSpace(report.Status))
        {
            ImGui.TextDisabled(
                "Partial UV analysis; see troubleshooting.");
        }

        var ranked = RankedUvRows(report).ToArray();

        if (ranked.Length == 0)
        {
            ImGui.TextDisabled(
                "No preliminary UV candidates found.");
            return;
        }

        ImGui.Spacing();

        ImGui.TextDisabled(
            "Preliminary UV candidates (UNVERIFIED)");

        ImGui.TextDisabled(
            $"Sparse UV sampling: {report.Triangles:N0} " +
            $"triangles, {report.Meshes} mesh(es)");

        int shown = compactShowMoreRows
            ? ranked.Length
            : Math.Min(5, ranked.Length);

        for (int i = 0; i < shown; i++)
        {
            var row = ranked[i];

            ImGui.TextUnformatted(row.Row);
            ImGui.SameLine();

            float width = Math.Min(
                250f,
                Math.Max(
                    110f,
                    ImGui.GetContentRegionAvail().X));

            ImGui.ProgressBar(
                Math.Clamp(
                    (float)(row.Percent / 100.0),
                    0f,
                    1f),
                new Vector2(width, 0f),
                $"{row.Percent:F1}%");
        }

        if (ranked.Length > 5)
        {
            string label = compactShowMoreRows
                ? "Show fewer UV rows"
                : $"Show {ranked.Length - 5} more UV rows";

            if (ImGui.SmallButton(
                $"{label}##uvmore_{index}"))
            {
                compactShowMoreRows = !compactShowMoreRows;
            }
        }

        ImGui.TextDisabled(
            "Experimental UV samples, not confirmed " +
            "active-mesh dye coverage.");
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
                $"Triangles sampled: {uvTriangles:N0}");

            DrawMeshIssueDiagnostics();

            // Keep useful development tools available,
            // but remove their clutter from the main UI.

            DrawMaterialAudit(selectedModels);

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

    private static void FitCompactWindow(float requiredHeight)
    {
        var display = ImGui.GetIO().DisplaySize;

        float width = MathF.Min(
            520f,
            MathF.Max(200f, display.X - 24f));

        float maxHeight = MathF.Max(
            220f,
            display.Y - 48f);

        float height = MathF.Min(
            MathF.Max(requiredHeight + 12f, 235f),
            maxHeight);

        ImGui.SetWindowSize(
            new Vector2(width, height),
            ImGuiCond.Always);

        // Keep the resized window within the display.
        var position = ImGui.GetWindowPos();

        float maxX = MathF.Max(4f, display.X - width - 4f);
        float maxY = MathF.Max(4f, display.Y - height - 4f);

        float x = Math.Clamp(position.X, 4f, maxX);
        float y = Math.Clamp(position.Y, 4f, maxY);

        if (x != position.X || y != position.Y)
        {
            ImGui.SetWindowPos(
                new Vector2(x, y),
                ImGuiCond.Always);
        }
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
            "v0.0.3.20  |  Read-only UV groundwork");

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
            if (ImGui.Button("Copy UV rows"))
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

        bool fitWindowRequested =
            ImGui.SmallButton("Fit window");

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