using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;
using Nvt.Replay.Rendering;
using Nvt.Replay.Sources;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Avalonia.Controls;
using Nvt.Replay.Avalonia.Views;

namespace Nvt.Replay.Avalonia;

public partial class MainWindow : Window
{
    private CancellationTokenSource? playbackCancellation;

    private readonly ReplayPlaybackController playbackController = new();

    private int currentLogicalIndex => playbackController.CurrentIndex;

    private int? loopIn => playbackController.LoopStart;

    private int? loopOut => playbackController.LoopEnd;

    private bool loopEnabled => playbackController.LoopEnabled;

    private double replaySpeed => playbackController.Rate.Multiplier;

    private ReplayAutoPauseIndex? autoPauseIndex;

    private bool maxReplaySpeed => playbackController.Rate.IsMaximum;

    private bool synchronizingSelection;

    private bool synchronizingLoopControls;

    private bool continuousTimelineSeekScheduled;

    private int? pendingContinuousTimelineSeek;

    private void SetTimelineCounts(int physical, int logical, int evidence)
    {
        TimelinePhysicalCountText.Text = physical.ToString("N0", CultureInfo.InvariantCulture);
        TimelineLogicalCountText.Text = logical.ToString("N0", CultureInfo.InvariantCulture);
        TimelineEvidenceCountText.Text = evidence.ToString("N0", CultureInfo.InvariantCulture);
    }

    private void InitializeReplay()
    {
        if (replaySession is null || session is null)
        {
            return;
        }

        var maximum = Math.Max(1, replaySession.Count - 1);
        PaintTab.IsEnabled = replaySession.Count > 0;
        SetSourceTransportEnabled(!outputWorkspaceActive);
        LoopToggleButton.IsEnabled = replaySession.Count > 1;
        ReplayTimelineSurface.IsEnabled = replaySession.Count > 0;
        ReplayTimelineSurface.SetMaximum(maximum);
        if (replayFrames is null || paintWorkspace is null || autoPauseIndex is null)
            throw new InvalidOperationException("Replay workspace must be prepared before it is presented.");
        playbackController.Load(
            replaySession.Timeline,
            autoPauseIndex,
            (exclusiveStart, inclusiveEnd) => reviewWorkspace?.ReviewSession.FirstPauseIndex(exclusiveStart, inclusiveEnd));
        playbackController.ConfigureAutoPause(new ReplayPlaybackPauseOptions(
            SettingsPage.PlaybackPreferences.PauseOnBreak,
            SettingsPage.PlaybackPreferences.PauseOnAllBreak));
        SynchronizePaintPresentation(fit: true);
        SyncOutputResolutionWithPanel();
        InitializeOutputRange();
        ReplayEndClockText.Text = FormatClock(SelectedEndTime());
    }

    private void SeekReplay(int logicalIndex, bool crossfade = false, bool synchronizeLists = true)
    {
        if (replaySession is null || replaySession.Count == 0)
        {
            return;
        }

        var clampedIndex = playbackController.Seek(logicalIndex);
        var snapshot = replayFrames?[clampedIndex] ?? replaySession.Seek(clampedIndex);
        reviewWorkspace?.SelectFrame(clampedIndex);
        if (reviewWorkspace?.SelectedContactId is null && InspectorContactsList.SelectedItem is not null)
            InspectorContactsList.SelectedItem = null;
        PaintSurface.SetHighlightedContact(reviewWorkspace?.SelectedContactId);
        RenderReplayFrame(snapshot, crossfade);
        UpdateReplayProgress(snapshot);

        if (synchronizeLists)
            SynchronizeReplayLists(clampedIndex, snapshot);

        if (synchronizeLists || ShouldPresentPlaybackDetails())
            PresentReplayDetails(snapshot);
    }

    private void UpdateReplayProgress(ITouchReplaySnapshot snapshot)
    {
        ReplayClockText.Text = FormatClock(SelectedTime(snapshot.Timeline));
        ReplayEndClockText.Text = FormatClock(SelectedEndTime());
        var physicalMaximum = Math.Max(1, session?.Records.Count - 1 ?? 1);
        var physicalValue = Math.Min(physicalMaximum, snapshot.PrimarySource.Index);
        var evidenceMaximum = Math.Max(1, diagnosticRows.Length);
        var evidenceValue = CountDiagnosticsThroughLine(snapshot.PrimarySource.Location.LineNumber);
        ReplayTimelineSurface.SetFrameState(
            snapshot.LogicalIndex,
            physicalValue / (double)physicalMaximum,
            evidenceValue / (double)evidenceMaximum);
    }

    private int CountDiagnosticsThroughLine(int lineNumber)
    {
        var low = 0;
        var high = diagnosticLineNumbers.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (diagnosticLineNumbers[middle] <= lineNumber) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private void SynchronizeReplayLists(int logicalIndex, ITouchReplaySnapshot? snapshot = null)
    {
        if (replaySession is null || decodedRows.Length == 0 || logicalIndex < 0)
            return;

        var clampedIndex = Math.Clamp(logicalIndex, 0, decodedRows.Length - 1);
        snapshot ??= replayFrames?[clampedIndex] ?? replaySession.Seek(clampedIndex);
        synchronizingSelection = true;
        try
        {
            var decodedRow = decodedRows[clampedIndex];
            DecodedFramesList.SelectedItem = decodedRow;
            DecodedFramesList.ScrollIntoView(decodedRow);
            var primaryPhysical = snapshot.PhysicalRecords[^1];
            if (rawRowsById.TryGetValue(primaryPhysical.StableId, out var rawRow))
            {
                RawRecordsList.SelectedItem = rawRow;
                RawRecordsList.ScrollIntoView(rawRow);
            }
        }
        finally
        {
            synchronizingSelection = false;
        }
    }

    private void UpdateAutoPauseIndicator(ReplayPlaybackPreferences preferences)
    {
        var enabled = new List<string>();
        if (preferences.PauseOnAlarmOrQaFail) enabled.Add("Alarm / QA Fail");
        if (preferences.PauseOnBreak) enabled.Add("Break");
        if (preferences.PauseOnAllBreak) enabled.Add("All Break");
        AutoPauseSettingsButton.Classes.Set("active", enabled.Count > 0);
        ToolTip.SetTip(AutoPauseSettingsButton,
            enabled.Count == 0 ? "Auto-pause is disabled" : $"Auto-pause · {string.Join(" · ", enabled)}");
    }

    private void PreviousFrameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        SeekReplay(playbackController.Step(-1));
    }

    private void NextFrameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        SeekReplay(playbackController.Step(1));
    }

    private void PlayPauseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (playbackCancellation is not null)
        {
            StopPlayback();
            return;
        }

        var start = playbackController.Play();
        if (start.Kind == ReplayPlaybackStartKind.Empty)
            return;
        if (start.Kind == ReplayPlaybackStartKind.InvalidLoop)
        {
            TimelineStatusText.Text = "Loop needs at least 2 frames · drag either Loop handle to widen the range";
            return;
        }
        SeekReplay(start.LogicalIndex);
        var cancellation = new CancellationTokenSource();
        playbackCancellation = cancellation;
        SetPlaybackVisualState(playing: true);
        UpdatePlaybackStatus();
        _ = RunPlaybackAsync(cancellation.Token, start.Generation);
    }

    private async Task RunPlaybackAsync(CancellationToken cancellationToken, int generation)
    {
        var playbackStarted = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plan = playbackController.PlanNext(generation);
                switch (plan.Kind)
                {
                    case ReplayPlaybackOperationKind.Stale:
                        return;
                    case ReplayPlaybackOperationKind.LoopRestart:
                        SeekReplay(plan.LogicalIndex, crossfade: true, synchronizeLists: false);
                        playbackStarted = Stopwatch.GetTimestamp();
                        continue;
                    case ReplayPlaybackOperationKind.Complete:
                        StopPlaybackIfCurrent(generation, "Replay complete · use Home, seek, or Play to restart");
                        return;
                    case ReplayPlaybackOperationKind.InvalidLoop:
                        StopPlaybackIfCurrent(
                            generation,
                            "Loop needs at least 2 frames · drag either Loop handle to widen the range");
                        return;
                }

                await DelayUntilAsync(playbackStarted, plan.DueTime, cancellationToken);
                var operation = playbackController.Advance(
                    generation,
                    Stopwatch.GetElapsedTime(playbackStarted));
                if (operation.Kind == ReplayPlaybackOperationKind.Stale)
                    return;
                if (operation.Kind is ReplayPlaybackOperationKind.Move or ReplayPlaybackOperationKind.Pause)
                    SeekReplay(operation.LogicalIndex, synchronizeLists: false);
                if (operation.Kind == ReplayPlaybackOperationKind.Pause)
                {
                    if (operation.PauseDecision?.SelectReview == true)
                        SelectReviewAt(operation.LogicalIndex);
                    StopPlaybackIfCurrent(
                        generation,
                        $"Paused on {PauseReasonText(operation.PauseDecision)} · continue when ready");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StopPlaybackIfCurrent(generation, $"Replay stopped after an error · {exception.Message}");
        }
    }

    private void StopPlaybackIfCurrent(int generation, string status)
    {
        if (generation == playbackController.Generation)
            StopPlayback(status);
    }

    private static string PauseReasonText(ReplayPlaybackPauseDecision? decision) => decision?.Reason switch
    {
        ReplayPlaybackPauseReason.Review => "Alarm / QA Fail",
        ReplayPlaybackPauseReason.AllBreak => "All Break",
        _ => "reported Break",
    };

    private static async Task DelayUntilAsync(
        long startedTimestamp,
        TimeSpan scheduledElapsed,
        CancellationToken cancellationToken)
    {
        var waited = false;
        while (true)
        {
            var remaining = scheduledElapsed - Stopwatch.GetElapsedTime(startedTimestamp);
            if (remaining <= TimeSpan.Zero) break;
            var slice = remaining > TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : remaining < TimeSpan.FromMilliseconds(1)
                    ? TimeSpan.FromMilliseconds(1)
                    : remaining;
            await Task.Delay(slice, cancellationToken);
            waited = true;
        }
        if (!waited)
            await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private TimeSpan DelayBetween(int current, int next)
    {
        return playbackController.Count == 0 ? TimeSpan.Zero : playbackController.DelayBetween(current, next);
    }

    private void UpdatePlaybackStatus()
    {
        if (replaySession is null || replaySession.Count == 0 || currentLogicalIndex < 0)
            return;
        var nextIndex = Math.Min(currentLogicalIndex + 1, replaySession.Count - 1);
        TimelineStatusText.Text =
            $"Playing {(maxReplaySpeed ? "MAX" : $"{replaySpeed:0.###}×")} · " +
            $"next {DelayBetween(currentLogicalIndex, nextIndex).TotalMilliseconds:0.###} ms · " +
            $"frame interval {replaySession.FrameInterval.TotalMilliseconds:0.###} ms";
    }

    private void RestartPlaybackTimingIfActive(ReplayPlaybackConfigurationChange change)
    {
        var previous = playbackCancellation;
        if (!change.RestartRequired || previous is null || replaySession is null || replaySession.Count == 0)
            return;

        var replacement = new CancellationTokenSource();
        playbackCancellation = replacement;
        previous.Cancel();
        previous.Dispose();
        UpdatePlaybackStatus();
        _ = RunPlaybackAsync(replacement.Token, change.Generation);
    }

    private void StopPlayback(string? status = null)
    {
        var cancellation = playbackCancellation;
        playbackCancellation = null;
        playbackController.Pause();
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (cancellation is not null && replaySession is { Count: > 0 } && currentLogicalIndex >= 0)
        {
            SynchronizeReplayLists(currentLogicalIndex);
            if (replayFrames is { } frames) PresentReplayDetails(frames[currentLogicalIndex]);
        }
        if (PlayPauseButton is not null)
        {
            SetPlaybackVisualState(playing: false);
        }
        if (TimelineStatusText is not null && replaySession is { Count: > 0 } &&
            (status is not null || cancellation is not null))
        {
            TimelineStatusText.Text = status ?? "Paused · Space play/pause · ←/→ step · drag Loop handles to set range";
        }
    }

    private void SetPlaybackVisualState(bool playing)
    {
        PlayPauseButton.Classes.Set("playing", playing);
        PlayPauseButton.Content = playing ? "Ⅱ  Pause" : "▶  Play";
        AutomationProperties.SetName(PlayPauseButton, playing ? "Pause replay" : "Play replay");
    }

    private void ReplayTimelineSurface_OnSeekRequested(object? sender, ReplayTimelineSeekEventArgs e)
    {
        if (synchronizingSelection || replaySession is null) return;

        if (e.IsContinuous)
        {
            pendingContinuousTimelineSeek = e.LogicalIndex;
            if (continuousTimelineSeekScheduled) return;
            continuousTimelineSeekScheduled = true;
            Dispatcher.UIThread.Post(ProcessPendingContinuousTimelineSeek, DispatcherPriority.Render);
            return;
        }

        pendingContinuousTimelineSeek = null;
        StopPlayback();
        SeekReplay(e.LogicalIndex);
    }

    private void ProcessPendingContinuousTimelineSeek()
    {
        continuousTimelineSeekScheduled = false;
        if (pendingContinuousTimelineSeek is not { } logicalIndex || replaySession is null) return;
        pendingContinuousTimelineSeek = null;
        StopPlayback();
        SeekReplay(logicalIndex, synchronizeLists: false);
    }

    private void ReplaySpeedComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if ((sender as ComboBox)?.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var tag = item.Tag?.ToString() ?? "1";
        var nextMaximum = tag.Equals("max", StringComparison.OrdinalIgnoreCase);
        var nextSpeed = replaySpeed;
        if (!nextMaximum && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            nextSpeed = speed;
        }
        if (nextMaximum == maxReplaySpeed && Math.Abs(nextSpeed - replaySpeed) < 0.0001) return;
        NotifyPlaybackTimingChanged(nextMaximum
            ? ReplayPlaybackRate.Maximum
            : ReplayPlaybackRate.FromMultiplier(nextSpeed));
    }

    private void ClockModeComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CompressIdleCheckBox is not null)
            CompressIdleCheckBox.IsVisible = ClockModeComboBox.SelectedIndex == 0;
        NotifyPlaybackTimingChanged();
        if (replaySession is not null && currentLogicalIndex >= 0)
        {
            ReplayClockText.Text = FormatClock(SelectedTime(replaySession.Timeline[currentLogicalIndex]));
            ReplayEndClockText.Text = FormatClock(SelectedEndTime());
            if (currentInspectorRecord is not null)
                ShowRecord(currentInspectorRecord, currentInspectorFrame, currentInspectorSnapshot);
        }
    }

    private void ReplayTimingOption_OnChanged(object? sender, RoutedEventArgs e) => NotifyPlaybackTimingChanged();

    private void NotifyPlaybackTimingChanged(ReplayPlaybackRate? selectedRate = null)
    {
        if (ClockModeComboBox is null || CompressIdleCheckBox is null) return;
        var change = playbackController.ConfigureTiming(
            ClockModeComboBox.SelectedIndex == 1
                ? ReplayPlaybackClockDomain.Frame
                : ReplayPlaybackClockDomain.Recorded,
            CompressIdleCheckBox.IsChecked == true,
            selectedRate ?? playbackController.Rate);
        RestartPlaybackTimingIfActive(change);
    }

    private void LoopInButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (currentLogicalIndex < 0)
        {
            return;
        }
        var nextStart = currentLogicalIndex;
        var nextEnd = loopOut < nextStart ? null : loopOut;
        EnableLoopFromShortcut(nextStart, nextEnd);
        UpdateLoopText();
    }

    private void LoopOutButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (currentLogicalIndex < 0)
        {
            return;
        }
        var nextEnd = currentLogicalIndex;
        var nextStart = loopIn > nextEnd ? null : loopIn;
        EnableLoopFromShortcut(nextStart, nextEnd);
        UpdateLoopText();
    }

    private void LoopToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (synchronizingLoopControls || ReplayTimelineSurface is null) return;
        var enabled = LoopToggleButton.IsChecked == true;
        var nextStart = loopIn;
        var nextEnd = loopOut;
        if (enabled && replaySession is { Count: > 1 })
        {
            if (nextStart is null || nextEnd is null)
            {
                var range = ReplayLoopRangePlanner.CreateDefault(
                    currentLogicalIndex,
                    replaySession.Count,
                    replaySession.FrameInterval);
                nextStart = range.StartLogicalIndex;
                nextEnd = range.EndLogicalIndex;
            }
        }
        ApplyLoopConfiguration(enabled, nextStart, nextEnd);
        UpdateLoopText();
    }

    private void ReplayTimelineSurface_OnLoopRangeChanged(object? sender, ReplayTimelineLoopEventArgs e)
    {
        ApplyLoopConfiguration(true, e.StartLogicalIndex, e.EndLogicalIndex);
        UpdateLoopText();
    }

    private void EnableLoopFromShortcut(int? start, int? end)
    {
        var enabled = start is not null && end is not null;
        ApplyLoopConfiguration(enabled || loopEnabled, start, end);
        if (!enabled) return;
        synchronizingLoopControls = true;
        try
        {
            LoopToggleButton.IsChecked = true;
        }
        finally
        {
            synchronizingLoopControls = false;
        }
    }

    private void ApplyLoopConfiguration(bool enabled, int? start, int? end)
    {
        var change = playbackController.ConfigureLoop(enabled, start, end);
        RestartPlaybackTimingIfActive(change);
    }

    private void UpdateLoopText()
    {
        var hasRange = loopIn is not null && loopOut is not null;
        LoopRangeText.Text = loopEnabled && hasRange
            ? $"Loop: on · {loopIn!.Value + 1:N0}-{loopOut!.Value + 1:N0}"
            : "Loop: off";
        ReplayTimelineSurface.SetLoopRange(loopIn, loopOut, loopEnabled);
        RefreshOutputPreviewIfVisible();
    }

    private TimeSpan SelectedTime(ReplayTimelineEntry entry) =>
        playbackController.ClockDomain == ReplayPlaybackClockDomain.Frame
            ? entry.FrameTime
            : entry.RecordedTime;

    private TimeSpan SelectedEndTime()
    {
        if (replaySession is null || replaySession.Count == 0)
        {
            return TimeSpan.Zero;
        }
        return SelectedTime(replaySession.Timeline[^1]);
    }

    private static string FormatClock(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);

    private void JumpFrames(int delta)
    {
        StopPlayback();
        SeekReplay(currentLogicalIndex + delta);
    }

    private void SelectRelativeReplaySpeed(int delta)
    {
        var items = ReplaySpeedComboBox.Items.OfType<ComboBoxItem>().ToArray();
        if (items.Length == 0) return;
        ReplaySpeedComboBox.SelectedIndex = Math.Clamp(ReplaySpeedComboBox.SelectedIndex + delta, 0, items.Length - 1);
        TimelineStatusText.Text = $"Replay speed · {items[ReplaySpeedComboBox.SelectedIndex].Content}";
    }

    private void SelectNormalReplaySpeed()
    {
        var items = ReplaySpeedComboBox.Items.OfType<ComboBoxItem>().ToArray();
        var normalIndex = Array.FindIndex(items, item => string.Equals(item.Tag?.ToString(), "1", StringComparison.Ordinal));
        if (normalIndex < 0) return;
        ReplaySpeedComboBox.SelectedIndex = normalIndex;
        TimelineStatusText.Text = "Replay speed · 1×";
    }
}
