using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>Una propiedad dinámica de una referencia de bloque, ya leída (protocol.md §5G.6).</summary>
    public sealed record DynamicPropertyInfo(
        string Nombre,
        object? Valor,
        bool SoloLectura,
        string Unidades,
        IReadOnlyList<object?>? ValoresPermitidos,
        string Descripcion);

    /// <summary>
    /// Resultado de buscar la propiedad de ancho: <see cref="Propiedad"/> = la elegida, o null si no hay ninguna o si es
    /// ambiguo; <see cref="Candidatas"/> = nombres que encajan. Con más de una no se elige (§5G.1).
    /// </summary>
    public sealed record WidthMatch(DynamicPropertyInfo? Propiedad, IReadOnlyList<string> Candidatas)
    {
        public bool Ambigua => Candidatas.Count > 1;
    }

    /// <summary>
    /// Lógica pura de las propiedades dinámicas (protocol.md §5G.1, §5G.6): buscar la propiedad de ancho, la forma JSON de la
    /// respuesta y el trato de los valores que da AutoCAD. Lo que toca el API de AutoCAD sigue en <c>DynamicBlockSupport</c>.
    /// </summary>
    public static class DynamicPropertyRules
    {
        public const string UnidadesNinguna = "ninguna";
        public const string UnidadesAngular = "angular";
        public const string UnidadesDistancia = "distancia";
        public const string UnidadesArea = "area";

        /// <summary>Tolerancia relativa para reconocer la propiedad de ancho por su valor nominal.</summary>
        public const double WidthMatchTol = 1e-3;

        /// <summary>Tolerancia relativa al comparar un número con un valor admitido o con el valor que dejó AutoCAD.</summary>
        private const double NumberMatchTol = 1e-9;

        /// <summary>
        /// Deduce la propiedad de ancho de un bloque de vano sin nombre configurado (con nombre, <c>VanoHandler</c> lo usa tal
        /// cual y no llega aquí): las propiedades de distancia escribibles cuyo valor numérico ≈
        /// <paramref name="anchoNominal"/> (tolerancia relativa <see cref="WidthMatchTol"/>). Con una sola, esa; con varias de
        /// nombre distinto no se elige (<see cref="WidthMatch.Ambigua"/>), porque fijar la equivocada deformaría el bloque sin
        /// avisar.
        /// </summary>
        public static WidthMatch FindWidthProperty(IReadOnlyList<DynamicPropertyInfo> props, double anchoNominal)
        {
            List<DynamicPropertyInfo> encajan = props
                .Where(p => !p.SoloLectura
                    && p.Unidades == UnidadesDistancia
                    && TryNumber(p.Valor, out double v)
                    && Math.Abs(v - anchoNominal) <= WidthMatchTol * anchoNominal)
                .ToList();
            List<string> nombres = encajan.Select(p => p.Nombre).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new WidthMatch(nombres.Count == 1 ? encajan[0] : null, nombres);
        }

        /// <summary>Advertencia de §5G.1 cuando el ancho se resuelve por escala: bloque no dinámico o sin propiedad de ancho.</summary>
        public static string ScaledWidthWarning(string bloque, double factor) =>
            "vano: el bloque '" + bloque + "' no es dinámico (o no se encontró su propiedad de ancho); se escaló "
            + FormatFactor(factor) + " a lo largo del vano: las jambas y la hoja quedan deformadas en proporción.";

        /// <summary>Advertencia de §5G.1 cuando varias propiedades pueden ser el ancho y no hay nombre configurado.</summary>
        public static string AmbiguousWidthWarning(string bloque, IReadOnlyList<string> candidatas, double factor) =>
            "vano: el bloque '" + bloque + "' tiene varias propiedades que pueden ser el ancho (" + string.Join(", ", candidatas)
            + "); no se eligió ninguna y se escaló " + FormatFactor(factor)
            + " a lo largo del vano: las jambas y la hoja quedan deformadas en proporción. Fije propiedad_ancho en lps.json biblioteca_vanos.";

        /// <summary>Advertencia de §5G.1 cuando el cliente pasa el ancho en propiedades_dinamicas.</summary>
        public static string WidthIgnoredWarning(string propiedad) =>
            "propiedades_dinamicas: el ancho lo fija la herramienta; se ignoró el valor de '" + propiedad + "'.";

        /// <summary>Advertencia de §5G.1: previsualización sin dibujar de un bloque que aún no está en el dibujo.</summary>
        public static string PendingWidthWarning(string bloque) =>
            "vano: la definición '" + bloque + "' aún no está en el dibujo; su propiedad de ancho se deducirá al crear la referencia en confirmar (ancho_por: pendiente).";

        /// <summary>Forma JSON de la respuesta: [{nombre, valor, solo_lectura, unidades, valores_permitidos, descripcion}].</summary>
        public static List<Dictionary<string, object?>> ToJson(IReadOnlyList<DynamicPropertyInfo> props)
        {
            var list = new List<Dictionary<string, object?>>(props.Count);
            foreach (DynamicPropertyInfo p in props)
            {
                list.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["nombre"] = p.Nombre,
                    ["valor"] = p.Valor,
                    ["solo_lectura"] = p.SoloLectura,
                    ["unidades"] = p.Unidades,
                    ["valores_permitidos"] = p.ValoresPermitidos,
                    ["descripcion"] = p.Descripcion,
                });
            }

            return list;
        }

        /// <summary>Valor numérico de cualquiera de los tipos con que AutoCAD guarda un número (short, int, double…).</summary>
        public static bool TryNumber(object? value, out double number)
        {
            switch (value)
            {
                case double d:
                    number = d;
                    return true;
                case float f:
                    number = f;
                    return true;
                case int i:
                    number = i;
                    return true;
                case short s:
                    number = s;
                    return true;
                case long l:
                    number = l;
                    return true;
                case decimal m:
                    number = (double)m;
                    return true;
                default:
                    number = 0;
                    return false;
            }
        }

        /// <summary>Números con tolerancia relativa; cadenas sin distinguir mayúsculas; el resto, Equals.</summary>
        public static bool SameValue(object? a, object? b)
        {
            if (TryNumber(a, out double x) && TryNumber(b, out double y))
            {
                return Math.Abs(x - y) <= NumberMatchTol * Math.Max(1.0, Math.Max(Math.Abs(x), Math.Abs(y)));
            }

            if (a is string s && b is string t)
            {
                return string.Equals(s, t, StringComparison.OrdinalIgnoreCase);
            }

            return Equals(a, b);
        }

        /// <summary>El número con el tipo del valor actual (short o int) si cabe en él; si no, double.</summary>
        public static object ConvertNumber(double number, object? actual)
        {
            bool whole = number == Math.Truncate(number);
            if (actual is short && whole && number >= short.MinValue && number <= short.MaxValue)
            {
                return (short)number;
            }

            if (actual is int && whole && number >= int.MinValue && number <= int.MaxValue)
            {
                return (int)number;
            }

            return number;
        }

        /// <summary>
        /// Número, cadena o array de coordenadas: lo que admite el serializador de la respuesta. Los puntos de AutoCAD llegan
        /// aquí ya convertidos a double[] (<c>DynamicBlockSupport</c>).
        /// </summary>
        public static object? NormalizeValue(object? value)
        {
            switch (value)
            {
                case null:
                case double:
                case string:
                case int:
                case double[]:
                    return value;
                case short s:
                    return (int)s;
                case float f:
                    return (double)f;
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        private static string FormatFactor(double factor) => factor.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Texto de un valor para los mensajes.</summary>
        public static string Format(object? value)
        {
            if (TryNumber(value, out double number))
            {
                return number.ToString("R", CultureInfo.InvariantCulture);
            }

            return value is double[] point
                ? "[" + string.Join(", ", point.Select(c => c.ToString("R", CultureInfo.InvariantCulture))) + "]"
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }
}
