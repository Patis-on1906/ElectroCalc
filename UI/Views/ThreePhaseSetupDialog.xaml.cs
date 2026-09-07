using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ElectroCalc.Core.Models;

namespace ElectroCalc.UI.Views
{
    public partial class ThreePhaseSetupDialog : Window
    {
        public ThreePhaseCircuitDefinition? Definition { get; private set; }

        public ThreePhaseSetupDialog(ThreePhaseCircuitInput initial)
        {
            InitializeComponent();
            SelectMode(initial.Mode);
            TxtFrequency.Text = Format(initial.FrequencyHz);
            TxtEmf.Text = Format(initial.PhaseEmfRms);
            TxtRA.Text = Format(initial.BranchA.ResistanceOhms);
            TxtXA.Text = Format(initial.BranchA.ReactanceOhms);
            TxtRB.Text = Format(initial.BranchB.ResistanceOhms);
            TxtXB.Text = Format(initial.BranchB.ReactanceOhms);
            TxtRC.Text = Format(initial.BranchC.ResistanceOhms);
            TxtXC.Text = Format(initial.BranchC.ReactanceOhms);
            UpdateBranchLabels();
        }

        private void BtnBuild_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var input = new ThreePhaseCircuitInput
                {
                    Mode = SelectedMode(),
                    FrequencyHz = Parse(TxtFrequency.Text, "частоту"),
                    PhaseEmfRms = Parse(TxtEmf.Text, "фазную ЭДС"),
                    BranchA = new ThreePhaseBranchInput
                    {
                        ResistanceOhms = Parse(TxtRA.Text, "R ветви A"),
                        ReactanceOhms = Parse(TxtXA.Text, "X ветви A")
                    },
                    BranchB = new ThreePhaseBranchInput
                    {
                        ResistanceOhms = Parse(TxtRB.Text, "R ветви B"),
                        ReactanceOhms = Parse(TxtXB.Text, "X ветви B")
                    },
                    BranchC = new ThreePhaseBranchInput
                    {
                        ResistanceOhms = Parse(TxtRC.Text, "R ветви C"),
                        ReactanceOhms = Parse(TxtXC.Text, "X ветви C")
                    }
                };

                Definition = ThreePhaseCircuitFactory.Create(input);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Некорректные параметры 3Φ-схемы",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CmbMode_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            UpdateBranchLabels();

        private void UpdateBranchLabels()
        {
            if (LblBranchA == null) return;
            bool delta = SelectedMode() == ThreePhaseCircuitMode.Delta;
            LblBranchA.Text = delta ? "AB" : "AN";
            LblBranchB.Text = delta ? "BC" : "BN";
            LblBranchC.Text = delta ? "CA" : "CN";
        }

        private ThreePhaseCircuitMode SelectedMode()
        {
            if (CmbMode?.SelectedItem is ComboBoxItem item &&
                Enum.TryParse(item.Tag?.ToString(), out ThreePhaseCircuitMode mode))
                return mode;
            return ThreePhaseCircuitMode.StarWithNeutral;
        }

        private void SelectMode(ThreePhaseCircuitMode mode)
        {
            foreach (var value in CmbMode.Items)
            {
                if (value is ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    CmbMode.SelectedItem = item;
                    return;
                }
            }
            CmbMode.SelectedIndex = 0;
        }

        private static double Parse(string text, string field)
        {
            if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
                throw new InvalidOperationException($"Введите корректное конечное число в поле «{field}».");
            return value;
        }

        private static string Format(double value) => value.ToString("G", CultureInfo.InvariantCulture);
    }
}
