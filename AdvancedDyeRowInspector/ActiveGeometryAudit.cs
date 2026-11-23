using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Lumina.Data.Files;
using Newtonsoft.Json.Linq;

using NativeGameObject =
    FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    // Snapshot values only. Native pointers are
    // checked and discarded in the same invocation.
    private readonly List<string> activeGeometryLines = new();

    private string activeGeometryStatus = "Not checked.";
    private int activeGeometryOmitted;

    private const int MaxActiveGeometryLines = 256;

    private void ResetActiveGeometryAudit()
    {
        activeGeometryLines.Clear();
        activeGeometryStatus = "Not checked.";
        activeGeometryOmitted = 0;
    }

    private void AddActiveGeometryLine(string line)
    {
        if (activeGeometryLines.Count >= MaxActiveGeometryLines)
        {
            activeGeometryOmitted++;
            return;
        }

        activeGeometryLines.Add(line);
    }

    private static void CollectActiveModelNodes(
        JObject node,
        List<JObject> found,
        ref int visited,
        int depth)
    {
        if (++visited > 4000 || depth > 40)
        {
            throw new InvalidOperationException(
                "Runtime model-tree safety limit exceeded.");
        }

        string game = JsonString(node, "GamePath");
        string actual = JsonString(node, "ActualPath");

        string effective = string.IsNullOrWhiteSpace(game)
            ? actual
            : game;

        if (Kind(effective) == "Model")
            found.Add(node);

        if (node.GetValue(
            "Children",
            StringComparison.OrdinalIgnoreCase) is not JArray children)
        {
            return;
        }

        foreach (var child in children.OfType<JObject>())
        {
            CollectActiveModelNodes(
                child, found, ref visited, depth + 1);
        }
    }

    private unsafe void CaptureActiveGeometry(
        List<ModelEntry> selectedModels)
    {
        ResetActiveGeometryAudit();

        if (!scanned)
        {
            activeGeometryStatus = "Scan an outfit first.";
            return;
        }

        if (pendingUvModels.Count != 0)
        {
            activeGeometryStatus =
                "Geometry scan is still running.";
            return;
        }

        var actor = Objects.LocalPlayer;

        if (actor == null ||
            actor.Address == nint.Zero ||
            actor.ObjectIndex.ToString() != actorIndex ||
            actor.Name.TextValue != actorName)
        {
            activeGeometryStatus =
                "Character changed. Rescan the outfit first.";
            return;
        }

        if (!treeIpc.HasFunction)
        {
            activeGeometryStatus =
                "Penumbra resource-tree API unavailable.";
            return;
        }

        var requested = selectedModels
            .Where(m => m.IsOutfit)
            .Take(24)
            .ToArray();

        if (requested.Length == 0)
        {
            activeGeometryStatus =
                "No selected outfit model to inspect.";
            return;
        }

        try
        {
            var gameObject =
                (NativeGameObject*)actor.Address;

            var drawObject =
                (CharacterBase*)gameObject->DrawObject;

            if (drawObject == null ||
                drawObject->SlotCount <= 0 ||
                drawObject->SlotCount > 24 ||
                drawObject->Models == null)
            {
                activeGeometryStatus =
                    "Unsupported or missing native draw object.";
                return;
            }

            // Penumbra provides read-only model-resource
            // associations and native object addresses.
            var result = treeIpc.InvokeFunc(
                true,
                new ushort[] {
                    (ushort)actor.ObjectIndex
                });

            if (result == null ||
                result.Length == 0 ||
                result[0] == null)
            {
                activeGeometryStatus =
                    "No runtime resource snapshot returned.";
                return;
            }

            JObject root = result[0]!;

            if (root.GetValue(
                "Nodes",
                StringComparison.OrdinalIgnoreCase) is not JArray nodes)
            {
                activeGeometryStatus =
                    "Runtime resource tree contains no nodes.";
                return;
            }

            var runtimeModels = new List<JObject>();
            int visited = 0;

            foreach (var node in nodes.OfType<JObject>())
            {
                CollectActiveModelNodes(
                    node, runtimeModels, ref visited, 0);
            }

            int liveModels = 0;
            int inspectedSubmeshes = 0;
            int masksSatisfied = 0;
            int masksMissing = 0;

            foreach (var model in requested)
            {
                AddActiveGeometryLine(
                    "MODEL: " + model.GamePath);

                var candidates = runtimeModels
                    .Where(node => SameRuntimeResource(
                        model.GamePath,
                        model.ActualPath,
                        JsonString(node, "GamePath"),
                        JsonString(node, "ActualPath")))
                    .ToArray();

                if (candidates.Length != 1)
                {
                    AddActiveGeometryLine(
                        "  Runtime model association: " +
                        candidates.Length +
                        " candidates. Unique match required.");
                    continue;
                }

                if (!ReadPreviewPointer(
                    candidates[0], out nint address))
                {
                    AddActiveGeometryLine(
                        "  No usable runtime model address.");
                    continue;
                }

                int liveSlot = -1;

                for (int slot = 0;
                     slot < drawObject->SlotCount;
                     slot++)
                {
                    if ((nint)drawObject->Models[slot] == address)
                    {
                        liveSlot = slot;
                        break;
                    }
                }

                if (liveSlot < 0)
                {
                    AddActiveGeometryLine(
                        "  Model pointer not found in the " +
                        "current native draw-object model array.");
                    continue;
                }

                var nativeModel = drawObject->Models[liveSlot];

                if (nativeModel == null)
                {
                    AddActiveGeometryLine(
                        "  Native model became unavailable.");
                    continue;
                }

                liveModels++;

                uint enabledAttributes =
                    nativeModel->EnabledAttributeIndexMask;

                uint enabledShapes =
                    nativeModel->EnabledShapeKeyIndexMask;

                int materialCount = nativeModel->MaterialCount;

                bool materialArrayUsable =
                    nativeModel->Materials != null &&
                    materialCount >= 0 &&
                    materialCount <=
                        CharacterBase.MaterialsPerSlot;

                AddActiveGeometryLine(
                    $"  Live model array slot={liveSlot}, " +
                    $"native slot index={nativeModel->SlotIndex}, " +
                    $"material count={materialCount}");

                AddActiveGeometryLine(
                    $"  Enabled attribute mask=0x{enabledAttributes:X8}; " +
                    $"enabled shape-key mask=0x{enabledShapes:X8}");

                AddActiveGeometryLine(
                    "  Mask comparison is structural evidence, " +
                    "NOT a rendered draw-call or pixel test.");

                MdlFile? mdl;

                try
                {
                    mdl = LoadModelFile(model);
                }
                catch (Exception ex)
                {
                    AddActiveGeometryLine(
                        "  Model file read failed: " +
                        ex.GetType().Name);
                    continue;
                }

                if (mdl == null ||
                    mdl.Lods == null ||
                    mdl.Lods.Length == 0 ||
                    mdl.Meshes == null)
                {
                    AddActiveGeometryLine(
                        "  Static LOD0 model data unavailable.");
                    continue;
                }

                int first = mdl.Lods[0].MeshIndex;
                int end = first + mdl.Lods[0].MeshCount;

                if (first < 0 ||
                    end < first ||
                    end > mdl.Meshes.Length)
                {
                    AddActiveGeometryLine(
                        "  Invalid static LOD0 mesh range.");
                    continue;
                }

                AddActiveGeometryLine(
                    $"  Static shape definitions=" +
                    mdl.ModelHeader.ShapeCount +
                    "; current shape-key mask is recorded " +
                    "without inferring rendered shapes.");

                for (int meshIndex = first;
                     meshIndex < end;
                     meshIndex++)
                {
                    var mesh = mdl.Meshes[meshIndex];

                    int materialIndex = mesh.MaterialIndex;

                    bool materialLoaded =
                        materialArrayUsable &&
                        materialIndex >= 0 &&
                        materialIndex < materialCount &&
                        nativeModel->Materials[materialIndex] != null;

                    string materialName;

                    try
                    {
                        materialName = ReadMaterialName(
                            mdl, mesh.MaterialIndex);
                    }
                    catch
                    {
                        materialName = "(unresolved)";
                    }

                    AddActiveGeometryLine(
                        $"  Mesh {meshIndex}: " +
                        $"material={materialName}, " +
                        $"native material pointer present={materialLoaded}, " +
                        $"vertices={mesh.VertexCount}, " +
                        $"indices={mesh.IndexCount}");

                    if (mesh.IndexCount == 0 ||
                        mesh.VertexCount == 0)
                    {
                        AddActiveGeometryLine(
                            "    Empty mesh; no geometry eligibility inferred.");
                        continue;
                    }

                    int subFirst = mesh.SubMeshIndex;
                    int subEnd = subFirst + mesh.SubMeshCount;

                    if (subFirst < 0 ||
                        subEnd < subFirst ||
                        subEnd > mdl.Submeshes.Length)
                    {
                        AddActiveGeometryLine(
                            "    Invalid static submesh range.");
                        continue;
                    }

                    if (mesh.SubMeshCount == 0)
                    {
                        AddActiveGeometryLine(
                            "    No explicit submesh attribute records.");
                        continue;
                    }

                    for (int s = subFirst; s < subEnd; s++)
                    {
                        var sub = mdl.Submeshes[s];
                        uint required = sub.AttributeIndexMask;

                        bool allBitsPresent =
                            (required & enabledAttributes) == required;

                        inspectedSubmeshes++;

                        if (required != 0)
                        {
                            if (allBitsPresent)
                                masksSatisfied++;
                            else
                                masksMissing++;
                        }

                        string assessment = required == 0
                            ? "no required attribute bits"
                            : allBitsPresent
                                ? "all required bits enabled"
                                : "some required bits not enabled";

                        AddActiveGeometryLine(
                            $"    Submesh {s}: " +
                            $"required=0x{required:X8}, " +
                            assessment +
                            $", indices={sub.IndexCount}");
                    }
                }
            }

            activeGeometryStatus =
                $"Live models {liveModels}/{requested.Length}; " +
                $"submeshes inspected {inspectedSubmeshes}; " +
                $"nonzero masks satisfied {masksSatisfied}, " +
                $"missing bits {masksMissing}.";

        }
        catch (Exception ex)
        {
            activeGeometryLines.Clear();

            activeGeometryStatus =
                "Active geometry inspection failed: " +
                ex.GetType().Name + ": " + ex.Message;
        }
    }

    private void DrawActiveGeometryAudit(
        List<ModelEntry> selectedModels)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(
            "Read-only native model masks");

        if (ImGui.SmallButton(
            "Check active geometry masks##ADRIActiveGeometry"))
        {
            CaptureActiveGeometry(selectedModels);
        }

        ImGui.TextDisabled(activeGeometryStatus);

        ImGui.TextDisabled(
            "Attribute-bit compatibility is not " +
            "proof of rendered triangles.");

        if (activeGeometryLines.Count == 0)
            return;

        if (ImGui.TreeNode(
            $"Geometry mask details ({activeGeometryLines.Count})"))
        {
            foreach (var line in activeGeometryLines)
                ImGui.TextWrapped(line);

            if (activeGeometryOmitted > 0)
            {
                ImGui.TextDisabled(
                    $"{activeGeometryOmitted} diagnostic " +
                    "lines omitted by safety limit.");
            }

            ImGui.TreePop();
        }
    }

    private void AppendActiveGeometryDiagnostics(
        StringBuilder sb)
    {
        sb.AppendLine("[DEV8 STAGE 2F ACTIVE GEOMETRY MASK AUDIT]");
        sb.AppendLine(activeGeometryStatus);
        sb.AppendLine(
            "Read-only snapshot; native attribute-bit " +
            "comparison does not establish draw calls, " +
            "occlusion, pixel visibility or shader output.");

        foreach (string line in activeGeometryLines)
            sb.AppendLine(line);

        if (activeGeometryOmitted > 0)
        {
            sb.AppendLine(
                $"Omitted lines: {activeGeometryOmitted}");
        }

        sb.AppendLine();
    }
}