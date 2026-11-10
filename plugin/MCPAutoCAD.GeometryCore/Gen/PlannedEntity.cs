using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.GeometryCore.Gen
{
    /// <summary>
    /// Entidad PLANIFICADA (protocol.md §5E.0): descripción neutra de lo que una herramienta generadora quiere dibujar,
    /// calculada por GeometryCore sin AutoCAD. El plugin (<c>GeometryDrawer</c>) la convierte en entidades reales, bien en
    /// la capa MCP-PREVIEW (previsualización), bien en su capa definitiva. <see cref="Concepto"/> = concepto de
    /// lps.json.capas_preferidas ("muros", "ejes", "textos", "achurados"…); <see cref="Capa"/> = nombre de capa
    /// explícito (prioridad sobre el concepto; R3 se aplica igual). Ambos null → MCP-DIBUJO.
    /// </summary>
    public abstract record PlannedEntity(string? Concepto, string? Capa)
    {
        /// <summary>Caja XY para R4 (null = no comprobable: no bloquea).</summary>
        public abstract BBox2? Caja();

        /// <summary>Tipo DXF resultante ("LWPOLYLINE", "LINE", "CIRCLE", "TEXT", "MTEXT", "HATCH", "OFFSET", "COPIA").</summary>
        public abstract string Tipo { get; }
    }

    /// <summary>LWPOLYLINE en el plano XY (elevación 0). Bulges null = tramos rectos.</summary>
    public sealed record PlannedPolyline(
        IReadOnlyList<(double x, double y)> Vertices,
        bool Cerrada,
        IReadOnlyList<double>? Bulges,
        double Ancho,
        string? Concepto,
        string? Capa) : PlannedEntity(Concepto, Capa)
    {
        public override string Tipo => "LWPOLYLINE";

        public override BBox2? Caja()
        {
            if (Vertices.Count == 0)
            {
                return null;
            }

            double xmin = double.PositiveInfinity, ymin = double.PositiveInfinity, xmax = double.NegativeInfinity, ymax = double.NegativeInfinity;
            foreach ((double x, double y) in Vertices)
            {
                xmin = Math.Min(xmin, x);
                ymin = Math.Min(ymin, y);
                xmax = Math.Max(xmax, x);
                ymax = Math.Max(ymax, y);
            }

            return BBox2.FromCorners(xmin, ymin, xmax, ymax);
        }

        /// <summary>Longitud de las cuerdas (sin bulges) y, si es cerrada, área por fórmula del cordón (valor absoluto).</summary>
        public (double longitud, double? area) Metrics() => PolygonMetrics.LengthAndArea(Vertices, Cerrada);
    }

    public sealed record PlannedLine(Point3 Inicio, Point3 Fin, string? Concepto, string? Capa) : PlannedEntity(Concepto, Capa)
    {
        public override string Tipo => "LINE";

        /// <summary>Aspecto propio de la LINE original cuando se parte (§5G.1): opaco aquí, lo aplica el plugin. null = ByLayer.</summary>
        public object? AspectoOriginal { get; init; }

        public override BBox2? Caja() => BBox2.FromPoints(new[] { Inicio, Fin });

        public double Longitud => Inicio.Distance(Fin);
    }

    public sealed record PlannedCircle(Point3 Centro, double Radio, string? Concepto, string? Capa) : PlannedEntity(Concepto, Capa)
    {
        public override string Tipo => "CIRCLE";

        public override BBox2? Caja() => BBox2.FromCorners(Centro.X - Radio, Centro.Y - Radio, Centro.X + Radio, Centro.Y + Radio);
    }

    /// <summary>
    /// TEXT de una línea. <see cref="Texto"/> LITERAL (el dibujante aplica TextEscape.EscapeDbText, R7).
    /// <see cref="Justificacion"/>: "izquierda" | "centro" | "derecha" | "medio" (como §5D.4) o "medio_centro"
    /// (TextHorizontalMode.TextMid + TextVerticalMode.TextVerticalMid: el punto es el centro geométrico del texto, como los
    /// rótulos de eje de la oficina). <see cref="Estilo"/> null = estilo actual.
    /// </summary>
    public sealed record PlannedText(
        Point3 Posicion,
        string Texto,
        double Altura,
        double RotacionGrados,
        string Justificacion,
        string? Estilo,
        string? Concepto,
        string? Capa) : PlannedEntity(Concepto, Capa)
    {
        public override string Tipo => "TEXT";

        public override BBox2? Caja() => BBox2.FromCorners(Posicion.X, Posicion.Y, Posicion.X, Posicion.Y);
    }

    /// <summary>
    /// MTEXT. <see cref="Texto"/> LITERAL con saltos de línea '\n' (el dibujante aplica MTextEscape.Escape: '\n' → \P).
    /// <see cref="Adjunto"/>: los nueve valores de §5D.4 ("medio_centro" para etiquetas de recinto).
    /// </summary>
    public sealed record PlannedMText(
        Point3 Posicion,
        string Texto,
        double Altura,
        double Ancho,
        double RotacionGrados,
        string Adjunto,
        string? Estilo,
        string? Concepto,
        string? Capa) : PlannedEntity(Concepto, Capa)
    {
        public override string Tipo => "MTEXT";

        public override BBox2? Caja() => BBox2.FromCorners(Posicion.X, Posicion.Y, Posicion.X, Posicion.Y);
    }

    /// <summary>
    /// Bucle de un sombreado rehecho (§5G.0): entidades nuevas del plan (<see cref="Indices"/>) y existentes
    /// (<see cref="Handles"/>), o, si el bucle original no tiene entidad de contorno, sus <see cref="Vertices"/> con
    /// <see cref="Bulges"/> (cerrado: primero = último). <see cref="TipoBucle"/> = valor de HatchLoopTypes.
    /// </summary>
    public sealed record PlannedHatchLoop(
        IReadOnlyList<int> Indices,
        IReadOnlyList<string> Handles,
        int TipoBucle,
        IReadOnlyList<(double x, double y)>? Vertices = null,
        IReadOnlyList<double>? Bulges = null);

    /// <summary>
    /// HATCH asociativo cuyos bucles son las <see cref="PlannedPolyline"/> CERRADAS situadas en <see cref="IndicesContorno"/>
    /// de la misma lista de entidades planificadas (todas antes del HATCH; el primero es el exterior, los demás islas con
    /// HatchStyle Normal). Orden de la API al dibujar: §5D.7.
    /// </summary>
    public sealed record PlannedHatch(IReadOnlyList<int> IndicesContorno, string Patron, double Escala, double AnguloGrados, string? Concepto, string? Capa)
        : PlannedEntity(Concepto, Capa)
    {
        public override string Tipo => "HATCH";

        /// <summary>
        /// Aspecto propio del sombreado original cuando se rehace (color, transparencia, grosor y tipo de línea, §5G.0).
        /// GeometryCore no conoce esos tipos de AutoCAD: es opaco aquí y lo aplica el plugin. null = ByLayer.
        /// </summary>
        public object? AspectoOriginal { get; init; }

        /// <summary>
        /// Bucles explícitos de un sombreado rehecho que combina entidades nuevas y existentes (§5G.0). null = un bucle por
        /// cada índice de <see cref="IndicesContorno"/>, que en todo caso lista los contornos nuevos para R4.
        /// </summary>
        public IReadOnlyList<PlannedHatchLoop>? Bucles { get; init; }

        /// <summary>Su caja es la unión de las de sus contornos; la calcula <see cref="GeneratedGeometry.CajasParaR4"/>.</summary>
        public override BBox2? Caja() => null;
    }

    /// <summary>
    /// Copia de una entidad existente transformada por <see cref="Transformacion"/> (planta XY; el plugin la convierte en
    /// Matrix3d). <see cref="CajaTransformada"/> = caja de la entidad original transformada (leída por el handler) para R4.
    /// El dibujante copia con Database.DeepCloneObjects + TransformBy (como copiar, §5D.8) y, en previsualización, lleva
    /// el clon a MCP-PREVIEW; en definitivo, conserva la capa del original salvo que <see cref="Capa"/>/<see cref="Concepto"/>
    /// indiquen otra.
    /// </summary>
    public sealed record PlannedCopy(string HandleOrigen, Affine2 Transformacion, BBox2? CajaTransformada, string? Concepto, string? Capa)
        : PlannedEntity(Concepto, Capa)
    {
        public override string Tipo => "COPIA";

        public override BBox2? Caja() => CajaTransformada;
    }

    /// <summary>
    /// Curva(s) paralela(s) a una existente: Curve.GetOffsetCurves(±Distancia); el signo lo decide el dibujante para que
    /// el resultado quede del lado de <see cref="LadoPunto"/> (el más cercano a ese punto, <see cref="OffsetChoice"/>).
    /// <see cref="CajaResultado"/> = caja de la curva ELEGIDA, calculada en la lectura con el mismo criterio que el
    /// dibujante (para R4 y numeros.bbox; corrección del gate en vivo de Fase 4A, 2026-10-27: antes era la caja de la
    /// original ampliada |Distancia|, que abarcaba las dos paralelas candidatas). Capa: la del original salvo Concepto/Capa.
    /// </summary>
    public sealed record PlannedOffset(string HandleOrigen, double Distancia, Point3 LadoPunto, BBox2? CajaResultado, string? Concepto, string? Capa)
        : PlannedEntity(Concepto, Capa)
    {
        public override string Tipo => "OFFSET";

        public override BBox2? Caja() => CajaResultado;
    }

    /// <summary>
    /// Ancho que no se pudo decidir al planificar (§5G.1): la definición dinámica viene de la biblioteca, sin propiedad_ancho
    /// configurada, y sus propiedades solo se conocen en la referencia recién creada. <see cref="EscalaSinPropiedad"/> y
    /// <see cref="FactorAncho"/> = la colocación por escala, por si no se encuentra una única propiedad.
    /// </summary>
    /// <remarks><see cref="ValorPropiedad"/> y <see cref="ValorNominal"/> ya llevan el ajuste_ancho del catálogo (§5G.1).</remarks>
    public sealed record PendingWidth(double ValorPropiedad, double ValorNominal, IReadOnlyList<double> EscalaSinPropiedad, double FactorAncho);

    /// <summary>
    /// Referencia de bloque (INSERT) planificada (Fase 4B, protocol.md §5G.1 y §5G.0): la usa <c>vano</c> para colocar la
    /// puerta/ventana. <see cref="Nombre"/> = nombre del bloque (del dibujo o de la biblioteca, §5D.5, si
    /// <see cref="DesdeBiblioteca"/>); <see cref="Escala"/> = [sx, sy, sz] (negativo = espejo); <see cref="PropiedadesDinamicas"/>
    /// = propiedad dinámica → valor (p. ej. el ancho), null si no hay que fijar ninguna; <see cref="CajaEstimada"/> = caja
    /// prevista para R4 (la calcula el planificador con el tamaño nominal del catálogo; el dibujante informa la real).
    /// En previsualización el INSERT se crea en MCP-PREVIEW (su contenido conserva sus capas internas).
    /// </summary>
    public sealed record PlannedBlockRef(
        string Nombre,
        bool DesdeBiblioteca,
        Point3 Posicion,
        double RotacionGrados,
        IReadOnlyList<double> Escala,
        IReadOnlyDictionary<string, object>? PropiedadesDinamicas,
        BBox2? CajaEstimada,
        string? Concepto,
        string? Capa) : PlannedEntity(Concepto, Capa)
    {
        public override string Tipo => "INSERT";

        /// <summary>No null = el dibujante deduce la propiedad de ancho en la referencia creada; <see cref="Escala"/> es la de ±1.</summary>
        public PendingWidth? AnchoPendiente { get; init; }

        public override BBox2? Caja() => CajaEstimada;
    }

    /// <summary>
    /// Borrado planificado de una entidad EXISTENTE del usuario (Fase 4B: <c>vano</c> borra el contorno original y sus
    /// sombreados asociativos; <c>union_muros</c> borra los dos contornos unidos). En previsualización NO se borra nada
    /// (se omite); en definitivo (o al confirmar) el dibujante la borra en la misma transacción. <see cref="TipoEsperado"/>
    /// = tipo DXF leído en la planificación (defensa: si cambió, el dibujante lanza). El handler registra además la huella
    /// de la entidad en <see cref="GeneratedGeometry.AnclasExtra"/> para que <c>confirmar</c> detecte cambios.
    /// </summary>
    public sealed record PlannedErase(string Handle, string TipoEsperado) : PlannedEntity(null, null)
    {
        public override string Tipo => "BORRAR";

        /// <summary>No cuenta para R4 (no crea geometría).</summary>
        public override BBox2? Caja() => null;
    }

    /// <summary>
    /// Cota planificada (Fase 4B, <c>cotas_automaticas</c>): <see cref="TipoCota"/> "lineal" (RotatedDimension con
    /// <see cref="RotacionGrados"/>) o "alineada" (AlignedDimension). <see cref="Estilo"/> = nombre de DIMSTYLE existente
    /// (null = el actual). Caja para R4 = la de P1, P2 y PosicionLinea (como <c>cota</c>, §5D.6).
    /// </summary>
    public sealed record PlannedDimension(
        string TipoCota,
        Point3 P1,
        Point3 P2,
        Point3 PosicionLinea,
        double RotacionGrados,
        string? Estilo,
        string? Concepto,
        string? Capa) : PlannedEntity(Concepto, Capa)
    {
        public override string Tipo => "DIMENSION";

        public override BBox2? Caja() => BBox2.FromPoints(new[] { P1, P2, PosicionLinea });

        /// <summary>Distancia P1–P2 en planta (lo que medirá una cota alineada).</summary>
        public double Distancia2D => Math.Sqrt((P2.X - P1.X) * (P2.X - P1.X) + (P2.Y - P1.Y) * (P2.Y - P1.Y));
    }

    /// <summary>
    /// Resultado de planificar una herramienta generadora: entidades a crear (en orden; un HATCH después de su contorno) y
    /// los NÚMEROS de verificación de la respuesta (<see cref="Numeros"/>: bbox, longitudes, áreas, n_entidades y lo propio
    /// de cada herramienta, protocol.md §5E.0). Lo produce la lógica pura; el plugin solo lo dibuja.
    /// </summary>
    public sealed class GeneratedGeometry
    {
        public GeneratedGeometry(IReadOnlyList<PlannedEntity> entidades, Dictionary<string, object?> numeros)
        {
            Entidades = entidades ?? throw new ArgumentNullException(nameof(entidades));
            Numeros = numeros ?? throw new ArgumentNullException(nameof(numeros));
        }

        public IReadOnlyList<PlannedEntity> Entidades { get; }

        /// <summary>Campos numéricos de la respuesta (se vuelcan en 'numeros'). Debe incluir al menos 'n_entidades' y 'bbox'.</summary>
        public Dictionary<string, object?> Numeros { get; }

        /// <summary>
        /// Huellas de las entidades existentes que el plan usó DIRECTAMENTE por handle (eje/contorno de muro y recinto,
        /// curva de offset, originales de los arrays), además de las anclas de los PuntoRef. La previsualización las
        /// registra y <c>confirmar</c> las vuelve a comprobar (protocol.md §5E.5).
        /// </summary>
        public List<AnchorFingerprint> AnclasExtra { get; } = new List<AnchorFingerprint>();

        /// <summary>Cajas para R4 en el orden de <see cref="Entidades"/> (un HATCH toma la de su contorno; sin caja → se omite).</summary>
        public List<BBox2> CajasParaR4()
        {
            var cajas = new List<BBox2>(Entidades.Count);
            for (int i = 0; i < Entidades.Count; i++)
            {
                BBox2? b = Entidades[i].Caja();
                if (Entidades[i] is PlannedHatch h)
                {
                    foreach (int k in h.IndicesContorno)
                    {
                        if (k >= 0 && k < Entidades.Count)
                        {
                            BBox2? c = Entidades[k].Caja();
                            if (c != null)
                            {
                                b = b == null ? c : b.Value.Union(c.Value);
                            }
                        }
                    }
                }

                if (b != null)
                {
                    cajas.Add(b.Value);
                }
            }

            return cajas;
        }

        /// <summary>Unión de todas las cajas conocidas, o null.</summary>
        public BBox2? Bbox()
        {
            BBox2? total = null;
            foreach (BBox2 b in CajasParaR4())
            {
                total = total == null ? b : total.Value.Union(b);
            }

            return total;
        }
    }
}
