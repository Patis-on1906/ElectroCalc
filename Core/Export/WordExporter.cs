using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Export
{
    /// <summary>
    /// Exports a CalculationResult to a .docx file.
    /// </summary>
    public static class WordExporter
    {
        public static void Export(CalculationResult result, string filePath)
        {
            using var doc = WordprocessingDocument.Create(
                filePath, WordprocessingDocumentType.Document);

            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            AddStyles(mainPart);

            // Title
            body.AppendChild(Heading(result.Analysis.Mode switch
            {
                CircuitAnalysisMode.ThreePhase => $"Расчёт трёхфазной цепи, f={result.Analysis.FrequencyHz:0.######} Гц",
                CircuitAnalysisMode.AC => $"Расчёт цепи синусоидального тока, f={result.Analysis.FrequencyHz:0.######} Гц",
                _ => "Расчёт цепи постоянного тока"
            }, "Title"));
            body.AppendChild(Heading(MethodName(result.Method), "Heading1"));
            body.AppendChild(NormalPara($"Дата: {DateTime.Now:dd.MM.yyyy HH:mm}"));
            body.AppendChild(HorizontalLine());

            // Steps
            foreach (var step in result.Steps)
            {
                body.AppendChild(Heading(step.Title, "Heading2"));
                body.AppendChild(NormalPara(step.Description));

                if (!string.IsNullOrWhiteSpace(step.Formula))
                    body.AppendChild(MonoPara(step.Formula));

                if (!string.IsNullOrWhiteSpace(step.MatrixText))
                    body.AppendChild(MonoPara(step.MatrixText));
            }

            if (result.Method != CalculationMethod.KirchhoffLaws && result.Method != CalculationMethod.PotentialDiagram)
            {
                // Results table
                body.AppendChild(Heading("Результаты по ветвям", "Heading1"));
                body.AppendChild(BuildBranchTable(result));

                if (result.PowerBalanceAvailable)
                {
                    var sg = result.TotalComplexPowerGenerated;
                    var sc = result.TotalComplexPowerConsumed;
                    body.AppendChild(Heading("Баланс мощностей", "Heading1"));
                    body.AppendChild(NormalPara($"ΣS_ист = {Phasor.Rectangular(sg)} ВА; ΣP_ист={sg.Real:F4} Вт; ΣQ_ист={sg.Imaginary:F4} вар; |ΣS_ист|={sg.Magnitude:F4} ВА"));
                    body.AppendChild(NormalPara($"ΣS_потр = {Phasor.Rectangular(sc)} ВА; ΣP_потр={sc.Real:F4} Вт; ΣQ_потр={sc.Imaginary:F4} вар; |ΣS_потр|={sc.Magnitude:F4} ВА"));
                    body.AppendChild(NormalPara($"Невязка |ΔS| = {result.PowerImbalance:E2} ВА"));
                    body.AppendChild(NormalPara(result.PowerBalanceOk
                        ? "✓ Баланс мощностей сошёлся"
                        : "✗ Баланс не сошёлся — проверьте схему"));
                }
            }

            mainPart.Document.Save();
        }

        // ── Paragraph builders ────────────────────────────────────────────────

        private static Paragraph Heading(string text, string style)
        {
            var p = new Paragraph();
            var pPr = new ParagraphProperties(new ParagraphStyleId { Val = style });
            p.AppendChild(pPr);
            p.AppendChild(new Run(new Text(text)));
            return p;
        }

        private static Paragraph NormalPara(string text)
        {
            var p = new Paragraph();
            foreach (var line in text.Split('\n'))
            {
                if (p.ChildElements.OfType<Run>().Any())
                    p.AppendChild(new Run(new Break()));
                p.AppendChild(new Run(new Text(line.TrimEnd('\r')) { Space = SpaceProcessingModeValues.Preserve }));
            }
            return p;
        }

        private static Paragraph MonoPara(string text)
        {
            var p  = new Paragraph();
            var pPr = new ParagraphProperties(new ParagraphStyleId { Val = "Normal" });
            p.AppendChild(pPr);

            foreach (var line in text.Split('\n'))
            {
                if (p.ChildElements.OfType<Run>().Any())
                    p.AppendChild(new Run(new Break()));
                var run = new Run();
                run.AppendChild(new RunProperties(new RunFonts { Ascii = "Courier New" },
                                                  new FontSize { Val = "18" }));
                run.AppendChild(new Text(line.TrimEnd('\r'))
                    { Space = SpaceProcessingModeValues.Preserve });
                p.AppendChild(run);
            }
            return p;
        }

        private static Paragraph HorizontalLine()
        {
            var p  = new Paragraph();
            var pPr = new ParagraphProperties();
            pPr.AppendChild(new ParagraphBorders(
                new BottomBorder { Val = BorderValues.Single, Size = 6 }));
            p.AppendChild(pPr);
            return p;
        }

        // ── Branch results table ──────────────────────────────────────────────

        private static Table BuildBranchTable(CalculationResult result)
        {
            var table = new Table();
            var tblPr = new TableProperties(
                new TableBorders(
                    new TopBorder    { Val = BorderValues.Single, Size = 4 },
                    new BottomBorder { Val = BorderValues.Single, Size = 4 },
                    new LeftBorder   { Val = BorderValues.Single, Size = 4 },
                    new RightBorder  { Val = BorderValues.Single, Size = 4 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideVerticalBorder   { Val = BorderValues.Single, Size = 4 }));
            table.AppendChild(tblPr);

            if (result.Analysis.Mode != CircuitAnalysisMode.DC)
            {
                table.AppendChild(TableRow(true, "Ветвь", "I (RMS-фазор), А", "U (RMS-фазор), В", "P, Вт", "Q, вар", "|S|, ВА"));
                foreach (var r in result.BranchResults)
                    table.AppendChild(TableRow(false,
                        r.Branch.ToString(),
                        $"{Phasor.Rectangular(r.CurrentPhasor)} = {Phasor.Exponential(r.CurrentPhasor)}",
                        $"{Phasor.Rectangular(r.VoltagePhasor)} = {Phasor.Exponential(r.VoltagePhasor)}",
                        $"{r.ActivePower:F4}", $"{r.ReactivePower:F4}", $"{r.ApparentPower:F4}"));
            }
            else
            {
                table.AppendChild(TableRow(true, "Ветвь", "I, А", "U, В", "P, Вт", "Q, вар", "|S|, ВА"));
                foreach (var r in result.BranchResults)
                    table.AppendChild(TableRow(false,
                        r.Branch.ToString(), $"{r.Current:F4}", $"{r.Voltage:F4}",
                        $"{r.ActivePower:F4}", $"{r.ReactivePower:F4}", $"{r.ApparentPower:F4}"));
            }
            return table;
        }

        private static TableRow TableRow(bool isHeader, params string[] cells)
        {
            var row = new TableRow();
            foreach (var cell in cells)
            {
                var tc = new TableCell();
                var p  = new Paragraph();
                var run = new Run(new Text(cell));
                if (isHeader)
                    run.PrependChild(new RunProperties(new Bold()));
                p.AppendChild(run);
                tc.AppendChild(p);
                row.AppendChild(tc);
            }
            return row;
        }

        // ── Styles ────────────────────────────────────────────────────────────

        private static void AddStyles(MainDocumentPart mainPart)
        {
            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles();

            stylesPart.Styles.AppendChild(MakeStyle("Normal",   "Normal",   false, 22));
            stylesPart.Styles.AppendChild(MakeStyle("Title",    "Title",    true,  36));
            stylesPart.Styles.AppendChild(MakeStyle("Heading1", "Heading 1",true,  28));
            stylesPart.Styles.AppendChild(MakeStyle("Heading2", "Heading 2",true,  24));
        }

        private static Style MakeStyle(string id, string name, bool bold, int halfPts)
        {
            var style = new Style
            {
                Type    = StyleValues.Paragraph,
                StyleId = id
            };
            style.AppendChild(new StyleName { Val = name });

            var rPr = new StyleRunProperties();
            if (bold)     rPr.AppendChild(new Bold());
            rPr.AppendChild(new FontSize { Val = halfPts.ToString() });
            style.AppendChild(rPr);

            return style;
        }

        private static string MethodName(CalculationMethod m) => m switch
        {
            CalculationMethod.MeshCurrents        => "Метод контурных токов (МКТ)",
            CalculationMethod.KirchhoffLaws       => "Законы Кирхгофа",
            CalculationMethod.NodePotentials       => "Метод узловых потенциалов (МУП)",
            CalculationMethod.EquivalentGenerator  => "Метод эквивалентного генератора",
            CalculationMethod.PotentialDiagram     => "Потенциальная диаграмма",
            CalculationMethod.SimplifiedThreeBranch => "Упрощённый расчёт трёхветвевой схемы",
            CalculationMethod.VectorData            => "Данные для векторных диаграмм",
            CalculationMethod.ThreePhase            => "Расчёт трёхфазной цепи",
            _ => m.ToString()
        };
    }
}
