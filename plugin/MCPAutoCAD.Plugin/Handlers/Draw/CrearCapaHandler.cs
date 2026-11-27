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
        private static readonly string[] Allowed = { "nombre", "capas", "libre", "color", "tipo_linea", "grosor", "transparencia", "confirmar" };
        private static readonly string[] BatchItemAllowed = { "nombre", "libre", "color", "tipo_linea", "grosor", "transparencia" };

        /// <summary>Propiedades por defecto de una capa cuyo nombre no está en lps.json: ACI 7, Continuous, grosor Default, imprimible.</summary>
        private static readonly LayerSpec LibreDefault = new LayerSpec(7, "Continuous", -3, true);

        /// <summary>Nombres que AutoCAD crea siempre y que no son del estándar LPS pero no merecen aviso.</summary>
        private static readonly string[] SystemNames = { "0", "DEFPOINTS" };

        public string Name => "crear_capa";

        public string Description =>
            "ESCRIBE en el dibujo solo si hace falta: crea una capa ('nombre') o un lote de capas ('capas': nombres, conceptos " +
            "o patrones con * y ? para capas existentes, cada elemento como texto o {nombre, libre?, color?, tipo_linea?, grosor?, " +
            "transparencia?}) con color, tipo de línea, grosor y transparencia opcionales, en un solo paso de deshacer. Si una capa " +
            "ya existe no la recrea: devuelve sus propiedades y, si se piden propiedades distintas, solo previsualiza los cambios " +
            "antes/después hasta que se pase confirmar: true.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        private const string SchemaColorLayer =
            @"{ ""anyOf"": [ { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 255, ""description"": ""Índice ACI."" }, { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 255 }, ""description"": ""Color verdadero [r, g, b]."" } ], ""description"": ""Color de la capa: ACI 1–255 o [r, g, b]. Defecto: el de lps.json (o ACI 7 si el nombre no está en el estándar)."" }";

        private const string SchemaGrosorLayer =
            @"{ ""type"": ""integer"", ""minimum"": -3, ""maximum"": 211, ""description"": ""Grosor de AutoCAD en centésimas de mm (0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211) o -3 Default (ByLayer y ByBlock no valen en una capa)."" }";

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""nombre"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Una sola capa: concepto de lps.json (muros, cotas, textos, ejes, columnas, vigas, puertas, ventanas, filos, achurados, mobiliario_fijo, sanitarios, estructura, mamposteria, niveles, escaleras, proyeccion, rotulo), nombre de capas_permitidas/capas_mcp, nombre MCP-*, o cualquier nombre válido (si no está en el estándar se rechaza salvo libre: true). Excluyente con 'capas'."" },
    ""capas"": {
      ""type"": ""array"",
      ""minItems"": 1,
      ""maxItems"": 100,
      ""items"": {
        ""anyOf"": [
          { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 },
          {
            ""type"": ""object"",
            ""properties"": {
              ""nombre"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 },
              ""libre"": { ""type"": ""boolean"" },
              ""color"": " + SchemaColorLayer + @",
              ""tipo_linea"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 },
              ""grosor"": " + SchemaGrosorLayer + @",
              ""transparencia"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 90 }
            },
            ""required"": [""nombre""],
            ""additionalProperties"": false
          }
        ]
      },
      ""description"": ""Modo lote (excluyente con 'nombre'): 1 a 100 capas a crear o modificar en un solo paso de deshacer. Cada entrada puede ser un nombre/concepto/patrón (*, ?) o un objeto {nombre, libre?, color?, tipo_linea?, grosor?, transparencia?} que hereda los valores globales como defecto.""
    },
    ""libre"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true: 'nombre' se usa tal cual, aunque coincida con un concepto de lps.json (p. ej. 'achurados' crea la capa 'achurados' y no la capa del concepto). En lote actúa como valor por defecto."" },
    ""color"": " + SchemaColorLayer + @",
    ""tipo_linea"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Tipo de línea de la capa (p. ej. Continuous, DASHDOT, DASHED, CENTER). Si no está cargado en el dibujo, al escribir se intenta cargar automáticamente de acadiso.lin/acad.lin; si tampoco existe allí se usa Continuous y se avisa. En lote actúa como valor por defecto."" },
    ""grosor"": " + SchemaGrosorLayer + @",
    ""transparencia"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 90, ""description"": ""Porcentaje de transparencia (0 = opaca). En lote actúa como valor por defecto."" },
    ""confirmar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = aplicar las propiedades pedidas a capas que YA EXISTEN con otras propiedades. Sin él, las capas existentes con cambios no se modifican y se devuelven sus diferencias antes/después."" }
  },
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
    ""preview"": { ""type"": ""boolean"" },
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
    ""capas"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""Solo en modo lote ('capas'): resultado por cada capa procesada."" },
    ""n_creadas"": { ""type"": ""integer"" },
    ""n_modificadas"": { ""type"": ""integer"" },
    ""n_sin_cambio"": { ""type"": ""integer"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""escrito"", ""insunits"", ""unidades"", ""advertencias""]
}");

        /// <summary>Especificación individual de capa ya validada (sin AutoCAD).</summary>
        private sealed class LayerItem
        {
            public LayerItem(string nombre, bool libre, int? aci, (int R, int G, int B)? rgb, string? tipoLinea, int? grosor, int? transparencia)
            {
                Nombre = nombre;
                Libre = libre;
                Aci = aci;
                Rgb = rgb;
                TipoLinea = tipoLinea;
                Grosor = grosor;
                Transparencia = transparencia;
            }

            public string Nombre { get; }
            public bool Libre { get; }
            public int? Aci { get; }
            public (int R, int G, int B)? Rgb { get; }
            public string? TipoLinea { get; }
            public int? Grosor { get; }
            public int? Transparencia { get; }
        }

        /// <summary>Parámetros ya leídos (sin AutoCAD): modo singular o lote.</summary>
        private sealed class Params
        {
            public Params(LayerItem? singular, List<LayerItem>? batch, bool confirmar)
            {
                Singular = singular;
                Batch = batch;
                Confirmar = confirmar;
            }

            public LayerItem? Singular { get; }
            public List<LayerItem>? Batch { get; }
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

        private sealed class BatchEntry
        {
            public BatchEntry(LayerItem item, Resolved resolved, ObjectId existingId, IReadOnlyList<LayerProps.Change> cambios)
            {
                Item = item;
                Resolved = resolved;
                ExistingId = existingId;
                Cambios = cambios;
            }

            public LayerItem Item { get; }
            public Resolved Resolved { get; }
            public ObjectId ExistingId { get; }
            public IReadOnlyList<LayerProps.Change> Cambios { get; }
            public bool Exists => !ExistingId.IsNull;
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            bool hasNombre = ParamReader.Has(p, "nombre");
            bool hasCapas = ParamReader.Has(p, "capas");
            if (hasNombre == hasCapas)
            {
                throw ToolException.InvalidParams(hasNombre
                    ? "nombre y capas son excluyentes: indique uno u otro"
                    : "nombre o capas: debe indicar nombre (una capa) o capas (lote)");
            }

            bool defLibre = ParamReader.OptionalBool(p, "libre") ?? false;
            (int? defAci, (int, int, int)? defRgb) = ReadColor(p, "");
            string? defTipo = ParamReader.OptionalString(p, "tipo_linea", 1, 255);
            int? defGrosor = ReadGrosor(p, "");
            int? defTransp = ParamReader.OptionalInt(p, "transparencia", 0, LayerProps.MaxTransparenciaPct);
            bool confirmar = ParamReader.OptionalBool(p, "confirmar") ?? false;

            if (hasNombre)
            {
                string nombre = ParamReader.RequiredText(p, "nombre", 1, 255);
                var single = new LayerItem(nombre, defLibre, defAci, defRgb, defTipo, defGrosor, defTransp);
                return new Params(single, null, confirmar);
            }

            JsonElement arr = p.GetProperty("capas");
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 1 || arr.GetArrayLength() > 100)
            {
                throw ToolException.InvalidParams("capas: debe ser un array de 1 a 100 elementos");
            }

            var items = new List<LayerItem>(arr.GetArrayLength());
            int idx = 0;
            foreach (JsonElement el in arr.EnumerateArray())
            {
                string prefix = string.Format(CultureInfo.InvariantCulture, "capas[{0}].", idx);
                if (el.ValueKind == JsonValueKind.String)
                {
                    string n = el.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(n) || n.Length > 255)
                    {
                        throw ToolException.InvalidParams(prefix.TrimEnd('.') + ": el nombre de capa debe tener entre 1 y 255 caracteres");
                    }

                    items.Add(new LayerItem(n, defLibre, defAci, defRgb, defTipo, defGrosor, defTransp));
                }
                else if (el.ValueKind == JsonValueKind.Object)
                {
                    ParamReader.RejectUnknown(el, BatchItemAllowed, prefix);
                    string n = RequiredTextPrefixed(el, "nombre", 1, 255, prefix);
                    bool libre = OptionalBoolPrefixed(el, "libre", prefix) ?? defLibre;
                    (int? aci, (int, int, int)? rgb) = ParamReader.Has(el, "color")
                        ? ReadColor(el, prefix)
                        : (defAci, defRgb);
                    string? tipo = OptionalStringPrefixed(el, "tipo_linea", 1, 255, prefix) ?? defTipo;
                    int? grosor = ParamReader.Has(el, "grosor") ? ReadGrosor(el, prefix) : defGrosor;
                    int? transp = ParamReader.Has(el, "transparencia")
                        ? OptionalIntPrefixed(el, "transparencia", 0, LayerProps.MaxTransparenciaPct, prefix)
                        : defTransp;
                    items.Add(new LayerItem(n, libre, aci, rgb, tipo, grosor, transp));
                }
                else
                {
                    throw ToolException.InvalidParams(prefix.TrimEnd('.') + ": debe ser un nombre de capa o un objeto {nombre, ...}");
                }

                idx++;
            }

            return new Params(null, items, confirmar);
        }

        private static int? ReadGrosor(JsonElement p, string prefix)
        {
            int? grosor = OptionalIntPrefixed(p, "grosor", -3, 211, prefix);
            if (grosor.HasValue && !LayerProps.IsValidLineWeight(grosor.Value))
            {
                throw ToolException.InvalidParams(prefix + LayerProps.InvalidLineWeight);
            }

            return grosor;
        }

        /// <summary>'color': entero ACI 1–255 o array [r, g, b] de 0–255.</summary>
        private static (int? aci, (int, int, int)? rgb) ReadColor(JsonElement p, string prefix)
        {
            if (!p.TryGetProperty("color", out JsonElement el))
            {
                return (null, null);
            }

            if (el.ValueKind == JsonValueKind.Array)
            {
                if (el.GetArrayLength() != 3)
                {
                    throw ToolException.InvalidParams(prefix + LayerProps.InvalidColor);
                }

                var c = new int[3];
                int i = 0;
                foreach (JsonElement x in el.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out int v) || v < 0 || v > 255)
                    {
                        throw ToolException.InvalidParams(prefix + LayerProps.InvalidColor);
                    }

                    c[i++] = v;
                }

                return (null, (c[0], c[1], c[2]));
            }

            return (OptionalIntPrefixed(p, "color", 1, 255, prefix), null);
        }

        private static string RequiredTextPrefixed(JsonElement obj, string name, int minLen, int maxLen, string prefix)
        {
            try
            {
                return ParamReader.RequiredText(obj, name, minLen, maxLen);
            }
            catch (ToolException ex) when (prefix.Length > 0 && ex.Code == ErrorCodes.InvalidParams)
            {
                string det = ex.ExtraData.TryGetValue("detalle", out object? d) ? d?.ToString() ?? "" : "";
                throw ToolException.InvalidParams(prefix + det);
            }
        }

        private static bool? OptionalBoolPrefixed(JsonElement obj, string name, string prefix)
        {
            try
            {
                return ParamReader.OptionalBool(obj, name);
            }
            catch (ToolException ex) when (prefix.Length > 0 && ex.Code == ErrorCodes.InvalidParams)
            {
                string det = ex.ExtraData.TryGetValue("detalle", out object? d) ? d?.ToString() ?? "" : "";
                throw ToolException.InvalidParams(prefix + det);
            }
        }

        private static string? OptionalStringPrefixed(JsonElement obj, string name, int minLen, int maxLen, string prefix)
        {
            try
            {
                return ParamReader.OptionalString(obj, name, minLen, maxLen);
            }
            catch (ToolException ex) when (prefix.Length > 0 && ex.Code == ErrorCodes.InvalidParams)
            {
                string det = ex.ExtraData.TryGetValue("detalle", out object? d) ? d?.ToString() ?? "" : "";
                throw ToolException.InvalidParams(prefix + det);
            }
        }

        private static int? OptionalIntPrefixed(JsonElement obj, string name, int min, int max, string prefix)
        {
            try
            {
                return ParamReader.OptionalInt(obj, name, min, max);
            }
            catch (ToolException ex) when (prefix.Length > 0 && ex.Code == ErrorCodes.InvalidParams)
            {
                string det = ex.ExtraData.TryGetValue("detalle", out object? d) ? d?.ToString() ?? "" : "";
                throw ToolException.InvalidParams(prefix + det);
            }
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = AcadUtil.InsUnits(db);
            List<string> warnings = AcadUtil.UnitWarnings(db);

            if (p.Batch != null)
            {
                return ExecuteBatch(doc, db, p.Batch, p.Confirmar, ctx.Pid, ins, warnings);
            }

            LayerItem item = p.Singular!;
            Resolved nr = Resolve(item);
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
                    IReadOnlyList<LayerProps.Change> cambios = LayerProps.Diff(
                        ReadValues(existing, tr),
                        BuildRequest(item, db, tr, p.Confirmar ? new List<string>() : warnings, strictLoaded: false, emitLinetypePreviewNotice: !p.Confirmar));
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
                    return UndoGroup.Run(doc, d => Modify(d, item, nr.Nombre, nr, pidMod, ins, warnings));
                }

                tr.Commit();
            }

            ValidateNewLayer(item, nr);

            int pid = ctx.Pid;
            return UndoGroup.Run(doc, d => Create(d, item, nr, pid, ins, warnings));
        }

        private static void ValidateNewLayer(LayerItem item, Resolved nr)
        {
            // Punto 5 (protocol §5D.1): un nombre fuera del estándar que no existe se rechaza salvo libre: true (un error
            // tipográfico no debe crear capas sueltas).
            if (!item.Libre && !nr.Estandar)
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
        }

        private static object ExecuteBatch(
            Document doc, Database db, List<LayerItem> items, bool confirmar, int pid, int ins, List<string> warnings)
        {
            List<BatchEntry> entries;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                entries = PlanBatch(db, tr, items, warnings, strictLoaded: false, emitLinetypePreviewNotice: !confirmar);
                tr.Commit();
            }

            bool anyExistingChanges = entries.Any(e => e.Exists && e.Cambios.Count > 0);
            bool anyCreate = entries.Any(e => !e.Exists);

            if (!confirmar && anyExistingChanges)
            {
                warnings.Add(
                    "Hay capas existentes con diferencias de propiedades: no se modificó el dibujo. Pase confirmar: true para aplicar todos los cambios del lote.");
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var rows = new List<Dictionary<string, object?>>(entries.Count);
                    foreach (BatchEntry e in entries)
                    {
                        if (e.Exists)
                        {
                            var existing = (LayerTableRecord)tr.GetObject(e.ExistingId, OpenMode.ForRead);
                            rows.Add(BuildBatchEntry(existing, tr, false, false, e.Cambios, e.Resolved));
                        }
                        else
                        {
                            rows.Add(BuildPlannedCreationEntry(e.Item, e.Resolved));
                        }
                    }

                    tr.Commit();
                    return BuildBatchResponse(pid, preview: true, escrito: false, rows, ins, warnings);
                }
            }

            if (!anyCreate && !anyExistingChanges)
            {
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var rows = new List<Dictionary<string, object?>>(entries.Count);
                    foreach (BatchEntry e in entries)
                    {
                        var existing = (LayerTableRecord)tr.GetObject(e.ExistingId, OpenMode.ForRead);
                        rows.Add(BuildBatchEntry(existing, tr, false, false, e.Cambios, e.Resolved));
                    }

                    tr.Commit();
                    return BuildBatchResponse(pid, preview: false, escrito: false, rows, ins, warnings);
                }
            }

            var candidateLinetypes = entries
                .Select(e => e.Exists ? e.Item.TipoLinea : (e.Item.TipoLinea ?? (e.Resolved.Spec ?? LibreDefault).TipoLinea))
                .ToList();
            return UndoGroup.Run(doc, d => WriteBatch(d, items, candidateLinetypes, pid, ins, warnings));
        }

        private static List<BatchEntry> PlanBatch(
            Database db, Transaction tr, List<LayerItem> items, List<string> warnings,
            bool strictLoaded = false, bool emitLinetypePreviewNotice = true, List<string>? linetypeWarnings = null)
        {
            List<string> ltWarnings = linetypeWarnings ?? warnings;
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            var existingNonXref = new List<(string Name, ObjectId Id)>();
            foreach (ObjectId lid in table)
            {
                if (lid.IsNull || lid.IsErased)
                {
                    continue;
                }

                var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                if (!ltr.IsDependent && ltr.Name.IndexOf('|') < 0)
                {
                    existingNonXref.Add((ltr.Name, lid));
                }
            }

            existingNonXref.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

            var entries = new List<BatchEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (LayerItem item in items)
            {
                string trimmed = item.Nombre.Trim();
                bool isWildcard = trimmed.IndexOf('*') >= 0 || trimmed.IndexOf('?') >= 0;
                if (isWildcard)
                {
                    int matched = 0;
                    foreach ((string exName, ObjectId exId) in existingNonXref)
                    {
                        if (!LayerPattern.Matches(exName, trimmed))
                        {
                            continue;
                        }

                        matched++;
                        if (!seen.Add(exName))
                        {
                            continue;
                        }

                        var existing = (LayerTableRecord)tr.GetObject(exId, OpenMode.ForRead);
                        LayerSpec? spec = LayerResolver.SpecFor(exName, AuditSupport.LayerRules);
                        var nr = new Resolved(exName, LayerPlan.OrigenNombre, spec, IsStandard(exName, spec));
                        IReadOnlyList<LayerProps.Change> cambios = LayerProps.Diff(
                            ReadValues(existing, tr),
                            BuildRequest(item, db, tr, ltWarnings, strictLoaded, emitLinetypePreviewNotice));
                        entries.Add(new BatchEntry(item, nr, exId, cambios));
                    }

                    if (matched == 0)
                    {
                        warnings.Add("crear_capa: el patrón '" + trimmed + "' no coincide con ninguna capa existente del dibujo.");
                    }

                    continue;
                }

                Resolved resolved = Resolve(item);
                if (!seen.Add(resolved.Nombre))
                {
                    throw ToolException.InvalidParams("capas: la capa '" + resolved.Nombre + "' aparece repetida en el lote");
                }

                ObjectId id = McpLayerFactory.FindLayer(table, resolved.Nombre);
                if (!id.IsNull)
                {
                    var existing = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    RejectXref(existing);
                    IReadOnlyList<LayerProps.Change> cambios = LayerProps.Diff(
                        ReadValues(existing, tr),
                        BuildRequest(item, db, tr, ltWarnings, strictLoaded, emitLinetypePreviewNotice));
                    entries.Add(new BatchEntry(item, resolved, id, cambios));
                }
                else
                {
                    ValidateNewLayer(item, resolved);
                    if (!resolved.Estandar)
                    {
                        warnings.Add(
                            "capa '" + resolved.Nombre + "' no sigue el estándar LPS (lps.json): no es un concepto, ni capas_permitidas/capas_mcp, ni MCP-*.");
                    }

                    entries.Add(new BatchEntry(item, resolved, ObjectId.Null, Array.Empty<LayerProps.Change>()));
                }
            }

            if (entries.Count == 0)
            {
                throw ToolException.InvalidParams("capas: ningún patrón coincidió con capas existentes del dibujo");
            }

            return entries;
        }

        private static object WriteBatch(
            Document doc, List<LayerItem> items, List<string?> candidateLinetypes, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            PreloadLinetypes(db, candidateLinetypes, warnings);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<BatchEntry> entries = PlanBatch(
                    db, tr, items, new List<string>(), strictLoaded: true, emitLinetypePreviewNotice: false, linetypeWarnings: warnings);
                var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var rows = new List<Dictionary<string, object?>>(entries.Count);

                foreach (BatchEntry entry in entries)
                {
                    if (!entry.Exists)
                    {
                        LayerTableRecord creada = CreateRecord(db, tr, table, entry.Item, entry.Resolved, warnings);
                        rows.Add(BuildBatchEntry(creada, tr, creada: true, modificada: false, Array.Empty<LayerProps.Change>(), entry.Resolved));
                    }
                    else if (entry.Cambios.Count > 0)
                    {
                        var ltr = (LayerTableRecord)tr.GetObject(entry.ExistingId, OpenMode.ForRead);
                        RejectXref(ltr);
                        ApplyModifications(db, tr, ltr, entry.Item, entry.Resolved.Nombre, warnings);
                        rows.Add(BuildBatchEntry(ltr, tr, creada: false, modificada: true, entry.Cambios, entry.Resolved));
                    }
                    else
                    {
                        var ltr = (LayerTableRecord)tr.GetObject(entry.ExistingId, OpenMode.ForRead);
                        rows.Add(BuildBatchEntry(ltr, tr, creada: false, modificada: false, Array.Empty<LayerProps.Change>(), entry.Resolved));
                    }
                }

                tr.Commit();
                return BuildBatchResponse(pid, preview: false, escrito: true, rows, ins, warnings);
            }
        }

        /// <summary>Resuelve el nombre: libre → literal (propiedades del estándar si el nombre está en él); si no, <see cref="DrawLayerFactory.Plan"/>.</summary>
        private static Resolved Resolve(LayerItem item)
        {
            if (item.Libre)
            {
                string literal = item.Nombre.Trim();
                if (literal.Length == 0)
                {
                    throw ToolException.InvalidParams(LayerResolver.InvalidName);
                }

                LayerSpec? spec = LayerResolver.SpecFor(literal, AuditSupport.LayerRules);
                return new Resolved(literal, LayerPlan.OrigenNombre, spec, IsStandard(literal, spec));
            }

            LayerPlan plan = DrawLayerFactory.Plan(item.Nombre, null);
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
        /// Lo pedido en forma comparable con <see cref="ReadValues"/>. En lectura previa conserva tipos válidos no cargados aún
        /// para reflejar el cambio previsto y los carga antes de escribir; en escritura (<paramref name="strictLoaded"/>) descarta
        /// tipos que tampoco existan en acadiso.lin/acad.lin.
        /// </summary>
        private static LayerProps.Request BuildRequest(
            LayerItem item, Database db, Transaction tr, List<string> warnings,
            bool strictLoaded = false, bool emitLinetypePreviewNotice = true)
        {
            string? color = item.Aci is int aci ? LayerProps.FormatAci(aci)
                : item.Rgb is { } rgb ? LayerProps.FormatRgb(rgb.R, rgb.G, rgb.B)
                : null;
            string? tipo = item.TipoLinea;
            if (tipo != null && !LinetypeLoaded(db, tr, tipo))
            {
                if (strictLoaded)
                {
                    string msg = "tipo_linea: '" + tipo + "' no está cargado en el dibujo ni en acadiso.lin/acad.lin; no se aplicó.";
                    if (!warnings.Contains(msg))
                    {
                        warnings.Add(msg);
                    }

                    tipo = null;
                }
                else if (EntityPropertyRules.LinetypeNameError(tipo) != null)
                {
                    warnings.Add("tipo_linea: '" + tipo + "' no es un nombre de tipo de línea válido; no se aplica ni se compara.");
                    tipo = null;
                }
                else if (emitLinetypePreviewNotice)
                {
                    warnings.Add("tipo_linea: '" + tipo + "' no está cargado aún en el dibujo; al escribir se intentará cargar de acadiso.lin/acad.lin.");
                }
            }

            return new LayerProps.Request(color, tipo, item.Grosor, item.Transparencia);
        }

        private static void PreloadLinetypes(Database db, IEnumerable<string?> names, List<string> warnings)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? raw in names)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                string name = raw.Trim();
                if (!seen.Add(name) || string.Equals(name, "Continuous", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (CambiarPropiedadesHandler.TryEnsureLinetypeLoaded(db, name, null, out string? loadedFrom) && loadedFrom != null)
                {
                    warnings.Add("tipo de línea '" + name + "' cargado de " + loadedFrom + ".");
                }
            }
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

            warnings.Add("La capa " + layer + " se creó con tipo de línea Continuous: '" + name + "' no está cargado en el dibujo ni en acadiso.lin/acad.lin.");
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
        private static object Create(Document doc, LayerItem item, Resolved nr, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            LayerSpec basis = nr.Spec ?? LibreDefault;
            PreloadLinetypes(db, new[] { item.TipoLinea ?? basis.TipoLinea }, warnings);
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

                LayerTableRecord creada = CreateRecord(db, tr, table, item, nr, warnings);
                Dictionary<string, object?> r = Build(pid, creada, tr, true, false, true, Array.Empty<LayerProps.Change>(), nr, ins, warnings);
                tr.Commit();
                return r;
            }
        }

        private static LayerTableRecord CreateRecord(
            Database db, Transaction tr, LayerTable table, LayerItem item, Resolved nr, List<string> warnings)
        {
            LayerSpec basis = nr.Spec ?? LibreDefault;
            if (item.Rgb == null && item.Aci == null && (basis.Color < 1 || basis.Color > 255))
            {
                warnings.Add("El color de lps.json de la capa " + nr.Nombre + " no es un ACI válido: se usa el ACI " + Math.Clamp(basis.Color, 1, 255).ToString(CultureInfo.InvariantCulture) + ".");
            }

            if (!LayerProps.IsValidLineWeight(item.Grosor ?? basis.Grosor))
            {
                warnings.Add("El grosor de lps.json de la capa " + nr.Nombre + " no es válido: se usa Default (-3).");
            }

            int baseAci = Math.Clamp(basis.Color, 1, 255);
            int? aciCreate = item.Rgb == null ? (int?)(item.Aci ?? baseAci) : null;
            int grosor = item.Grosor ?? basis.Grosor;
            var ltr = new LayerTableRecord
            {
                Name = nr.Nombre,
                Color = ColorOf(aciCreate, item.Rgb, baseAci),
                LinetypeObjectId = ResolveLinetype(db, tr, item.TipoLinea ?? basis.TipoLinea, nr.Nombre, warnings),
                LineWeight = (LineWeight)(LayerProps.IsValidLineWeight(grosor) ? grosor : -3),
                IsPlottable = basis.Plot,
            };

            table.UpgradeOpen();
            ObjectId id = table.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);

            // Fallo 52: en AutoCAD, asignar Transparency a un LayerTableRecord antes de añadirlo a la base lanza eNoDatabase.
            if (item.Transparencia.HasValue)
            {
                ltr.Transparency = new Transparency(LayerProps.AlphaFromPercent(item.Transparencia.Value));
            }

            return (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
        }

        /// <summary>Aplica a una capa existente las propiedades pedidas que difieren. Recalcula las diferencias dentro del paso de deshacer.</summary>
        private static object Modify(Document doc, LayerItem item, string nombre, Resolved nr, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            PreloadLinetypes(db, new[] { item.TipoLinea }, warnings);
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
                IReadOnlyList<LayerProps.Change> cambios = LayerProps.Diff(
                    ReadValues(ltr, tr),
                    BuildRequest(item, db, tr, warnings, strictLoaded: true, emitLinetypePreviewNotice: false));
                // Punto 12: si entre la lectura previa y este paso otro proceso ya aplicó lo pedido, cambios.Count es 0 y el
                // grupo de deshacer queda vacío. Es solo una carrera; no se evita aquí (UndoGroup abre el grupo antes).
                if (cambios.Count > 0)
                {
                    ApplyModifications(db, tr, ltr, item, nombre, warnings);
                }

                PluginLog.Info("crear_capa: '" + nombre + "' con " + cambios.Count.ToString(CultureInfo.InvariantCulture) + " cambio(s) de propiedades.");
                Dictionary<string, object?> r = Build(pid, ltr, tr, false, cambios.Count > 0, cambios.Count > 0, cambios, nr, ins, warnings);
                tr.Commit();
                return r;
            }
        }

        private static void ApplyModifications(
            Database db, Transaction tr, LayerTableRecord ltr, LayerItem item, string nombre, List<string> warnings)
        {
            ltr.UpgradeOpen();
            if (item.Aci != null || item.Rgb != null)
            {
                ltr.Color = ColorOf(item.Aci, item.Rgb, 7);
            }

            if (item.TipoLinea != null && LinetypeLoaded(db, tr, item.TipoLinea))
            {
                ltr.LinetypeObjectId = ResolveLinetype(db, tr, item.TipoLinea, nombre, warnings);
            }

            if (item.Grosor.HasValue)
            {
                ltr.LineWeight = (LineWeight)item.Grosor.Value;
            }

            if (item.Transparencia.HasValue)
            {
                ltr.Transparency = new Transparency(LayerProps.AlphaFromPercent(item.Transparencia.Value));
            }
        }

        private static Dictionary<string, object?> Build(
            int pid, LayerTableRecord ltr, Transaction tr, bool creada, bool modificada, bool escrito,
            IReadOnlyList<LayerProps.Change> cambios, Resolved nr, int ins, List<string> warnings)
        {
            Dictionary<string, object?> entry = BuildBatchEntry(ltr, tr, creada, modificada, cambios, nr);
            entry["pid"] = pid;
            entry["escrito"] = escrito;
            entry["insunits"] = ins;
            entry["unidades"] = UnitsTable.Name(ins);
            entry["advertencias"] = warnings;
            return entry;
        }

        private static Dictionary<string, object?> BuildBatchEntry(
            LayerTableRecord ltr, Transaction tr, bool creada, bool modificada,
            IReadOnlyList<LayerProps.Change> cambios, Resolved nr)
        {
            Color c = ltr.Color;
            Transparency t = ltr.Transparency;
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["capa"] = ltr.Name,
                ["creada"] = creada,
                ["modificada"] = modificada,
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
            };
        }

        private static Dictionary<string, object?> BuildPlannedCreationEntry(LayerItem item, Resolved nr)
        {
            LayerSpec basis = nr.Spec ?? LibreDefault;
            int baseAci = Math.Clamp(basis.Color, 1, 255);
            int? aci = item.Rgb == null ? (int?)(item.Aci ?? baseAci) : null;
            int grosor = item.Grosor ?? basis.Grosor;
            if (!LayerProps.IsValidLineWeight(grosor))
            {
                grosor = -3;
            }

            string colorText = aci is int a
                ? LayerProps.FormatAci(a)
                : LayerProps.FormatRgb(item.Rgb!.Value.R, item.Rgb!.Value.G, item.Rgb!.Value.B);

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["capa"] = nr.Nombre,
                ["creada"] = true,
                ["modificada"] = false,
                ["cambios"] = new List<object>(),
                ["color"] = aci ?? -1,
                ["color_texto"] = colorText,
                ["tipo_linea"] = item.TipoLinea ?? basis.TipoLinea,
                ["grosor"] = grosor,
                ["transparencia"] = item.Transparencia ?? 0,
                ["plot"] = basis.Plot,
                ["bloqueada"] = false,
                ["congelada"] = false,
                ["apagada"] = false,
                ["origen"] = nr.Origen == LayerPlan.OrigenConcepto ? LayerPlan.OrigenConcepto : LayerPlan.OrigenNombre,
                ["estandar"] = nr.Estandar,
            };
        }

        private static Dictionary<string, object?> BuildBatchResponse(
            int pid, bool preview, bool escrito, List<Dictionary<string, object?>> rows, int ins, List<string> warnings)
        {
            int creadas = rows.Count(r => r["creada"] is true);
            int modificadas = rows.Count(r => r["modificada"] is true);
            int sinCambio = rows.Count - creadas - modificadas;
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = pid,
                ["preview"] = preview,
                ["escrito"] = escrito,
                ["capas"] = rows,
                ["n_creadas"] = creadas,
                ["n_modificadas"] = modificadas,
                ["n_sin_cambio"] = sinCambio,
                ["insunits"] = ins,
                ["unidades"] = UnitsTable.Name(ins),
                ["advertencias"] = warnings,
            };
        }
    }
}
