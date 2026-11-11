using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Gen;
using MCPAutoCAD.Plugin.Logging;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// insertar_export (protocol.md §5G.4). Hilo principal; requiere documento; ESCRITURA R1–R7 sin previsualización (un
    /// CTRL+Z: definición importada + capas + INSERT). Flujo: Parse → RequireDocument → R2 → PuntoRef + R4/R5 → validación
    /// completa (ruta, existencia, lectura lateral, nombre, capa destino, espacio) → UndoGroup.Run con UNA transacción.
    /// SEGURIDAD: 'ruta' es una entrada del cliente. Solo se usa en ExportRules.ValidatePath, File.Exists y
    /// ReadDwgFile de solo lectura sobre una base lateral en memoria que se descarta siempre y nunca se guarda: el filtrado
    /// de capas borra en esa base, jamás en el archivo. Aquí no hay SendStringToExecute ni comandos, y Database.Insert solo
    /// clona entidades y símbolos: ningún script ni LISP del archivo se ejecuta. Riesgo aceptado (documentado en §5G.4): si
    /// lps.json no define carpetas_export_permitidas, la ruta puede apuntar a cualquier DWG legible por el usuario de
    /// AutoCAD, también UNC (File.Exists y la lectura contactan con ese servidor) y su contenido entra en el dibujo.
    /// Database.Insert no se revierte con el Abort de la transacción (§5D.5): toda validación va antes de importar.
    /// </summary>
    internal sealed class InsertarExportHandler : IToolHandler
    {
        private const string ComoBloque = "bloque";
        private const string ComoXref = "xref";
        private const string FiltroTodas = "todas";
        private const string FiltroCype = "cype";
        private const string FolderRejected = "ruta: fuera de las carpetas permitidas de lps.json (carpetas_export_permitidas)";
        private const string NoFoldersWarning = "lps.json no define carpetas_export_permitidas: se admite cualquier ruta absoluta .dwg legible; añada la clave para limitar las carpetas.";

        private static readonly string[] Comos = { ComoBloque, ComoXref };
        private static readonly string[] Filtros = { FiltroTodas, FiltroCype };

        private static readonly string[] Allowed =
        {
            "ruta", "posicion", "rotacion", "escala", "escala_unidades", "nombre_bloque", "filtrar_capas", "capa_destino", "como",
            "espacio", "forzar", "aceptar_sin_unidades",
        };

        public string Name => "insertar_export";

        public string Description =>
            "ESCRIBE en el dibujo un export de CYPE (archivo .dwg, ruta absoluta de solo lectura: nunca se modifica el origen) como un bloque con nombre derivado del archivo, en 'posicion' (PuntoRef), aplicando el factor de unidades export→dibujo (mm→m) salvo escala_unidades 'ninguna'. Conserva las capas CYPE (ARM_*, ALZ_*, CIM_*…) y avisa de las capas ajenas que trae (filtrar_capas 'cype' las deja fuera). Un paso de deshacer; sin previsualización.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs;

        /// <summary>Un timeout de escritura no la revierte: el bloque puede quedar en el dibujo (§3, regla 7).</summary>
        public string TimeoutHint(JsonElement parameters) =>
            "Compruebe con bloques si " + ExpectedBlockName(parameters) + " ya se insertó antes de repetir; un CTRL+Z lo retira.";

        private static string ExpectedBlockName(JsonElement p)
        {
            if (p.TryGetProperty("nombre_bloque", out JsonElement nombre) && nombre.ValueKind == JsonValueKind.String)
            {
                return nombre.GetString() ?? "";
            }

            string ruta = p.TryGetProperty("ruta", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
            return ExportRules.BlockNameFor(ruta, Array.Empty<string>());
        }

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""ruta"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1000, ""description"": ""Ruta absoluta (unidad o UNC) del .dwg exportado, sin '..' ni comodines. Solo se lee: nunca se ejecuta ni se escribe. Si lps.json define carpetas_export_permitidas, debe estar dentro de una."" },
    ""posicion"": " + GenSchema.SchemaPuntoRef + @",
    ""rotacion"": { ""type"": ""number"", ""default"": 0, ""description"": ""Rotación en grados, antihoraria desde +X."" },
    ""escala"": { ""type"": ""number"", ""not"": { ""const"": 0 }, ""default"": 1, ""description"": ""Escala pedida, distinta de 0 (negativa = espejo). Con escala_unidades 'auto' se multiplica por el factor de unidades."" },
    ""escala_unidades"": { ""type"": ""string"", ""enum"": [""auto"", ""ninguna""], ""default"": ""auto"", ""description"": ""'auto' = aplicar el factor de unidades del export al dibujo (mm→m = 0,001); 'ninguna' = usar la escala tal cual."" },
    ""nombre_bloque"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 200, ""description"": ""Nombre del bloque a crear; si ya existe se rechaza. Por defecto CYPE_<nombre del archivo> (con _2, _3… si existe)."" },
    ""filtrar_capas"": { ""type"": ""string"", ""enum"": [""todas"", ""cype""], ""default"": ""todas"", ""description"": ""'cype' = deja fuera las entidades del modelo cuya capa no es 0 ni del grupo cype de lps.json (se borran en memoria, nunca en el archivo) y las cuenta; 'todas' = importa todo."" },
    ""capa_destino"": " + DrawWrite.SchemaCapa + @",
    ""como"": { ""type"": ""string"", ""enum"": [""bloque"", ""xref""], ""default"": ""bloque"", ""description"": ""Solo 'bloque' está disponible; para una referencia externa (xref) use adjuntar_xref."" },
    ""espacio"": " + DrawWrite.SchemaEspacio + @",
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @"
  },
  ""required"": [""ruta"", ""posicion""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""entidad"": { ""type"": ""object"", ""properties"": {
      ""handle"": { ""type"": ""string"" },
      ""bloque"": { ""type"": ""string"" },
      ""posicion"": { ""type"": ""array"" },
      ""escala"": { ""type"": ""array"" },
      ""rotacion"": { ""type"": ""number"" },
      ""bbox"": { ""type"": [""array"", ""null""] } } },
    ""archivo"": { ""type"": ""string"" },
    ""unidades_export"": { ""type"": ""integer"" },
    ""factor_unidades"": { ""type"": ""number"" },
    ""escala_aplicada"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""escala_unidades"": { ""type"": ""string"" },
    ""n_entidades_export"": { ""type"": ""integer"" },
    ""n_entidades_excluidas"": { ""type"": ""integer"" },
    ""capas_importadas"": { ""type"": ""object"", ""properties"": { ""cype"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }, ""otras"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } } },
    ""capas_export"": { ""type"": ""object"", ""properties"": { ""cype"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }, ""otras"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } } },
    ""bloques_importados"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""puntos_resueltos"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""capa"": { ""type"": ""string"" },
    ""capa_creada"": { ""type"": ""boolean"" },
    ""espacio"": { ""type"": ""string"" },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""handles"", ""entidad"", ""archivo"", ""unidades_export"", ""factor_unidades"", ""escala_aplicada"", ""escala_unidades"", ""n_entidades_export"", ""n_entidades_excluidas"", ""capas_importadas"", ""capas_export"", ""bloques_importados"", ""puntos_resueltos"", ""capa"", ""capa_creada"", ""espacio"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        /// <summary>Parámetros ya validados.</summary>
        private sealed class ExportParams
        {
            public ExportParams(
                string ruta, PointRef posicion, double rotacionGrados, double escala, string escalaUnidades, string? nombreBloque,
                string filtrarCapas, DrawWrite.Common common)
            {
                Ruta = ruta;
                Posicion = posicion;
                RotacionGrados = rotacionGrados;
                Escala = escala;
                EscalaUnidades = escalaUnidades;
                NombreBloque = nombreBloque;
                FiltrarCapas = filtrarCapas;
                Common = common;
            }

            public string Ruta { get; }
            public PointRef Posicion { get; }
            public double RotacionGrados { get; }
            public double Escala { get; }
            public string EscalaUnidades { get; }
            public string? NombreBloque { get; }
            public string FiltrarCapas { get; }

            /// <summary>Common.Capa = capa_destino.</summary>
            public DrawWrite.Common Common { get; }
        }

        /// <summary>Lo que la validación previa deja resuelto para la escritura.</summary>
        private sealed class Validated
        {
            public Validated(string nombre, string capaDestino, bool capaExiste, List<string> patronesCype, BBox2? caja, Database side, SideContents contenido)
            {
                Side = side;
                Contenido = contenido;
                Nombre = nombre;
                CapaDestino = capaDestino;
                CapaExiste = capaExiste;
                PatronesCype = patronesCype;
                Caja = caja;
            }

            public string Nombre { get; }
            public string CapaDestino { get; }
            public bool CapaExiste { get; }
            public List<string> PatronesCype { get; }

            /// <summary>Caja del export colocado en el dibujo; null si ninguna entidad da extensión.</summary>
            public BBox2? Caja { get; }

            /// <summary>Base lateral, la única lectura del archivo: la usa la escritura y se descarta siempre (§5G.4).</summary>
            public Database Side { get; }

            /// <summary>Inventario de <see cref="Side"/> antes de filtrar.</summary>
            public SideContents Contenido { get; }
        }

        /// <summary>Inventario de la base lateral antes de filtrar.</summary>
        internal sealed class SideContents
        {
            public int NEntidades { get; set; }

            /// <summary>Capas con entidades en el modelo o en las definiciones de bloque, en orden de aparición.</summary>
            public List<string> Capas { get; } = new List<string>();

            /// <summary>Definiciones con nombre (no anónimas, layouts ni xrefs).</summary>
            public List<string> Bloques { get; } = new List<string>();

            /// <summary>Extensión de las entidades del modelo por capa: $EXTMIN no sirve, en el export real vale 1e20.</summary>
            public Dictionary<string, BBox2> CajasPorCapa { get; } = new Dictionary<string, BBox2>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Ancho y alto de las entidades de las capas de recuadro (§5G.4).</summary>
            public List<(double ancho, double alto)> Recuadros { get; } = new List<(double ancho, double alto)>();

            /// <summary>Textos con "Escala 1:N": datos del plano, solo se les extrae el número.</summary>
            public List<string> TextosEscala { get; } = new List<string>();
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        /// <summary>Valida y convierte params. Sin AutoCAD: hilo del pipe y otra vez en el principal.</summary>
        private static ExportParams Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string ruta = ParamReader.RequiredText(p, "ruta", 1, 1000);
            PointRef posicion = GenSchema.RequiredPointRef(p, "posicion");
            double rotacion = ReadRotacion(p);
            double escala = ReadEscala(p);
            string modo = ParamReader.OptionalEnum(p, "escala_unidades", BlockInsertScale.Modos) ?? BlockInsertScale.ModoAuto;
            string? nombre = ReadNombreBloque(p);
            string filtrar = ParamReader.OptionalEnum(p, "filtrar_capas", Filtros) ?? FiltroTodas;
            string? capaDestino = ParamReader.OptionalString(p, "capa_destino", 1, 255);
            DrawLayerFactory.Plan(capaDestino, null);
            if (ParamReader.OptionalEnum(p, "como", Comos) == ComoXref)
            {
                throw ToolException.InvalidParams("como: xref no se admite en insertar_export; use adjuntar_xref para adjuntar una referencia externa");
            }

            string? espacio = ParamReader.OptionalString(p, "espacio", 1, 255);
            bool forzar = ParamReader.OptionalBool(p, "forzar") ?? false;
            bool aceptar = ParamReader.OptionalBool(p, "aceptar_sin_unidades") ?? false;
            var common = new DrawWrite.Common(capaDestino, espacio, forzar, aceptar);
            return new ExportParams(ruta, posicion, rotacion, escala, modo, nombre, filtrar, common);
        }

        private static double ReadRotacion(JsonElement p)
        {
            if (!p.TryGetProperty("rotacion", out JsonElement el))
            {
                return 0.0;
            }

            if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out double v) || !double.IsFinite(v))
            {
                throw ToolException.InvalidParams("rotacion: debe ser un número finito (grados)");
            }

            return v;
        }

        private static double ReadEscala(JsonElement p)
        {
            if (!p.TryGetProperty("escala", out JsonElement el))
            {
                return 1.0;
            }

            if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out double v) || !double.IsFinite(v) || v == 0.0)
            {
                throw ToolException.InvalidParams("escala: debe ser un número distinto de 0");
            }

            return v;
        }

        /// <summary>nombre_bloque opcional (1–200, reglas de BlockNameRules; el mensaje cita el campo real).</summary>
        private static string? ReadNombreBloque(JsonElement p)
        {
            string? nombre = ParamReader.OptionalString(p, "nombre_bloque", 1, ExportRules.MaxBlockName);
            string? error = nombre == null ? null : BlockNameRules.Validate(nombre);
            if (error != null)
            {
                throw ToolException.InvalidParams("nombre_bloque" + error.Substring("nombre".Length), new Dictionary<string, object?> { ["bloque"] = nombre });
            }

            return nombre;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            ExportParams p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;

            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = DrawWrite.CheckUnits(db, p.Common.AceptarSinUnidades, warnings);                       // R2

            List<ResolvedPoint> puntos;
            using (Transaction ro = db.TransactionManager.StartOpenCloseTransaction())
            {
                puntos = PointRefResolver.ResolveAll(db, ro, new[] { p.Posicion }, new[] { "posicion" }, warnings);
                ro.Commit();
            }

            Point3 posicion = puntos[0].Punto;

            // Validación completa ANTES de escribir (R1; Database.Insert no se revierte).
            Validated v = PreValidate(db, p, posicion, ins, warnings);

            // UndoGroup.Run puede devolver una tarea pendiente: entonces es Write quien descarta la base lateral, y hacerlo
            // aquí se la quitaría. Aquí solo se descarta si la escritura ya no va a ejecutarse.
            bool pendiente = false;
            try
            {
                // R4/R5 sobre la caja real del export colocado; el punto de inserción no dice dónde acaba un plano de 34 m.
                PlacementCheck.Run(db, p.Common.Espacio, new[] { v.Caja ?? PlacementCheck.PointBox(posicion) }, p.Common.Forzar, "entidades", warnings);
                object resultado = UndoGroup.Run(doc, d =>
                {
                    try
                    {
                        return Write(d, p, v, posicion, puntos, ctx.Pid, ins, warnings);
                    }
                    finally
                    {
                        DisposeSide(v.Side);   // §5G.4: Dispose SIEMPRE; nunca se guarda la base lateral.
                    }
                }, () => DisposeSide(v.Side));   // 0.10.3: también si la escritura no llega a ejecutarse
                pendiente = resultado is Task { IsCompleted: false };
                return resultado;
            }
            finally
            {
                if (!pendiente)
                {
                    DisposeSide(v.Side);
                }
            }
        }

        private static void DisposeSide(Database side)
        {
            if (!side.IsDisposed)
            {
                side.Dispose();
            }
        }

        /// <summary>
        /// Lectura previa sin efectos, fuera del UndoGroup, en el orden de §5G.4: ruta, existencia, lectura lateral (DWG
        /// ilegible → INTERNAL), lámina con escala_unidades 'auto', nombre del bloque, capa destino y espacio. Si la capa destino
        /// no existe en el dibujo pero el archivo trae una homónima se le aplica R3.5, porque Database.Insert la traerá con
        /// sus propiedades.
        /// </summary>
        private static Validated PreValidate(Database db, ExportParams p, Point3 posicion, int ins, List<string> warnings)
        {
            string? pathError = ExportRules.ValidatePath(p.Ruta, ReadAllowedFolders(warnings));
            if (pathError != null)
            {
                throw ToolException.InvalidParams(pathError);
            }

            if (!File.Exists(p.Ruta))
            {
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "El archivo '" + p.Ruta + "' no existe.",
                    new Dictionary<string, object?> { ["archivo"] = p.Ruta });
            }

            // La base lateral sobrevive a este método: se queda en Validated para la escritura, y solo se descarta aquí si se rechaza.
            Database side = ReadExport(p.Ruta);
            try
            {
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    SideContents contenido = ScanSide(side);
                    bool auto = string.Equals(p.EscalaUnidades, BlockInsertScale.ModoAuto, StringComparison.Ordinal);
                    if (auto)
                    {
                        RejectSheet(contenido, ins);
                    }

                    string nombre = ResolveBlockName(db, tr, p);
                    LayerPlan plan = DrawLayerFactory.Check(db, tr, p.Common.Capa, null, out bool capaExiste);   // R3
                    if (!capaExiste)
                    {
                        BlockLibrary.CheckSideLayer(side, plan.Nombre, l => DrawLayerFactory.RequireWritable(l, new List<string>()));
                    }

                    SpaceResolver.Resolve(db, tr, p.Common.Espacio);                                             // R5
                    tr.Commit();
                    List<string> patrones = ReadCypePatterns();
                    BBox2? caja = PlacedBox(side, contenido, patrones, p, posicion, ins);
                    return new Validated(nombre, plan.Nombre, capaExiste, patrones, caja, side, contenido);
                }
            }
            catch
            {
                side.Dispose();
                throw;
            }
        }

        /// <summary>§5G.4: una lámina en cm de papel con 'auto' se rechaza: el error sería de cientos de veces y en silencio.</summary>
        private static void RejectSheet(SideContents contenido, int ins)
        {
            ExportRules.SheetDetection lamina = ExportRules.DetectSheet(contenido.Recuadros, contenido.TextosEscala);
            if (!lamina.EsLamina)
            {
                return;
            }

            double factorCm = UnitsScale.Factor(ExportRules.SheetUnits, ins) ?? 0.01;
            var escalas = new List<double>(lamina.Escalas.Count);
            foreach (int n in lamina.Escalas)
            {
                escalas.Add(ExportRules.SuggestedScale(n, factorCm));
            }

            throw ToolException.InvalidParams(
                ExportRules.SheetRejection(lamina, factorCm),
                new Dictionary<string, object?>
                {
                    ["formatos"] = lamina.Formatos,
                    ["escalas"] = lamina.Escalas,
                    ["escalas_sugeridas"] = escalas,
                });
        }

        /// <summary>Caja del export colocado: la de lo que entra (filtrar_capas 'cype' deja fuera las capas ajenas), con escala y rotación.</summary>
        private static BBox2? PlacedBox(Database side, SideContents contenido, List<string> patrones, ExportParams p, Point3 posicion, int ins)
        {
            (List<string> _, List<string> otras) = ExportRules.ClassifyLayers(new List<string>(contenido.CajasPorCapa.Keys), patrones);
            BBox2? total = null;
            foreach (KeyValuePair<string, BBox2> capa in contenido.CajasPorCapa)
            {
                if (p.FiltrarCapas != FiltroCype || !otras.Contains(capa.Key, StringComparer.OrdinalIgnoreCase))
                {
                    total = total == null ? capa.Value : total.Value.Union(capa.Value);
                }
            }

            if (total == null)
            {
                return null;
            }

            (double? factor, _) = ExportRules.Factor((int)side.Insunits, ins);
            (double[] aplicada, _) = BlockInsertScale.Apply(new[] { p.Escala, p.Escala, p.Escala }, factor, p.EscalaUnidades);
            return ExportRules.PlacedBox(total.Value, (side.Insbase.X, side.Insbase.Y), aplicada, p.RotacionGrados, posicion);
        }

        /// <summary>
        /// lps.json carpetas_export_permitidas. Ausente (o lps.json no disponible) → null + advertencia de §5G.4. Presente
        /// pero sin ser un array no vacío de rutas → se rechaza como fuera de las carpetas: ValidatePath no limita con una
        /// lista vacía, y una clave mal escrita no debe abrir todas las rutas.
        /// </summary>
        internal static List<string>? ReadAllowedFolders(List<string> warnings)
        {
            if (!(LpsStandard.Root is JsonElement root)
                || root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(ExportRules.LpsKeyFolders, out JsonElement el))
            {
                warnings.Add(NoFoldersWarning);
                return null;
            }

            var folders = new List<string>();
            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in el.EnumerateArray())
                {
                    string? folder = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (string.IsNullOrWhiteSpace(folder))
                    {
                        folders.Clear();
                        break;
                    }

                    folders.Add(folder);
                }
            }

            if (folders.Count == 0)
            {
                PluginLog.Warn("insertar_export: lps.json " + ExportRules.LpsKeyFolders + " no es un array no vacío de rutas; se rechaza la ruta.");
                throw ToolException.InvalidParams(FolderRejected);
            }

            return folders;
        }

        /// <summary>lps.json grupos_externos_permitidos.cype (vacío si no está definido: ninguna capa será CYPE).</summary>
        private static List<string> ReadCypePatterns()
        {
            var patterns = new List<string>();
            if (LpsStandard.Root is JsonElement root
                && root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("grupos_externos_permitidos", out JsonElement groups)
                && groups.ValueKind == JsonValueKind.Object
                && groups.TryGetProperty(FiltroCype, out JsonElement cype)
                && cype.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in cype.EnumerateArray())
                {
                    string? pattern = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (!string.IsNullOrEmpty(pattern))
                    {
                        patterns.Add(pattern);
                    }
                }
            }

            return patterns;
        }

        /// <summary>BlockLibrary.ReadSide con un mensaje propio: un DWG ilegible aquí no es de la biblioteca.</summary>
        private static Database ReadExport(string ruta)
        {
            try
            {
                return BlockLibrary.ReadSide(ruta);
            }
            catch (ToolException ex) when (ex.Code == ErrorCodes.Internal)
            {
                throw new ToolException(ErrorCodes.Internal, "No se pudo leer el export: " + Path.GetFileName(ruta) + ".", ex.ExtraData);
            }
        }

        /// <summary>nombre_bloque dado (no puede existir) o el derivado del archivo; válido como símbolo de AutoCAD.</summary>
        private static string ResolveBlockName(Database db, Transaction tr, ExportParams p)
        {
            var existentes = new List<string>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                if (!id.IsNull && !id.IsErased)
                {
                    existentes.Add(((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
                }
            }

            string nombre;
            if (p.NombreBloque == null)
            {
                nombre = ExportRules.BlockNameFor(p.Ruta, existentes);
            }
            else
            {
                nombre = p.NombreBloque;
                if (existentes.Exists(n => string.Equals(n, p.NombreBloque, StringComparison.OrdinalIgnoreCase)))
                {
                    throw ToolException.InvalidParams(
                        "nombre_bloque: el bloque '" + nombre + "' ya existe en el dibujo",
                        new Dictionary<string, object?> { ["bloque"] = nombre });
                }
            }

            try
            {
                SymbolUtilityServices.ValidateSymbolName(nombre, false);
            }
            catch (AcRuntimeException)
            {
                throw ToolException.InvalidParams(
                    "nombre_bloque: '" + nombre + "' no es un nombre de bloque válido",
                    new Dictionary<string, object?> { ["bloque"] = nombre });
            }

            return nombre;
        }

        /// <summary>
        /// Escritura dentro de UNA transacción (R1). Replica la estructura de BlockLibrary.Import, que no sirve aquí porque
        /// hay que filtrar la base lateral entre su lectura y Database.Insert: la importación va PRIMERO, antes de abrir
        /// ninguna tabla en <c>tr</c>, y la base lateral se descarta en finally. Nunca cambia CTAB/TILEMODE/CLAYER ni la vista.
        /// </summary>
        private static object Write(
            Document doc, ExportParams p, Validated v, Point3 posicion, List<ResolvedPoint> puntos, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                HashSet<string> capasAntes;
                using (Transaction ro = db.TransactionManager.StartOpenCloseTransaction())
                {
                    capasAntes = new HashSet<string>(LayerNamesOrdered(db, ro), StringComparer.OrdinalIgnoreCase);
                    ro.Commit();
                }

                ObjectId btrId;
                Database side = v.Side;
                SideContents contenido = v.Contenido;
                int unidadesExport = (int)side.Insunits;
                (List<string> capasExportCype, List<string> capasExportOtras) = ExportRules.ClassifyLayers(contenido.Capas, v.PatronesCype);
                int excluidas = p.FiltrarCapas == FiltroCype ? EraseForeignEntities(side, p.Ruta, capasExportOtras) : 0;
                try
                {
                    // preserveSourceDatabase: true → la base lateral queda intacta (y se descarta igualmente).
                    btrId = db.Insert(v.Nombre, side, true);
                }
                catch (AcRuntimeException ex)
                {
                    throw ImportFailed(p.Ruta, "Database.Insert: " + ex.ErrorStatus + ": " + ex.Message);
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (btrId.IsNull || !bt.Has(v.Nombre))
                {
                    throw ImportFailed(p.Ruta, "Database.Insert no creó la definición '" + v.Nombre + "'");
                }

                ObjectId importedId = bt[v.Nombre];
                StampUnits(tr, importedId, unidadesExport);

                // Antes de DrawLayerFactory.Ensure: la capa destino que se cree no es una capa del export.
                var capasNuevas = new List<string>();
                foreach (string name in LayerNamesOrdered(db, tr))
                {
                    if (!capasAntes.Contains(name))
                    {
                        capasNuevas.Add(name);
                    }
                }

                (List<string> cypeNuevas, List<string> otrasNuevas) = ExportRules.ClassifyLayers(capasNuevas, v.PatronesCype);
                string? ajenas = ExportRules.ForeignLayersWarning(otrasNuevas);
                if (ajenas != null)
                {
                    warnings.Add(ajenas);
                }

                (double? factor, string? avisoFactor) = ExportRules.Factor(unidadesExport, ins);
                bool auto = string.Equals(p.EscalaUnidades, BlockInsertScale.ModoAuto, StringComparison.Ordinal);
                if (auto && avisoFactor != null)
                {
                    warnings.Add(avisoFactor);
                }
                else if (auto && factor == null)
                {
                    warnings.Add(BlockInsertScale.NoFactorWarning(unidadesExport, ins));
                }

                (double[] aplicada, _) = BlockInsertScale.Apply(new[] { p.Escala, p.Escala, p.Escala }, factor, p.EscalaUnidades);

                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, p.Common.Espacio);        // R5
                DrawLayerFactory.Result layer = DrawLayerFactory.Ensure(db, tr, p.Common.Capa, null, warnings); // R3

                var space = (BlockTableRecord)tr.GetObject(resolved.Btr.ObjectId, OpenMode.ForWrite);
                var br = new BlockReference(AcadUtil.ToPoint3d(posicion), importedId);
                DrawWrite.ApplyByLayer(br, db, layer.Id);
                br.ScaleFactors = new Scale3d(aplicada[0], aplicada[1], aplicada[2]);
                br.Rotation = Angles.ToRadians(Angles.Normalize360(p.RotacionGrados));
                string handle = DrawWrite.Append(space, tr, br);

                var puntosJson = new List<Dictionary<string, object?>>(puntos.Count);
                foreach (ResolvedPoint rp in puntos)
                {
                    puntosJson.Add(rp.ToJson());
                }

                Dictionary<string, object?> result = DrawWrite.Response(pid, new List<string> { handle }, layer, resolved.Name, ins, warnings);
                result["entidad"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["handle"] = handle,
                    ["bloque"] = v.Nombre,
                    ["posicion"] = AcadUtil.ToArray(br.Position),
                    ["escala"] = new[] { br.ScaleFactors.X, br.ScaleFactors.Y, br.ScaleFactors.Z },
                    ["rotacion"] = AcadUtil.ToDegrees360(br.Rotation),
                    ["bbox"] = (AcadUtil.TryBBox2(br) ?? v.Caja)?.ToArray2D(),
                };
                result["archivo"] = p.Ruta;
                result["unidades_export"] = unidadesExport;
                result["factor_unidades"] = auto ? factor ?? 1.0 : 1.0;   // §5G.4: con 'ninguna' no se aplicó ningún factor
                result["escala_aplicada"] = aplicada;
                result["escala_unidades"] = p.EscalaUnidades;
                result["n_entidades_export"] = contenido.NEntidades;
                result["n_entidades_excluidas"] = excluidas;
                result["capas_importadas"] = LayerGroups(cypeNuevas, otrasNuevas);
                result["capas_export"] = LayerGroups(capasExportCype, capasExportOtras);
                result["bloques_importados"] = contenido.Bloques;
                result["puntos_resueltos"] = puntosJson;
                result["origen_texto"] = "contenido_del_plano";

                tr.Commit();
                return result;
            }
        }

        /// <summary>
        /// Inventario del export (solo lectura): entidades del modelo, capas con entidades, bloques con nombre y, del modelo,
        /// la extensión por capa, los recuadros y los textos de escala.
        /// </summary>
        private static SideContents ScanSide(Database side)
        {
            var contents = new SideContents();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction tr = side.TransactionManager.StartOpenCloseTransaction())
            {
                var bt = (BlockTable)tr.GetObject(side.BlockTableId, OpenMode.ForRead);
                ObjectId modelId = bt[BlockTableRecord.ModelSpace];
                foreach (ObjectId btrId in bt)
                {
                    if (btrId.IsNull || btrId.IsErased)
                    {
                        continue;
                    }

                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    bool isModel = btrId == modelId;
                    if ((btr.IsLayout && !isModel) || btr.IsFromExternalReference || btr.IsFromOverlayReference || btr.IsDependent)
                    {
                        continue;
                    }

                    if (!isModel && !btr.IsAnonymous && !btr.Name.StartsWith("*", StringComparison.Ordinal))
                    {
                        contents.Bloques.Add(btr.Name);
                    }

                    foreach (ObjectId id in btr)
                    {
                        if (id.IsNull || id.IsErased || !(tr.GetObject(id, OpenMode.ForRead, false) is Entity e))
                        {
                            continue;
                        }

                        if (isModel)
                        {
                            contents.NEntidades++;
                            Measure(e, contents);
                        }

                        if (seen.Add(e.Layer))
                        {
                            contents.Capas.Add(e.Layer);
                        }
                    }
                }

                tr.Commit();
            }

            return contents;
        }

        private static void Measure(Entity e, SideContents contents)
        {
            BBox2? caja = AcadUtil.TryBBox2(e);
            if (caja != null)
            {
                contents.CajasPorCapa[e.Layer] = contents.CajasPorCapa.TryGetValue(e.Layer, out BBox2 previa) ? previa.Union(caja.Value) : caja.Value;
                if (ExportRules.IsSheetLayer(e.Layer))
                {
                    contents.Recuadros.Add((caja.Value.Xmax - caja.Value.Xmin, caja.Value.Ymax - caja.Value.Ymin));
                }
            }

            string? texto = e is DBText t ? t.TextString : (e as MText)?.Text;
            if (ExportRules.IsScaleText(texto))
            {
                contents.TextosEscala.Add(texto!);
            }
        }

        /// <summary>
        /// filtrar_capas 'cype': borra en la base lateral (en memoria, nunca en el archivo) las entidades del modelo en
        /// <paramref name="capasAjenas"/>. Abre forzando las capas bloqueadas del export. Devuelve cuántas borró.
        /// </summary>
        private static int EraseForeignEntities(Database side, string ruta, IReadOnlyCollection<string> capasAjenas)
        {
            var ajenas = new HashSet<string>(capasAjenas, StringComparer.OrdinalIgnoreCase);
            int n = 0;
            try
            {
                using (Transaction tr = side.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(side.BlockTableId, OpenMode.ForRead);
                    var model = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in model)
                    {
                        if (id.IsNull || id.IsErased || !(tr.GetObject(id, OpenMode.ForRead, false) is Entity e) || !ajenas.Contains(e.Layer))
                        {
                            continue;
                        }

                        tr.GetObject(id, OpenMode.ForWrite, false, true).Erase();
                        n++;
                    }

                    tr.Commit();
                }
            }
            catch (AcRuntimeException ex)
            {
                throw ImportFailed(ruta, "filtrar_capas: " + ex.ErrorStatus + ": " + ex.Message);
            }

            return n;
        }

        /// <summary>BlockTableRecord.Units = INSUNITS del export si es válido (como BlockLibrary.StampUnits, que es privado).</summary>
        private static void StampUnits(Transaction tr, ObjectId btrId, int unidades)
        {
            if (unidades == 0 || !UnitsTable.IsInRange(unidades))
            {
                return;
            }

            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            if ((int)btr.Units != unidades)
            {
                btr.UpgradeOpen();
                btr.Units = (UnitsValue)unidades;
            }
        }

        private static List<string> LayerNamesOrdered(Database db, Transaction tr)
        {
            var names = new List<string>();
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                if (!id.IsNull && !id.IsErased)
                {
                    names.Add(((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
                }
            }

            return names;
        }

        private static Dictionary<string, object?> LayerGroups(List<string> cype, List<string> otras) =>
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["cype"] = cype, ["otras"] = otras };

        private static ToolException ImportFailed(string ruta, string detalle) =>
            new ToolException(
                ErrorCodes.Internal,
                "No se pudo importar el export: " + Path.GetFileName(ruta) + ".",
                new Dictionary<string, object?> { ["detalle"] = detalle, ["archivo"] = ruta });
    }
}
