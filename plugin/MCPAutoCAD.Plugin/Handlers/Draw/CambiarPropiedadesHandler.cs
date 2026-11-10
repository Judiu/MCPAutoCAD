using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Clean;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// cambiar_propiedades (0.9). Hilo principal; requiere documento. Cambia color, tipo de línea, escala de tipo, grosor y
    /// transparencia de entidades EXISTENTES, por handle (1–500), en un solo paso de deshacer. Modelo: cambiar_capa
    /// (<see cref="HandleGuard"/> para las comprobaciones de protección; la pasada de lectura sin efectos va antes del
    /// <see cref="UndoGroup"/> y la escritura vuelve a resolver los handles dentro). Modo de llamada: el de
    /// <see cref="CleanWrite"/>: sin confirmar: true (o con preview: true) solo PREVISUALIZA lo que haría, antes y después
    /// por entidad (resumido si son muchas); con confirmar: true escribe. Las entidades fuera de capas MCP-* exigen
    /// confirmar: true, como en cambiar_capa. Rechaza handles inexistentes, atributos, entidades dentro de bloques,
    /// viewports de papel y entidades en capas bloqueadas o de xref.
    /// </summary>
    internal sealed class CambiarPropiedadesHandler : IToolHandler
    {
        /// <summary>Cuántas entidades se detallan (antes/después) en la respuesta.</summary>
        public const int MaxDetail = 50;

        private static readonly string[] Own =
        {
            "handles", "tipo_linea", "escala_tipo_linea", "grosor", "transparencia",
        };

        private static readonly string[] Allowed = PrimitiveSupport.Concat(
            PrimitiveSupport.Concat(Own, EntityColor.ParamNames),
            CleanWrite.CommonParams);

        private const string KeyColor = "color";
        private const string KeyTipo = "tipo_linea";
        private const string KeyEscala = "escala_tipo_linea";
        private const string KeyGrosor = "grosor";
        private const string KeyTransp = "transparencia";

        private static readonly string[] KeyOrder = { KeyColor, KeyTipo, KeyEscala, KeyGrosor, KeyTransp };

        public string Name => "cambiar_propiedades";

        public string Description =>
            "CAMBIA propiedades visuales de entidades existentes (por handle, 1 a 500): color (ACI, 'porcapa'/'porbloque' o color_rgb), " +
            "tipo de línea (cargado en el dibujo o de acad.lin/acadiso.lin), escala del tipo de línea, grosor (mm válidos) y transparencia (0–90 %), " +
            "en un solo paso de deshacer. Sin confirmar: true solo PREVISUALIZA (antes y después por entidad) y no modifica nada; con confirmar: true escribe. " +
            "Rechaza handles inexistentes, atributos, entidades dentro de bloques, viewports de papel y capas bloqueadas o de xref.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""handles"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": " + ParamReader.SchemaHandle + @", ""description"": ""Handles de las entidades a cambiar (1–500, sin repetidos). No se admiten atributos, entidades dentro de bloques, viewports de papel ni capas bloqueadas o de xref."" },
    ""color"": " + EntityColor.SchemaColor + @",
    ""color_rgb"": " + EntityColor.SchemaColorRgb + @",
    ""tipo_linea"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre de un tipo de línea del dibujo; si no está cargado se intenta cargar de acad.lin (MEASUREMENT imperial) o acadiso.lin (métrico). 'porcapa' o 'porbloque' también valen."" },
    ""escala_tipo_linea"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Escala del tipo de línea (> 0)."" },
    ""grosor"": { ""anyOf"": [ { ""type"": ""number"", ""enum"": [0, 0.05, 0.09, 0.13, 0.15, 0.18, 0.2, 0.25, 0.3, 0.35, 0.4, 0.5, 0.53, 0.6, 0.7, 0.8, 0.9, 1, 1.06, 1.2, 1.4, 1.58, 2, 2.11] }, { ""type"": ""string"", ""enum"": [""porcapa"", ""porbloque"", ""defecto""] } ], ""description"": ""Grosor en mm (valores válidos de AutoCAD), 'porcapa', 'porbloque' o 'defecto'."" },
    ""transparencia"": { ""anyOf"": [ { ""type"": ""number"", ""minimum"": 0, ""maximum"": 90 }, { ""type"": ""string"", ""enum"": [""porcapa"", ""porbloque""] } ], ""description"": ""Transparencia en % (0–90; 0 = opaco) o 'porcapa' / 'porbloque'."" },
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""handles""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""n"": { ""type"": ""integer"" },
    ""n_usuario"": { ""type"": ""integer"" },
    ""n_cambian"": { ""type"": ""integer"" },
    ""n_sin_cambio"": { ""type"": ""integer"" },
    ""espacio"": { ""type"": ""string"" },
    ""capas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""pedido"": { ""type"": ""object"", ""description"": ""Valores pedidos, en forma legible."" },
    ""resumen"": { ""type"": ""object"", ""description"": ""Por propiedad: antes y después, con cuántas entidades tienen cada valor."" },
    ""detalle"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""Antes y después por entidad (hasta 50)."" },
    ""detalle_truncado"": { ""type"": ""boolean"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""handles"", ""n"", ""n_usuario"", ""n_cambian"", ""n_sin_cambio"", ""espacio"", ""capas"", ""pedido"", ""resumen"", ""detalle"", ""detalle_truncado"", ""insunits"", ""unidades"", ""advertencias""]
}");

        /// <summary>Tipo de línea pedido: nombre, o "porcapa" / "porbloque".</summary>
        private sealed class Props
        {
            public ColorSpec? Color { get; set; }

            /// <summary>Nombre, "porcapa" o "porbloque"; null = no se pide.</summary>
            public string? Tipo { get; set; }

            public double? Escala { get; set; }

            /// <summary>"porcapa", "porbloque", "defecto" o el grosor en centésimas de mm (como texto "0.25").</summary>
            public string? Grosor { get; set; }

            /// <summary>Centésimas de mm si Grosor es un valor numérico.</summary>
            public int GrosorHundredths { get; set; }

            /// <summary>"porcapa", "porbloque" o el porcentaje ya redondeado a lo que AutoCAD muestra; null = no se pide.</summary>
            public string? Transp { get; set; }

            /// <summary>Porcentaje pedido (si es numérico).</summary>
            public double TranspPct { get; set; }

            public bool Any => Color != null || Tipo != null || Escala != null || Grosor != null || Transp != null;

            public bool Pide(string key) => key switch
            {
                KeyColor => Color != null,
                KeyTipo => Tipo != null,
                KeyEscala => Escala != null,
                KeyGrosor => Grosor != null,
                _ => Transp != null,
            };

            /// <summary>Valor pedido en forma de texto (el mismo formato que <see cref="Snapshot"/>).</summary>
            public string Texto(string key) => key switch
            {
                KeyColor => ColorRules.Describe(Color!.Value),
                KeyTipo => Tipo!,
                KeyEscala => FormatNumber(Escala!.Value),
                KeyGrosor => Grosor!,
                _ => Transp!,
            };
        }

        private sealed class Params
        {
            public Params(List<string> handles, Props props, CleanWrite.Common common)
            {
                Handles = handles;
                Props = props;
                Common = common;
            }

            public List<string> Handles { get; }
            public Props Props { get; }
            public CleanWrite.Common Common { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            List<string> handles = ParamReader.RequiredHandles(p, "handles", 1, EntityPropertyRules.MaxHandles);
            var props = new Props();

            props.Color = EntityColor.Read(p);

            string? tipo = ParamReader.OptionalString(p, "tipo_linea", 1, 255);
            if (tipo != null)
            {
                string t = tipo.Trim();
                if (t.Length == 0)
                {
                    throw ToolException.InvalidParams("tipo_linea: debe tener al menos 1 carácter(es)");
                }

                bool palabra = string.Equals(t, "porcapa", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t, "porbloque", StringComparison.OrdinalIgnoreCase);
                if (!palabra)
                {
                    // Comodines y caracteres inválidos se rechazan antes de intentar cargar nada del fichero de tipos.
                    string? nombreError = EntityPropertyRules.LinetypeNameError(t);
                    if (nombreError != null)
                    {
                        throw ToolException.InvalidParams(nombreError);
                    }
                }

                props.Tipo = palabra ? t.ToLowerInvariant() : t;
            }

            props.Escala = ParamReader.OptionalNumber(p, "escala_tipo_linea", 0, double.PositiveInfinity, exclusiveMin: true);

            if (ParamReader.Has(p, "grosor"))
            {
                JsonElement g = p.GetProperty("grosor");
                if (g.ValueKind == JsonValueKind.String && EntityPropertyRules.IsLineWeightWord(g.GetString() ?? "", out string word))
                {
                    props.Grosor = word;
                }
                else if (g.ValueKind == JsonValueKind.Number && g.TryGetDouble(out double mm) && EntityPropertyRules.TryLineWeightHundredths(mm, out int h))
                {
                    props.GrosorHundredths = h;
                    props.Grosor = FormatMm(h);
                }
                else if (g.ValueKind == JsonValueKind.Number && g.TryGetDouble(out double bad))
                {
                    throw ToolException.InvalidParams(EntityPropertyRules.BadLineWeight(bad));
                }
                else
                {
                    throw ToolException.InvalidParams("grosor: debe ser un número (mm válidos) o porcapa, porbloque, defecto");
                }
            }

            if (ParamReader.Has(p, "transparencia"))
            {
                JsonElement tr = p.GetProperty("transparencia");
                string word = tr.ValueKind == JsonValueKind.String ? (tr.GetString() ?? "").Trim().ToLowerInvariant() : "";
                if (word == "porcapa" || word == "porbloque")
                {
                    props.Transp = word;
                }
                else if (tr.ValueKind == JsonValueKind.Number && tr.TryGetDouble(out double pct) && EntityPropertyRules.IsTransparencyPercent(pct))
                {
                    // Lo que AutoCAD mostrará tras redondear el alfa (mismo formato que Current).
                    props.TranspPct = pct;
                    props.Transp = PercentText(EntityPropertyRules.PercentFromAlpha(EntityPropertyRules.AlphaFromPercent(pct)));
                }
                else
                {
                    throw ToolException.InvalidParams("transparencia: debe ser un número entre 0 y 90 (%), porcapa o porbloque");
                }
            }

            if (!props.Any)
            {
                throw ToolException.InvalidParams(EntityPropertyRules.NoProperties);
            }

            CleanWrite.Common common = CleanWrite.ReadCommon(p);
            return new Params(handles, props, common);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(p.Common.Confirmar, p.Common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = CleanWrite.ReadUnits(db);

            // Lectura sin efectos: protección (pasos 1–5 de §5D.8; la confirmación se trata abajo) y "antes".
            HandleGuard.Resolved first;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                first = HandleGuard.Resolve(db, tr, p.Handles, true, warnings);
                tr.Commit();
            }

            // Previsualización: no se carga nada. Si el tipo ya está cargado, se usa su nombre real (la escritura compara igual);
            // si no, solo se avisa de que se cargará al escribir.
            if (mode == CleanWrite.Mode.Preview && p.Props.Tipo != null && !IsKeyword(p.Props.Tipo))
            {
                string? real = LoadedName(db, p.Props.Tipo);
                if (real != null)
                {
                    p.Props.Tipo = real;
                }
                else
                {
                    warnings.Add("tipo_linea: '" + p.Props.Tipo + "' no está cargado: al escribir se intentará cargar de "
                        + EntityPropertyRules.LinetypeFileFor(AcadUtil.Measurement(db)) + ".");
                }
            }

            if (mode == CleanWrite.Mode.Preview)
            {
                if (first.NUsuario > 0)
                {
                    warnings.Add("cambiar_propiedades: " + first.NUsuario + " entidad(es) fuera de capas MCP-*: con confirmar: true se cambiarían.");
                }

                var antesPrev = new List<Dictionary<string, string>>(first.Items.Count);
                var despuesPrev = new List<Dictionary<string, string>>(first.Items.Count);
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (HandleGuard.Item item in first.Items)
                    {
                        Entity e = (Entity)tr.GetObject(item.Id, OpenMode.ForRead, false);
                        Dictionary<string, string> antes = Snapshot(e, p.Props);
                        antesPrev.Add(antes);
                        despuesPrev.Add(Requested(antes, p.Props));
                    }

                    tr.Commit();
                }

                int pidPrev = ctx.Pid;
                return Build(pidPrev, mode, ins, warnings, first, p, antesPrev, despuesPrev);
            }

            int pid = ctx.Pid;
            return UndoGroup.Run(doc, d => Write(d, p, pid, ins, warnings));
        }

        /// <summary>
        /// Escritura en UNA transacción: carga el tipo de línea pedido (si hace falta, fuera de la transacción, dentro del
        /// paso de deshacer), vuelve a resolver los handles (el guard de esta segunda pasada manda), aplica las propiedades
        /// y lee cada entidad de nuevo para el "después". Cualquier excepción descarta la transacción.
        /// </summary>
        private static object Write(Document doc, Params p, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            ObjectId linetypeId = ObjectId.Null;
            if (p.Props.Tipo != null && !IsKeyword(p.Props.Tipo))
            {
                linetypeId = ResolveLinetype(db, p.Props.Tipo, warnings, load: true);
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                HandleGuard.Resolved res = HandleGuard.Resolve(db, tr, p.Handles, true, new List<string>());
                var antes = new List<Dictionary<string, string>>(res.Items.Count);
                var despues = new List<Dictionary<string, string>>(res.Items.Count);
                foreach (HandleGuard.Item item in res.Items)
                {
                    antes.Add(Snapshot(item.Entity, p.Props));
                    item.Entity.UpgradeOpen();
                    Apply(item.Entity, p.Props, db, linetypeId);
                    despues.Add(Snapshot(item.Entity, p.Props));
                }

                tr.Commit();
                return Build(pid, CleanWrite.Mode.Write, ins, warnings, res, p, antes, despues);
            }
        }

        private static Dictionary<string, object?> Build(
            int pid,
            CleanWrite.Mode mode,
            int ins,
            List<string> warnings,
            HandleGuard.Resolved res,
            Params p,
            List<Dictionary<string, string>> antes,
            List<Dictionary<string, string>> despues)
        {
            int n = res.Items.Count;
            var handles = new List<string>(n);
            int cambian = 0;
            var resumenAntes = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            var resumenDespues = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            var detalle = new List<object>(Math.Min(n, MaxDetail));
            for (int i = 0; i < n; i++)
            {
                handles.Add(res.Items[i].Info.Handle);
                Dictionary<string, string> a = antes[i];
                Dictionary<string, string> d = despues[i];
                if (KeysDiffer(a, d, p.Props))
                {
                    cambian++;
                }

                foreach (string key in KeyOrder)
                {
                    if (!p.Props.Pide(key))
                    {
                        continue;
                    }

                    Count(resumenAntes, key, a[key]);
                    Count(resumenDespues, key, d[key]);
                }

                if (i < MaxDetail)
                {
                    detalle.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["handle"] = res.Items[i].Info.Handle,
                        ["capa"] = res.Items[i].Info.Capa,
                        ["antes"] = Pick(a, p.Props),
                        ["despues"] = Pick(d, p.Props),
                    });
                }
            }

            var resumen = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string key in KeyOrder)
            {
                if (!p.Props.Pide(key))
                {
                    continue;
                }

                resumen[key] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["antes"] = resumenAntes.TryGetValue(key, out Dictionary<string, int>? ra) ? CleanWrite.SortedCounts(ra) : new Dictionary<string, object?>(),
                    ["despues"] = resumenDespues.TryGetValue(key, out Dictionary<string, int>? rd) ? CleanWrite.SortedCounts(rd) : new Dictionary<string, object?>(),
                };
            }

            var pedido = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string key in KeyOrder)
            {
                if (p.Props.Pide(key))
                {
                    pedido[key] = p.Props.Texto(key);
                }
            }

            Dictionary<string, object?> r = CleanWrite.Response(pid, mode, ins, warnings);
            r["handles"] = handles;
            r["n"] = n;
            r["n_usuario"] = res.NUsuario;
            r["n_cambian"] = cambian;
            r["n_sin_cambio"] = n - cambian;
            r["espacio"] = res.Espacio;
            r["capas"] = res.Capas;
            r["pedido"] = pedido;
            r["resumen"] = resumen;
            r["detalle"] = detalle;
            r["detalle_truncado"] = n > MaxDetail;
            return r;
        }

        /// <summary>Aplica las propiedades pedidas a una entidad ya abierta para escritura.</summary>
        private static void Apply(Entity e, Props props, Database db, ObjectId namedLinetype)
        {
            if (props.Color != null)
            {
                e.Color = EntityColor.ToColor(props.Color.Value);
            }

            if (props.Tipo != null)
            {
                e.LinetypeId = props.Tipo == "porcapa" ? db.ByLayerLinetype
                    : props.Tipo == "porbloque" ? db.ByBlockLinetype
                    : namedLinetype;
            }

            if (props.Escala != null)
            {
                e.LinetypeScale = props.Escala.Value;
            }

            if (props.Grosor != null)
            {
                e.LineWeight = props.Grosor switch
                {
                    "porcapa" => LineWeight.ByLayer,
                    "porbloque" => LineWeight.ByBlock,
                    "defecto" => LineWeight.ByLineWeightDefault,
                    // Los valores del enum LineWeight son las centésimas de mm (verificado en acdbmgd.dll).
                    _ => (LineWeight)props.GrosorHundredths,
                };
            }

            if (props.Transp != null)
            {
                e.Transparency = props.Transp == "porcapa" ? new Transparency(TransparencyMethod.ByLayer)
                    : props.Transp == "porbloque" ? new Transparency(TransparencyMethod.ByBlock)
                    : new Transparency(EntityPropertyRules.AlphaFromPercent(props.TranspPct));
            }
        }

        /// <summary>Valores actuales de las propiedades PEDIDAS, en texto (mismo formato que <see cref="Props.Texto"/>).</summary>
        private static Dictionary<string, string> Snapshot(Entity e, Props props)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in KeyOrder)
            {
                if (props.Pide(key))
                {
                    d[key] = Current(e, key);
                }
            }

            return d;
        }

        private static string Current(Entity e, string key)
        {
            switch (key)
            {
                case KeyColor:
                    return EntityColor.Describe(e.Color);
                case KeyTipo:
                    string lt = e.Linetype ?? "";
                    return string.Equals(lt, "ByLayer", StringComparison.OrdinalIgnoreCase) ? "porcapa"
                        : string.Equals(lt, "ByBlock", StringComparison.OrdinalIgnoreCase) ? "porbloque"
                        : lt;
                case KeyEscala:
                    return FormatNumber(e.LinetypeScale);
                case KeyGrosor:
                    LineWeight lw = e.LineWeight;
                    if (lw == LineWeight.ByLayer)
                    {
                        return "porcapa";
                    }

                    if (lw == LineWeight.ByBlock)
                    {
                        return "porbloque";
                    }

                    if (lw == LineWeight.ByLineWeightDefault)
                    {
                        return "defecto";
                    }

                    return FormatMm((int)lw);
                default:
                    Transparency t = e.Transparency;
                    if (t.IsByLayer)
                    {
                        return "porcapa";
                    }

                    if (t.IsByBlock)
                    {
                        return "porbloque";
                    }

                    return PercentText(EntityPropertyRules.PercentFromAlpha(t.Alpha));
            }
        }

        /// <summary>Antes con las claves pedidas reemplazadas por lo que se pediría (previsualización).</summary>
        private static Dictionary<string, string> Requested(Dictionary<string, string> antes, Props props)
        {
            var d = new Dictionary<string, string>(antes, StringComparer.Ordinal);
            foreach (string key in KeyOrder)
            {
                if (props.Pide(key))
                {
                    d[key] = props.Texto(key);
                }
            }

            return d;
        }

        private static bool KeysDiffer(Dictionary<string, string> a, Dictionary<string, string> d, Props props)
        {
            foreach (string key in KeyOrder)
            {
                // El nombre del tipo de línea no distingue mayúsculas en AutoCAD (LINETYPE "dashed" = "DASHED").
                StringComparison cmp = key == KeyTipo ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (props.Pide(key) && !string.Equals(a[key], d[key], cmp))
                {
                    return true;
                }
            }

            return false;
        }

        private static Dictionary<string, object?> Pick(Dictionary<string, string> d, Props props)
        {
            var r = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (string key in KeyOrder)
            {
                if (props.Pide(key))
                {
                    r[key] = d[key];
                }
            }

            return r;
        }

        private static void Count(Dictionary<string, Dictionary<string, int>> into, string key, string value)
        {
            if (!into.TryGetValue(key, out Dictionary<string, int>? map))
            {
                map = new Dictionary<string, int>(StringComparer.Ordinal);
                into[key] = map;
            }

            map.TryGetValue(value, out int n);
            map[value] = n + 1;
        }

        private static bool IsKeyword(string tipo) => tipo == "porcapa" || tipo == "porbloque";

        /// <summary>Nombre real (con sus mayúsculas) del tipo de línea cargado con ese nombre, o null.</summary>
        private static string? LoadedName(Database db, string name)
        {
            ObjectId id = FindLinetype(db, name);
            if (id.IsNull)
            {
                return null;
            }

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var rec = (LinetypeTableRecord)tr.GetObject(id, OpenMode.ForRead);
                string real = rec.Name;
                tr.Commit();
                return real;
            }
        }

        /// <summary>Id del tipo de línea cargado con ese nombre (sin distinguir mayúsculas), o Null.</summary>
        private static ObjectId FindLinetype(Database db, string name)
        {
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                ObjectId id = ObjectId.Null;
                try
                {
                    if (table.Has(name))
                    {
                        id = table[name];
                    }
                }
                catch (Exception)
                {
                    id = ObjectId.Null;
                }

                tr.Commit();
                return id.IsNull || id.IsErased ? ObjectId.Null : id;
            }
        }

        /// <summary>
        /// Tipo de línea por nombre. Si no está cargado y <paramref name="load"/>, lo carga de acad.lin (MEASUREMENT
        /// imperial) o acadiso.lin (métrico) FUERA de cualquier transacción (dentro del paso de deshacer). Si no existe
        /// tras intentarlo → INVALID_PARAMS con las opciones cargadas. Sin load devuelve Null si no está cargado.
        /// </summary>
        private static ObjectId ResolveLinetype(Database db, string name, List<string> warnings, bool load)
        {
            ObjectId id = FindLinetype(db, name);
            if (!id.IsNull || !load)
            {
                return id;
            }

            string file = EntityPropertyRules.LinetypeFileFor(AcadUtil.Measurement(db));
            try
            {
                SymbolUtilityServices.ValidateSymbolName(name, false);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                throw ToolException.InvalidParams(
                    "tipo_linea: '" + name + "' no es un nombre de tipo de línea válido.");
            }

            try
            {
                db.LoadLineTypeFile(name, file);
            }
            catch (Exception)
            {
                // Nombre no encontrado en el fichero o nombre inválido: se trata abajo.
            }

            id = FindLinetype(db, name);
            if (!id.IsNull)
            {
                warnings.Add("tipo de línea '" + name + "' cargado de " + file + ".");
                return id;
            }

            throw ToolException.InvalidParams(
                "tipo_linea: '" + name + "' no está cargado en el dibujo ni en " + file + ".",
                new Dictionary<string, object?> { ["opciones"] = LoadedLinetypes(db) });
        }

        private static List<string> LoadedLinetypes(Database db)
        {
            var names = new List<string>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    var rec = (LinetypeTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    names.Add(rec.Name);
                }

                tr.Commit();
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.Take(100).ToList();
        }

        private static string FormatNumber(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string FormatMm(int hundredths) => (hundredths / 100.0).ToString("0.00", CultureInfo.InvariantCulture);

        private static string PercentText(double pct) => Math.Round(pct, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
    }
}
