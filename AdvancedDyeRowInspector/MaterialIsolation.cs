using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using NativeGameObject =
    FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    private nint isoOriginal, isoReplacement;
    private nint isoActor, isoDraw, isoModel, isoMaterial;
    private int isoSlot, isoMatSlot;
    private string isoEquip = "";
    private DateTime isoExpiry;
    private string isoStatus = "Not tested.";

    private static void WriteHalf(byte[] data, int offset, float value)
    {
        ushort bits = BitConverter.HalfToUInt16Bits((Half)value);
        data[offset] = (byte)bits;
        data[offset + 1] = (byte)(bits >> 8);
    }

    private unsafe void StopMaterialIsolation(string reason)
    {
        if (isoOriginal == 0) return;
        bool restored = false;

        try
        {
            var actor = Objects.LocalPlayer;

            if (actor != null && actor.Address == isoActor)
            {
                var game = (NativeGameObject*)actor.Address;
                var draw = (CharacterBase*)game->DrawObject;

                if (draw != null && (nint)draw == isoDraw &&
                    draw->SlotCount > isoSlot &&
                    draw->SlotCount <= 24 &&
                    draw->Models != null &&
                    draw->ColorTableTextures != null)
                {
                    var model = draw->Models[isoSlot];

                    if ((nint)model == isoModel && model != null &&
                        model->Materials != null &&
                        model->MaterialCount > isoMatSlot &&
                        (nint)model->Materials[isoMatSlot] == isoMaterial)
                    {
                        ref nint slot = ref *(nint*)(
                            draw->ColorTableTextures +
                            isoSlot * CharacterBase.MaterialsPerSlot +
                            isoMatSlot);

                        var original = (Texture*)isoOriginal;
                        original->IncRef();

                        nint prior = Interlocked.CompareExchange(
                            ref slot, isoOriginal, isoReplacement);

                        if (prior == isoReplacement)
                        {
                            ((Texture*)isoReplacement)->DecRef();
                            restored = true;
                        }
                        else
                        {
                            original->DecRef();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"ADRI isolation restore: {ex}");
        }
        finally
        {
            ((Texture*)isoOriginal)->DecRef();

            isoOriginal = isoReplacement = 0;
            isoActor = isoDraw = isoModel = isoMaterial = 0;
            isoEquip = "";

            isoStatus = restored
                ? reason
                : "Preview ended. Target changed; newer state was preserved.";
        }
    }

    private unsafe void StartMaterialIsolation(
        MaterialEntry material, List<ModelEntry> selectedModels)
    {
        StopMaterialIsolation("Previous preview restored.");

        ResolvePreviewTargets(
            new[] { material },
            selectedModels.Where(m => m.IsOutfit).ToList());

        if (previewTargets.Count != 1 ||
            !previewTargets[0].Compatible)
        {
            isoStatus = "Unique compatible runtime target required: " +
                previewTargetStatus;
            return;
        }

        var target = previewTargets[0];
        Texture* replacement = null;
        bool installed = false;

        try
        {
            var actor = Objects.LocalPlayer;

            if (actor == null || actor.Address == nint.Zero ||
                actor.Name.TextValue != actorName ||
                actor.ObjectIndex.ToString() != actorIndex)
                throw new InvalidOperationException("Actor changed.");

            var game = (NativeGameObject*)actor.Address;
            var draw = (CharacterBase*)game->DrawObject;

            if (draw == null || draw->Models == null ||
                draw->ColorTableTextures == null ||
                target.ModelSlot < 0 ||
                target.ModelSlot >= draw->SlotCount ||
                draw->SlotCount > 24)
                throw new InvalidOperationException("Invalid draw object.");

            var model = draw->Models[target.ModelSlot];

            if (model == null || model->Materials == null ||
                target.MaterialSlot < 0 ||
                target.MaterialSlot >= model->MaterialCount ||
                model->MaterialCount > CharacterBase.MaterialsPerSlot)
                throw new InvalidOperationException("Material slot changed.");

            var native = model->Materials[target.MaterialSlot];

            if (native == null || native->MaterialResourceHandle == null)
                throw new InvalidOperationException("Material unavailable.");

            var resource = native->MaterialResourceHandle;

            if (!resource->HasColorTable ||
                resource->DataSet == null ||
                resource->DataSetSize < 2048)
                throw new InvalidOperationException(
                    "Material colorset dataset unavailable.");

            int index = target.ModelSlot *
                CharacterBase.MaterialsPerSlot +
                target.MaterialSlot;

            var original = draw->ColorTableTextures[index];

            if (original == null ||
                original->ActualWidth != 8 ||
                original->ActualHeight != 32 ||
                original->TextureFormat !=
                    TextureFormat.R16G16B16A16_FLOAT)
                throw new InvalidOperationException(
                    "Live colorset format changed.");

            byte[] bytes = new byte[2048];

            Marshal.Copy(
                (nint)resource->DataSet,
                bytes, 0, bytes.Length);

            for (int row = 0; row < 32; row++)
            {
                int offset = row * 64;

                WriteHalf(bytes, offset, 4f);
                WriteHalf(bytes, offset + 2, 0f);
                WriteHalf(bytes, offset + 4, 4f);

                WriteHalf(bytes, offset + 16, 2f);
                WriteHalf(bytes, offset + 18, 0f);
                WriteHalf(bytes, offset + 20, 2f);
            }

            replacement = Texture.CreateTexture2D(
                8, 32, 1,
                TextureFormat.R16G16B16A16_FLOAT,
                TextureFlags.TextureType2D |
                TextureFlags.Managed |
                TextureFlags.Immutable,
                7);

            if (replacement == null)
                throw new InvalidOperationException(
                    "Texture allocation failed.");

            fixed (byte* ptr = bytes)
            {
                if (!replacement->InitializeContents(ptr))
                    throw new InvalidOperationException(
                        "Texture initialization failed.");
            }

            ref nint slot = ref *(nint*)(
                draw->ColorTableTextures + index);

            original->IncRef();

            nint prior = Interlocked.CompareExchange(
                ref slot, (nint)replacement, (nint)original);

            if (prior != (nint)original)
            {
                original->DecRef();
                throw new InvalidOperationException(
                    "Colorset changed before swap.");
            }

            installed = true;

            isoOriginal = (nint)original;
            isoReplacement = (nint)replacement;
            isoActor = actor.Address;
            isoDraw = (nint)draw;
            isoModel = (nint)model;
            isoMaterial = (nint)native;
            isoSlot = target.ModelSlot;
            isoMatSlot = target.MaterialSlot;
            isoEquip = compactSlot;
            isoExpiry = DateTime.UtcNow.AddSeconds(8);

            original->DecRef();

            isoStatus = "Magenta: " +
                FriendlyMaterialName(material.GamePath) +
                " (maximum 8 seconds).";
        }
        catch (Exception ex)
        {
            isoStatus = "Highlight failed: " + ex.Message;
        }
        finally
        {
            if (!installed && replacement != null)
                replacement->DecRef();
        }
    }

    private unsafe void MaintainMaterialIsolation()
    {
        if (isoOriginal == 0) return;

        var actor = Objects.LocalPlayer;

        bool stop = !windowOpen ||
            compactSlot != isoEquip ||
            DateTime.UtcNow >= isoExpiry ||
            actor == null ||
            actor.Address != isoActor;

        if (!stop && actor != null)
        {
            var game = (NativeGameObject*)actor.Address;
            var draw = (CharacterBase*)game->DrawObject;

            stop = draw == null ||
                (nint)draw != isoDraw ||
                draw->Models == null ||
                draw->ColorTableTextures == null ||
                draw->SlotCount <= isoSlot ||
                draw->SlotCount > 24;

            if (!stop)
            {
                var model = draw->Models[isoSlot];

                stop = model == null ||
                    (nint)model != isoModel ||
                    model->Materials == null ||
                    model->MaterialCount <= isoMatSlot ||
                    (nint)model->Materials[isoMatSlot] != isoMaterial ||
                    (nint)draw->ColorTableTextures[
                        isoSlot * CharacterBase.MaterialsPerSlot +
                        isoMatSlot] != isoReplacement;
            }
        }

        if (stop)
            StopMaterialIsolation("Original texture restored.");
    }

    private void DrawMaterialIsolation(
        List<ModelEntry> selectedModels)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Temporary material isolation");
        ImGui.TextDisabled(
            "Experimental magenta highlight; maximum 8 seconds.");

        if (isoOriginal != 0)
        {
            if (ImGui.SmallButton("Restore now##ADRIIsoRestore"))
                StopMaterialIsolation("Original texture restored.");
        }
        else
        {
            foreach (var material in selectedModels
                .Where(m => m.IsOutfit)
                .SelectMany(m => m.Materials)
                .Where(IsOutfitMaterial)
                .Distinct())
            {
                if (ImGui.SmallButton(
                    "Highlight " +
                    FriendlyMaterialName(material.GamePath) +
                    "##ADRIIso" + material.GamePath))
                    StartMaterialIsolation(material, selectedModels);
            }
        }

        ImGui.TextWrapped(isoStatus);
        ImGui.TextDisabled(
            "Preview uses material base data; current dye overrides " +
            "may temporarily disappear.");
    }

    private void AppendMaterialIsolationDiagnostics(StringBuilder sb)
    {
        sb.AppendLine("[DEV8 STAGE 2H MATERIAL ISOLATION]");
        sb.AppendLine("Status: " + isoStatus);
        sb.AppendLine("Active: " + (isoOriginal != 0));
        sb.AppendLine(
            "Temporary native colorset swap. No saved state writes.");
        sb.AppendLine();
    }
}