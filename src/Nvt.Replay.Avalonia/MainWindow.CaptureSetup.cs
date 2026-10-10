using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nvt.Replay.Avalonia.Controls;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Avalonia;

public partial class MainWindow
{
    private static readonly SelectOption[] CaptureSetupEventVersions =
    [
        new("0x82", "Common Event Buffer format 0x82", "0x82"),
        new("0x83", "Common Event Buffer format 0x83", "0x83"),
        new("0x84", "Common Event Buffer format 0x84", "0x84"),
        new("0x85", "Common Event Buffer format 0x85", "0x85"),
        new("0x97", "Desay two-transaction format", "0x97"),
    ];

    private readonly EventSuppressionScope configuringCaptureSetup = new();

    private void ShowRegisterProfileInference(NvtRegisterProfileInferenceResult inference)
    {
        _captureWorkspace.Configure(inference);
        using (configuringCaptureSetup.Enter())
        {
            CaptureSetupEventVersionComboBox.SelectedItem = CurrentCaptureSetupVersion();
            CaptureSetupI2cAddressTextBox.Text = FormatTargetI2cAddress(targetI2cAddress);
            CaptureSetupPalmProfileComboBox.SelectedIndex = Desay97ProfileComboBox.SelectedIndex;
            PresentCaptureSetupInference(inference, preserveSelectedProfile: false);
        }

        UpdateCaptureSetupValidation();
        RegisterProfileInferenceOverlay.IsVisible = true;
        RegisterProfileInferenceOverlay.Focus();
        CaptureSetupEventVersionComboBox.Focus();
    }

    private void PresentCaptureSetupInference(
        NvtRegisterProfileInferenceResult inference,
        bool preserveSelectedProfile)
    {
        var previousProfile = preserveSelectedProfile
            ? (InferredRegisterProfileComboBox.SelectedItem as RegisterProfileChoice)?.IcFamily
            : null;
        var choices = new[] { new RegisterProfileChoice("Unconfirmed", null) }
            .Concat(NvtRegisterCatalog.Profiles.Select(profile =>
                new RegisterProfileChoice(profile.IcFamily, profile.IcFamily)))
            .ToArray();
        InferredRegisterProfileComboBox.ItemsSource = choices;
        ComboBoxAutoSizer.Fit(InferredRegisterProfileComboBox);

        var suggestedProfile = previousProfile ?? inference.UniqueProfile?.IcFamily;
        InferredRegisterProfileComboBox.SelectedItem = choices.First(choice =>
            string.Equals(choice.IcFamily, suggestedProfile, StringComparison.OrdinalIgnoreCase));

        RegisterProfileInferenceBadge.Classes.Remove("detected");
        RegisterProfileInferenceBadge.Classes.Remove("review");
        RegisterProfileInferenceBadge.Classes.Remove("none");
        switch (inference.Status)
        {
            case NvtRegisterProfileInferenceStatus.Unique:
                CaptureSetupDetectionTitleText.Text =
                    $"{inference.UniqueProfile!.IcFamily} inferred from capture";
                RegisterProfileInferenceBadgeText.Text = "AUTO-DETECTED";
                RegisterProfileInferenceBadge.Classes.Add("detected");
                CaptureSetupProfileHintText.Text = "SUGGESTED";
                break;
            case NvtRegisterProfileInferenceStatus.Ambiguous:
                CaptureSetupDetectionTitleText.Text =
                    $"Event Buffer 0x{inference.Evidence[0].SelectedPage:X5} matches multiple IC profiles";
                RegisterProfileInferenceBadgeText.Text = "NEEDS CONFIRMATION";
                RegisterProfileInferenceBadge.Classes.Add("review");
                CaptureSetupProfileHintText.Text = "REVIEW";
                break;
            case NvtRegisterProfileInferenceStatus.Conflicting:
                CaptureSetupDetectionTitleText.Text = "Multiple Event Buffer pages need review";
                RegisterProfileInferenceBadgeText.Text = "CONFLICTING EVIDENCE";
                RegisterProfileInferenceBadge.Classes.Add("review");
                CaptureSetupProfileHintText.Text = "REVIEW";
                break;
            default:
                CaptureSetupDetectionTitleText.Text = "No IC profile inferred";
                RegisterProfileInferenceBadgeText.Text = "NO IC MATCH";
                RegisterProfileInferenceBadge.Classes.Add("none");
                CaptureSetupProfileHintText.Text = "OPTIONAL";
                break;
        }

        RegisterProfileInferenceTitleText.Text = "Confirm decoder settings";
        RegisterProfileInferenceEvidenceText.Text = CaptureSetupEvidenceSummary(inference);
        CaptureSetupSourceText.Text = session?.Probe.DisplayName ?? "-";
        CaptureSetupTargetText.Text = $"I²C {CaptureSetupI2cAddressTextBox.Text}";
    }

    private static string CaptureSetupEvidenceSummary(NvtRegisterProfileInferenceResult inference)
    {
        if (inference.Evidence.Count == 0)
            return "No verified Event Buffer address evidence was found.";

        var pages = string.Join(", ", inference.Evidence
            .Select(item => $"0x{item.SelectedPage:X5}")
            .Distinct(StringComparer.Ordinal));
        var first = inference.Evidence[0];
        return $"{inference.Evidence.Count:N0} matching Event Buffer records at {pages}. " +
               $"First evidence: record {first.RecordIndex:N0}, {first.DisplayEvidence}.";
    }

    private SelectOption? CurrentCaptureSetupVersion()
    {
        var selectedVersion = (EventVersionComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        return CaptureSetupEventVersions.FirstOrDefault(option =>
            option.Value.Equals(selectedVersion, StringComparison.OrdinalIgnoreCase));
    }

    private void HideRegisterProfileInference()
    {
        _captureWorkspace.DeferConfiguration();
        RegisterProfileInferenceOverlay.IsVisible = false;
        InferredRegisterProfileComboBox.ItemsSource = null;
        InferredRegisterProfileComboBox.SelectedIndex = -1;
        CaptureSetupEventVersionComboBox.SelectedIndex = -1;
        CaptureSetupPalmProfileComboBox.SelectedIndex = -1;
        CaptureSetupPalmProfilePanel.IsVisible = false;
    }

    private async void ApplyRegisterProfileInferenceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryReadCaptureSetup(
                out var version,
                out var targetAddress,
                out var profileChoice,
                out var palmProfile))
        {
            UpdateCaptureSetupValidation();
            return;
        }

        CaptureSetupDecodeButton.IsEnabled = false;
        targetI2cAddress = targetAddress;
        TargetI2cAddressTextBox.Text = FormatTargetI2cAddress(targetI2cAddress);
        if (!string.Equals(session?.RegisterProfile, profileChoice.IcFamily, StringComparison.OrdinalIgnoreCase))
        {
            SelectRegisterProfileChoice(profileChoice.IcFamily);
            await ApplyRegisterProfileAsync(profileChoice, decodeIfReady: false);
            if (!string.Equals(session?.RegisterProfile, profileChoice.IcFamily, StringComparison.OrdinalIgnoreCase))
            {
                SetCaptureSetupValidation(
                    "The IC profile could not be applied. Raw data remains available.",
                    isReady: false);
                CaptureSetupDecodeButton.IsEnabled = false;
                return;
            }
        }
        else
        {
            SelectRegisterProfileChoice(profileChoice.IcFamily);
        }

        using (configuringEventVersion.Enter())
        {
            EventVersionComboBox.SelectedIndex = Array.FindIndex(
                CaptureSetupEventVersions,
                option => option.Value.Equals(version, StringComparison.OrdinalIgnoreCase));
            Desay97ProfileComboBox.SelectedIndex = palmProfile;
        }

        HideRegisterProfileInference();
        await _replayShell.ExecuteAsync(_replayShell.DecodeCommand);
    }

    private bool TryReadCaptureSetup(
        out string version,
        out int targetAddress,
        out RegisterProfileChoice profileChoice,
        out int palmProfile)
    {
        version = (CaptureSetupEventVersionComboBox.SelectedItem as SelectOption)?.Value ?? string.Empty;
        profileChoice = InferredRegisterProfileComboBox.SelectedItem as RegisterProfileChoice ??
                        new RegisterProfileChoice("Unconfirmed", null);
        palmProfile = CaptureSetupPalmProfileComboBox.SelectedIndex;
        targetAddress = targetI2cAddress;
        if (string.IsNullOrWhiteSpace(version) ||
            !TryParseTargetI2cAddress(CaptureSetupI2cAddressTextBox.Text, out targetAddress))
            return false;
        if (!version.Equals("0x97", StringComparison.OrdinalIgnoreCase)) return true;
        return profileChoice.IcFamily is not null && palmProfile >= 0;
    }

    private void CancelRegisterProfileInferenceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        HideRegisterProfileInference();
        ConfigurationHintText.Text =
            "Event Buffer Version remains unconfirmed; Raw Explorer is available without decoding.";
        SessionStatusText.Text = "Capture setup deferred; original source remains unchanged";
        WorkspaceTabs.SelectedIndex = 0;
    }

    private void RegisterProfileInferenceOverlay_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CancelRegisterProfileInferenceButton_OnClick(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void CaptureSetupEventVersionComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CaptureSetupPalmProfilePanel is null) return;
        var isDesay97 = CaptureSetupEventVersionComboBox.SelectedItem is SelectOption { Value: "0x97" };
        CaptureSetupPalmProfilePanel.IsVisible = isDesay97;
        CaptureSetupProfileHintText.Text = isDesay97
            ? "REQUIRED"
            : pendingRegisterProfileInference?.Status == NvtRegisterProfileInferenceStatus.Unique
                ? "SUGGESTED"
                : pendingRegisterProfileInference?.Status is NvtRegisterProfileInferenceStatus.Ambiguous or
                    NvtRegisterProfileInferenceStatus.Conflicting
                    ? "REVIEW"
                    : "OPTIONAL";
        if (!isDesay97) CaptureSetupPalmProfileComboBox.SelectedIndex = -1;
        if (!configuringCaptureSetup.IsActive) UpdateCaptureSetupValidation();
    }

    private void CaptureSetupPalmProfileComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!configuringCaptureSetup.IsActive) UpdateCaptureSetupValidation();
    }

    private void CaptureSetupRegisterProfileComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!configuringCaptureSetup.IsActive) UpdateCaptureSetupValidation();
    }

    private void CaptureSetupI2cAddressTextBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (session is null ||
            !TryParseTargetI2cAddress(CaptureSetupI2cAddressTextBox.Text, out var requested))
        {
            SetCaptureSetupValidation(
                "Enter a 7-bit I²C address from 0x00 through 0x7F.",
                isReady: false);
            CaptureSetupDecodeButton.IsEnabled = false;
            return;
        }

        CaptureSetupI2cAddressTextBox.Text = FormatTargetI2cAddress(requested);
        CaptureSetupTargetText.Text = $"I²C {FormatTargetI2cAddress(requested)}";
        var inference = NvtRegisterProfileInference.Infer(session.Records, requested);
        _captureWorkspace.Configure(inference);
        using (configuringCaptureSetup.Enter())
        {
            PresentCaptureSetupInference(inference, preserveSelectedProfile: false);
        }
        UpdateCaptureSetupValidation();
    }

    private void UpdateCaptureSetupValidation()
    {
        var version = (CaptureSetupEventVersionComboBox.SelectedItem as SelectOption)?.Value;
        if (string.IsNullOrWhiteSpace(version))
        {
            SetCaptureSetupValidation("Choose an Event Buffer Version to continue.", isReady: false);
            CaptureSetupDecodeButton.IsEnabled = false;
            return;
        }
        if (!TryParseTargetI2cAddress(CaptureSetupI2cAddressTextBox.Text, out _))
        {
            SetCaptureSetupValidation(
                "Enter a 7-bit I²C address from 0x00 through 0x7F.",
                isReady: false);
            CaptureSetupDecodeButton.IsEnabled = false;
            return;
        }
        if (version.Equals("0x97", StringComparison.OrdinalIgnoreCase) &&
            InferredRegisterProfileComboBox.SelectedItem is not RegisterProfileChoice { IcFamily: not null })
        {
            SetCaptureSetupValidation("0x97 requires a confirmed IC profile.", isReady: false);
            CaptureSetupDecodeButton.IsEnabled = false;
            return;
        }
        if (version.Equals("0x97", StringComparison.OrdinalIgnoreCase) &&
            CaptureSetupPalmProfileComboBox.SelectedIndex < 0)
        {
            SetCaptureSetupValidation("Choose Standard or Benz Palm for 0x97.", isReady: false);
            CaptureSetupDecodeButton.IsEnabled = false;
            return;
        }

        SetCaptureSetupValidation(
            "Ready to decode. The original capture will remain unchanged.",
            isReady: true);
        CaptureSetupDecodeButton.IsEnabled = true;
    }

    private void SetCaptureSetupValidation(string message, bool isReady)
    {
        CaptureSetupValidationText.Text = message;
        CaptureSetupValidationText.Classes.Remove("ready");
        if (isReady) CaptureSetupValidationText.Classes.Add("ready");
    }
}
