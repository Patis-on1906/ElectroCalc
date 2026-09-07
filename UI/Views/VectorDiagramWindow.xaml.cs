using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ElectroCalc.Core.Models;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace ElectroCalc.UI.Views
{
    public partial class VectorDiagramWindow : Window
    {
        private static readonly OxyColor[] Palette =
        {
            OxyColor.FromRgb(21, 101, 192),
            OxyColor.FromRgb(198, 40, 40),
            OxyColor.FromRgb(46, 125, 50),
            OxyColor.FromRgb(123, 31, 162),
            OxyColor.FromRgb(239, 108, 0),
            OxyColor.FromRgb(0, 121, 107),
            OxyColor.FromRgb(69, 90, 100),
            OxyColor.FromRgb(173, 20, 87)
        };

        private readonly IReadOnlyList<PhasorDiagramData> _diagrams;

        public VectorDiagramWindow(CalculationResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (result.Method != CalculationMethod.VectorData || !result.Success)
                throw new ArgumentException("Окно диаграмм требует успешный результат режима VectorData.", nameof(result));
            if (result.VectorDiagrams.Count == 0)
                throw new ArgumentException("Результат не содержит данных для построения диаграмм.", nameof(result));

            InitializeComponent();
            _diagrams = result.VectorDiagrams;
            CmbDiagram.ItemsSource = _diagrams;
            CmbDiagram.SelectedIndex = 0;
        }

        private void CmbDiagram_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbDiagram.SelectedItem is PhasorDiagramData diagram)
                ShowDiagram(diagram);
        }

        private void BtnResetZoom_Click(object sender, RoutedEventArgs e)
        {
            if (CmbDiagram.SelectedItem is PhasorDiagramData diagram)
                ShowDiagram(diagram);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void ShowDiagram(PhasorDiagramData diagram)
        {
            DiagramPlot.Model = CreatePlotModel(diagram);
            TxtDescription.Text = diagram.Description;
            GridVectors.ItemsSource = diagram.Vectors.Select(vector => new VectorRow(vector, diagram.Unit)).ToList();

            if (!diagram.HasExpectedResultant)
            {
                TxtClosure.Text = diagram.HeadToTail
                    ? $"Σ = {FormatComplex(diagram.ActualResultant)} {diagram.Unit}"
                    : $"Показано векторов: {diagram.Vectors.Count}";
                TxtClosure.Foreground = new SolidColorBrush(Color.FromRgb(33, 33, 33));
                return;
            }

            double scale = Math.Max(1.0, diagram.Vectors.Sum(vector => vector.Value.Magnitude));
            bool closed = diagram.ClosureError <= 1e-6 * scale;
            TxtClosure.Text =
                $"{diagram.ExpectedResultantLabel}: расчетная сумма {FormatComplex(diagram.ActualResultant)} {diagram.Unit}; " +
                $"невязка {diagram.ClosureError:E2} {diagram.Unit} {(closed ? "✓" : "⚠")}";
            TxtClosure.Foreground = new SolidColorBrush(closed
                ? Color.FromRgb(46, 125, 50)
                : Color.FromRgb(198, 40, 40));
        }

        internal static PlotModel CreatePlotModel(PhasorDiagramData diagram)
        {
            var model = new PlotModel
            {
                Title = diagram.Title,
                Subtitle = diagram.HeadToTail
                    ? "Векторы сложены последовательно (голова к хвосту)"
                    : "Фазоры показаны из начала координат",
                PlotType = PlotType.Cartesian,
                PlotAreaBorderColor = OxyColor.FromRgb(176, 190, 197),
                Background = OxyColors.White
            };

            var points = new List<Complex> { Complex.Zero };
            Complex cursor = Complex.Zero;
            var path = new LineSeries
            {
                Color = OxyColor.FromRgb(176, 190, 197),
                LineStyle = LineStyle.Dot,
                StrokeThickness = 1,
                MarkerType = MarkerType.Circle,
                MarkerSize = 2.5,
                MarkerFill = OxyColor.FromRgb(120, 144, 156)
            };
            path.Points.Add(new DataPoint(0, 0));

            for (int i = 0; i < diagram.Vectors.Count; i++)
            {
                var vector = diagram.Vectors[i];
                Complex start = diagram.HeadToTail ? cursor : Complex.Zero;
                Complex end = start + vector.Value;
                if (diagram.HeadToTail) cursor = end;
                points.Add(start);
                points.Add(end);

                if (vector.Value.Magnitude > 1e-14)
                {
                    model.Annotations.Add(new ArrowAnnotation
                    {
                        StartPoint = Point(start),
                        EndPoint = Point(end),
                        Color = Palette[i % Palette.Length],
                        TextColor = Palette[i % Palette.Length],
                        Text = vector.Label,
                        StrokeThickness = 2,
                        HeadLength = 5,
                        HeadWidth = 2.5
                    });
                }

                if (diagram.HeadToTail)
                    path.Points.Add(Point(end));
            }

            if (diagram.HeadToTail)
                model.Series.Add(path);

            if (diagram.HasExpectedResultant && diagram.ExpectedResultant.Magnitude > 1e-14)
            {
                points.Add(diagram.ExpectedResultant);
                model.Annotations.Add(new ArrowAnnotation
                {
                    StartPoint = new DataPoint(0, 0),
                    EndPoint = Point(diagram.ExpectedResultant),
                    Color = OxyColors.Black,
                    TextColor = OxyColors.Black,
                    Text = diagram.ExpectedResultantLabel,
                    LineStyle = LineStyle.Dash,
                    StrokeThickness = 1.5,
                    HeadLength = 5,
                    HeadWidth = 2.5
                });
            }

            double extent = Math.Max(1e-6, points.Max(point => Math.Max(Math.Abs(point.Real), Math.Abs(point.Imaginary))));
            double limit = extent * 1.25;
            model.Axes.Add(CreateAxis(AxisPosition.Bottom, $"Re, {diagram.Unit}", -limit, limit));
            model.Axes.Add(CreateAxis(AxisPosition.Left, $"Im, {diagram.Unit}", -limit, limit));
            return model;
        }

        private static LinearAxis CreateAxis(AxisPosition position, string title, double minimum, double maximum) => new()
        {
            Position = position,
            Title = title,
            Minimum = minimum,
            Maximum = maximum,
            PositionAtZeroCrossing = true,
            AxislineStyle = LineStyle.Solid,
            AxislineColor = OxyColor.FromRgb(84, 110, 122),
            MajorGridlineStyle = LineStyle.Dot,
            MinorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = OxyColor.FromArgb(90, 176, 190, 197),
            MinorGridlineColor = OxyColor.FromArgb(45, 176, 190, 197),
            MinimumPadding = 0,
            MaximumPadding = 0
        };

        private static DataPoint Point(Complex value) => new(value.Real, value.Imaginary);

        private static string FormatComplex(Complex value) =>
            $"{value.Real:0.######} {(value.Imaginary < 0 ? "−" : "+")} j{Math.Abs(value.Imaginary):0.######}";

        private sealed class VectorRow
        {
            public string Label { get; }
            public string Rectangular { get; }
            public string Magnitude { get; }
            public string Phase { get; }

            public VectorRow(PhasorDiagramVector vector, string unit)
            {
                Label = vector.Label;
                Rectangular = $"{FormatComplex(vector.Value)} {unit}";
                Magnitude = $"{vector.Value.Magnitude:0.######}";
                Phase = $"{Math.Atan2(vector.Value.Imaginary, vector.Value.Real) * 180 / Math.PI:0.##}°";
            }
        }
    }
}
