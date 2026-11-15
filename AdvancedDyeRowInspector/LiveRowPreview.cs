using System;
using System.Linq;
using System.Text;
using System.Threading;

using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

using NativeGameObject =
    FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    private readonly record struct LivePreviewRequest(
        nint Actor,
        nint DrawObject,
        nint Model,
        nint Material,
        int ModelSlot,
        int MaterialSlot,
        int Row);

    private LivePreviewRequest? pendingLivePreview;
    private LivePreviewRequest? activeLivePreview;

    private nint originalLiveTexture;
    private nint replacementLiveTexture;

    private DateTime livePreviewExpires;
    private bool cancelLivePreview;

    private int selectedPreviewRow = 22;

    private string livePreviewStatus = "Not started.";

    private static string PreviewRowName(int index)
        => $"{index / 2 + 1}" +
           (index % 2 == 0 ? "A" : "B");

    private unsafe bool TryGetLiveSlot(
        in LivePreviewRequest request,
        out Texture** textureSlot,
        out MaterialResourceHandle* materialHandle)
    {
        textureSlot = null;
        materialHandle = null;

        var player = Objects.LocalPlayer;

        if (player == null ||
            player.Address == nint.Zero ||
            player.Address != request.Actor ||
            player.ObjectIndex.ToString() != actorIndex ||
            player.Name.TextValue != actorName)
        {
            return false;
        }

        var native = (NativeGameObject*)player.Address;
        var draw = (CharacterBase*)native->DrawObject;

        if (draw == null ||
            (nint)draw != request.DrawObject ||
            draw->SlotCount < 1 ||
            draw->SlotCount > 24 ||
            draw->Models == null ||
            draw->ColorTableTextures == null ||
            request.ModelSlot < 0 ||
            request.ModelSlot >= draw->SlotCount ||
            request.MaterialSlot < 0 ||
            request.MaterialSlot >= CharacterBase.MaterialsPerSlot)
        {
            return false;
        }

        var model = draw->Models[request.ModelSlot];

        if (model == null ||
            (nint)model != request.Model ||
            model->Materials == null ||
            model->MaterialCount < 1 ||
            model->MaterialCount > CharacterBase.MaterialsPerSlot ||
            request.MaterialSlot >= model->MaterialCount)
        {
            return false;
        }

        var material = model->Materials[request.MaterialSlot];

        if (material == null ||
            (nint)material != request.Material ||
            material->MaterialResourceHandle == null)
        {
            return false;
        }

        materialHandle = material->MaterialResourceHandle;

        textureSlot = draw->ColorTableTextures +
            request.ModelSlot * CharacterBase.MaterialsPerSlot +
            request.MaterialSlot;

        return true;
    }

    private unsafe void QueueLivePreview(
        MaterialEntry[] materials,
        System.Collections.Generic.List<ModelEntry> modelsForSlot)
    {
        if (activeLivePreview.HasValue ||
            pendingLivePreview.HasValue)
        {
            livePreviewStatus =
                "Stop the current preview first.";
            return;
        }

        ResolvePreviewTargets(materials, modelsForSlot);

        var compatible = previewTargets
            .Where(t => t.Compatible)
            .ToArray();

        if (previewTargets.Count != 1 ||
            compatible.Length != 1)
        {
            livePreviewStatus =
                "Exactly one compatible preview target required.";
            return;
        }

        var player = Objects.LocalPlayer;

        if (player == null ||
            player.Address == nint.Zero ||
            player.ObjectIndex.ToString() != actorIndex ||
            player.Name.TextValue != actorName)
        {
            livePreviewStatus =
                "Character changed. Rescan first.";
            return;
        }

        var native = (NativeGameObject*)player.Address;
        var draw = (CharacterBase*)native->DrawObject;

        var target = compatible[0];

        if (draw == null ||
            draw->Models == null ||
            draw->SlotCount <= target.ModelSlot ||
            draw->SlotCount > 24 ||
            target.MaterialSlot < 0 ||
            target.MaterialSlot >= CharacterBase.MaterialsPerSlot)
        {
            livePreviewStatus =
                "Draw-object model slot unavailable.";
            return;
        }

        var model = draw->Models[target.ModelSlot];

        if (model == null ||
            model->Materials == null ||
            model->MaterialCount <= target.MaterialSlot ||
            model->MaterialCount > CharacterBase.MaterialsPerSlot)
        {
            livePreviewStatus =
                "Draw-object material slot unavailable.";
            return;
        }

        var material = model->Materials[target.MaterialSlot];

        if (material == null)
        {
            livePreviewStatus = "Loaded material unavailable.";
            return;
        }

        pendingLivePreview = new LivePreviewRequest(
            player.Address,
            (nint)draw,
            (nint)model,
            (nint)material,
            target.ModelSlot,
            target.MaterialSlot,
            selectedPreviewRow);

        cancelLivePreview = false;

        livePreviewStatus =
            $"Queued {PreviewRowName(selectedPreviewRow)} preview.";
    }

    private unsafe void BeginLivePreview(
        in LivePreviewRequest request)
    {
        if (!TryGetLiveSlot(
            request,
            out var textureSlot,
            out var materialHandle))
        {
            livePreviewStatus =
                "Target changed before preview. Rescan.";
            return;
        }

        var original = *textureSlot;

        if (original == null ||
            original->ActualWidth != 8 ||
            original->ActualHeight != 32 ||
            original->TextureFormat !=
                TextureFormat.R16G16B16A16_FLOAT ||
            !materialHandle->HasColorTable ||
            materialHandle->ColorTableWidth != 8 ||
            materialHandle->ColorTableHeight != 32 ||
            materialHandle->ColorTableSpan.Length != 1024)
        {
            livePreviewStatus =
                "Incompatible live colorset or base table.";
            return;
        }

        // The controlled comparison supplies the actual
        // GPU colorset, including existing overrides.
        if (controlledSourceValues != null &&
            (nint)original != controlledOriginalTexture)
        {
            livePreviewStatus =
                "Live colorset changed during comparison.";
            return;
        }

        Half[] values = controlledSourceValues != null
            ? (Half[])controlledSourceValues.Clone()
            : materialHandle->ColorTableSpan.ToArray();

        int offset = request.Row * 32;

        // Bright magenta, both diffuse and emissive.
        values[offset] = (Half)1f;
        values[offset + 1] = (Half)0f;
        values[offset + 2] = (Half)1f;

        values[offset + 8] = (Half)6f;
        values[offset + 9] = (Half)0f;
        values[offset + 10] = (Half)6f;

        var device = Device.Instance();

        if (device == null)
        {
            livePreviewStatus =
                "Graphics device unavailable.";
            return;
        }

        int* size = stackalloc int[2] { 8, 32 };

        var replacement = device->CreateTexture2D(
            size,
            1,
            TextureFormat.R16G16B16A16_FLOAT,
            TextureFlags.TextureNoSwizzle |
            TextureFlags.Immutable |
            TextureFlags.Managed,
            7);

        if (replacement == null)
        {
            livePreviewStatus =
                "Could not create temporary colorset texture.";
            return;
        }

        bool installed = false;

        try
        {
            fixed (Half* data = values)
            {
                if (!replacement->InitializeContents(data))
                {
                    livePreviewStatus =
                        "Could not initialize temporary texture.";
                    return;
                }
            }

            nint observed = Interlocked.CompareExchange(
                ref *(nint*)textureSlot,
                (nint)replacement,
                (nint)original);

            if (observed != (nint)original)
            {
                livePreviewStatus =
                    "Another preview changed the texture. Aborted.";
                return;
            }

            installed = true;

            activeLivePreview = request;
            originalLiveTexture = (nint)original;
            replacementLiveTexture = (nint)replacement;

            livePreviewExpires =
                DateTime.UtcNow.AddSeconds(6);

            livePreviewStatus =
                $"Highlighting {PreviewRowName(request.Row)} " +
                "for six seconds.";
        }
        finally
        {
            if (!installed)
                replacement->DecRef();
        }
    }

    private unsafe void EndLivePreview(string reason)
    {
        pendingLivePreview = null;
        cancelLivePreview = false;

        if (activeLivePreview is not { } request)
        {
            livePreviewStatus = reason;
            return;
        }

        nint original = originalLiveTexture;
        nint replacement = replacementLiveTexture;

        activeLivePreview = null;
        originalLiveTexture = nint.Zero;
        replacementLiveTexture = nint.Zero;

        bool restored = false;

        if (original != nint.Zero &&
            replacement != nint.Zero &&
            TryGetLiveSlot(
                request,
                out var textureSlot,
                out _))
        {
            nint observed = Interlocked.CompareExchange(
                ref *(nint*)textureSlot,
                original,
                replacement);

            if (observed == replacement)
            {
                ((Texture*)replacement)->DecRef();
                restored = true;
            }
        }

        if (!restored && original != nint.Zero)
        {
            // Never write to a stale native slot.
            ((Texture*)original)->DecRef();
        }

        livePreviewStatus = restored
            ? reason + " Original texture restored."
            : reason +
              " Target changed; no stale slot was written.";
    }

    // FIX: Do not name this parameter '_'.
    // A named '_' would capture 'out _' above/below.
    private unsafe void OnLivePreviewFramework(
        IFramework framework)
    {
        try
        {
            if (activeLivePreview is { } active)
            {
                if (!TryGetLiveSlot(
                        active,
                        out var textureSlot,
                        out _) ||
                    (nint)(*textureSlot) !=
                        replacementLiveTexture)
                {
                    EndLivePreview("Live material changed.");
                    return;
                }

                if (cancelLivePreview ||
                    !windowOpen ||
                    DateTime.UtcNow >= livePreviewExpires)
                {
                    EndLivePreview(
                        cancelLivePreview
                            ? "Stopped."
                            : "Preview ended.");
                }

                return;
            }

            if (pendingLivePreview is { } pending)
            {
                pendingLivePreview = null;

                if (!cancelLivePreview && windowOpen)
                {
                    BeginLivePreview(pending);
                }
                else
                {
                    livePreviewStatus = "Preview canceled.";
                }

                cancelLivePreview = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(
                $"ADRI live row preview failed: {ex}");

            try
            {
                EndLivePreview("Preview error.");
            }
            catch (Exception cleanupError)
            {
                Log.Warning(
                    $"ADRI preview cleanup failed: {cleanupError}");
            }
        }
    }

    private void DrawLiveRowPreview(
        MaterialEntry[] materials,
        System.Collections.Generic.List<ModelEntry> modelsForSlot)
    {
        ImGui.Separator();

        ImGui.TextUnformatted(
            "Experimental: temporary row highlight");

        ImGui.SliderInt(
            "Row (1A-16B)##ADRIRow",
            ref selectedPreviewRow,
            0,
            31);

        ImGui.TextDisabled(
            "Selected: " + PreviewRowName(selectedPreviewRow));

        if (controlledRunning)
        {
            ImGui.TextDisabled(
                "Controlled comparison in progress.");
        }
        else if (screenNoiseRunning)
        {
            ImGui.TextDisabled(
                "Finish screen capture before highlighting.");
        }
        else if (activeLivePreview.HasValue ||
            pendingLivePreview.HasValue)
        {
            if (ImGui.SmallButton(
                "Stop and restore##ADRIPreviewStop"))
            {
                cancelLivePreview = true;
            }
        }
        else if (ImGui.SmallButton(
            "Highlight for 6 seconds##ADRIPreviewStart"))
        {
            QueueLivePreview(materials, modelsForSlot);
        }

        ImGui.TextWrapped(livePreviewStatus);

        ImGui.TextDisabled(
            "Temporary live appearance change; " +
            "restores automatically.");

        ImGui.TextDisabled(
            "Other rows use base MTRL colors during preview.");

        ImGui.TextDisabled(
            "Does not measure rendered visibility.");
    }

    private void AppendLivePreviewDiagnostics(StringBuilder sb)
    {
        sb.AppendLine("[DEV6 STAGE 2 LIVE ROW PREVIEW]");
        sb.AppendLine("Status: " + livePreviewStatus);

        sb.AppendLine(
            "Selected row: " +
            PreviewRowName(selectedPreviewRow));

        sb.AppendLine(
            "Active: " + activeLivePreview.HasValue);

        sb.AppendLine();
    }
}