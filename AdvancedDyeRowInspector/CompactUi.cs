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
    private MaterialEntry? compactHoverMaterial;
    private int compactHoverRow = -1;
    private DateTime compactHoverSince;
    private bool compactHoverAttempted;
    private bool compactHoverOwnsPreview;

    private void UpdateCompactHover(
        MaterialEntry? material, int row,
        List<ModelEntry> selectedModels)
    {
        if (!ReferenceEquals(compactHoverMaterial, material) ||
            compactHoverRow != row)
        {
            if (compactHoverOwnsPreview && isoOriginal != 0)
                StopMaterialIsolation("Hover preview restored.");

            compactHoverOwnsPreview = false;
            compactHoverMaterial = material;
            compactHoverRow = row;
            compactHoverSince = DateTime.UtcNow;
            compactHoverAttempted = false;
        }

        if (material == null || compactHoverAttempted ||
            (DateTime.UtcNow - compactHoverSince).TotalMilliseconds < 150)
            return;

        compactHoverAttempted = true;
        StartMaterialIsolation(material, selectedModels, row);
        compactHoverOwnsPreview = isoOriginal != 0;
    }


    // Use the actual Dalamud theme.
    // No hardcoded colors or plugin-global ImGui overrides.

    private List<ModelEntry> CompactModels(string slot)
    {
        return models
            .Where(m => m.Slot == slot)
            .Where(m => m.IsOutfit)
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
        int index,
        ref MaterialEntry? hoveredMaterial,
        ref int hoveredRowIndex)
    {
        ImGui.Separator();

        ImGui.TextUnformatted(
            NativeMaterialDisplayName(material));

        ImGui.TextDisabled(
            "MTRL: " + DisplayName(material.GamePath));

        var sharedSlots = VerifiedSharedSlots(
            material, compactSlot);

        if (sharedSlots.Length > 0)
        {
            ImGui.TextDisabled(
                "Shared with: " +
                string.Join(", ", sharedSlots));
        }

        if (compactAdvanced) ImGui.TextWrapped(material.ActualPath);

        if (pendingUvModels.Count > 0)
        {
            ImGui.TextDisabled("Scanning UV candidates...");
            return;
        }

        if (!nativeMaterialIndices.ContainsKey(material))
        {
            ImGui.TextDisabled(
                "Row preview unavailable: native index unverified.");
            return;
        }

        if (!uvReports.TryGetValue(material, out var report) ||
            report.Capped ||
            report.CleanSamples == 0 ||
            uvIncompleteMaterials.Contains(material) ||
            uvRaceVariantMaterials.Contains(material))
        {
            ImGui.TextDisabled(
                "UV candidates incomplete or unavailable.");
            return;
        }

        var rows = RankedUvRows(report).ToArray();

        if (rows.Length == 0)
        {
            ImGui.TextDisabled(
                "No UV candidates above the threshold.");
            return;
        }

        ImGui.TextDisabled("UV candidate rows");

        int columns = ImGui.GetContentRegionAvail().X >= 520f ? 3 :
            ImGui.GetContentRegionAvail().X >= 330f ? 2 : 1;

        if (ImGui.BeginTable(
            $"##ADRICompactRows_{index}", columns,
            ImGuiTableFlags.SizingStretchSame |
            ImGuiTableFlags.NoSavedSettings))
        {
            foreach (var row in rows)
            {
                int number = int.Parse(row.Row[..^1]);
                int rowIndex = (number - 1) * 2 +
                    (row.Row[^1] == 'B' ? 1 : 0);

                ImGui.TableNextColumn();
                ImGui.Selectable(
                    $"{row.Row}   {row.Percent:F1}% UV##ADRIHover_{index}_{rowIndex}");

                if (ImGui.IsItemHovered())
                {
                    hoveredMaterial = material;
                    hoveredRowIndex = rowIndex;
                }
            }
            ImGui.EndTable();
        }
    }

    private void DrawCompactAdvanced(List<ModelEntry> selectedModels)
    {
        if (!compactAdvanced)
            return;

        ImGui.Separator();

        if (ImGui.TreeNode("Detection options"))
        {
            ImGui.SliderFloat(
                "Minimum UV influence (%)",
                ref minimumUvPercent, 0f, 10f, "%.2f%%");

            ImGui.Checkbox(
                "Flip UV V (rescan required)", ref flipUvV);

            ImGui.TreePop();
        }

        if (ImGui.TreeNode("Diagnostics"))
        {
            ImGui.TextDisabled(
                $"Geometry: {uvProcessedModels}/{uvTotalModels} models, " +
                $"{uvMatchedMeshes} matched meshes.");
            ImGui.TextDisabled(nativeMaterialLabelStatus);

            if (ImGui.Button("Copy full diagnostics"))
                ImGui.SetClipboardText(MakeDiagnostics());

            if (ImGui.TreeNode("Material UV details"))
            {
                foreach (var material in CompactMaterials(compactSlot))
                {
                    ImGui.Separator();
                    ImGui.TextUnformatted(
                        NativeMaterialDisplayName(material));
                    DrawUvReport(material);
                }
                ImGui.TreePop();
            }

            if (ImGui.TreeNode("Developer audits"))
            {
                DrawMeshIssueDiagnostics();
                DrawMaterialAudit(selectedModels);
                DrawActiveGeometryAudit(selectedModels);
                DrawSpatialGeometryAudit(selectedModels);

                ImGui.TextWrapped("Penumbra: " + treeStatus);
                ImGui.TextWrapped("Paths: " + pathStatus);
                ImGui.TextWrapped("Glamourer: " + glamourerStatus);

                ImGui.TreePop();
            }
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
        {
            UpdateCompactHover(null, -1, new List<ModelEntry>());
            return;
        }

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
            UpdateCompactHover(null, -1, new List<ModelEntry>());
            ImGui.End();
            ImGui.PopStyleVar();
            return;
        }

        ImGui.TextDisabled(
            $"v{PluginBuildVersion}  |  UV coverage-mask analysis");

        ImGui.Spacing();

        if (ImGui.Button("Scan outfit", new Vector2(114f, 0f)))
            Scan();

        ImGui.SameLine();
        if (ImGui.Button(
            compactAdvanced ? "Hide details" : "Details",
            new Vector2(108f, 0f)))
            compactAdvanced = !compactAdvanced;

        ImGui.SameLine();
        bool fitWindowRequested =
            ImGui.Button("Fit", new Vector2(56f, 0f));

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fit window to content.");
        ImGui.Spacing();

        ImGui.TextDisabled(
            "Glamourer Advanced Dyes: " +
            (dyeProbeError.Length > 0 ? "PROBE ERROR" :
             !dyeProbeInitialized ? "waiting" :
             dyePopupOpen ? "OPEN" : "closed") +
            $" | opened {dyePopupOpenEvents}, closed {dyePopupCloseEvents}");

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

            UpdateCompactHover(null, -1, new List<ModelEntry>()); DrawCompactAdvanced(new List<ModelEntry>());

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

        ImGui.TextDisabled(
            isoOriginal != 0 ||
            isoStatus.StartsWith(
                "Highlight failed:", StringComparison.Ordinal)
                ? isoStatus
                : "Hover a dye row to highlight it (max. 8s).");

        ImGui.BeginDisabled(isoOriginal == 0);
        if (ImGui.SmallButton(
            "Restore now##ADRICompactRestore"))
            StopMaterialIsolation("Original texture restored.");
        ImGui.EndDisabled();

        float resultsStartY = ImGui.GetCursorPosY();
        float resultsContentHeight = 0f;

        MaterialEntry? hoveredMaterial = null;
        int hoveredRowIndex = -1;

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
                    DrawCompactMaterial(materials[i], i, ref hoveredMaterial, ref hoveredRowIndex);
                }
            }

            UpdateCompactHover(hoveredMaterial, hoveredRowIndex, selectedModels); DrawCompactAdvanced(selectedModels);

            // Child-local position includes content below
            // the currently visible scroll area.
            resultsContentHeight = ImGui.GetCursorPosY();
        }
        else
        {
            UpdateCompactHover(null, -1, selectedModels);
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