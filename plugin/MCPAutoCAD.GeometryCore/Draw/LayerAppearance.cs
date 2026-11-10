using System;
using System.Globalization;

namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>Propiedades de aspecto de una capa (lo que una entidad ByLayer hereda).</summary>
    public sealed record LayerLook(int Color, string TipoLinea, int Grosor, double Transparencia, bool Plot);

    /// <summary>Qué propiedades de una entidad son ByLayer (true = hereda de la capa).</summary>
    public sealed record ByLayerFlags(bool Color, bool TipoLinea, bool Grosor, bool Transparencia);

    /// <summary>Qué hay que fijar en la entidad antes de moverla para que no cambie de aspecto (null = no tocar).</summary>
    public sealed record AppearanceFix(int? Color, string? TipoLinea, int? Grosor, double? Transparencia, bool PierdeNoPlot);

    /// <summary>
    /// <c>normalizar_capas conservar_aspecto</c> (protocol.md §5G.5; mejora pendiente de la Fase L): decide, entidad por
    /// entidad, qué propiedades heredadas de la capa de origen hay que fijar en la entidad al moverla a la capa destino,
    /// como hace tools/normalizar_capas_rotulo.py. Puro.
    /// </summary>
    public static class LayerAppearance
    {
        private const double TransparencyTolerance = 1e-9;

        /// <summary>
        /// Comparación sin mayúsculas de tipos de línea; transparencias con tolerancia 1e-9. Una propiedad ByLayer cuyo
        /// valor en la capa destino sea IGUAL al de la capa origen no se fija (sigue ByLayer). <see cref="AppearanceFix.PierdeNoPlot"/>
        /// = true si la capa origen no imprime y la destino sí (no se puede fijar por entidad: solo se avisa).
        /// </summary>
        public static AppearanceFix Compute(ByLayerFlags flags, LayerLook origen, LayerLook destino)
        {
            if (flags == null)
            {
                throw new ArgumentNullException(nameof(flags));
            }

            if (origen == null)
            {
                throw new ArgumentNullException(nameof(origen));
            }

            if (destino == null)
            {
                throw new ArgumentNullException(nameof(destino));
            }

            bool colorDifiere = origen.Color != destino.Color;
            bool tipoDifiere = !string.Equals(origen.TipoLinea, destino.TipoLinea, StringComparison.OrdinalIgnoreCase);
            bool grosorDifiere = origen.Grosor != destino.Grosor;
            bool transparenciaDifiere = Math.Abs(origen.Transparencia - destino.Transparencia) > TransparencyTolerance;

            return new AppearanceFix(
                flags.Color && colorDifiere ? origen.Color : null,
                flags.TipoLinea && tipoDifiere ? origen.TipoLinea : null,
                flags.Grosor && grosorDifiere ? origen.Grosor : null,
                flags.Transparencia && transparenciaDifiere ? origen.Transparencia : null,
                !origen.Plot && destino.Plot);
        }

        /// <summary>true si <paramref name="fix"/> fija al menos una propiedad (la entidad cambiaría de aspecto sin conservar_aspecto).</summary>
        public static bool Changes(AppearanceFix fix)
        {
            if (fix == null)
            {
                throw new ArgumentNullException(nameof(fix));
            }

            return fix.Color != null || fix.TipoLinea != null || fix.Grosor != null || fix.Transparencia != null;
        }

        /// <summary>
        /// Aviso de la previsualización sin conservar_aspecto: "&lt;n&gt; entidad(es) cambiarían de aspecto al pasar a su capa
        /// destino (color, tipo de línea, grosor o transparencia heredados de la capa origen); pase conservar_aspecto: true
        /// para fijarlos en cada entidad antes de moverla." (null si n = 0).
        /// </summary>
        public static string? WouldChangeWarning(int n)
        {
            if (n <= 0)
            {
                return null;
            }

            return Int(n) + " entidad(es) cambiarían de aspecto al pasar a su capa destino (color, tipo de línea, grosor o "
                + "transparencia heredados de la capa origen); pase conservar_aspecto: true para fijarlos en cada entidad antes de moverla.";
        }

        /// <summary>
        /// Aviso de la escritura con conservar_aspecto: "conservar_aspecto: se fijaron color, tipo de línea, grosor o
        /// transparencia en &lt;n&gt; entidad(es) para conservar su aspecto; &lt;k&gt; pierden el 'no imprimir' de su capa origen
        /// (no se fija por entidad)." (la segunda frase solo si k &gt; 0; null si n = 0 y k = 0).
        /// </summary>
        public static string? FixedWarning(int n, int k)
        {
            if (n <= 0 && k <= 0)
            {
                return null;
            }

            string texto = "conservar_aspecto: se fijaron color, tipo de línea, grosor o transparencia en " + Int(n)
                + " entidad(es) para conservar su aspecto";
            return k > 0
                ? texto + "; " + Int(k) + " pierden el 'no imprimir' de su capa origen (no se fija por entidad)."
                : texto + ".";
        }

        private static string Int(int i) => i.ToString(CultureInfo.InvariantCulture);
    }
}
