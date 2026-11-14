using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Dalamud.Bindings.ImGui;
using Newtonsoft.Json.Linq;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    private sealed class RuntimeTextureEvidence
    {
        public string GamePath = "";
        public string ResolvedPath = "";
        public string Name = "";
        public string Handle = "";
        public bool IndexNamed;
        public bool IdCandidate;
    }

    private sealed class RuntimeMaterialEvidence
    {
        public int MaterialNodes;
        public readonly List<RuntimeTextureEvidence> Textures = new();
    }

    private readonly Dictionary<MaterialEntry, RuntimeMaterialEvidence>
        runtimeMaterialEvidence = new();

    private string runtimeBindingStatus = "Not checked.";

    // A strong resource association requires BOTH the
    // original game path and the resolved resource path.
    private static bool SameRuntimeResource(
        string gameA,
        string actualA,
        string gameB,
        string actualB)
    {
        if (string.IsNullOrWhiteSpace(gameA) ||
            string.IsNullOrWhiteSpace(gameB) ||
            string.IsNullOrWhiteSpace(actualA) ||
            string.IsNullOrWhiteSpace(actualB))
        {
            return false;
        }

        return string.Equals(
                AuditPath(gameA),
                AuditPath(gameB),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                AuditPath(actualA),
                AuditPath(actualB),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRuntimeIndexLabel(string name)
    {
        // Normalize labels such as g_SamplerIndex.
        // Generic "Texture #0" labels are not proof.
        string normalized = new string(
            name.Where(char.IsLetterOrDigit).ToArray());

        return normalized.Contains(
                "SamplerIndex",
                StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(
                "IndexSampler",
                StringComparison.OrdinalIgnoreCase);
    }

    private void WalkRuntimeBindingTree(
        JObject node,
        MaterialEntry[] requested,
        MaterialEntry[] active,
        ref int visited,
        int depth)
    {
        if (++visited > 4000 || depth > 40)
        {
            throw new InvalidOperationException(
                "Runtime tree safety limit exceeded.");
        }

        string game = JsonString(node, "GamePath");
        string actual = JsonString(node, "ActualPath");

        string effective = string.IsNullOrWhiteSpace(game)
            ? actual
            : game;

        string kind = Kind(effective);

        if (kind == "Model")
        {
            active = Array.Empty<MaterialEntry>();
        }
        else if (kind == "Material")
        {
            active = requested
                .Where(m => SameRuntimeResource(
                    m.GamePath,
                    m.ActualPath,
                    game,
                    actual))
                .ToArray();

            foreach (var material in active)
            {
                runtimeMaterialEvidence[material]
                    .MaterialNodes++;
            }
        }
        else if (kind == "Texture" && active.Length > 0)
        {
            string name = JsonString(node, "Name");
            string handle = JsonString(node, "ResourceHandle");

            foreach (var material in active)
            {
                var evidence = runtimeMaterialEvidence[material];

                bool alreadyRecorded = evidence.Textures.Any(t =>
                    SameRuntimeResource(
                        t.GamePath,
                        t.ResolvedPath,
                        game,
                        actual)
                    && string.Equals(
                        t.Handle,
                        handle,
                        StringComparison.Ordinal));

                if (alreadyRecorded)
                    continue;

                bool idCandidate = material.Textures.Any(t =>
                    t.IsId &&
                    SameRuntimeResource(
                        t.GamePath,
                        t.ActualPath,
                        game,
                        actual));

                evidence.Textures.Add(
                    new RuntimeTextureEvidence
                    {
                        GamePath = game,
                        ResolvedPath = actual,
                        Name = name,
                        Handle = handle,
                        IndexNamed = HasRuntimeIndexLabel(name),
                        IdCandidate = idCandidate
                    });
            }
        }

        if (node.GetValue(
            "Children",
            StringComparison.OrdinalIgnoreCase) is not JArray children)
        {
            return;
        }

        foreach (var child in children.OfType<JObject>())
        {
            WalkRuntimeBindingTree(
                child,
                requested,
                active,
                ref visited,
                depth + 1);
        }
    }

    private void CaptureRuntimeMaterialBindings(
        MaterialEntry[] materials)
    {
        runtimeMaterialEvidence.Clear();
        runtimeBindingStatus = "Checking runtime resources...";

        if (!scanned)
        {
            runtimeBindingStatus = "Scan an outfit first.";
            return;
        }

        var actor = Objects.LocalPlayer;

        if (actor == null ||
            actor.ObjectIndex.ToString() != actorIndex ||
            actor.Name.TextValue != actorName)
        {
            runtimeBindingStatus =
                "Character differs from the scan. Rescan first.";
            return;
        }

        if (!treeIpc.HasFunction)
        {
            runtimeBindingStatus =
                "Penumbra resource-tree API unavailable.";
            return;
        }

        try
        {
            // withUiData=true: request labels derived
            // from Penumbra's live shader/resource tree.
            // This IPC call is read-only and on-demand.
            var result = treeIpc.InvokeFunc(
                true,
                new ushort[] { (ushort)actor.ObjectIndex });

            if (result == null ||
                result.Length == 0 ||
                result[0] == null)
            {
                runtimeBindingStatus =
                    "Penumbra returned no runtime snapshot.";
                return;
            }

            JObject root = result[0]!;

            if (root.GetValue(
                "Nodes",
                StringComparison.OrdinalIgnoreCase) is not JArray nodes)
            {
                runtimeBindingStatus =
                    "Runtime snapshot contains no nodes.";
                return;
            }

            foreach (var material in materials)
            {
                runtimeMaterialEvidence.Add(
                    material,
                    new RuntimeMaterialEvidence());
            }

            int visited = 0;

            foreach (var node in nodes.OfType<JObject>())
            {
                WalkRuntimeBindingTree(
                    node,
                    materials,
                    Array.Empty<MaterialEntry>(),
                    ref visited,
                    0);
            }

            int found = runtimeMaterialEvidence.Values
                .Count(e => e.MaterialNodes > 0);

            int textures = runtimeMaterialEvidence.Values
                .Sum(e => e.Textures.Count);

            int named = runtimeMaterialEvidence.Values
                .Sum(e => e.Textures.Count(t => t.IndexNamed));

            runtimeBindingStatus =
                $"Snapshot: {found}/{materials.Length} materials, " +
                $"{textures} texture links, " +
                $"{named} index-labeled links.";
        }
        catch (Exception ex)
        {
            runtimeMaterialEvidence.Clear();

            runtimeBindingStatus =
                "Runtime inspection failed: " +
                ex.GetType().Name + ": " + ex.Message;
        }
    }

    private void DrawRuntimeMaterialBindings(
        MaterialEntry[] materials)
    {
        ImGui.Spacing();
        ImGui.Separator();

        ImGui.TextUnformatted("Loaded texture associations");

        if (ImGui.SmallButton(
            "Check runtime bindings##ADRIRuntime"))
        {
            CaptureRuntimeMaterialBindings(materials);
        }

        ImGui.TextDisabled(runtimeBindingStatus);

        foreach (var material in materials)
        {
            if (!runtimeMaterialEvidence.TryGetValue(
                material,
                out var evidence))
            {
                continue;
            }

            ImGui.Spacing();

            ImGui.TextUnformatted(
                FriendlyMaterialName(material.GamePath));

            if (evidence.MaterialNodes == 0)
            {
                ImGui.TextDisabled(
                    "No matching loaded material resource.");
                continue;
            }

            ImGui.TextDisabled(
                $"Loaded material nodes: {evidence.MaterialNodes}");

            if (evidence.Textures.Count == 0)
            {
                ImGui.TextDisabled(
                    "No linked texture handles reported.");
                continue;
            }

            foreach (var texture in evidence.Textures)
            {
                string classification = texture.IndexNamed
                    ? "Index-labeled"
                    : texture.IdCandidate
                        ? "ID filename candidate"
                        : "Other texture";

                string label = string.IsNullOrWhiteSpace(
                    texture.Name)
                    ? "(unlabeled)"
                    : texture.Name;

                ImGui.BulletText(
                    classification + " - " + label);

                ImGui.Indent();

                ImGui.TextWrapped(texture.GamePath);

                if (texture.IndexNamed)
                {
                    ImGui.TextDisabled(
                        "Runtime shader label identifies " +
                        "an index-sampler association.");
                }
                else if (texture.IdCandidate)
                {
                    ImGui.TextDisabled(
                        "Filename candidate only; shader " +
                        "association remains unverified.");
                }

                ImGui.Unindent();
            }
        }

        ImGui.TextDisabled(
            "Loaded resources do not prove rendered " +
            "visibility or actual pixel influence.");
    }

    private void AppendRuntimeMaterialDiagnostics(
        StringBuilder sb)
    {
        sb.AppendLine(
            "[DEV5 FIX2 RUNTIME MATERIAL BINDINGS]");

        sb.AppendLine(runtimeBindingStatus);

        foreach (var pair in runtimeMaterialEvidence)
        {
            sb.AppendLine(
                "MATERIAL: " + pair.Key.GamePath);

            sb.AppendLine(
                "RESOLVED: " + pair.Key.ActualPath);

            sb.AppendLine(
                "MATERIAL NODES: " + pair.Value.MaterialNodes);

            foreach (var texture in pair.Value.Textures)
            {
                sb.AppendLine(
                    "  LABEL: " + texture.Name);

                sb.AppendLine(
                    "  GAME: " + texture.GamePath);

                sb.AppendLine(
                    "  RESOLVED: " + texture.ResolvedPath);

                sb.AppendLine(
                    "  HANDLE: " + texture.Handle);

                sb.AppendLine(
                    "  INDEX LABELED: " + texture.IndexNamed);

                sb.AppendLine(
                    "  ID CANDIDATE: " + texture.IdCandidate);
            }
        }

        sb.AppendLine();
    }
}