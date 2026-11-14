using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Dalamud.Bindings.ImGui;
using Lumina.Data.Files;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    // g_SamplerIndex -- index/ID texture sampler.
    // Verified against Penumbra.GameData's shader IDs.
    private const uint IndexSamplerId = 0x565F8FD8u;

    private sealed class MaterialAudit
    {
        public string Shader = "";
        public string IndexTexture = "";
        public string Status = "";
        public string Evidence = "";
        public int DatasetBytes;
        public bool IndexDeclared;
        public bool TreePathMatched;
        public bool ReadSucceeded;
    }

    private readonly Dictionary<MaterialEntry, MaterialAudit>
        materialAudits = new();

    private DateTime materialAuditScanTime;

    private static string AuditPath(string path)
    {
        return path.Trim().Replace('\\', '/');
    }

    private static string AuditString(
        byte[] strings,
        int offset)
    {
        if (strings == null ||
            offset < 0 ||
            offset >= strings.Length)
        {
            throw new InvalidDataException(
                "Invalid material string-table offset.");
        }

        int end = Array.IndexOf(
            strings,
            (byte)0,
            offset);

        if (end < 0)
        {
            throw new InvalidDataException(
                "Unterminated material string-table value.");
        }

        return Encoding.UTF8.GetString(
            strings,
            offset,
            end - offset);
    }

    private static string AuditFileName(string path)
    {
        string cleaned = AuditPath(path);
        int last = cleaned.LastIndexOf('/');

        return last < 0
            ? cleaned
            : cleaned[(last + 1)..];
    }

    private MaterialAudit InspectMaterialFile(
        MaterialEntry material)
    {
        var audit = new MaterialAudit();

        try
        {
            if (string.IsNullOrWhiteSpace(material.GamePath))
            {
                audit.Status = "Material game path unavailable.";
                return audit;
            }

            MtrlFile? mtrl;

            string actual = material.ActualPath;

            if (Path.IsPathRooted(actual))
            {
                var info = new FileInfo(actual);

                if (!info.Exists ||
                    info.Length <= 0 ||
                    info.Length > 16L * 1024 * 1024)
                {
                    audit.Status =
                        "Resolved material absent or exceeds 16 MiB.";
                    return audit;
                }

                mtrl = Data.GameData.GetFileFromDisk<MtrlFile>(
                    actual,
                    material.GamePath);
            }
            else
            {
                string path = string.IsNullOrWhiteSpace(actual)
                    ? material.GamePath
                    : actual;

                mtrl = Data.GetFile<MtrlFile>(path);
            }

            if (mtrl == null)
            {
                audit.Status = "Material file unavailable.";
                return audit;
            }

            if (mtrl.Strings == null ||
                mtrl.TextureOffsets == null ||
                mtrl.Samplers == null)
            {
                audit.Status =
                    "Material file lacks required sampler data.";
                return audit;
            }

            audit.ReadSucceeded = true;
            audit.DatasetBytes = mtrl.FileHeader.DataSetSize;

            audit.Shader = AuditString(
                mtrl.Strings,
                mtrl.FileHeader.ShaderPackageNameOffset);

            var samplers = mtrl.Samplers
                .Where(s => s.SamplerId == IndexSamplerId)
                .ToArray();

            if (samplers.Length == 0)
            {
                audit.Status =
                    "No index sampler declared by this material.";

                audit.Evidence =
                    "Filename-based ID texture detection " +
                    "cannot verify shader usage.";

                return audit;
            }

            if (samplers.Length != 1)
            {
                audit.Status =
                    "Multiple index sampler declarations.";

                audit.Evidence =
                    "Sampler binding is ambiguous.";

                return audit;
            }

            audit.IndexDeclared = true;

            int textureIndex = samplers[0].TextureIndex;

            if (textureIndex < 0 ||
                textureIndex >= mtrl.TextureOffsets.Length)
            {
                audit.Status = "Index sampler texture index invalid.";
                return audit;
            }

            int textureOffset =
                mtrl.TextureOffsets[textureIndex].Offset;

            audit.IndexTexture = AuditString(
                mtrl.Strings,
                textureOffset);

            if (string.IsNullOrWhiteSpace(audit.IndexTexture))
            {
                audit.Status = "Index sampler has an empty texture path.";
                return audit;
            }

            // A genuine match requires the full game texture
            // path declared by the MTRL to appear in the
            // selected material's Penumbra resource subtree.
            // Matching filenames alone are insufficient.

            string declared = AuditPath(audit.IndexTexture);

            var exactMatches = material.Textures
                .Where(t => !string.IsNullOrWhiteSpace(t.GamePath))
                .Where(t => string.Equals(
                    AuditPath(t.GamePath),
                    declared,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            int resolutions = exactMatches
                .Select(t => AuditPath(t.ActualPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            if (exactMatches.Length > 0 && resolutions == 1)
            {
                audit.TreePathMatched = true;
                audit.Status =
                    "Index sampler path matches Penumbra's resource tree.";

                audit.Evidence =
                    "Resolved texture: " +
                    (string.IsNullOrWhiteSpace(
                        exactMatches[0].ActualPath)
                        ? "(not supplied)"
                        : exactMatches[0].ActualPath);

                return audit;
            }

            if (exactMatches.Length > 0)
            {
                audit.Status =
                    "Index sampler path has ambiguous resolutions.";

                audit.Evidence =
                    "Multiple different resolved paths " +
                    "were reported for the same texture.";

                return audit;
            }

            var filenameCandidates = material.Textures
                .Where(t => string.Equals(
                    AuditFileName(t.GamePath),
                    AuditFileName(declared),
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (filenameCandidates.Length > 0)
            {
                audit.Status =
                    "Filename matches, but full resource path does not.";

                audit.Evidence =
                    "Unverified candidate only. " +
                    "ADRI will not treat this as confirmation.";
            }
            else
            {
                audit.Status =
                    "Declared index texture not linked in resource tree.";

                audit.Evidence =
                    "The sampler exists in the MTRL, " +
                    "but no matching Penumbra texture path was found.";
            }
        }
        catch (Exception ex)
        {
            audit.Status =
                "Material inspection unavailable.";

            audit.Evidence =
                ex.GetType().Name + ": " + ex.Message;
        }

        return audit;
    }

    private void DrawMaterialAudit(
        List<ModelEntry> selectedModels)
    {
        if (!ImGui.TreeNode(
            "Material / index sampler verification##ADRIAudit"))
            return;

        if (!scanned)
        {
            ImGui.TextDisabled("Scan an outfit first.");
            ImGui.TreePop();
            return;
        }

        // Invalidate evidence whenever the user rescans.
        if (materialAuditScanTime != scanTime)
        {
            materialAudits.Clear();
            materialAuditScanTime = scanTime;
        }

        var materials = selectedModels
            .SelectMany(m => m.Materials)
            .Where(IsOutfitMaterial)
            .Distinct()
            .ToArray();

        if (materials.Length == 0)
        {
            ImGui.TextDisabled(
                "No selected outfit materials to verify.");

            ImGui.TreePop();
            return;
        }

        // Process at most one material per frame.
        // This keeps disk parsing out of bulk UI loops.
        foreach (var material in materials)
        {
            if (materialAudits.ContainsKey(material))
                continue;

            materialAudits.Add(
                material,
                InspectMaterialFile(material));

            break;
        }

        foreach (var material in materials)
        {
            ImGui.Separator();

            ImGui.TextUnformatted(
                FriendlyMaterialName(material.GamePath));

            if (!materialAudits.TryGetValue(
                material,
                out var audit))
            {
                ImGui.TextDisabled("Material audit pending.");
                continue;
            }

            if (audit.ReadSucceeded)
            {
                ImGui.TextDisabled(
                    "Shader: " + audit.Shader);

                ImGui.TextDisabled(
                    $"Color dataset: {audit.DatasetBytes} bytes");
            }

            if (!string.IsNullOrWhiteSpace(
                audit.IndexTexture))
            {
                ImGui.TextWrapped(
                    "Declared index map: " +
                    audit.IndexTexture);
            }

            ImGui.TextWrapped(audit.Status);

            if (!string.IsNullOrWhiteSpace(audit.Evidence))
                ImGui.TextWrapped(audit.Evidence);
        }

        ImGui.Spacing();

        ImGui.TextDisabled(
            "Read-only MTRL declaration vs. Penumbra links.");

        ImGui.TextDisabled(
            "This does not confirm live GPU binding or visibility.");

        ImGui.TreePop();
    }

    private void AppendMaterialAuditDiagnostics(
        StringBuilder sb)
    {
        sb.AppendLine("[DEV5 MATERIAL / INDEX SAMPLER AUDIT]");
        sb.AppendLine(
            "Read-only material-file declarations; " +
            "NOT live shader binding proof.");

        if (materialAuditScanTime != scanTime)
        {
            sb.AppendLine("No current material audits requested.");
            sb.AppendLine();
            return;
        }

        foreach (var entry in materialAudits)
        {
            sb.AppendLine(
                "MATERIAL: " + entry.Key.GamePath);

            sb.AppendLine(
                "RESOLVED: " + entry.Key.ActualPath);

            sb.AppendLine(
                "SHADER: " + entry.Value.Shader);

            sb.AppendLine(
                "INDEX: " + entry.Value.IndexTexture);

            sb.AppendLine(
                "INDEX DECLARED: " +
                entry.Value.IndexDeclared);

            sb.AppendLine(
                "FULL TREE PATH MATCH: " +
                entry.Value.TreePathMatched);

            sb.AppendLine(
                "DATASET BYTES: " +
                entry.Value.DatasetBytes);

            sb.AppendLine(
                "STATUS: " + entry.Value.Status);

            sb.AppendLine(
                "EVIDENCE: " + entry.Value.Evidence);
        }

        sb.AppendLine();
    }
}