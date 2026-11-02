using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Flow.Launcher.Plugin.NumenCalculator;

/// <summary>
/// Flow settings panel for the plugin (spec §10). Every control change persists through
/// <see cref="Plugin.SaveSettings"/>; evaluation picks the new number/date formatting and timezone
/// up on the next query.
/// </summary>
public partial class SettingsControl : UserControl
{
    private readonly Plugin? _plugin;
    private bool _loading;

    public SettingsControl()
    {
        InitializeComponent();
        ApplyDateExamples();
    }

    /// <summary>
    /// Fills the pattern examples with the plugin's real formatter so weekday, month, and AM/PM
    /// names match the system culture instead of being hard-coded English.
    /// </summary>
    private void ApplyDateExamples()
    {
        var local = new DateTime(2026, 10, 2, 14, 23, 0, DateTimeKind.Local);
        TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(local);
        var instant = new DateTimeOffset(local, offset);
        var sample = new DateTimePart(
            instant.ToUnixTimeSeconds(),
            0,
            null,
            (int)offset.TotalSeconds,
            null,
            null);

        SetDateExample(DateExamplePattern1, DateExampleOutput1, sample);
        SetDateExample(DateExamplePattern2, DateExampleOutput2, sample);
        SetDateExample(DateExamplePattern3, DateExampleOutput3, sample);
    }

    private static void SetDateExample(TextBlock pattern, TextBlock output, DateTimePart sample)
    {
        output.Text = DateOutputFormatter.Format(sample, pattern.Text) ?? string.Empty;
    }

    public SettingsControl(Plugin plugin)
        : this()
    {
        ArgumentNullException.ThrowIfNull(plugin);
        _plugin = plugin;
        ApplyTranslations();
        LoadSettings();
        Unloaded += SettingsControl_Unloaded;
    }

    private void ApplyTranslations()
    {
        NumberFormatLabel.Text = Translate("flowlauncher_plugin_numen_calculator_settings_number_format");
        DecimalSeparatorLabel.Text = Translate("flowlauncher_plugin_numen_calculator_settings_decimal_separator");
        GroupSeparatorLabel.Text = Translate("flowlauncher_plugin_numen_calculator_settings_group_separator");
        NumberFormatHintLabel.Text = Translate("flowlauncher_plugin_numen_calculator_settings_expression_hint");
        DecimalHintLabel.Text = Translate("flowlauncher_plugin_numen_calculator_settings_decimal_separator");
        DecimalHintValue.Text = Translate("flowlauncher_plugin_numen_calculator_settings_decimal_accepts");
        GroupHintLabel.Text = Translate("flowlauncher_plugin_numen_calculator_settings_group_separator");
        GroupHintValue.Text = Translate("flowlauncher_plugin_numen_calculator_settings_group_accepts");
        NumberFormatFooter.Text = Translate("flowlauncher_plugin_numen_calculator_settings_number_format_footer");
        DateFormatLabel.Text = Translate("flowlauncher_plugin_numen_calculator_settings_date_format");
        DateFormatHintLabel.Text = Translate("flowlauncher_plugin_numen_calculator_settings_expression_hint");
        DateFormatIsoExample.Text = Translate("flowlauncher_plugin_numen_calculator_settings_date_format_iso_example");
        DateFormatFooter.Text = Translate("flowlauncher_plugin_numen_calculator_settings_date_format_footer");
        TimezoneLabel.Text = Translate("flowlauncher_plugin_numen_calculator_settings_timezone");
        TimezoneHelp.Text = Translate("flowlauncher_plugin_numen_calculator_settings_timezone_help");
        ImplicitCurrencyBox.Content = Translate("flowlauncher_plugin_numen_calculator_settings_implicit_currency");
        ImplicitUnitBox.Content = Translate("flowlauncher_plugin_numen_calculator_settings_implicit_unit");
        ImplicitTimezoneBox.Content = Translate("flowlauncher_plugin_numen_calculator_settings_implicit_timezone");
        RefreshOnStartupBox.Content = Translate("flowlauncher_plugin_numen_calculator_settings_refresh_on_startup");
        EndpointLabel.Text = Translate("flowlauncher_plugin_numen_calculator_settings_rate_endpoint");
        EndpointHelp.Text = Translate("flowlauncher_plugin_numen_calculator_settings_rate_endpoint_help");
        RefreshButton.Content = Translate("flowlauncher_plugin_numen_calculator_settings_refresh_now");
    }

    private void LoadSettings()
    {
        if (_plugin is null)
            return;

        var settings = _plugin.Settings;
        _loading = true;
        try
        {
            DecimalSeparatorBox.Text = settings.DecimalSeparator ?? "";
            GroupSeparatorBox.Text = settings.GroupSeparator ?? "";
            DateFormatBox.Text = settings.DateFormat ?? "";

            var timezoneItems = new List<TimezoneItem>
            {
                new(null, Translate("flowlauncher_plugin_numen_calculator_settings_timezone_system")),
            };
            timezoneItems.AddRange(TimeZoneCatalog.IanaIds.Select(id => new TimezoneItem(id, id)));

            TimezoneBox.ItemsSource = timezoneItems;
            TimezoneBox.SelectedIndex = IndexOfTimezone(timezoneItems, settings.Timezone);

            ImplicitCurrencyBox.IsChecked = settings.ImplicitCurrencyConversion;
            ImplicitUnitBox.IsChecked = settings.ImplicitUnitConversion;
            ImplicitTimezoneBox.IsChecked = settings.ImplicitTimezoneConversion;
            RefreshOnStartupBox.IsChecked = settings.RefreshRatesOnStartup;
            EndpointBox.Text = settings.RateEndpoint ?? "";
        }
        finally
        {
            _loading = false;
        }
    }

    private static int IndexOfTimezone(IReadOnlyList<TimezoneItem> items, string? name)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].IanaName, name, StringComparison.Ordinal))
                return index;
        }

        return 0;
    }

    private void SeparatorBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Moving between the two boxes must not validate a half-entered pair.
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (!DecimalSeparatorBox.IsKeyboardFocusWithin &&
                    !GroupSeparatorBox.IsKeyboardFocusWithin)
                {
                    CommitNumberFormat(showError: true);
                }
            }),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void DateFormatBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitDateFormat(showError: true);
    }

    private void CommitNumberFormat(bool showError)
    {
        if (_loading || _plugin is null)
            return;

        if (!NumberFormatSpec.TryNormalize(
                DecimalSeparatorBox.Text,
                GroupSeparatorBox.Text,
                out var decimalSeparator,
                out var groupSeparator,
                out _))
        {
            DecimalSeparatorBox.Text = _plugin.Settings.DecimalSeparator ?? "";
            GroupSeparatorBox.Text = _plugin.Settings.GroupSeparator ?? "";
            if (showError)
                ShowError(Translate("flowlauncher_plugin_numen_calculator_settings_number_format_invalid"));
            return;
        }

        DecimalSeparatorBox.Text = decimalSeparator;
        GroupSeparatorBox.Text = groupSeparator;

        if (!string.Equals(decimalSeparator, _plugin.Settings.DecimalSeparator, StringComparison.Ordinal) ||
            !string.Equals(groupSeparator, _plugin.Settings.GroupSeparator, StringComparison.Ordinal))
        {
            _plugin.Settings.DecimalSeparator = decimalSeparator;
            _plugin.Settings.GroupSeparator = groupSeparator;
            _plugin.SaveSettings();
        }
    }

    private void CommitDateFormat(bool showError)
    {
        if (_loading || _plugin is null)
            return;

        if (!DateFormatSpec.TryNormalize(DateFormatBox.Text, out var normalized, out _))
        {
            DateFormatBox.Text = _plugin.Settings.DateFormat ?? "";
            if (showError)
                ShowError(Translate("flowlauncher_plugin_numen_calculator_settings_date_format_invalid"));
            return;
        }

        DateFormatBox.Text = normalized;

        if (!string.Equals(normalized, _plugin.Settings.DateFormat, StringComparison.Ordinal))
        {
            _plugin.Settings.DateFormat = normalized;
            _plugin.SaveSettings();
        }
    }

    private void TimezoneBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _plugin is null)
            return;

        _plugin.Settings.Timezone = (TimezoneBox.SelectedItem as TimezoneItem)?.IanaName ?? "";
        _plugin.SaveSettings();
    }

    private void ConversionBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _plugin is null)
            return;

        _plugin.Settings.ImplicitCurrencyConversion = ImplicitCurrencyBox.IsChecked == true;
        _plugin.Settings.ImplicitUnitConversion = ImplicitUnitBox.IsChecked == true;
        _plugin.Settings.ImplicitTimezoneConversion = ImplicitTimezoneBox.IsChecked == true;
        _plugin.Settings.RefreshRatesOnStartup = RefreshOnStartupBox.IsChecked == true;
        _plugin.SaveSettings();
    }

    private void EndpointBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitEndpoint(showError: true);
    }

    private void CommitEndpoint(bool showError)
    {
        if (_loading || _plugin is null)
            return;

        var candidate = (EndpointBox.Text ?? "").Trim();
        if (!IsValidHttpEndpoint(candidate))
        {
            EndpointBox.Text = _plugin.Settings.RateEndpoint ?? "";
            if (showError)
                ShowError(Translate("flowlauncher_plugin_numen_calculator_settings_endpoint_invalid"));
            return;
        }

        if (!string.Equals(candidate, _plugin.Settings.RateEndpoint, StringComparison.Ordinal))
        {
            _plugin.Settings.RateEndpoint = candidate;
            _plugin.SaveSettings();
        }
    }

    private static bool IsValidHttpEndpoint(string candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        !string.IsNullOrEmpty(uri.Host);

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_plugin is null || !RefreshButton.IsEnabled)
            return;

        RefreshButton.IsEnabled = false;
        RefreshStatusText.Text = Translate("flowlauncher_plugin_numen_calculator_settings_refresh_running");
        try
        {
            var result = await _plugin.RefreshRatesAsync();
            if (result.Applied)
            {
                RefreshStatusText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    Translate("flowlauncher_plugin_numen_calculator_settings_refresh_updated"),
                    DateTimeOffset.Now.ToString("g", CultureInfo.CurrentCulture));
            }
            else
            {
                var message = string.IsNullOrWhiteSpace(result.Error)
                    ? Translate("flowlauncher_plugin_numen_calculator_settings_refresh_failed_unknown")
                    : string.Format(
                        CultureInfo.CurrentCulture,
                        Translate("flowlauncher_plugin_numen_calculator_settings_refresh_failed"),
                        result.Error);
                RefreshStatusText.Text = message;
                ShowError(message);
            }
        }
        catch (Exception ex)
        {
            var message = string.Format(
                CultureInfo.CurrentCulture,
                Translate("flowlauncher_plugin_numen_calculator_settings_refresh_failed"),
                ex.Message);
            RefreshStatusText.Text = message;
            ShowError(message);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void SettingsControl_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_plugin is null)
            return;

        CommitEndpoint(showError: false);
        CommitNumberFormat(showError: false);
        CommitDateFormat(showError: false);
        _plugin.SaveSettings();
    }

    private void ShowError(string message)
    {
        try
        {
            _plugin?.Api?.ShowMsgError(
                Translate("flowlauncher_plugin_numen_calculator_settings_error_title"),
                message);
        }
        catch (Exception)
        {
            // A failing dialog must not break the settings panel.
        }
    }

    private string Translate(string key)
    {
        try
        {
            var translation = _plugin?.Api?.GetTranslation(key);
            return string.IsNullOrWhiteSpace(translation) ? key : translation!;
        }
        catch (Exception)
        {
            return key;
        }
    }

    private sealed record TimezoneItem(string? IanaName, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
