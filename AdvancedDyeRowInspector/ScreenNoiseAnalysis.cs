using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace AdvancedDyeRowInspector;

public sealed partial class Plugin
{
    private sealed record ScreenFrame(
        int Width,
        int Height,
        int Pitch,
        int Format,
        byte[] Pixels);

    private sealed record ScreenNoiseResult(
        int Width,
        int Height,
        int Format,
        int SampleStep,
        long Samples,
        double MeanDifference,
        double ChangedPercent,
        double StrongChangedPercent);

    private readonly object screenNoiseGate = new();

    private CancellationTokenSource? screenNoiseCancellation;

    private volatile bool screenNoiseRunning;
    private bool screenNoiseDisposed;

    private string screenNoiseStatus = "Not measured.";
    private ScreenNoiseResult? screenNoiseResult;

    private static bool SupportedScreenFormat(int format)
    {
        // DXGI RGBA8 and BGRA8, including SRGB variants.
        return format is 28 or 29 or 87 or 91;
    }

    private async Task<ScreenFrame> CaptureScreenFrameAsync(
        uint viewportId,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();

        // Capture before Dalamud renders its ImGui windows.
        // AutoUpdate=false ensures a single captured frame.
        var arguments = new ImGuiViewportTextureArgs
        {
            ViewportId = viewportId,
            AutoUpdate = false,
            TakeBeforeImGuiRender = true,
            KeepTransparency = false
        };

        using var captured = await Textures
            .CreateFromImGuiViewportAsync(
                arguments,
                "ADRI DEV7 baseline frame",
                cancellation)
            .ConfigureAwait(false);

        var (specification, pixels) = await Readback
            .GetRawImageAsync(
                captured,
                leaveWrapOpen: true,
                cancellationToken: cancellation)
            .ConfigureAwait(false);

        cancellation.ThrowIfCancellationRequested();

        if (!SupportedScreenFormat(specification.DxgiFormat))
        {
            throw new NotSupportedException(
                "Unsupported screen pixel format: " +
                specification.DxgiFormat +
                ". BGRA8 or RGBA8 is required.");
        }

        if (specification.Width < 64 ||
            specification.Height < 64 ||
            specification.Width > 7680 ||
            specification.Height > 4320)
        {
            throw new InvalidOperationException(
                "Unexpected screen capture dimensions.");
        }

        long minimumPitch =
            (long)specification.Width * 4;

        long minimumLength =
            (long)(specification.Height - 1) *
            specification.Pitch + minimumPitch;

        if (specification.Pitch < minimumPitch ||
            minimumLength > pixels.LongLength)
        {
            throw new InvalidOperationException(
                "Invalid screenshot pixel buffer or row pitch.");
        }

        return new ScreenFrame(
            specification.Width,
            specification.Height,
            specification.Pitch,
            specification.DxgiFormat,
            pixels);
    }

    private static ScreenNoiseResult CompareScreenFrames(
        ScreenFrame first,
        ScreenFrame second)
    {
        if (first.Width != second.Width ||
            first.Height != second.Height ||
            first.Format != second.Format)
        {
            throw new InvalidOperationException(
                "Screen size or pixel format changed " +
                "between the two captures.");
        }

        // Sample a fixed spatial grid instead of keeping
        // or processing a full-resolution difference image.
        int step = Math.Max(
            1,
            Math.Max(
                first.Width / 240,
                first.Height / 150));

        long samples = 0;
        long changed = 0;
        long stronglyChanged = 0;
        long totalDifference = 0;

        for (int y = 0; y < first.Height; y += step)
        {
            int rowA = y * first.Pitch;
            int rowB = y * second.Pitch;

            for (int x = 0; x < first.Width; x += step)
            {
                int a = rowA + x * 4;
                int b = rowB + x * 4;

                int d0 = Math.Abs(
                    first.Pixels[a] - second.Pixels[b]);

                int d1 = Math.Abs(
                    first.Pixels[a + 1] - second.Pixels[b + 1]);

                int d2 = Math.Abs(
                    first.Pixels[a + 2] - second.Pixels[b + 2]);

                int strongest = Math.Max(
                    d0,
                    Math.Max(d1, d2));

                totalDifference += d0 + d1 + d2;

                if (strongest >= 16)
                    changed++;

                if (strongest >= 48)
                    stronglyChanged++;

                samples++;
            }
        }

        if (samples <= 0)
        {
            throw new InvalidOperationException(
                "No screenshot pixels were sampled.");
        }

        return new ScreenNoiseResult(
            first.Width,
            first.Height,
            first.Format,
            step,
            samples,
            totalDifference / (samples * 3.0),
            changed * 100.0 / samples,
            stronglyChanged * 100.0 / samples);
    }

    private void SetScreenNoiseStatus(string status)
    {
        lock (screenNoiseGate)
        {
            if (!screenNoiseDisposed)
                screenNoiseStatus = status;
        }
    }

    private async Task MeasureScreenNoiseAsync(
        uint viewportId,
        CancellationTokenSource source)
    {
        try
        {
            var cancellation = source.Token;

            SetScreenNoiseStatus(
                "Capturing first unmodified frame...");

            var first = await CaptureScreenFrameAsync(
                viewportId,
                cancellation);

            SetScreenNoiseStatus(
                "Capturing second frame...");

            await Task.Delay(
                350,
                cancellation).ConfigureAwait(false);

            var second = await CaptureScreenFrameAsync(
                viewportId,
                cancellation);

            var result = CompareScreenFrames(first, second);

            lock (screenNoiseGate)
            {
                if (!screenNoiseDisposed &&
                    !cancellation.IsCancellationRequested)
                {
                    screenNoiseResult = result;
                    screenNoiseStatus =
                        "Baseline comparison complete.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetScreenNoiseStatus(
                "Baseline capture canceled.");
        }
        catch (Exception ex)
        {
            SetScreenNoiseStatus(
                "Capture failed: " +
                ex.GetType().Name + ": " + ex.Message);

            Log.Warning(
                $"ADRI DEV7 screen capture failure: {ex}");
        }
        finally
        {
            lock (screenNoiseGate)
            {
                if (ReferenceEquals(
                    screenNoiseCancellation, source))
                {
                    screenNoiseCancellation = null;
                    screenNoiseRunning = false;
                }
            }

            source.Dispose();
        }
    }

    private void BeginScreenNoiseMeasurement()
    {
        if (controlledRunning)
        {
            SetScreenNoiseStatus(
                "Finish the controlled comparison first.");
            return;
        }

        if (activeLivePreview.HasValue ||
            pendingLivePreview.HasValue)
        {
            SetScreenNoiseStatus(
                "Stop the dye-row preview first.");
            return;
        }

        // Only read ImGui's viewport ID from the UI thread.
        uint viewportId = ImGui.GetMainViewport().ID;

        CancellationTokenSource source;

        lock (screenNoiseGate)
        {
            if (screenNoiseRunning || screenNoiseDisposed)
                return;

            source = new CancellationTokenSource();

            screenNoiseCancellation = source;
            screenNoiseResult = null;
            screenNoiseRunning = true;
            screenNoiseStatus = "Starting capture...";
        }

        // Asynchronous GPU readback. Never block the UI
        // thread while waiting for either screenshot.
        _ = MeasureScreenNoiseAsync(viewportId, source);
    }

    private void CancelScreenNoiseMeasurement()
    {
        CancellationTokenSource? source;

        lock (screenNoiseGate)
        {
            source = screenNoiseCancellation;
        }

        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Capture finished during cancellation.
        }
    }

    private void DisposeScreenNoiseMeasurement()
    {
        lock (screenNoiseGate)
        {
            screenNoiseDisposed = true;
        }

        CancelScreenNoiseMeasurement();
    }

    private void DrawScreenNoiseDiagnostics()
    {
        if (!ImGui.TreeNode(
            "DEV7 / screen capture baseline"))
        {
            return;
        }

        ImGui.TextDisabled(
            "Measure natural frame-to-frame image changes.");

        if (screenNoiseRunning)
        {
            if (ImGui.SmallButton(
                "Cancel capture##ADRINoiseCancel"))
            {
                CancelScreenNoiseMeasurement();
            }
        }
        else if (ImGui.SmallButton(
            "Measure baseline noise##ADRINoiseStart"))
        {
            BeginScreenNoiseMeasurement();
        }

        string status;
        ScreenNoiseResult? result;

        lock (screenNoiseGate)
        {
            status = screenNoiseStatus;
            result = screenNoiseResult;
        }

        ImGui.TextWrapped(status);

        if (result != null)
        {
            ImGui.Separator();

            ImGui.TextUnformatted(
                $"Screen: {result.Width}x{result.Height}");

            ImGui.TextDisabled(
                $"DXGI format {result.Format}; " +
                $"sample every {result.SampleStep} pixels");

            ImGui.TextUnformatted(
                $"Samples compared: {result.Samples:N0}");

            ImGui.TextUnformatted(
                $"Average RGB difference: " +
                $"{result.MeanDifference:F2} / 255");

            ImGui.TextUnformatted(
                $"Pixels changed (>=16): " +
                $"{result.ChangedPercent:F2}%");

            ImGui.TextUnformatted(
                $"Strong changes (>=48): " +
                $"{result.StrongChangedPercent:F2}%");
        }

        ImGui.Spacing();

        ImGui.TextDisabled(
            "Two game frames, about 350 ms apart.");

        ImGui.TextDisabled(
            "Plugin windows excluded; game HUD remains.");

        ImGui.TextDisabled(
            "Images stay in memory and are discarded.");

        ImGui.TextDisabled(
            "Full-screen noise, not character visibility.");

        ImGui.TreePop();
    }

    private void AppendScreenNoiseDiagnostics(StringBuilder sb)
    {
        sb.AppendLine("[DEV7 SCREEN CAPTURE BASELINE]");

        lock (screenNoiseGate)
        {
            sb.AppendLine("Status: " + screenNoiseStatus);

            if (screenNoiseResult is { } result)
            {
                sb.AppendLine(
                    $"Resolution: {result.Width}x{result.Height}");

                sb.AppendLine(
                    $"DXGI format: {result.Format}");

                sb.AppendLine(
                    $"Sample step: {result.SampleStep}");

                sb.AppendLine(
                    $"Sample count: {result.Samples}");

                sb.AppendLine(
                    $"Mean RGB difference: " +
                    $"{result.MeanDifference:F4}/255");

                sb.AppendLine(
                    $"Changed >=16: " +
                    $"{result.ChangedPercent:F4}%");

                sb.AppendLine(
                    $"Changed >=48: " +
                    $"{result.StrongChangedPercent:F4}%");
            }
        }

        sb.AppendLine();
    }
}