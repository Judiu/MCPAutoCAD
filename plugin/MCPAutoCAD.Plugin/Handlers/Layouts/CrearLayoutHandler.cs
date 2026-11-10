using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Clean;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Layouts
{
    /// <summary>
    /// crear_layout (plugin 0.9.0, fase M). Crea un layout de papel al final de las pestañas, vacío o importado de un layout
    /// de otro archivo (.dwg/.dwt): configuración de página (PlotSettings.CopyFrom) y entidades del papel
    /// (WblockCloneObjects; los viewports de la plantilla no se copian porque mostrarían otro modelo). Lo inicializa
    /// (Layout.Initialize) para que tenga su viewport de papel y admita crear_viewport sin activarlo; no cambia la pestaña
    /// activa. Sin confirmar: true solo previsualiza.
    /// SEGURIDAD: 'plantilla' es una entrada del cliente: misma validación que insertar_export (ExportRules.ValidatePath,
    /// carpetas_export_permitidas); se lee en una base lateral en memoria que se descarta y nunca se guarda.
    /// </summary>
    internal sealed class CrearLayoutHandler : IToolHandler
    {
        private const int MaxListed = 40;
        private static readonly string[] Allowed = { "nombre", "plantilla", "layout_plantilla", "confirmar", "preview" };

        public string Name => "crear_layout";

        public string Description =>
            "Crea un layout de papel nuevo al final de las pestañas: vacío (configuración de página por defecto del dibujo) o " +
            "importado de un layout de otro archivo .dwg/.dwt ('plantilla' + 'layout_plantilla'), con su configuración de página y " +
            "lo que tenga en el papel (rótulo, textos; sin sus viewports). Queda inicializado para crear_viewport, sin cambiar la " +
            "pestaña activa. Sin confirmar: true solo previsualiza; con confirmar: true lo crea en un paso de deshacer.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""nombre"": " + LayoutWrite.SchemaNuevoNombre + @",
    ""plantilla"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1000, ""description"": ""Ruta absoluta (unidad o UNC) de un .dwg o .dwt del que importar el layout, sin '..' ni comodines. Solo se lee. Si lps.json define carpetas_export_permitidas, debe estar dentro de una."" },
    ""layout_plantilla"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Layout de la plantilla a importar (sin distinguir mayúsculas). Obligatorio si la plantilla tiene más de un layout de papel."" },
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""nombre""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""nombre"": { ""type"": ""string"" },
    ""handle"": { ""type"": [""string"", ""null""], ""description"": ""Handle del layout creado (null en previsualización)."" },
    ""orden"": { ""type"": ""integer"" },
    ""plantilla"": { ""type"": [""string"", ""null""] },
    ""layout_plantilla"": { ""type"": [""string"", ""null""] },
    ""entidades_copiadas"": { ""type"": ""integer"", ""description"": ""Entidades del papel de la plantilla que se copiarán (previsualización) o que se copiaron (sin viewports)."" },
    ""viewports_omitidos"": { ""type"": ""integer"" },
    ""inicializado"": { ""type"": [""boolean"", ""null""], ""description"": ""true si el layout quedó con su viewport de papel (null en previsualización)."" },
    ""pagina"": { ""type"": [""object"", ""null""], ""description"": ""impresora, papel, tamano_papel_mm, estilo_impresion, rotacion, escala_impresion, unidades_papel, centrado: de la plantilla en previsualización, del layout creado al escribir; null si es vacío y en previsualización."" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""nombre"", ""handle"", ""orden"", ""plantilla"", ""layout_plantilla"", ""entidades_copiadas"", ""viewports_omitidos"", ""inicializado"", ""pagina""]
}");

        private sealed class Params
        {
            public string Nombre = "";
            public string? Plantilla;
            public string? LayoutPlantilla;
            public CleanWrite.Common Common = new CleanWrite.Common(false, null);
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            var r = new Params
            {
                Nombre = LayoutWrite.ReadNewName(p, "nombre"),
                Plantilla = ParamReader.OptionalString(p, "plantilla", 1, 1000),
                LayoutPlantilla = ParamReader.OptionalString(p, "layout_plantilla", 1, 255),
                Common = CleanWrite.ReadCommon(p),
            };
            if (r.LayoutPlantilla != null && r.Plantilla == null)
            {
                throw ToolException.InvalidParams("layout_plantilla: solo aplica con plantilla");
            }

            if (r.Plantilla != null)
            {
                string ext = Path.GetExtension(r.Plantilla);
                if (!string.Equals(ext, ".dwg", StringComparison.OrdinalIgnoreCase) && !string.Equals(ext, ".dwt", StringComparison.OrdinalIgnoreCase))
                {
                    throw ToolException.InvalidParams("plantilla: solo se admiten archivos .dwg o .dwt");
                }
            }

            return r;
        }

        /// <summary>Contenido del layout de la plantilla (lectura de la base lateral).</summary>
        private sealed class Source
        {
            public string Layout = "";
            public ObjectId LayoutId;
            public List<ObjectId> Entities = new List<ObjectId>();
            public int ViewportsOmitidos;
            public int Xrefs;
            public Dictionary<string, object?>? Pagina;
        }

        /// <summary>Ruta validada (carpetas permitidas, existencia). Sin AutoCAD salvo el disco.</summary>
        private static void CheckPath(string ruta, List<string> warnings)
        {
            // ExportRules solo admite .dwg: se valida la misma ruta con esa extensión (el resto de reglas es igual).
            string asDwg = Path.ChangeExtension(ruta, ".dwg");
            string? error = ExportRules.ValidatePath(asDwg, InsertarExportHandler.ReadAllowedFolders(warnings));
            if (error != null)
            {
                throw ToolException.InvalidParams(error.Replace("ruta:", "plantilla:"));
            }

            if (!File.Exists(ruta))
            {
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "El archivo '" + ruta + "' no existe.",
                    new Dictionary<string, object?> { ["archivo"] = ruta });
            }
        }

        private static Source ReadSource(Database side, string? layoutName)
        {
            var src = new Source();
            using (Transaction tr = side.TransactionManager.StartOpenCloseTransaction())
            {
                var paper = new List<(string name, ObjectId id)>();
                var dict = (DBDictionary)tr.GetObject(side.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    if (!entry.Value.IsNull && !entry.Value.IsErased && tr.GetObject(entry.Value, OpenMode.ForRead) is Layout l && !l.ModelType)
                    {
                        paper.Add((l.LayoutName, entry.Value));
                    }
                }

                var names = paper.ConvertAll(x => x.name);
                (string name, ObjectId id)? chosen = null;
                if (layoutName == null)
                {
                    if (paper.Count != 1)
                    {
                        throw ToolException.InvalidParams(
                            "layout_plantilla: la plantilla tiene " + paper.Count + " layouts de papel; indique cuál (" + Join(names) + ")");
                    }

                    chosen = paper[0];
                }
                else
                {
                    foreach (var item in paper)
                    {
                        if (string.Equals(item.name, layoutName, StringComparison.OrdinalIgnoreCase))
                        {
                            chosen = item;
                        }
                    }

                    if (chosen == null)
                    {
                        throw ToolException.InvalidParams(
                            "layout_plantilla: la plantilla no tiene el layout '" + layoutName + "' (layouts: " + Join(names) + ")");
                    }
                }

                var layout = (Layout)tr.GetObject(chosen.Value.id, OpenMode.ForRead);
                src.Layout = layout.LayoutName;
                src.LayoutId = chosen.Value.id;
                src.Pagina = LayoutWrite.DescribePage(layout);
                var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                ObjectId paperVp = Audit.LayoutViewports.PaperViewportId(tr, btr);
                foreach (ObjectId id in btr)
                {
                    if (id.IsNull || id.IsErased || id == paperVp)
                    {
                        continue;
                    }

                    if (id.ObjectClass.DxfName == "VIEWPORT")
                    {
                        src.ViewportsOmitidos++;
                        continue;
                    }

                    src.Entities.Add(id);
                    if (tr.GetObject(id, OpenMode.ForRead) is BlockReference br
                        && tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) is BlockTableRecord def && def.IsFromExternalReference)
                    {
                        src.Xrefs++;
                    }
                }

                tr.Commit();
            }

            return src;
        }

        private static string Join(List<string> names) =>
            names.Count <= MaxListed ? string.Join(", ", names) : string.Join(", ", names.GetRange(0, MaxListed)) + " y " + (names.Count - MaxListed) + " más";

        private static int NextOrder(Database db, Transaction tr)
        {
            int max = 0;
            foreach ((string _, ObjectId _, int order) in LayoutWrite.PaperLayouts(db, tr))
            {
                max = Math.Max(max, order);
            }

            return max + 1;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(p.Common.Confirmar, p.Common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = CleanWrite.ReadUnits(db);
            var warnings = new List<string>(AcadUtil.UnitWarnings(db));

            Dictionary<string, object?> Build(CleanWrite.Mode m, string? handle, int orden, Source? src, bool? inicializado, Dictionary<string, object?>? pagina)
            {
                Dictionary<string, object?> r = CleanWrite.Response(ctx.Pid, m, ins, warnings);
                r["nombre"] = p.Nombre;
                r["handle"] = handle;
                r["orden"] = orden;
                r["plantilla"] = p.Plantilla;
                r["layout_plantilla"] = src?.Layout;
                r["entidades_copiadas"] = src?.Entities.Count ?? 0;
                r["viewports_omitidos"] = src?.ViewportsOmitidos ?? 0;
                r["inicializado"] = inicializado;
                r["pagina"] = pagina;
                return r;
            }

            // Lectura previa (también al escribir: un rechazo no deja un paso de deshacer vacío).
            int ordenPre;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                string? clash = LayoutWrite.Clash(db, tr, p.Nombre, null);
                if (clash != null)
                {
                    throw ToolException.InvalidParams("nombre: ya existe el layout '" + clash + "'");
                }

                ordenPre = NextOrder(db, tr);
                tr.Commit();
            }

            Source? srcPre = null;
            if (p.Plantilla != null)
            {
                CheckPath(p.Plantilla, warnings);
                using (Database side = ReadTemplate(p.Plantilla))
                {
                    srcPre = ReadSource(side, p.LayoutPlantilla);
                }

                if (srcPre.Xrefs > 0)
                {
                    warnings.Add("El papel de la plantilla tiene " + srcPre.Xrefs + " referencia(s) externa(s) (xref); se copian con su ruta original, que puede no existir en este PC.");
                }

                if (srcPre.ViewportsOmitidos > 0)
                {
                    warnings.Add("La plantilla tiene " + srcPre.ViewportsOmitidos + " viewport(s) de dibujo en '" + srcPre.Layout + "'; no se copian (cree los suyos con crear_viewport).");
                }
            }

            if (mode == CleanWrite.Mode.Preview)
            {
                return Build(mode, null, ordenPre, srcPre, null, srcPre?.Pagina);
            }

            return UndoGroup.Run(doc, d =>
            {
                Database wdb = d.Database;
                using (Transaction tr = wdb.TransactionManager.StartTransaction())
                {
                    if (LayoutWrite.Clash(wdb, tr, p.Nombre, null) != null)
                    {
                        throw ToolException.InvalidParams("nombre: ya existe el layout '" + p.Nombre + "'");
                    }

                    tr.Commit();
                }

                // Todo lo que puede fallar al leer va ANTES de crear el layout (no queda un layout a medias por eso).
                Database? side = p.Plantilla == null ? null : ReadTemplate(p.Plantilla);
                try
                {
                    Source? src = side == null ? null : ReadSource(side, p.LayoutPlantilla);

                    // LayoutManager abre el Layout por su cuenta: sin Transaction abierta (como renombrar/borrar/copiar).
                    ObjectId newId = LayoutManager.Current.CreateLayout(p.Nombre);
                    try
                    {
                        return Fill(wdb, newId, p, src, side, warnings, Build);
                    }
                    catch (Exception ex)
                    {
                        // UndoGroup solo revierte con UNDOCTL completo: se intenta quitar aquí el layout a medias.
                        string estado;
                        try
                        {
                            LayoutManager.Current.DeleteLayout(p.Nombre);
                            estado = "se eliminó el layout a medio crear";
                        }
                        catch (Exception)
                        {
                            estado = "el layout '" + p.Nombre + "' quedó a medio crear: bórrelo con borrar_layout o CTRL+Z";
                        }

                        string detalle = ex is ToolException tex && tex.ExtraData != null && tex.ExtraData.TryGetValue("detalle", out object? d0)
                            ? Convert.ToString(d0, System.Globalization.CultureInfo.InvariantCulture) ?? ex.Message
                            : ex.Message;
                        throw new ToolException(
                            ErrorCodes.Internal,
                            "No se pudo crear el layout '" + p.Nombre + "'.",
                            new Dictionary<string, object?> { ["detalle"] = detalle + "; " + estado });
                    }
                }
                finally
                {
                    side?.Dispose();
                }
            });
        }

        /// <summary>Lee la plantilla en una base lateral; error de lectura → INTERNAL con mensaje propio.</summary>
        private static Database ReadTemplate(string ruta)
        {
            try
            {
                return MCPAutoCAD.Plugin.Handlers.Draw.BlockLibrary.ReadSide(ruta);
            }
            catch (ToolException ex) when (ex.Code == ErrorCodes.Internal)
            {
                throw new ToolException(ErrorCodes.Internal, "No se pudo leer la plantilla: " + Path.GetFileName(ruta) + ".", ex.ExtraData);
            }
        }

        /// <summary>
        /// Completa el layout recién creado: configuración de página y entidades de la plantilla, orden al final e
        /// inicialización. Cada paso en su propia Transaction; Initialize sin el registro de bloque abierto.
        /// </summary>
        private static object Fill(
            Database wdb,
            ObjectId newId,
            Params p,
            Source? src,
            Database? side,
            List<string> warnings,
            Func<CleanWrite.Mode, string?, int, Source?, bool?, Dictionary<string, object?>?, Dictionary<string, object?>> build)
        {
            int clonadas = 0;
            using (Transaction tr = wdb.TransactionManager.StartTransaction())
            {
                var layout = (Layout)tr.GetObject(newId, OpenMode.ForWrite);
                if (side != null && src != null)
                {
                    // Solo la parte de PlotSettings (no nombre, orden ni ids de la otra base): copia intermedia no residente.
                    using (var ps = new PlotSettings(false))
                    {
                        using (Transaction str = side.TransactionManager.StartOpenCloseTransaction())
                        {
                            ps.CopyFrom((Layout)str.GetObject(src.LayoutId, OpenMode.ForRead));
                            str.Commit();
                        }

                        layout.CopyFrom(ps);
                    }

                    if (src.Entities.Count > 0)
                    {
                        var map = new IdMapping();
                        side.WblockCloneObjects(new ObjectIdCollection(src.Entities.ToArray()), layout.BlockTableRecordId, map, DuplicateRecordCloning.Ignore, false);
                        foreach (IdPair pair in map)
                        {
                            if (pair.IsCloned && pair.IsPrimary)
                            {
                                clonadas++;
                            }
                        }
                    }
                }

                // Orden y nombre después de copiar: nada de la plantilla los sobrescribe.
                layout.TabOrder = NextOrderExcluding(wdb, tr, newId);
                if (!string.Equals(layout.LayoutName, p.Nombre, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("El layout creado se llama '" + layout.LayoutName + "' en vez de '" + p.Nombre + "'.");
                }

                tr.Commit();
            }

            // Viewport de papel: comparar el contenido del registro antes y después de Initialize.
            HashSet<ObjectId> before = BtrIds(wdb, newId, out bool hadPaper);
            bool inicializado = hadPaper;
            if (!hadPaper)
            {
                try
                {
                    using (Transaction tr = wdb.TransactionManager.StartTransaction())
                    {
                        ((Layout)tr.GetObject(newId, OpenMode.ForWrite)).Initialize();
                        tr.Commit();
                    }
                }
                catch (Exception ex)
                {
                    // Inicializar es opcional: un fallo aquí no debe borrar un layout ya completo.
                    warnings.Add("No se pudo inicializar el layout (" + (ex is Autodesk.AutoCAD.Runtime.Exception aex ? aex.ErrorStatus.ToString() : ex.Message) + "): actívelo una vez haciendo clic en su pestaña antes de crear viewports.");
                }
            }

            using (Transaction tr = wdb.TransactionManager.StartOpenCloseTransaction())
            {
                var layout = (Layout)tr.GetObject(newId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                int nuevosVp = 0;
                foreach (ObjectId id in btr)
                {
                    if (!id.IsErased && !before.Contains(id) && id.ObjectClass.DxfName == "VIEWPORT")
                    {
                        nuevosVp++;
                    }
                }

                if (!hadPaper)
                {
                    // El primero que crea Initialize es el de papel; los demás los añade AutoCAD (LAYOUTCREATEVIEWPORT).
                    inicializado = nuevosVp > 0 || !Audit.LayoutViewports.PaperViewportId(tr, btr).IsNull;
                    if (!inicializado)
                    {
                        warnings.Add("AutoCAD no creó el viewport de papel del layout: actívelo una vez haciendo clic en su pestaña antes de crear viewports.");
                    }

                    if (nuevosVp > 1)
                    {
                        warnings.Add("Al inicializar el layout AutoCAD añadió " + (nuevosVp - 1) + " viewport(s) de dibujo (LAYOUTCREATEVIEWPORT); revíselos con listar_layouts.");
                    }
                }

                if (src != null)
                {
                    if (clonadas != src.Entities.Count)
                    {
                        warnings.Add("Se copiaron " + clonadas + " de " + src.Entities.Count + " entidades del papel de la plantilla; revise el layout.");
                    }

                    warnings.Add("Las definiciones de bloque, capas y estilos que la plantilla comparte por nombre con este dibujo se conservan como están en el dibujo (no se sobrescriben).");
                }

                Dictionary<string, object?> pagina = LayoutWrite.DescribePage(layout);
                string handle = AcadUtil.HandleToString(layout.Handle);
                int orden = layout.TabOrder;
                tr.Commit();
                Dictionary<string, object?> r = build(CleanWrite.Mode.Write, handle, orden, src, inicializado, pagina);
                r["entidades_copiadas"] = clonadas;
                return r;
            }
        }

        /// <summary>Ids del registro de bloque del layout y si ya tiene viewport de papel (Transaction cerrada al volver).</summary>
        private static HashSet<ObjectId> BtrIds(Database db, ObjectId layoutId, out bool hasPaper)
        {
            var set = new HashSet<ObjectId>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    set.Add(id);
                }

                hasPaper = !Audit.LayoutViewports.PaperViewportId(tr, btr).IsNull;
                tr.Commit();
            }

            return set;
        }

        private static int NextOrderExcluding(Database db, Transaction tr, ObjectId exclude)
        {
            int max = 0;
            foreach ((string _, ObjectId id, int order) in LayoutWrite.PaperLayouts(db, tr))
            {
                if (id != exclude)
                {
                    max = Math.Max(max, order);
                }
            }

            return max + 1;
        }
    }
}
