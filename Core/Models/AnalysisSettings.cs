using System;
using System.Globalization;
using System.Numerics;

namespace ElectroCalc.Core.Models
{
    public enum CircuitAnalysisMode
    {
        DC,
        AC,
        ThreePhase
    }

    public enum ThreePhaseLoadConnection
    {
        Star,
        Delta
    }

    public enum ThreePhasePhase
    {
        None,
        A,
        B,
        C
    }

    /// <summary>
    /// Настройки электрического режима. Не привязаны к UI и передаются в
    /// расчётную модель/решатели, поэтому новые режимы можно добавлять без
    /// изменения модели холста.
    /// </summary>
    public sealed class CircuitAnalysisSettings
    {
        public CircuitAnalysisMode Mode { get; set; } = CircuitAnalysisMode.DC;
        public double FrequencyHz { get; set; } = 50.0;
        public ThreePhaseLoadConnection ThreePhaseConnection { get; set; } = ThreePhaseLoadConnection.Star;
        public bool ThreePhaseHasNeutral { get; set; } = true;

        /// <summary>Для AC и 3Φ используется одна и та же фазорная RMS-модель.</summary>
        public bool IsPhasorMode => Mode != CircuitAnalysisMode.DC;

        public double AngularFrequency => IsPhasorMode
            ? 2.0 * Math.PI * FrequencyHz
            : 0.0;

        public CircuitAnalysisSettings Clone() => new()
        {
            Mode = Mode,
            FrequencyHz = FrequencyHz,
            ThreePhaseConnection = ThreePhaseConnection,
            ThreePhaseHasNeutral = ThreePhaseHasNeutral
        };

        public void Validate()
        {
            if (double.IsNaN(FrequencyHz) || double.IsInfinity(FrequencyHz))
                throw new InvalidOperationException("Частота должна быть конечным числом.");
            if (IsPhasorMode && FrequencyHz <= 0)
                throw new InvalidOperationException("Для синусоидального режима частота должна быть больше 0 Гц.");
            if (Mode == CircuitAnalysisMode.ThreePhase &&
                ThreePhaseConnection == ThreePhaseLoadConnection.Delta)
                ThreePhaseHasNeutral = false; // в треугольнике нейтраль нагрузки отсутствует по определению
        }

        public override string ToString() => Mode switch
        {
            CircuitAnalysisMode.DC => "DC (постоянный ток)",
            CircuitAnalysisMode.AC => $"AC, f={FrequencyHz:0.######} Гц",
            CircuitAnalysisMode.ThreePhase => $"3Φ, f={FrequencyHz:0.######} Гц, " +
                $"{(ThreePhaseConnection == ThreePhaseLoadConnection.Star ? "звезда" : "треугольник")}" +
                (ThreePhaseConnection == ThreePhaseLoadConnection.Star
                    ? (ThreePhaseHasNeutral ? ", N подключён" : ", без N")
                    : string.Empty),
            _ => Mode.ToString()
        };
    }

    /// <summary>Общие операции с действующими комплексными значениями (RMS).</summary>
    public static class Phasor
    {
        private const double Eps = 1e-12;

        public static Complex FromMagnitudePhase(double rms, double phaseDegrees) =>
            Complex.FromPolarCoordinates(rms, phaseDegrees * Math.PI / 180.0);

        public static double PhaseDegrees(Complex value) =>
            value.Magnitude < Eps ? 0.0 : value.Phase * 180.0 / Math.PI;

        public static bool NearlyZero(Complex value, double eps = Eps) => value.Magnitude <= eps;

        public static bool NearlyEqual(Complex a, Complex b, double eps = 1e-9)
        {
            double scale = Math.Max(1.0, Math.Max(a.Magnitude, b.Magnitude));
            return (a - b).Magnitude <= eps * scale;
        }

        public static string Rectangular(Complex value, string format = "0.######")
        {
            double re = Math.Abs(value.Real) < Eps ? 0.0 : value.Real;
            double im = Math.Abs(value.Imaginary) < Eps ? 0.0 : value.Imaginary;
            if (Math.Abs(im) < Eps)
                return re.ToString(format, CultureInfo.InvariantCulture);
            if (Math.Abs(re) < Eps)
                return $"{im.ToString(format, CultureInfo.InvariantCulture)}j";
            string sign = im >= 0 ? "+" : "−";
            return $"{re.ToString(format, CultureInfo.InvariantCulture)} {sign} j{Math.Abs(im).ToString(format, CultureInfo.InvariantCulture)}";
        }

        public static string Polar(Complex value, string magnitudeFormat = "0.######", string phaseFormat = "0.##") =>
            $"{value.Magnitude.ToString(magnitudeFormat, CultureInfo.InvariantCulture)}∠{PhaseDegrees(value).ToString(phaseFormat, CultureInfo.InvariantCulture)}°";

        public static string Exponential(Complex value, string magnitudeFormat = "0.######", string phaseFormat = "0.##") =>
            $"{value.Magnitude.ToString(magnitudeFormat, CultureInfo.InvariantCulture)}·e^(j{PhaseDegrees(value).ToString(phaseFormat, CultureInfo.InvariantCulture)}°)";

        public static string Format(Complex value, CircuitAnalysisMode mode, string unit = "", int decimals = 6)
        {
            string suffix = string.IsNullOrWhiteSpace(unit) ? string.Empty : $" {unit}";
            if (mode == CircuitAnalysisMode.DC)
                return $"{value.Real.ToString($"F{decimals}", CultureInfo.InvariantCulture)}{suffix}";
            string numericFormat = "0." + new string('#', decimals);
            return $"{Rectangular(value, numericFormat)}{suffix} = {Polar(value, numericFormat, "0.##")}{suffix}";
        }

        public static string Compact(Complex value, CircuitAnalysisMode mode, string unit = "")
        {
            string suffix = string.IsNullOrWhiteSpace(unit) ? string.Empty : $" {unit}";
            return mode == CircuitAnalysisMode.DC
                ? $"{value.Real:0.####}{suffix}"
                : $"{Polar(value, "0.####", "0.##")}{suffix}";
        }
    }
}
