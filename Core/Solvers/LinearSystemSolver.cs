using System;
using System.Collections.Generic;
using System.Text;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Solvers
{
    /// <summary>
    /// Метод Гаусса с частичным выбором главного элемента и пошаговым логом.
    /// </summary>
    public static class LinearSystemSolver
    {
        public static double[] Solve(
            double[,] A,
            double[] b,
            string[] variableNames,
            List<SolutionStep> steps,
            string[]? variableUnits = null)
        {
            int n = b.Length;
            if (A.GetLength(0) != n || A.GetLength(1) != n)
                throw new ArgumentException("Матрица A должна быть квадратной и согласованной с вектором b.");
            if (variableNames.Length != n)
                throw new ArgumentException("Количество имён переменных не совпадает с размером системы.");
            if (variableUnits != null && variableUnits.Length != n)
                throw new ArgumentException("Количество единиц измерения не совпадает с размером системы.");

            double[,] aug = BuildAugmented(A, b, n);

            steps.Add(new SolutionStep
            {
                Title = "Исходная система уравнений",
                Description = "Расширенная матрица [A|b]:",
                MatrixText = FormatAugmented(aug, n, variableNames)
            });

            for (int col = 0; col < n; col++)
            {
                int maxRow = col;
                double maxVal = Math.Abs(aug[col, col]);
                for (int row = col + 1; row < n; row++)
                {
                    double v = Math.Abs(aug[row, col]);
                    if (v > maxVal)
                    {
                        maxVal = v;
                        maxRow = row;
                    }
                }

                if (maxVal < 1e-12)
                    throw new InvalidOperationException(
                        "Система вырождена. Проверьте связность схемы, короткие замыкания идеальных источников " +
                        "и совместимость заданных источников тока/напряжения.");

                if (maxRow != col)
                {
                    SwapRows(aug, col, maxRow, n);
                    steps.Add(new SolutionStep
                    {
                        Title = $"Перестановка строк {col + 1} и {maxRow + 1}",
                        Description = "Частичный выбор главного элемента:",
                        MatrixText = FormatAugmented(aug, n, variableNames)
                    });
                }

                for (int row = col + 1; row < n; row++)
                {
                    double factor = aug[row, col] / aug[col, col];
                    if (Math.Abs(factor) < 1e-15) continue;
                    for (int k = col; k <= n; k++)
                        aug[row, k] -= factor * aug[col, k];
                }
            }

            steps.Add(new SolutionStep
            {
                Title = "Верхнетреугольная форма",
                Description = "После прямого хода метода Гаусса:",
                MatrixText = FormatAugmented(aug, n, variableNames)
            });

            double[] x = new double[n];
            var backSb = new StringBuilder();
            for (int row = n - 1; row >= 0; row--)
            {
                double sum = aug[row, n];
                for (int k = row + 1; k < n; k++)
                    sum -= aug[row, k] * x[k];
                x[row] = sum / aug[row, row];

                string unit = variableUnits == null || string.IsNullOrWhiteSpace(variableUnits[row])
                    ? string.Empty
                    : $" {variableUnits[row]}";
                backSb.AppendLine($"  {variableNames[row]} = {x[row]:F6}{unit}");
            }

            steps.Add(new SolutionStep
            {
                Title = "Обратный ход — решение",
                Description = "Найденные переменные:",
                MatrixText = backSb.ToString()
            });

            return x;
        }

        private static double[,] BuildAugmented(double[,] A, double[] b, int n)
        {
            var aug = new double[n, n + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) aug[i, j] = A[i, j];
                aug[i, n] = b[i];
            }
            return aug;
        }

        private static void SwapRows(double[,] m, int r1, int r2, int n)
        {
            for (int k = 0; k <= n; k++)
                (m[r1, k], m[r2, k]) = (m[r2, k], m[r1, k]);
        }

        public static string FormatAugmented(double[,] aug, int n, string[] vars)
        {
            var sb = new StringBuilder();
            sb.Append("    │");
            foreach (var v in vars) sb.Append($" {v,10}");
            sb.AppendLine($" │ {"b",10}");
            sb.AppendLine(new string('─', 6 + (n + 1) * 11));

            for (int i = 0; i < n; i++)
            {
                sb.Append($" {i + 1,2} │");
                for (int j = 0; j < n; j++)
                    sb.Append($" {aug[i, j],10:F4}");
                sb.AppendLine($" │ {aug[i, n],10:F4}");
            }
            return sb.ToString();
        }
    }
}
