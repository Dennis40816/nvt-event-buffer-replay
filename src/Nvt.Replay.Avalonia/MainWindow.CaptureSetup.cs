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

    private bool configuringCaptureSetup;

    private void ShowRegisterProfileInference(NvtRegisterProfileInferenceResult inference)
    {
        pendingRegisterProfileInference = inference;
        configuringCaptureSetup = true;
        try
        {
            CaptureSetupEventVersionComboBox.SelectedItem = CurrentCaptureSetupVersion();
            CaptureSetupI2cAddressTextBox.Text = FormatTargetI2cAddress(targetI2cAddress);
            CaptureSetupPalmProfileComboBox.SelectedIndex = Desay97ProfileComboBox.SelectedIndex;
            PresentCaptureSetupInference(inference, preserveSelectedProfile: false);
        }
        finally
        {
            configuringCaptureSetup = false;
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
                RegisterProfileInferenceTitleText.Text =
                    $"Confirm {inference.UniqueProfile!.IcFamily} decoder settings";
                RegisterProfileInferenceBadgeText.Text = "AUTO-DETECTED";
                RegisterProfileInferenceBadge.Classes.Add("detected");
                CaptureSetupProfileHintText.Text = "AUTO-DETECTED";
                break;
            case NvtRegisterProfileInferenceStatus.Ambiguous:
                RegisterProfileInferenceTitleText.Text =
                    $"Event Buffer 0x{inference.Evidence[0].SelectedPage:X5} needs an IC choice";
                RegisterProfileInferenceBadgeText.Text = "NEEDS CONFIRMATION";
                RegisterProfileInferenceBadge.Classes.Add("review");
                CaptureSetupProfileHintText.Text = "CONFIRM";
                break;
            case NvtRegisterProfileInferenceStatus.Conflicting:
                RegisterProfileInferenceTitleText.Text = "Multiple Event Buffer pages were captured";
                RegisterProfileInferenceBadgeText.Text = "CONFLICTING EVIDENCE";
                RegisterProfileInferenceBadge.Classes.Add("review");
                CaptureSetupProfileHintText.Text = "CONFIRM";
                break;
            default:
                RegisterProfileInferenceTitleText.Text = "Choose the Event Buffer format";
                RegisterProfileInferenceBadgeText.Text = "NO IC MATCH";
                RegisterProfileInferenceBadge.Classes.Add("none");
                CaptureSetupProfileHintText.Text = "OPTIONAL";
                break;
        }

        RegisterProfileInferenceEvidenceText.Text = inference.EvidenceSummary;
        CaptureSetupSourceText.Text = session?.Probe.DisplayName ?? "-";
        CaptureSetupTargetText.Text = $"I²C {CaptureSetupI2cAddressTextBox.Text}";
    }

    private SelectOption? CurrentCaptureSetupVersion()
    {
        var selectedVersion = (EventVersionComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        return CaptureSetupEventVersions.FirstOrDefault(option =>
            option.Value.Equals(selectedVersion, StringComparison.OrdinalIgnoreCase));
    }

    private void HideRegisterProfileInference()
    {
        pendingRegisterProfileInference = null;
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

        configuringEventVersion = true;
        try
        {
            EventVersionComboBox.SelectedIndex = Array.FindIndex(
                CaptureSetupEventVersions,
                option => option.Value.Equals(version, StringComparison.OrdinalIgnoreCase));
            Desay97ProfileComboBox.SelectedIndex = palmProfile;
        }
        finally
        {
            configuringEventVersion = false;
        }

        HideRegisterProfileInference();
        await DecodeSelectedAsync();
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
                ? "AUTO-DETECTED"
                : pendingRegisterProfileInference?.Status is NvtRegisterProfileInferenceStatus.Ambiguous or
                    NvtRegisterProfileInferenceStatus.Conflicting
                    ? "CONFIRM"
                    : "OPTIONAL";
        if (!isDesay97) CaptureSetupPalmProfileComboBox.SelectedIndex = -1;
        if (!configuringCaptureSetup) UpdateCaptureSetupValidation();
    }

    private void CaptureSetupPalmProfileComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!configuringCaptureSetup) UpdateCaptureSetupValidation();
    }

    private void CaptureSetupRegisterProfileComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!configuringCaptureSetup) UpdateCaptureSetupValidation();
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
        pendingRegisterProfileInference = inference;
        configuringCaptureSetup = true;
        try
        {
            PresentCaptureSetupInference(inference, preserveSelectedProfile: false);
        }
        finally
        {
            configuringCaptureSetup = false;
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
