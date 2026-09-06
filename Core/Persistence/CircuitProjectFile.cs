using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElectroCalc.Core.Models;

namespace ElectroCalc.Core.Persistence
{
    /// <summary>
    /// Portable JSON representation of the visual schematic. It stores the
    /// drawing itself (elements, junctions and logical wire endpoints), not the
    /// derived CircuitGraph. The graph is rebuilt after loading, which keeps the
    /// file independent from solver implementation details.
    /// </summary>
    public sealed class CircuitProjectFile
    {
        public const int CurrentFormatVersion = 3;
        public string Format { get; set; } = "ElectroCalcCircuit";
        public int Version { get; set; } = CurrentFormatVersion;
        public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
        public SavedAnalysisSettings Analysis { get; set; } = new();
        public List<SavedElement> Elements { get; set; } = new();
        public List<SavedJunction> Junctions { get; set; } = new();
        public List<SavedWire> Wires { get; set; } = new();
        public SavedUiState UiState { get; set; } = new();
    }

    public sealed class SavedElement
    {
        /// <summary>ID of ElementControl inside the saved drawing.</summary>
        public Guid Id { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ElementType Type { get; set; }
        public string Name { get; set; } = string.Empty;
        public double Value { get; set; }
        public double InternalResistance { get; set; }
        public bool IsPositiveAtStart { get; set; }
        public double PhaseDegrees { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ThreePhasePhase ThreePhasePhase { get; set; } = ThreePhasePhase.None;
        public double CenterX { get; set; }
        public double CenterY { get; set; }
    }


    public sealed class SavedAnalysisSettings
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public CircuitAnalysisMode Mode { get; set; } = CircuitAnalysisMode.DC;
        public double FrequencyHz { get; set; } = 50.0;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ThreePhaseLoadConnection ThreePhaseConnection { get; set; } = ThreePhaseLoadConnection.Star;
        public bool ThreePhaseHasNeutral { get; set; } = true;
    }

    public sealed class SavedJunction
    {
        public Guid Id { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    public sealed class SavedWire
    {
        public SavedEndpoint From { get; set; } = new();
        public SavedEndpoint To { get; set; } = new();
    }

    public sealed class SavedEndpoint
    {
        public Guid OwnerId { get; set; }
        public bool IsElement { get; set; }
        /// <summary>true=A, false=B. For a junction this is always false.</summary>
        public bool IsPortA { get; set; }
    }

    public sealed class SavedUiState
    {
        /// <summary>MKT, MUP or EQGEN.</summary>
        public string CalculationMethod { get; set; } = "MKT";
        /// <summary>
        /// Visual element IDs that formed the selected equivalent-generator load
        /// branch at save time. Empty when no load was selected.
        /// </summary>
        public List<Guid> LoadBranchElementIds { get; set; } = new();
    }

    public static class CircuitProjectSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new JsonStringEnumConverter() }
        };

        public static void Save(CircuitProjectFile project, string fileName)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            project.Format = "ElectroCalcCircuit";
            project.Version = CircuitProjectFile.CurrentFormatVersion;
            project.SavedUtc = DateTime.UtcNow;
            File.WriteAllText(fileName, JsonSerializer.Serialize(project, Options));
        }

        public static CircuitProjectFile Load(string fileName)
        {
            var json = File.ReadAllText(fileName);
            var project = JsonSerializer.Deserialize<CircuitProjectFile>(json, Options)
                          ?? throw new InvalidDataException("Файл схемы пуст или повреждён.");

            if (!string.Equals(project.Format, "ElectroCalcCircuit", StringComparison.Ordinal))
                throw new InvalidDataException("Это не файл схемы ElectroCalc.");
            if (project.Version < 1 || project.Version > CircuitProjectFile.CurrentFormatVersion)
                throw new InvalidDataException(
                    $"Версия файла {project.Version} не поддерживается этой версией ElectroCalc.");

            // Старые версии не содержали 3Φ-настроек/назначений фаз. Отсутствующие JSON-свойства
            // естественно получают безопасные значения по умолчанию.
            project.Analysis ??= new SavedAnalysisSettings();
            if (project.Version == 1)
            {
                project.Analysis.Mode = CircuitAnalysisMode.DC;
                if (project.Analysis.FrequencyHz <= 0) project.Analysis.FrequencyHz = 50.0;
            }
            project.Elements ??= new();
            project.Junctions ??= new();
            project.Wires ??= new();
            project.UiState ??= new();
            project.UiState.LoadBranchElementIds ??= new();
            return project;
        }
    }
}
