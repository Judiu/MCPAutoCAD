using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// extender_linea (0.8.1, ampliado en 0.9). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z). Mueve UN extremo de
    /// una LINE: con 'hasta' (LINE o LWPOLYLINE) a la intersección más cercana con la frontera (primero hacia fuera; si no, sobre
    /// la recta); con 'hasta_punto' a la proyección del punto sobre la recta. Geometría en <see cref="LineExtend"/>.
    /// <para>limite_extendido (0.9): la frontera cuenta como recta infinita. En una LINE, su recta; en una polilínea ABIERTA solo
    /// su primer y último segmento (como EXTEND con Edge=Extend); los segmentos interiores y las polilíneas cerradas no se
    /// extienden. Rechazos: intersección casi paralela (&lt; 0,5°) o extensión mayor que 2 × el tamaño de la extensión del
    /// dibujo (INVALID_PARAMS con motivo).</para>
    /// <para>Si el resultado no mueve el extremo (p. ej. 'hasta_punto' ya en ese punto), no se escribe nada y no se crea paso de
    /// deshacer.</para>
    /// Las polilíneas con arcos (bulge) se rechazan: no se aproximan.
    /// </summary>
    internal sealed class ExtenderLineaHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "handle", "extremo", "hasta", "hasta_punto", "limite_extendido", "confirmar", "aceptar_sin_unidades" };

        private static readonly string[] Extremos = { "inicio", "fin" };

        /// <summary>Factor del tope de extensión con limite_extendido: 2 × el tamaño de la extensión del dibujo.</summary>
        private const double ExtensionCapFactor = 2.0;

        public string Name => "extender_linea";

        public string Description =>
            "EXTIENDE o RECORTA un extremo (inicio o fin) de una LINE en un solo paso de deshacer. Con 'hasta' (LINE o LWPOLYLINE " +
            "sin arcos) va a la intersección más cercana con esa frontera: primero hacia fuera del extremo; si no hay corte, la más " +
            "cercana sobre la recta. Con limite_extendido: true la frontera cuenta como recta infinita (en polilíneas abiertas, solo " +
            "el primer y último segmento). Con 'hasta_punto' [x, y] el extremo va a la proyección perpendicular de ese punto sobre la " +
            "recta de la línea (sin frontera). Devuelve el punto anterior y el nuevo. Rechaza si no hay corte, si el resultado queda " +
            "de longitud cero o al otro lado del extremo fijo, y con limite_extendido si la frontera es casi paralela o la extensión " +
            "es excesiva. Si el extremo ya está en el punto pedido, no escribe nada.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""handle"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"", ""description"": ""Handle de la LINE a extender o recortar."" },
    ""extremo"": { ""type"": ""string"", ""enum"": [""inicio"", ""fin""], ""description"": ""Extremo que se mueve: inicio (primer punto) o fin (segundo punto)."" },
    ""hasta"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"", ""description"": ""Handle de la frontera: una LINE o una LWPOLYLINE sin arcos. Exclusivo con hasta_punto."" },
    ""hasta_punto"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""Punto [x, y] (o [x, y, z]) en WCS. El extremo se coloca en la proyección perpendicular de este punto sobre la recta de la línea (si el punto está sobre la recta, en el propio punto). Exclusivo con hasta."" },
    ""limite_extendido"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Solo con hasta: la frontera se toma como recta infinita (una LINE: su recta; una polilínea abierta: su primer y último segmento). Sirve cuando el límite no toca físicamente la prolongación (p. ej. caras de muro separadas milímetros). Rechaza intersecciones casi paralelas (< 0,5°) y extensiones mayores que 2 × el tamaño del dibujo."" },
    ""confirmar"": " + ModifySupport.SchemaConfirmar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @"
  },
  ""required"": [""handle"", ""extremo""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = ModifySupport.BuildOutputSchema(@"
    ""extremo"": { ""type"": ""string"" },
    ""hasta"": { ""type"": [""string"", ""null""] },
    ""hasta_punto"": { ""type"": [""array"", ""null""], ""items"": { ""type"": ""number"" } },
    ""modo"": { ""type"": ""string"", ""enum"": [""frontera"", ""frontera_infinita"", ""punto""] },
    ""punto_anterior"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""punto_nuevo"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""extendida"": { ""type"": ""boolean"" },
    ""escrito"": { ""type"": ""boolean"" },
    ""entidad"": { ""type"": ""object"", ""properties"": {
      ""handle"": { ""type"": ""string"" },
      ""inicio"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
      ""fin"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } } } }");

        private sealed class Params
        {
            public Params(string handle, bool fin, string? hasta, Point3? hastaPunto, bool limiteExtendido, bool confirmar, bool aceptar)
            {
                Handle = handle;
                Fin = fin;
                Hasta = hasta;
                HastaPunto = hastaPunto;
                LimiteExtendido = limiteExtendido;
                Confirmar = confirmar;
                AceptarSinUnidades = aceptar;
            }

            public string Handle { get; }

            /// <summary>true = extremo fin; false = extremo inicio.</summary>
            public bool Fin { get; }

            /// <summary>Handle de la frontera; null si el destino es un punto (<see cref="HastaPunto"/>).</summary>
            public string? Hasta { get; }

            /// <summary>Punto de proyección ('hasta_punto'); null si la frontera es un handle.</summary>
            public Point3? HastaPunto { get; }

            /// <summary>'limite_extendido': la frontera se trata como recta infinita (solo con 'hasta').</summary>
            public bool LimiteExtendido { get; }

            public bool Confirmar { get; }

            public bool AceptarSinUnidades { get; }
        }

        /// <summary>Segmentos de la frontera y, por segmento, si cuenta como recta infinita con limite_extendido.</summary>
        private sealed class Frontera
        {
            public Frontera(List<(Point3 A, Point3 B)> segmentos, List<bool> infinitos)
            {
                Segmentos = segmentos;
                Infinitos = infinitos;
            }

            public List<(Point3 A, Point3 B)> Segmentos { get; }

            public List<bool> Infinitos { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string handle = ParamReader.OptionalHandle(p, "handle")?.ToUpperInvariant()
                ?? throw ToolException.InvalidParams("handle: obligatorio");
            if (!ParamReader.Has(p, "extremo"))
            {
                throw ToolException.InvalidParams("extremo: obligatorio");
            }

            string extremo = ParamReader.OptionalEnum(p, "extremo", Extremos) ?? "fin";
            bool tieneHasta = ParamReader.Has(p, "hasta");
            bool tienePunto = ParamReader.Has(p, "hasta_punto");
            if (tieneHasta == tienePunto)
            {
                throw ToolException.InvalidParams("debe indicar exactamente uno de 'hasta' (handle de la frontera) o 'hasta_punto' ([x, y])");
            }

            bool limite = ParamReader.OptionalBool(p, "limite_extendido") ?? false;
            string? hasta = null;
            Point3? punto = null;
            if (tieneHasta)
            {
                hasta = ParamReader.OptionalHandle(p, "hasta")?.ToUpperInvariant()
                    ?? throw ToolException.InvalidParams("hasta: obligatorio");
                if (string.Equals(handle, hasta, StringComparison.Ordinal))
                {
                    throw ToolException.InvalidParams("hasta: debe ser distinto de handle");
                }
            }
            else
            {
                if (limite)
                {
                    throw ToolException.InvalidParams("limite_extendido: solo aplica con 'hasta' (una frontera)");
                }

                punto = ParamReader.OptionalPoint(p, "hasta_punto");
            }

            bool confirmar = ParamReader.OptionalBool(p, "confirmar") ?? false;
            bool aceptar = ParamReader.OptionalBool(p, "aceptar_sin_unidades") ?? false;
            return new Params(handle, extremo == "fin", hasta, punto, limite, confirmar, aceptar);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = DrawWrite.CheckUnits(db, p.AceptarSinUnidades, warnings);
            int pid = ctx.Pid;

            // Lectura y cálculo sin grupo de deshacer: aquí salen todos los rechazos y el caso "no mueve nada".
            double span = UnirLineasHandler.ExtentsSpan(db);
            double tol = LineMerge.DefaultTolerance(span);
            double maxExtension = double.PositiveInfinity;
            LineExtend.Result plan;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                HandleGuard.Resolved res = HandleGuard.Resolve(db, tr, new List<string> { p.Handle }, p.Confirmar, warnings);
                Line line = RequireLine(res.Items[0].Entity, p.Handle);
                Frontera? frontera = p.Hasta != null ? BoundaryOf(db, tr, p.Hasta) : null;
                maxExtension = ExtensionCap(ToPoint3(line.StartPoint), ToPoint3(line.EndPoint), frontera, span);
                plan = Plan(line, p, frontera, tol, maxExtension);   // rechazos aquí, antes del grupo de deshacer

                if (Distance(plan.Anterior, plan.Nuevo) <= tol)
                {
                    // 10 (0.9): ya está en ese punto: no se escribe ni se crea paso de deshacer.
                    warnings.Add("extender_linea: el extremo ya está en el punto pedido; no se escribió nada.");
                    Dictionary<string, object?> sinCambios = BuildResponse(pid, p, res, ins, warnings, plan, line, false);
                    tr.Commit();
                    return sinCambios;
                }

                tr.Commit();
            }

            return UndoGroup.Run(doc, d => Write(d, p, pid, ins, warnings, tol, maxExtension));
        }

        /// <summary>
        /// Tope de la extensión con limite_extendido (0.10, revisión 2): ExtensionCapFactor × max(tamaño del dibujo, longitud de
        /// la línea + diagonal de la caja de la frontera). No depende solo de EXTMIN/EXTMAX (que pueden ser inválidos).
        /// </summary>
        private static double ExtensionCap(Point3 a, Point3 b, Frontera? frontera, double drawingSpan)
        {
            double diag = 0;
            if (frontera != null && frontera.Segmentos.Count > 0)
            {
                double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
                double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
                foreach ((Point3 A, Point3 B) seg in frontera.Segmentos)
                {
                    foreach (Point3 q in new[] { seg.A, seg.B })
                    {
                        minX = Math.Min(minX, q.X);
                        minY = Math.Min(minY, q.Y);
                        maxX = Math.Max(maxX, q.X);
                        maxY = Math.Max(maxY, q.Y);
                    }
                }

                diag = Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)));
            }

            return ExtensionCapFactor * Math.Max(drawingSpan, Distance(a, b) + diag);
        }

        private static object Write(Document doc, Params p, int pid, int ins, List<string> warnings, double tol, double maxExtension)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                HandleGuard.Resolved res = HandleGuard.Resolve(db, tr, new List<string> { p.Handle }, p.Confirmar, new List<string>());
                Line line = RequireLine(res.Items[0].Entity, p.Handle);
                Frontera? frontera = p.Hasta != null ? BoundaryOf(db, tr, p.Hasta) : null;
                LineExtend.Result plan = Plan(line, p, frontera, tol, maxExtension);

                line.UpgradeOpen();
                if (p.Fin)
                {
                    line.EndPoint = AcadUtil.ToPoint3d(plan.Nuevo);
                }
                else
                {
                    line.StartPoint = AcadUtil.ToPoint3d(plan.Nuevo);
                }

                Dictionary<string, object?> r = BuildResponse(pid, p, res, ins, warnings, plan, line, true);
                tr.Commit();
                return r;
            }
        }

        /// <summary>Respuesta común. <paramref name="escrito"/> = false si no se modificó nada (extremo ya en el punto).</summary>
        private static Dictionary<string, object?> BuildResponse(
            int pid, Params p, HandleGuard.Resolved res, int ins, List<string> warnings, LineExtend.Result plan, Line line, bool escrito)
        {
            Point3 inicio = ToPoint3(line.StartPoint);
            Point3 fin = ToPoint3(line.EndPoint);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = pid,
                ["handles"] = new List<string> { p.Handle },
                ["n"] = 1,
                ["n_usuario"] = res.NUsuario,
                ["espacio"] = res.Espacio,
                ["capas"] = res.Capas,
                ["insunits"] = ins,
                ["unidades"] = UnitsTable.Name(ins),
                ["advertencias"] = warnings,
                ["escrito"] = escrito,
                ["extremo"] = p.Fin ? "fin" : "inicio",
                ["hasta"] = p.Hasta,
                ["hasta_punto"] = p.HastaPunto is Point3 hp ? AcadUtil.ToArray(AcadUtil.ToPoint3d(hp)) : null,
                ["modo"] = p.HastaPunto != null ? "punto" : (p.LimiteExtendido ? "frontera_infinita" : "frontera"),
                ["punto_anterior"] = AcadUtil.ToArray(AcadUtil.ToPoint3d(plan.Anterior)),
                ["punto_nuevo"] = AcadUtil.ToArray(AcadUtil.ToPoint3d(plan.Nuevo)),
                ["extendida"] = plan.Extendida,
                ["entidad"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["handle"] = p.Handle,
                    ["inicio"] = AcadUtil.ToArray(AcadUtil.ToPoint3d(inicio)),
                    ["fin"] = AcadUtil.ToArray(AcadUtil.ToPoint3d(fin)),
                },
            };
        }

        /// <summary>
        /// Calcula el nuevo extremo. Los rechazos de geometría son INVALID_GEOMETRY con el texto de GeometryCore; los propios de
        /// limite_extendido (casi paralela, tope de extensión) son INVALID_PARAMS con el motivo.
        /// </summary>
        private static LineExtend.Result Plan(Line line, Params p, Frontera? frontera, double tol, double maxExtension)
        {
            Point3 a = ToPoint3(line.StartPoint);
            Point3 b = ToPoint3(line.EndPoint);
            LineExtend.Extremo extremo = p.Fin ? LineExtend.Extremo.Fin : LineExtend.Extremo.Inicio;
            try
            {
                if (p.HastaPunto is Point3 punto)
                {
                    return LineExtend.ComputeToPoint(a, b, extremo, punto, tol);
                }

                return LineExtend.Compute(
                    a,
                    b,
                    extremo,
                    frontera?.Segmentos ?? new List<(Point3 A, Point3 B)>(),
                    tol,
                    p.LimiteExtendido,
                    maxExtension,
                    frontera?.Infinitos);
            }
            catch (ArgumentException ex)
            {
                if (ex.Message.StartsWith("limite_extendido:", StringComparison.Ordinal))
                {
                    throw ToolException.InvalidParams(ex.Message, new Dictionary<string, object?> { ["handle"] = p.Handle, ["hasta"] = p.Hasta });
                }

                throw new ToolException(
                    ErrorCodes.InvalidGeometry,
                    "extender_linea: " + ex.Message + ".",
                    new Dictionary<string, object?> { ["handle"] = p.Handle, ["hasta"] = p.Hasta });
            }
        }

        private static Line RequireLine(Entity e, string handle)
        {
            if (e is Line line)
            {
                return line;
            }

            throw ToolException.InvalidParams(
                "handle: no es una LINE",
                new Dictionary<string, object?> { ["handle"] = handle, ["tipo"] = AcadUtil.DxfName(e) });
        }

        /// <summary>
        /// Segmentos de la frontera. LINE: un segmento, infinito con limite_extendido. LWPOLYLINE sin arcos: cerrada → todos
        /// finitos (incluye el segmento de cierre); abierta → sus segmentos son finitos salvo el primero y el último, que son
        /// infinitos con limite_extendido (como EXTEND con Edge=Extend).
        /// </summary>
        private static Frontera BoundaryOf(Database db, Transaction tr, string handle)
        {
            Entity b = AcadUtil.TryGetEntityByHandle(db, handle, tr) ?? throw ToolException.EntityNotFound(handle);
            var segs = new List<(Point3 A, Point3 B)>();

            if (b is Line l)
            {
                segs.Add((ToPoint3(l.StartPoint), ToPoint3(l.EndPoint)));
                return new Frontera(segs, new List<bool> { true });
            }

            if (b is Polyline pl)
            {
                int n = pl.NumberOfVertices;
                if (n < 2)
                {
                    throw ToolException.InvalidParams("hasta: la polilínea necesita al menos 2 vértices", new Dictionary<string, object?> { ["hasta"] = handle });
                }

                int count = pl.Closed ? n : n - 1;
                var infinitos = new List<bool>(count);
                for (int i = 0; i < count; i++)
                {
                    if (pl.GetBulgeAt(i) != 0.0)
                    {
                        throw ToolException.InvalidParams(
                            "hasta: la polilínea tiene arcos (bulge); no se admiten como frontera",
                            new Dictionary<string, object?> { ["hasta"] = handle });
                    }

                    int j = (i + 1) % n;
                    segs.Add((VertexOf(pl, i), VertexOf(pl, j)));
                    infinitos.Add(!pl.Closed && (i == 0 || i == count - 1));
                }

                return new Frontera(segs, infinitos);
            }

            throw ToolException.InvalidParams(
                "hasta: debe ser una LINE o una LWPOLYLINE (tipo " + AcadUtil.DxfName(b) + ")",
                new Dictionary<string, object?> { ["hasta"] = handle, ["tipo"] = AcadUtil.DxfName(b) });
        }

        private static Point3 VertexOf(Polyline pl, int i)
        {
            Point2d q = pl.GetPoint2dAt(i);
            return new Point3(q.X, q.Y, pl.Elevation);
        }

        private static Point3 ToPoint3(Point3d p) => new Point3(p.X, p.Y, p.Z);

        private static double Distance(Point3 p, Point3 q)
        {
            double dx = p.X - q.X;
            double dy = p.Y - q.Y;
            double dz = p.Z - q.Z;
            return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }
    }
}
