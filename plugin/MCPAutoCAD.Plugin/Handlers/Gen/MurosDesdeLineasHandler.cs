using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.GeometryCore.Layouts;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Audit;
using MCPAutoCAD.Plugin.Handlers.Clean;
using MCPAutoCAD.Plugin.Handlers.Draw;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// muros_desde_lineas. Hilo principal; requiere documento. Previsualiza por defecto; con confirmar: true dibuja los muros
    /// en UN paso de deshacer. Geometría pura en GeometryCore (<see cref="WallFromLines"/>); aquí solo se lee el espacio,
    /// se validan capas/unidades y se escribe.
    /// <list type="number">
    /// <item>Parse (sin AutoCAD): bbox, capas_origen, espesores y tolerancia (en unidades del dibujo; por defecto 0,075 /
    /// 0,62 / 0,012 m convertidos con INSUNITS o unidades_modelo), CleanWrite.ReadCommon y DrawWrite.ReadCommon.</item>
    /// <item>Lectura con StartOpenCloseTransaction: LINE y LWPOLYLINE abiertas de 2 vértices en capas_origen con los dos
    /// extremos dentro de la bbox; HATCH de capas_origen dentro de la bbox si borrar_achurados_origen. Capas bloqueadas o
    /// xref con entidades a borrar → rechazo. Emparejado y unión en GeometryCore.</item>
    /// <item>Previsualización → inventario. Escritura → UndoGroup.Run: polilíneas cerradas (contorno y agujeros) y, con
    /// achurar, un HATCH por muro con los bucles de contorno y agujeros; luego borra lo indicado (revalidado por ObjectId).</item>
    /// </list>
    /// </summary>
    internal sealed class MurosDesdeLineasHandler : IToolHandler
    {
        private const string DefaultConceptMuros = "muros";
        private const string DefaultConceptAchurados = "achurados";
        private const string DefaultPatron = "ANSI31";
        private const string Solid = "SOLID";
        private const int MaxRegionesEnRespuesta = 50;

        private static readonly string[] Own =
        {
            "bbox", "capas_origen", "espesor_min", "espesor_max", "espesores", "tolerancia_espesor", "tolerancia_union", "unidades_modelo", "capa_achurado",
            "patron", "escala_patron", "angulo_patron", "achurar", "borrar_origen", "borrar_achurados_origen",
        };

        private static readonly string[] Allowed = PrimitiveSupport.Concat(PrimitiveSupport.Concat(Own, DrawWrite.CommonParams), CleanWrite.CommonParams);

        public string Name => "muros_desde_lineas";

        public string Description =>
            "PREVISUALIZA o ESCRIBE muros de un dibujo importado (p. ej. de Revit) que vienen como pares de LINE paralelas (las dos caras) " +
            "dentro de una bbox: empareja las caras por espesor y dibuja una polilínea cerrada por muro (con sus agujeros) con sombreado " +
            "opcional (ANSI31), en un solo paso de deshacer. RECOMIENDA pasar 'espesores' (lista discreta de espesores reales de la planta, " +
            "±1 cm): con solo el rango, un hueco de aire entre dos muros puede emparejarse como muro. Se priorizan pares cerrados " +
            "por tapas. tolerancia_union DESPLAZA geometría hasta esa distancia en juntas a tope o en T. " +
            "Sin confirmar: true solo PREVISUALIZA (muros, pares, líneas sin pareja, áreas y qué se borraría) y no modifica nada. " +
            "borrar_origen (líneas y tapas de muro) exige 'espesores'. borrar_achurados_origen borra achurados; ambos exigen confirmar: true.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""bbox"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 }, ""description"": ""Caja [[xmin, ymin], [xmax, ymax]] en WCS. Solo se usan segmentos con AMBOS extremos dentro de la caja; los que la cruzan no se tocan (quedan sin pareja)."" },
    ""capas_origen"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""description"": ""Capas de las LINE (y LWPOLYLINE abiertas de 2 vértices) de las caras del muro. Deben existir (NOT_FOUND)."" },
    ""espesor_min"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Espesor mínimo (distancia entre caras), en unidades del dibujo. Por defecto 0,075 m convertido a INSUNITS (o a unidades_modelo)."" },
    ""espesor_max"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Espesor máximo, en unidades del dibujo. Por defecto 0,62 m convertido."" },
    ""espesores"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 10, ""items"": { ""type"": ""number"", ""exclusiveMinimum"": 0 }, ""description"": ""Lista discreta de espesores reales de la planta (p. ej. [0.10, 0.15, 0.20]) en unidades del dibujo. Cada par debe caer a ±tolerancia_espesor de un valor de la lista y el rango espesor_min/espesor_max se ignora. OBLIGATORIO con borrar_origen: true (con el rango, huecos de aire y jambas pueden confundirse con muros). Sin espesores se avisa en las advertencias."" },
    ""tolerancia_espesor"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Tolerancia de cada valor de espesores, en unidades del dibujo. Por defecto ±1 cm convertido con las unidades; si no hay unidades conocidas, se deriva de tolerancia_union."" },
    ""tolerancia_union"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Tolerancia en unidades del dibujo (> 0). Además de unir, DESPLAZA GEOMETRÍA: un extremo de muro a menos de esta distancia de la cara perpendicular de otro muro se pega a ella (junta a tope o en T), hasta esa distancia. Por defecto 0,012 m convertido."" },
    ""unidades_modelo"": { ""type"": ""string"", ""enum"": [""m"", ""cm"", ""mm""], ""description"": ""Unidad de los valores por defecto. Con INSUNITS métrico debe coincidir (INVALID_PARAMS si no); con INSUNITS 0 o no métrico la declara."" },
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""capa_achurado"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Capa de los achurados. Por defecto el concepto 'achurados' de lps.json."" },
    ""patron"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""default"": ""ANSI31"", ""description"": ""SOLID o nombre de un patrón predefinido de AutoCAD."" },
    ""escala_patron"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 1, ""description"": ""Escala del patrón (se ignora con SOLID)."" },
    ""angulo_patron"": { ""type"": ""number"", ""default"": 0, ""description"": ""Ángulo del patrón en grados (se ignora con SOLID)."" },
    ""achurar"": { ""type"": ""boolean"", ""default"": true, ""description"": ""true = un HATCH por muro con el patrón indicado."" },
    ""borrar_origen"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true (con confirmar: true) borra las LINE emparejadas cuyo recorrido queda cubierto por los muros. Las que solo cubren en parte no se borran."" },
    ""borrar_achurados_origen"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true (con confirmar: true) borra los HATCH de capas_origen cuya caja está dentro de la bbox."" },
    ""espacio"": " + DrawWrite.SchemaEspacio + @",
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @",
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""bbox"", ""capas_origen""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""espacio"": { ""type"": ""string"" },
    ""capa"": { ""type"": ""string"" },
    ""capa_achurado"": { ""type"": [""string"", ""null""] },
    ""patron"": { ""type"": [""string"", ""null""] },
    ""metros_por_unidad"": { ""type"": [""number"", ""null""] },
    ""espesor_min"": { ""type"": ""number"" },
    ""espesor_max"": { ""type"": ""number"" },
    ""tolerancia_union"": { ""type"": ""number"" },
    ""n_lineas"": { ""type"": ""integer"" },
    ""n_caras"": { ""type"": ""integer"" },
    ""n_pares"": { ""type"": ""integer"" },
    ""n_muros"": { ""type"": ""integer"" },
    ""n_agujeros"": { ""type"": ""integer"" },
    ""area_total"": { ""type"": ""number"" },
    ""muros"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""sin_pareja"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""parciales"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""ignoradas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""borrar_lineas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""borrar_achurados"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""borrar_tapas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""borradas"": { ""type"": [""object"", ""null""] },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  }
}");

        /// <summary>Parámetros leídos (sin AutoCAD).</summary>
        private sealed class Params
        {
            public BBox2 Bbox { get; set; }

            public List<string> CapasOrigen { get; set; } = new List<string>();

            public double? EspesorMin { get; set; }

            public double? EspesorMax { get; set; }

            public double? Tolerancia { get; set; }

            /// <summary>Espesores discretos en unidades del dibujo (o null para usar el rango).</summary>
            public List<double>? Espesores { get; set; }

            /// <summary>Tolerancia de cada valor de <see cref="Espesores"/> en unidades del dibujo (null = ±1 cm derivado).</summary>
            public double? ToleranciaEspesor { get; set; }

            public string? UnidadesModelo { get; set; }

            public string? CapaAchurado { get; set; }

            public string Patron { get; set; } = DefaultPatron;

            public double EscalaPatron { get; set; } = 1.0;

            /// <summary>Grados normalizados a [0, 360).</summary>
            public double AnguloPatron { get; set; }

            public bool Achurar { get; set; }

            public bool BorrarOrigen { get; set; }

            public bool BorrarAchurados { get; set; }

            public CleanWrite.Common Modo { get; set; } = new CleanWrite.Common(false, null);

            public DrawWrite.Common Dw { get; set; } = new DrawWrite.Common(null, null, false, false);
        }

        /// <summary>Unidades resueltas: factor (m por unidad de dibujo), umbrales en unidades del dibujo y opciones de emparejado.</summary>
        private sealed class Units
        {
            public double? MetrosPorUnidad { get; set; }

            public WallPairOptions Opt { get; set; } = null!;
        }

        /// <summary>Lo que la lectura encuentra (sin escribir).</summary>
        private sealed class Lectura
        {
            public string Espacio { get; set; } = SpaceResolver.ModelName;

            public string CapaMuros { get; set; } = "";

            public string? CapaAchurados { get; set; }

            public List<WallLine> Lines { get; set; } = new List<WallLine>();

            public WallBuildResult Build { get; set; } = null!;

            public List<ObjectId> EraseLines { get; set; } = new List<ObjectId>();

            public List<string> EraseLineHandles { get; set; } = new List<string>();

            public List<string> EraseTapaHandles { get; set; } = new List<string>();

            public List<ObjectId> EraseHatches { get; set; } = new List<ObjectId>();

            public List<string> EraseHatchHandles { get; set; } = new List<string>();
        }

        /// <summary>espesores: 1–10 valores &gt; 0 en unidades del dibujo (lista discreta; ver descripción).</summary>
        private static List<double>? ReadEspesores(JsonElement p)
        {
            if (!p.TryGetProperty("espesores", out JsonElement el))
            {
                return null;
            }

            if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() < 1 || el.GetArrayLength() > 10)
            {
                throw ToolException.InvalidParams("espesores: debe ser un array de 1 a 10 números > 0");
            }

            var list = new List<double>(el.GetArrayLength());
            foreach (JsonElement item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double v) || !(v > 0) || !double.IsFinite(v))
                {
                    throw ToolException.InvalidParams("espesores: cada valor debe ser un número > 0");
                }

                list.Add(v);
            }

            return list;
        }

        /// <summary>Polilínea con ancho (constante o por vértice): no es una línea de muro, se excluye.</summary>
        private static bool TieneAncho(Polyline pl) =>
            pl.ConstantWidth != 0.0
            || pl.GetStartWidthAt(0) != 0.0 || pl.GetEndWidthAt(0) != 0.0
            || pl.GetStartWidthAt(1) != 0.0 || pl.GetEndWidthAt(1) != 0.0;

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            BBox2 bbox = ParamReader.OptionalBbox(p, "bbox") ?? throw ToolException.InvalidParams("bbox: obligatorio");
            List<string> capas = ParamReader.OptionalStringArray(p, "capas_origen", 1, 100, 1)
                ?? throw ToolException.InvalidParams("capas_origen: obligatorio");
            double? espesorMin = ParamReader.OptionalNumber(p, "espesor_min", 0, 1e9, exclusiveMin: true);
            double? espesorMax = ParamReader.OptionalNumber(p, "espesor_max", 0, 1e9, exclusiveMin: true);
            double? tolerancia = ParamReader.OptionalNumber(p, "tolerancia_union", 0, 1e9, exclusiveMin: true);
            List<double>? espesores = ReadEspesores(p);
            double? toleranciaEspesor = ParamReader.OptionalNumber(p, "tolerancia_espesor", 0, 1e9, exclusiveMin: true);
            bool borrarOrigenPedido = ParamReader.OptionalBool(p, "borrar_origen") ?? false;
            if (borrarOrigenPedido && espesores == null)
            {
                // Decisión de seguridad: con el rango, un hueco de aire o una jamba puede emparejarse como muro y se borraría.
                throw ToolException.InvalidParams(
                    "borrar_origen: exige 'espesores' (lista discreta de los espesores reales de la planta, p. ej. [0.10, 0.15, 0.20]). "
                    + "Con solo el rango espesor_min/espesor_max, un hueco de aire entre muros o una jamba de vano puede emparejarse como muro "
                    + "y borrarse. Sin borrar_origen el modo rango sirve para previsualizar o dibujar.");
            }

            string? unidades = ParamReader.OptionalEnum(p, "unidades_modelo", new[] { "m", "cm", "mm" });
            string? capaAch = ParamReader.OptionalString(p, "capa_achurado", 1, 255);
            DrawLayerFactory.Plan(capaAch, DefaultConceptAchurados);
            string patron = (ParamReader.OptionalString(p, "patron", 1, 255) ?? DefaultPatron).Trim();
            if (patron.Length == 0)
            {
                throw ToolException.InvalidParams("patron: debe tener al menos 1 carácter(es)");
            }

            double escala = ParamReader.OptionalNumber(p, "escala_patron", 0, 1e9, exclusiveMin: true) ?? 1.0;
            double angulo = Angles.Normalize360(ParamReader.OptionalNumber(p, "angulo_patron", -360000, 360000) ?? 0.0);
            bool achurar = ParamReader.OptionalBool(p, "achurar") ?? true;
            bool borrarOrigen = ParamReader.OptionalBool(p, "borrar_origen") ?? false;
            bool borrarAch = ParamReader.OptionalBool(p, "borrar_achurados_origen") ?? false;
            CleanWrite.Common modo = CleanWrite.ReadCommon(p);
            DrawWrite.Common dw = DrawWrite.ReadCommon(p, DefaultConceptMuros);

            return new Params
            {
                Bbox = bbox,
                CapasOrigen = capas,
                EspesorMin = espesorMin,
                EspesorMax = espesorMax,
                Tolerancia = tolerancia,
                Espesores = espesores,
                ToleranciaEspesor = toleranciaEspesor,
                UnidadesModelo = unidades,
                CapaAchurado = capaAch,
                Patron = patron,
                EscalaPatron = escala,
                AnguloPatron = angulo,
                Achurar = achurar,
                BorrarOrigen = borrarOrigen,
                BorrarAchurados = borrarAch,
                Modo = modo,
                Dw = dw,
            };
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(p.Modo.Confirmar, p.Modo.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = CleanWrite.ReadUnits(db);
            Units u = ResolveUnits(p, ins, warnings);
            if (mode == CleanWrite.Mode.Write)
            {
                DrawWrite.CheckUnits(db, p.Dw.AceptarSinUnidades, warnings); // R2: INSUNITS = 0 no se escribe sin aceptar_sin_unidades
            }

            Lectura lec;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                lec = Read(db, tr, p, u, mode, warnings);
                tr.Commit();
            }

            if (lec.Build.Regions.Count > 0)
            {
                PlacementCheck.Run(db, p.Dw.Espacio, new List<BBox2> { p.Bbox }, p.Dw.Forzar, "bbox", warnings);
            }

            if (mode == CleanWrite.Mode.Preview)
            {
                return BuildResponse(ctx.Pid, mode, ins, warnings, p, u, lec, new List<string>(), null, null);
            }

            int pid = ctx.Pid;
            return UndoGroup.Run(doc, d => Write(d, pid, ins, warnings, p, u, lec));
        }

        // ------------------------------------------------------------------ unidades

        /// <summary>
        /// Factor m/unidad (<see cref="ViewportPlanner.ResolveModelMeters"/>) solo si hace falta (algún valor por defecto o
        /// unidades_modelo). Sin factor conocido y con defaults → INVALID_PARAMS con el motivo. Los valores explícitos van en
        /// unidades del dibujo tal cual.
        /// </summary>
        private static Units ResolveUnits(Params p, int ins, List<string> warnings)
        {
            // Unidades exigidas solo si hay valores por defecto que convertir (o unidades_modelo).
            bool needFactor = p.EspesorMin == null || p.EspesorMax == null || p.Tolerancia == null || p.UnidadesModelo != null;
            double? factor = null;
            if (needFactor)
            {
                double? insMeters = UnitsScale.MetersPerUnit(ins);
                factor = ViewportPlanner.ResolveModelMeters(insMeters, p.UnidadesModelo, out string? problem);
                if (factor == null)
                {
                    throw ToolException.InvalidParams(problem ?? "unidades: no se pueden determinar las unidades del dibujo");
                }
            }
            else if (p.Espesores != null && p.ToleranciaEspesor == null)
            {
                // Sin unidades conocidas, la tolerancia de cada espesor se deriva de tolerancia_union (no se exigen unidades).
                factor = ViewportPlanner.ResolveModelMeters(UnitsScale.MetersPerUnit(ins), null, out _);
            }

            double espMin = p.EspesorMin ?? WallFromLines.DefaultEspesorMinM / factor!.Value;
            double espMax = p.EspesorMax ?? WallFromLines.DefaultEspesorMaxM / factor!.Value;
            double tol = p.Tolerancia ?? WallFromLines.DefaultToleranciaM / factor!.Value;
            double tolEsp = p.ToleranciaEspesor
                ?? (factor != null ? WallFromLines.DefaultToleranciaEspesorM / factor.Value : tol);
            WallPairOptions opt;
            try
            {
                opt = new WallPairOptions(
                    espMin, espMax, tol, WallPairOptions.DefaultAnguloGrados,
                    p.Espesores, p.Espesores == null ? 0.0 : tolEsp);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }

            return new Units { MetrosPorUnidad = factor, Opt = opt };
        }

        // ------------------------------------------------------------------ lectura

        private static Lectura Read(Database db, Transaction tr, Params p, Units u, CleanWrite.Mode mode, List<string> warnings)
        {
            SpaceResolver.ResolvedSpace space = SpaceResolver.Resolve(db, tr, p.Dw.Espacio);

            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            var missing = p.CapasOrigen.Where(n => !LayerExists(layers, n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
            {
                string detail = "capas_origen: capas inexistentes: " + string.Join(", ", missing);
                throw new ToolException(
                    ErrorCodes.NotFound,
                    detail,
                    new Dictionary<string, object?> { ["detalle"] = detail, ["capas"] = missing });
            }

            var origin = new HashSet<string>(p.CapasOrigen, StringComparer.OrdinalIgnoreCase);
            var lines = new List<WallLine>();
            var lineIds = new Dictionary<string, ObjectId>(StringComparer.Ordinal);
            var hatchIds = new List<ObjectId>();
            int anchoExcluidas = 0;
            int hatchCruzan = 0;
            foreach (ObjectId id in space.Btr)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                Entity? e = AuditSupport.Open(tr, id);
                if (e == null || !origin.Contains(AuditSupport.Layer(e)))
                {
                    continue;
                }

                string h = AuditSupport.Handle(id);
                if (e is Line ln)
                {
                    Point3d a = ln.StartPoint;
                    Point3d b = ln.EndPoint;
                    if (p.Bbox.Contains(a.X, a.Y) && p.Bbox.Contains(b.X, b.Y))
                    {
                        lines.Add(new WallLine(h, a.X, a.Y, b.X, b.Y));
                        lineIds[h] = id;
                    }
                }
                else if (e is Polyline pl && !pl.Closed && pl.NumberOfVertices == 2 && pl.GetBulgeAt(0) == 0.0)
                {
                    if (TieneAncho(pl))
                    {
                        anchoExcluidas++;
                        continue;
                    }

                    Point2d a = pl.GetPoint2dAt(0);
                    Point2d b = pl.GetPoint2dAt(1);
                    if (p.Bbox.Contains(a.X, a.Y) && p.Bbox.Contains(b.X, b.Y))
                    {
                        lines.Add(new WallLine(h, a.X, a.Y, b.X, b.Y));
                        lineIds[h] = id;
                    }
                }
                else if (e is Hatch)
                {
                    BBox2? hb = AcadUtil.TryBBox2(e);
                    if (hb == null)
                    {
                        continue;
                    }

                    bool dentro = p.Bbox.Contains(hb.Value.Xmin, hb.Value.Ymin) && p.Bbox.Contains(hb.Value.Xmax, hb.Value.Ymax);
                    if (dentro && p.BorrarAchurados)
                    {
                        hatchIds.Add(id);
                    }
                    else if (!dentro && p.Bbox.Intersects(hb.Value))
                    {
                        hatchCruzan++;
                    }
                }
            }

            if (anchoExcluidas > 0)
            {
                warnings.Add(
                    anchoExcluidas.ToString(CultureInfo.InvariantCulture)
                    + " polilínea(s) con ancho excluidas: no son líneas de cara de muro.");
            }

            if (hatchCruzan > 0)
            {
                warnings.Add(
                    hatchCruzan.ToString(CultureInfo.InvariantCulture)
                    + " HATCH de capas_origen cruzan la bbox sin estar contenidos: no se borran (posible achurado doble).");
            }

            if (lines.Count > WallFromLines.MaxLineas)
            {
                throw ToolException.InvalidParams(
                    "demasiadas líneas en la bbox (" + lines.Count.ToString(CultureInfo.InvariantCulture)
                    + "; límite " + WallFromLines.MaxLineas.ToString(CultureInfo.InvariantCulture) + "): reduzca la bbox o capas_origen");
            }

            WallBuildResult build;
            try
            {
                build = WallFromLines.Build(lines, u.Opt);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }

            warnings.AddRange(build.Pairing.Advertencias);

            if (p.BorrarOrigen && build.Union.ContornosAbiertos > 0)
            {
                // Con contornos abiertos la geometría de muros no es fiable: no se borra nada del plano.
                throw ToolException.InvalidParams(
                    build.Union.ContornosAbiertos.ToString(CultureInfo.InvariantCulture)
                    + " contorno(s) no cerraron: repita sin borrar_origen o ajuste tolerancia_union.");
            }

            if (build.Union.ContornosAbiertos > 0)
            {
                warnings.Add(
                    "muros_desde_lineas: " + build.Union.ContornosAbiertos.ToString(CultureInfo.InvariantCulture)
                    + " contorno(s) no cerraron por degeneración numérica y se descartaron; revise la tolerancia_union.");
            }

            var lec = new Lectura
            {
                Espacio = space.Name,
                Lines = lines,
                Build = build,
            };

            if (p.BorrarOrigen)
            {
                foreach (string h in build.Borrables)
                {
                    lec.EraseLines.Add(lineIds[h]);
                    lec.EraseLineHandles.Add(h);
                }

                // Tapas de muro: líneas perpendiculares de longitud ≈ espesor que unen las dos caras, cubiertas por los muros.
                foreach (string h in build.Tapas)
                {
                    lec.EraseLines.Add(lineIds[h]);
                    lec.EraseTapaHandles.Add(h);
                }
            }

            if (p.BorrarAchurados)
            {
                lec.EraseHatches.AddRange(hatchIds);
                lec.EraseHatchHandles.AddRange(hatchIds.Select(id => AuditSupport.Handle(id)));
            }

            RequireUnlockedToErase(tr, lec.EraseLines.Concat(lec.EraseHatches).ToList());

            DrawLayerFactory.Check(db, tr, p.Dw.Capa, DefaultConceptMuros, out _, mode == CleanWrite.Mode.Preview ? warnings : null);
            lec.CapaMuros = DrawLayerFactory.Plan(p.Dw.Capa, DefaultConceptMuros).Nombre;
            if (p.Achurar)
            {
                DrawLayerFactory.Check(db, tr, p.CapaAchurado, DefaultConceptAchurados, out _, mode == CleanWrite.Mode.Preview ? warnings : null);
                lec.CapaAchurados = DrawLayerFactory.Plan(p.CapaAchurado, DefaultConceptAchurados).Nombre;
            }

            return lec;
        }

        private static bool LayerExists(LayerTable layers, string name)
        {
            try
            {
                return layers.Has(name);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Entidades a borrar en capas bloqueadas o de xref → INVALID_PARAMS (como borrar_fuera_de, L7).</summary>
        private static void RequireUnlockedToErase(Transaction tr, List<ObjectId> ids)
        {
            var blocked = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in ids)
            {
                var e = (Entity)tr.GetObject(id, OpenMode.ForRead, false);
                var layer = (LayerTableRecord)tr.GetObject(e.LayerId, OpenMode.ForRead, false);
                if (layer.IsLocked || layer.IsDependent || layer.Name.IndexOf('|') >= 0)
                {
                    blocked.TryGetValue(layer.Name, out int n);
                    blocked[layer.Name] = n + 1;
                }
            }

            if (blocked.Count > 0)
            {
                string detalle = string.Join(", ", blocked.Select(kv => kv.Key + " (" + kv.Value.ToString(CultureInfo.InvariantCulture) + ")"));
                throw ToolException.InvalidParams(
                    "capas bloqueadas o de xref con entidades a borrar: " + detalle + "; desbloquéelas o quite borrar_origen/borrar_achurados_origen");
            }
        }

        // ------------------------------------------------------------------ escritura

        /// <summary>
        /// Dentro de UNA transacción (un CTRL+Z): capas (R3), polilíneas de contorno y agujeros, achurados y borrado de
        /// origen. Si algo lanza, la transacción se descarta y no queda nada.
        /// </summary>
        private static object Write(Document doc, int pid, int ins, List<string> warnings, Params p, Units u, Lectura lec)
        {
            Database db = doc.Database;
            var creados = new List<string>();
            int nBorradasLineas = 0;
            int nBorradasAch = 0;
            int omitidas = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                SpaceResolver.ResolvedSpace space = SpaceResolver.Resolve(db, tr, p.Dw.Espacio);
                BlockTableRecord btr = PrimitiveSupport.OpenForWrite(tr, space);
                DrawLayerFactory.Result capaMuros = DrawLayerFactory.Ensure(db, tr, p.Dw.Capa, DefaultConceptMuros, warnings);
                DrawLayerFactory.Result? capaAch = p.Achurar
                    ? DrawLayerFactory.Ensure(db, tr, p.CapaAchurado, DefaultConceptAchurados, warnings)
                    : null;

                foreach (WallRegion g in lec.Build.Regions)
                {
                    creados.Add(CreatePolyline(btr, tr, db, capaMuros.Id, g.Outer));
                    foreach (IReadOnlyList<(double x, double y)> hole in g.Holes)
                    {
                        creados.Add(CreatePolyline(btr, tr, db, capaMuros.Id, hole));
                    }
                }

                if (capaAch != null)
                {
                    foreach (WallRegion g in lec.Build.Regions)
                    {
                        creados.Add(CreateHatch(btr, tr, db, capaAch.Id, g, p));
                    }
                }

                foreach (ObjectId id in lec.EraseLines.Concat(lec.EraseHatches))
                {
                    if (id.IsNull || id.IsErased)
                    {
                        omitidas++;
                        continue;
                    }

                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity e) || e.OwnerId != btr.ObjectId)
                    {
                        omitidas++;
                        continue;
                    }

                    bool esHatch = e is Hatch;
                    e.Erase();
                    if (esHatch)
                    {
                        nBorradasAch++;
                    }
                    else
                    {
                        nBorradasLineas++;
                    }
                }

                tr.Commit();
            }

            if (omitidas > 0)
            {
                warnings.Add(
                    omitidas.ToString(CultureInfo.InvariantCulture)
                    + " entidad(es) previstas para borrar ya no existían o ya no estaban en el espacio '" + lec.Espacio + "' y no se borraron.");
            }

            PluginLog.Info(string.Format(
                CultureInfo.InvariantCulture,
                "muros_desde_lineas: {0} muro(s), {1} achurado(s), {2} línea(s) y {3} achurado(s) de origen borrados",
                lec.Build.Regions.Count,
                creados.Count - lec.Build.Regions.Sum(g => 1 + g.Holes.Count),
                nBorradasLineas,
                nBorradasAch));

            return BuildResponse(pid, CleanWrite.Mode.Write, ins, warnings, p, u, lec, creados, nBorradasLineas, nBorradasAch);
        }

        private static string CreatePolyline(BlockTableRecord btr, Transaction tr, Database db, ObjectId layerId, IReadOnlyList<(double x, double y)> v)
        {
            var pl = new Polyline(v.Count);
            DrawWrite.ApplyByLayer(pl, db, layerId);
            for (int i = 0; i < v.Count; i++)
            {
                pl.AddVertexAt(i, new Point2d(v[i].x, v[i].y), 0.0, 0.0, 0.0);
            }

            pl.Closed = true;
            return DrawWrite.Append(btr, tr, pl);
        }

        /// <summary>
        /// Un HATCH por muro. Orden de API como <see cref="HatchHandler"/>: AppendEntity → ByLayer → PatternScale →
        /// SetHatchPattern → (PatternAngle → SetHatchPattern) → AppendLoop por contorno y agujero → EvaluateHatch.
        /// </summary>
        private static string CreateHatch(BlockTableRecord btr, Transaction tr, Database db, ObjectId layerId, WallRegion g, Params p)
        {
            bool solid = string.Equals(p.Patron, Solid, StringComparison.OrdinalIgnoreCase);
            string patronName = solid ? Solid : p.Patron.ToUpperInvariant();
            var hatch = new Hatch();
            string handle = DrawWrite.Append(btr, tr, hatch);
            DrawWrite.ApplyByLayer(hatch, db, layerId);
            if (!solid)
            {
                hatch.PatternScale = p.EscalaPatron;
            }

            try
            {
                hatch.SetHatchPattern(HatchPatternType.PreDefined, patronName);
            }
            catch (Exception ex) when (!(ex is ToolException))
            {
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "El patrón de sombreado '" + p.Patron + "' no existe.",
                    new Dictionary<string, object?> { ["patron"] = p.Patron, ["detalle"] = ex.GetType().Name + ": " + ex.Message });
            }

            if (!solid && p.AnguloPatron != 0.0)
            {
                hatch.PatternAngle = Angles.ToRadians(p.AnguloPatron);
                hatch.SetHatchPattern(HatchPatternType.PreDefined, patronName);
            }

            AppendLoop(hatch, g.Outer, interior: false);
            foreach (IReadOnlyList<(double x, double y)> hole in g.Holes)
            {
                AppendLoop(hatch, hole, interior: true);
            }

            hatch.EvaluateHatch(true);
            return handle;
        }

        /// <summary>Contorno exterior: External; agujeros: Default (interior de la isla), como exige la API de islas.</summary>
        private static void AppendLoop(Hatch hatch, IReadOnlyList<(double x, double y)> loop, bool interior)
        {
            var pts = new Point2dCollection();
            var bulges = new DoubleCollection();
            foreach ((double x, double y) v in loop)
            {
                pts.Add(new Point2d(v.x, v.y));
                bulges.Add(0.0);
            }

            // Cierre explícito del bucle: se repite el primer vértice (como HatchHandler).
            pts.Add(new Point2d(loop[0].x, loop[0].y));
            bulges.Add(0.0);
            HatchLoopTypes tipo = interior ? HatchLoopTypes.Default | HatchLoopTypes.Polyline : HatchLoopTypes.External | HatchLoopTypes.Polyline;
            hatch.AppendLoop(tipo, pts, bulges);
        }

        // ------------------------------------------------------------------ respuesta

        private static object BuildResponse(
            int pid,
            CleanWrite.Mode mode,
            int ins,
            List<string> warnings,
            Params p,
            Units u,
            Lectura lec,
            List<string> creados,
            int? borradasLineas,
            int? borradasAch)
        {
            Dictionary<string, object?> r = CleanWrite.Response(pid, mode, ins, warnings);
            IReadOnlyList<WallRegion> regiones = lec.Build.Regions;
            r["espacio"] = lec.Espacio;
            r["capa"] = lec.CapaMuros;
            r["capa_achurado"] = p.Achurar ? lec.CapaAchurados : null;
            r["patron"] = p.Achurar ? p.Patron : null;
            r["metros_por_unidad"] = u.MetrosPorUnidad;
            r["espesor_min"] = u.Opt.EspesorMin;
            r["espesor_max"] = u.Opt.EspesorMax;
            r["tolerancia_union"] = u.Opt.Tolerancia;
            r["n_lineas"] = lec.Lines.Count;
            r["n_caras"] = lec.Build.Pairing.Rects.Count;
            r["n_pares"] = lec.Build.Pairing.Pares;
            r["n_muros"] = regiones.Count;
            r["n_agujeros"] = regiones.Sum(g => g.Holes.Count);
            r["area_total"] = regiones.Sum(g => g.Area);
            r["muros"] = regiones.Take(MaxRegionesEnRespuesta).Select(g => (object)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["n_vertices"] = g.Outer.Count,
                ["area"] = g.Area,
                ["agujeros"] = g.Holes.Count,
                ["bbox"] = new[] { new[] { g.MinX, g.MinY }, new[] { g.MaxX, g.MaxY } },
            }).ToList();
            r["sin_pareja"] = lec.Build.Pairing.SinPareja;
            r["parciales"] = lec.Build.Parciales;
            r["ignoradas"] = lec.Build.Pairing.Ignoradas;
            r["borrar_lineas"] = lec.EraseLineHandles;
            r["borrar_achurados"] = lec.EraseHatchHandles;
            r["borrar_tapas"] = lec.EraseTapaHandles;
            r["borradas"] = borradasLineas == null
                ? null
                : new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["lineas"] = borradasLineas.Value,
                    ["achurados"] = borradasAch ?? 0,
                };
            r["handles"] = creados;
            return r;
        }
    }
}
