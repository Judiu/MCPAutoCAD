using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// cotas_automaticas (protocol.md §5G.3): cotas lineales de los puntos característicos de las entidades dadas, en
    /// cadena o total, a 'separacion' del lado pedido. Cálculo en <see cref="DimensionChain"/> (GeometryCore); dibujo en
    /// GeometryDrawer.DrawDimension. Las entidades de 'handles' solo se leen: no exige confirmar (a diferencia de
    /// HandleGuard no rechaza capas bloqueadas) y llevan huella en AnclasExtra para que confirmar detecte cambios.
    /// No toca DIMSCALE ni CANNOSCALE: los informa en 'numeros'.
    /// </summary>
    internal sealed class CotasAutomaticasHandler : GeneratorBase
    {
        private const string Concepto = "cotas";
        private const string TipoLineal = "lineal";
        private const string DireccionAuto = "auto";
        private const string DireccionInvalida = "direccion: debe ser x, y, auto o un número en grados";
        private const string LadoInvalido = "lado: debe ser izquierda o derecha";

        private static readonly string[] AllowedNames = BuildAllowed();

        public override string Name => "cotas_automaticas";

        public override string Description =>
            "ESCRIBE cotas lineales automáticas en la capa Cotas: proyecta los vértices/extremos/centros de las entidades dadas (muros, polilíneas, líneas y burbujas de ejes) sobre una dirección (x, y, grados o auto) y crea una cadena de cotas (o una total con cadena: false) a 'separacion' del lado pedido, con el estilo de cota indicado (o el actual, con advertencia). Devuelve las medidas. Con preview: true solo calcula y devuelve números + preview_id.";

        public override JsonElement InputSchema { get; } = GenSchema.Build(
            @"    ""handles"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"" }, ""description"": ""Entidades cuyos puntos característicos se acotan (1–100, sin repetidos), todas en el mismo espacio: LINE (extremos), LWPOLYLINE/POLYLINE 2D (vértices), CIRCLE/ARC (centro), INSERT (punto de inserción), TEXT/MTEXT (posición). Solo se leen: no exige confirmar."" },
    ""direccion"": { ""description"": ""Dirección de la línea de cota: x (0°), y (90°), un número en grados, o auto (la del tramo más largo de la primera entidad)."", ""default"": ""auto"", ""oneOf"": [
      { ""type"": ""string"", ""enum"": [""x"", ""y"", ""auto""] },
      { ""type"": ""number"" }
    ] },
    ""lado"": { ""description"": ""Lado de la línea de cota respecto a la dirección (izquierda o derecha), o un PuntoRef: la línea de cota va al lado de ese punto."", ""default"": ""izquierda"", ""oneOf"": [
      { ""type"": ""string"", ""enum"": [""izquierda"", ""derecha""] },
      " + GenSchema.SchemaPuntoRef + @"
    ] },
    ""separacion"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Distancia de la línea de cota al punto más extremo del conjunto en ese lado (la oficina usa 0,15–0,19; segunda cadena 0,35)."" },
    ""estilo"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre de un estilo de cota existente (sin distinguir mayúsculas). Por defecto, el actual, con advertencia."" },
    ""cadena"": { ""type"": ""boolean"", ""default"": true, ""description"": ""true = una cota por tramo consecutivo; false = una sola cota total."" },
    ""tolerancia"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 0.001, ""description"": ""Los puntos proyectados a menos de esta distancia se funden en uno."" },
    ""capa"": " + DrawWrite.SchemaCapa,
            "handles", "separacion");

        public override JsonElement? OutputSchema { get; } = GenSchema.Output(
            @"        ""medidas"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
        ""total"": { ""type"": ""number"" },
        ""n_cotas"": { ""type"": ""integer"" },
        ""direccion"": { ""type"": ""number"" },
        ""lado"": { ""type"": ""string"" },
        ""separacion"": { ""type"": ""number"" },
        ""estilo"": { ""type"": ""string"" },
        ""dimscale"": { ""type"": [""number"", ""null""] },
        ""cannoscale"": { ""type"": [""string"", ""null""] },
        ""n_puntos"": { ""type"": ""integer"" },
        ""n_puntos_fundidos"": { ""type"": ""integer"" }");

        protected override IReadOnlyCollection<string> Allowed => AllowedNames;

        protected override string? DefaultConcept => Concepto;

        private static string[] BuildAllowed()
        {
            var list = new List<string> { "handles", "direccion", "lado", "separacion", "estilo", "cadena", "tolerancia", "capa" };
            list.AddRange(GenSchema.CommonParams);
            return list.ToArray();
        }

        private sealed class Params
        {
            public Params(
                List<string> handles, double? direccion, string? lado, PointRef? ladoPunto, double separacion, string? estilo,
                bool cadena, double tolerancia, string? capa, string? espacio)
            {
                Handles = handles;
                Direccion = direccion;
                Lado = lado;
                LadoPunto = ladoPunto;
                Separacion = separacion;
                Estilo = estilo;
                Cadena = cadena;
                Tolerancia = tolerancia;
                Capa = capa;
                Espacio = espacio;
            }

            public List<string> Handles { get; }

            /// <summary>Grados; null = auto.</summary>
            public double? Direccion { get; }

            /// <summary>izquierda | derecha; null si el lado viene dado por <see cref="LadoPunto"/>.</summary>
            public string? Lado { get; }

            public PointRef? LadoPunto { get; }
            public double Separacion { get; }
            public string? Estilo { get; }
            public bool Cadena { get; }
            public double Tolerancia { get; }
            public string? Capa { get; }

            /// <summary>Parámetro común 'espacio' (solo para avisar si destino y entidades no coinciden).</summary>
            public string? Espacio { get; }
        }

        protected override object ParseOwn(JsonElement p)
        {
            List<string> handles = ParamReader.RequiredHandles(p, "handles", 1, 100);
            double? direccion = ReadDireccion(p);
            (string? lado, PointRef? ladoPunto) = ReadLado(p);
            double separacion = ParamReader.RequiredNumber(p, "separacion", 0, double.PositiveInfinity, exclusiveMin: true);
            string? estilo = ParamReader.OptionalString(p, "estilo", 1, 255);
            bool cadena = ParamReader.OptionalBool(p, "cadena") ?? true;
            double tolerancia = ParamReader.OptionalNumber(p, "tolerancia", 0, double.PositiveInfinity, exclusiveMin: true) ?? DimensionChain.DefaultTol;
            string? capa = ParamReader.OptionalString(p, "capa", 1, 255);
            if (capa != null)
            {
                DrawLayerFactory.Plan(capa, Concepto);
            }

            string? espacio = ParamReader.OptionalString(p, "espacio", 1, 255);
            return new Params(handles, direccion, lado, ladoPunto, separacion, estilo, cadena, tolerancia, capa, espacio);
        }

        /// <summary>x → 0, y → 90, auto → null, número finito → tal cual; otro valor → <see cref="DireccionInvalida"/>.</summary>
        private static double? ReadDireccion(JsonElement p)
        {
            if (!p.TryGetProperty("direccion", out JsonElement el))
            {
                return null;
            }

            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double grados) && double.IsFinite(grados))
            {
                return grados;
            }

            if (el.ValueKind == JsonValueKind.String)
            {
                switch (el.GetString())
                {
                    case "x":
                        return 0.0;
                    case "y":
                        return 90.0;
                    case DireccionAuto:
                        return null;
                }
            }

            throw ToolException.InvalidParams(DireccionInvalida);
        }

        /// <summary>Cadena izquierda|derecha (defecto izquierda) o PuntoRef.</summary>
        private static (string? Lado, PointRef? Punto) ReadLado(JsonElement p)
        {
            if (!p.TryGetProperty("lado", out JsonElement el))
            {
                return (DimensionChain.LadoIzquierda, null);
            }

            if (el.ValueKind != JsonValueKind.String)
            {
                return (null, GenSchema.ReadPointRef(el, "lado"));
            }

            string? lado = el.GetString();
            if (lado != DimensionChain.LadoIzquierda && lado != DimensionChain.LadoDerecha)
            {
                throw ToolException.InvalidParams(LadoInvalido);
            }

            return (lado, null);
        }

        protected override IReadOnlyList<(PointRef Ref, string Path)> PointRefs(object own)
        {
            var p = (Params)own;
            var list = new List<(PointRef Ref, string Path)>();
            if (p.LadoPunto != null)
            {
                list.Add((p.LadoPunto, "lado"));
            }

            return list;
        }

        protected override GeneratedGeometry Plan(object own, IReadOnlyList<ResolvedPoint> puntos, Database db, Transaction tr, List<string> advertencias)
        {
            var p = (Params)own;

            // Mismo orden que §5D.8: existencia de todas, dentro de bloque, mismo espacio; después los puntos.
            var entities = new List<Entity>(p.Handles.Count);
            foreach (string h in p.Handles)
            {
                entities.Add(PointRefResolver.Require(db, tr, h));
            }

            string espacioEntidades = RequireSameSpace(entities, p.Handles, tr);
            string destino = SpaceResolver.Resolve(db, tr, p.Espacio).Name;
            if (!string.Equals(destino, espacioEntidades, StringComparison.OrdinalIgnoreCase))
            {
                advertencias.Add("cotas_automaticas: las entidades están en '" + espacioEntidades + "' y las cotas se crean en '" + destino + "' (parámetro espacio).");
            }

            var todos = new List<(double x, double y)>();
            List<(double x, double y)>? primeros = null;
            var huellas = new List<AnchorFingerprint>(entities.Count);
            for (int i = 0; i < entities.Count; i++)
            {
                Entity e = entities[i];
                string h = p.Handles[i];
                List<(double x, double y)> pts = CharacteristicPoints(e, tr)
                    ?? throw ToolException.InvalidParams(
                        "handles[" + I(i) + "]: la entidad " + h + " (" + AcadUtil.DxfName(e) + ") no aporta puntos acotables",
                        new Dictionary<string, object?> { ["handle"] = h });
                primeros ??= pts;
                todos.AddRange(pts);
                huellas.Add(PointRefResolver.Fingerprint(e, h));
            }

            (_, string estiloNombre) = GeometryDrawer.ResolveDimStyle(db, tr, p.Estilo);
            if (p.Estilo == null)
            {
                advertencias.Add("cotas_automaticas: se usó el estilo de cota actual '" + estiloNombre + "'; indique estilo para fijarlo (la oficina usa COTA75).");
            }

            double direccion = p.Direccion ?? DimensionChain.DominantDirection(primeros!);
            (double x, double y)? ladoPunto = null;
            if (p.LadoPunto != null)
            {
                ladoPunto = (puntos[0].Punto.X, puntos[0].Punto.Y);
            }

            DimensionChain.ChainResult cadena;
            try
            {
                cadena = DimensionChain.Build(todos, direccion, p.Lado, ladoPunto, p.Separacion, p.Cadena, p.Tolerancia);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }

            var planned = new List<PlannedEntity>(cadena.Cotas.Count);
            foreach (DimensionChain.Segment s in cadena.Cotas)
            {
                planned.Add(new PlannedDimension(
                    TipoLineal,
                    new Point3(s.P1.x, s.P1.y, 0),
                    new Point3(s.P2.x, s.P2.y, 0),
                    new Point3(s.Linea.x, s.Linea.y, 0),
                    direccion,
                    p.Estilo,
                    Concepto,
                    p.Capa));
            }

            var numeros = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["medidas"] = new List<double>(cadena.Medidas),
                ["total"] = cadena.Total,
                ["n_cotas"] = cadena.Cotas.Count,
                ["direccion"] = direccion,
                ["lado"] = p.Lado ?? SideName(cadena),
                ["separacion"] = p.Separacion,
                ["estilo"] = estiloNombre,
                ["dimscale"] = ReadDimscale(db),
                ["cannoscale"] = ReadCannoscale(db),
                ["n_puntos"] = todos.Count,
                ["n_puntos_fundidos"] = cadena.NPuntosFundidos,
            };

            var geo = new GeneratedGeometry(planned, numeros);
            geo.AnclasExtra.AddRange(huellas);
            return geo;
        }

        /// <summary>
        /// §5D.8 pasos 2 y 4 aplicables a una lectura: ninguna dentro de una definición de bloque y todas en el mismo
        /// espacio. Devuelve ese espacio. Un atributo cuenta en el espacio de su INSERT (lo rechaza después el tipo).
        /// </summary>
        private static string RequireSameSpace(List<Entity> entities, List<string> handles, Transaction tr)
        {
            var espacios = new List<string>(entities.Count);
            for (int i = 0; i < entities.Count; i++)
            {
                string espacio = SpaceResolver.OwnerName(entities[i], tr) ?? "?";
                if (espacio.StartsWith(SpaceResolver.BlockPrefix, StringComparison.Ordinal))
                {
                    throw ToolException.InvalidParams(
                        "handles[" + I(i) + "]: la entidad está dentro de una definición de bloque",
                        new Dictionary<string, object?> { ["handle"] = handles[i] });
                }

                espacios.Add(espacio);
            }

            for (int i = 1; i < espacios.Count; i++)
            {
                if (!string.Equals(espacios[i], espacios[0], StringComparison.Ordinal))
                {
                    throw ToolException.InvalidParams(
                        "handles: las entidades deben estar en el mismo espacio (" + handles[0] + " en " + espacios[0] + ", " + handles[i] + " en " + espacios[i] + ")",
                        new Dictionary<string, object?> { ["handles"] = new List<string> { handles[0], handles[i] } });
                }
            }

            return espacios[0];
        }

        /// <summary>
        /// Puntos característicos (§5G.3), o null si el tipo no aporta ninguno. Los casos excluidos lo son porque heredan de
        /// un tipo admitido sin serlo: ATTRIB y ATTDEF derivan de DBText y Table (ACAD_TABLE) de BlockReference.
        /// </summary>
        private static List<(double x, double y)>? CharacteristicPoints(Entity e, Transaction tr)
        {
            switch (e)
            {
                case Line or Polyline or Polyline2d:
                    return AxisReader.Read(e, tr)?.Puntos;
                case Circle c:
                    return new List<(double x, double y)> { (c.Center.X, c.Center.Y) };
                case Arc a:
                    return new List<(double x, double y)> { (a.Center.X, a.Center.Y) };
                case Table or AttributeReference or AttributeDefinition:
                    return null;
                case BlockReference br:
                    return new List<(double x, double y)> { (br.Position.X, br.Position.Y) };
                case DBText t:
                    return new List<(double x, double y)> { (t.Position.X, t.Position.Y) };
                case MText m:
                    return new List<(double x, double y)> { (m.Location.X, m.Location.Y) };
                default:
                    return null;
            }
        }

        /// <summary>Lado resuelto cuando se dio un punto: izquierda si la normal del lado es la izquierda de la dirección.</summary>
        private static string SideName(DimensionChain.ChainResult r)
        {
            double producto = -r.Direccion.y * r.NormalLado.x + r.Direccion.x * r.NormalLado.y;
            return producto >= 0 ? DimensionChain.LadoIzquierda : DimensionChain.LadoDerecha;
        }

        private static double? ReadDimscale(Database db)
        {
            try
            {
                return db.Dimscale;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return null;
            }
        }

        /// <summary>Nombre de la escala anotativa actual (informativo); null si no se puede leer.</summary>
        private static string? ReadCannoscale(Database db)
        {
            try
            {
                using (AnnotationScale? scale = db.Cannoscale)
                {
                    return scale?.Name;
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return null;
            }
        }

        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
