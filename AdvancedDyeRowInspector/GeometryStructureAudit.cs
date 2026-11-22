using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lumina.Data.Files;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    // Static model-structure diagnostics only.
    // Submesh attributes and shape records are not proof
    // of which geometry the game currently renders.

    private readonly List<string> structureAudit = new();

    private const int MaxStructureAuditLines = 512;
    private int structureLinesOmitted;
    private int structureMeshes;
    private int structureSubmeshes;
    private int structureRangeWarnings;

    private void ResetStructureAudit()
    {
        structureAudit.Clear();
        structureLinesOmitted = 0;
        structureMeshes = 0;
        structureSubmeshes = 0;
        structureRangeWarnings = 0;
    }

    private void AddStructureLine(string message)
    {
        if (structureAudit.Count >= MaxStructureAuditLines)
        {
            structureLinesOmitted++;
            return;
        }

        structureAudit.Add(message);
    }

    private static string DecodeAttributeMask(
        MdlFile mdl, uint mask)
    {
        if (mask == 0)
            return "(no attribute bits)";

        var names = new List<string>();

        for (int bit = 0; bit < 32; bit++)
        {
            if ((mask & (1u << bit)) == 0)
                continue;

            if (bit >= mdl.AttributeNameOffsets.Length)
            {
                names.Add($"bit{bit}:unmapped");
                continue;
            }

            uint rawOffset = mdl.AttributeNameOffsets[bit];

            if (rawOffset >= mdl.Strings.Length)
            {
                names.Add($"bit{bit}:invalid-offset");
                continue;
            }

            int start = (int)rawOffset;
            int end = Array.IndexOf(
                mdl.Strings, (byte)0, start);

            if (end < 0)
            {
                names.Add($"bit{bit}:unterminated");
                continue;
            }

            string name = Encoding.UTF8.GetString(
                mdl.Strings, start, end - start);

            names.Add($"bit{bit}:{name}");
        }

        return string.Join(", ", names);
    }

    private void AuditGeometryModel(
        ModelEntry model,
        MdlFile mdl,
        int first,
        int end)
    {
        AddStructureLine(
            $"MODEL {model.Slot} / {model.GamePath}");

        AddStructureLine(
            $"  LOD0 meshes={end - first}, " +
            $"submesh records={mdl.Submeshes.Length}, " +
            $"attribute names={mdl.AttributeNameOffsets.Length}, " +
            $"shape definitions={mdl.ModelHeader.ShapeCount}");

        AddStructureLine(
            "  Active attribute/shape selection: " +
            "UNVERIFIED; no runtime filtering applied.");

        if (mdl.AttributeNameOffsets.Length > 32)
        {
            AddStructureLine(
                "  Warning: attribute count exceeds " +
                "the 32-bit submesh mask.");
        }

        for (int meshIndex = first;
             meshIndex < end;
             meshIndex++)
        {
            var mesh = mdl.Meshes[meshIndex];
            structureMeshes++;

            AddStructureLine(
                $"  Mesh {meshIndex}: " +
                $"materialIndex={mesh.MaterialIndex}, " +
                $"vertices={mesh.VertexCount}, " +
                $"indices={mesh.IndexCount}, " +
                $"submeshes={mesh.SubMeshCount}");

            if (mesh.SubMeshCount == 0)
            {
                AddStructureLine(
                    "    No submesh records: " +
                    "whole mesh remains a candidate.");
                continue;
            }

            int subFirst = mesh.SubMeshIndex;
            int subEnd = subFirst + mesh.SubMeshCount;

            if (subEnd > mdl.Submeshes.Length)
            {
                structureRangeWarnings++;
                AddStructureLine(
                    "    INVALID submesh table range.");
                continue;
            }

            long meshStart = mesh.StartIndex;
            long meshEnd = meshStart + mesh.IndexCount;

            for (int s = subFirst; s < subEnd; s++)
            {
                var sub = mdl.Submeshes[s];
                structureSubmeshes++;

                long start = sub.IndexOffset;
                long finish = start + sub.IndexCount;

                bool valid =
                    start >= meshStart &&
                    finish <= meshEnd &&
                    finish >= start;

                if (!valid)
                    structureRangeWarnings++;

                string attributes = DecodeAttributeMask(
                    mdl, sub.AttributeIndexMask);

                AddStructureLine(
                    $"    Submesh {s}: " +
                    $"indices=[{start},{finish}), " +
                    $"mask=0x{sub.AttributeIndexMask:X8}, " +
                    $"rangeValid={valid}, " +
                    $"attributes={attributes}");
            }
        }
    }

    private void MarkMaterialIncomplete(
        ModelEntry model,
        MdlFile mdl,
        int meshIndex)
    {
        string materialName;

        try
        {
            materialName = ReadMaterialName(
                mdl, mdl.Meshes[meshIndex].MaterialIndex);
        }
        catch
        {
            return;
        }

        foreach (var material in model.Materials
            .Where(IsOutfitMaterial)
            .Where(m => string.Equals(
                BaseName(m.GamePath),
                materialName,
                StringComparison.OrdinalIgnoreCase)))
        {
            uvIncompleteMaterials.Add(material);

            if (uvReports.TryGetValue(material, out var report))
                report.Capped = true;
        }
    }

    private void RecordCappedMesh(
        ModelEntry model,
        MdlFile mdl,
        int meshIndex,
        string reason)
    {
        string materialName = "(unresolved)";

        try
        {
            materialName = ReadMaterialName(
                mdl, mdl.Meshes[meshIndex].MaterialIndex);
        }
        catch
        {
            // Preserve the safety-budget classification.
        }

        MarkMaterialIncomplete(model, mdl, meshIndex);

        RecordMeshIssue(
            model,
            meshIndex,
            "UV safety-budget omission",
            materialName,
            reason + "; geometry structure was audited, " +
            "but this mesh was not UV-rasterized.");
    }

    private void AppendStructureAudit(StringBuilder sb)
    {
        sb.AppendLine("[STRUCTURAL GEOMETRY AUDIT]");
        sb.AppendLine(
            "LOD0 structural records only; " +
            "NOT runtime visibility or active-shape proof.");

        sb.AppendLine(
            $"Meshes: {structureMeshes}; " +
            $"submeshes: {structureSubmeshes}; " +
            $"range warnings: {structureRangeWarnings}; " +
            $"omitted diagnostic lines: {structureLinesOmitted}");

        foreach (string line in structureAudit)
            sb.AppendLine(line);

        sb.AppendLine();
    }
}