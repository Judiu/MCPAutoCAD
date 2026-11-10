using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// muro (protocol.md §5E.3.1): contorno de un muro con esquinas a inglete a partir de su eje (PuntoRef[] o handle de
    /// LINE/LWPOLYLINE/POLYLINE 2D), espesor y justificación. Geometría en <see cref="WallOutline.Build"/> (GeometryCore).
    /// Representación 'contorno' (defecto): una LWPOLYLINE cerrada (anillo: exterior + interior) + HATCH opcional asociado;
    /// 'caras': dos LWPOLYLINE (caras) + LINE de tapa en cada extremo (sin tapas si cerrado). Concepto 'muros'.
    /// </summary>
    internal sealed class MuroHandler : GeneratorBase
    {
        private const string Contorno = "contorno";
        private const string Caras = "caras";

        private static readonly string[] Justificaciones = { "centro", "izquierda", "derecha" };
        private static readonly string[] Representaciones = { Contorno, Caras };
        private static readonly string[] AchuradoProps = { "patron", "escala", "angulo" };
        private static readonly string[] AllowedNames = BuildAllowed();

        public override string Name => "muro";

        public override string Description =>
            "ESCRIBE un muro arquitectónico como polilínea cerrada (contorno con esquinas a inglete) en la capa MUROS a partir de su eje (PuntoRef[] o handle de línea/polilínea), espesor y justificación (centro|izquierda|derecha); achurado opcional en Achurados. Rechaza espesores mayores que los tramos, ejes autointersecados e ingletes imposibles. Con preview: true solo calcula y devuelve números + preview_id (dibujado en MCP-PREVIEW). No une con muros existentes.";

        public override JsonElement InputSchema { get; } = GenSchema.Build(
            @"    ""eje"": { ""description"": ""Eje del muro, en orden: array de 2 a 200 PuntoRef, o el handle de una LINE / LWPOLYLINE / POLYLINE 2D existente (se toman sus vértices; una polilínea cerrada implica cerrado: true)."", ""oneOf"": [
      { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 200, ""items"": " + GenSchema.SchemaPuntoRef + @" },
      " + ParamReader.SchemaHandle + @"
    ] },
    ""espesor"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Espesor del muro (> 0), en unidades de dibujo."" },
    ""justificacion"": { ""type"": ""string"", ""enum"": [""centro"", ""izquierda"", ""derecha""], ""default"": ""centro"", ""description"": ""Posición del eje mirando en el sentido de recorrido: izquierda = el muro queda a la izquierda del eje; derecha = a la derecha; centro = mitad y mitad."" },
    ""cerrado"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = el último vértice se une al primero (anillo); exige al menos 3 vértices."" },
    ""representacion"": { ""type"": ""string"", ""enum"": [""contorno"", ""caras""], ""default"": ""contorno"", ""description"": ""contorno = una LWPOLYLINE cerrada (anillo: exterior e interior); caras = dos LWPOLYLINE (caras) + LINE de tapa en cada extremo (sin tapas si cerrado)."" },
    ""achurado"": { ""type"": ""object"", ""description"": ""Sombreado asociativo del contorno (solo con representacion 'contorno'), en capa_achurado."", ""properties"": {
      ""patron"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""default"": ""SOLID"" },
      ""escala"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 1 },
      ""angulo"": { ""type"": ""number"", ""default"": 0 }
    }, ""additionalProperties"": false },
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""capa_achurado"": " + DrawWrite.SchemaCapa + @",
    ""limite_inglete"": { ""type"": ""number"", ""minimum"": 1, ""default"": 4, ""description"": ""El vértice de inglete de cada cara no puede alejarse del vértice de inglete de la línea media del muro más de limite_inglete × espesor / 2 (no depende de la justificación; con 4, ángulos interiores de al menos ~29°)."" }",
            "eje", "espesor");

        public override JsonElement? OutputSchema { get; } = GenSchema.Output(
            @"        ""longitud_eje"": { ""type"": ""number"" },
        ""longitud_contorno"": { ""type"": ""number"" },
        ""area"": { ""type"": ""number"" },
        ""espesor"": { ""type"": ""number"" },
        ""n_tramos"": { ""type"": ""integer"" },
        ""justificacion"": { ""type"": ""string"" },
        ""representacion"": { ""type"": ""string"" },
        ""cerrado"": { ""type"": ""boolean"" }");

        protected override IReadOnlyCollection<string> Allowed => AllowedNames;

        protected override string? DefaultConcept => "muros";

        private static string[] BuildAllowed()
        {
            var list = new List<string> { "eje", "espesor", "justificacion", "cerrado", "representacion", "achurado", "capa", "capa_achurado", "limite_inglete" };
            list.AddRange(GenSchema.CommonParams);
            return list.ToArray();
        }

        private sealed class Hatching
        {
            public Hatching(string patron, double escala, double angulo)
            {
                Patron = patron;
                Escala = escala;
                Angulo = angulo;
            }

            public string Patron { get; }
            public double Escala { get; }
            public double Angulo { get; }
        }

        private sealed class Params
        {
            public Params(List<PointRef>? eje, string? ejeHandle, double espesor, string justificacion, bool cerrado, string representacion, Hatching? achurado, string? capa, string? capaAchurado, double limiteInglete)
            {
                Eje = eje;
                EjeHandle = ejeHandle;
                Espesor = espesor;
                Justificacion = justificacion;
                Cerrado = cerrado;
                Representacion = representacion;
                Achurado = achurado;
                Capa = capa;
                CapaAchurado = capaAchurado;
                LimiteInglete = limiteInglete;
            }

            /// <summary>Eje como PuntoRef (excluyente con <see cref="EjeHandle"/>).</summary>
            public List<PointRef>? Eje { get; }

            /// <summary>Handle (mayúsculas) de la LINE/polilínea del eje.</summary>
            public string? EjeHandle { get; }
            public double Espesor { get; }
            public string Justificacion { get; }
            public bool Cerrado { get; }
            public string Representacion { get; }
            public Hatching? Achurado { get; }
            public string? Capa { get; }
            public string? CapaAchurado { get; }
            public double LimiteInglete { get; }
        }

        protected override object ParseOwn(JsonElement p)
        {
            if (!p.TryGetProperty("eje", out JsonElement ejeEl))
            {
                throw ToolException.InvalidParams("eje: obligatorio");
            }

            List<PointRef>? eje = null;
            string? ejeHandle = null;
            if (ejeEl.ValueKind == JsonValueKind.String)
            {
                ejeHandle = AxisReader.ReadHandle(ejeEl, "eje");
            }
            else if (ejeEl.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    eje = PointRef.ParseList(ejeEl, "eje", 2, 200);
                }
                catch (ArgumentException ex)
                {
                    throw ToolException.InvalidParams(ex.Message);
                }
            }
            else
            {
                throw ToolException.InvalidParams("eje: debe ser un array de 2 a 200 puntos o el handle de una línea/polilínea");
            }

            double espesor = ParamReader.RequiredNumber(p, "espesor", 0, double.PositiveInfinity, exclusiveMin: true);
            string justificacion = ParamReader.OptionalEnum(p, "justificacion", Justificaciones) ?? "centro";
            bool cerrado = ParamReader.OptionalBool(p, "cerrado") ?? false;
            string representacion = ParamReader.OptionalEnum(p, "representacion", Representaciones) ?? Contorno;

            Hatching? achurado = null;
            if (p.TryGetProperty("achurado", out JsonElement ach))
            {
                if (ach.ValueKind != JsonValueKind.Object)
                {
                    throw ToolException.InvalidParams("achurado: debe ser un objeto");
                }

                ParamReader.RejectUnknown(ach, AchuradoProps, "achurado.");
                string patron = (ParamReader.OptionalString(ach, "patron", 1, 255) ?? "SOLID").Trim();
                if (patron.Length == 0)
                {
                    throw ToolException.InvalidParams("achurado.patron: debe tener al menos 1 carácter(es)");
                }

                double? escala = null;
                if (ach.TryGetProperty("escala", out JsonElement escEl))
                {
                    if (escEl.ValueKind != JsonValueKind.Number || !escEl.TryGetDouble(out double e) || !double.IsFinite(e) || e <= 0)
                    {
                        throw ToolException.InvalidParams("achurado.escala: debe ser un número mayor que 0");
                    }

                    escala = e;
                }

                double angulo = 0.0;
                if (ach.TryGetProperty("angulo", out JsonElement angEl))
                {
                    if (angEl.ValueKind != JsonValueKind.Number || !angEl.TryGetDouble(out double a) || !double.IsFinite(a))
                    {
                        throw ToolException.InvalidParams("achurado.angulo: debe ser un número finito");
                    }

                    angulo = a;
                }

                achurado = new Hatching(patron, escala ?? 1.0, Angles.Normalize360(angulo));
                if (representacion != Contorno)
                {
                    throw ToolException.InvalidParams("achurado: solo con representacion 'contorno'");
                }
            }

            string? capa = ParamReader.OptionalString(p, "capa", 1, 255);
            if (capa != null)
            {
                DrawLayerFactory.Plan(capa, "muros");
            }

            string? capaAchurado = ParamReader.OptionalString(p, "capa_achurado", 1, 255);
            if (capaAchurado != null)
            {
                DrawLayerFactory.Plan(capaAchurado, "achurados");
            }

            double limite = WallOutline.DefaultMiterLimit;
            if (p.TryGetProperty("limite_inglete", out JsonElement limEl))
            {
                if (limEl.ValueKind != JsonValueKind.Number || !limEl.TryGetDouble(out limite) || !double.IsFinite(limite) || limite < 1)
                {
                    throw ToolException.InvalidParams("limite_inglete: debe ser un número mayor o igual que 1");
                }
            }
            return new Params(eje, ejeHandle, espesor, justificacion, cerrado, representacion, achurado, capa, capaAchurado, limite);
        }

        protected override IReadOnlyList<(PointRef Ref, string Path)> PointRefs(object own)
        {
            var p = (Params)own;
            var list = new List<(PointRef Ref, string Path)>();
            if (p.Eje != null)
            {
                for (int i = 0; i < p.Eje.Count; i++)
                {
                    list.Add((p.Eje[i], "eje[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]"));
                }
            }

            return list;
        }

        protected override GeneratedGeometry Plan(object own, IReadOnlyList<ResolvedPoint> puntos, Database db, Transaction tr, List<string> advertencias)
        {
            var p = (Params)own;
            bool cerrado = p.Cerrado;
            List<(double x, double y)> eje;
            AnchorFingerprint? huellaEje = null;
            if (p.EjeHandle != null)
            {
                Entity e = PointRefResolver.Require(db, tr, p.EjeHandle);
                huellaEje = PointRefResolver.Fingerprint(e, p.EjeHandle);
                AxisReader.Vertices? read = AxisReader.Read(e, tr);
                if (read == null)
                {
                    throw ToolException.InvalidParams(
                        "eje: la entidad " + p.EjeHandle + " (" + AcadUtil.DxfName(e) + ") no es una línea ni una polilínea",
                        new Dictionary<string, object?> { ["handle"] = p.EjeHandle });
                }

                if (read.TieneArcos)
                {
                    advertencias.Add("eje: la polilínea " + p.EjeHandle + " tiene arcos; se usaron las cuerdas.");
                }

                // Vértices consecutivos duplicados de una polilínea EXISTENTE: se colapsan con advertencia (con PuntoRef[]
                // siguen siendo error, "eje[<i>]: vértice duplicado con el anterior").
                eje = PolygonMetrics.CollapseConsecutiveDuplicates(read.Puntos, out int colapsados, lazoCerrado: read.Cerrada || cerrado);
                if (colapsados > 0)
                {
                    advertencias.Add(PolygonMetrics.CollapsedAxisDuplicatesWarning(p.EjeHandle, colapsados));
                }

                cerrado = cerrado || read.Cerrada;
            }
            else
            {
                eje = new List<(double x, double y)>(puntos.Count);
                foreach (ResolvedPoint rp in puntos)
                {
                    eje.Add((rp.Punto.X, rp.Punto.Y));
                }
            }

            WallJustification just = p.Justificacion switch
            {
                "izquierda" => WallJustification.Izquierda,
                "derecha" => WallJustification.Derecha,
                _ => WallJustification.Centro,
            };

            WallResult w;
            try
            {
                w = WallOutline.Build(eje, p.Espesor, just, cerrado, p.LimiteInglete);
            }
            catch (WallGeometryException ex)
            {
                throw ex.EsParametro ? ToolException.InvalidParams(ex.Message) : ToolException.InvalidGeometry(ex.Message);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }

            bool anillo = w.Interior != null;
            var entidades = new List<PlannedEntity>();
            if (p.Representacion == Contorno)
            {
                entidades.Add(new PlannedPolyline(w.Contorno, true, null, 0.0, "muros", p.Capa));
                var indices = new List<int> { 0 };
                if (anillo)
                {
                    entidades.Add(new PlannedPolyline(w.Interior!, true, null, 0.0, "muros", p.Capa));
                    indices.Add(1);
                }

                if (p.Achurado != null)
                {
                    entidades.Add(new PlannedHatch(indices, p.Achurado.Patron, p.Achurado.Escala, p.Achurado.Angulo, "achurados", p.CapaAchurado));
                }
            }
            else
            {
                entidades.Add(new PlannedPolyline(w.CaraIzquierda, anillo, null, 0.0, "muros", p.Capa));
                entidades.Add(new PlannedPolyline(w.CaraDerecha, anillo, null, 0.0, "muros", p.Capa));
                if (!anillo && w.CaraIzquierda.Count > 0 && w.CaraDerecha.Count > 0)
                {
                    (double x, double y) li = w.CaraIzquierda[0], ri = w.CaraDerecha[0];
                    (double x, double y) lf = w.CaraIzquierda[w.CaraIzquierda.Count - 1], rf = w.CaraDerecha[w.CaraDerecha.Count - 1];
                    entidades.Add(new PlannedLine(new Point3(li.x, li.y, 0), new Point3(ri.x, ri.y, 0), "muros", p.Capa));
                    entidades.Add(new PlannedLine(new Point3(lf.x, lf.y, 0), new Point3(rf.x, rf.y, 0), "muros", p.Capa));
                }
            }

            var numeros = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["longitud_eje"] = w.LongitudEje,
                ["longitud_contorno"] = w.LongitudContorno,
                ["area"] = w.Area,
                ["espesor"] = p.Espesor,
                ["n_tramos"] = w.NTramos,
                ["justificacion"] = p.Justificacion,
                ["representacion"] = p.Representacion,
                ["cerrado"] = anillo,
            };
            var geo = new GeneratedGeometry(entidades, numeros);
            if (huellaEje != null)
            {
                geo.AnclasExtra.Add(huellaEje);
            }

            return geo;
        }
    }

    /// <summary>
    /// Lectura de los vértices (planta XY, WCS) de una entidad existente usada como eje de muro o contorno de recinto:
    /// LINE (2 vértices), LWPOLYLINE (GetPoint3dAt/GetBulgeAt) y POLYLINE 2D (Vertex2d; se omiten los vértices de control
    /// de spline). Una polilínea abierta cuyo último vértice coincide con el primero (≥ 4 vértices, tolerancia 1e-9) se
    /// trata como cerrada sin repetir el vértice; una cerrada que lo repite también lo pierde. Solo lectura.
    /// </summary>
    internal static class AxisReader
    {
        internal sealed class Vertices
        {
            public Vertices(List<(double x, double y)> puntos, bool cerrada, bool tieneArcos, double z)
            {
                Puntos = puntos;
                Cerrada = cerrada;
                TieneArcos = tieneArcos;
                Z = z;
            }

            public List<(double x, double y)> Puntos { get; }

            /// <summary>Closed, o primer vértice = último.</summary>
            public bool Cerrada { get; }

            /// <summary>Algún tramo efectivo con bulge ≠ 0.</summary>
            public bool TieneArcos { get; }

            /// <summary>Z (WCS) del primer vértice.</summary>
            public double Z { get; }
        }

        private const double Tol = 1e-9;

        /// <summary>Handle en una cadena (patrón ^[0-9A-Fa-f]{1,16}$): "&lt;campo&gt;: formato de handle inválido". Mayúsculas.</summary>
        public static string ReadHandle(JsonElement el, string campo)
        {
            string v = el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
            bool ok = v.Length >= 1 && v.Length <= 16;
            foreach (char c in v)
            {
                ok &= (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
            }

            if (!ok)
            {
                throw ToolException.InvalidParams(campo + ": formato de handle inválido");
            }

            return v.ToUpperInvariant();
        }

        /// <summary>Vértices de LINE / LWPOLYLINE / POLYLINE 2D; null si la entidad es de otro tipo.</summary>
        public static Vertices? Read(Entity e, Transaction tr)
        {
            var pts = new List<(double x, double y)>();
            var bulges = new List<double>();
            bool closed;
            double z = 0.0;
            switch (e)
            {
                case Line ln:
                    pts.Add((ln.StartPoint.X, ln.StartPoint.Y));
                    pts.Add((ln.EndPoint.X, ln.EndPoint.Y));
                    bulges.Add(0.0);
                    bulges.Add(0.0);
                    z = ln.StartPoint.Z;
                    closed = false;
                    break;
                case Polyline pl:
                    for (int i = 0; i < pl.NumberOfVertices; i++)
                    {
                        Point3d v = pl.GetPoint3dAt(i);
                        if (i == 0)
                        {
                            z = v.Z;
                        }

                        pts.Add((v.X, v.Y));
                        bulges.Add(pl.GetBulgeAt(i));
                    }

                    closed = pl.Closed;
                    break;
                case Polyline2d p2:
                    foreach (ObjectId vid in p2)
                    {
                        if (!(tr.GetObject(vid, OpenMode.ForRead, false) is Vertex2d v2) || v2.VertexType == Vertex2dType.SplineControlVertex)
                        {
                            continue;
                        }

                        Point3d pos;
                        try
                        {
                            pos = p2.VertexPosition(v2);
                        }
                        catch (Exception)
                        {
                            pos = v2.Position;
                        }

                        if (pts.Count == 0)
                        {
                            z = pos.Z;
                        }

                        pts.Add((pos.X, pos.Y));
                        bulges.Add(v2.Bulge);
                    }

                    closed = p2.Closed;
                    break;
                default:
                    return null;
            }

            int n = pts.Count;
            if (n >= 2 && Same(pts[0], pts[n - 1]) && (closed || n >= 4))
            {
                pts.RemoveAt(n - 1);
                bulges.RemoveAt(n - 1);
                closed = true;
            }

            // Tramos efectivos: k → k+1 (y el de cierre si es cerrada).
            bool arcs = false;
            int segs = closed ? pts.Count : pts.Count - 1;
            for (int k = 0; k < segs && k < bulges.Count; k++)
            {
                if (bulges[k] != 0.0)
                {
                    arcs = true;
                    break;
                }
            }

            return new Vertices(pts, closed, arcs, z);
        }

        private static bool Same((double x, double y) a, (double x, double y) b) =>
            Math.Abs(a.x - b.x) <= Tol && Math.Abs(a.y - b.y) <= Tol;
    }
}
