using System.Windows;
using System.Windows.Controls;
using ElectroCalc.Core.Models;

namespace ElectroCalc.UI.Views
{
    public partial class ElementPickerDialog : Window
    {
        // null means "Junction" (wire node, no element)
        public ElementType? SelectedType { get; private set; } = ElementType.Resistor;
        public string  ElementName  { get; private set; } = "R1";
        public double  ElementValue { get; private set; } = 100;
        public double  InternalR    { get; private set; } = 0;
        public bool    Polarity     { get; private set; } = true;
        public double  PhaseDegrees { get; private set; } = 0.0;

        private readonly CircuitAnalysisMode _analysisMode;

        public ElementPickerDialog(CircuitAnalysisMode analysisMode = CircuitAnalysisMode.DC)
        {
            _analysisMode = analysisMode;
            InitializeComponent();
            UpdateModeHints();
        }

        private void UpdateModeHints()
        {
            if (PanelPhase != null)
                PanelPhase.Visibility = _analysisMode != CircuitAnalysisMode.DC ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CmbType_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (CmbType.SelectedItem is not ComboBoxItem item) return;
            var tag = item.Tag?.ToString();

            bool isJunction = tag == "Junction";
            bool isSource   = tag is "VoltageSource" or "CurrentSource";

            if (PanelFields     != null) PanelFields.Visibility     = isJunction ? Visibility.Collapsed : Visibility.Visible;
            if (TxtJunctionHint != null) TxtJunctionHint.Visibility  = isJunction ? Visibility.Visible  : Visibility.Collapsed;
            if (PanelSource     != null) PanelSource.Visibility      = isSource   ? Visibility.Visible  : Visibility.Collapsed;
            if (PanelPhase      != null) PanelPhase.Visibility       = isSource && _analysisMode != CircuitAnalysisMode.DC ? Visibility.Visible : Visibility.Collapsed;
            if (TxtValueLabel   != null) TxtValueLabel.Text          = isSource && _analysisMode != CircuitAnalysisMode.DC ? "Действующее значение (RMS):" : "Значение:";
            if (ChkPolarity != null)
            {
                ChkPolarity.Content = tag == "CurrentSource"
                    ? $"Направление тока: {(ChkPolarity.IsChecked == true ? "A → B" : "B → A")}"
                    : $"Положительный вывод: порт {(ChkPolarity.IsChecked == true ? "A" : "B")}";
                ChkPolarity.ToolTip = tag == "CurrentSource"
                    ? "Направление тока задаётся относительно портов и поворачивается вместе с элементом."
                    : "Полярность ЭДС задаётся относительно портов и поворачивается вместе с элементом.";
            }

            if (TxtUnit != null)
                TxtUnit.Text = tag switch
                {
                    "Resistor"      => "Ом",
                    "VoltageSource" => "В",
                    "CurrentSource" => "А",
                    "Capacitor"     => "Ф",
                    "Inductor"      => "Гн",
                    _ => ""
                };

            // Auto-name
            if (TxtName != null)
                TxtName.Text = tag switch
                {
                    "Resistor"      => "R1",
                    "VoltageSource" => "E1",
                    "CurrentSource" => "J1",
                    "Capacitor"     => "C1",
                    "Inductor"      => "L1",
                    _ => ""
                };
        }

        private void ChkPolarity_Changed(object s, RoutedEventArgs e)
        {
            if (CmbType?.SelectedItem is not ComboBoxItem item || ChkPolarity == null) return;
            string? tag = item.Tag?.ToString();
            ChkPolarity.Content = tag == "CurrentSource"
                ? $"Направление тока: {(ChkPolarity.IsChecked == true ? "A → B" : "B → A")}"
                : $"Положительный вывод: порт {(ChkPolarity.IsChecked == true ? "A" : "B")}";
        }

        private void BtnAdd_Click(object s, RoutedEventArgs e)
        {
            var tag = (CmbType.SelectedItem as ComboBoxItem)?.Tag?.ToString();

            if (tag == "Junction")
            {
                SelectedType = null;
                DialogResult = true;
                return;
            }

            SelectedType = tag switch
            {
                "VoltageSource" => ElementType.VoltageSource,
                "CurrentSource" => ElementType.CurrentSource,
                "Capacitor"     => ElementType.Capacitor,
                "Inductor"      => ElementType.Inductor,
                _ => ElementType.Resistor
            };

            ElementName = TxtName.Text.Trim();
            if (string.IsNullOrWhiteSpace(ElementName))
            {
                MessageBox.Show("Введите имя элемента.", "Некорректные данные",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParseNumber(TxtValue.Text, out double v))
            {
                MessageBox.Show("Введите корректное числовое значение элемента.", "Некорректные данные",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParseNumber(TxtInternalR.Text, out double r) || r < 0)
            {
                MessageBox.Show("Внутреннее сопротивление должно быть неотрицательным числом.", "Некорректные данные",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if ((SelectedType is ElementType.Resistor or ElementType.Capacitor or ElementType.Inductor) && v < 0)
            {
                MessageBox.Show("Параметр пассивного элемента не может быть отрицательным.", "Некорректные данные",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_analysisMode != CircuitAnalysisMode.DC &&
                SelectedType is (ElementType.VoltageSource or ElementType.CurrentSource) && v < 0)
            {
                MessageBox.Show("Действующее значение (RMS) источника не может быть отрицательным.", "Некорректные данные",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double phase = 0.0;
            if (_analysisMode != CircuitAnalysisMode.DC && SelectedType is (ElementType.VoltageSource or ElementType.CurrentSource))
            {
                if (!TryParseNumber(TxtPhase.Text, out phase))
                {
                    MessageBox.Show("Фаза источника должна быть корректным числом в градусах.", "Некорректные данные",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            ElementValue = v;
            InternalR = r;
            Polarity = ChkPolarity.IsChecked == true;
            PhaseDegrees = phase;
            DialogResult = true;
        }

        private static bool TryParseNumber(string text, out double value) =>
            double.TryParse(
                text.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value) && !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
