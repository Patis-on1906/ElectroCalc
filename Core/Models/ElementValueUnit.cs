using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ElectroCalc.Core.Models
{
    /// <summary>
    /// Единица, доступная в поле номинала. В CircuitElement.Value значение
    /// всегда хранится в базовых единицах СИ.
    /// </summary>
    public sealed class ElementValueUnit
    {
        public ElementType ElementType { get; }
        public string Symbol { get; }
        public double MultiplierToSi { get; }

        public ElementValueUnit(ElementType elementType, string symbol, double multiplierToSi)
        {
            ElementType = elementType;
            Symbol = symbol;
            MultiplierToSi = multiplierToSi;
        }

        public double ToSi(double displayedValue) => displayedValue * MultiplierToSi;
        public double FromSi(double siValue) => siValue / MultiplierToSi;
        public override string ToString() => Symbol;
    }

    public static class ElementValueUnits
    {
        private static readonly IReadOnlyDictionary<ElementType, IReadOnlyList<ElementValueUnit>> Options =
            new Dictionary<ElementType, IReadOnlyList<ElementValueUnit>>
            {
                [ElementType.Resistor] = Units(ElementType.Resistor, ("Ом", 1.0), ("кОм", 1e3), ("МОм", 1e6)),
                [ElementType.VoltageSource] = Units(ElementType.VoltageSource, ("В", 1.0), ("мВ", 1e-3), ("кВ", 1e3)),
                [ElementType.CurrentSource] = Units(ElementType.CurrentSource, ("А", 1.0), ("мА", 1e-3), ("мкА", 1e-6)),
                [ElementType.Capacitor] = Units(ElementType.Capacitor,
                    ("Ф", 1.0), ("мФ", 1e-3), ("мкФ", 1e-6), ("нФ", 1e-9), ("пФ", 1e-12)),
                [ElementType.Inductor] = Units(ElementType.Inductor,
                    ("Гн", 1.0), ("мГн", 1e-3), ("мкГн", 1e-6))
            };

        public static IReadOnlyList<ElementValueUnit> For(ElementType type) => Options[type];

        public static ElementValueUnit DefaultFor(ElementType type)
        {
            string preferred = type switch
            {
                ElementType.Capacitor => "мкФ",
                ElementType.Inductor => "мГн",
                _ => Options[type][0].Symbol
            };
            return Options[type].First(unit => unit.Symbol == preferred);
        }

        public static ElementValueUnit BestFor(ElementType type, double siValue)
        {
            var units = Options[type];
            if (siValue == 0) return DefaultFor(type);

            double magnitude = Math.Abs(siValue);
            return units
                .Where(unit => magnitude / unit.MultiplierToSi >= 1.0)
                .OrderBy(unit => magnitude / unit.MultiplierToSi >= 1000.0 ? 1 : 0)
                .ThenBy(unit => Math.Abs(Math.Log10(magnitude / unit.MultiplierToSi) - 1.0))
                .FirstOrDefault() ?? units[^1];
        }

        public static string Format(double siValue, ElementType type, string numberFormat = "0.######")
        {
            var unit = BestFor(type, siValue);
            return $"{unit.FromSi(siValue).ToString(numberFormat, CultureInfo.InvariantCulture)} {unit.Symbol}";
        }

        private static IReadOnlyList<ElementValueUnit> Units(ElementType type,
            params (string Symbol, double Multiplier)[] values) =>
            values.Select(value => new ElementValueUnit(type, value.Symbol, value.Multiplier)).ToArray();
    }
}
