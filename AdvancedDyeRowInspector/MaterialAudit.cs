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
    private const uint NormalSamplerId = 0x0C5EC1F1u;

    private sealed class MaterialAudit
    {
        public string Shader = "";
        public string IndexTexture = "";
        public string Status = "";
        public string Evidence = "";
        public int DatasetBytes;
        public readonly List<string> SamplerBindings = new();
        public string NormalTexture = "";
        public string DerivedIndexCandidate = "";
        public string CandidateStatus = "";
        public bool IndexDeclared;
        public bool TreePathMatched;
        public bool ReadSucceeded;

        public int DeclaredTextureCount;
        public int ExplicitSamplerCount;
        public int ResolvedDeclaredTextures;
        public int IdNamedDeclaredTextures;

        public string SourceStatus = "";

        public readonly List<string>
            TextureDeclarations = new();
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

    private static string SamplerRole(uint id)
    {
        return id switch
        {
            IndexSamplerId  => "Index",
            NormalSamplerId => "Normal",
            0x8A4E82B6u   => "Mask",
            0x115306BEu   => "Diffuse",
            0x2B99E025u   => "Specular",
            _             => "Unknown"
        };
    }

    private void InspectDeclaredTexturePaths(
        MtrlFile mtrl,
        MaterialEntry material,
        MaterialAudit audit)
    {
        audit.DeclaredTextureCount =
            mtrl.TextureOffsets.Length;

        audit.ExplicitSamplerCount =
            mtrl.Samplers.Length;

        // Limit displayed declarations to protect
        // the UI from malformed material resources.
        int count = Math.Min(
            audit.DeclaredTextureCount, 64);

        for (int i = 0; i < count; i++)
        {
            string declared;

            try
            {
                declared = AuditString(
                    mtrl.Strings,
                    mtrl.TextureOffsets[i].Offset);
            }
            catch (Exception ex)
            {
                audit.TextureDeclarations.Add(
                    $"[{i}] Invalid declaration: " +
                    ex.GetType().Name);
                continue;
            }

            bool idNamed = IsIdTexture(
                declared, declared);

            if (idNamed)
                audit.IdNamedDeclaredTextures++;

            var links = material.Textures
                .Where(t =>
                    !string.IsNullOrWhiteSpace(t.GamePath) &&
                    string.Equals(
                        AuditPath(t.GamePath),
                        AuditPath(declared),
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var resolved = links
                .Select(t => AuditPath(t.ActualPath))
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            string state;

            if (links.Length == 0)
            {
                state = "not linked in resource tree";
            }
            else if (resolved.Length == 1)
            {
                audit.ResolvedDeclaredTextures++;
                state = "one Penumbra-resolved source";
            }
            else if (resolved.Length > 1)
            {
                state = "ambiguous resolved sources";
            }
            else
            {
                state = "linked; resolution unavailable";
            }

            string type = idNamed
                ? "ID-named candidate (unverified)"
                : "Declared texture";

            audit.TextureDeclarations.Add(
                $"[{i}] {type}: {declared} | {state}");
        }

        if (audit.DeclaredTextureCount > count)
        {
            audit.TextureDeclarations.Add(
                "Remaining declarations omitted by " +
                "the diagnostic safety limit.");
        }
    }

    // Diagnostic candidate logic only.
    // Mirrors common normal/index naming conventions
    // without claiming an actual shader binding.
    private static string CandidateIndexPath(string normal)
    {
        string path = AuditPath(normal);

        if (path.EndsWith(
            "_norm.tex", StringComparison.OrdinalIgnoreCase))
            return path[..^9] + "_id.tex";

        if (path.EndsWith(
            "_n.tex", StringComparison.OrdinalIgnoreCase))
            return path[..^6] + "_id.tex";

        return "";
    }

    private void InspectSamplerBindings(
        MtrlFile mtrl,
        MaterialEntry material,
        MaterialAudit audit)
    {
        foreach (var sampler in mtrl.Samplers)
        {
            string path;

            if (sampler.TextureIndex >=
                mtrl.TextureOffsets.Length)
            {
                path = "(invalid texture index)";
            }
            else
            {
                int offset = mtrl.TextureOffsets[
                    sampler.TextureIndex].Offset;

                try
                {
                    path = AuditString(mtrl.Strings, offset);
                }
                catch (Exception ex)
                {
                    path = "(invalid texture path: " +
                        ex.Message + ")";
                }
            }

            audit.SamplerBindings.Add(
                $"0x{sampler.SamplerId:X8} " +
                $"{SamplerRole(sampler.SamplerId)} -> {path}");
        }

        // This is a separate heuristic. Do not use it to
        // override an explicitly declared index sampler.
        if (mtrl.Samplers.Any(
            sampler => sampler.SamplerId == IndexSamplerId))
            return;

        var normal = mtrl.Samplers
            .Where(s => s.SamplerId == NormalSamplerId)
            .ToArray();

        if (normal.Length != 1)
        {
            audit.CandidateStatus = normal.Length == 0
                ? "No normal sampler available for fallback analysis."
                : "Normal sampler declaration is ambiguous.";
            return;
        }

        int index = normal[0].TextureIndex;

        if (index >= mtrl.TextureOffsets.Length)
        {
            audit.CandidateStatus =
                "Normal sampler texture index is invalid.";
            return;
        }

        try
        {
            audit.NormalTexture = AuditString(
                mtrl.Strings,
                mtrl.TextureOffsets[index].Offset);
        }
        catch (Exception ex)
        {
            audit.CandidateStatus =
                "Cannot read normal map path: " + ex.Message;
            return;
        }

        audit.DerivedIndexCandidate =
            CandidateIndexPath(audit.NormalTexture);

        if (audit.DerivedIndexCandidate.Length == 0)
        {
            audit.CandidateStatus =
                "Normal texture name does not provide " +
                "a conventional ID candidate.";
            return;
        }

        var matches = material.Textures
            .Where(t => !string.IsNullOrWhiteSpace(t.GamePath))
            .Where(t => string.Equals(
                AuditPath(t.GamePath),
                audit.DerivedIndexCandidate,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
        {
            audit.CandidateStatus =
                "Candidate not found in this material's " +
                "Penumbra resource subtree.";
            return;
        }

        int resolutions = matches
            .Select(t => AuditPath(t.ActualPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        audit.CandidateStatus = resolutions == 1
            ? "Candidate exists in Penumbra's resource tree. " +
              "Actual shader usage remains UNVERIFIED."
            : "Candidate has ambiguous Penumbra resolutions. " +
              "Actual shader usage remains UNVERIFIED.";
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

            InspectDeclaredTexturePaths(
                mtrl, material, audit);

            InspectSamplerBindings(mtrl, material, audit);

            var samplers = mtrl.Samplers
                .Where(s => s.SamplerId == IndexSamplerId)
                .ToArray();

            if (samplers.Length == 0)
            {
                audit.Status =
                    "No explicit index sampler declared.";

                audit.SourceStatus =
                    "Row source unresolved: no index sampler " +
                    "is explicitly declared in the MTRL.";

                if (mtrl.Samplers.Any(
                    s => s.SamplerId == NormalSamplerId))
                {
                    audit.SourceStatus =
                        "Normal sampler declared without index " +
                        "sampler. Legacy mapping remains unverified.";
                }

                audit.Evidence =
                    "Missing explicit samplers does not imply " +
                    "missing loaded textures. See declarations " +
                    "and runtime resource associations.";

                return audit;
            }

            if (samplers.Length != 1)
            {
                audit.Status =
                    "Multiple index sampler declarations.";

                audit.SourceStatus =
                    "Ambiguous index sampler declarations. " +
                    "No dye-row source selected.";

                audit.Evidence =
                    "Sampler binding is ambiguous.";

                return audit;
            }

            audit.IndexDeclared = true;

            audit.SourceStatus =
                "Index sampler declared; resource-path " +
                "verification required.";

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

                audit.SourceStatus =
                    "Index sampler and unique Penumbra-resolved " +
                    "resource path agree. Texture source identified; " +
                    "active-mesh coverage remains unverified.";

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

                ImGui.TextDisabled(
                    $"MTRL: {audit.DeclaredTextureCount} " +
                    $"textures, {audit.ExplicitSamplerCount} " +
                    "explicit samplers");

                ImGui.TextWrapped(
                    audit.SourceStatus);

                if (ImGui.TreeNode(
                    $"Declared textures " +
                    $"({audit.DeclaredTextureCount})" +
                    $"##adri_decl_{material.GetHashCode()}"))
                {
                    foreach (string declaration in
                        audit.TextureDeclarations)
                    {
                        ImGui.TextWrapped(declaration);
                    }

                    ImGui.TreePop();
                }
            }

            if (!string.IsNullOrWhiteSpace(
                audit.IndexTexture))
            {
                ImGui.TextWrapped(
                    "Declared index map: " +
                    audit.IndexTexture);
            }

            if (!string.IsNullOrWhiteSpace(audit.NormalTexture))
            {
                ImGui.TextWrapped(
                    "Normal map: " + audit.NormalTexture);
            }

            if (!string.IsNullOrWhiteSpace(
                audit.DerivedIndexCandidate))
            {
                ImGui.TextWrapped(
                    "Possible ID map: " +
                    audit.DerivedIndexCandidate);
            }

            if (!string.IsNullOrWhiteSpace(audit.CandidateStatus))
                ImGui.TextWrapped(audit.CandidateStatus);

            if (ImGui.TreeNode(
                $"Sampler bindings ({audit.SamplerBindings.Count})" +
                $"##samplers_{material.GamePath}_{material.ActualPath}"))
            {
                foreach (string binding in audit.SamplerBindings)
                    ImGui.TextWrapped(binding);

                ImGui.TreePop();
            }

            ImGui.TextWrapped(audit.Status);

            if (!string.IsNullOrWhiteSpace(audit.Evidence))
                ImGui.TextWrapped(audit.Evidence);
        }

        DrawRuntimeMaterialBindings(materials);

        DrawPreviewTargetResolver(materials, selectedModels);

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
                "MTRL TEXTURE DECLARATIONS: " +
                entry.Value.DeclaredTextureCount);

            sb.AppendLine(
                "MTRL EXPLICIT SAMPLERS: " +
                entry.Value.ExplicitSamplerCount);

            sb.AppendLine(
                "PENUMBRA-RESOLVED DECLARED TEXTURES: " +
                entry.Value.ResolvedDeclaredTextures);

            sb.AppendLine(
                "ID-NAMED DECLARED CANDIDATES: " +
                entry.Value.IdNamedDeclaredTextures);

            sb.AppendLine(
                "SOURCE ASSESSMENT: " +
                entry.Value.SourceStatus);

            foreach (string declaration in
                entry.Value.TextureDeclarations)
            {
                sb.AppendLine(
                    "DECLARED TEXTURE: " + declaration);
            }

            sb.AppendLine(
                "NORMAL MAP: " + entry.Value.NormalTexture);

            sb.AppendLine(
                "DERIVED INDEX CANDIDATE: " +
                entry.Value.DerivedIndexCandidate);

            sb.AppendLine(
                "CANDIDATE STATUS: " +
                entry.Value.CandidateStatus);

            foreach (string binding in entry.Value.SamplerBindings)
                sb.AppendLine("SAMPLER: " + binding);

            sb.AppendLine(
                "STATUS: " + entry.Value.Status);

            sb.AppendLine(
                "EVIDENCE: " + entry.Value.Evidence);
        }

        sb.AppendLine();
    }
}