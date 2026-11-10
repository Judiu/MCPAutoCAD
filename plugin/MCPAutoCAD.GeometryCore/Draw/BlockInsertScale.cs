using System;
using System.Collections.Generic;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Escala de inserción de insertar_bloque (protocol.md §5D.5, "Unidades"): lectura del parámetro <c>escala</c>
    /// (número o array [sx, sy] / [sx, sy, sz], todos ≠ 0 y finitos) y aplicación del factor de unidades
    /// (<see cref="Audit.UnitsScale.Factor"/>) según <c>escala_unidades</c> ("auto" | "ninguna").
    /// </summary>
    public static class BlockInsertScale
    {
        public const string ModoAuto = "auto";
        public const string ModoNinguna = "ninguna";

        /// <summary>Valores admitidos de escala_unidades (orden del schema).</summary>
        public static readonly string[] Modos = { ModoAuto, ModoNinguna };

        /// <summary>Mensaje exacto de §5D.5 / §6 para una escala inválida.</summary>
        public const string InvalidScale = "escala: debe ser un número distinto de 0 o un array [sx, sy] / [sx, sy, sz]";

        /// <summary>Tolerancia con la que un factor se considera 1 (no se aplica ni se advierte).</summary>
        private const double UnitTolerance = 1e-12;

        /// <summary>Escala uniforme: [s, s, s]. s = 0 o no finito → ArgumentException(<see cref="InvalidScale"/>).</summary>
        public static double[] ParseEscala(double s)
        {
            if (!IsValidComponent(s))
            {
                throw new ArgumentException(InvalidScale);
            }

            return new[] { s, s, s };
        }

        /// <summary>
        /// Escala por componentes: [sx, sy] → [sx, sy, sx] (como el comando INSERT, cuya Z por defecto es la X);
        /// [sx, sy, sz] tal cual. Otra longitud, algún 0 o no finito → ArgumentException(<see cref="InvalidScale"/>).
        /// </summary>
        public static double[] ParseEscala(IReadOnlyList<double> componentes)
        {
            if (componentes == null || (componentes.Count != 2 && componentes.Count != 3))
            {
                throw new ArgumentException(InvalidScale);
            }

            foreach (double c in componentes)
            {
                if (!IsValidComponent(c))
                {
                    throw new ArgumentException(InvalidScale);
                }
            }

            return componentes.Count == 2
                ? new[] { componentes[0], componentes[1], componentes[0] }
                : new[] { componentes[0], componentes[1], componentes[2] };
        }

        /// <summary>
        /// Escala aplicada a la inserción. <paramref name="modo"/> "ninguna" → la pedida (sin conversión);
        /// "auto" → pedida × factor por componente (lo que haría INSERT con escala automática). Factor null, no finito,
        /// ≤ 0 o igual a 1 → la pedida y <c>factorAplicado = false</c>. El signo (espejo) se conserva.
        /// </summary>
        public static (double[] aplicada, bool factorAplicado) Apply(double[] escala, double? factor, string modo)
        {
            if (escala == null || escala.Length != 3)
            {
                throw new ArgumentException("escala: se esperan 3 componentes");
            }

            if (!string.Equals(modo, ModoAuto, StringComparison.Ordinal) && !string.Equals(modo, ModoNinguna, StringComparison.Ordinal))
            {
                throw new ArgumentException("escala_unidades: debe ser uno de " + string.Join(", ", Modos));
            }

            var copia = new[] { escala[0], escala[1], escala[2] };
            if (string.Equals(modo, ModoNinguna, StringComparison.Ordinal) || !IsApplicableFactor(factor))
            {
                return (copia, false);
            }

            double f = factor!.Value;
            return (new[] { copia[0] * f, copia[1] * f, copia[2] * f }, true);
        }

        /// <summary>true si el factor es finito, &gt; 0 y distinto de 1 (entonces "auto" lo aplica).</summary>
        public static bool IsApplicableFactor(double? factor) =>
            factor.HasValue && double.IsFinite(factor.Value) && factor.Value > 0 && Math.Abs(factor.Value - 1.0) > UnitTolerance;

        /// <summary>
        /// Factor de unidades que se informa (factor_unidades): el de <see cref="Audit.UnitsScale.Factor"/> si alguna
        /// de las dos unidades lo tiene; 1 si alguna es 0 o no tiene equivalencia métrica (<paramref name="sinFactor"/> = true,
        /// entonces corresponde <see cref="NoFactorWarning"/>).
        /// </summary>
        public static double ReportedFactor(int unidadesBloque, int insunitsDibujo, out bool sinFactor)
        {
            double? f = unidadesBloque == 0 || insunitsDibujo == 0 ? null : Audit.UnitsScale.Factor(unidadesBloque, insunitsDibujo);
            sinFactor = f == null || !double.IsFinite(f.Value) || f.Value <= 0;
            return sinFactor ? 1.0 : f!.Value;
        }

        /// <summary>
        /// "Se aplicó el factor de unidades 0.001 (bloque en mm, dibujo en m): escala final 0.001, 0.001, 0.001. Si la
        /// geometría del bloque ya está en las unidades del dibujo (metadato de unidades incorrecto, ver auditar_bloques),
        /// repita con escala_unidades: "ninguna"."
        /// </summary>
        public static string AppliedWarning(double factor, int unidadesBloque, int insunitsDibujo, double[] aplicada)
        {
            if (aplicada == null || aplicada.Length != 3)
            {
                throw new ArgumentException("aplicada: se esperan 3 componentes");
            }

            return "Se aplicó el factor de unidades " + Format(factor) + " (bloque en " + UnitsTable.Name(unidadesBloque)
                + ", dibujo en " + UnitsTable.Name(insunitsDibujo) + "): escala final " + Format(aplicada[0]) + ", "
                + Format(aplicada[1]) + ", " + Format(aplicada[2])
                + ". Si la geometría del bloque ya está en las unidades del dibujo (metadato de unidades incorrecto, ver auditar_bloques), repita con escala_unidades: \"ninguna\".";
        }

        /// <summary>"No se aplicó factor de unidades: el bloque (INSUNITS=0) o el dibujo (INSUNITS=6) no declaran unidades con equivalencia métrica."</summary>
        public static string NoFactorWarning(int unidadesBloque, int insunitsDibujo) =>
            "No se aplicó factor de unidades: el bloque (INSUNITS=" + unidadesBloque.ToString(CultureInfo.InvariantCulture)
            + ") o el dibujo (INSUNITS=" + insunitsDibujo.ToString(CultureInfo.InvariantCulture)
            + ") no declaran unidades con equivalencia métrica.";

        /// <summary>Número en cultura invariante con 10 cifras significativas (0.001, 39.37, -1).</summary>
        public static string Format(double v) => v.ToString("G10", CultureInfo.InvariantCulture);

        private static bool IsValidComponent(double c) => double.IsFinite(c) && c != 0;
    }
}
