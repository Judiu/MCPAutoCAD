using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// insertar_bloque (protocol.md §5D.5 y §5G.6). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z: definición
    /// importada de la biblioteca + capas que traiga + INSERT + atributos). Flujo §5D.0: Parse → RequireDocument →
    /// R2 (CheckUnits) → R4/R5 (PlacementCheck, caja = punto de inserción) → resolución previa de la definición (lectura)
    /// → validación previa completa (lectura: espacio, capa destino, etiquetas de atributos) → UndoGroup.Run con UNA
    /// transacción.
    /// Corrección 0.4.0 (auditoría de Fase 3): Database.Insert (importación desde la biblioteca) NO se revierte con el
    /// Abort de la Transaction, así que nada que pueda fallar por validación puede ir después de importar (R1). Toda la
    /// validación se hace en <see cref="PreValidate"/> antes del UndoGroup, leyendo los ATTDEF de la base lateral del
    /// .dwg de la biblioteca; dentro de la escritura se repite la comprobación de etiquetas sobre la base lateral recién
    /// leída y ANTES de Database.Insert (el archivo podría haber cambiado entre ambas lecturas). Tras importar solo
    /// quedan operaciones que no validan datos del cliente (DrawLayerFactory.Ensure ya comprobado, creación del INSERT y
    /// de sus ATTRIB); un fallo inesperado de AutoCAD ahí sigue siendo INTERNAL y es un riesgo residual a verificar en
    /// el gate en vivo (si deja la definición importada en el dibujo).
    /// Decisiones documentadas (no cambian el contrato):
    /// - escala [sx, sy] → sz = sx (como el comando INSERT, cuya Z por defecto es la X).
    /// - Una definición xref (o dependiente de xref) existente → INVALID_PARAMS "nombre: no se insertan referencias
    ///   externas (xref) ni bloques dependientes de xref" (el contrato dice que no se admiten pero no fija el texto).
    /// - Los ATTDEF constantes no admiten valor: su etiqueta cuenta como desconocida y no aparece en data.etiquetas.
    /// - El bloque importado se define con el nombre del ARCHIVO (mayúsculas del disco), no con las del cliente.
    /// - propiedades_dinamicas se aplica tras crear el INSERT y sus atributos: solo puede comprobarse sobre la referencia
    ///   ya creada (§5G.6), así que un rechazo tras una importación deja el riesgo residual de arriba y se registra en
    ///   plugin.log. Una propiedad repetida en distinta caja → "propiedades_dinamicas: propiedad repetida '&lt;p&gt;'"; un
    ///   valor que no sea número finito ni cadena → "propiedades_dinamicas.&lt;p&gt;: debe ser un número o una cadena".
    /// - La sustitución "número de" (PlanoNFormat) se intenta en todo atributo multilínea con ese formato (§5D.5); la
    ///   advertencia de formato no reconocido solo se emite para PLANO_N. Un valor "" deja el atributo en blanco.
    /// </summary>
    internal sealed class InsertarBloqueHandler : IToolHandler
    {
        private const int MaxAttributes = 100;
        private const int MaxDynamicProperties = 20;

        public const string XrefMessage = "nombre: no se insertan referencias externas (xref) ni bloques dependientes de xref";
        public const string LibraryHint = "pase desde_biblioteca: true si el bloque está en la biblioteca";

        private static readonly string[] Own = { "nombre", "posicion", "escala", "rotacion", "atributos", "desde_biblioteca", "escala_unidades", "propiedades_dinamicas" };
        private static readonly string[] Allowed = BuildAllowed();

        public string Name => "insertar_bloque";

        public string Description =>
            "ESCRIBE en el dibujo: inserta una referencia de bloque en 'posicion' y rellena sus atributos por etiqueta (sin " +
            "distinguir mayúsculas), en un solo paso de deshacer. Si el bloque no existe en el dibujo y 'desde_biblioteca' es " +
            "true, importa su definición de la biblioteca del plugin (hoy LPS_ROTULO, Puerta Dinamica LM, PuertaBaño, VENTANA01 y TANQUE 1000 LTS; el cliente solo da el nombre, nunca rutas). " +
            "Con escala_unidades 'auto' aplica el factor de unidades bloque→dibujo como INSERT; use 'ninguna' si la geometría del " +
            "bloque ya está en las unidades del dibujo. Para PLANO_N del rótulo pase solo el número ('3'); en atributos multilínea (p. ej. MUNICIPIO o PROYECTISTA del rótulo) separe las líneas con \\n. Capa por defecto " +
            "MCP-DIBUJO (para el rótulo use capa 'rotulo'). En un bloque dinámico, 'propiedades_dinamicas' fija sus parámetros por nombre " +
            "y la respuesta devuelve siempre 'dinamico' y las propiedades con sus nombres reales.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""nombre"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre del bloque (sin distinguir mayúsculas). Solo el nombre: sin rutas, sin \\, /, :, .. ni extensión .dwg. Los bloques anónimos (*U…) no se admiten."" },
    ""posicion"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3, ""description"": ""Punto de inserción [x, y] o [x, y, z] en WCS."" },
    ""escala"": { ""oneOf"": [ { ""type"": ""number"", ""not"": { ""const"": 0 } }, { ""type"": ""array"", ""items"": { ""type"": ""number"", ""not"": { ""const"": 0 } }, ""minItems"": 2, ""maxItems"": 3 } ], ""default"": 1, ""description"": ""Escala pedida: número uniforme o [sx, sy] / [sx, sy, sz], todos distintos de 0 (negativo = espejo; con [sx, sy] la Z es sx). Con escala_unidades 'auto' se multiplica por el factor de unidades."" },
    ""rotacion"": { ""type"": ""number"", ""default"": 0, ""description"": ""Rotación en grados, antihoraria desde +X."" },
    ""atributos"": { ""type"": ""object"", ""minProperties"": 1, ""maxProperties"": 100, ""additionalProperties"": { ""type"": ""string"", ""maxLength"": 500 }, ""description"": ""Valores por etiqueta (TAG → texto literal, 0–500 caracteres; sin distinguir mayúsculas). Las etiquetas no dadas toman el valor por defecto del bloque; '' deja el atributo en blanco. Para PLANO_N del rótulo pase solo el número. En atributos multilínea (MUNICIPIO, PROYECTISTA del rótulo) separe las líneas con \\n; en los de una línea los saltos se sustituyen por espacios."" },
    ""desde_biblioteca"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = si el bloque no existe en el dibujo, importarlo de la biblioteca de bloques del plugin."" },
    ""escala_unidades"": { ""type"": ""string"", ""enum"": [""auto"", ""ninguna""], ""default"": ""auto"", ""description"": ""'auto' = aplicar el factor de unidades del bloque al dibujo (como INSERT); 'ninguna' = usar la escala tal cual."" },
    ""propiedades_dinamicas"": { ""type"": ""object"", ""minProperties"": 1, ""maxProperties"": 20, ""additionalProperties"": { ""oneOf"": [ { ""type"": ""number"" }, { ""type"": ""string"" } ] }, ""description"": ""Solo bloques dinámicos: valores por nombre de propiedad (nombre → número o cadena, sin distinguir mayúsculas). Se aplican tras crear la referencia; los nombres reales los devuelve 'propiedades_dinamicas' en la respuesta. Un rechazo tras importar de la biblioteca puede dejar la definición en el dibujo."" },
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""espacio"": " + DrawWrite.SchemaEspacio + @",
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @"
  },
  ""required"": [""nombre"", ""posicion""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""entidad"": { ""type"": ""object"" },
    ""atributos"": { ""type"": ""object"" },
    ""handles_atributos"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""importado_de_biblioteca"": { ""type"": ""boolean"" },
    ""archivo"": { ""type"": ""string"" },
    ""capas_importadas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""dinamico"": { ""type"": ""boolean"" },
    ""propiedades_dinamicas"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""unidades_bloque"": { ""type"": ""integer"" },
    ""factor_unidades"": { ""type"": ""number"" },
    ""escala_aplicada"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""escala_unidades"": { ""type"": ""string"" },
    ""capa"": { ""type"": ""string"" },
    ""capa_creada"": { ""type"": ""boolean"" },
    ""espacio"": { ""type"": ""string"" },
    ""origen_texto"": { ""type"": ""string"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""handles"", ""entidad"", ""atributos"", ""handles_atributos"", ""importado_de_biblioteca"", ""dinamico"", ""propiedades_dinamicas"", ""unidades_bloque"", ""factor_unidades"", ""escala_aplicada"", ""escala_unidades"", ""capa"", ""capa_creada"", ""espacio"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        /// <summary>Parámetros ya validados.</summary>
        private sealed class InsertParams
        {
            public InsertParams(
                string nombre, Point3 posicion, double[] escala, double rotacionGrados,
                List<KeyValuePair<string, string>> atributos, bool desdeBiblioteca, string escalaUnidades,
                Dictionary<string, object>? propiedadesDinamicas, DrawWrite.Common common)
            {
                Nombre = nombre;
                Posicion = posicion;
                Escala = escala;
                RotacionGrados = rotacionGrados;
                Atributos = atributos;
                DesdeBiblioteca = desdeBiblioteca;
                EscalaUnidades = escalaUnidades;
                PropiedadesDinamicas = propiedadesDinamicas;
                Common = common;
            }

            public string Nombre { get; }
            public Point3 Posicion { get; }
            public double[] Escala { get; }
            public double RotacionGrados { get; }

            /// <summary>Atributos del cliente (TAG tal cual lo escribió → valor), sin repetidos ignorando mayúsculas.</summary>
            public List<KeyValuePair<string, string>> Atributos { get; }

            public bool DesdeBiblioteca { get; }
            public string EscalaUnidades { get; }

            /// <summary>Propiedades dinámicas del cliente (nombre → double o string); null si no las pasó.</summary>
            public Dictionary<string, object>? PropiedadesDinamicas { get; }

            public DrawWrite.Common Common { get; }
        }

        /// <summary>Definición resuelta antes de escribir: existente en el dibujo o archivo de la biblioteca.</summary>
        private sealed class Definition
        {
            public Definition(string nombre, string? archivo)
            {
                Nombre = nombre;
                Archivo = archivo;
            }

            /// <summary>Nombre real del bloque (el del dibujo, o el del archivo de la biblioteca).</summary>
            public string Nombre { get; }

            /// <summary>Ruta del DWG de la biblioteca si hay que importarlo; null si ya existe en el dibujo.</summary>
            public string? Archivo { get; }

            public bool FromLibrary => Archivo != null;
        }

        private static string[] BuildAllowed()
        {
            var list = new List<string>(Own);
            list.AddRange(DrawWrite.CommonParams);
            return list.ToArray();
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        /// <summary>Valida y convierte params. Sin AutoCAD: hilo del pipe y otra vez en el principal.</summary>
        private static InsertParams Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);

            string nombre = ParamReader.RequiredText(p, "nombre", 1, 255);
            string? nameError = BlockNameRules.Validate(nombre);
            if (nameError != null)
            {
                throw ToolException.InvalidParams(nameError, new Dictionary<string, object?> { ["bloque"] = nombre });
            }

            Point3 posicion = ParamReader.RequiredPoint(p, "posicion");
            double[] escala = ReadEscala(p);
            double rotacion = ReadAngle(p, "rotacion") ?? 0.0;
            List<KeyValuePair<string, string>> atributos = ReadAttributes(p);
            bool desdeBiblioteca = ParamReader.OptionalBool(p, "desde_biblioteca") ?? false;
            string modo = ParamReader.OptionalEnum(p, "escala_unidades", BlockInsertScale.Modos) ?? BlockInsertScale.ModoAuto;
            Dictionary<string, object>? propiedades = ReadDynamicProperties(p);
            DrawWrite.Common common = DrawWrite.ReadCommon(p, null);
            return new InsertParams(nombre, posicion, escala, rotacion, atributos, desdeBiblioteca, modo, propiedades, common);
        }

        /// <summary>escala: número ≠ 0 o array [sx, sy] / [sx, sy, sz] de números ≠ 0; defecto [1, 1, 1].</summary>
        private static double[] ReadEscala(JsonElement p)
        {
            if (!p.TryGetProperty("escala", out JsonElement el))
            {
                return new[] { 1.0, 1.0, 1.0 };
            }

            try
            {
                if (el.ValueKind == JsonValueKind.Number)
                {
                    if (!el.TryGetDouble(out double s))
                    {
                        throw new ArgumentException(BlockInsertScale.InvalidScale);
                    }

                    return BlockInsertScale.ParseEscala(s);
                }

                if (el.ValueKind == JsonValueKind.Array)
                {
                    var values = new List<double>(3);
                    foreach (JsonElement c in el.EnumerateArray())
                    {
                        if (c.ValueKind != JsonValueKind.Number || !c.TryGetDouble(out double v))
                        {
                            throw new ArgumentException(BlockInsertScale.InvalidScale);
                        }

                        values.Add(v);
                    }

                    return BlockInsertScale.ParseEscala(values);
                }
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }

            throw ToolException.InvalidParams(BlockInsertScale.InvalidScale);
        }

        /// <summary>Ángulo opcional en grados: cualquier número finito (se normaliza al escribir).</summary>
        private static double? ReadAngle(JsonElement p, string name)
        {
            if (!p.TryGetProperty(name, out JsonElement el))
            {
                return null;
            }

            if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out double v) || !double.IsFinite(v))
            {
                throw ToolException.InvalidParams(name + ": debe ser un número finito (grados)");
            }

            return v;
        }

        /// <summary>atributos: objeto TAG → cadena (1–100 entradas, valores 0–500 caracteres), sin tags repetidos (sin mayúsculas).</summary>
        private static List<KeyValuePair<string, string>> ReadAttributes(JsonElement p)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (!p.TryGetProperty("atributos", out JsonElement el))
            {
                return list;
            }

            string shape = "atributos: debe ser un objeto con 1 a " + MaxAttributes.ToString(CultureInfo.InvariantCulture) + " etiquetas";
            if (el.ValueKind != JsonValueKind.Object)
            {
                throw ToolException.InvalidParams(shape);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty prop in el.EnumerateObject())
            {
                string tag = prop.Name;
                if (tag.Trim().Length == 0)
                {
                    throw ToolException.InvalidParams("atributos: etiqueta vacía");
                }

                if (!seen.Add(tag))
                {
                    throw ToolException.InvalidParams("atributos: etiqueta repetida '" + tag + "'");
                }

                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    throw ToolException.InvalidParams("atributos." + tag + ": debe ser una cadena");
                }

                string value = prop.Value.GetString() ?? "";
                if (ParamReader.CodePointCount(value) > PluginInfo.MaxAttributeLength)
                {
                    throw ToolException.InvalidParams(
                        "atributos." + tag + ": debe tener entre 0 y " + PluginInfo.MaxAttributeLength.ToString(CultureInfo.InvariantCulture) + " caracteres");
                }

                list.Add(new KeyValuePair<string, string>(tag, value));
            }

            if (list.Count < 1 || list.Count > MaxAttributes)
            {
                throw ToolException.InvalidParams(shape);
            }

            return list;
        }

        /// <summary>propiedades_dinamicas: objeto nombre → número finito o cadena (1–20 entradas), sin repetidos ignorando mayúsculas; null si falta.</summary>
        private static Dictionary<string, object>? ReadDynamicProperties(JsonElement p)
        {
            if (!p.TryGetProperty("propiedades_dinamicas", out JsonElement el))
            {
                return null;
            }

            string shape = "propiedades_dinamicas: debe ser un objeto con 1 a " + MaxDynamicProperties.ToString(CultureInfo.InvariantCulture) + " propiedades";
            if (el.ValueKind != JsonValueKind.Object)
            {
                throw ToolException.InvalidParams(shape);
            }

            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty prop in el.EnumerateObject())
            {
                string name = prop.Name;
                if (name.Trim().Length == 0)
                {
                    throw ToolException.InvalidParams("propiedades_dinamicas: propiedad con nombre vacío");
                }

                if (values.ContainsKey(name))
                {
                    throw ToolException.InvalidParams("propiedades_dinamicas: propiedad repetida '" + name + "'");
                }

                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    values[name] = prop.Value.GetString() ?? "";
                }
                else if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out double number) && double.IsFinite(number))
                {
                    values[name] = number;
                }
                else
                {
                    throw ToolException.InvalidParams("propiedades_dinamicas." + name + ": debe ser un número o una cadena");
                }
            }

            if (values.Count < 1 || values.Count > MaxDynamicProperties)
            {
                throw ToolException.InvalidParams(shape);
            }

            return values;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            InsertParams p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;

            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = DrawWrite.CheckUnits(db, p.Common.AceptarSinUnidades, warnings);                       // R2
            PlacementCheck.Run(db, p.Common.Espacio, new[] { PlacementCheck.PointBox(p.Posicion) }, p.Common.Forzar, "entidades", warnings); // R4/R5

            // Resolución de la definición ANTES del UndoGroup (lectura): los NOT_FOUND / anónimos / xref no crean pasos.
            Definition def = ResolveDefinition(db, p);

            // Validación completa ANTES de escribir (R1; Database.Insert no se revierte): espacio, capa, etiquetas.
            (string capaDestino, bool capaExiste) = PreValidate(db, p, def);
            return UndoGroup.Run(doc, d => Write(d, p, def, capaDestino, capaExiste, ctx.Pid, ins, warnings));
        }

        /// <summary>
        /// Lectura previa sin efectos (StartOpenCloseTransaction, fuera del UndoGroup): (1) espacio destino (R5; ya lo
        /// resolvió PlacementCheck, se repite como defensa); (2) capa destino según R3 sin crearla
        /// (<see cref="DrawLayerFactory.Check"/>: bloqueada/congelada/xref → INVALID_PARAMS; inexistente no creable →
        /// NOT_FOUND); (3) etiquetas pedidas contra los ATTDEF no constantes de la definición: la del dibujo, o la del
        /// .dwg de la biblioteca leído en una base lateral que se descarta al terminar. Con biblioteca y capa destino
        /// inexistente en el dibujo, si el archivo trae una capa con ese nombre se le aplican también las comprobaciones
        /// de R3.5, porque Database.Insert la traerá con sus propiedades y Ensure la encontrará ya importada.
        /// </summary>
        private static (string Capa, bool Existe) PreValidate(Database db, InsertParams p, Definition def)
        {
            LayerPlan plan;
            bool layerExists;
            List<string>? tags = null;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                SpaceResolver.Resolve(db, tr, p.Common.Espacio);                              // R5
                plan = DrawLayerFactory.Check(db, tr, p.Common.Capa, null, out layerExists);    // R3
                if (!def.FromLibrary)
                {
                    BlockTableRecord btr = FindBlock(db, tr, def.Nombre) ?? throw ToolException.BlockNotFound(def.Nombre);
                    tags = TagsOf(CollectAttributeDefinitions(btr, tr));
                }

                tr.Commit();
            }

            if (!def.FromLibrary)
            {
                RequireKnownTags(p.Atributos, tags!);
                return (plan.Nombre, layerExists);
            }

            using (Database side = BlockLibrary.ReadSide(def.Archivo!))
            {
                if (!layerExists)
                {
                    BlockLibrary.CheckSideLayer(side, plan.Nombre, l => DrawLayerFactory.RequireWritable(l, new List<string>()));
                }

                RequireKnownTags(p.Atributos, BlockLibrary.ReadAttributeTags(side));
            }

            return (plan.Nombre, layerExists);
        }

        /// <summary>
        /// (1) existe en el dibujo (no anónimo, no xref) → se usa; (2) no existe y sin desde_biblioteca → NOT_FOUND con
        /// detalle; (3) biblioteca: &lt;carpeta&gt;\&lt;nombre&gt;.dwg o NOT_FOUND (data.bloque, data.archivo).
        /// </summary>
        private static Definition ResolveDefinition(Database db, InsertParams p)
        {
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                BlockTableRecord? btr = FindBlock(db, tr, p.Nombre);
                if (btr != null)
                {
                    RequireInsertable(btr, p.Nombre);
                    string name = btr.Name;
                    tr.Commit();
                    return new Definition(name, null);
                }

                tr.Commit();
            }

            if (!p.DesdeBiblioteca)
            {
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "El bloque '" + p.Nombre + "' no existe en el dibujo.",
                    new Dictionary<string, object?> { ["bloque"] = p.Nombre, ["detalle"] = LibraryHint });
            }

            string? file = BlockLibrary.FindFile(p.Nombre);
            if (file == null)
            {
                string expected;
                try
                {
                    expected = BlockNameRules.LibraryFile(BlockLibrary.Folder, p.Nombre);
                }
                catch (ArgumentException ex)
                {
                    throw ToolException.InvalidParams(ex.Message);
                }

                throw new ToolException(
                    ErrorCodes.NotFound,
                    "El bloque '" + p.Nombre + "' no está en el dibujo ni en la biblioteca (" + BlockLibrary.Folder + ").",
                    new Dictionary<string, object?> { ["bloque"] = p.Nombre, ["archivo"] = expected });
            }

            string blockName = System.IO.Path.GetFileNameWithoutExtension(file);
            try
            {
                SymbolUtilityServices.ValidateSymbolName(blockName, false);
            }
            catch (AcRuntimeException)
            {
                throw ToolException.InvalidParams(BlockNameRules.PathMessage, new Dictionary<string, object?> { ["bloque"] = p.Nombre });
            }

            return new Definition(blockName, file);
        }

        /// <summary>Definición no borrada con ese nombre (sin mayúsculas), o null.</summary>
        private static BlockTableRecord? FindBlock(Database db, Transaction tr, string nombre)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            bool has;
            try
            {
                has = bt.Has(nombre);
            }
            catch (AcRuntimeException)
            {
                has = false;
            }

            if (!has)
            {
                return null;
            }

            ObjectId id = bt[nombre];
            if (id.IsNull || id.IsErased)
            {
                return null;
            }

            return (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
        }

        /// <summary>Layouts y anónimos → "nombre: no se insertan bloques anónimos"; xref/dependiente → <see cref="XrefMessage"/>.</summary>
        private static void RequireInsertable(BlockTableRecord btr, string nombre)
        {
            var data = new Dictionary<string, object?> { ["bloque"] = nombre };
            if (btr.IsLayout || btr.IsAnonymous || btr.Name.StartsWith("*", StringComparison.Ordinal))
            {
                throw ToolException.InvalidParams(BlockNameRules.AnonymousMessage, data);
            }

            if (btr.IsFromExternalReference || btr.IsFromOverlayReference || btr.IsDependent || btr.Name.IndexOf('|') >= 0)
            {
                throw ToolException.InvalidParams(XrefMessage, data);
            }
        }

        /// <summary>
        /// Escritura dentro de UNA transacción (R1). Una excepción la descarta (capa creada, INSERT, ATTRIB), pero
        /// Database.Insert no se revierte con el Abort: por eso toda la validación va en <see cref="PreValidate"/> y,
        /// para las etiquetas, también antes de Database.Insert. Nunca cambia CTAB/TILEMODE/CLAYER ni la vista.
        /// </summary>
        private static object Write(Document doc, InsertParams p, Definition def, string capaDestino, bool capaExiste, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // La importación va PRIMERO, antes de abrir ninguna tabla en esta transacción (ver BlockLibrary.Import).
                ObjectId btrId = ObjectId.Null;
                int fileUnits = 0;
                List<string>? importedLayers = null;
                // Todo lo validable ya se comprobó en PreValidate; las etiquetas se vuelven a comprobar sobre la base
                // lateral recién leída y ANTES de Database.Insert, que no se revierte con el Abort.
                if (def.FromLibrary)
                {
                    btrId = BlockLibrary.Import(
                        db, tr, def.Nombre, def.Archivo!,
                        side =>
                        {
                            // Se repite también R3.5 sobre la capa homónima del archivo, por si cambió entre lecturas.
                            if (!capaExiste)
                            {
                                BlockLibrary.CheckSideLayer(side, capaDestino, l => DrawLayerFactory.RequireWritable(l, new List<string>()));
                            }

                            RequireKnownTags(p.Atributos, BlockLibrary.ReadAttributeTags(side));
                        },
                        out fileUnits, out importedLayers);
                }

                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, p.Common.Espacio);   // R5
                DrawLayerFactory.Result layer = DrawLayerFactory.Ensure(db, tr, p.Common.Capa, null, warnings); // R3

                if (btrId.IsNull)
                {
                    BlockTableRecord? existing = FindBlock(db, tr, def.Nombre);
                    if (existing == null)
                    {
                        throw ToolException.BlockNotFound(def.Nombre);
                    }

                    RequireInsertable(existing, p.Nombre);
                    btrId = existing.ObjectId;
                }

                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);

                // Unidades (§5D.5): las del archivo si se importó; si no, BlockTableRecord.Units.
                int blockUnits = def.FromLibrary ? fileUnits : (int)btr.Units;
                double factor = BlockInsertScale.ReportedFactor(blockUnits, ins, out bool noFactor);
                if (noFactor)
                {
                    warnings.Add(BlockInsertScale.NoFactorWarning(blockUnits, ins));
                }

                (double[] applied, bool factorApplied) = BlockInsertScale.Apply(p.Escala, noFactor ? null : factor, p.EscalaUnidades);
                if (factorApplied)
                {
                    warnings.Add(BlockInsertScale.AppliedWarning(factor, blockUnits, ins, applied));
                }

                // ATTDEF no constantes en el orden de la definición; las etiquetas del cliente deben existir todas
                // (ya comprobado en PreValidate; se repite como defensa).
                List<AttributeDefinition> attDefs = CollectAttributeDefinitions(btr, tr);
                Dictionary<string, string> values = RequireKnownTags(p.Atributos, TagsOf(attDefs));

                var space = (BlockTableRecord)tr.GetObject(resolved.Btr.ObjectId, OpenMode.ForWrite);
                var br = new BlockReference(AcadUtil.ToPoint3d(p.Posicion), btrId);
                DrawWrite.ApplyByLayer(br, db, layer.Id);
                br.ScaleFactors = new Scale3d(applied[0], applied[1], applied[2]);
                br.Rotation = Normalize360(p.RotacionGrados) * Math.PI / 180.0;
                string handle = DrawWrite.Append(space, tr, br);   // el INSERT ya está en el espacio antes de sus atributos

                var finalValues = new Dictionary<string, object?>(StringComparer.Ordinal);
                var attHandles = new List<string>(attDefs.Count);
                foreach (AttributeDefinition ad in attDefs)
                {
                    var ar = new AttributeReference();
                    ar.SetAttributeFromBlock(ad, br.BlockTransform);   // posición, altura, estilo y capa del ATTDEF
                    bool given = values.TryGetValue(ad.Tag, out string? value);
                    bool multiline = ad.IsMTextAttributeDefinition;
                    if (given && !multiline)
                    {
                        // Atributo de una línea: los saltos se sustituyen por espacios (como texto) y se advierte.
                        string single = TextEscape.CollapseNewlines(value!, out bool hadNewlines);
                        if (hadNewlines)
                        {
                            warnings.Add("atributos." + ad.Tag + ": es de una sola línea; los saltos de línea se sustituyeron por espacios.");
                        }

                        ar.TextString = TextEscape.EscapeDbText(single);   // R7
                    }
                    else if (given)
                    {
                        SetMultilineValue(ar, ad.Tag, value!, warnings);
                    }

                    br.AttributeCollection.AppendAttribute(ar);
                    tr.AddNewlyCreatedDBObject(ar, true);

                    if (given && !multiline && ar.Justify != AttachmentPoint.BaseLeft)
                    {
                        try
                        {
                            // Atributo justificado: recolocar según su punto de alineación tras cambiar el texto.
                            ar.AdjustAlignment(db);
                        }
                        catch (AcRuntimeException ex)
                        {
                            PluginLog.Warn("insertar_bloque: AdjustAlignment de '" + ad.Tag + "' falló (" + ex.ErrorStatus + ")");
                        }
                    }

                    finalValues[ad.Tag] = VisibleValue(ar);
                    attHandles.Add(AcadUtil.HandleToString(ar.Handle));
                }

                if (p.PropiedadesDinamicas != null)
                {
                    try
                    {
                        DynamicBlockSupport.Apply(br, tr, btr.Name, p.PropiedadesDinamicas, warnings);
                    }
                    catch (ToolException ex) when (def.FromLibrary)
                    {
                        // Riesgo residual de §5G.6: Database.Insert no se revierte con el Abort de la transacción.
                        PluginLog.Warn("insertar_bloque: propiedades_dinamicas rechazadas tras importar '" + def.Nombre +
                            "' de la biblioteca; la definición podría quedar en el dibujo. " + ex.Message);
                        throw;
                    }
                }

                BBox2? box = AcadUtil.TryBBox2(br);
                var entity = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["handle"] = handle,
                    ["bloque"] = btr.Name,
                    ["posicion"] = AcadUtil.ToArray(br.Position),
                    ["escala"] = new[] { br.ScaleFactors.X, br.ScaleFactors.Y, br.ScaleFactors.Z },
                    ["rotacion"] = AcadUtil.ToDegrees360(br.Rotation),
                    ["n_atributos"] = attHandles.Count,
                    ["bbox"] = box?.ToArray2D(),
                };

                Dictionary<string, object?> result = DrawWrite.Response(pid, new List<string> { handle }, layer, resolved.Name, ins, warnings);
                result["entidad"] = entity;
                result["atributos"] = finalValues;
                result["handles_atributos"] = attHandles;
                result["importado_de_biblioteca"] = def.FromLibrary;
                if (def.FromLibrary)
                {
                    result["archivo"] = def.Archivo;
                    result["capas_importadas"] = importedLayers ?? new List<string>();
                }

                result["dinamico"] = DynamicBlockSupport.IsDynamic(br, tr);
                result["propiedades_dinamicas"] = DynamicPropertyRules.ToJson(DynamicBlockSupport.Read(br, tr));   // tras aplicar
                result["unidades_bloque"] = blockUnits;
                result["factor_unidades"] = factor;
                result["escala_aplicada"] = applied;
                result["escala_unidades"] = p.EscalaUnidades;
                result["origen_texto"] = "contenido_del_plano";

                tr.Commit();
                return result;
            }
        }

        private static List<AttributeDefinition> CollectAttributeDefinitions(BlockTableRecord btr, Transaction tr)
        {
            var list = new List<AttributeDefinition>();
            foreach (ObjectId id in btr)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                if (tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant)
                {
                    list.Add(ad);
                }
            }

            return list;
        }

        /// <summary>
        /// Valor del cliente por TAG del ATTDEF (sin mayúsculas). Etiqueta que el bloque no define (o constante) →
        /// INVALID_PARAMS "atributos: etiqueta desconocida '&lt;TAG&gt;' (etiquetas del bloque: A, B, …)" con data.etiquetas.
        /// </summary>
        /// <remarks>Lógica pura en <see cref="AttributeTags.Match"/> (con pruebas en GeometryCore).</remarks>
        private static Dictionary<string, string> RequireKnownTags(List<KeyValuePair<string, string>> given, List<string> blockTags)
        {
            AttributeTags.Result r = AttributeTags.Match(given, blockTags);
            if (!r.Ok)
            {
                throw ToolException.InvalidParams(r.Error!, new Dictionary<string, object?> { ["etiquetas"] = r.Etiquetas });
            }

            return r.Values!;
        }

        private static List<string> TagsOf(List<AttributeDefinition> attDefs)
        {
            var tags = new List<string>(attDefs.Count);
            foreach (AttributeDefinition ad in attDefs)
            {
                tags.Add(ad.Tag);
            }

            return tags;
        }

        /// <summary>
        /// Atributo multilínea: se conserva el MTEXT del ATTDEF y se sustituye su contenido. Formato "número de"
        /// (PLANO_N) → solo el número (<see cref="PlanoNFormat"/>); si no, el valor literal escapado (R7).
        /// </summary>
        private static void SetMultilineValue(AttributeReference ar, string tag, string value, List<string> warnings)
        {
            using (MText m = ar.MTextAttribute)
            {
                string contents;
                if (value.Length == 0)
                {
                    contents = "";
                }
                else if (PlanoNFormat.TrySubstitute(m.Contents ?? "", PlanoNFormat.NormalizeNumber(value), out string substituted))
                {
                    contents = substituted;
                }
                else
                {
                    contents = MTextEscape.Escape(value);
                    if (string.Equals(tag, PlanoNFormat.Tag, StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add(PlanoNFormat.NotRecognizedWarning);
                    }
                }

                m.Contents = contents;
                ar.MTextAttribute = m;
                ar.UpdateMTextAttribute();
            }
        }

        /// <summary>Valor visible: MText.Text para multilínea; TextString para una línea.</summary>
        private static string VisibleValue(AttributeReference ar)
        {
            if (ar.IsMTextAttribute)
            {
                try
                {
                    using (MText m = ar.MTextAttribute)
                    {
                        return m.Text ?? "";
                    }
                }
                catch (AcRuntimeException)
                {
                    return ar.TextString ?? "";
                }
            }

            return ar.TextString ?? "";
        }

        private static double Normalize360(double grados)
        {
            double r = grados % 360.0;
            if (r < 0)
            {
                r += 360.0;
            }

            return r >= 360.0 ? 0.0 : r;
        }
    }
}
