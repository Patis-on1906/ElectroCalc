using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ElectroCalc.Core.Models;

namespace ElectroCalc.UI.Views
{
    public partial class ThreePhaseCalculationDialog : Window
    {
        private readonly CircuitAnalysisSettings _analysis;
        public ThreePhaseFaultSettings Settings { get; private set; }

        public ThreePhaseCalculationDialog(CircuitAnalysisSettings analysis, ThreePhaseFaultSettings initial)
        {
            _analysis = analysis.Clone();
            Settings = initial.Clone();
            InitializeComponent();
            SelectEnum(CmbOperatingMode, initial.OperatingMode);
            TxtFaultResistance.Text = initial.ShortCircuitResistanceOhms.ToString("G", CultureInfo.InvariantCulture);
            RefreshLocations(initial.Location);
            RefreshUi();
        }

        private sealed class LocationOption
        {
            public ThreePhaseFaultLocation Value { get; init; }
            public string Text { get; init; } = string.Empty;
        }

        private ThreePhaseOperatingMode SelectedMode()
        {
            if (CmbOperatingMode.SelectedItem is ComboBoxItem item &&
                Enum.TryParse(item.Tag?.ToString(), out ThreePhaseOperatingMode value))
                return value;
            return ThreePhaseOperatingMode.Normal;
        }

        private static void SelectEnum(ComboBox combo, ThreePhaseOperatingMode value)
        {
            foreach (var itemValue in combo.Items)
            {
                if (itemValue is ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), value.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }

        private void Selection_Changed(object sender, SelectionChangedEventArgs e)
        {
            RefreshLocations(CmbLocation?.SelectedItem is LocationOption current
                ? current.Value
                : Settings.Location);
            RefreshUi();
        }

        private void RefreshLocations(ThreePhaseFaultLocation preferred)
        {
            if (CmbLocation == null) return;
            bool star = _analysis.ThreePhaseConnection == ThreePhaseLoadConnection.Star;
            var values = new List<LocationOption>
            {
                new() { Value = ThreePhaseFaultLocation.LineA, Text = star ? "Фазная ветвь AN" : LineText("A") },
                new() { Value = ThreePhaseFaultLocation.LineB, Text = star ? "Фазная ветвь BN" : LineText("B") },
                new() { Value = ThreePhaseFaultLocation.LineC, Text = star ? "Фазная ветвь CN" : LineText("C") }
            };
            if (!star)
            {
                values.Add(new() { Value = ThreePhaseFaultLocation.BranchAB, Text = "Ветвь нагрузки AB" });
                values.Add(new() { Value = ThreePhaseFaultLocation.BranchBC, Text = "Ветвь нагрузки BC" });
                values.Add(new() { Value = ThreePhaseFaultLocation.BranchCA, Text = "Ветвь нагрузки CA" });
            }

            CmbLocation.ItemsSource = values;
            CmbLocation.SelectedItem = values.Find(option => option.Value == preferred) ?? values[0];

            string LineText(string phase) => SelectedMode() == ThreePhaseOperatingMode.ShortCircuit
                ? $"Фаза {phase}–N источника"
                : $"Линейный провод {phase}";
        }

        private void RefreshUi()
        {
            if (PanelShortCircuit == null) return;
            var mode = SelectedMode();
            CmbLocation.IsEnabled = mode != ThreePhaseOperatingMode.Normal;
            PanelShortCircuit.Visibility = mode == ThreePhaseOperatingMode.ShortCircuit
                ? Visibility.Visible
                : Visibility.Collapsed;
            TxtHint.Text = mode switch
            {
                ThreePhaseOperatingMode.Normal => "Будет рассчитан установившийся симметричный источник с заданной нагрузкой.",
                ThreePhaseOperatingMode.OpenCircuit => "В отчёт войдут исходный нормальный режим и режим после обрыва, включая напряжение на месте разрыва.",
                _ => "В отчёт войдут исходный нормальный режим и режим КЗ. Положительное Rк ограничивает ток идеального источника и моделирует сопротивление дуги, линии и контактов."
            };
        }

        private void BtnCalculate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                double resistance = Parse(TxtFaultResistance.Text);
                Settings = new ThreePhaseFaultSettings
                {
                    OperatingMode = SelectedMode(),
                    Location = CmbLocation.SelectedItem is LocationOption option
                        ? option.Value
                        : ThreePhaseFaultLocation.LineA,
                    ShortCircuitResistanceOhms = resistance
                };
                Settings.Validate(_analysis);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Некорректный аварийный режим",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static double Parse(string text)
        {
            if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
                throw new InvalidOperationException("Введите корректное конечное сопротивление места КЗ.");
            return value;
        }

    }
}
