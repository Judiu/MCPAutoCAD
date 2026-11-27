using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Audit;
using MCPAutoCAD.Plugin.Handlers.Clean;
using MCPAutoCAD.Plugin.Logging;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// <c>insertar_dwg</c> (fallo 56, plugin 0.10.6). Hilo principal; requiere documento; ESCRITURA solo con
    /// <c>confirmar: true</c> (sin él previsualiza conteos, capas, bloques, unidades y colocación sin escribir).
    /// Unifica un archivo <c>.dwg</c> local (solo lectura via <see cref="BlockLibrary.ReadSide"/>) en el dibujo activo,
    /// ya sea clonando las entidades del espacio modelo (<c>modo: "entidades"</c>, incluyendo cotas, bloques y
    /// propiedades completas de capa como TrueColor/transparencia) o insertándolo como definición de bloque
    /// (<c>modo: "bloque"</c>), en un solo paso de deshacer.
    /// </summary>
    internal sealed class InsertarDwgHandler : IToolHandler
    {
        private const int TiempoMs = 120000;

        private static readonly string[] Allowed =
        {
            "ruta", "modo", "espacio", "desplazamiento", "escala", "rotacion",
            "nombre_bloque", "capa_destino", "prefijo_capas", "prefijo_bloques",
            "filtrar_capas", "excluir_capas",
            "forzar", "aceptar_sin_unidades", "confirmar", "preview",
        };

        private static readonly Autodesk.AutoCAD.Runtime.RXClass ClaseInsert =
            Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(BlockReference));

        public string Name => "insertar_dwg";

        public string Description =>
            "ESCRIBE en el dibujo: inserta o unifica un archivo .dwg local (solo lectura; nunca modifica el origen) " +
            "conservando definiciones de bloque, cotas, achurados y propiedades completas de capa (color RGB/ACI, " +
            "transparencia, grosor, tipo de línea). Con modo: 'entidades' (defecto) clona entidades nativas al espacio " +
            "destino con prefijo_capas / prefijo_bloques opcionales, filtrar_capas / excluir_capas, desplazamiento, escala " +
            "y rotacion (tope 10000 modelo / 20000 efectivas). Con modo: 'bloque' lo inserta como definición de bloque. " +
            "Sin confirmar: true solo PREVISUALIZA. Con confirmar: true escribe en un solo paso de deshacer.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public int MainThreadTimeoutMs => TiempoMs;

        public string TimeoutHint(JsonElement parameters) =>
            "Si confirmar:true agotó la espera, compruebe con consultar_entidades o bloques antes de repetir para no duplicar.";

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""ruta"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1000, ""description"": ""Ruta absoluta en unidad local o asignada de un archivo .dwg (sin UNC, '..' ni comodines; máximo 200 MB). Solo se lee."" },
    ""modo"": { ""type"": ""string"", ""enum"": [""entidades"", ""bloque""], ""default"": ""entidades"", ""description"": ""'entidades' (defecto): clona entidades del espacio modelo (incluye DIMENSION, INSERT, HATCH…). 'bloque': inserta todo el DWG como una definición de bloque."" },
    ""espacio"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""default"": ""modelo"", ""description"": ""'modelo' (defecto) o nombre de un layout destino."" },
    ""desplazamiento"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""default"": [0, 0, 0], ""description"": ""Vector [dx, dy] o [dx, dy, dz] en modo='entidades', o punto de inserción en modo='bloque'."" },
    ""escala"": { ""type"": ""number"", ""not"": { ""const"": 0 }, ""default"": 1, ""description"": ""Factor de escala uniforme (en modo='entidades' debe ser > 0; en modo='bloque' != 0)."" },
    ""rotacion"": { ""type"": ""number"", ""default"": 0, ""description"": ""Rotación en grados antihorarios alrededor del origen (en modo='entidades' o 'bloque')."" },
    ""nombre_bloque"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 200, ""description"": ""Solo modo='bloque': nombre de la definición a crear (defecto derivado del archivo)."" },
    ""capa_destino"": " + DrawWrite.SchemaCapa + @",
    ""prefijo_capas"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 80, ""description"": ""Prefijo añadido en memoria a las capas del DWG origen (salvo '0' y 'DEFPOINTS') antes de importar, p. ej. 'P2_'."" },
    ""prefijo_bloques"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 80, ""description"": ""Prefijo añadido en memoria a los bloques con nombre del DWG origen antes de importar."" },
    ""filtrar_capas"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 }, ""description"": ""Patrones de capa a incluir (admite comodines * y ?)."" },
    ""excluir_capas"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 }, ""description"": ""Patrones de capa a excluir (admite comodines * y ?), p. ej. ['*DIMS*']."" },
    ""forzar"": " + DrawWrite.SchemaForzar + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @",
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""ruta""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""archivo"": { ""type"": ""string"" },
    ""modo"": { ""type"": ""string"" },
    ""espacio"": { ""type"": ""string"" },
    ""n_entidades_archivo"": { ""type"": ""integer"" },
    ""n"": { ""type"": ""integer"" },
    ""n_entidades_en_bloques"": { ""type"": ""integer"" },
    ""n_bloques_efectivos"": { ""type"": ""integer"" },
    ""n_entidades_efectivas"": { ""type"": ""integer"" },
    ""excluidas_por_filtro"": { ""type"": ""integer"" },
    ""omitidas_xref"": { ""type"": ""integer"" },
    ""por_tipo"": { ""type"": ""object"" },
    ""por_capa"": { ""type"": ""object"" },
    ""capas_nuevas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""capas_existentes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""capas_creadas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""bloques_nuevos"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""bloques_existentes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""nombre_bloque"": { ""type"": [""string"", ""null""] },
    ""handles_muestra"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""bbox"": { ""type"": [""array"", ""null""] },
    ""insunits_archivo"": { ""type"": ""integer"" },
    ""unidades_archivo"": { ""type"": ""string"" },
    ""unidades_compatibles"": { ""type"": ""boolean"" },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""archivo"", ""modo"", ""espacio"", ""n"", ""n_entidades_en_bloques"", ""n_entidades_efectivas"", ""origen_texto""]
}");

        private sealed class Params
        {
            public string Ruta = "";
            public string Modo = InsertDwgRules.ModoEntidades;
            public string Espacio = SpaceResolver.ModelName;
            public Point3 Desplazamiento = new Point3(0, 0, 0);
            public double Escala = 1.0;
            public double Rotacion;
            public string? NombreBloque;
            public string? CapaDestino;
            public string? PrefijoCapas;
            public string? PrefijoBloques;
            public List<string>? FiltrarCapas;
            public List<string>? ExcluirCapas;
            public bool Forzar;
            public bool AceptarSinUnidades;
            public CleanWrite.Mode Mode;
        }

        private sealed class SelectedEntity
        {
            public SelectedEntity(ObjectId id, string tipo, string capaOriginal, string capaEfectiva, string? bloqueOriginal, string? bloqueEfectivo, ObjectId defId, BBox2? box)
            {
                Id = id;
                Tipo = tipo;
                CapaOriginal = capaOriginal;
                CapaEfectiva = capaEfectiva;
                BloqueOriginal = bloqueOriginal;
                BloqueEfectivo = bloqueEfectivo;
                DefId = defId;
                Box = box;
            }

            public ObjectId Id { get; }
            public string Tipo { get; }
            public string CapaOriginal { get; }
            public string CapaEfectiva { get; }
            public string? BloqueOriginal { get; }
            public string? BloqueEfectivo { get; }
            public ObjectId DefId { get; }
            public BBox2? Box { get; }
        }

        private sealed class Prep
        {
            public string Espacio = SpaceResolver.ModelName;
            public ObjectId SpaceId;
            public int TotalArchivo;
            public int ExcluidasFiltro;
            public int OmitidasXref;
            public List<SelectedEntity> Seleccion = new List<SelectedEntity>();
            public int[] Pesos = Array.Empty<int>();
            public List<IReadOnlyCollection<int>> Grupos = new List<IReadOnlyCollection<int>>();
            public Dictionary<string, int> PorTipo = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<string, int> PorCapa = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public List<string> CapasNuevas = new List<string>();
            public List<string> CapasExistentes = new List<string>();
            public List<string> BloquesNuevos = new List<string>();
            public List<string> BloquesExistentes = new List<string>();
            public int EntidadesEnBloques;
            public int BloquesEfectivos;
            public int EntidadesEfectivas;
            public int InsArchivo;
            public bool UnidadesCompatibles = true;
            public BBox2? CajaTransformada;
            public string? NombreBloqueFinal;
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string ruta = ParamReader.RequiredText(p, "ruta", 1, 1000);
            string? rutaErr = InsertDwgRules.ValidatePath(ruta);
            if (rutaErr != null)
            {
                throw ToolException.InvalidParams(rutaErr);
            }

            string modo = ParamReader.OptionalEnum(p, "modo", InsertDwgRules.ValidModes) ?? InsertDwgRules.ModoEntidades;
            string espacio = ParamReader.OptionalString(p, "espacio", 1, 255) ?? SpaceResolver.ModelName;
            Point3 disp = ParamReader.Has(p, "desplazamiento")
                ? ParamReader.RequiredPoint(p, "desplazamiento")
                : new Point3(0, 0, 0);

            double escala = 1.0;
            if (p.TryGetProperty("escala", out JsonElement escEl))
            {
                if (escEl.ValueKind != JsonValueKind.Number || !escEl.TryGetDouble(out escala) || !double.IsFinite(escala) || escala == 0.0)
                {
                    throw ToolException.InvalidParams("escala: debe ser un número distinto de 0");
                }
            }

            if (modo == InsertDwgRules.ModoEntidades && escala <= 0.0)
            {
                throw ToolException.InvalidParams("escala: debe ser mayor que 0 cuando modo='entidades'");
            }

            double rot = 0.0;
            if (p.TryGetProperty("rotacion", out JsonElement rotEl))
            {
                if (rotEl.ValueKind != JsonValueKind.Number || !rotEl.TryGetDouble(out rot) || !double.IsFinite(rot))
                {
                    throw ToolException.InvalidParams("rotacion: debe ser un número finito (grados)");
                }
            }

            string? nombreBloque = ParamReader.OptionalString(p, "nombre_bloque", 1, ExportRules.MaxBlockName);
            if (nombreBloque != null)
            {
                if (modo != InsertDwgRules.ModoBloque)
                {
                    throw ToolException.InvalidParams("nombre_bloque: solo aplica con modo='bloque'");
                }

                string? bErr = BlockNameRules.Validate(nombreBloque);
                if (bErr != null)
                {
                    throw ToolException.InvalidParams("nombre_bloque" + bErr.Substring("nombre".Length));
                }
            }

            string? capaDestino = ParamReader.OptionalString(p, "capa_destino", 1, 255);
            if (capaDestino != null)
            {
                if (modo != InsertDwgRules.ModoBloque)
                {
                    throw ToolException.InvalidParams("capa_destino: solo aplica con modo='bloque'");
                }

                DrawLayerFactory.Plan(capaDestino, null);
            }

            string? prefCapas = ParamReader.OptionalString(p, "prefijo_capas", 1, InsertDwgRules.MaxPrefixLength);
            string? pcErr = InsertDwgRules.ValidatePrefix(prefCapas, "prefijo_capas");
            if (pcErr != null)
            {
                throw ToolException.InvalidParams(pcErr);
            }

            string? prefBloques = ParamReader.OptionalString(p, "prefijo_bloques", 1, InsertDwgRules.MaxPrefixLength);
            string? pbErr = InsertDwgRules.ValidatePrefix(prefBloques, "prefijo_bloques");
            if (pbErr != null)
            {
                throw ToolException.InvalidParams(pbErr);
            }

            List<string>? filtrar = InsertDwgRules.NormalizeLayerPatterns(
                ParamReader.OptionalStringArray(p, "filtrar_capas", 1, InsertDwgRules.MaxFilterLayers, 1),
                "filtrar_capas", out string? errFilt);
            if (errFilt != null)
            {
                throw ToolException.InvalidParams(errFilt);
            }

            List<string>? excluir = InsertDwgRules.NormalizeLayerPatterns(
                ParamReader.OptionalStringArray(p, "excluir_capas", 1, InsertDwgRules.MaxFilterLayers, 1),
                "excluir_capas", out string? errExcl);
            if (errExcl != null)
            {
                throw ToolException.InvalidParams(errExcl);
            }

            CleanWrite.Common cw = CleanWrite.ReadCommon(p);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(cw.Confirmar, cw.Preview);

            return new Params
            {
                Ruta = ruta,
                Modo = modo,
                Espacio = espacio,
                Desplazamiento = disp,
                Escala = escala,
                Rotacion = rot,
                NombreBloque = nombreBloque,
                CapaDestino = capaDestino,
                PrefijoCapas = prefCapas,
                PrefijoBloques = prefBloques,
                FiltrarCapas = filtrar,
                ExcluirCapas = excluir,
                Forzar = ParamReader.OptionalBool(p, "forzar") ?? false,
                AceptarSinUnidades = ParamReader.OptionalBool(p, "aceptar_sin_unidades") ?? false,
                Mode = mode,
            };
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = p.Mode == CleanWrite.Mode.Write
                ? DrawWrite.CheckUnits(db, p.AceptarSinUnidades, warnings)
                : CleanWrite.ReadUnits(db);

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

            string? sizeErr = InsertDwgRules.ValidateSize(bytes);
            if (sizeErr != null)
            {
                throw ToolException.InvalidParams(sizeErr);
            }

            Database side = BlockLibrary.ReadSide(p.Ruta);
            bool pendiente = false;
            try
            {
                Prep prep = Prepare(db, side, p, ins, warnings);

                if (p.Mode == CleanWrite.Mode.Preview)
                {
                    if (prep.CajaTransformada != null)
                    {
                        var r4w = new List<string>();
                        try
                        {
                            PlacementCheck.Run(db, prep.Espacio, new[] { prep.CajaTransformada.Value }, false, "entidades", r4w);
                        }
                        catch (ToolException ex) when (ex.Code == ErrorCodes.InvalidParams)
                        {
                            warnings.Add("Colocación: " + ex.Message);
                        }
                    }

                    return BuildResponse(ctx.Pid, CleanWrite.Mode.Preview, ins, warnings, p, prep, capasCreadas: new List<string>(), handlesMuestra: new List<string>());
                }

                if (prep.Seleccion.Count == 0)
                {
                    throw ToolException.InvalidParams("no hay entidades en el espacio modelo del DWG que pasen el filtro: nada que insertar");
                }

                if (p.Modo == InsertDwgRules.ModoEntidades)
                {
                    if (prep.Seleccion.Count > InsertDwgRules.MaxEntities)
                    {
                        throw ToolException.InvalidParams(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "el DWG tiene {0} entidades seleccionadas en el modelo (máximo {1} por llamada); use filtrar_capas, excluir_capas o modo='bloque'.",
                                prep.Seleccion.Count, InsertDwgRules.MaxEntities),
                            new Dictionary<string, object?> { ["motivo"] = "TOO_LARGE", ["entidades_modelo"] = prep.Seleccion.Count });
                    }

                    if (prep.EntidadesEfectivas > InsertDwgRules.MaxEffectiveEntities)
                    {
                        throw ToolException.InvalidParams(
                            ImportGeometryRules.TooManyEffectiveEntities(prep.Seleccion.Count, prep.EntidadesEnBloques),
                            new Dictionary<string, object?>
                            {
                                ["motivo"] = "TOO_LARGE",
                                ["entidades_modelo"] = prep.Seleccion.Count,
                                ["entidades_en_bloques"] = prep.EntidadesEnBloques,
                                ["entidades_efectivas"] = prep.EntidadesEfectivas,
                            });
                    }
                }

                var boxes = prep.CajaTransformada != null
                    ? new[] { prep.CajaTransformada.Value }
                    : new[] { PlacementCheck.PointBox(p.Desplazamiento) };
                PlacementCheck.Run(db, prep.Espacio, boxes, p.Forzar, "entidades", warnings);

                object resultado = UndoGroup.Run(doc, d =>
                {
                    try
                    {
                        return Write(d, p, prep, side, warnings, ctx.Pid, ins);
                    }
                    finally
                    {
                        DisposeSide(side);
                    }
                }, () => DisposeSide(side));
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

        private static Prep Prepare(Database db, Database side, Params p, int ins, List<string> warnings)
        {
            var prep = new Prep
            {
                InsArchivo = (int)side.Insunits,
            };

            // Destino: espacio, capas existentes, bloques existentes
            var destinoLayers = new Dictionary<string, (bool Locked, bool Frozen, bool Xref, bool Off)>(StringComparer.OrdinalIgnoreCase);
            var destinoBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction dstTr = db.TransactionManager.StartOpenCloseTransaction())
            {
                SpaceResolver.ResolvedSpace space = SpaceResolver.Resolve(db, dstTr, p.Espacio);
                if (!space.Btr.IsLayout)
                {
                    throw ToolException.InvalidParams("espacio: debe ser el modelo o un layout");
                }

                prep.Espacio = space.Name;
                prep.SpaceId = space.Btr.ObjectId;

                var lt = (LayerTable)dstTr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in lt)
                {
                    if (!lid.IsNull && !lid.IsErased)
                    {
                        var ltr = (LayerTableRecord)dstTr.GetObject(lid, OpenMode.ForRead);
                        destinoLayers[ltr.Name] = (ltr.IsLocked, ltr.IsFrozen, ltr.IsDependent || ltr.Name.IndexOf('|') >= 0, ltr.IsOff);
                    }
                }

                var bt = (BlockTable)dstTr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId bid in bt)
                {
                    if (!bid.IsNull && !bid.IsErased)
                    {
                        destinoBlocks.Add(((BlockTableRecord)dstTr.GetObject(bid, OpenMode.ForRead)).Name);
                    }
                }

                if (p.Modo == InsertDwgRules.ModoBloque)
                {
                    DrawLayerFactory.Check(db, dstTr, p.CapaDestino, null, out bool capaEx, warnings);
                    if (p.NombreBloque != null)
                    {
                        if (destinoBlocks.Contains(p.NombreBloque))
                        {
                            throw ToolException.InvalidParams("nombre_bloque: el bloque '" + p.NombreBloque + "' ya existe en el dibujo");
                        }

                        prep.NombreBloqueFinal = p.NombreBloque;
                    }
                    else
                    {
                        prep.NombreBloqueFinal = ExportRules.BlockNameFor(p.Ruta, destinoBlocks.ToList());
                    }

                    if (!capaEx && p.CapaDestino != null)
                    {
                        string targetLyr = DrawLayerFactory.Plan(p.CapaDestino, null).Nombre;
                        BlockLibrary.CheckSideLayer(side, targetLyr, l => DrawLayerFactory.RequireWritable(l, new List<string>()));
                    }
                }

                dstTr.Commit();
            }

            // Unidades
            ImportGeometryRules.VeredictoUnidades uv = ImportGeometryRules.CompareUnits(prep.InsArchivo, ins, UnitsTable.SpanishName);
            prep.UnidadesCompatibles = uv.Estado != ImportGeometryRules.EstadoUnidades.Incompatible;
            if (uv.Estado == ImportGeometryRules.EstadoUnidades.Incompatible)
            {
                if (p.Mode == CleanWrite.Mode.Write && Math.Abs(p.Escala - 1.0) < 1e-9)
                {
                    throw ToolException.InvalidParams(
                        uv.Mensaje.Replace("importar_geometria", "insertar_dwg", StringComparison.Ordinal)
                        + " (o indique 'escala' explícita de conversión)",
                        new Dictionary<string, object?> { ["insunits_archivo"] = prep.InsArchivo, ["insunits"] = ins });
                }

                warnings.Add(uv.Mensaje.Replace("importar_geometria", "insertar_dwg", StringComparison.Ordinal));
            }
            else if (uv.Estado == ImportGeometryRules.EstadoUnidades.Advertencia)
            {
                warnings.Add(uv.Mensaje);
            }

            // Inventario origen (side)
            var memoXref = new Dictionary<ObjectId, bool>();
            BBox2? rawBox = null;
            var blockCache = new Dictionary<ObjectId, BlockScan>();
            var defsContadas = new HashSet<ObjectId>();
            var layerCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            using (Transaction srcTr = side.TransactionManager.StartOpenCloseTransaction())
            {
                var bt = (BlockTable)srcTr.GetObject(side.BlockTableId, OpenMode.ForRead);
                var model = (BlockTableRecord)srcTr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                var usedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var usedBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var asociativos = new Dictionary<ObjectId, (bool Completo, List<ObjectId> Contorno)>();

                foreach (ObjectId id in model)
                {
                    if (id.IsNull || id.IsErased || !(srcTr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                    {
                        continue;
                    }

                    prep.TotalArchivo++;
                    string layerOrig = ent.Layer ?? "0";
                    if (!PassesLayerFilters(layerOrig, p.FiltrarCapas, p.ExcluirCapas, layerCache))
                    {
                        prep.ExcluidasFiltro++;
                        continue;
                    }

                    if (layerOrig.IndexOf('|') >= 0)
                    {
                        prep.OmitidasXref++;
                        continue;
                    }

                    string? bOrig = null;
                    string? bEff = null;
                    ObjectId defId = ObjectId.Null;
                    if (ent is BlockReference br)
                    {
                        defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                        if (defId.IsNull || defId.IsErased || !(srcTr.GetObject(defId, OpenMode.ForRead, false) is BlockTableRecord btr))
                        {
                            prep.OmitidasXref++;
                            continue;
                        }

                        if (btr.IsFromExternalReference || btr.IsFromOverlayReference || btr.IsDependent
                            || ContainsXref(srcTr, defId, memoXref, new HashSet<ObjectId>()))
                        {
                            prep.OmitidasXref++;
                            continue;
                        }

                        bOrig = btr.Name;
                        bEff = InsertDwgRules.ApplyBlockPrefix(bOrig, p.PrefijoBloques);
                        if (!bEff.StartsWith("*", StringComparison.Ordinal))
                        {
                            usedBlocks.Add(bEff);
                        }
                    }

                    string tipo = NormalizeType(ent);
                    string layerEff = InsertDwgRules.ApplyLayerPrefix(layerOrig, p.PrefijoCapas);
                    usedLayers.Add(layerEff);
                    CleanWrite.Increment(prep.PorTipo, tipo);
                    CleanWrite.Increment(prep.PorCapa, layerEff);

                    if (ent is Hatch h && h.Associative)
                    {
                        asociativos[id] = HatchContour(h);
                    }

                    BBox2? box = AcadUtil.TryBBox2(ent);
                    if (box != null)
                    {
                        rawBox = rawBox == null ? box.Value : rawBox.Value.Union(box.Value);
                    }

                    prep.Seleccion.Add(new SelectedEntity(id, tipo, layerOrig, layerEff, bOrig, bEff, defId, box));
                }

                // Contar entidades dentro de definiciones de bloque nuevas + pesos por elemento
                prep.Pesos = new int[prep.Seleccion.Count];
                for (int i = 0; i < prep.Seleccion.Count; i++)
                {
                    prep.Pesos[i] = 1;
                    ObjectId defId = prep.Seleccion[i].DefId;
                    if (defId.IsNull)
                    {
                        continue;
                    }

                    CountBlockTree(srcTr, defId, p.PrefijoBloques, destinoBlocks, defsContadas, blockCache, out int nEnt, out int nBlq);
                    prep.EntidadesEnBloques += nEnt;
                    prep.BloquesEfectivos += nBlq;
                    prep.Pesos[i] += nEnt;
                }

                prep.EntidadesEfectivas = prep.Seleccion.Count + prep.EntidadesEnBloques;

                var idxMap = new Dictionary<ObjectId, int>(prep.Seleccion.Count);
                for (int i = 0; i < prep.Seleccion.Count; i++)
                {
                    idxMap[prep.Seleccion[i].Id] = i;
                }

                foreach (KeyValuePair<ObjectId, (bool Completo, List<ObjectId> Contorno)> kv in asociativos)
                {
                    if (!idxMap.TryGetValue(kv.Key, out int iHatch) || !kv.Value.Completo || kv.Value.Contorno.Any(c => !idxMap.ContainsKey(c)))
                    {
                        continue;
                    }

                    var grp = new List<int> { iHatch };
                    grp.AddRange(kv.Value.Contorno.Select(c => idxMap[c]));
                    prep.Grupos.Add(grp);
                }

                foreach (string lName in usedLayers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (destinoLayers.TryGetValue(lName, out var st))
                    {
                        prep.CapasExistentes.Add(lName);
                        if (p.Modo == InsertDwgRules.ModoEntidades)
                        {
                            string? reason = ImportGeometryRules.LayerBlockReason(st.Locked, st.Frozen, st.Xref);
                            if (reason != null)
                            {
                                throw ToolException.InvalidParams(
                                    "capa '" + lName + "' ya existe en el dibujo y no admite entidades (" + reason + "). Use prefijo_capas o excluir_capas.",
                                    new Dictionary<string, object?> { ["capa"] = lName });
                            }

                            if (st.Off)
                            {
                                warnings.Add("capa '" + lName + "' está apagada en el dibujo: las entidades insertadas quedarán ocultas.");
                            }
                        }
                    }
                    else
                    {
                        prep.CapasNuevas.Add(lName);
                    }
                }

                foreach (string bName in usedBlocks.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (destinoBlocks.Contains(bName))
                    {
                        prep.BloquesExistentes.Add(bName);
                    }
                    else
                    {
                        prep.BloquesNuevos.Add(bName);
                    }
                }

                srcTr.Commit();
            }

            if (rawBox != null)
            {
                prep.CajaTransformada = TransformBox(rawBox.Value, p.Desplazamiento, p.Escala, p.Rotacion);
            }

            if (p.Modo == InsertDwgRules.ModoEntidades)
            {
                if (prep.Seleccion.Count > InsertDwgRules.MaxEntities)
                {
                    warnings.Add("Hay " + prep.Seleccion.Count.ToString(CultureInfo.InvariantCulture) + " entidades en el modelo (límite al confirmar: "
                        + InsertDwgRules.MaxEntities.ToString(CultureInfo.InvariantCulture) + "); use filtrar_capas, excluir_capas o modo='bloque'.");
                }
                else if (prep.EntidadesEfectivas > InsertDwgRules.MaxEffectiveEntities)
                {
                    warnings.Add(ImportGeometryRules.TooManyEffectiveEntities(prep.Seleccion.Count, prep.EntidadesEnBloques));
                }
            }

            if (p.Modo == InsertDwgRules.ModoEntidades && prep.BloquesExistentes.Count > 0)
            {
                string? bWarn = ImportGeometryRules.AvisoDefinicionesExistentes("bloques", prep.BloquesExistentes, destinoBlocks);
                if (bWarn != null)
                {
                    warnings.Add(bWarn.Replace("importar_geometria", "insertar_dwg", StringComparison.Ordinal));
                }
            }

            return prep;
        }

        private static object Write(Document doc, Params p, Prep prep, Database side, List<string> warnings, int pid, int ins)
        {
            Database db = doc.Database;
            HashSet<string> capasAntes = LeerNombresCapas(db);

            // Paso 1: Renombrar capas/bloques en la base lateral EN MEMORIA (el DWG origen en disco no se toca jamás).
            ApplyPrefixesInSide(side, p.PrefijoCapas, p.PrefijoBloques, warnings);

            if (p.Modo == InsertDwgRules.ModoBloque)
            {
                return WriteAsBlock(db, p, prep, side, capasAntes, warnings, pid, ins);
            }

            return WriteAsEntities(db, p, prep, side, capasAntes, warnings, pid, ins);
        }

        private static object WriteAsEntities(
            Database db, Params p, Prep prep, Database side, HashSet<string> capasAntes,
            List<string> warnings, int pid, int ins)
        {
            // Transformar en la base lateral en memoria si hay desplazamiento/escala/rotación.
            Matrix3d xform = BuildTransform(p.Desplazamiento, p.Escala, p.Rotacion);
            if (!AcadUtil.IsIdentity(xform, 1e-9))
            {
                TransformSideEntities(side, prep.Seleccion.Select(s => s.Id), xform);
            }

            List<List<int>> lotes = ImportGeometryRules.LotesPonderados(
                prep.Seleccion.Count, prep.Pesos, prep.Grupos, InsertDwgRules.BatchSize);
            var clonados = new Dictionary<ObjectId, ObjectId>();

            for (int k = 0; k < lotes.Count; k++)
            {
                var ids = new ObjectIdCollection();
                var batchSet = new HashSet<ObjectId>();
                foreach (int idx in lotes[k])
                {
                    ObjectId id = prep.Seleccion[idx].Id;
                    ids.Add(id);
                    batchSet.Add(id);
                }

                using var mapa = new IdMapping();
                try
                {
                    side.WblockCloneObjects(ids, prep.SpaceId, mapa, DuplicateRecordCloning.Ignore, false);
                }
                catch (AcRuntimeException ex)
                {
                    throw new ToolException(
                        ErrorCodes.Internal,
                        "No se pudo insertar el DWG '" + Path.GetFileName(p.Ruta) + "' (lote " + (k + 1) + " de " + lotes.Count + ").",
                        new Dictionary<string, object?> { ["detalle"] = "WblockCloneObjects: " + ex.ErrorStatus + ": " + ex.Message });
                }

                foreach (IdPair pair in mapa)
                {
                    if (pair.IsCloned && batchSet.Contains(pair.Key) && !pair.Value.IsNull)
                    {
                        clonados[pair.Key] = pair.Value;
                    }
                }
            }

            var handlesMuestra = new List<string>(InsertDwgRules.MaxSampleHandles);
            int nCreadas = 0;
            foreach (SelectedEntity item in prep.Seleccion)
            {
                if (clonados.TryGetValue(item.Id, out ObjectId created))
                {
                    nCreadas++;
                    if (handlesMuestra.Count < InsertDwgRules.MaxSampleHandles)
                    {
                        handlesMuestra.Add(AcadUtil.HandleToString(created.Handle));
                    }
                }
            }

            HashSet<string> capasDespues = LeerNombresCapas(db);
            List<string> capasCreadas = capasDespues.Where(c => !capasAntes.Contains(c))
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

            return BuildResponse(pid, CleanWrite.Mode.Write, ins, warnings, p, prep, capasCreadas, handlesMuestra, nOverride: nCreadas);
        }

        private static object WriteAsBlock(
            Database db, Params p, Prep prep, Database side, HashSet<string> capasAntes,
            List<string> warnings, int pid, int ins)
        {
            if (p.FiltrarCapas != null || p.ExcluirCapas != null)
            {
                var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                using (Transaction tr = side.TransactionManager.StartTransaction())
                {
                    var btSide = (BlockTable)tr.GetObject(side.BlockTableId, OpenMode.ForRead);
                    var modelSide = (BlockTableRecord)tr.GetObject(btSide[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in modelSide)
                    {
                        if (!id.IsNull && !id.IsErased && tr.GetObject(id, OpenMode.ForRead, false) is Entity ent)
                        {
                            if (!PassesLayerFilters(ent.Layer ?? "0", p.FiltrarCapas, p.ExcluirCapas, cache))
                            {
                                tr.GetObject(id, OpenMode.ForWrite, false, true).Erase();
                            }
                        }
                    }

                    tr.Commit();
                }
            }

            string blockName = prep.NombreBloqueFinal!;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId btrId;
                try
                {
                    btrId = db.Insert(blockName, side, true);
                }
                catch (AcRuntimeException ex)
                {
                    throw new ToolException(
                        ErrorCodes.Internal,
                        "No se pudo insertar el DWG como bloque: " + Path.GetFileName(p.Ruta) + ".",
                        new Dictionary<string, object?> { ["detalle"] = "Database.Insert: " + ex.ErrorStatus + ": " + ex.Message });
                }

                if (prep.InsArchivo > 0 && UnitsTable.IsInRange(prep.InsArchivo)
                    && tr.GetObject(btrId, OpenMode.ForRead, false) is BlockTableRecord btr)
                {
                    if ((int)btr.Units != prep.InsArchivo)
                    {
                        btr.UpgradeOpen();
                        btr.Units = (UnitsValue)prep.InsArchivo;
                    }
                }

                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, p.Espacio);
                DrawLayerFactory.Result layer = DrawLayerFactory.Ensure(db, tr, p.CapaDestino, null, warnings);
                var space = (BlockTableRecord)tr.GetObject(resolved.Btr.ObjectId, OpenMode.ForWrite);
                var br = new BlockReference(AcadUtil.ToPoint3d(p.Desplazamiento), btrId);
                DrawWrite.ApplyByLayer(br, db, layer.Id);
                br.ScaleFactors = new Scale3d(p.Escala, p.Escala, p.Escala);
                br.Rotation = Angles.ToRadians(Angles.Normalize360(p.Rotacion));
                string handle = DrawWrite.Append(space, tr, br);
                BBox2? placedBox = AcadUtil.TryBBox2(br);
                if (placedBox != null)
                {
                    prep.CajaTransformada = placedBox;
                }

                tr.Commit();

                HashSet<string> capasDespues = LeerNombresCapas(db);
                List<string> capasCreadas = capasDespues.Where(c => !capasAntes.Contains(c))
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
                return BuildResponse(pid, CleanWrite.Mode.Write, ins, warnings, p, prep, capasCreadas, new List<string> { handle }, nOverride: 1);
            }
        }

        private static void ApplyPrefixesInSide(Database side, string? prefCapas, string? prefBloques, List<string> warnings)
        {
            if (string.IsNullOrEmpty(prefCapas) && string.IsNullOrEmpty(prefBloques))
            {
                return;
            }

            using (Transaction tr = side.TransactionManager.StartTransaction())
            {
                if (!string.IsNullOrEmpty(prefCapas))
                {
                    var lt = (LayerTable)tr.GetObject(side.LayerTableId, OpenMode.ForRead);
                    var toRename = new List<(ObjectId Id, string NewName)>();
                    foreach (ObjectId lid in lt)
                    {
                        if (lid.IsNull || lid.IsErased) continue;
                        var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                        if (ltr.IsDependent || ltr.Name.IndexOf('|') >= 0) continue;
                        string candidate = InsertDwgRules.ApplyLayerPrefix(ltr.Name, prefCapas);
                        if (string.Equals(candidate, ltr.Name, StringComparison.Ordinal)) continue;
                        if (candidate.Length > 255 || lt.Has(candidate))
                        {
                            warnings.Add("prefijo_capas: se conserva '" + ltr.Name + "' porque '" + candidate + "' ya existe o excede 255 caracteres.");
                            continue;
                        }

                        toRename.Add((lid, candidate));
                    }

                    foreach ((ObjectId lid, string newName) in toRename)
                    {
                        var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForWrite);
                        ltr.Name = newName;
                    }
                }

                if (!string.IsNullOrEmpty(prefBloques))
                {
                    var bt = (BlockTable)tr.GetObject(side.BlockTableId, OpenMode.ForRead);
                    var toRename = new List<(ObjectId Id, string NewName)>();
                    foreach (ObjectId bid in bt)
                    {
                        if (bid.IsNull || bid.IsErased) continue;
                        var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                        if (btr.IsLayout || btr.IsAnonymous || btr.IsFromExternalReference || btr.IsFromOverlayReference || btr.IsDependent)
                        {
                            continue;
                        }

                        string candidate = InsertDwgRules.ApplyBlockPrefix(btr.Name, prefBloques);
                        if (string.Equals(candidate, btr.Name, StringComparison.Ordinal)) continue;
                        if (candidate.Length > 255 || bt.Has(candidate))
                        {
                            warnings.Add("prefijo_bloques: se conserva '" + btr.Name + "' porque '" + candidate + "' ya existe o excede 255 caracteres.");
                            continue;
                        }

                        toRename.Add((bid, candidate));
                    }

                    foreach ((ObjectId bid, string newName) in toRename)
                    {
                        var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForWrite);
                        btr.Name = newName;
                    }
                }

                tr.Commit();
            }
        }

        private static void TransformSideEntities(Database side, IEnumerable<ObjectId> ids, Matrix3d xform)
        {
            using (Transaction tr = side.TransactionManager.StartTransaction())
            {
                // Desbloquear temporalmente capas bloqueadas del DWG lateral para permitir TransformBy en memoria.
                var unlocked = new List<LayerTableRecord>();
                var lt = (LayerTable)tr.GetObject(side.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in lt)
                {
                    if (!lid.IsNull && !lid.IsErased
                        && tr.GetObject(lid, OpenMode.ForRead, false) is LayerTableRecord ltr && ltr.IsLocked)
                    {
                        ltr.UpgradeOpen();
                        ltr.IsLocked = false;
                        unlocked.Add(ltr);
                    }
                }

                foreach (ObjectId id in ids)
                {
                    if (!id.IsNull && !id.IsErased && tr.GetObject(id, OpenMode.ForWrite, false) is Entity ent)
                    {
                        ent.TransformBy(xform);
                    }
                }

                foreach (LayerTableRecord ltr in unlocked)
                {
                    ltr.IsLocked = true;
                }

                tr.Commit();
            }
        }

        private static Matrix3d BuildTransform(Point3 disp, double escala, double rotGrados)
        {
            Matrix3d s = Math.Abs(escala - 1.0) > 1e-12
                ? Matrix3d.Scaling(escala, Point3d.Origin)
                : Matrix3d.Identity;
            Matrix3d r = Math.Abs(rotGrados) > 1e-12
                ? Matrix3d.Rotation(Angles.ToRadians(rotGrados), Vector3d.ZAxis, Point3d.Origin)
                : Matrix3d.Identity;
            Matrix3d t = Math.Abs(disp.X) > 1e-12 || Math.Abs(disp.Y) > 1e-12 || Math.Abs(disp.Z) > 1e-12
                ? Matrix3d.Displacement(new Vector3d(disp.X, disp.Y, disp.Z))
                : Matrix3d.Identity;
            return t * r * s;
        }

        private static BBox2 TransformBox(BBox2 b, Point3 disp, double escala, double rotGrados) =>
            ExportRules.PlacedBox(b, (0.0, 0.0), new[] { escala, escala, escala }, rotGrados, disp);

        private static bool PassesLayerFilters(string layer, List<string>? incluir, List<string>? excluir, Dictionary<string, bool> cache)
        {
            if (cache.TryGetValue(layer, out bool cached))
            {
                return cached;
            }

            bool ok = (incluir == null || LayerPattern.MatchesAny(layer, incluir))
                && (excluir == null || !LayerPattern.MatchesAny(layer, excluir));
            cache[layer] = ok;
            return ok;
        }

        private static string NormalizeType(Entity e) => e switch
        {
            Polyline => "LWPOLYLINE",
            Polyline2d => "POLYLINE",
            Polyline3d => "POLYLINE3D",
            PolygonMesh or PolyFaceMesh => "POLYMESH",
            Solid => "SOLID",
            Dimension => "DIMENSION",
            _ => AcadUtil.DxfName(e),
        };

        private static bool ContainsXref(Transaction tr, ObjectId defId, Dictionary<ObjectId, bool> memo, HashSet<ObjectId> visiting)
        {
            if (memo.TryGetValue(defId, out bool cached))
            {
                return cached;
            }

            if (defId.IsNull || !visiting.Add(defId) || !(tr.GetObject(defId, OpenMode.ForRead, false) is BlockTableRecord btr))
            {
                return false;
            }

            bool hasXref = false;
            foreach (ObjectId id in btr)
            {
                Autodesk.AutoCAD.Runtime.RXClass? cls = id.ObjectClass;
                if (cls == null || !cls.IsDerivedFrom(ClaseInsert))
                {
                    continue;
                }

                if (tr.GetObject(id, OpenMode.ForRead, false) is BlockReference br)
                {
                    ObjectId childDef = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                    if (!childDef.IsNull && tr.GetObject(childDef, OpenMode.ForRead, false) is BlockTableRecord child
                        && (child.IsFromExternalReference || child.IsFromOverlayReference || child.IsDependent || ContainsXref(tr, childDef, memo, visiting)))
                    {
                        hasXref = true;
                        break;
                    }
                }
            }

            memo[defId] = hasXref;
            return hasXref;
        }

        private sealed record BlockScan(string Name, bool Skip, int DirectEntities, List<ObjectId> NestedDefs);

        private static void CountBlockTree(
            Transaction tr,
            ObjectId defId,
            string? prefBloques,
            HashSet<string> bloquesEnDibujo,
            HashSet<ObjectId> defsContadas,
            Dictionary<ObjectId, BlockScan> cache,
            out int entitiesAdded,
            out int blocksAdded)
        {
            entitiesAdded = 0;
            blocksAdded = 0;
            if (defId.IsNull || defId.IsErased || !defsContadas.Add(defId))
            {
                return;
            }

            if (!cache.TryGetValue(defId, out BlockScan? scan))
            {
                if (!(tr.GetObject(defId, OpenMode.ForRead, false) is BlockTableRecord btr)
                    || btr.IsLayout || btr.IsFromExternalReference || btr.IsFromOverlayReference || btr.IsDependent)
                {
                    scan = new BlockScan("", true, 0, new List<ObjectId>());
                }
                else
                {
                    int direct = 0;
                    var nested = new List<ObjectId>();
                    foreach (ObjectId id in btr)
                    {
                        if (id.IsNull || id.IsErased) continue;
                        direct++;
                        Autodesk.AutoCAD.Runtime.RXClass? cls = id.ObjectClass;
                        if (cls != null && cls.IsDerivedFrom(ClaseInsert)
                            && tr.GetObject(id, OpenMode.ForRead, false) is BlockReference br)
                        {
                            ObjectId cDef = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                            if (!cDef.IsNull) nested.Add(cDef);
                        }
                    }

                    scan = new BlockScan(btr.Name, false, direct, nested);
                }

                cache[defId] = scan;
            }

            if (scan.Skip)
            {
                return;
            }

            string effectiveName = InsertDwgRules.ApplyBlockPrefix(scan.Name, prefBloques);
            bool isAnon = effectiveName.StartsWith("*", StringComparison.Ordinal);
            if (!isAnon && bloquesEnDibujo.Contains(effectiveName))
            {
                return;
            }

            blocksAdded = 1;
            entitiesAdded = scan.DirectEntities;
            foreach (ObjectId child in scan.NestedDefs)
            {
                CountBlockTree(tr, child, prefBloques, bloquesEnDibujo, defsContadas, cache, out int ce, out int cb);
                entitiesAdded += ce;
                blocksAdded += cb;
            }
        }

        private static (bool Completo, List<ObjectId> Contorno) HatchContour(Hatch h)
        {
            var ids = new List<ObjectId>();
            bool complete = true;
            for (int i = 0; i < h.NumberOfLoops; i++)
            {
                ObjectIdCollection? assoc = null;
                try
                {
                    assoc = h.GetAssociatedObjectIdsAt(i);
                }
                catch (AcRuntimeException)
                {
                    assoc = null;
                }

                if (assoc == null || assoc.Count == 0)
                {
                    complete = false;
                    continue;
                }

                foreach (ObjectId id in assoc)
                {
                    ids.Add(id);
                }
            }

            return (complete, ids);
        }

        private static HashSet<string> LeerNombresCapas(Database db)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction ro = db.TransactionManager.StartOpenCloseTransaction())
            {
                var table = (LayerTable)ro.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    if (!id.IsNull && !id.IsErased)
                    {
                        names.Add(((LayerTableRecord)ro.GetObject(id, OpenMode.ForRead)).Name);
                    }
                }

                ro.Commit();
            }

            return names;
        }

        private static Dictionary<string, object?> BuildResponse(
            int pid,
            CleanWrite.Mode mode,
            int ins,
            List<string> warnings,
            Params p,
            Prep prep,
            List<string> capasCreadas,
            List<string> handlesMuestra,
            int? nOverride = null)
        {
            Dictionary<string, object?> res = CleanWrite.Response(pid, mode, ins, warnings);
            res["archivo"] = p.Ruta;
            res["modo"] = p.Modo;
            res["espacio"] = prep.Espacio;
            res["n_entidades_archivo"] = prep.TotalArchivo;
            res["n"] = nOverride ?? prep.Seleccion.Count;
            res["n_entidades_en_bloques"] = prep.EntidadesEnBloques;
            res["n_bloques_efectivos"] = prep.BloquesEfectivos;
            res["n_entidades_efectivas"] = prep.EntidadesEfectivas;
            res["excluidas_por_filtro"] = prep.ExcluidasFiltro;
            res["omitidas_xref"] = prep.OmitidasXref;
            res["por_tipo"] = CleanWrite.SortedCounts(prep.PorTipo);
            res["por_capa"] = CleanWrite.SortedCounts(prep.PorCapa);
            res["capas_nuevas"] = prep.CapasNuevas;
            res["capas_existentes"] = prep.CapasExistentes;
            res["capas_creadas"] = capasCreadas;
            res["bloques_nuevos"] = prep.BloquesNuevos;
            res["bloques_existentes"] = prep.BloquesExistentes;
            res["nombre_bloque"] = prep.NombreBloqueFinal;
            res["handles_muestra"] = handlesMuestra;
            res["bbox"] = prep.CajaTransformada?.ToArray2D();
            res["insunits_archivo"] = prep.InsArchivo;
            res["unidades_archivo"] = UnitsTable.Name(prep.InsArchivo);
            res["unidades_compatibles"] = prep.UnidadesCompatibles;
            res["origen_texto"] = "contenido_del_plano";
            return res;
        }
    }
}
