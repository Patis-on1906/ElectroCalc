using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Комплексный метод Гаусса с частичным выбором главного элемента.
    /// Используется AC-решателями; действительная система является его частным случаем.
    /// </summary>
    public static class ComplexLinearSystemSolver
    {
        public static Complex[] Solve(
            Complex[,] a,
            Complex[] b,
            string[] variableNames,
            List<SolutionStep>? steps = null,
            bool logIntermediateSteps = false)
        {
            int n = b.Length;
            if (a.GetLength(0) != n || a.GetLength(1) != n)
                throw new ArgumentException("Матрица A должна быть квадратной и согласованной с вектором b.");
            if (variableNames.Length != n)
                throw new ArgumentException("Количество имён переменных не совпадает с размером системы.");

            var aug = new Complex[n, n + 1];
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++) aug[r, c] = a[r, c];
                aug[r, n] = b[r];
            }

            if (logIntermediateSteps && steps != null)
                steps.Add(new SolutionStep { Title = "Исходная комплексная система", MatrixText = FormatAugmented(aug, n, variableNames) });

            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                double max = aug[col, col].Magnitude;
                for (int row = col + 1; row < n; row++)
                {
                    double v = aug[row, col].Magnitude;
                    if (v > max) { max = v; pivot = row; }
                }

                if (max < 1e-12)
                    throw new InvalidOperationException(
                        "Комплексная система вырождена. Проверьте связность схемы и совместимость идеальных источников.");

                if (pivot != col)
                    for (int k = col; k <= n; k++)
                        (aug[col, k], aug[pivot, k]) = (aug[pivot, k], aug[col, k]);

                for (int row = col + 1; row < n; row++)
                {
                    Complex factor = aug[row, col] / aug[col, col];
                    if (factor.Magnitude < 1e-15) continue;
                    for (int k = col; k <= n; k++)
                        aug[row, k] -= factor * aug[col, k];
                }
            }

            var x = new Complex[n];
            for (int row = n - 1; row >= 0; row--)
            {
                Complex sum = aug[row, n];
                for (int k = row + 1; k < n; k++) sum -= aug[row, k] * x[k];
                x[row] = sum / aug[row, row];
            }
            return x;
        }

        public static string FormatAugmented(Complex[,] aug, int n, string[] vars)
        {
            var sb = new StringBuilder();
            for (int r = 0; r < n; r++)
            {
                var terms = new List<string>();
                for (int c = 0; c < n; c++)
                {
                    Complex coef = aug[r, c];
                    if (coef.Magnitude <= 1e-14) continue;
                    terms.Add($"({Phasor.Rectangular(coef)})·{vars[c]}");
                }
                sb.AppendLine($"  {(terms.Count == 0 ? "0" : string.Join(" + ", terms))} = {Phasor.Rectangular(aug[r, n])}");
            }
            return sb.ToString();
        }
    }
}
