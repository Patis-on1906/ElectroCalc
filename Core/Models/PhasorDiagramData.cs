using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ElectroCalc.Core.Models
{
    /// <summary>Один подписанный RMS-фазор для графической векторной диаграммы.</summary>
    public sealed class PhasorDiagramVector
    {
        public string Label { get; set; } = string.Empty;
        public Complex Value { get; set; } = Complex.Zero;
    }

    /// <summary>
    /// Структурированный набор фазоров. При HeadToTail=false все векторы
    /// начинаются в начале координат; при true каждый следующий начинается
    /// в конце предыдущего (геометрическая сумма).
    /// </summary>
    public sealed class PhasorDiagramData
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public bool HeadToTail { get; set; }
        public bool HasExpectedResultant { get; set; }
        public Complex ExpectedResultant { get; set; } = Complex.Zero;
        public string ExpectedResultantLabel { get; set; } = "Σ";
        public List<PhasorDiagramVector> Vectors { get; } = new();

        public Complex ActualResultant =>
            Vectors.Aggregate(Complex.Zero, (sum, vector) => sum + vector.Value);

        public double ClosureError => HasExpectedResultant
            ? (ActualResultant - ExpectedResultant).Magnitude
            : 0.0;
    }
}
