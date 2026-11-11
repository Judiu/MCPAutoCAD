using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
using ImportRules = MCPAutoCAD.GeometryCore.Draw.ImportGeometryRules;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// importar_geometria (fallo 32, "DXF first"). Hilo principal; requiere documento. Lee el DXF en una base lateral
    /// (<c>new Database(false, true)</c> + <c>DxfIn</c>), cuenta por tipo y capa y, con confirmar:true, clona las entidades
    /// importables del espacio modelo al dibujo activo con <c>WblockCloneObjects</c> (DuplicateRecordCloning.Ignore: las capas
    /// existentes conservan su definición; las que faltan llegan con el color y tipo de línea del DXF) dentro de UN CTRL+Z.
    /// Flujo: Parse (sin AutoCAD) → RequireDocument → R2 → archivo (existencia y tamaño) → lectura lateral → Prepare (inventario,
    /// plan, tope de entidades, unidades, capas bloqueadas) → sin confirmar: solo informe (R4 incluida, sin escribir) →
    /// con confirmar: R4 (lanza si cae fuera) → UndoGroup.Run con UNA escritura. La base lateral se descarta siempre y nunca
    /// se guarda; el DXF no se modifica. Riesgo aceptado: la ruta es una entrada del cliente; solo se valida por forma
    /// (unidad local, .dxf, sin UNC, '..' ni comodines), no por carpetas permitidas.
    /// </summary>
    internal sealed class ImportarGeometriaHandler : IToolHandler
    {
        /// <summary>Espera del hilo principal: el parseo de ~25.000 entidades y la escritura pueden superar el plazo normal.</summary>
        private const int TiempoMs = 120000;

        private const string OrigenTexto = "contenido_del_plano";
        private const string EspacioModelo = SpaceResolver.ModelName;

        private static readonly string[] Allowed = { "ruta", "filtrar_capas", "desde", "devolver_handles", "confirmar", "forzar", "aceptar_sin_unidades" };

        public string Name => "importar_geometria";

        public string Description =>
            "ESCRIBE en el dibujo: importa como entidades nativas del espacio modelo las de un DXF local (LINE, LWPOLYLINE, POLYLINE 2D, "
            + "CIRCLE, ARC, ELLIPSE, SPLINE, POINT, TEXT, MTEXT, HATCH, SOLID e INSERT con su definición de bloque y atributos). "
            + "Sin confirmar:true solo previsualiza y NO escribe nada (conteos por tipo y capa, capas y bloques nuevos, extensión, "
            + "unidades y colocación). Con confirmar:true importa en un solo paso de deshacer (CTRL+Z). Máximo 10000 entidades por "
            + "llamada: para más, filtrar_capas o páginas con desde. Cotas, 3D, xrefs e imágenes se omiten y se cuentan; el DXF no "
            + "se modifica. Una llamada cada vez: otra simultánea devuelve BUSY sin escribir.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public int MainThreadTimeoutMs => TiempoMs;

        /// <summary>Un timeout con confirmar:true no la revierte: puede quedar una importación parcial (regla 7).</summary>
        public string TimeoutHint(JsonElement parameters) =>
            "Si confirmar:true agotó la espera, la escritura puede completarse después de la espera: compruebe con consultar_entidades antes de repetir para no duplicar entidades.";

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""ruta"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1000, ""description"": ""Ruta absoluta en una unidad, local o asignada (p. ej. C:\\... o Z:\\...), de un archivo .dxf. No se admiten rutas UNC (\\\\servidor\\...), '..' ni comodines. Máximo 200 MB. Solo se lee: nunca se modifica."" },
    ""filtrar_capas"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 }, ""description"": ""Nombres de capa del DXF a importar (sin distinguir mayúsculas). Sin este parámetro se importan todas las capas. Máximo 10000 entidades por llamada: para archivos grandes, importe por capas o por páginas (desde)."" },
    ""desde"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""Paginación para archivos de más de 10000 entidades seleccionadas: índice (0, 10000, 20000…) de la primera entidad a importar, en el orden del espacio modelo del DXF y tras filtrar_capas. Importa hasta 10000 y devuelve siguiente_desde (null en la última página). Haga una llamada después de otra; sin desde, más de 10000 se rechaza."" },
    ""devolver_handles"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = con confirmar:true devuelve en 'handles' TODOS los handles creados (en el orden del DXF), no solo la muestra de 50. Respuesta grande: úselo solo si va a editar o borrar esas entidades."" },
    ""confirmar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""false (defecto) = SOLO PREVISUALIZA: no escribe nada, no crea capas ni deshacer. true = importa las entidades al espacio modelo en un solo paso de deshacer."" },
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @"
  },
  ""required"": [""ruta""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""confirmar"": { ""type"": ""boolean"" },
    ""escrito"": { ""type"": ""boolean"" },
    ""archivo"": { ""type"": ""string"" },
    ""espacio"": { ""type"": ""string"" },
    ""n_entidades_archivo"": { ""type"": ""integer"" },
    ""n"": { ""type"": ""integer"", ""description"": ""Entidades de esta página (preview) o creadas (confirmar). por_tipo y por_capa describen la selección completa del filtro."" },
    ""por_tipo"": { ""type"": ""object"" },
    ""por_capa"": { ""type"": ""object"" },
    ""creadas"": { ""type"": ""object"", ""description"": ""Solo con confirmar:true: conteo creado por tipo."" },
    ""omitidas"": { ""type"": ""object"" },
    ""excluidas_por_capa"": { ""type"": ""integer"" },
    ""capas_nuevas"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""capas_existentes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""capas_creadas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""filtro_sin_coincidencia"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""bbox"": { ""type"": [""array"", ""null""] },
    ""colocacion"": { ""type"": ""object"" },
    ""handles_muestra"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""handles_total"": { ""type"": ""integer"" },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Solo con devolver_handles:true y confirmar:true: todos los handles creados."" },
    ""pagina"": { ""type"": ""object"", ""description"": ""desde, cantidad y total de la selección; siguiente_desde = desde de la próxima llamada (null si es la última)."" },
    ""siguiente_desde"": { ""type"": [""integer"", ""null""] },
    ""lotes"": { ""type"": ""integer"", ""description"": ""Clonados internos (de hasta 2000 entidades) usados en la escritura."" },
    ""incompleta"": { ""type"": ""boolean"", ""description"": ""true = no se crearon todas las entidades seleccionadas aun tras reintentar (ver faltantes_por_tipo y advertencias)."" },
    ""faltantes_por_tipo"": { ""type"": ""object"" },
    ""bloques_nuevos"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Definiciones de bloque (de los INSERT importados) que no existen en el dibujo."" },
    ""bloques_existentes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Bloques que ya existen en el dibujo: se usa la definición del dibujo, no la del DXF."" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""insunits_archivo"": { ""type"": ""integer"" },
    ""unidades_archivo"": { ""type"": ""string"" },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" },
    ""n_papel_ignoradas"": { ""type"": ""integer"", ""description"": ""Entidades del espacio papel del DXF, que no se importan."" },
    ""unidades_compatibles"": { ""type"": ""boolean"", ""description"": ""false = el DXF y el dibujo declaran unidades distintas. La previsualización lo informa aquí; con confirmar:true se rechaza."" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""confirmar"", ""escrito"", ""archivo"", ""n"", ""origen_texto"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        /// <summary>Parámetros ya validados (sin tocar AutoCAD).</summary>
        private sealed class ImportParams
        {
            public ImportParams(string ruta, HashSet<string>? filtro, bool confirmar, bool forzar, bool aceptarSinUnidades, int? desde, bool devolverHandles)
            {
                Desde = desde;
                DevolverHandles = devolverHandles;
                Ruta = ruta;
                Filtro = filtro;
                Confirmar = confirmar;
                Forzar = forzar;
                AceptarSinUnidades = aceptarSinUnidades;
            }

            public string Ruta { get; }

            /// <summary>null = todas las capas.</summary>
            public HashSet<string>? Filtro { get; }

            public bool Confirmar { get; }
            public bool Forzar { get; }
            public bool AceptarSinUnidades { get; }

            /// <summary>null = sin paginar (más de MaxEntities se rechaza).</summary>
            public int? Desde { get; }

            public bool DevolverHandles { get; }
        }

        private static ImportParams Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string ruta = ParamReader.RequiredText(p, "ruta", 1, 1000);
            string? errorRuta = ImportRules.ValidatePath(ruta);
            if (errorRuta != null)
            {
                throw ToolException.InvalidParams(errorRuta);
            }

            List<string>? capas = ParamReader.OptionalStringArray(p, "filtrar_capas", 1, ImportRules.MaxFilterLayers, 1);
            HashSet<string>? filtro = ImportRules.NormalizeFilter(capas, out string? errorFiltro);
            if (errorFiltro != null)
            {
                throw ToolException.InvalidParams(errorFiltro);
            }

            bool confirmar = ParamReader.OptionalBool(p, "confirmar") ?? false;
            bool forzar = ParamReader.OptionalBool(p, "forzar") ?? false;
            bool aceptar = ParamReader.OptionalBool(p, "aceptar_sin_unidades") ?? false;
            int? desde = ParamReader.OptionalInt(p, "desde", 0, int.MaxValue);
            bool devolver = ParamReader.OptionalBool(p, "devolver_handles") ?? false;
            return new ImportParams(ruta, filtro, confirmar, forzar, aceptar, desde, devolver);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            ImportParams p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;

            List<string> advertencias = AcadUtil.UnitWarnings(db);
            int ins = DrawWrite.CheckUnits(db, p.AceptarSinUnidades, advertencias);   // R2 (también en la previsualización)

            if (!File.Exists(p.Ruta))
            {
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "El archivo '" + p.Ruta + "' no existe.",
                    new Dictionary<string, object?> { ["archivo"] = p.Ruta });
            }

            long bytes;
            try
            {
                bytes = new FileInfo(p.Ruta).Length;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw ToolException.InvalidParams("ruta: no se pudo leer el archivo (" + ex.Message + ")");
            }

            string? errorTamano = ImportRules.ValidateSize(bytes);
            if (errorTamano != null)
            {
                throw ToolException.InvalidParams(errorTamano);
            }

            Database side = ReadDxf(p.Ruta, ctx.Pid);
            bool pendiente = false;
            try
            {
                Preparacion prep = Prepare(db, side, p, ins, advertencias);

                if (!p.Confirmar)
                {
                    return Preview(db, p, prep, advertencias, ctx.Pid);
                }

                if (prep.Seleccion.Count == 0)
                {
                    throw ToolException.InvalidParams("no hay entidades importables que pasen el filtro: nada que importar");
                }

                // R4 sobre la caja real de lo que se importa; lanza INVALID_PARAMS sin escribir si cae fuera (salvo forzar).
                PlacementCheck.Run(db, EspacioModelo, prep.Cajas, p.Forzar, "entidades", advertencias);
                object resultado = UndoGroup.Run(doc, d =>
                {
                    try
                    {
                        return Write(d, p, prep, side, advertencias, ctx.Pid);
                    }
                    finally
                    {
                        DisposeSide(side);   // Dispose SIEMPRE; la base lateral nunca se guarda.
                    }
                }, () => DisposeSide(side));   // re-auditoría: también si la escritura no llega a ejecutarse
                pendiente = resultado is Task { IsCompleted: false };
                return resultado;
            }
            finally
            {
                if (!pendiente)
                {
                    DisposeSide(side);
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

        // ---- lectura del archivo ----

        /// <summary>DxfIn sobre una base lateral nueva. El registro de DxfIn va a %LOCALAPPDATA%\MCPAutoCAD\tmp y se borra si todo fue bien.</summary>
        private static Database ReadDxf(string ruta, int pid)
        {
            string carpeta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCPAutoCAD", "tmp");
            Directory.CreateDirectory(carpeta);
            PurgarRegistrosViejos(carpeta);
            string registro = Path.Combine(carpeta, "importar_" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N") + ".log");

            var side = new Database(false, true);
            try
            {
                side.DxfIn(ruta, registro);
            }
            catch (AcRuntimeException ex)
            {
                side.Dispose();
                throw ReadFailed(ruta, "DxfIn: " + ex.ErrorStatus + ": " + ex.Message, registro);
            }
            catch (IOException ex)
            {
                side.Dispose();
                throw ReadFailed(ruta, "DxfIn: " + ex.Message, registro);
            }
            catch
            {
                side.Dispose();
                throw;
            }

            try
            {
                File.Delete(registro);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Un registro sobrante en tmp no impide importar.
            }

            return side;
        }

        /// <summary>
        /// Un DXF que AutoCAD no puede leer es un error del archivo que pasa el cliente: INVALID_PARAMS, no INTERNAL. Fallo 41:
        /// si el DXF tiene nombres de capa no válidos ('|', '&lt;'…), el mensaje los nombra.
        /// </summary>
        private static ToolException ReadFailed(string ruta, string detalle, string registro)
        {
            var datos = new Dictionary<string, object?> { ["detalle"] = detalle, ["archivo"] = ruta, ["registro"] = registro };
            List<(string Nombre, char Caracter)> invalidas = BuscarCapasInvalidas(ruta);
            if (invalidas.Count > 0)
            {
                datos["capas_invalidas"] = invalidas.Select(c => c.Nombre).ToList();
                return ToolException.InvalidParams(ImportRules.MensajeCapasInvalidas(Path.GetFileName(ruta), invalidas), datos);
            }

            return ToolException.InvalidParams(
                "No se pudo leer el DXF '" + Path.GetFileName(ruta) + "': no parece un DXF válido o está dañado.", datos);
        }

        /// <summary>Lee el DXF como texto (solo tras un fallo de DxfIn) buscando nombres de capa no válidos. Best effort.</summary>
        private static List<(string Nombre, char Caracter)> BuscarCapasInvalidas(string ruta)
        {
            try
            {
                return ImportRules.CapasInvalidasEnDxf(File.ReadLines(ruta));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is OutOfMemoryException)
            {
                PluginLog.Warn("importar_geometria: no se pudo revisar los nombres de capa del DXF (" + ex.Message + ").");
                return new List<(string, char)>();
            }
        }

        /// <summary>Borra los registros de DxfIn (importar_*.log) de más de un día que quedaron de sesiones anteriores. Best effort.</summary>
        private static void PurgarRegistrosViejos(string carpeta)
        {
            try
            {
                DateTime limite = DateTime.UtcNow.AddDays(-1);
                foreach (string archivo in Directory.GetFiles(carpeta, "importar_*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(archivo) < limite)
                        {
                            File.Delete(archivo);
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // Un registro en uso o protegido no impide importar.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                PluginLog.Warn("importar_geometria: no se pudo revisar la carpeta de registros (" + ex.Message + ").");
            }
        }

        /// <summary>Tipo DXF normalizado. POLYLINE = 2D (heavy); la 3D se distingue para omitirla.</summary>
        private static string TipoDe(Entity e)
        {
            switch (e)
            {
                case Polyline:
                    return "LWPOLYLINE";
                case Polyline2d:
                    return "POLYLINE";
                case Polyline3d:
                    return "POLYLINE3D";
                case PolygonMesh:
                case PolyFaceMesh:
                    return "POLYMESH";   // malla: no es polilínea 2D y no se importa
                case Solid:
                    return "SOLID";
                default:
                    return AcadUtil.DxfName(e);
            }
        }

        private sealed class Elemento
        {
            public Elemento(ObjectId id, string tipo, string capa)
            {
                Id = id;
                Tipo = tipo;
                Capa = capa;
            }

            public ObjectId Id { get; }
            public string Tipo { get; }
            public string Capa { get; }

            /// <summary>Handle en el DXF (para informar de una entidad que no se creó).</summary>
            public string Handle { get; set; } = "";

            /// <summary>Caja 2D (solo entidades seleccionadas; null si no tiene extensión).</summary>
            public BBox2? Caja { get; set; }

            /// <summary>INSERT: nombre de la definición (la dinámica, si lo es); null en el resto.</summary>
            public string? Bloque { get; set; }

            /// <summary>Normal distinta de +Z o cota Z distinta de cero (solo entidades seleccionadas).</summary>
            public bool FueraDePlano { get; set; }
        }

        /// <summary>Inventario del modelo del DXF, color de cada capa y caja de lo importable que pasa el filtro.</summary>
        private sealed class Escaneo
        {
            public List<Elemento> Todos { get; } = new List<Elemento>();
            public BBox2? Caja { get; set; }
            public Dictionary<string, (int Aci, string Nombre)> Colores { get; } = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Estado (bloqueada, congelada, apagada) de cada capa del DXF.</summary>
            public Dictionary<string, EstadoCapa> Estados { get; } = new Dictionary<string, EstadoCapa>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Entidades del espacio papel del DXF: no se importan y se cuentan.</summary>
            public int PapelIgnoradas { get; set; }

            /// <summary>HATCH asociativos seleccionados: ids de su contorno y si se leyeron todos.</summary>
            public Dictionary<ObjectId, (bool Completo, List<ObjectId> Contorno)> Asociativos { get; } =
                new Dictionary<ObjectId, (bool Completo, List<ObjectId> Contorno)>();
        }

        private static Escaneo Scan(Database side, IReadOnlySet<string>? filtro)
        {
            var esc = new Escaneo();
            var memoXref = new Dictionary<ObjectId, bool>();
            using (Transaction tr = side.TransactionManager.StartOpenCloseTransaction())
            {
                var bt = (BlockTable)tr.GetObject(side.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(BlockTableRecord.ModelSpace))
                {
                    throw ToolException.InvalidParams("el DXF no tiene espacio modelo (sección BLOCKS ausente o dañada)");
                }

                var model = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in model)
                {
                    if (id.IsNull || id.IsErased || !(tr.GetObject(id, OpenMode.ForRead, false) is Entity e))
                    {
                        continue;
                    }

                    string tipo = TipoDe(e);
                    string? bloque = null;
                    if (e is BlockReference br)
                    {
                        // Fallo 42: un INSERT se importa con su definición; el de un xref no (se cuenta como XREF omitido).
                        ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                        if (defId.IsNull)
                        {
                            tipo = "INSERT_SIN_DEFINICION";   // INSERT dañado: se omite y se cuenta
                        }
                        else if (tr.GetObject(defId, OpenMode.ForRead) is BlockTableRecord def)
                        {
                            if (def.IsFromExternalReference || def.IsFromOverlayReference || def.IsDependent
                                || ContieneXref(tr, def.ObjectId, memoXref, new HashSet<ObjectId>()))
                            {
                                tipo = "XREF";   // también un bloque con un xref anidado: el clonado traería la referencia
                            }
                            else
                            {
                                bloque = def.Name;
                            }
                        }
                    }

                    string capa = e.Layer;
                    var elemento = new Elemento(id, tipo, capa) { Bloque = bloque };
                    esc.Todos.Add(elemento);
                    if (ImportRules.Selecciona(tipo, capa, filtro))
                    {
                        elemento.Handle = AcadUtil.HandleToString(id.Handle);
                        elemento.FueraDePlano = FueraDePlanoXY(e);
                        if (e is Hatch h && h.Associative)
                        {
                            esc.Asociativos[id] = ContornoDe(h);
                        }

                        BBox2? caja = AcadUtil.TryBBox2(e);
                        elemento.Caja = caja;
                        if (caja != null)
                        {
                            esc.Caja = esc.Caja == null ? caja : esc.Caja.Value.Union(caja.Value);
                        }
                    }
                }

                // Todos los espacios papel (*Paper_Space, *Paper_Space0…), no solo el primero.
                foreach (ObjectId btrId in bt)
                {
                    if (tr.GetObject(btrId, OpenMode.ForRead) is BlockTableRecord rec && rec.IsLayout
                        && !string.Equals(rec.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (ObjectId id in rec)
                        {
                            if (!id.IsNull && !id.IsErased)
                            {
                                esc.PapelIgnoradas++;
                            }
                        }
                    }
                }

                var capas = (LayerTable)tr.GetObject(side.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in capas)
                {
                    if (id.IsNull || id.IsErased)
                    {
                        continue;
                    }

                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    esc.Colores[ltr.Name] = (ltr.Color.IsByAci ? ltr.Color.ColorIndex : 0, ltr.Color.ColorNameForDisplay);
                    esc.Estados[ltr.Name] = new EstadoCapa(ltr.IsLocked, ltr.IsFrozen, ltr.IsDependent, ltr.IsOff);
                }

                tr.Commit();
            }

            return esc;
        }

        /// <summary>true si la definición contiene (a cualquier profundidad) un INSERT de un xref. Solo lectura.</summary>
        private static readonly Autodesk.AutoCAD.Runtime.RXClass ClaseInsert =
            Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(BlockReference));

        private static bool ContieneXref(Transaction tr, ObjectId defId, Dictionary<ObjectId, bool> memo, HashSet<ObjectId> vistos)
        {
            if (memo.TryGetValue(defId, out bool ya))
            {
                return ya;
            }

            if (defId.IsNull || !vistos.Add(defId) || !(tr.GetObject(defId, OpenMode.ForRead) is BlockTableRecord def))
            {
                return false;   // ciclo o no legible: no se memoriza (el ciclo se resuelve en la llamada que lo abrió)
            }

            bool res = false;
            foreach (ObjectId id in def)
            {
                Autodesk.AutoCAD.Runtime.RXClass? clase = id.ObjectClass;
                if (clase == null || !clase.IsDerivedFrom(ClaseInsert))
                {
                    continue;
                }

                if (tr.GetObject(id, OpenMode.ForRead, false) is BlockReference br)
                {
                    ObjectId hijo = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                    if (!hijo.IsNull && tr.GetObject(hijo, OpenMode.ForRead) is BlockTableRecord h
                        && (h.IsFromExternalReference || h.IsFromOverlayReference || h.IsDependent || ContieneXref(tr, hijo, memo, vistos)))
                    {
                        res = true;
                        break;
                    }
                }
            }

            memo[defId] = res;
            return res;
        }

        /// <summary>true si la entidad no está en el plano XY con cota cero: normal distinta de +Z o Z distinta de 0 (solo aviso).</summary>
        private static bool FueraDePlanoXY(Entity e)
        {
            Vector3d? normal = NormalDe(e);
            if (normal is Vector3d n
                && (Math.Abs(n.X) > 1e-9 || Math.Abs(n.Y) > 1e-9 || Math.Abs(n.Z - 1.0) > 1e-9))
            {
                return true;
            }

            try
            {
                Extents3d ext = e.GeometricExtents;
                return Math.Abs(ext.MinPoint.Z) > 1e-9 || Math.Abs(ext.MaxPoint.Z) > 1e-9;
            }
            catch (AcRuntimeException)
            {
                return false;   // sin extensión calculable: R4 lo avisa aparte
            }
        }

        /// <summary>Normal de las entidades que la exponen; null si el tipo no tiene normal.</summary>
        private static Vector3d? NormalDe(Entity e)
        {
            switch (e)
            {
                case Circle c: return c.Normal;
                case Arc a: return a.Normal;
                case Ellipse el: return el.Normal;
                case Hatch h: return h.Normal;
                case Solid s: return s.Normal;
                case Polyline pl: return pl.Normal;
                case Polyline2d p2: return p2.Normal;
                case DBText t: return t.Normal;
                case MText m: return m.Normal;
                case DBPoint d: return d.Normal;
                case Line l: return l.Normal;
                case BlockReference b: return b.Normal;
                default: return null;
            }
        }

        /// <summary>
        /// Contorno asociado de un HATCH asociativo: ids de sus bucles y si se leyeron todos. Un bucle sin ids no se puede
        /// verificar y cuenta como incompleto (el HATCH se importará no asociativo).
        /// </summary>
        private static (bool Completo, List<ObjectId> Contorno) ContornoDe(Hatch h)
        {
            var ids = new List<ObjectId>();
            bool completo = true;
            // Re-auditoría: HatchLoop.Curves son Curve2d (geometría), no ids; los ids del contorno salen de
            // GetAssociatedObjectIdsAt (como AssociativeHatchSync y WallEntitySupport).
            for (int i = 0; i < h.NumberOfLoops; i++)
            {
                ObjectIdCollection? asociados = null;
                try
                {
                    asociados = h.GetAssociatedObjectIdsAt(i);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    asociados = null;
                }

                if (asociados == null || asociados.Count == 0)
                {
                    completo = false;
                    continue;
                }

                foreach (ObjectId id in asociados)
                {
                    ids.Add(id);
                }
            }

            return (completo, ids);
        }

        /// <summary>Nombres de una tabla de símbolos (estilos de texto, tipos de línea) de un dibujo. Solo lectura.</summary>
        private static List<string> NombresDeTabla(Database d, ObjectId tablaId)
        {
            var nombres = new List<string>();
            using (Transaction ro = d.TransactionManager.StartOpenCloseTransaction())
            {
                var tabla = (SymbolTable)ro.GetObject(tablaId, OpenMode.ForRead);
                foreach (ObjectId id in tabla)
                {
                    if (!id.IsNull && !id.IsErased)
                    {
                        nombres.Add(((SymbolTableRecord)ro.GetObject(id, OpenMode.ForRead)).Name);
                    }
                }

                ro.Commit();
            }

            return nombres;
        }

        private sealed class EstadoCapa
        {
            public EstadoCapa(bool bloqueada, bool congelada, bool xref, bool apagada)
            {
                Bloqueada = bloqueada;
                Congelada = congelada;
                Xref = xref;
                Apagada = apagada;
            }

            public bool Bloqueada { get; }
            public bool Congelada { get; }
            public bool Xref { get; }
            public bool Apagada { get; }
        }

        /// <summary>Capas del dibujo activo con su estado (lectura, sin escribir).</summary>
        private static Dictionary<string, EstadoCapa> LeerCapasDestino(Database db)
        {
            var capas = new Dictionary<string, EstadoCapa>(StringComparer.OrdinalIgnoreCase);
            using (Transaction ro = db.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (LayerTable)ro.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    if (id.IsNull || id.IsErased)
                    {
                        continue;
                    }

                    var ltr = (LayerTableRecord)ro.GetObject(id, OpenMode.ForRead);
                    capas[ltr.Name] = new EstadoCapa(ltr.IsLocked, ltr.IsFrozen, ltr.IsDependent, ltr.IsOff);
                }

                ro.Commit();
            }

            return capas;
        }

        private sealed class Preparacion
        {
            public Escaneo Escaneo { get; set; } = new Escaneo();
            public ImportRules.ImportPlan Plan { get; set; } = null!;
            public List<Elemento> Seleccion { get; set; } = new List<Elemento>();
            public List<BBox2> Cajas { get; set; } = new List<BBox2>();
            public int InsunitsArchivo { get; set; }
            public int InsunitsDibujo { get; set; }
            public ImportRules.VeredictoUnidades Unidades { get; set; } = null!;

            /// <summary>false = unidades incompatibles (solo llega aquí en la previsualización; con confirmar se rechaza antes).</summary>
            public bool UnidadesCompatibles { get; set; } = true;

            /// <summary>HATCH asociativos cuyo contorno no entra en la importación (ids del archivo): se importan no asociativos.</summary>
            public HashSet<ObjectId> HatchSinAsociar { get; set; } = new HashSet<ObjectId>();

            /// <summary>Página de la selección que se importa (la selección completa sin desde).</summary>
            public ImportRules.Pagina Pagina { get; set; } = new ImportRules.Pagina(0, 0, null);

            /// <summary>Total seleccionado por el filtro (antes de paginar).</summary>
            public int TotalSeleccion { get; set; }

            /// <summary>Grupos de índices de <see cref="Seleccion"/> que deben clonarse en el mismo lote (HATCH asociativo + contorno).</summary>
            public List<IReadOnlyCollection<int>> Grupos { get; set; } = new List<IReadOnlyCollection<int>>();

            public List<string> BloquesNuevos { get; set; } = new List<string>();
            public List<string> BloquesExistentes { get; set; } = new List<string>();
        }

        /// <summary>Añade el aviso si lo hay (null = nada que avisar).</summary>
        private static void AddSi(List<string> lista, string? aviso)
        {
            if (aviso != null)
            {
                lista.Add(aviso);
            }
        }

        /// <summary>
        /// Todo lo que puede fallar antes de escribir, sin efectos: inventario, plan, tope, unidades y capas bloqueadas. Las
        /// advertencias de unidades y de capas apagadas se añaden a <paramref name="advertencias"/>.
        /// </summary>
        private static Preparacion Prepare(Database db, Database side, ImportParams p, int ins, List<string> advertencias)
        {
            Escaneo esc = Scan(side, p.Filtro);
            Dictionary<string, EstadoCapa> destino = LeerCapasDestino(db);
            var nombresDestino = new HashSet<string>(destino.Keys, StringComparer.OrdinalIgnoreCase);

            var entidades = esc.Todos.Select(t => new ImportRules.EntidadArchivo(t.Tipo, t.Capa));
            ImportRules.ImportPlan plan = ImportRules.Plan(entidades, p.Filtro, nombresDestino);
            ImportRules.Pagina? pagina = ImportRules.Paginar(plan.Seleccionadas, p.Desde, out string? errorPagina);
            if (pagina == null && !p.Confirmar && p.Desde == null && plan.Seleccionadas > ImportRules.MaxEntities)
            {
                // Auditoría: la previsualización sin desde no falla por el tope; describe la primera página y dice cómo seguir.
                pagina = ImportRules.Paginar(plan.Seleccionadas, 0, out errorPagina);
                advertencias.Add(ImportRules.TooManyEntities(plan.Seleccionadas) + " (esta previsualización describe la primera página).");
            }

            if (pagina == null)
            {
                throw ToolException.InvalidParams(
                    errorPagina ?? "desde: no válido",
                    new Dictionary<string, object?>
                    {
                        ["entidades"] = plan.Seleccionadas,
                        ["maximo"] = ImportRules.MaxEntities,
                        ["sugerencia"] = "filtrar_capas o desde",
                        ["por_capa"] = Ordenado(plan.PorCapa),
                    });
            }

            int insArchivo = (int)side.Insunits;
            ImportRules.VeredictoUnidades unidades = ImportRules.CompareUnits(insArchivo, ins, UnitsTable.SpanishName);
            bool unidadesCompatibles = unidades.Estado != ImportRules.EstadoUnidades.Incompatible;
            if (!unidadesCompatibles)
            {
                // Con confirmar:true se sigue rechazando; la previsualización devuelve el inventario con el motivo en advertencias.
                if (p.Confirmar)
                {
                    throw ToolException.InvalidParams(
                        unidades.Mensaje,
                        new Dictionary<string, object?> { ["insunits_archivo"] = insArchivo, ["insunits"] = ins });
                }

                advertencias.Add(unidades.Mensaje);
            }

            if (unidades.Estado == ImportRules.EstadoUnidades.Advertencia)
            {
                advertencias.Add(unidades.Mensaje);
            }

            var capasPagina = new HashSet<string>(
                esc.Todos.Where(t => ImportRules.Selecciona(t.Tipo, t.Capa, p.Filtro)).Skip(pagina.Inicio).Take(pagina.Cantidad).Select(t => t.Capa),
                StringComparer.OrdinalIgnoreCase);
            var bloqueadas = new List<(string Nombre, string Motivo)>();
            foreach (string capa in plan.CapasExistentes.Where(c => capasPagina.Contains(c)))
            {
                EstadoCapa ec = destino[capa];
                string? motivo = ImportRules.LayerBlockReason(ec.Bloqueada, ec.Congelada, ec.Xref);
                EstadoCapa? enDxf = esc.Estados.TryGetValue(capa, out EstadoCapa? x) ? x : null;
                if (motivo != null && pagina.Inicio > 0 && !ec.Xref && enDxf != null && enDxf.Bloqueada == ec.Bloqueada
                    && enDxf.Congelada == ec.Congelada)
                {
                    // Auditoría (páginas): la capa tiene en el dibujo el mismo estado que en el DXF, típicamente porque la creó una
                    // página anterior de esta importación. Se escribe igual (la base de datos lo admite) y se avisa.
                    advertencias.Add("capa '" + capa + "' (" + motivo + ") tiene en el dibujo el mismo estado que en el DXF: se importa en ella igual.");
                }
                else if (motivo != null)
                {
                    bloqueadas.Add((capa, motivo));
                }
                else if (ec.Apagada)
                {
                    advertencias.Add("capa '" + capa + "' apagada en el dibujo: las entidades importadas en ella quedan ocultas.");
                }
            }

            if (bloqueadas.Count > 0)
            {
                throw ToolException.InvalidParams(
                    ImportRules.BlockedLayersMessage(bloqueadas),
                    new Dictionary<string, object?> { ["capas"] = bloqueadas.Select(b => b.Nombre).ToList() });
            }

            foreach (string capa in plan.FiltroSinCoincidencia)
            {
                advertencias.Add("filtrar_capas: la capa '" + capa + "' no existe en el DXF.");
            }

            var seleccion = esc.Todos.Where(t => ImportRules.Selecciona(t.Tipo, t.Capa, p.Filtro))
                .Skip(pagina.Inicio).Take(pagina.Cantidad).ToList();
            var cajas = new List<BBox2>();
            BBox2? cajaPagina = null;
            foreach (Elemento el in seleccion)
            {
                if (el.Caja != null)
                {
                    cajaPagina = cajaPagina == null ? el.Caja : cajaPagina.Value.Union(el.Caja.Value);
                }
            }

            if (cajaPagina != null)
            {
                cajas.Add(cajaPagina.Value);
            }
            else if (seleccion.Count > 0)
            {
                advertencias.Add(ImportRules.AvisoSinExtension);   // ninguna entidad tiene extensión: R4 no pudo comprobar la colocación
            }

            AddSi(advertencias, ImportRules.AvisoFueraDePlanoXY(seleccion.Count(s => s.FueraDePlano)));

            var estadosNuevas = plan.CapasNuevas.Select(nombre =>
            {
                EstadoCapa? ec = esc.Estados.TryGetValue(nombre, out EstadoCapa? x) ? x : null;
                return new ImportRules.EstadoCapaNueva(nombre, ec?.Congelada ?? false, ec?.Apagada ?? false, ec?.Bloqueada ?? false);
            }).ToList();
            AddSi(advertencias, ImportRules.AvisoCapasNuevasConEstado(estadosNuevas));

            // Con DuplicateRecordCloning.Ignore el dibujo conserva su definición de estilos y tipos de línea homónimos. A partir
            // de la segunda página, lo que creó la primera "ya existe": no se avisa (sería falso).
            bool primeraPagina = pagina.Inicio == 0;
            if (primeraPagina)
            {
                AddSi(advertencias, ImportRules.AvisoDefinicionesExistentes(
                    "estilos de texto", NombresDeTabla(side, side.TextStyleTableId), NombresDeTabla(db, db.TextStyleTableId)));
                AddSi(advertencias, ImportRules.AvisoDefinicionesExistentes(
                    "tipos de línea", NombresDeTabla(side, side.LinetypeTableId), NombresDeTabla(db, db.LinetypeTableId)));
            }

            int hatches = seleccion.Count(s => s.Tipo == "HATCH");
            AddSi(advertencias, ImportRules.AvisoOrdenDeDibujoHatch(hatches));

            // Fallo 42: bloques de los INSERT. Con DuplicateRecordCloning.Ignore el dibujo conserva su definición homónima.
            // Los anónimos (*U…, *D…) siempre llegan como bloques nuevos (AutoCAD los renombra).
            HashSet<string> bloquesDibujo = new HashSet<string>(NombresDeTabla(db, db.BlockTableId), StringComparer.OrdinalIgnoreCase);
            var bloquesUsados = seleccion.Where(s => s.Bloque != null && !s.Bloque.StartsWith("*", StringComparison.Ordinal))
                .Select(s => s.Bloque!).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();
            var bloquesExistentes = bloquesUsados.Where(b => bloquesDibujo.Contains(b)).ToList();
            var bloquesNuevos = bloquesUsados.Where(b => !bloquesDibujo.Contains(b)).ToList();
            if (primeraPagina)
            {
                AddSi(advertencias, ImportRules.AvisoDefinicionesExistentes("bloques", bloquesExistentes, bloquesDibujo));
            }

            // Un HATCH asociativo cuyo contorno no entra en la importación (o en esta página) se importará no asociativo; el
            // que sí lo tiene va en el mismo lote que su contorno.
            var indice = new Dictionary<ObjectId, int>();
            for (int i = 0; i < seleccion.Count; i++)
            {
                indice[seleccion[i].Id] = i;
            }

            var sinAsociar = new HashSet<ObjectId>();
            var grupos = new List<IReadOnlyCollection<int>>();
            foreach (KeyValuePair<ObjectId, (bool Completo, List<ObjectId> Contorno)> kv in esc.Asociativos)
            {
                if (!indice.TryGetValue(kv.Key, out int iHatch))
                {
                    continue;   // fuera de esta página
                }

                if (!kv.Value.Completo || kv.Value.Contorno.Any(c => !indice.ContainsKey(c)))
                {
                    sinAsociar.Add(kv.Key);
                    continue;
                }

                var grupo = new List<int> { iHatch };
                grupo.AddRange(kv.Value.Contorno.Select(c => indice[c]));
                grupos.Add(grupo);
            }

            return new Preparacion
            {
                Escaneo = esc,
                Plan = plan,
                Seleccion = seleccion,
                Cajas = cajas,
                InsunitsArchivo = insArchivo,
                InsunitsDibujo = ins,
                Unidades = unidades,
                UnidadesCompatibles = unidadesCompatibles,
                HatchSinAsociar = sinAsociar,
                Pagina = pagina,
                TotalSeleccion = plan.Seleccionadas,
                Grupos = grupos,
                BloquesNuevos = bloquesNuevos,
                BloquesExistentes = bloquesExistentes,
            };
        }

        // ---- previsualización (sin confirmar: no escribe) ----

        private static Dictionary<string, object?> Preview(Database db, ImportParams p, Preparacion prep, List<string> advertencias, int pid)
        {
            // R4 en modo informe: sin forzar, una caja fuera se devuelve como colocación, no como error de la herramienta.
            var colocacion = new Dictionary<string, object?>(StringComparer.Ordinal);
            var avisosR4 = new List<string>();
            try
            {
                PlacementCheck.Result r = PlacementCheck.Run(db, EspacioModelo, prep.Cajas, false, "entidades", avisosR4);
                colocacion["dentro"] = r.Fuera == 0;
                if (r.Parcial)
                {
                    colocacion["parcial"] = true;
                }
            }
            catch (ToolException ex) when (ex.Code == ErrorCodes.InvalidParams)
            {
                colocacion["dentro"] = false;
                colocacion["mensaje"] = ex.Message;
            }

            advertencias.AddRange(avisosR4);
            ImportRules.ImportPlan plan = prep.Plan;
            Escaneo esc = prep.Escaneo;

            var capasNuevas = plan.CapasNuevas.Select(nombre =>
            {
                (int aci, string color) = esc.Colores.TryGetValue(nombre, out (int Aci, string Nombre) c) ? (c.Aci, c.Nombre) : (0, "");
                EstadoCapa? est = esc.Estados.TryGetValue(nombre, out EstadoCapa? e0) ? e0 : null;
                return (object)new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["nombre"] = nombre,
                    ["color_aci"] = aci,
                    ["color"] = color,
                    ["congelada"] = est?.Congelada ?? false,
                    ["apagada"] = est?.Apagada ?? false,
                    ["bloqueada"] = est?.Bloqueada ?? false,
                };
            }).ToList();

            var res = Base(p, pid, false, prep, advertencias);
            res["n"] = prep.Seleccion.Count;
            res["por_tipo"] = Ordenado(plan.PorTipo);
            res["por_capa"] = Ordenado(plan.PorCapa);
            res["omitidas"] = Ordenado(plan.Omitidas);
            res["excluidas_por_capa"] = plan.ExcluidasPorCapa;
            res["capas_nuevas"] = capasNuevas;
            res["capas_existentes"] = plan.CapasExistentes;
            res["filtro_sin_coincidencia"] = plan.FiltroSinCoincidencia;
            res["colocacion"] = colocacion;
            res["handles_muestra"] = new List<string>();
            res["handles_total"] = 0;
            res["bloques_nuevos"] = prep.BloquesNuevos;
            res["bloques_existentes"] = prep.BloquesExistentes;
            return res;
        }

        /// <summary>Campos comunes de la respuesta (preview y escritura): archivo, espacio, unidades, bbox, advertencias.</summary>
        private static Dictionary<string, object?> Base(ImportParams p, int pid, bool escrito, Preparacion prep, List<string> advertencias)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = pid,
                ["confirmar"] = p.Confirmar,
                ["escrito"] = escrito,
                ["archivo"] = p.Ruta,
                ["espacio"] = EspacioModelo,
                ["n_entidades_archivo"] = prep.Plan.Total,
                ["bbox"] = prep.Escaneo.Caja?.ToArray2D(),
                ["insunits"] = prep.InsunitsDibujo,
                ["unidades"] = UnitsTable.SpanishName(prep.InsunitsDibujo),
                ["insunits_archivo"] = prep.InsunitsArchivo,
                ["unidades_archivo"] = UnitsTable.SpanishName(prep.InsunitsArchivo),
                ["origen_texto"] = OrigenTexto,
                ["n_papel_ignoradas"] = prep.Escaneo.PapelIgnoradas,
                ["unidades_compatibles"] = prep.UnidadesCompatibles,
                ["pagina"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["desde"] = prep.Pagina.Inicio,
                    ["cantidad"] = prep.Pagina.Cantidad,
                    ["total"] = prep.TotalSeleccion,
                    ["siguiente_desde"] = prep.Pagina.Siguiente,
                },
                ["siguiente_desde"] = prep.Pagina.Siguiente,
                ["advertencias"] = advertencias,
            };
        }

        private static Dictionary<string, object?> Ordenado(IReadOnlyDictionary<string, int> dict)
        {
            var res = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in dict.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                res[kv.Key] = kv.Value;
            }

            return res;
        }

        // ---- escritura (un CTRL+Z) ----

        private static HashSet<string> LeerNombresCapas(Database db)
        {
            var nombres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction ro = db.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (LayerTable)ro.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    if (!id.IsNull && !id.IsErased)
                    {
                        nombres.Add(((LayerTableRecord)ro.GetObject(id, OpenMode.ForRead)).Name);
                    }
                }

                ro.Commit();
            }

            return nombres;
        }

        /// <summary>
        /// Clonado del espacio modelo del archivo al dibujo. El clonado va PRIMERO, sin transacción abierta en el dibujo
        /// (como Database.Insert en insertar_export). Los conteos salen del IdMapping; los handles, solo de la muestra.
        /// </summary>
        private static object Write(Document doc, ImportParams p, Preparacion prep, Database side, List<string> advertencias, int pid)
        {
            Database db = doc.Database;
            HashSet<string> capasAntes = LeerNombresCapas(db);

            ObjectId espacioId;
            using (Transaction ro = db.TransactionManager.StartOpenCloseTransaction())
            {
                var bt = (BlockTable)ro.GetObject(db.BlockTableId, OpenMode.ForRead);
                espacioId = bt[BlockTableRecord.ModelSpace];
                ro.Commit();
            }

            // Fallo 38: lotes de hasta BatchSize entidades (un WblockCloneObjects por lote), todos dentro del mismo paso de
            // deshacer. Un HATCH asociativo y su contorno van en el mismo lote para conservar la asociación.
            List<List<int>> lotes = ImportRules.Lotes(prep.Seleccion.Count, prep.Grupos, ImportRules.BatchSize);
            var clonados = new Dictionary<ObjectId, ObjectId>();
            int enModeloAntes = ContarEnModelo(db, espacioId);
            for (int k = 0; k < lotes.Count; k++)
            {
                Clonar(side, espacioId, lotes[k].Select(i => prep.Seleccion[i].Id), clonados, p, k + 1, lotes.Count);
            }

            // Fallo 36: lo que no se clonó sin excepción se reintenta una vez (en un lote aparte) antes de informar.
            // Solo si el espacio modelo creció exactamente lo que dice el mapa de clonado: si creció más, el mapa no las vio
            // y reintentar las duplicaría.
            var faltan = prep.Seleccion.Where(el => !clonados.ContainsKey(el.Id)).ToList();
            int crecimiento = faltan.Count > 0 ? ContarEnModelo(db, espacioId) - enModeloAntes : clonados.Count;
            bool reintentado = false;
            if (faltan.Count > 0 && crecimiento != clonados.Count)
            {
                PluginLog.Warn("importar_geometria: faltan " + faltan.Count + " entidades pero el modelo creció " + crecimiento
                    + " y el mapa registra " + clonados.Count + "; no se reintenta para no duplicar.");
                advertencias.Add("importar_geometria: el espacio modelo cambió en " + crecimiento + " entidades y el clonado registró "
                    + clonados.Count + ": el resultado no es fiable (n y handles pueden no cuadrar). Deshaga con un CTRL+Z en AutoCAD y repita.");
            }
            else if (faltan.Count > 0)
            {
                PluginLog.Warn("importar_geometria: " + faltan.Count + " de " + prep.Seleccion.Count
                    + " entidades no se clonaron sin excepción; se reintenta una vez (primera: " + faltan[0].Tipo + " " + faltan[0].Handle + ").");
                foreach (List<int> lote in ImportRules.Lotes(faltan.Count, null, ImportRules.BatchSize))
                {
                    Clonar(side, espacioId, lote.Select(i => faltan[i].Id), clonados, p, 0, 0);
                }

                // Un HATCH asociativo reintentado, o cuyo contorno se reintentó, quedó clonado en otra llamada que su contorno
                // (ids colgantes): se desasocia.
                var idsFaltan = new HashSet<ObjectId>(faltan.Select(f => f.Id));
                foreach (IReadOnlyCollection<int> grupo in prep.Grupos)
                {
                    if (grupo.Any(i => idsFaltan.Contains(prep.Seleccion[i].Id)))
                    {
                        prep.HatchSinAsociar.Add(prep.Seleccion[grupo.First()].Id);   // el primero del grupo es el HATCH
                    }
                }

                foreach (Elemento el in faltan.Where(f => prep.Escaneo.Asociativos.ContainsKey(f.Id)))
                {
                    prep.HatchSinAsociar.Add(el.Id);
                }

                reintentado = true;
            }

            // A4: los HATCH cuyo contorno no se importó quedan no asociativos (relleno fijo), en el mismo paso de deshacer.
            int noAsociativos = 0;
            if (prep.HatchSinAsociar.Count > 0)
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId origen in prep.HatchSinAsociar)
                    {
                        if (clonados.TryGetValue(origen, out ObjectId hatchNuevo))
                        {
                            // Como HatchHandler: desasociar y quitar los objetos asociados (si no, quedan reactores
                            // huérfanos en las curvas clonadas). Un fallo aquí no revierte la importación: se avisa.
                            try
                            {
                                var hn = (Hatch)tr.GetObject(hatchNuevo, OpenMode.ForWrite);
                                bool eraAsociativo = hn.Associative;
                                hn.Associative = false;
                                if (hn.GetAssociatedObjectIds().Count > 0)
                                {
                                    hn.RemoveAssociatedObjectIds();
                                }

                                if (eraAsociativo)
                                {
                                    noAsociativos++;
                                }
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception ex)
                            {
                                PluginLog.Warn("importar_geometria: no se pudo desasociar un HATCH (" + ex.ErrorStatus + ").");
                                advertencias.Add("Un achurado importado no se pudo desasociar de su contorno (" + ex.ErrorStatus
                                    + "); revíselo con AUDIT.");
                            }
                        }
                    }

                    tr.Commit();
                }
            }

            AddSi(advertencias, ImportRules.AvisoHatchNoAsociativos(noAsociativos));

            var handles = new List<string>();
            var todos = new List<string>();
            var creadasPorTipo = new Dictionary<string, int>(StringComparer.Ordinal);
            var faltantesPorTipo = new Dictionary<string, int>(StringComparer.Ordinal);
            Elemento? primeraFaltante = null;
            int n = 0;
            foreach (Elemento el in prep.Seleccion)
            {
                if (!clonados.TryGetValue(el.Id, out ObjectId nuevo))
                {
                    faltantesPorTipo[el.Tipo] = faltantesPorTipo.TryGetValue(el.Tipo, out int f) ? f + 1 : 1;
                    primeraFaltante ??= el;
                    continue;
                }

                n++;
                creadasPorTipo[el.Tipo] = creadasPorTipo.TryGetValue(el.Tipo, out int c) ? c + 1 : 1;
                string h = AcadUtil.HandleToString(nuevo.Handle);
                if (handles.Count < ImportRules.MaxSampleHandles)
                {
                    handles.Add(h);
                }

                if (p.DevolverHandles)
                {
                    todos.Add(h);
                }
            }

            bool incompleta = primeraFaltante != null;
            if (primeraFaltante != null)
            {
                string aviso = ImportRules.AvisoIncompleta(
                    n, prep.Seleccion.Count, faltantesPorTipo, primeraFaltante.Tipo, primeraFaltante.Capa, primeraFaltante.Handle,
                    reintentado, p.Desde);
                PluginLog.Warn("importar_geometria: " + aviso);
                advertencias.Insert(0, aviso);
            }

            HashSet<string> capasDespues = LeerNombresCapas(db);
            var capasCreadas = capasDespues.Where(c => !capasAntes.Contains(c))
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

            Dictionary<string, object?> res = Base(p, pid, true, prep, advertencias);
            res["n"] = n;
            res["creadas"] = Ordenado(creadasPorTipo);
            res["por_tipo"] = Ordenado(prep.Plan.PorTipo);
            res["omitidas"] = Ordenado(prep.Plan.Omitidas);
            res["excluidas_por_capa"] = prep.Plan.ExcluidasPorCapa;
            res["capas_creadas"] = capasCreadas;
            res["capas_existentes"] = prep.Plan.CapasExistentes;
            res["handles_muestra"] = handles;
            res["handles_total"] = n;
            if (p.DevolverHandles)
            {
                res["handles"] = todos;
            }

            res["lotes"] = lotes.Count;
            res["incompleta"] = incompleta;
            if (incompleta)
            {
                // No invitar a pasar a la página siguiente con esta incompleta.
                res["siguiente_desde"] = null;
                if (res["pagina"] is Dictionary<string, object?> pag)
                {
                    pag["siguiente_desde"] = null;
                }
            }
            if (incompleta)
            {
                res["faltantes_por_tipo"] = Ordenado(faltantesPorTipo);
            }

            res["bloques_nuevos"] = prep.BloquesNuevos;
            res["bloques_existentes"] = prep.BloquesExistentes;
            return res;
        }

        /// <summary>Entidades no borradas del espacio modelo (sin abrirlas).</summary>
        private static int ContarEnModelo(Database db, ObjectId espacioId)
        {
            int n = 0;
            using (Transaction ro = db.TransactionManager.StartOpenCloseTransaction())
            {
                var modelo = (BlockTableRecord)ro.GetObject(espacioId, OpenMode.ForRead);
                foreach (ObjectId id in modelo)
                {
                    if (!id.IsErased)
                    {
                        n++;
                    }
                }

                ro.Commit();
            }

            return n;
        }

        /// <summary>
        /// Un WblockCloneObjects de un lote al espacio modelo del dibujo (sin transacción abierta en el dibujo, como
        /// Database.Insert en insertar_export) y anota en <paramref name="clonados"/> origen → copia. Una excepción corta toda
        /// la importación: UndoGroup revierte lo escrito. <paramref name="lote"/> 0 = reintento.
        /// </summary>
        private static void Clonar(
            Database side, ObjectId espacioId, IEnumerable<ObjectId> origenes, Dictionary<ObjectId, ObjectId> clonados, ImportParams p, int lote, int lotes)
        {
            var ids = new ObjectIdCollection();
            var seleccionados = new HashSet<ObjectId>();
            foreach (ObjectId id in origenes)
            {
                ids.Add(id);
                seleccionados.Add(id);
            }

            if (ids.Count == 0)
            {
                return;
            }

            using var mapa = new IdMapping();
            try
            {
                side.WblockCloneObjects(ids, espacioId, mapa, DuplicateRecordCloning.Ignore, false);
            }
            catch (AcRuntimeException ex)
            {
                string cual = lote == 0 ? "reintento" : "lote " + lote + " de " + lotes;
                throw new ToolException(
                    ErrorCodes.Internal,
                    "No se pudo importar el DXF: " + Path.GetFileName(p.Ruta) + " (" + cual + ").",
                    new Dictionary<string, object?>
                    {
                        ["detalle"] = "WblockCloneObjects (" + cual + "): " + ex.ErrorStatus + ": " + ex.Message,
                        ["archivo"] = p.Ruta,
                        ["aviso"] = "se intentó revertir la importación con el deshacer del paso (solo funciona con UNDOCTL completo); compruebe con consultar_entidades antes de reintentar. No use CTRL+Z.",
                    });
            }

            foreach (IdPair par in mapa)
            {
                if (par.IsCloned && seleccionados.Contains(par.Key) && !par.Value.IsNull)
                {
                    clonados[par.Key] = par.Value;
                }
            }
        }
    }
}
