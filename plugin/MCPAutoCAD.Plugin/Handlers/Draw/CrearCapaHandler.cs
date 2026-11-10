using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Audit;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// crear_capa (protocol.md §5D.1, 0.9). Hilo principal; requiere documento.
    /// <list type="bullet">
    /// <item>Nombre: concepto de lps.json (p. ej. "cotas"), nombre de capas_permitidas/capas_mcp, MCP-* o CUALQUIER nombre
    /// válido (0.9). Un concepto se resuelve a su capa salvo <c>libre: true</c>, que usa el nombre tal cual. Un nombre que no
    /// sigue el estándar se crea con propiedades por defecto y se avisa.</item>
    /// <item>Propiedades opcionales al crear: color (ACI 1–255 o [r, g, b]), tipo_linea, grosor (valores de AutoCAD) y
    /// transparencia (0–90 %). Si la capa ya existe no se borra ni se recrea: sin confirmar: true se devuelven las
    /// diferencias antes/después y no se escribe; con confirmar: true se aplican en un solo paso de deshacer.</item>
    /// <item>Idempotente: una capa existente con lo pedido → creada: false, modificada: false, sin escritura.</item>
    /// </list>
    /// Escritura de creación: una Transaction dentro de <see cref="UndoGroup.Run"/> (un CTRL+Z). Sin 'confirmar' la llamada
    /// sobre una capa existente solo lee.
    /// </summary>
    internal sealed class CrearCapaHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "nombre", "libre", "color", "tipo_linea", "grosor", "transparencia", "confirmar" };

        /// <summary>Propiedades por defecto de una capa cuyo nombre no está en lps.json: ACI 7, Continuous, grosor Default, imprimible.</summary>
        private static readonly LayerSpec LibreDefault = new LayerSpec(7, "Continuous", -3, true);

        /// <summary>Nombres que AutoCAD crea siempre y que no son del estándar LPS pero no merecen aviso.</summary>
        private static readonly string[] SystemNames = { "0", "DEFPOINTS" };

        public string Name => "crear_capa";

        public string Description =>
            "ESCRIBE en el dibujo solo si hace falta: crea una capa con el nombre dado (concepto de lps.json como 'cotas', nombre " +
            "del estándar LPS o MCP-*; cualquier otro nombre válido solo con libre: true, que además usa el nombre tal cual aunque " +
            "coincida con un concepto) con color, tipo de línea, grosor y transparencia opcionales, en un solo paso de deshacer. Si la capa ya " +
            "existe no la recrea: devuelve sus propiedades (creada: false) y, si se piden propiedades distintas, solo las " +
            "compara (cambios antes/después) hasta que se pase confirmar: true.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""nombre"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Concepto de lps.json (muros, cotas, textos, ejes, columnas, vigas, puertas, ventanas, filos, achurados, mobiliario_fijo, sanitarios, estructura, mamposteria, niveles, escaleras, proyeccion, rotulo), nombre de capas_permitidas/capas_mcp, nombre MCP-*, o cualquier nombre válido de capa (si no está en el estándar se rechaza salvo libre: true)."" },
    ""libre"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true: 'nombre' se usa tal cual, aunque coincida con un concepto de lps.json (p. ej. 'achurados' crea la capa 'achurados' y no la capa del concepto)."" },
    ""color"": { ""anyOf"": [ { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 255, ""description"": ""Índice ACI."" }, { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 255 }, ""description"": ""Color verdadero [r, g, b]."" } ], ""description"": ""Color de la capa: ACI 1–255 o [r, g, b]. Defecto: el de lps.json (o ACI 7 si el nombre no está en el estándar)."" },
    ""tipo_linea"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Tipo de línea cargado en el dibujo. Si no está cargado se usa Continuous y se avisa."" },
    ""grosor"": { ""type"": ""integer"", ""minimum"": -3, ""maximum"": 211, ""description"": ""Grosor de AutoCAD en centésimas de mm (0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211) o -3 Default (ByLayer y ByBlock no valen en una capa)."" },
    ""transparencia"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 90, ""description"": ""Porcentaje de transparencia (0 = opaca)."" },
    ""confirmar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = aplicar las propiedades pedidas a una capa que YA EXISTE con otras propiedades. Sin él, la capa existente no se modifica y la respuesta muestra cambios (antes/después)."" }
  },
  ""required"": [""nombre""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""capa"": { ""type"": ""string"" },
    ""creada"": { ""type"": ""boolean"" },
    ""modificada"": { ""type"": ""boolean"" },
    ""escrito"": { ""type"": ""boolean"" },
    ""cambios"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""campo"": { ""type"": ""string"" }, ""antes"": { ""type"": ""string"" }, ""despues"": { ""type"": ""string"" } } } },
    ""color"": { ""type"": ""integer"", ""description"": ""Índice ACI; -1 si es color verdadero (ver color_texto)."" },
    ""color_texto"": { ""type"": ""string"" },
    ""tipo_linea"": { ""type"": ""string"" },
    ""grosor"": { ""type"": ""integer"" },
    ""transparencia"": { ""type"": ""integer"" },
    ""plot"": { ""type"": ""boolean"" },
    ""bloqueada"": { ""type"": ""boolean"" },
    ""congelada"": { ""type"": ""boolean"" },
    ""apagada"": { ""type"": ""boolean"" },
    ""origen"": { ""type"": ""string"", ""enum"": [""concepto"", ""nombre""] },
    ""estandar"": { ""type"": ""boolean"", ""description"": ""false si el nombre no sigue el estándar LPS (se avisa en advertencias)."" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""capa"", ""creada"", ""modificada"", ""escrito"", ""cambios"", ""color"", ""color_texto"", ""tipo_linea"", ""grosor"", ""transparencia"", ""plot"", ""bloqueada"", ""congelada"", ""apagada"", ""origen"", ""estandar"", ""insunits"", ""unidades"", ""advertencias""]
}");

        /// <summary>Parámetros ya leídos (sin AutoCAD).</summary>
        private sealed class Params
        {
            public Params(string nombre, bool libre, int? aci, (int R, int G, int B)? rgb, string? tipoLinea, int? grosor, int? transparencia, bool confirmar)
            {
                Nombre = nombre;
                Libre = libre;
                Aci = aci;
                Rgb = rgb;
                TipoLinea = tipoLinea;
                Grosor = grosor;
                Transparencia = transparencia;
                Confirmar = confirmar;
            }

            public string Nombre { get; }

            public bool Libre { get; }

            public int? Aci { get; }

            public (int R, int G, int B)? Rgb { get; }

            public string? TipoLinea { get; }

            public int? Grosor { get; }

            public int? Transparencia { get; }

            public bool Confirmar { get; }
        }

        /// <summary>Nombre final de la capa y de dónde sale (sin AutoCAD).</summary>
        private sealed class Resolved
        {
            public Resolved(string nombre, string origen, LayerSpec? spec, bool estandar)
            {
                Nombre = nombre;
                Origen = origen;
                Spec = spec;
                Estandar = estandar;
            }

            public string Nombre { get; }

            /// <summary>"concepto" o "nombre" (salida).</summary>
            public string Origen { get; }

            /// <summary>Propiedades del estándar; null si el nombre no está en lps.json.</summary>
            public LayerSpec? Spec { get; }

            public bool Estandar { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string nombre = ParamReader.RequiredText(p, "nombre", 1, 255);
            bool libre = ParamReader.OptionalBool(p, "libre") ?? false;
            (int? aci, (int, int, int)? rgb) = ReadColor(p);
            string? tipo = ParamReader.OptionalString(p, "tipo_linea", 1, 255);
            int? grosor = ParamReader.OptionalInt(p, "grosor", -3, 211);
            if (grosor.HasValue && !LayerProps.IsValidLineWeight(grosor.Value))
            {
                throw ToolException.InvalidParams(LayerProps.InvalidLineWeight);
            }

            int? transparencia = ParamReader.OptionalInt(p, "transparencia", 0, LayerProps.MaxTransparenciaPct);
            bool confirmar = ParamReader.OptionalBool(p, "confirmar") ?? false;
            return new Params(nombre, libre, aci, rgb, tipo, grosor, transparencia, confirmar);
        }

        /// <summary>'color': entero ACI 1–255 o array [r, g, b] de 0–255.</summary>
        private static (int? aci, (int, int, int)? rgb) ReadColor(JsonElement p)
        {
            if (!p.TryGetProperty("color", out JsonElement el))
            {
                return (null, null);
            }

            if (el.ValueKind == JsonValueKind.Array)
            {
                if (el.GetArrayLength() != 3)
                {
                    throw ToolException.InvalidParams(LayerProps.InvalidColor);
                }

                var c = new int[3];
                int i = 0;
                foreach (JsonElement x in el.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out int v) || v < 0 || v > 255)
                    {
                        throw ToolException.InvalidParams(LayerProps.InvalidColor);
                    }

                    c[i++] = v;
                }

                return (null, (c[0], c[1], c[2]));
            }

            return (ParamReader.OptionalInt(p, "color", 1, 255), null);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = AcadUtil.InsUnits(db);
            List<string> warnings = AcadUtil.UnitWarnings(db);
            Resolved nr = Resolve(p);
            if (!nr.Estandar)
            {
                warnings.Add(
                    "capa '" + nr.Nombre + "' no sigue el estándar LPS (lps.json): no es un concepto, ni capas_permitidas/capas_mcp, ni MCP-*.");
            }

            // Lectura previa SIN efectos (regla 9): si existe, se compara y se responde sin UndoGroup salvo confirmar.
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                ObjectId id = McpLayerFactory.FindLayer(table, nr.Nombre);
                if (!id.IsNull)
                {
                    var existing = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    RejectXref(existing);
                    IReadOnlyList<LayerProps.Change> cambios = LayerProps.Diff(ReadValues(existing, tr), BuildRequest(p, db, tr, warnings));
                    if (cambios.Count > 0 && !p.Confirmar)
                    {
                        warnings.Add(
                            "La capa '" + existing.Name + "' ya existe con otras propiedades: no se modificó. Pase confirmar: true para aplicar los cambios.");
                    }

                    if (cambios.Count == 0 || !p.Confirmar)
                    {
                        Dictionary<string, object?> r = Build(ctx.Pid, existing, tr, false, false, false, cambios, nr, ins, warnings);
                        tr.Commit();
                        return r;
                    }

                    tr.Commit();
                    int pidMod = ctx.Pid;
                    return UndoGroup.Run(doc, d => Modify(d, p, nr.Nombre, nr, pidMod, ins, warnings));
                }

                tr.Commit();
            }

            // Punto 5 (protocol §5D.1): un nombre fuera del estándar que no existe se rechaza salvo libre: true (un error
            // tipográfico no debe crear capas sueltas).
            if (!p.Libre && !nr.Estandar)
            {
                throw ToolException.InvalidParams(
                    "capa '" + nr.Nombre + "' no está en lps.json (capas_permitidas/capas_mcp, conceptos o MCP-*): si es intencional, use libre: true para crearla con ese nombre tal cual.",
                    new Dictionary<string, object?> { ["capa"] = nr.Nombre });
            }

            // Nombre no válido como símbolo → INVALID_PARAMS ANTES del UndoGroup (no deja un paso de deshacer vacío).
            try
            {
                SymbolUtilityServices.ValidateSymbolName(nr.Nombre, false);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                throw ToolException.InvalidParams(LayerResolver.InvalidName, new Dictionary<string, object?> { ["capa"] = nr.Nombre });
            }

            int pid = ctx.Pid;
            return UndoGroup.Run(doc, d => Create(d, p, nr, pid, ins, warnings));
        }

        /// <summary>Resuelve el nombre: libre → literal (propiedades del estándar si el nombre está en él); si no, <see cref="DrawLayerFactory.Plan"/>.</summary>
        private static Resolved Resolve(Params p)
        {
            if (p.Libre)
            {
                string literal = p.Nombre.Trim();
                if (literal.Length == 0)
                {
                    throw ToolException.InvalidParams(LayerResolver.InvalidName);
                }

                LayerSpec? spec = LayerResolver.SpecFor(literal, AuditSupport.LayerRules);
                return new Resolved(literal, LayerPlan.OrigenNombre, spec, IsStandard(literal, spec));
            }

            LayerPlan plan = DrawLayerFactory.Plan(p.Nombre, null);
            return new Resolved(plan.Nombre, plan.Origen, plan.Especificacion, IsStandard(plan.Nombre, plan.Especificacion));
        }

        private static bool IsStandard(string nombre, LayerSpec? spec) =>
            spec != null || SystemNames.Any(s => string.Equals(s, nombre, StringComparison.OrdinalIgnoreCase));

        /// <summary>Capa con xref (dependiente o con '|') → INVALID_PARAMS (mismo rechazo que el resto de escrituras de capas).</summary>
        private static void RejectXref(LayerTableRecord layer)
        {
            if (layer.IsDependent || layer.Name.IndexOf('|') >= 0)
            {
                throw ToolException.InvalidParams(DrawLayerFactory.XrefLayer, new Dictionary<string, object?> { ["capa"] = layer.Name });
            }
        }

        /// <summary>
        /// Lo pedido en forma comparable con <see cref="ReadValues"/>. Un tipo de línea no cargado se descarta (no se
        /// puede aplicar) con advertencia.
        /// </summary>
        private static LayerProps.Request BuildRequest(Params p, Database db, Transaction tr, List<string> warnings)
        {
            string? color = p.Aci is int aci ? LayerProps.FormatAci(aci)
                : p.Rgb is { } rgb ? LayerProps.FormatRgb(rgb.R, rgb.G, rgb.B)
                : null;
            string? tipo = p.TipoLinea;
            if (tipo != null && !LinetypeLoaded(db, tr, tipo))
            {
                warnings.Add("tipo_linea: '" + tipo + "' no está cargado en el dibujo; no se aplica ni se compara.");
                tipo = null;
            }

            return new LayerProps.Request(color, tipo, p.Grosor, p.Transparencia);
        }

        /// <summary>Propiedades actuales de la capa en el mismo formato que <see cref="BuildRequest"/>.</summary>
        private static LayerProps.Values ReadValues(LayerTableRecord ltr, Transaction tr)
        {
            Color c = ltr.Color;
            string color = c.IsByAci ? LayerProps.FormatAci(c.ColorIndex) : LayerProps.FormatRgb(c.Red, c.Green, c.Blue);
            int transp = ltr.Transparency.IsByAlpha ? LayerProps.PercentFromAlpha(ltr.Transparency.Alpha) : 0;
            return new LayerProps.Values(color, LinetypeName(ltr, tr), (int)ltr.LineWeight, transp);
        }

        private static string LinetypeName(LayerTableRecord ltr, Transaction tr)
        {
            try
            {
                if (!ltr.LinetypeObjectId.IsNull && tr.GetObject(ltr.LinetypeObjectId, OpenMode.ForRead) is LinetypeTableRecord lt)
                {
                    return lt.Name;
                }
            }
            catch (Exception)
            {
                // Tipo de línea ilegible: se informa Continuous.
            }

            return "Continuous";
        }

        private static bool LinetypeLoaded(Database db, Transaction tr, string name)
        {
            try
            {
                var lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                return lt.Has(name);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return false;
            }
        }

        /// <summary>Tipo de línea cargado con ese nombre, o Continuous con advertencia (como DrawLayerFactory).</summary>
        private static ObjectId ResolveLinetype(Database db, Transaction tr, string? name, string layer, List<string> warnings)
        {
            if (string.IsNullOrEmpty(name) || string.Equals(name, "Continuous", StringComparison.OrdinalIgnoreCase))
            {
                return db.ContinuousLinetype;
            }

            try
            {
                var lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                if (lt.Has(name))
                {
                    ObjectId id = lt[name];
                    if (!id.IsNull && !id.IsErased)
                    {
                        return id;
                    }
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Nombre inválido o tabla ilegible: Continuous.
            }

            warnings.Add("La capa " + layer + " se creó con tipo de línea Continuous: '" + name + "' no está cargado en el dibujo.");
            return db.ContinuousLinetype;
        }

        private static Color ColorOf(int? aci, (int R, int G, int B)? rgb, int defaultAci)
        {
            if (rgb is { } c)
            {
                return Color.FromRgb((byte)c.R, (byte)c.G, (byte)c.B);
            }

            return Color.FromColorIndex(ColorMethod.ByAci, (short)(aci ?? defaultAci));
        }

        /// <summary>Crea la capa en UNA transacción y responde con sus propiedades.</summary>
        private static object Create(Document doc, Params p, Resolved nr, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            LayerSpec basis = nr.Spec ?? LibreDefault;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                ObjectId existing = McpLayerFactory.FindLayer(table, nr.Nombre);
                if (!existing.IsNull)
                {
                    // Otro proceso la creó entre la lectura y la escritura: se responde sin cambios.
                    var ya = (LayerTableRecord)tr.GetObject(existing, OpenMode.ForRead);
                    Dictionary<string, object?> r0 = Build(pid, ya, tr, false, false, false, Array.Empty<LayerProps.Change>(), nr, ins, warnings);
                    tr.Commit();
                    return r0;
                }

                if (p.Rgb == null && p.Aci == null && (basis.Color < 1 || basis.Color > 255))
                {
                    warnings.Add("El color de lps.json de la capa " + nr.Nombre + " no es un ACI válido: se usa el ACI " + Math.Clamp(basis.Color, 1, 255).ToString(CultureInfo.InvariantCulture) + ".");
                }

                if (!LayerProps.IsValidLineWeight(p.Grosor ?? basis.Grosor))
                {
                    warnings.Add("El grosor de lps.json de la capa " + nr.Nombre + " no es válido: se usa Default (-3).");
                }

                int baseAci = Math.Clamp(basis.Color, 1, 255);
                int? aciCreate = p.Rgb == null ? (int?)(p.Aci ?? baseAci) : null;
                int grosor = p.Grosor ?? basis.Grosor;
                var ltr = new LayerTableRecord
                {
                    Name = nr.Nombre,
                    Color = ColorOf(aciCreate, p.Rgb, baseAci),
                    LinetypeObjectId = ResolveLinetype(db, tr, p.TipoLinea ?? basis.TipoLinea, nr.Nombre, warnings),
                    LineWeight = (LineWeight)(LayerProps.IsValidLineWeight(grosor) ? grosor : -3),
                    IsPlottable = basis.Plot,
                };
                if (p.Transparencia.HasValue)
                {
                    ltr.Transparency = new Transparency(LayerProps.AlphaFromPercent(p.Transparencia.Value));
                }

                table.UpgradeOpen();
                ObjectId id = table.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
                var creada = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                Dictionary<string, object?> r = Build(pid, creada, tr, true, false, true, Array.Empty<LayerProps.Change>(), nr, ins, warnings);
                tr.Commit();
                return r;
            }
        }

        /// <summary>Aplica a una capa existente las propiedades pedidas que difieren. Recalcula las diferencias dentro del paso de deshacer.</summary>
        private static object Modify(Document doc, Params p, string nombre, Resolved nr, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                ObjectId id = McpLayerFactory.FindLayer(table, nombre);
                if (id.IsNull)
                {
                    throw ToolException.InvalidParams("capa: la capa ya no existe", new Dictionary<string, object?> { ["capa"] = nombre });
                }

                var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                RejectXref(ltr);
                // Las advertencias de tipo de línea ya se añadieron en la lectura previa: aquí no se repiten.
                IReadOnlyList<LayerProps.Change> cambios = LayerProps.Diff(ReadValues(ltr, tr), BuildRequest(p, db, tr, new List<string>()));
                // Punto 12: si entre la lectura previa y este paso otro proceso ya aplicó lo pedido, cambios.Count es 0 y el
                // grupo de deshacer queda vacío. Es solo una carrera; no se evita aquí (UndoGroup abre el grupo antes).
                if (cambios.Count > 0)
                {
                    ltr.UpgradeOpen();
                    if (p.Aci != null || p.Rgb != null)
                    {
                        ltr.Color = ColorOf(p.Aci, p.Rgb, 7);
                    }

                    if (p.TipoLinea != null && LinetypeLoaded(db, tr, p.TipoLinea))
                    {
                        ltr.LinetypeObjectId = ResolveLinetype(db, tr, p.TipoLinea, nombre, warnings);
                    }

                    if (p.Grosor.HasValue)
                    {
                        ltr.LineWeight = (LineWeight)p.Grosor.Value;
                    }

                    if (p.Transparencia.HasValue)
                    {
                        ltr.Transparency = new Transparency(LayerProps.AlphaFromPercent(p.Transparencia.Value));
                    }
                }

                PluginLog.Info("crear_capa: '" + nombre + "' con " + cambios.Count.ToString(CultureInfo.InvariantCulture) + " cambio(s) de propiedades.");
                Dictionary<string, object?> r = Build(pid, ltr, tr, false, cambios.Count > 0, cambios.Count > 0, cambios, nr, ins, warnings);
                tr.Commit();
                return r;
            }
        }

        private static Dictionary<string, object?> Build(
            int pid, LayerTableRecord ltr, Transaction tr, bool creada, bool modificada, bool escrito,
            IReadOnlyList<LayerProps.Change> cambios, Resolved nr, int ins, List<string> warnings)
        {
            Color c = ltr.Color;
            Transparency t = ltr.Transparency;
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = pid,
                ["capa"] = ltr.Name,
                ["creada"] = creada,
                ["modificada"] = modificada,
                ["escrito"] = escrito,
                ["cambios"] = cambios.Select(ch => (object)new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["campo"] = ch.Campo,
                    ["antes"] = ch.Antes,
                    ["despues"] = ch.Despues,
                }).ToList(),
                ["color"] = c.IsByAci ? (int)c.ColorIndex : -1,
                ["color_texto"] = c.IsByAci ? LayerProps.FormatAci(c.ColorIndex) : LayerProps.FormatRgb(c.Red, c.Green, c.Blue),
                ["tipo_linea"] = LinetypeName(ltr, tr),
                ["grosor"] = (int)ltr.LineWeight,
                ["transparencia"] = t.IsByAlpha ? LayerProps.PercentFromAlpha(t.Alpha) : 0,
                ["plot"] = ltr.IsPlottable,
                ["bloqueada"] = ltr.IsLocked,
                ["congelada"] = ltr.IsFrozen,
                ["apagada"] = ltr.IsOff,
                ["origen"] = nr.Origen == LayerPlan.OrigenConcepto ? LayerPlan.OrigenConcepto : LayerPlan.OrigenNombre,
                ["estandar"] = nr.Estandar,
                ["insunits"] = ins,
                ["unidades"] = UnitsTable.Name(ins),
                ["advertencias"] = warnings,
            };
        }
    }
}
