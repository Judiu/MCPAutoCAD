using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// recinto (protocol.md §5E.3.3): polilínea cerrada (si se dan vértices PuntoRef[]) o solo la etiqueta (si se da el
    /// handle de una LWPOLYLINE/POLYLINE 2D cerrada existente) + MTEXT 'medio_centro' con el nombre y el área en m² con
    /// coma decimal, en un punto interior (centroide si cae dentro; si no, polo de inaccesibilidad). Contorno por defecto
    /// en MCP-DIBUJO (la oficina no dibuja ese contorno); etiqueta en el concepto 'textos'.
    /// </summary>
    internal sealed class RecintoHandler : GeneratorBase
    {
        private const double MinArea = 1e-9;

        private static readonly string[] AllowedNames = BuildAllowed();

        public override string Name => "recinto";

        public override string Description =>
            "ESCRIBE un recinto: polilínea cerrada (si se dan vértices PuntoRef[]) o solo la etiqueta (si se da el handle de una polilínea cerrada existente) + MTEXT centrado con el nombre y el área en m² con coma decimal, en un punto interior del polígono. Valida cierre, autointersección y área > 0. Con preview: true solo calcula y devuelve números + preview_id.";

        public override JsonElement InputSchema { get; } = GenSchema.Build(
            @"    ""contorno"": { ""description"": ""Array de 3 a 200 PuntoRef (se crea la polilínea cerrada, sin repetir el primer vértice al final) o el handle de una LWPOLYLINE / POLYLINE 2D cerrada existente (solo se crea la etiqueta)."", ""oneOf"": [
      { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 200, ""items"": " + GenSchema.SchemaPuntoRef + @" },
      " + ParamReader.SchemaHandle + @"
    ] },
    ""nombre"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 100, ""description"": ""Nombre del recinto (literal; los saltos de línea se sustituyen por espacios)."" },
    ""altura_texto"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Altura del MTEXT de la etiqueta (> 0)."" },
    ""decimales"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 3, ""default"": 2, ""description"": ""Decimales del área."" },
    ""etiqueta_en"": " + GenSchema.SchemaPuntoRef + @",
    ""ancho_texto"": { ""type"": ""number"", ""minimum"": 0, ""default"": 0, ""description"": ""Ancho de ajuste del MTEXT (0 = sin ajuste)."" },
    ""estilo"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Estilo de texto existente; por defecto el actual."" },
    ""capa_contorno"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Capa de la polilínea (concepto de lps.json, capa existente o capa del estándar / MCP-*). Por defecto MCP-DIBUJO."" },
    ""capa_texto"": " + DrawWrite.SchemaCapa,
            "contorno", "nombre", "altura_texto");

        public override JsonElement? OutputSchema { get; } = GenSchema.Output(
            @"        ""area"": { ""type"": ""number"" },
        ""perimetro"": { ""type"": ""number"" },
        ""centroide"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
        ""punto_etiqueta"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
        ""etiqueta_en_centroide"": { ""type"": ""boolean"" },
        ""n_vertices"": { ""type"": ""integer"" },
        ""etiqueta"": { ""type"": ""string"" }");

        protected override IReadOnlyCollection<string> Allowed => AllowedNames;

        protected override string? DefaultConcept => null;

        private static string[] BuildAllowed()
        {
            var list = new List<string> { "contorno", "nombre", "altura_texto", "decimales", "etiqueta_en", "ancho_texto", "estilo", "capa_contorno", "capa_texto" };
            list.AddRange(GenSchema.CommonParams);
            return list.ToArray();
        }

        private sealed class Params
        {
            public Params(List<PointRef>? contorno, string? handle, string nombre, bool nombreConSaltos, double altura, int decimales, PointRef? etiquetaEn, double ancho, string? estilo, string? capaContorno, string? capaTexto)
            {
                Contorno = contorno;
                Handle = handle;
                Nombre = nombre;
                NombreConSaltos = nombreConSaltos;
                Altura = altura;
                Decimales = decimales;
                EtiquetaEn = etiquetaEn;
                Ancho = ancho;
                Estilo = estilo;
                CapaContorno = capaContorno;
                CapaTexto = capaTexto;
            }

            public List<PointRef>? Contorno { get; }

            /// <summary>Handle (mayúsculas) de la polilínea cerrada existente.</summary>
            public string? Handle { get; }

            /// <summary>Nombre ya sin saltos de línea.</summary>
            public string Nombre { get; }
            public bool NombreConSaltos { get; }
            public double Altura { get; }
            public int Decimales { get; }
            public PointRef? EtiquetaEn { get; }
            public double Ancho { get; }
            public string? Estilo { get; }
            public string? CapaContorno { get; }
            public string? CapaTexto { get; }
        }

        protected override object ParseOwn(JsonElement p)
        {
            if (!p.TryGetProperty("contorno", out JsonElement cEl))
            {
                throw ToolException.InvalidParams("contorno: obligatorio");
            }

            List<PointRef>? contorno = null;
            string? handle = null;
            if (cEl.ValueKind == JsonValueKind.String)
            {
                handle = AxisReader.ReadHandle(cEl, "contorno");
            }
            else if (cEl.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    contorno = PointRef.ParseList(cEl, "contorno", 3, 200);
                }
                catch (ArgumentException ex)
                {
                    throw ToolException.InvalidParams(ex.Message);
                }
            }
            else
            {
                throw ToolException.InvalidParams("contorno: debe ser un array de 3 a 200 puntos o el handle de una polilínea cerrada");
            }

            string nombre = TextEscape.CollapseNewlines(ParamReader.RequiredText(p, "nombre", 1, 100), out bool saltos);
            double altura = ParamReader.RequiredNumber(p, "altura_texto", 0, double.PositiveInfinity, exclusiveMin: true);
            int decimales = ParamReader.OptionalInt(p, "decimales", 0, 3) ?? 2;
            PointRef? etiquetaEn = GenSchema.OptionalPointRef(p, "etiqueta_en");
            double ancho = PrimitiveSupport.OptionalNonNegative(p, "ancho_texto") ?? 0.0;
            string? estilo = ParamReader.OptionalString(p, "estilo", 1, 255);
            string? capaContorno = ParamReader.OptionalString(p, "capa_contorno", 1, 255);
            if (capaContorno != null)
            {
                DrawLayerFactory.Plan(capaContorno, null);
            }

            string? capaTexto = ParamReader.OptionalString(p, "capa_texto", 1, 255);
            if (capaTexto != null)
            {
                DrawLayerFactory.Plan(capaTexto, "textos");
            }

            return new Params(contorno, handle, nombre, saltos, altura, decimales, etiquetaEn, ancho, estilo, capaContorno, capaTexto);
        }

        protected override IReadOnlyList<(PointRef Ref, string Path)> PointRefs(object own)
        {
            var p = (Params)own;
            var list = new List<(PointRef Ref, string Path)>();
            if (p.Contorno != null)
            {
                for (int i = 0; i < p.Contorno.Count; i++)
                {
                    list.Add((p.Contorno[i], "contorno[" + i.ToString(CultureInfo.InvariantCulture) + "]"));
                }
            }

            if (p.EtiquetaEn != null)
            {
                list.Add((p.EtiquetaEn, "etiqueta_en"));
            }

            return list;
        }

        protected override GeneratedGeometry Plan(object own, IReadOnlyList<ResolvedPoint> puntos, Database db, Transaction tr, List<string> advertencias)
        {
            var p = (Params)own;
            if (p.NombreConSaltos)
            {
                advertencias.Add("nombre: los saltos de línea se sustituyeron por espacios.");
            }

            // Estilo inexistente → NOT_FOUND antes de escribir nada.
            if (p.Estilo != null)
            {
                PrimitiveSupport.ResolveTextStyle(db, tr, p.Estilo);
            }

            List<(double x, double y)> v;
            double z = 0.0;
            AnchorFingerprint? huellaContorno = null;
            if (p.Handle != null)
            {
                Entity e = PointRefResolver.Require(db, tr, p.Handle);
                huellaContorno = PointRefResolver.Fingerprint(e, p.Handle);
                AxisReader.Vertices? read = e is Line ? null : AxisReader.Read(e, tr);
                if (read == null || !read.Cerrada || read.Puntos.Count < 3)
                {
                    throw ToolException.InvalidParams(
                        "contorno: la entidad " + p.Handle + " no es una polilínea cerrada (" + AcadUtil.DxfName(e) + ")",
                        new Dictionary<string, object?> { ["handle"] = p.Handle });
                }

                if (read.TieneArcos)
                {
                    advertencias.Add("contorno: la polilínea " + p.Handle + " tiene arcos; el área y el centroide se calcularon sobre las cuerdas.");
                }

                // Vértices consecutivos duplicados de una polilínea EXISTENTE (frecuentes en planos reales): se colapsan con
                // advertencia en vez de rechazar; si tras colapsar quedan < 3 o hay autointersección, PolylineValidator
                // rechaza abajo como siempre.
                v = PolygonMetrics.CollapseConsecutiveDuplicates(read.Puntos, out int colapsados, lazoCerrado: true);
                if (colapsados > 0)
                {
                    advertencias.Add(PolygonMetrics.CollapsedDuplicatesWarning(p.Handle, colapsados));
                }

                z = read.Z;
            }
            else
            {
                int n = p.Contorno!.Count;
                v = new List<(double x, double y)>(n);
                for (int i = 0; i < n; i++)
                {
                    v.Add((puntos[i].Punto.X, puntos[i].Punto.Y));
                }
            }

            IReadOnlyList<string> errors = PolylineValidator.Validate(v, true, null);
            if (errors.Count > 0)
            {
                string msg = errors[0];
                if (msg.StartsWith("vertices", StringComparison.Ordinal))
                {
                    msg = "contorno" + msg.Substring("vertices".Length);
                }

                throw ToolException.InvalidParams(msg);
            }

            (double perimetro, double? areaN) = PolygonMetrics.LengthAndArea(v, true);
            double area = areaN ?? 0.0;
            if (!(area > MinArea))
            {
                throw ToolException.InvalidGeometry("contorno: el área es 0");
            }

            (double cx, double cy) = PolygonMetrics.Centroid(v);
            Point3 etiqueta;
            bool enCentroide;
            if (p.EtiquetaEn != null)
            {
                etiqueta = puntos[puntos.Count - 1].Punto;
                enCentroide = false;
            }
            else
            {
                (double ex, double ey) = PolygonMetrics.InteriorPoint(v, out enCentroide);
                etiqueta = new Point3(ex, ey, z);
            }

            string texto = p.Nombre + "\n" + PolygonMetrics.FormatArea(area, p.Decimales);
            var entidades = new List<PlannedEntity>(2);
            if (p.Handle == null)
            {
                entidades.Add(new PlannedPolyline(v, true, null, 0.0, null, p.CapaContorno));
            }

            entidades.Add(new PlannedMText(etiqueta, texto, p.Altura, p.Ancho, 0.0, "medio_centro", p.Estilo, "textos", p.CapaTexto));

            var numeros = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["area"] = area,
                ["perimetro"] = perimetro,
                ["centroide"] = new[] { cx, cy },
                ["punto_etiqueta"] = etiqueta.ToArray(),
                ["etiqueta_en_centroide"] = enCentroide,
                ["n_vertices"] = v.Count,
                ["etiqueta"] = texto,
            };

            // Con handle no se crea polilínea: la caja de la respuesta es la del contorno existente (no solo el punto del MTEXT).
            if (p.Handle != null)
            {
                numeros["bbox"] = PrimitiveSupport.BoxOf(v).ToArray2D();
            }

            var geo = new GeneratedGeometry(entidades, numeros);
            if (huellaContorno != null)
            {
                geo.AnclasExtra.Add(huellaContorno);
            }

            return geo;
        }
    }
}
