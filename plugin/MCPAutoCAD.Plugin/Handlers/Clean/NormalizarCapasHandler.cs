using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Clean;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Audit;
using MCPAutoCAD.Plugin.Handlers.Draw;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Clean
{
    /// <summary>
    /// normalizar_capas (protocol.md §5F.3). Hilo principal; requiere documento; ESCRITURA con previsualización sin estado
    /// (§5F.0 L1: sin confirmar: true no se escribe nada). Reasigna a otra capa todas las entidades de cada capa origen de
    /// un mapa {origen: destino} en modelo y layouts (y, con incluir_bloques, dentro de las definiciones con nombre), en
    /// un paso de deshacer. Desde 0.6.1 (gate en vivo): el ámbito se limita con <c>espacios</c> ("modelo" y/o layouts;
    /// por defecto todos) y los VIEWPORT de los layouts no se reasignan (el n.º 1 nunca; los demás solo con
    /// <c>incluir_viewports: true</c>); criterio puro en <see cref="LayerMapScope"/>. La validación del mapa es SOLO la de <see cref="LayerMapValidator.Validate"/> (GeometryCore);
    /// los destinos se resuelven/crean con R3 (<see cref="DrawLayerFactory"/>). Las entidades conservan color, tipo de
    /// línea y grosor propios. Flujo §5F.0: Parse → RequireDocument → CleanWrite.ReadUnits (L3) → lectura
    /// (StartOpenCloseTransaction) con el inventario completo y todos los rechazos → previsualización
    /// (CleanWrite.Response) o UndoGroup.Run con UNA Transaction que revalida cada entidad antes de tocarla.
    /// Desde 0.7 (§5G.5): <c>conservar_aspecto</c> fija en cada entidad, antes de moverla, lo que heredaba de su capa origen
    /// y cambiaría en la destino (decisión pura en <see cref="LayerAppearance"/>); sin él solo se cuenta y se avisa.
    /// </summary>
    internal sealed class NormalizarCapasHandler : IToolHandler
    {
        private const int MaxPairs = 200;
        private const int MaxNameLength = 255;
        private const string TipoAtributo = "ATTRIB";
        private const string PrefijoBloque = "bloque:";

        private static readonly string[] Allowed =
        {
            "mapa", "espacios", "incluir_viewports", "incluir_bloques", "borrar_capas_origen_vacias", "conservar_aspecto", "confirmar", "preview",
        };

        public string Name => "normalizar_capas";

        public string Description =>
            "REASIGNA a otra capa todas las entidades de cada capa origen de un mapa {origen: destino} (destino = concepto de " +
            "lps.json, capa existente o capa del estándar que se crea), en modelo y layouts (espacios limita el ámbito, p. ej. solo " +
            "al modelo) y, con incluir_bloques, dentro de las definiciones de bloque, en un solo paso de deshacer; conserva " +
            "colores y tipos de línea propios. Nunca reasigna el viewport n.º 1 de un layout y los demás VIEWPORT solo con " +
            "incluir_viewports: true. Rechaza orígenes inexistentes, capas bloqueadas/congeladas/xref y mapas con cadenas o " +
            "ciclos. Sin confirmar: true solo PREVISUALIZA (conteo por capa origen, tipo y espacio, y lo excluido) y no " +
            "modifica nada. Con conservar_aspecto: true fija en cada entidad las propiedades que heredaba de su capa origen " +
            "para que no cambie de aspecto.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""mapa"": {
      ""type"": ""object"", ""minProperties"": 1, ""maxProperties"": 200,
      ""propertyNames"": { ""minLength"": 1, ""maxLength"": 255 },
      ""additionalProperties"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 },
      ""description"": ""Pares capa_origen → destino (sin distinguir mayúsculas). destino = concepto de lps.json.capas_preferidas (muros → MUROS), nombre de una capa existente, o capa de lps.json.capas_permitidas que se crea con sus propiedades. Ni el origen ni el destino pueden ser capas del MCP (MCP-*): las entidades del usuario no pasan a capas del MCP ni al revés. Varias capas origen pueden ir al mismo destino; un destino no puede ser también origen (aplane cadenas y ciclos).""
    },
    ""espacios"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 },
      ""description"": ""Limita el mapa a estos espacios: modelo y/o nombres de layout (sin distinguir mayúsculas). Por defecto, el modelo y todos los layouts. Un layout inexistente es NOT_FOUND. Las definiciones de bloque dependen solo de incluir_bloques.""
    },
    ""incluir_viewports"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = también reasigna los VIEWPORT de los layouts del ámbito, salvo el n.º 1 de cada layout (el propio espacio papel), que nunca se reasigna. Por defecto se excluyen, se cuentan y se avisa."" },
    ""incluir_bloques"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = también las entidades del interior de las definiciones de bloque con nombre (no anónimas, no layouts, no xrefs), con independencia de espacios."" },
    ""borrar_capas_origen_vacias"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = tras reasignar, borra las capas origen que queden sin ninguna entidad (modelo, layouts, también lo excluido por espacios o viewports, ni bloques, anónimos incluidos; lo decide Database.Purge). Nunca 0, DEFPOINTS ni la capa actual."" },
    ""conservar_aspecto"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = antes de mover cada entidad fija en ella lo que heredaba ByLayer de su capa origen (color, tipo de línea, grosor, transparencia) cuando la capa destino tiene otro valor, para que no cambie de aspecto (como la cadena del rótulo v2). Por defecto no se toca y la previsualización avisa cuántas cambiarían de aspecto."" },
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""mapa""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""mapa"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""origen"": { ""type"": ""string"" }, ""destino"": { ""type"": ""string"" },
      ""destino_concepto"": { ""type"": [""string"", ""null""] }, ""destino_existe"": { ""type"": ""boolean"" },
      ""destino_creada"": { ""type"": ""boolean"" }, ""n"": { ""type"": ""integer"" } } } },
    ""n_reasignar"": { ""type"": ""integer"" },
    ""n_reasignadas"": { ""type"": [""integer"", ""null""] },
    ""por_origen"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""object"", ""properties"": {
      ""por_tipo"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
      ""por_espacio"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
      ""por_espacio_bloques"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
      ""excluidas"": { ""type"": ""object"", ""properties"": {
        ""n"": { ""type"": ""integer"" },
        ""por_espacio"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
        ""viewports"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
        ""viewports_papel"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } } } } } } },
    ""n_excluidas"": { ""type"": ""integer"" },
    ""espacios"": { ""type"": [""array"", ""null""], ""items"": { ""type"": ""string"" } },
    ""incluir_viewports"": { ""type"": ""boolean"" },
    ""n_propiedades_forzadas"": { ""type"": ""integer"" },
    ""n_cambian_aspecto"": { ""type"": ""integer"" },
    ""n_aspecto_fijado"": { ""type"": [""integer"", ""null""] },
    ""incluir_bloques"": { ""type"": ""boolean"" },
    ""borrar_capas_origen_vacias"": { ""type"": ""boolean"" },
    ""capas_borrar"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""capas_borradas"": { ""type"": [""array"", ""null""], ""items"": { ""type"": ""string"" } },
    ""capas_creadas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""duracion_ms"": { ""type"": ""object"", ""properties"": {
      ""lectura"": { ""type"": ""integer"" }, ""escritura"": { ""type"": [""integer"", ""null""] } } },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""mapa"", ""n_reasignar"", ""n_reasignadas"", ""por_origen"", ""n_excluidas"", ""espacios"", ""incluir_viewports"", ""n_propiedades_forzadas"", ""n_cambian_aspecto"", ""n_aspecto_fijado"", ""incluir_bloques"", ""borrar_capas_origen_vacias"", ""capas_borrar"", ""capas_borradas"", ""capas_creadas"", ""duracion_ms"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        // ------------------------------------------------------------------ tipos internos

        /// <summary>Parámetros ya validados (sin AutoCAD).</summary>
        private sealed class Params
        {
            public Params(
                List<KeyValuePair<string, string>> mapa, List<string>? espacios, bool incluirViewports, bool incluirBloques,
                bool borrarVacias, bool conservarAspecto, CleanWrite.Common common)
            {
                Mapa = mapa;
                Espacios = espacios;
                IncluirViewports = incluirViewports;
                IncluirBloques = incluirBloques;
                BorrarVacias = borrarVacias;
                ConservarAspecto = conservarAspecto;
                Common = common;
            }

            /// <summary>Pares en el orden del cliente.</summary>
            public List<KeyValuePair<string, string>> Mapa { get; }

            /// <summary>espacios tal como los escribió el cliente; null = todos (modelo y layouts).</summary>
            public List<string>? Espacios { get; }

            public bool IncluirViewports { get; }

            public bool IncluirBloques { get; }

            public bool BorrarVacias { get; }

            public bool ConservarAspecto { get; }

            public CleanWrite.Common Common { get; }
        }

        /// <summary>Entrada del mapa con su inventario (una por par del cliente, mismo orden).</summary>
        private sealed class EntryPlan
        {
            public EntryPlan(LayerMapEntry entry, string destinoParam, ObjectId originId)
            {
                Entry = entry;
                DestinoParam = destinoParam;
                OriginId = originId;
            }

            public LayerMapEntry Entry { get; }

            /// <summary>Destino tal como lo escribió el cliente (se vuelve a resolver igual con DrawLayerFactory).</summary>
            public string DestinoParam { get; }

            public ObjectId OriginId { get; }

            /// <summary>Nombre real del destino (el del dibujo si existe; si no, el que se creará).</summary>
            public string DestinoNombre { get; set; } = "";

            public int N { get; set; }

            public Dictionary<string, int> PorTipo { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

            public Dictionary<string, int> PorEspacio { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

            public Dictionary<string, int> PorEspacioBloques { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>
            /// Entidades (y atributos) de la capa origen que NO se reasignan (anónimos, bloques sin incluir_bloques, espacios
            /// fuera de <c>espacios</c>, viewports excluidos y el viewport n.º 1…). Solo estima capas_borrar: en la escritura
            /// decide Database.Purge.
            /// </summary>
            public int Restantes { get; set; }

            /// <summary>Excluidas por espacio fuera del ámbito (por espacio, entidades + ATTRIB).</summary>
            public Dictionary<string, int> ExcluidasEspacio { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>VIEWPORT de layout excluidos sin incluir_viewports (por layout).</summary>
            public Dictionary<string, int> ExcluidosViewport { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>Viewports n.º 1 (espacio papel) en la capa origen (por layout): nunca se reasignan.</summary>
            public Dictionary<string, int> ViewportsPapel { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>Total excluido de los espacios (por espacio + viewports + viewports n.º 1).</summary>
            public int Excluidas { get; set; }

            /// <summary>Aspecto de la capa origen (§5G.5).</summary>
            public LayerLook? OrigenLook { get; set; }

            /// <summary>Aspecto de la capa destino: la existente o la que se va a crear con las propiedades de lps.json.</summary>
            public LayerLook? DestinoLook { get; set; }

            /// <summary>Color de la capa origen: un color verdadero no cabe en LayerLook.Color (solo ACI) y se fija tal cual.</summary>
            public Color? OrigenColor { get; set; }
        }

        /// <summary>Entidad a reasignar: id, capa origen esperada, entrada del mapa y qué fijar para conservar su aspecto.</summary>
        private readonly struct Target
        {
            public Target(ObjectId id, ObjectId originLayer, int entry, AppearanceFix? fix)
            {
                Id = id;
                OriginLayer = originLayer;
                Entry = entry;
                Fix = fix;
            }

            public ObjectId Id { get; }
            public ObjectId OriginLayer { get; }
            public int Entry { get; }

            /// <summary>null si las propiedades de la entidad no se pudieron leer (no se fija nada).</summary>
            public AppearanceFix? Fix { get; }
        }

        /// <summary>Resultado completo de la lectura previa (el inventario es el mismo en ambos modos, L5).</summary>
        private sealed class Plan
        {
            public List<EntryPlan> Entries { get; } = new List<EntryPlan>();

            public List<Target> Targets { get; } = new List<Target>();

            /// <summary>Destinos distintos (por nombre, sin mayúsculas) en orden de aparición: parámetro original → existe.</summary>
            public List<(string param, string nombre, bool existe)> Destinos { get; } = new List<(string, string, bool)>();

            public int Forzadas { get; set; }

            /// <summary>Entidades a reasignar con al menos una propiedad heredada que cambiaría en la capa destino.</summary>
            public int CambianAspecto { get; set; }

            public int AnonDefsConOrigen { get; set; }

            /// <summary>Espacios resueltos (nombres reales, orden del cliente); null = todos.</summary>
            public IReadOnlyList<string>? Espacios { get; set; }

            /// <summary>VIEWPORT excluidos sin incluir_viewports, por layout en orden de pestañas (para la advertencia).</summary>
            public List<KeyValuePair<string, int>> ViewportsExcluidos { get; } = new List<KeyValuePair<string, int>>();

            /// <summary>Viewports de papel (n.º 1) de todos los layouts: la escritura nunca los toca (defensa explícita, O1).</summary>
            public HashSet<ObjectId> ViewportsPapel { get; } = new HashSet<ObjectId>();

            public List<string> CapasBorrar { get; } = new List<string>();

            /// <summary>Ids de las capas origen candidatas a borrar (no 0, DEFPOINTS ni la actual).</summary>
            public List<ObjectId> CapasCandidatas { get; } = new List<ObjectId>();
        }

        // ------------------------------------------------------------------ parámetros

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            List<KeyValuePair<string, string>> mapa = ReadMapa(p);
            List<string>? espacios = ReadEspacios(p);
            bool incluirViewports = ParamReader.OptionalBool(p, "incluir_viewports") ?? false;
            bool incluirBloques = ParamReader.OptionalBool(p, "incluir_bloques") ?? false;
            bool borrar = ParamReader.OptionalBool(p, "borrar_capas_origen_vacias") ?? false;
            bool conservarAspecto = ParamReader.OptionalBool(p, "conservar_aspecto") ?? false;
            CleanWrite.Common common = CleanWrite.ReadCommon(p);
            return new Params(mapa, espacios, incluirViewports, incluirBloques, borrar, conservarAspecto, common);
        }

        /// <summary>mapa: objeto con 1–200 pares cadena (1–255) → cadena (1–255), en el orden del cliente.</summary>
        private static List<KeyValuePair<string, string>> ReadMapa(JsonElement p)
        {
            string shape = "mapa: debe ser un objeto con 1 a " + I(MaxPairs) + " pares capa_origen → destino";
            if (!p.TryGetProperty("mapa", out JsonElement el))
            {
                throw ToolException.InvalidParams("mapa: es obligatorio");
            }

            if (el.ValueKind != JsonValueKind.Object)
            {
                throw ToolException.InvalidParams(shape);
            }

            var list = new List<KeyValuePair<string, string>>();
            foreach (JsonProperty prop in el.EnumerateObject())
            {
                string origen = prop.Name;
                if (origen.Length < 1 || origen.Length > MaxNameLength)
                {
                    throw ToolException.InvalidParams("mapa: cada capa origen debe tener entre 1 y " + I(MaxNameLength) + " caracteres");
                }

                string path = "mapa['" + origen + "']";
                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    throw ToolException.InvalidParams(path + ": debe ser una cadena");
                }

                string destino = prop.Value.GetString() ?? "";
                if (destino.Length < 1 || destino.Length > MaxNameLength)
                {
                    throw ToolException.InvalidParams(path + ": debe tener entre 1 y " + I(MaxNameLength) + " caracteres");
                }

                list.Add(new KeyValuePair<string, string>(origen, destino));
            }

            if (list.Count < 1 || list.Count > MaxPairs)
            {
                throw ToolException.InvalidParams(shape);
            }

            return list;
        }

        /// <summary>
        /// espacios: array opcional de 1–100 cadenas de 1–255 caracteres sin repetidos (sin distinguir mayúsculas). La
        /// existencia de los layouts se comprueba en la lectura (NOT_FOUND), después de validar el mapa.
        /// </summary>
        private static List<string>? ReadEspacios(JsonElement p)
        {
            if (!p.TryGetProperty("espacios", out JsonElement el))
            {
                return null;
            }

            if (el.ValueKind != JsonValueKind.Array)
            {
                throw ToolException.InvalidParams(LayerMapScope.ShapeError);
            }

            int n = el.GetArrayLength();
            if (n < 1 || n > LayerMapScope.MaxEspacios)
            {
                throw ToolException.InvalidParams(LayerMapScope.ShapeError);
            }

            var list = new List<string>(n);
            int i = 0;
            foreach (JsonElement item in el.EnumerateArray())
            {
                string? value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (value == null || value.Length < 1 || value.Length > LayerMapScope.MaxNombre)
                {
                    throw ToolException.InvalidParams(LayerMapScope.ItemError(i));
                }

                list.Add(value);
                i++;
            }

            string? repetido = LayerMapScope.FindDuplicate(list);
            if (repetido != null)
            {
                throw ToolException.InvalidParams(repetido);
            }

            return list;
        }

        // ------------------------------------------------------------------ ejecución

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(p.Common.Confirmar, p.Common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = CleanWrite.ReadUnits(db); // L3 desde 0.8.3: no interpreta unidades, INSUNITS = 0 solo advierte

            var swRead = Stopwatch.StartNew();
            Plan plan = Read(db, p, warnings);
            long lecturaMs = swRead.ElapsedMilliseconds;

            if (mode == CleanWrite.Mode.Preview)
            {
                var wouldCreate = new List<string>();
                foreach ((string _, string nombre, bool existe) in plan.Destinos)
                {
                    if (!existe)
                    {
                        wouldCreate.Add(nombre);
                    }
                }

                return BuildResponse(ctx.Pid, mode, ins, warnings, p, plan, null, wouldCreate, null, lecturaMs, null, null);
            }

            return UndoGroup.Run(doc, d => Write(d, ctx.Pid, ins, warnings, p, plan, lecturaMs));
        }

        /// <summary>
        /// Lectura previa (StartOpenCloseTransaction, sin efectos): validación del mapa (GeometryCore), comprobación R3 de
        /// los destinos e inventario completo de lo que se reasignaría.
        /// </summary>
        private static Plan Read(Database db, Params p, List<string> warnings)
        {
            var plan = new Plan();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                // 1. Estado de todas las capas (sin distinguir mayúsculas).
                var states = new Dictionary<string, LayerState>(StringComparer.OrdinalIgnoreCase);
                var ids = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                ObjectId clayer = db.Clayer;
                var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    if (id.IsNull || id.IsErased || !(tr.GetObject(id, OpenMode.ForRead, false) is LayerTableRecord ltr))
                    {
                        continue;
                    }

                    string name = ltr.Name ?? "";
                    bool xref = ltr.IsDependent || name.IndexOf('|') >= 0;
                    states[name] = new LayerState(name, ltr.IsLocked, ltr.IsFrozen, xref, id == clayer);
                    ids[name] = id;
                }

                // 2. Validación del mapa (orden exacto de §5F.3/§5F.8; la primera que falla responde).
                LayerMapError? error = LayerMapValidator.Validate(p.Mapa, states, AuditSupport.LayerRules, p.IncluirBloques, out IReadOnlyList<LayerMapEntry> entries);
                if (error != null)
                {
                    throw ToToolException(error);
                }

                // 2b. espacios (después del mapa): layouts inexistentes → NOT_FOUND con todos los que faltan y la lista
                //     de espacios del dibujo.
                List<SpaceResolver.ResolvedSpace> spaces = AuditSupport.AllSpaces(db, tr);
                var layoutNames = new List<string>();
                foreach (SpaceResolver.ResolvedSpace s in spaces)
                {
                    if (!IsModelSpace(s))
                    {
                        layoutNames.Add(s.Name);
                    }
                }

                if (!LayerMapScope.TryResolve(p.Espacios, layoutNames, out IReadOnlyList<string>? resueltos, out IReadOnlyList<string> faltan))
                {
                    string detalle = LayerMapScope.MissingDetail(faltan, layoutNames);
                    throw new ToolException(
                        ErrorCodes.NotFound,
                        detalle,
                        new Dictionary<string, object?>
                        {
                            ["espacios"] = new List<string>(faltan),
                            ["layouts"] = layoutNames,
                            ["detalle"] = detalle,
                        });
                }

                plan.Espacios = resueltos;

                var byOrigin = new Dictionary<ObjectId, int>();
                for (int i = 0; i < entries.Count; i++)
                {
                    LayerMapEntry entry = entries[i];
                    string destinoParam = i < p.Mapa.Count ? p.Mapa[i].Value : entry.Destino.Nombre;
                    ObjectId originId = ids.TryGetValue(entry.Origen, out ObjectId oid) ? oid : ObjectId.Null;
                    if (originId.IsNull)
                    {
                        // El validador ya comprobó la existencia: solo por defensa.
                        throw ToolException.LayerNotFound(entry.Origen);
                    }

                    plan.Entries.Add(new EntryPlan(entry, destinoParam, originId));
                    byOrigin[originId] = i;
                }

                // 3. Destinos (R3): existentes escribibles (apagada → advertencia R3.5) o creables con nombre válido.
                var seenDest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (EntryPlan e in plan.Entries)
                {
                    string key = e.Entry.Destino.Nombre;
                    if (seenDest.TryGetValue(key, out string? realName))
                    {
                        e.DestinoNombre = realName;
                        continue;
                    }

                    DrawLayerFactory.Check(db, tr, e.DestinoParam, null, out bool existe);
                    string nombre = key;
                    if (existe)
                    {
                        ObjectId did = McpLayerFactory.FindLayer(table, key);
                        if (!did.IsNull && tr.GetObject(did, OpenMode.ForRead, false) is LayerTableRecord dl)
                        {
                            nombre = dl.Name;
                            DrawLayerFactory.RequireWritable(dl, warnings);
                        }
                    }

                    seenDest[key] = nombre;
                    e.DestinoNombre = nombre;
                    plan.Destinos.Add((e.DestinoParam, nombre, existe));
                }

                // 3b. Aspecto de las capas origen y destino (§5G.5), antes de clasificar las entidades.
                foreach (EntryPlan e in plan.Entries)
                {
                    var origen = (LayerTableRecord)tr.GetObject(e.OriginId, OpenMode.ForRead, false);
                    e.OrigenColor = origen.Color;
                    e.OrigenLook = ReadLook(origen, tr);
                    e.DestinoLook = DestinationLook(db, tr, table, e);
                }

                // 4. Inventario: espacios (siempre), definiciones con nombre (incluir_bloques) y, para estimar las capas
                //    que quedarían vacías, todo lo que no se reasigna.
                var spaceIds = new HashSet<ObjectId>();
                foreach (SpaceResolver.ResolvedSpace space in spaces)
                {
                    spaceIds.Add(space.Btr.ObjectId);
                    bool isLayout = !IsModelSpace(space);
                    var scope = new SpaceScope(space.Name, isLayout, LayerMapScope.Includes(resueltos, space.Name, !isLayout), p.IncluirViewports);
                    ScanSpace(tr, space.Btr, scope, byOrigin, plan);
                    if (scope.ViewportsExcluidos > 0)
                    {
                        plan.ViewportsExcluidos.Add(new KeyValuePair<string, int>(space.Name, scope.ViewportsExcluidos));
                    }
                }

                bool needRemaining = p.BorrarVacias;
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    if (btrId.IsNull || btrId.IsErased || spaceIds.Contains(btrId))
                    {
                        continue;
                    }

                    if (!(tr.GetObject(btrId, OpenMode.ForRead, false) is BlockTableRecord btr))
                    {
                        continue;
                    }

                    string name = btr.Name ?? "";
                    if (btr.IsFromExternalReference || btr.IsDependent || name.IndexOf('|') >= 0)
                    {
                        // Contenido de xref: no vive en esta base de datos (o es dependiente): no se toca ni se cuenta.
                        continue;
                    }

                    bool anonymous = btr.IsAnonymous || name.StartsWith("*", StringComparison.Ordinal);
                    if (!btr.IsLayout && !anonymous && p.IncluirBloques)
                    {
                        ScanBlock(tr, btr, PrefijoBloque + name, byOrigin, plan);
                        continue;
                    }

                    bool checkDynamic = p.IncluirBloques && !btr.IsLayout && name.StartsWith("*U", StringComparison.OrdinalIgnoreCase);
                    if (needRemaining || checkDynamic)
                    {
                        if (ScanRemaining(tr, btr, byOrigin, plan, needRemaining) && checkDynamic)
                        {
                            plan.AnonDefsConOrigen++;
                        }
                    }
                }

                // 5. Capas origen que quedarían vacías (estimación; en la escritura decide Database.Purge).
                if (p.BorrarVacias)
                {
                    foreach (EntryPlan e in plan.Entries)
                    {
                        string origen = e.Entry.Origen;
                        bool protegida = string.Equals(origen, "0", StringComparison.Ordinal)
                            || string.Equals(origen, "DEFPOINTS", StringComparison.OrdinalIgnoreCase)
                            || e.OriginId == clayer;
                        if (protegida)
                        {
                            if (e.Restantes == 0)
                            {
                                warnings.Add("borrar_capas_origen_vacias: la capa '" + origen + "' se conserva aunque quede vacía (capa 0, DEFPOINTS o capa actual).");
                            }

                            continue;
                        }

                        plan.CapasCandidatas.Add(e.OriginId);
                        if (e.Restantes == 0)
                        {
                            plan.CapasBorrar.Add(origen);
                        }
                    }
                }

                tr.Commit();
            }

            string? avisoViewports = LayerMapScope.ExcludedViewportsWarning(plan.ViewportsExcluidos);
            if (avisoViewports != null)
            {
                warnings.Add(avisoViewports);
            }

            if (plan.AnonDefsConOrigen > 0)
            {
                warnings.Add(I(plan.AnonDefsConOrigen) + " definición(es) anónima(s) de bloques dinámicos conservan sus capas.");
            }

            if (plan.Forzadas > 0)
            {
                warnings.Add(LayerMapValidator.ForcedPropertiesWarning(plan.Forzadas));
            }

            if (!p.ConservarAspecto)
            {
                string? avisoAspecto = LayerAppearance.WouldChangeWarning(plan.CambianAspecto);
                if (avisoAspecto != null)
                {
                    warnings.Add(avisoAspecto);
                }
            }

            return plan;
        }

        /// <summary>Ámbito de un espacio durante la lectura (§5F.3, 0.6.1).</summary>
        private sealed class SpaceScope
        {
            public SpaceScope(string name, bool isLayout, bool included, bool incluirViewports)
            {
                Name = name;
                IsLayout = isLayout;
                Included = included;
                IncluirViewports = incluirViewports;
            }

            public string Name { get; }

            public bool IsLayout { get; }

            public bool Included { get; }

            public bool IncluirViewports { get; }

            /// <summary>VIEWPORT de este layout en capas origen excluidos por no pasar incluir_viewports.</summary>
            public int ViewportsExcluidos { get; set; }
        }

        /// <summary>
        /// Recorre un espacio (modelo o layout) y decide con <see cref="LayerMapScope.Classify"/> qué hacer con cada
        /// entidad viva cuya capa es un origen (y con los ATTRIB de cada INSERT cuya propia capa es un origen): reasignar
        /// o contarla como excluida (espacio fuera del ámbito, VIEWPORT sin incluir_viewports o viewport n.º 1). El
        /// viewport n.º 1 lo resuelve <see cref="LayoutViewports.PaperViewportId"/> (el mismo criterio que §5C.5):
        /// Layout.GetViewports() si el layout está inicializado (AutoCAD devuelve primero el de papel); si no, Number = 1
        /// y, como respaldo, el primer VIEWPORT del layout (en los layouts no actuales Number = −1 en todos).
        /// </summary>
        private static void ScanSpace(Transaction tr, BlockTableRecord btr, SpaceScope scope, Dictionary<ObjectId, int> byOrigin, Plan plan)
        {
            ObjectId paperId = scope.IsLayout ? LayoutViewports.PaperViewportId(tr, btr) : ObjectId.Null;
            if (!paperId.IsNull)
            {
                plan.ViewportsPapel.Add(paperId);
            }

            foreach (ObjectId id in btr)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                Entity? e = AuditSupport.Open(tr, id);
                if (e == null)
                {
                    continue;
                }

                bool isViewport = false;
                bool isPaper = false;
                if (scope.IsLayout && e is Viewport)
                {
                    isViewport = true;
                    isPaper = id == paperId;
                }

                ObjectId layerId = SafeLayerId(e);
                if (byOrigin.TryGetValue(layerId, out int index))
                {
                    ScopeDecision d = LayerMapScope.Classify(scope.Included, isViewport, isPaper, scope.IncluirViewports);
                    Decide(plan, scope, d, index, id, layerId, AcadUtil.DxfNameFromId(id) ?? AcadUtil.DxfName(e), e);
                }

                if (e is BlockReference br)
                {
                    foreach (ObjectId attId in SafeAttributes(br))
                    {
                        if (attId.IsNull || attId.IsErased || !(AuditSupport.Open(tr, attId) is AttributeReference att))
                        {
                            continue;
                        }

                        ObjectId attLayer = SafeLayerId(att);
                        if (byOrigin.TryGetValue(attLayer, out int attIndex))
                        {
                            ScopeDecision d = LayerMapScope.Classify(scope.Included, false, false, scope.IncluirViewports);
                            Decide(plan, scope, d, attIndex, attId, attLayer, TipoAtributo, att);
                        }
                    }
                }
            }
        }

        /// <summary>Aplica la decisión de ámbito: reasignar (Record) o contar como excluida (también en Restantes).</summary>
        private static void Decide(Plan plan, SpaceScope scope, ScopeDecision d, int index, ObjectId id, ObjectId layerId, string tipo, Entity e)
        {
            EntryPlan entry = plan.Entries[index];
            switch (d)
            {
                case ScopeDecision.Reasignar:
                    Record(plan, index, id, layerId, tipo, scope.Name, false, e);
                    return;
                case ScopeDecision.ExcluidaEspacio:
                    CleanWrite.Increment(entry.ExcluidasEspacio, scope.Name);
                    break;
                case ScopeDecision.ExcluidaViewport:
                    CleanWrite.Increment(entry.ExcluidosViewport, scope.Name);
                    scope.ViewportsExcluidos++;
                    break;
                default:
                    CleanWrite.Increment(entry.ViewportsPapel, scope.Name);
                    break;
            }

            entry.Excluidas++;
            entry.Restantes++;
        }

        /// <summary>
        /// Recorre una definición de bloque con nombre (incluir_bloques; no depende de espacios) y apunta para reasignar
        /// cada entidad viva cuya capa es un origen, más los ATTRIB de cada INSERT anidado cuya propia capa es un origen.
        /// </summary>
        private static void ScanBlock(Transaction tr, BlockTableRecord btr, string label, Dictionary<ObjectId, int> byOrigin, Plan plan)
        {
            foreach (ObjectId id in btr)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                Entity? e = AuditSupport.Open(tr, id);
                if (e == null)
                {
                    continue;
                }

                ObjectId layerId = SafeLayerId(e);
                if (byOrigin.TryGetValue(layerId, out int index))
                {
                    Record(plan, index, id, layerId, AcadUtil.DxfNameFromId(id) ?? AcadUtil.DxfName(e), label, true, e);
                }

                if (e is BlockReference br)
                {
                    foreach (ObjectId attId in SafeAttributes(br))
                    {
                        if (attId.IsNull || attId.IsErased || !(AuditSupport.Open(tr, attId) is AttributeReference att))
                        {
                            continue;
                        }

                        ObjectId attLayer = SafeLayerId(att);
                        if (byOrigin.TryGetValue(attLayer, out int attIndex))
                        {
                            Record(plan, attIndex, attId, attLayer, TipoAtributo, label, true, att);
                        }
                    }
                }
            }
        }

        private static void Record(Plan plan, int index, ObjectId id, ObjectId layerId, string tipo, string label, bool isBlock, Entity e)
        {
            EntryPlan entry = plan.Entries[index];
            entry.N++;
            CleanWrite.Increment(entry.PorTipo, tipo);
            CleanWrite.Increment(isBlock ? entry.PorEspacioBloques : entry.PorEspacio, label);
            if (HasOwnProperties(e))
            {
                plan.Forzadas++;
            }

            AppearanceFix? fix = ComputeFix(e, entry);
            if (fix != null && LayerAppearance.Changes(fix))
            {
                plan.CambianAspecto++;
            }

            plan.Targets.Add(new Target(id, layerId, index, fix));
        }

        /// <summary>
        /// Recorre una definición que no se reasigna (anónima, layout fuera del diccionario o con nombre sin incluir_bloques):
        /// suma a Restantes las entidades/ATTRIB en capas origen (si <paramref name="count"/>). Devuelve true si alguna
        /// entidad de la definición está en una capa origen.
        /// </summary>
        private static bool ScanRemaining(Transaction tr, BlockTableRecord btr, Dictionary<ObjectId, int> byOrigin, Plan plan, bool count)
        {
            bool any = false;
            foreach (ObjectId id in btr)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                Entity? e = AuditSupport.Open(tr, id);
                if (e == null)
                {
                    continue;
                }

                if (byOrigin.TryGetValue(SafeLayerId(e), out int index))
                {
                    any = true;
                    if (!count)
                    {
                        return true;
                    }

                    plan.Entries[index].Restantes++;
                }

                if (count && e is BlockReference br)
                {
                    foreach (ObjectId attId in SafeAttributes(br))
                    {
                        if (!attId.IsNull && !attId.IsErased && AuditSupport.Open(tr, attId) is AttributeReference att
                            && byOrigin.TryGetValue(SafeLayerId(att), out int attIndex))
                        {
                            plan.Entries[attIndex].Restantes++;
                        }
                    }
                }
            }

            return any;
        }

        /// <summary>
        /// Escritura (dentro de UndoGroup.Run): UNA Transaction. Ensure de cada destino (R3, crea los que falten), después
        /// LayerId = destino en cada entidad revalidada (viva y todavía en su capa origen; con conservar_aspecto, fijando
        /// antes lo que heredaba de la capa origen) y, con borrar_capas_origen_vacias,
        /// Database.Purge sobre las capas origen candidatas y Erase de las purgables.
        /// </summary>
        private static object Write(Document doc, int pid, int ins, List<string> warnings, Params p, Plan plan, long lecturaMs)
        {
            Database db = doc.Database;
            var sw = Stopwatch.StartNew();
            var created = new List<string>();
            List<string>? borradas = p.BorrarVacias ? new List<string>() : null;
            int reasignadas = 0;
            int perdidas = 0;
            int aspectoFijado = 0;
            int pierdenNoPlot = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // 1. Destinos (Ensure: R3 con las advertencias de tipo de línea no cargado / capa apagada).
                var ensureWarnings = new List<string>();
                var destIds = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                foreach ((string param, string nombre, bool _) in plan.Destinos)
                {
                    DrawLayerFactory.Result res = DrawLayerFactory.Ensure(db, tr, param, null, ensureWarnings);
                    destIds[nombre] = res.Id;
                    if (res.Creada)
                    {
                        created.Add(res.Nombre);
                    }
                }

                foreach (string w in ensureWarnings)
                {
                    AddOnce(warnings, w);
                }

                var entryDest = new ObjectId[plan.Entries.Count];
                for (int i = 0; i < plan.Entries.Count; i++)
                {
                    entryDest[i] = destIds[plan.Entries[i].DestinoNombre];
                }

                // 2. Reasignación (revalida cada objeto antes de tocarlo).
                foreach (Target t in plan.Targets)
                {
                    if (t.Id.IsNull || t.Id.IsErased || !(tr.GetObject(t.Id, OpenMode.ForRead, false) is Entity e) || e.LayerId != t.OriginLayer)
                    {
                        perdidas++;
                        continue;
                    }

                    if (e is Viewport vpw && (plan.ViewportsPapel.Contains(t.Id) || SafeNumber(vpw) == 1))
                    {
                        // Defensa: el viewport n.º 1 (espacio papel) nunca se reasigna (la lectura ya lo excluyó): ni el
                        // resuelto en la lectura (también en layouts no actuales, Number = −1) ni uno con Number = 1.
                        perdidas++;
                        continue;
                    }

                    e.UpgradeOpen();
                    if (p.ConservarAspecto && t.Fix != null)
                    {
                        pierdenNoPlot += t.Fix.PierdeNoPlot ? 1 : 0;
                        if (LayerAppearance.Changes(t.Fix))
                        {
                            ApplyFix(e, t.Fix, plan.Entries[t.Entry].OrigenColor!);
                            aspectoFijado++;
                        }
                    }

                    e.LayerId = entryDest[t.Entry];
                    reasignadas++;
                }

                // 3. Capas origen vacías: Database.Purge decide (ve las reasignaciones de esta misma transacción).
                if (borradas != null && plan.CapasCandidatas.Count > 0)
                {
                    var candidates = new ObjectIdCollection();
                    foreach (ObjectId id in plan.CapasCandidatas)
                    {
                        if (!id.IsNull && !id.IsErased && id != db.Clayer)
                        {
                            candidates.Add(id);
                        }
                    }

                    db.Purge(candidates);
                    foreach (ObjectId id in candidates)
                    {
                        if (tr.GetObject(id, OpenMode.ForWrite, false) is LayerTableRecord ltr)
                        {
                            string name = ltr.Name;
                            ltr.Erase();
                            borradas.Add(name);
                        }
                    }
                }

                tr.Commit();
            }

            long escrituraMs = sw.ElapsedMilliseconds;
            if (perdidas > 0)
            {
                warnings.Add(I(perdidas) + " entidad(es) previstas ya no existían o cambiaron de capa desde la lectura; no se reasignaron.");
            }

            if (borradas != null && !SameNames(borradas, plan.CapasBorrar))
            {
                warnings.Add("Las capas origen borradas difieren de la previsualización (Database.Purge decide): previstas "
                    + I(plan.CapasBorrar.Count) + ", borradas " + I(borradas.Count) + ".");
            }

            if (p.ConservarAspecto)
            {
                string? avisoFijado = LayerAppearance.FixedWarning(aspectoFijado, pierdenNoPlot);
                if (avisoFijado != null)
                {
                    warnings.Add(avisoFijado);
                }
            }

            PluginLog.Info("normalizar_capas: " + I(reasignadas) + " entidad(es) reasignada(s) en "
                + escrituraMs.ToString(CultureInfo.InvariantCulture) + " ms");
            return BuildResponse(pid, CleanWrite.Mode.Write, ins, warnings, p, plan, reasignadas, created, borradas, lecturaMs, escrituraMs, aspectoFijado);
        }

        private static Dictionary<string, object?> BuildResponse(
            int pid, CleanWrite.Mode mode, int ins, List<string> warnings, Params p, Plan plan, int? reasignadas,
            List<string> capasCreadas, List<string>? capasBorradas, long lecturaMs, long? escrituraMs, int? aspectoFijado)
        {
            Dictionary<string, object?> r = CleanWrite.Response(pid, mode, ins, warnings);
            var creadasSet = new HashSet<string>(capasCreadas, StringComparer.OrdinalIgnoreCase);
            var mapa = new List<Dictionary<string, object?>>(plan.Entries.Count);
            var porOrigen = new Dictionary<string, object?>(StringComparer.Ordinal);
            int total = 0;
            int excluidas = 0;
            foreach (EntryPlan e in plan.Entries)
            {
                LayerPlan destino = e.Entry.Destino;
                mapa.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["origen"] = e.Entry.Origen,
                    ["destino"] = e.DestinoNombre,
                    ["destino_concepto"] = destino.Origen == LayerPlan.OrigenConcepto ? destino.Concepto : null,
                    ["destino_existe"] = e.Entry.DestinoExiste,
                    ["destino_creada"] = creadasSet.Contains(e.DestinoNombre),
                    ["n"] = e.N,
                });

                var detail = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["por_tipo"] = CleanWrite.SortedCounts(e.PorTipo),
                    ["por_espacio"] = InOrder(e.PorEspacio),
                };
                if (p.IncluirBloques)
                {
                    detail["por_espacio_bloques"] = CleanWrite.SortedCounts(e.PorEspacioBloques);
                }

                detail["excluidas"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["n"] = e.Excluidas,
                    ["por_espacio"] = InOrder(e.ExcluidasEspacio),
                    ["viewports"] = InOrder(e.ExcluidosViewport),
                    ["viewports_papel"] = InOrder(e.ViewportsPapel),
                };
                porOrigen[e.Entry.Origen] = detail;
                total += e.N;
                excluidas += e.Excluidas;
            }

            r["mapa"] = mapa;
            r["n_reasignar"] = total;
            r["n_reasignadas"] = reasignadas;
            r["por_origen"] = porOrigen;
            r["n_excluidas"] = excluidas;
            r["espacios"] = plan.Espacios == null ? null : new List<string>(plan.Espacios);
            r["incluir_viewports"] = p.IncluirViewports;
            r["n_propiedades_forzadas"] = plan.Forzadas;   // significado previo a 0.7: lo fijado se cuenta en n_aspecto_fijado (§5G.5)
            r["n_cambian_aspecto"] = plan.CambianAspecto;
            r["n_aspecto_fijado"] = aspectoFijado;
            r["incluir_bloques"] = p.IncluirBloques;
            r["borrar_capas_origen_vacias"] = p.BorrarVacias;
            r["capas_borrar"] = plan.CapasBorrar;
            r["capas_borradas"] = capasBorradas;
            r["capas_creadas"] = capasCreadas;
            r["duracion_ms"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["lectura"] = lecturaMs,
                ["escritura"] = escrituraMs,
            };
            r["origen_texto"] = AuditSupport.OrigenTexto;
            return r;
        }

        // ------------------------------------------------------------------ apoyo

        /// <summary>Rechazo del validador → ToolException (NOT_FOUND con data.capas/data.capa + detalle; INVALID_PARAMS).</summary>
        private static ToolException ToToolException(LayerMapError error)
        {
            var data = new Dictionary<string, object?>();
            if (error.Capas != null)
            {
                data["capas"] = new List<string>(error.Capas);
            }

            if (error.Capa != null)
            {
                data["capa"] = error.Capa;
            }

            if (error.Codigo == LayerMapValidator.CodigoNotFound)
            {
                data["detalle"] = error.Detalle;
                string message = error.Capas == null && error.Capa != null
                    ? "La capa '" + error.Capa + "' no existe en el dibujo."
                    : error.Detalle;
                return new ToolException(ErrorCodes.NotFound, message, data);
            }

            return ToolException.InvalidParams(error.Detalle, data);
        }

        /// <summary>true si la entidad tiene color, tipo de línea o grosor propios (no ByLayer).</summary>
        private static bool HasOwnProperties(Entity e)
        {
            try
            {
                return !e.Color.IsByLayer
                    || !string.Equals(e.Linetype, "ByLayer", StringComparison.OrdinalIgnoreCase)
                    || e.LineWeight != LineWeight.ByLayer;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Qué fijar en la entidad (§5G.5); null si sus propiedades no se pueden leer.</summary>
        private static AppearanceFix? ComputeFix(Entity e, EntryPlan entry)
        {
            ByLayerFlags flags;
            try
            {
                flags = new ByLayerFlags(
                    e.Color.IsByLayer,
                    string.Equals(e.Linetype, "ByLayer", StringComparison.OrdinalIgnoreCase),
                    e.LineWeight == LineWeight.ByLayer,
                    e.Transparency.IsByLayer);
            }
            catch (Exception)
            {
                return null;
            }

            return LayerAppearance.Compute(flags, entry.OrigenLook!, entry.DestinoLook!);
        }

        /// <summary>Fija en la entidad (abierta para escritura) lo que indica <paramref name="fix"/>, antes de cambiarle la capa.</summary>
        private static void ApplyFix(Entity e, AppearanceFix fix, Color origenColor)
        {
            if (fix.Color != null)
            {
                e.Color = origenColor.IsByAci ? Color.FromColorIndex(ColorMethod.ByAci, (short)fix.Color.Value) : origenColor;
            }

            if (fix.TipoLinea != null)
            {
                e.Linetype = fix.TipoLinea;
            }

            if (fix.Grosor != null)
            {
                e.LineWeight = (LineWeight)fix.Grosor.Value;
            }

            if (fix.Transparencia != null)
            {
                e.Transparency = new Transparency((byte)Math.Clamp(Math.Round(255.0 * (1.0 - fix.Transparencia.Value / 100.0)), 0.0, 255.0));
            }
        }

        /// <summary>Lo que hereda una entidad ByLayer de esta capa. Transparencia en %: alfa 255 = opaca = 0 %.</summary>
        private static LayerLook ReadLook(LayerTableRecord layer, Transaction tr)
        {
            string tipoLinea = "Continuous";
            if (!layer.LinetypeObjectId.IsNull && tr.GetObject(layer.LinetypeObjectId, OpenMode.ForRead, false) is LinetypeTableRecord lt)
            {
                tipoLinea = lt.Name;
            }

            Transparency t = layer.Transparency;
            double transparencia = t.IsByAlpha ? (255 - t.Alpha) / 255.0 * 100.0 : 0.0;
            return new LayerLook(layer.Color.ColorIndex, tipoLinea, (int)layer.LineWeight, transparencia, layer.IsPlottable);
        }

        /// <summary>
        /// Aspecto de la capa destino. Si aún no existe, el que le dará DrawLayerFactory.Ensure: propiedades de lps.json con
        /// sus mismas sustituciones (tipo de línea no cargado → Continuous, grosor no válido → Default) y transparencia 0.
        /// </summary>
        private static LayerLook DestinationLook(Database db, Transaction tr, LayerTable table, EntryPlan e)
        {
            ObjectId id = McpLayerFactory.FindLayer(table, e.DestinoNombre);
            if (!id.IsNull)
            {
                return ReadLook((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead, false), tr);
            }

            LayerSpec? spec = e.Entry.Destino.Especificacion;
            if (spec == null)
            {
                return e.OrigenLook!;   // el validador solo deja pasar destinos creables: solo por defensa
            }

            var linetypes = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            string tipoLinea = !string.IsNullOrEmpty(spec.TipoLinea) && linetypes.Has(spec.TipoLinea) ? spec.TipoLinea : "Continuous";
            int grosor = Enum.IsDefined(typeof(LineWeight), spec.Grosor) ? spec.Grosor : (int)LineWeight.ByLineWeightDefault;
            return new LayerLook(Math.Clamp(spec.Color, 1, 255), tipoLinea, grosor, 0.0, spec.Plot);
        }

        /// <summary>
        /// true si el espacio es *Model_Space (por el registro de bloque, no por el nombre: un layout puede llamarse
        /// "modelo", O2).
        /// </summary>
        private static bool IsModelSpace(SpaceResolver.ResolvedSpace s) =>
            string.Equals(s.Btr.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase);

        /// <summary>Viewport.Number (−1 si no se puede leer o en un layout que no es el actual).</summary>
        private static int SafeNumber(Viewport vp)
        {
            try
            {
                return vp.Number;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static ObjectId SafeLayerId(Entity e)
        {
            try
            {
                return e.LayerId;
            }
            catch (Exception)
            {
                return ObjectId.Null;
            }
        }

        private static IEnumerable<ObjectId> SafeAttributes(BlockReference br)
        {
            var list = new List<ObjectId>();
            try
            {
                foreach (ObjectId id in br.AttributeCollection)
                {
                    list.Add(id);
                }
            }
            catch (Exception)
            {
                // Colección ilegible: sin atributos.
            }

            return list;
        }

        /// <summary>Conteos en el orden de inserción (espacios: modelo y layouts por TabOrder).</summary>
        private static Dictionary<string, object?> InOrder(Dictionary<string, int> counts)
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in counts)
            {
                d[kv.Key] = kv.Value;
            }

            return d;
        }

        private static bool SameNames(List<string> a, List<string> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            var set = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
            foreach (string x in b)
            {
                if (!set.Contains(x))
                {
                    return false;
                }
            }

            return true;
        }

        private static void AddOnce(List<string> warnings, string w)
        {
            if (!string.IsNullOrEmpty(w) && !warnings.Contains(w))
            {
                warnings.Add(w);
            }
        }

        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
