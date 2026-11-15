using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    // A borrowed wrapper around the game's live shader view.
    // The native texture is kept alive separately with IncRef.
    private sealed class BorrowedColorsetWrap : IDalamudTextureWrap
    {
        private readonly nint shaderView;

        public BorrowedColorsetWrap(nint shaderView)
            => this.shaderView = shaderView;

        public ImTextureID Handle => new(this.shaderView);
        public int Width => 8;
        public int Height => 32;

        // Does not own the native reference.
        public void Dispose() { }
    }

    private sealed record ColorsetBorrow(
        LivePreviewRequest Target,
        nint NativeTexture,
        BorrowedColorsetWrap Wrap);

    private readonly record struct ActorRegion(
        int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private sealed record ControlledRowResult(
        string Row,
        ActorRegion Region,
        long Samples,
        double BaselineMean,
        double PreviewMean,
        double RestoredMean,
        double BaselineStrongPercent,
        double PreviewStrongPercent,
        double NoiseAdjustedPercent);

    private readonly object controlledGate = new();

    private CancellationTokenSource? controlledCancellation;
    private volatile bool controlledRunning;
    private bool controlledDisposed;

    private string controlledStatus = "Not measured.";
    private ControlledRowResult? controlledResult;

    // Used only for the automatic comparison.
    // The existing manual preview keeps its current behavior.
    private Half[]? controlledSourceValues;
    private nint controlledOriginalTexture;

    private bool TryGetActorRegion(out ActorRegion region)
    {
        region = default;

        var player = Objects.LocalPlayer;

        if (player == null ||
            !scanned ||
            player.ObjectIndex.ToString() != actorIndex ||
            player.Name.TextValue != actorName)
            return false;

        // Estimate the character's screen rectangle.
        // This is not an exact silhouette or occlusion mask.
        if (!GameGui.WorldToScreen(
                player.Position, out var feet) ||
            !GameGui.WorldToScreen(
                player.Position + new Vector3(0, 1.8f, 0),
                out var head))
            return false;

        var viewport = ImGui.GetMainViewport();

        int width = (int)viewport.Size.X;
        int height = (int)viewport.Size.Y;

        float projectedHeight = MathF.Abs(feet.Y - head.Y);

        if (width < 128 ||
            height < 128 ||
            projectedHeight < 80 ||
            !float.IsFinite(projectedHeight))
            return false;

        float centerX = (feet.X + head.X) * 0.5f;

        int left = Math.Clamp(
            (int)(centerX - projectedHeight * 0.53f),
            0, width);

        int right = Math.Clamp(
            (int)(centerX + projectedHeight * 0.53f),
            0, width);

        int top = Math.Clamp(
            (int)(MathF.Min(feet.Y, head.Y) -
                  projectedHeight * 0.18f),
            0, height);

        int bottom = Math.Clamp(
            (int)(MathF.Max(feet.Y, head.Y) +
                  projectedHeight * 0.12f),
            0, height);

        if (right - left < 64 || bottom - top < 80)
            return false;

        region = new(left, top, right, bottom);
        return true;
    }

    private unsafe ColorsetBorrow? BorrowSelectedColorset(
        List<ModelEntry> selectedModels)
    {
        var materials = selectedModels
            .SelectMany(m => m.Materials)
            .Where(IsOutfitMaterial)
            .Distinct()
            .ToArray();

        if (materials.Length != 1)
        {
            SetControlledStatus(
                "Choose an equipment slot with one outfit material.");
            return null;
        }

        // Reuse the existing verified DEV6 target resolver,
        // but do not start the preview yet.
        QueueLivePreview(materials, selectedModels);

        if (pendingLivePreview is not { } request)
        {
            SetControlledStatus(
                "Preview target unavailable: " + livePreviewStatus);
            return null;
        }

        pendingLivePreview = null;

        if (!TryGetLiveSlot(request, out var slot, out _) ||
            slot == null)
        {
            SetControlledStatus(
                "Selected material changed. Rescan first.");
            return null;
        }

        var original = *slot;

        if (original == null ||
            original->ActualWidth != 8 ||
            original->ActualHeight != 32 ||
            original->TextureFormat !=
                TextureFormat.R16G16B16A16_FLOAT ||
            original->D3D11ShaderResourceView == null)
        {
            SetControlledStatus(
                "Live GPU colorset cannot be read.");
            return null;
        }

        // The borrowed reference keeps the native
        // texture and its D3D11 view valid for readback.
        original->IncRef();

        return new ColorsetBorrow(
            request,
            (nint)original,
            new BorrowedColorsetWrap(
                (nint)original->D3D11ShaderResourceView));
    }

    private static Half[] DecodeGpuColorset(
        int width,
        int height,
        int format,
        int pitch,
        byte[] pixels)
    {
        // DXGI_FORMAT_R16G16B16A16_FLOAT = 10.
        if (width != 8 ||
            height != 32 ||
            format != 10 ||
            pitch < 64 ||
            (long)(height - 1) * pitch + 64 >
                pixels.LongLength)
        {
            throw new InvalidOperationException(
                "GPU colorset must be 8x32 RGBA16F.");
        }

        var values = new Half[1024];

        for (int row = 0; row < 32; row++)
        {
            for (int value = 0; value < 32; value++)
            {
                int offset =
                    row * pitch + value * 2;

                values[row * 32 + value] =
                    BitConverter.UInt16BitsToHalf(
                        BitConverter.ToUInt16(
                            pixels, offset));
            }
        }

        return values;
    }

    private static ControlledRowResult CompareControlledFrames(
        ScreenFrame beforeA,
        ScreenFrame beforeB,
        ScreenFrame highlighted,
        ScreenFrame restored,
        ActorRegion region,
        string rowName)
    {
        if (beforeA.Width != beforeB.Width ||
            beforeA.Width != highlighted.Width ||
            beforeA.Width != restored.Width ||
            beforeA.Height != beforeB.Height ||
            beforeA.Height != highlighted.Height ||
            beforeA.Height != restored.Height ||
            beforeA.Format != beforeB.Format ||
            beforeA.Format != highlighted.Format ||
            beforeA.Format != restored.Format)
        {
            throw new InvalidOperationException(
                "Screen resolution or pixel format changed.");
        }

        if (region.Right > beforeA.Width ||
            region.Bottom > beforeA.Height)
        {
            throw new InvalidOperationException(
                "Character region moved outside the viewport.");
        }

        int step = Math.Max(2, region.Height / 160);

        long samples = 0;
        long baselineStrong = 0;
        long previewStrong = 0;
        long excess = 0;

        long naturalSum = 0;
        long previewSum = 0;
        long restoredSum = 0;

        for (int y = region.Top;
             y < region.Bottom;
             y += step)
        {
            for (int x = region.Left;
                 x < region.Right;
                 x += step)
            {
                int a = y * beforeA.Pitch + x * 4;
                int b = y * beforeB.Pitch + x * 4;
                int c = y * highlighted.Pitch + x * 4;
                int d = y * restored.Pitch + x * 4;

                int natural = 0;
                int preview = 0;
                int reset = 0;

                for (int channel = 0;
                     channel < 3;
                     channel++)
                {
                    natural = Math.Max(
                        natural,
                        Math.Abs(
                            beforeA.Pixels[a + channel] -
                            beforeB.Pixels[b + channel]));

                    preview = Math.Max(
                        preview,
                        Math.Abs(
                            beforeB.Pixels[b + channel] -
                            highlighted.Pixels[c + channel]));

                    reset = Math.Max(
                        reset,
                        Math.Abs(
                            beforeB.Pixels[b + channel] -
                            restored.Pixels[d + channel]));
                }

                naturalSum += natural;
                previewSum += preview;
                restoredSum += reset;

                if (natural >= 48)
                    baselineStrong++;

                if (preview >= 48)
                    previewStrong++;

                if (preview >= Math.Max(
                    48, natural * 2 + 16))
                    excess++;

                samples++;
            }
        }

        if (samples == 0)
        {
            throw new InvalidOperationException(
                "Empty actor comparison region.");
        }

        return new ControlledRowResult(
            rowName,
            region,
            samples,
            naturalSum / (double)samples,
            previewSum / (double)samples,
            restoredSum / (double)samples,
            baselineStrong * 100.0 / samples,
            previewStrong * 100.0 / samples,
            excess * 100.0 / samples);
    }

    private void SetControlledStatus(string status)
    {
        lock (controlledGate)
        {
            if (!controlledDisposed)
                controlledStatus = status;
        }
    }

    private void BeginControlledRowComparison(
        List<ModelEntry> selectedModels)
    {
        if (controlledRunning ||
            screenNoiseRunning ||
            activeLivePreview.HasValue ||
            pendingLivePreview.HasValue)
        {
            SetControlledStatus(
                "Finish the current preview or capture first.");
            return;
        }

        if (!TryGetActorRegion(out var region))
        {
            SetControlledStatus(
                "Character projection unavailable. " +
                "Keep your character fully in view.");
            return;
        }

        var borrow = BorrowSelectedColorset(selectedModels);

        if (borrow == null)
            return;

        CancellationTokenSource source;

        lock (controlledGate)
        {
            if (controlledDisposed)
            {
                ReleaseBorrowedColorset(
                    borrow.NativeTexture);
                return;
            }

            source = new CancellationTokenSource();

            controlledCancellation = source;
            controlledRunning = true;
            controlledResult = null;

            controlledStatus =
                "Capturing unmodified baseline...";
        }

        uint viewport = ImGui.GetMainViewport().ID;

        _ = RunControlledRowComparisonAsync(
            viewport, region, borrow, source);
    }

    private async Task RunControlledRowComparisonAsync(
        uint viewport,
        ActorRegion region,
        ColorsetBorrow borrow,
        CancellationTokenSource source)
    {
        var token = source.Token;

        try
        {
            // Two untouched frames establish local noise.
            var first = await CaptureScreenFrameAsync(
                viewport, token).ConfigureAwait(false);

            await Task.Delay(
                250, token).ConfigureAwait(false);

            var second = await CaptureScreenFrameAsync(
                viewport, token).ConfigureAwait(false);

            SetControlledStatus(
                "Reading actual GPU colorset...");

            // Preserve all live values, including Glamourer
            // overrides. Do not fall back to the base MTRL.
            var (spec, pixels) = await Readback
                .GetRawImageAsync(
                    borrow.Wrap,
                    leaveWrapOpen: true,
                    cancellationToken: token)
                .ConfigureAwait(false);

            var current = DecodeGpuColorset(
                spec.Width,
                spec.Height,
                spec.DxgiFormat,
                spec.Pitch,
                pixels);

            SetControlledStatus(
                "Applying one-row temporary highlight...");

            await Framework.RunOnTick(() =>
            {
                if (!windowOpen ||
                    !TryConfirmBorrowedTarget(borrow))
                {
                    throw new InvalidOperationException(
                        "Actor or colorset changed; aborting.");
                }

                controlledSourceValues = current;

                controlledOriginalTexture =
                    borrow.NativeTexture;

                pendingLivePreview = borrow.Target;
                cancelLivePreview = false;

            }, cancellationToken: token)
                .ConfigureAwait(false);

            // Allow the existing framework preview
            // callback to install the temporary texture.
            await Framework.DelayTicks(
                3, token).ConfigureAwait(false);

            bool started = await Framework.RunOnTick(
                () => activeLivePreview.HasValue &&
                      activeLivePreview.Value == borrow.Target,
                cancellationToken: token)
                .ConfigureAwait(false);

            if (!started)
            {
                throw new InvalidOperationException(
                    "Live highlight did not activate: " +
                    livePreviewStatus);
            }

            await Task.Delay(
                230, token).ConfigureAwait(false);

            var during = await CaptureScreenFrameAsync(
                viewport, token).ConfigureAwait(false);

            await Framework.RunOnTick(
                () => EndLivePreview(
                    "DEV7 controlled capture finished."),
                cancellationToken: token)
                .ConfigureAwait(false);

            await Task.Delay(
                180, token).ConfigureAwait(false);

            var after = await CaptureScreenFrameAsync(
                viewport, token).ConfigureAwait(false);

            var result = CompareControlledFrames(
                first,
                second,
                during,
                after,
                region,
                PreviewRowName(borrow.Target.Row));

            lock (controlledGate)
            {
                if (!controlledDisposed &&
                    !token.IsCancellationRequested)
                {
                    controlledResult = result;
                    controlledStatus =
                        "Controlled comparison complete (experimental).";
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetControlledStatus(
                "Controlled comparison canceled.");
        }
        catch (Exception ex)
        {
            SetControlledStatus(
                "Comparison unavailable: " +
                ex.GetType().Name + ": " + ex.Message);

            Log.Warning(
                $"ADRI controlled row comparison failed: {ex}");
        }
        finally
        {
            try
            {
                // Native cleanup must run on the framework.
                // Existing DEV6 logic validates the pointer
                // before restoring the original slot value.
                await Framework.RunOnFrameworkThread(() =>
                {
                    if (activeLivePreview is { } active &&
                        active == borrow.Target)
                    {
                        EndLivePreview(
                            "DEV7 comparison cleanup.");
                    }

                    if (pendingLivePreview is { } pending &&
                        pending == borrow.Target)
                    {
                        pendingLivePreview = null;
                    }

                    controlledSourceValues = null;
                    controlledOriginalTexture = nint.Zero;

                    ReleaseBorrowedColorset(
                        borrow.NativeTexture);

                }).ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                Log.Warning(
                    $"ADRI controlled comparison cleanup failed: {cleanup}");
            }

            lock (controlledGate)
            {
                if (ReferenceEquals(
                    controlledCancellation, source))
                {
                    controlledCancellation = null;
                    controlledRunning = false;
                }
            }

            source.Dispose();
        }
    }

    private unsafe bool TryConfirmBorrowedTarget(
        ColorsetBorrow borrow)
    {
        return TryGetLiveSlot(
                   borrow.Target, out var slot, out _) &&
               slot != null &&
               (nint)(*slot) == borrow.NativeTexture;
    }

    private static unsafe void ReleaseBorrowedColorset(
        nint nativeTexture)
    {
        if (nativeTexture != nint.Zero)
            ((Texture*)nativeTexture)->DecRef();
    }

    private void CancelControlledRowComparison()
    {
        CancellationTokenSource? source;

        lock (controlledGate)
            source = controlledCancellation;

        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void DisposeControlledRowComparison()
    {
        lock (controlledGate)
            controlledDisposed = true;

        CancelControlledRowComparison();
    }

    private void DrawControlledRowDiagnostics(
        List<ModelEntry> selectedModels)
    {
        if (!ImGui.TreeNode(
            "DEV7 / controlled row comparison"))
            return;

        ImGui.TextDisabled(
            "Two baseline frames, live GPU colorset, " +
            "highlight, restore.");

        ImGui.TextDisabled(
            "Uses approximate character bounds; " +
            "background may remain.");

        if (controlledRunning)
        {
            if (ImGui.SmallButton(
                "Cancel comparison##ADRICompareCancel"))
                CancelControlledRowComparison();
        }
        else if (ImGui.SmallButton(
            "Compare selected dye row##ADRICompareStart"))
        {
            BeginControlledRowComparison(selectedModels);
        }

        string status;
        ControlledRowResult? result;

        lock (controlledGate)
        {
            status = controlledStatus;
            result = controlledResult;
        }

        ImGui.TextWrapped(status);

        if (result != null)
        {
            ImGui.Separator();

            ImGui.TextUnformatted(
                $"Row {result.Row} | actor region " +
                $"{result.Region.Width}x{result.Region.Height}");

            ImGui.TextDisabled(
                $"Samples: {result.Samples:N0}");

            ImGui.TextUnformatted(
                $"Baseline mean difference: " +
                $"{result.BaselineMean:F1}/255");

            ImGui.TextUnformatted(
                $"During highlight: " +
                $"{result.PreviewMean:F1}/255");

            ImGui.TextUnformatted(
                $"After restoration: " +
                $"{result.RestoredMean:F1}/255");

            ImGui.TextUnformatted(
                $"Strong changes: " +
                $"{result.BaselineStrongPercent:F1}% baseline / " +
                $"{result.PreviewStrongPercent:F1}% highlighted");

            ImGui.TextUnformatted(
                $"Exceeds local noise: " +
                $"{result.NoiseAdjustedPercent:F1}% of sampled region");

            if (result.BaselineStrongPercent > 12)
            {
                ImGui.TextDisabled(
                    "High natural movement: result may be inconclusive.");
            }
        }

        ImGui.TextDisabled(
            "Not a validated on-screen visibility verdict.");

        ImGui.TreePop();
    }

    private void AppendControlledRowDiagnostics(
        StringBuilder sb)
    {
        sb.AppendLine(
            "[DEV7 CONTROLLED ROW COMPARISON]");

        lock (controlledGate)
        {
            sb.AppendLine("Status: " + controlledStatus);

            if (controlledResult is { } result)
            {
                sb.AppendLine(
                    $"Row: {result.Row}; region: " +
                    $"{result.Region.Width}x{result.Region.Height}");

                sb.AppendLine(
                    $"Samples: {result.Samples}; " +
                    $"baseline: {result.BaselineMean:F3}; " +
                    $"preview: {result.PreviewMean:F3}; " +
                    $"restored: {result.RestoredMean:F3}");

                sb.AppendLine(
                    $"Strong baseline: " +
                    $"{result.BaselineStrongPercent:F3}%; " +
                    $"strong preview: " +
                    $"{result.PreviewStrongPercent:F3}%; " +
                    $"exceeds local noise: " +
                    $"{result.NoiseAdjustedPercent:F3}%");
            }
        }

        sb.AppendLine();
    }
}