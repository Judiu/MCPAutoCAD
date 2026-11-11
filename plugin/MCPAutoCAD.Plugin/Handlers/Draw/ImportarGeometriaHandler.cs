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

        private static readonly string[] Allowed = { "ruta", "filtrar_capas", "confirmar", "forzar", "aceptar_sin_unidades" };

        public string Name => "importar_geometria";

        public string Description =>
            "ESCRIBE en el dibujo: importa como entidades nativas del espacio modelo las de un DXF local (LINE, LWPOLYLINE, POLYLINE 2D, "
            + "CIRCLE, ARC, ELLIPSE, SPLINE, POINT, TEXT, MTEXT, HATCH, SOLID). Sin confirmar:true solo previsualiza y NO escribe nada "
            + "(conteos por tipo y capa, capas nuevas, extensión, unidades y colocación). Con confirmar:true importa todo en un solo "
            + "paso de deshacer (CTRL+Z). Bloques, cotas, 3D, xrefs e imágenes se omiten y se cuentan; el DXF no se modifica.";

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
    ""filtrar_capas"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 }, ""description"": ""Nombres de capa del DXF a importar (sin distinguir mayúsculas). Sin este parámetro se importan todas las capas. Máximo 100000 entidades por llamada: para archivos grandes, importe por capas."" },
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
    ""n"": { ""type"": ""integer"", ""description"": ""Entidades importables que pasan el filtro (preview) o creadas (confirmar)."" },
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
            public ImportParams(string ruta, HashSet<string>? filtro, bool confirmar, bool forzar, bool aceptarSinUnidades)
            {
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
            return new ImportParams(ruta, filtro, confirmar, forzar, aceptar);
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
                });
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

        /// <summary>Un DXF que AutoCAD no puede leer es un error del archivo que pasa el cliente: INVALID_PARAMS, no INTERNAL.</summary>
        private static ToolException ReadFailed(string ruta, string detalle, string registro) =>
            ToolException.InvalidParams(
                "No se pudo leer el DXF '" + Path.GetFileName(ruta) + "': no parece un DXF válido o está dañado.",
                new Dictionary<string, object?> { ["detalle"] = detalle, ["archivo"] = ruta, ["registro"] = registro });

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
                    string capa = e.Layer;
                    var elemento = new Elemento(id, tipo, capa);
                    esc.Todos.Add(elemento);
                    if (ImportRules.Selecciona(tipo, capa, filtro))
                    {
                        elemento.FueraDePlano = FueraDePlanoXY(e);
                        if (e is Hatch h && h.Associative)
                        {
                            esc.Asociativos[id] = ContornoDe(h);
                        }

                        BBox2? caja = AcadUtil.TryBBox2(e);
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
            if (plan.Excede)
            {
                throw ToolException.InvalidParams(
                    ImportRules.TooManyEntities(plan.Seleccionadas),
                    new Dictionary<string, object?>
                    {
                        ["entidades"] = plan.Seleccionadas,
                        ["maximo"] = ImportRules.MaxEntities,
                        ["sugerencia"] = "filtrar_capas",
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

            var bloqueadas = new List<(string Nombre, string Motivo)>();
            foreach (string capa in plan.CapasExistentes)
            {
                EstadoCapa ec = destino[capa];
                string? motivo = ImportRules.LayerBlockReason(ec.Bloqueada, ec.Congelada, ec.Xref);
                if (motivo != null)
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

            var seleccion = esc.Todos.Where(t => ImportRules.Selecciona(t.Tipo, t.Capa, p.Filtro)).ToList();
            var cajas = new List<BBox2>();
            if (esc.Caja != null)
            {
                cajas.Add(esc.Caja.Value);
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

            // Con DuplicateRecordCloning.Ignore el dibujo conserva su definición de estilos y tipos de línea homónimos.
            AddSi(advertencias, ImportRules.AvisoDefinicionesExistentes(
                "estilos de texto", NombresDeTabla(side, side.TextStyleTableId), NombresDeTabla(db, db.TextStyleTableId)));
            AddSi(advertencias, ImportRules.AvisoDefinicionesExistentes(
                "tipos de línea", NombresDeTabla(side, side.LinetypeTableId), NombresDeTabla(db, db.LinetypeTableId)));

            int hatches = plan.PorTipo.TryGetValue("HATCH", out int nHatch) ? nHatch : 0;
            AddSi(advertencias, ImportRules.AvisoOrdenDeDibujoHatch(hatches));

            // Un HATCH asociativo cuyo contorno no entra en la importación se importará no asociativo.
            var idsSeleccion = new HashSet<ObjectId>(seleccion.Select(s => s.Id));
            var sinAsociar = new HashSet<ObjectId>();
            foreach (KeyValuePair<ObjectId, (bool Completo, List<ObjectId> Contorno)> kv in esc.Asociativos)
            {
                if (!kv.Value.Completo || kv.Value.Contorno.Any(c => !idsSeleccion.Contains(c)))
                {
                    sinAsociar.Add(kv.Key);
                }
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
            res["n"] = plan.Seleccionadas;
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

            var ids = new ObjectIdCollection();
            var seleccionados = new HashSet<ObjectId>();
            foreach (Elemento el in prep.Seleccion)
            {
                ids.Add(el.Id);
                seleccionados.Add(el.Id);
            }

            using var mapa = new IdMapping();
            try
            {
                side.WblockCloneObjects(ids, espacioId, mapa, DuplicateRecordCloning.Ignore, false);
            }
            catch (AcRuntimeException ex)
            {
                throw new ToolException(
                    ErrorCodes.Internal,
                    "No se pudo importar el DXF: " + Path.GetFileName(p.Ruta) + ".",
                    new Dictionary<string, object?>
                    {
                        ["detalle"] = "WblockCloneObjects: " + ex.ErrorStatus + ": " + ex.Message,
                        ["archivo"] = p.Ruta,
                        ["aviso"] = "se intentó revertir la importación con el deshacer del paso (solo funciona con UNDOCTL completo); compruebe con consultar_entidades antes de reintentar. No use CTRL+Z.",
                    });
            }

            var clonados = new Dictionary<ObjectId, ObjectId>();
            foreach (IdPair par in mapa)
            {
                if (par.IsCloned && seleccionados.Contains(par.Key) && !par.Value.IsNull)
                {
                    clonados[par.Key] = par.Value;
                }
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
            var creadasPorTipo = new Dictionary<string, int>(StringComparer.Ordinal);
            int n = 0;
            foreach (Elemento el in prep.Seleccion)
            {
                if (!clonados.TryGetValue(el.Id, out ObjectId nuevo))
                {
                    continue;
                }

                n++;
                creadasPorTipo[el.Tipo] = creadasPorTipo.TryGetValue(el.Tipo, out int c) ? c + 1 : 1;
                if (handles.Count < ImportRules.MaxSampleHandles)
                {
                    handles.Add(AcadUtil.HandleToString(nuevo.Handle));
                }
            }

            if (n != prep.Seleccion.Count)
            {
                advertencias.Add("importación incompleta: se crearon " + n + " de " + prep.Seleccion.Count + " entidades seleccionadas.");
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
            return res;
        }
    }
}
