using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Imagenes;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Documento;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// insertar_imagen (fallo 24). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z). Equivalente a IMAGEATTACH:
    /// RasterImageDef (en ACAD_IMAGE_DICT, reutilizada si ya apunta al mismo archivo) + RasterImage con su marco.
    /// Flujo: Parse (sin AutoCAD) → RequireDocument → R2 → lectura del origen (existencia, tamaño, cabecera) → marco →
    /// R4 (<see cref="PlacementCheck"/>) → copia opcional junto al DWG (FUERA del grupo de deshacer) → UndoGroup.Run con UNA
    /// Transaction. Si la escritura falla, la copia se borra solo si esta llamada la creó (<see cref="Destino.Revertir"/>).
    /// SEGURIDAD: 'ruta' es entrada del cliente; solo se lee (File.Exists, ReadAllBytes) y, con copiar, se copia a
    /// &lt;carpeta del DWG&gt;\imagenes. Nunca se escribe en carpetas_solo_lectura de lps.json ni se sobrescribe un archivo.
    /// </summary>
    internal sealed class InsertarImagenHandler : IToolHandler
    {
        private const string AdvertenciaSinCopia =
            "copiar:false: el dibujo referencia la imagen por su ruta absoluta; en otro PC faltará hasta copiarla (use copiar:true para llevarla con el dibujo).";

        private static readonly string[] Own = { "ruta", "posicion", "ancho", "rotacion", "copiar" };
        private static readonly string[] Allowed = PrimitiveSupport.Concat(Own, DrawWrite.CommonParams);

        public string Name => "insertar_imagen";

        public string Description =>
            "ESCRIBE en el dibujo: inserta una imagen (.png, .jpg, .jpeg, .tif, .tiff o .bmp) como referencia de imagen (IMAGEATTACH): " +
            "'posicion' es su esquina inferior izquierda, 'ancho' su anchura en unidades del dibujo (la altura sale de la proporción " +
            "de la imagen) y 'rotacion' su giro. Con copiar:true (defecto) copia el archivo a <carpeta del DWG>\\imagenes y lo " +
            "referencia por ruta relativa, así el logo viaja con el dibujo: CREA esa carpeta junto al DWG (el dibujo debe estar " +
            "guardado y su carpeta no puede estar en carpetas_solo_lectura de lps.json); con copiar:false referencia la ruta " +
            "absoluta. Un solo paso de deshacer (la copia del archivo queda en disco).";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""ruta"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1000, ""description"": ""Ruta absoluta con unidad (p. ej. O:\\LPS\\logo.png) del archivo .png, .jpg, .jpeg, .tif, .tiff o .bmp, de hasta 50 MB. Sin rutas UNC (use la unidad asignada), sin '..' ni comodines. Debe existir."" },
    ""posicion"": " + PrimitiveSupport.PointSchema + @",
    ""ancho"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Anchura de la imagen en unidades del dibujo (> 0). La altura se calcula por la proporción de píxeles."" },
    ""rotacion"": " + PrimitiveSupport.AngleSchemaDefault0 + @",
    ""copiar"": { ""type"": ""boolean"", ""default"": true, ""description"": ""true = copia la imagen a <carpeta del DWG>\\imagenes (sin sobrescribir; reutiliza si es idéntica) y la referencia con ruta relativa; exige dibujo guardado y carpeta fuera de carpetas_solo_lectura. false = referencia la ruta absoluta de origen (faltará en otro PC)."" },
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""espacio"": " + DrawWrite.SchemaEspacio + @",
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @"
  },
  ""required"": [""ruta"", ""posicion"", ""ancho""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""capa"": { ""type"": ""string"" },
    ""capa_creada"": { ""type"": ""boolean"" },
    ""espacio"": { ""type"": ""string"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""entidad"": {
      ""type"": ""object"",
      ""properties"": {
        ""handle"": { ""type"": ""string"" },
        ""posicion"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
        ""ancho"": { ""type"": ""number"" },
        ""alto"": { ""type"": ""number"" },
        ""rotacion"": { ""type"": ""number"" },
        ""pixeles"": { ""type"": ""object"", ""properties"": { ""ancho"": { ""type"": ""integer"" }, ""alto"": { ""type"": ""integer"" } } },
        ""definicion_reutilizada"": { ""type"": ""boolean"" }
      }
    },
    ""archivo_origen"": { ""type"": ""string"" },
    ""archivo_referenciado"": { ""type"": ""string"", ""description"": ""Ruta relativa .\\imagenes\\<nombre> con copiar:true; ruta absoluta con copiar:false."" },
    ""archivo_copiado"": { ""type"": [""string"", ""null""], ""description"": ""Ruta absoluta de la copia usada (creada o reutilizada); null con copiar:false."" },
    ""reutilizado"": { ""type"": ""boolean"", ""description"": ""true si la copia ya existía con el mismo contenido (SHA-256) y no se copió de nuevo."" }
  },
  ""required"": [""pid"", ""handles"", ""capa"", ""capa_creada"", ""espacio"", ""insunits"", ""unidades"", ""advertencias"", ""entidad"", ""archivo_origen"", ""archivo_referenciado"", ""archivo_copiado"", ""reutilizado""]
}");

        /// <summary>Parámetros ya validados (sin AutoCAD).</summary>
        private sealed class Params
        {
            public Params(string ruta, Point3 posicion, double ancho, double rotacion, bool copiar, DrawWrite.Common common)
            {
                Ruta = ruta;
                Posicion = posicion;
                Ancho = ancho;
                Rotacion = rotacion;
                Copiar = copiar;
                Common = common;
            }

            public string Ruta { get; }
            public Point3 Posicion { get; }
            public double Ancho { get; }
            public double Rotacion { get; }
            public bool Copiar { get; }
            public DrawWrite.Common Common { get; }
        }

        /// <summary>Archivo de origen leído y comprobado: bytes, SHA-256 y dimensiones en píxeles.</summary>
        private sealed class Origen
        {
            public Origen(string ruta, string nombre, byte[] bytes, string sha256, int pxAncho, int pxAlto)
            {
                Ruta = ruta;
                Nombre = nombre;
                Bytes = bytes;
                Sha256 = sha256;
                PxAncho = pxAncho;
                PxAlto = pxAlto;
            }

            public string Ruta { get; }
            public string Nombre { get; }
            public byte[] Bytes { get; }
            public string Sha256 { get; }
            public int PxAncho { get; }
            public int PxAlto { get; }
        }

        /// <summary>
        /// Copia junto al DWG. <see cref="Creado"/> solo es true si ESTA llamada creó el archivo; <see cref="Revertir"/> lo borra
        /// en ese caso si la escritura no llegó a confirmarse. Un archivo reutilizado nunca se borra.
        /// </summary>
        private sealed class Destino
        {
            public Destino(string rutaCopia, string referencia, bool reutilizado)
            {
                RutaCopia = rutaCopia;
                Referencia = referencia;
                Reutilizado = reutilizado;
            }

            public string RutaCopia { get; }
            public string Referencia { get; }
            public bool Reutilizado { get; }
            public bool Creado { get; set; }
            private bool Confirmado { get; set; }

            public void Confirmar() => Confirmado = true;

            public void Revertir()
            {
                if (!Creado || Confirmado)
                {
                    return;
                }

                Creado = false;
                try
                {
                    File.Delete(RutaCopia);
                    PluginLog.Info("insertar_imagen: escritura fallida; copia borrada: " + RutaCopia);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    PluginLog.Warn("insertar_imagen: no se pudo borrar la copia " + RutaCopia + " (" + ex.GetType().Name + ").");
                }
            }
        }

        /// <summary>Todo lo necesario para escribir, calculado antes del grupo de deshacer.</summary>
        private sealed class Plan
        {
            public Plan(Params p, Origen origen, MarcoImagen marco, Destino? destino, string referencia, int pid, int ins, List<string> warnings)
            {
                P = p;
                Origen = origen;
                Marco = marco;
                Destino = destino;
                Referencia = referencia;
                Pid = pid;
                Ins = ins;
                Warnings = warnings;
            }

            public Params P { get; }
            public Origen Origen { get; }
            public MarcoImagen Marco { get; }
            public Destino? Destino { get; }
            public string Referencia { get; }
            public int Pid { get; }
            public int Ins { get; }
            public List<string> Warnings { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string ruta = ParamReader.RequiredText(p, "ruta", 1, 1000);
            string? detalle = ImagenRules.ValidarRutaOrigen(ruta);
            if (detalle != null)
            {
                throw ToolException.InvalidParams(detalle);
            }

            Point3 posicion = ParamReader.RequiredPoint(p, "posicion");
            double ancho = ParamReader.RequiredNumber(p, "ancho", 0, double.PositiveInfinity, exclusiveMin: true);
            double rotacion = PrimitiveSupport.OptionalFinite(p, "rotacion") ?? 0.0;
            bool copiar = ParamReader.OptionalBool(p, "copiar") ?? true;
            DrawWrite.Common common = DrawWrite.ReadCommon(p, null);
            return new Params(ruta, posicion, ancho, rotacion, copiar, common);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = DrawWrite.CheckUnits(db, p.Common.AceptarSinUnidades, warnings);

            // Validación completa ANTES de copiar o escribir (la copia y la escritura no se pueden deshacer igual).
            Origen origen = LeerOrigen(p.Ruta);
            MarcoImagen marco = ImagenRules.Marco(p.Ancho, p.Rotacion, origen.PxAncho, origen.PxAlto);
            (double minX, double minY, double maxX, double maxY) = marco.Caja(p.Posicion.X, p.Posicion.Y);
            PlacementCheck.Run(
                db, p.Common.Espacio, new[] { BBox2.FromCorners(minX, minY, maxX, maxY) }, p.Common.Forzar, "entidades", warnings);

            Destino? destino = null;
            string referencia = p.Ruta;
            if (p.Copiar)
            {
                destino = PrepararCopia(doc, origen);
                referencia = destino.Referencia;
            }
            else
            {
                warnings.Add(AdvertenciaSinCopia);
            }

            var plan = new Plan(p, origen, marco, destino, referencia, ctx.Pid, ins, warnings);
            object resultado;
            try
            {
                resultado = UndoGroup.Run(doc, d => Write(d, plan));
            }
            catch
            {
                destino?.Revertir();
                throw;
            }

            if (destino != null && resultado is Task task)
            {
                // Ruta de contexto de comando: la escritura puede fallar más tarde; entonces se borra la copia propia.
                task.ContinueWith(
                    t =>
                    {
                        if (t.IsFaulted || t.IsCanceled)
                        {
                            destino.Revertir();
                        }
                    },
                    TaskContinuationOptions.ExecuteSynchronously);
            }

            return resultado;
        }

        /// <summary>Lee el origen: existe, tamaño admitido, cabecera coherente con la extensión. Solo lectura.</summary>
        private static Origen LeerOrigen(string ruta)
        {
            if (!File.Exists(ruta))
            {
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "No existe la imagen '" + ruta + "'.",
                    new Dictionary<string, object?> { ["ruta"] = ruta });
            }

            string? tamano = ImagenRules.ValidarTamano(new FileInfo(ruta).Length);
            if (tamano != null)
            {
                throw ToolException.InvalidParams(tamano);
            }

            byte[] bytes = File.ReadAllBytes(ruta);
            string ext = Path.GetExtension(ruta);
            if (!ImageHeader.TryDimensiones(ext, bytes, out int pxAncho, out int pxAlto))
            {
                throw ToolException.InvalidParams(
                    "ruta: el archivo no es una imagen " + ext.ToLowerInvariant() + " válida (cabecera no reconocida o dimensiones no válidas)");
            }

            return new Origen(ruta, ImagenRules.NombreArchivo(ruta), bytes, ImagenRules.Sha256Hex(bytes), pxAncho, pxAlto);
        }

        /// <summary>
        /// Copia el origen a &lt;carpeta del DWG&gt;\imagenes (fuera del grupo de deshacer). Exige dibujo guardado y que la carpeta
        /// no esté en carpetas_solo_lectura. Nunca sobrescribe: si el nombre existe con el mismo SHA-256 se reutiliza; si no,
        /// se usa un sufijo _2, _3…
        /// </summary>
        private static Destino PrepararCopia(Document doc, Origen origen)
        {
            string? rutaDwg = DocumentoSupport.RutaDe(doc);
            if (rutaDwg == null || DocumentoSupport.SafeIntSysVar("DWGTITLED") != 1)
            {
                throw ToolException.InvalidParams(
                    "copiar: el dibujo no está guardado; guárdelo antes (guardar) o use copiar:false");
            }

            string carpetaDwg = Path.GetDirectoryName(rutaDwg)
                ?? throw ToolException.InvalidParams("copiar: no se puede determinar la carpeta del dibujo");
            string carpetaImagenes = Path.Combine(carpetaDwg, ImagenRules.CarpetaImagenes);

            List<string> soloLectura = DocumentoSupport.CarpetasSoloLectura();
            // También la carpeta del DWG (existe): resuelve uniones/SUBST que la ruta nueva, aún inexistente, no resuelve.
            if (DocumentoSupport.EnCarpetaSoloLectura(Path.Combine(carpetaImagenes, origen.Nombre), soloLectura)
                || DocumentoSupport.EnCarpetaSoloLectura(carpetaDwg, soloLectura))
            {
                throw ToolException.InvalidParams(
                    "copiar: la carpeta del dibujo está en carpetas_solo_lectura de lps.json; no se copian imágenes junto a un plano de solo lectura (use copiar:false)");
            }

            DecisionCopia? decision = ImagenRules.ResolverCopia(
                origen.Nombre,
                c => File.Exists(Path.Combine(carpetaImagenes, c)),
                c => MismoContenido(Path.Combine(carpetaImagenes, c), origen.Sha256));
            if (decision == null)
            {
                throw ToolException.InvalidParams(
                    "copiar: no hay un nombre libre en " + ImagenRules.CarpetaImagenes + "\\ para '" + origen.Nombre + "'");
            }

            DecisionCopia d = decision.Value;
            string rutaCopia = Path.Combine(carpetaImagenes, d.Nombre);
            var destino = new Destino(rutaCopia, ImagenRules.RutaRelativa(d.Nombre), d.Reutilizar);
            if (d.Reutilizar)
            {
                return destino;
            }

            try
            {
                Directory.CreateDirectory(carpetaImagenes);
                // CreateNew: si el archivo apareció entre medias, falla aquí y NO se borra (no es nuestro).
                using (var fs = new FileStream(rutaCopia, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    destino.Creado = true;
                    fs.Write(origen.Bytes, 0, origen.Bytes.Length);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Carpeta sin permiso de escritura, disco lleno, archivo creado entre medias…: error del usuario, no INTERNAL.
                destino.Revertir();
                throw ToolException.InvalidParams("copiar: no se pudo escribir " + rutaCopia + " (" + ex.Message + ")");
            }
            catch
            {
                destino.Revertir();
                throw;
            }

            PluginLog.Info("insertar_imagen: copiada " + origen.Ruta + " → " + rutaCopia);
            return destino;
        }

        private static bool MismoContenido(string ruta, string sha256)
        {
            try
            {
                return string.Equals(ImagenRules.Sha256Hex(File.ReadAllBytes(ruta)), sha256, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Ilegible: se considera distinto y se usa otro nombre; nunca se sobrescribe.
                return false;
            }
        }

        private static object Write(Document doc, Plan plan)
        {
            Database db = doc.Database;
            Params p = plan.P;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, p.Common.Espacio);
                DrawLayerFactory.Result layer = DrawLayerFactory.Ensure(db, tr, p.Common.Capa, null, plan.Warnings);
                BlockTableRecord space = PrimitiveSupport.OpenForWrite(tr, resolved);

                (ObjectId defId, bool defReutilizada) = ObtenerDefinicion(db, tr, plan.Referencia, plan.Destino?.RutaCopia ?? plan.Referencia, plan.Origen.Nombre);

                var img = new RasterImage();
                DrawWrite.ApplyByLayer(img, db, layer.Id);
                // Fallo 24 (en vivo, 0.10.1): Orientation antes de ImageDefId lanza InvalidOperationException ("not valid due
                // to the current state of the object"). Orden del ejemplo de Autodesk: primero la definición, luego el marco.
                img.ImageDefId = defId;
                MarcoImagen m = plan.Marco;
                img.Orientation = new CoordinateSystem3d(
                    AcadUtil.ToPoint3d(p.Posicion),
                    new Vector3d(m.UX, m.UY, 0.0),
                    new Vector3d(m.VX, m.VY, 0.0));
                string handle = DrawWrite.Append(space, tr, img);
                // Asocia la imagen con su definición (reactores persistentes, como IMAGEATTACH). Se abre en escritura en la misma
                // transacción; la definición nueva ya está abierta así.
                // Como el ejemplo de Autodesk (Attach Raster Images): sin EnableReactors la definición figura "sin referencia".
                RasterImage.EnableReactors(true);
                img.AssociateRasterDef((RasterImageDef)tr.GetObject(defId, OpenMode.ForWrite));

                tr.Commit();
                plan.Destino?.Confirmar();

                Dictionary<string, object?> r = DrawWrite.Response(plan.Pid, new[] { handle }, layer, resolved.Name, plan.Ins, plan.Warnings);
                r["entidad"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["handle"] = handle,
                    ["posicion"] = new[] { p.Posicion.X, p.Posicion.Y, p.Posicion.Z },
                    ["ancho"] = m.Ancho,
                    ["alto"] = m.Alto,
                    ["rotacion"] = p.Rotacion,
                    ["pixeles"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["ancho"] = plan.Origen.PxAncho,
                        ["alto"] = plan.Origen.PxAlto,
                    },
                    ["definicion_reutilizada"] = defReutilizada,
                };
                r["archivo_origen"] = plan.Origen.Ruta;
                r["archivo_referenciado"] = plan.Referencia;
                r["archivo_copiado"] = plan.Destino?.RutaCopia;
                r["reutilizado"] = plan.Destino?.Reutilizado ?? false;
                return r;
            }
        }

        /// <summary>
        /// Definición de imagen para <paramref name="referencia"/>: reutiliza la que ya apunta a la misma ruta; si no, crea una
        /// en ACAD_IMAGE_DICT con clave única y la carga. Si AutoCAD no puede cargarla, lanza INVALID_PARAMS (la transacción
        /// no se confirma y no queda nada).
        /// </summary>
        private static (ObjectId id, bool reutilizada) ObtenerDefinicion(Database db, Transaction tr, string referencia, string rutaAbsoluta, string nombre)
        {
            ObjectId dictId = RasterImageDef.GetImageDictionary(db);
            if (dictId.IsNull)
            {
                dictId = RasterImageDef.CreateImageDictionary(db);
            }

            var dictRo = (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead);
            var claves = new List<string>();
            ObjectId existente = ObjectId.Null;
            foreach (DBDictionaryEntry entry in dictRo)
            {
                claves.Add(entry.Key);
                if (entry.Value.IsNull || entry.Value.IsErased)
                {
                    continue;
                }

                if (existente.IsNull && tr.GetObject(entry.Value, OpenMode.ForRead) is RasterImageDef def && ImagenRules.MismaRuta(def.SourceFileName, referencia))
                {
                    existente = entry.Value;
                }
            }

            if (!existente.IsNull)
            {
                var defRo = (RasterImageDef)tr.GetObject(existente, OpenMode.ForRead);
                if (!defRo.IsLoaded)
                {
                    CargarOFallar((RasterImageDef)tr.GetObject(existente, OpenMode.ForWrite), referencia, rutaAbsoluta);
                }

                return (existente, true);
            }

            var dictRw = (DBDictionary)tr.GetObject(dictId, OpenMode.ForWrite);
            var nueva = new RasterImageDef { SourceFileName = referencia };
            ObjectId id = dictRw.SetAt(ImagenRules.ClaveDiccionario(nombre, claves), nueva);
            tr.AddNewlyCreatedDBObject(nueva, true);
            CargarOFallar(nueva, referencia, rutaAbsoluta);
            return (id, false);
        }

        /// <summary>
        /// Carga la definición. Se fija ActiveFileName = ruta absoluta de la copia (auditoría Fable): Load usa esa ruta y
        /// SourceFileName (la relativa, la que se guarda en el DWG) no cambia; así no se depende de cómo AutoCAD busca una ruta
        /// relativa dentro de la transacción.
        /// </summary>
        private static void CargarOFallar(RasterImageDef def, string referencia, string rutaAbsoluta)
        {
            try
            {
                def.ActiveFileName = rutaAbsoluta;
                def.Load();
            }
            catch (Exception ex)
            {
                PluginLog.Warn("insertar_imagen: AutoCAD no pudo cargar " + referencia + " (" + ex.GetType().Name + ": " + ex.Message + ").");
                throw ToolException.InvalidParams("ruta: AutoCAD no pudo cargar la imagen (" + ex.Message + ")");
            }

            if (!def.IsLoaded)
            {
                throw ToolException.InvalidParams("ruta: AutoCAD no pudo cargar la imagen '" + referencia + "'");
            }
        }
    }
}
