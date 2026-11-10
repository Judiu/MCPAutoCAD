using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// Base común de las herramientas GENERADORAS de la Fase 4A (protocol.md §5E.0, §5E.3): muro, ejes_rejilla, recinto,
    /// offset, array_rectangular, array_polar. Implementa el flujo completo; cada herramienta solo aporta:
    /// <list type="number">
    /// <item><see cref="ParseOwn"/>: parámetros propios (puro; hilo del pipe y otra vez en el principal).</item>
    /// <item><see cref="PointRefs"/>: los PuntoRef que hay que resolver (con su ruta para los mensajes).</item>
    /// <item><see cref="Plan"/>: la geometría (<see cref="GeneratedGeometry"/>) a partir de los puntos resueltos; puede LEER
    /// el dibujo (transacción de lectura) pero nunca escribir; lanza INVALID_GEOMETRY / INVALID_PARAMS.</item>
    /// </list>
    /// Flujo de <see cref="Execute"/>: Parse → RequireDocument → R2 estricta (INSUNITS = 0 → UNITS_UNKNOWN, sin escape) →
    /// lectura (resolver PuntoRef, Plan, huérfanas de MCP-PREVIEW) → R4 (PlacementCheck sobre las cajas del plan) →
    /// <b>preview</b>: registrar en <see cref="PreviewStore"/> y, con dibujar_preview, UndoGroup.Run dibujando en MCP-PREVIEW;
    /// <b>sin preview</b>: UndoGroup.Run dibujando en las capas definitivas. <see cref="Confirm"/> (lo llama el handler
    /// confirmar) borra las entidades de preview que sigan existiendo y RECALCULA + dibuja definitivo en UN paso de deshacer.
    /// </summary>
    internal abstract class GeneratorBase : IToolHandler
    {
        /// <summary>Catálogo de generadoras por nombre (lo usa confirmar para recalcular). Se rellena al instanciar cada handler.</summary>
        private static readonly Dictionary<string, GeneratorBase> Catalog = new Dictionary<string, GeneratorBase>(StringComparer.Ordinal);

        protected GeneratorBase()
        {
            lock (Catalog)
            {
                Catalog[Name] = this;
            }
        }

        /// <summary>Generadora registrada con ese nombre, o null.</summary>
        public static GeneratorBase? Find(string name)
        {
            lock (Catalog)
            {
                return Catalog.TryGetValue(name, out GeneratorBase? g) ? g : null;
            }
        }

        public abstract string Name { get; }

        public abstract string Description { get; }

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public abstract JsonElement InputSchema { get; }

        public abstract JsonElement? OutputSchema { get; }

        /// <summary>Nombres de TODOS los parámetros admitidos (propios + <see cref="GenSchema.CommonParams"/>).</summary>
        protected abstract IReadOnlyCollection<string> Allowed { get; }

        /// <summary>Concepto de capa por defecto de la herramienta (p. ej. "muros"); null → MCP-DIBUJO.</summary>
        protected abstract string? DefaultConcept { get; }

        /// <summary>Parámetros propios ya leídos (objeto opaco del handler). Sin AutoCAD. Lanza INVALID_PARAMS.</summary>
        protected abstract object ParseOwn(JsonElement p);

        /// <summary>PuntoRef a resolver, en orden, con la ruta del campo para los mensajes ("eje[0]", "origen", "centro").</summary>
        protected abstract IReadOnlyList<(PointRef Ref, string Path)> PointRefs(object own);

        /// <summary>
        /// Calcula la geometría. <paramref name="puntos"/> en el mismo orden que <see cref="PointRefs"/>. <paramref name="tr"/> es
        /// de SOLO LECTURA (StartOpenCloseTransaction): úsela para leer entidades existentes (offset, arrays). Lanza
        /// <see cref="ToolException"/> (INVALID_GEOMETRY / INVALID_PARAMS / NOT_FOUND).
        /// </summary>
        protected abstract GeneratedGeometry Plan(object own, IReadOnlyList<ResolvedPoint> puntos, Database db, Transaction tr, List<string> advertencias);

        /// <summary>
        /// Campos propios de la herramienta en la raíz de la respuesta. Se llama desde <see cref="BuildResponse"/>: valen igual
        /// para la llamada directa, la previsualización y confirmar.
        /// </summary>
        protected virtual void AddResponseFields(Dictionary<string, object?> response, GeometryDrawer.Result drawn)
        {
        }

        /// <summary>Etiqueta de R4 para los mensajes ("entidades" por defecto).</summary>
        protected virtual string PlacementLabel => "entidades";

        public void ValidateParams(JsonElement parameters)
        {
            ParamReader.RequireObject(parameters, Allowed);
            GenSchema.ReadCommon(parameters);
            ParseOwn(parameters);
        }

        /// <summary>Resultado de la fase de lectura (sin escribir nada).</summary>
        private sealed class Prepared
        {
            public Prepared(object own, GenSchema.Common common, List<ResolvedPoint> puntos, GeneratedGeometry geometry, string espacio, int insunits, List<string> warnings)
            {
                Own = own;
                Common = common;
                Puntos = puntos;
                Geometry = geometry;
                Espacio = espacio;
                Insunits = insunits;
                Warnings = warnings;
            }

            public object Own { get; }
            public GenSchema.Common Common { get; }
            public List<ResolvedPoint> Puntos { get; }
            public GeneratedGeometry Geometry { get; }
            public string Espacio { get; }
            public int Insunits { get; }
            public List<string> Warnings { get; }
        }

        /// <summary>Parse + R2 + resolución + Plan + huérfanas + R4. Lanza sin haber escrito nada.</summary>
        private Prepared Prepare(JsonElement parameters, Document doc, bool checkOrphans)
        {
            ParamReader.RequireObject(parameters, Allowed);
            GenSchema.Common common = GenSchema.ReadCommon(parameters);
            object own = ParseOwn(parameters);
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);

            // R2 estricta (PLAN.md §9): la Fase 4 NO genera geometría sin unidades declaradas; no hay aceptar_sin_unidades.
            int ins = DrawWrite.CheckUnits(db, false, warnings);

            List<ResolvedPoint> puntos;
            GeneratedGeometry geometry;
            string espacio;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                espacio = SpaceResolver.Resolve(db, tr, common.Espacio).Name;
                IReadOnlyList<(PointRef Ref, string Path)> refs = PointRefs(own);
                var refList = new List<PointRef>(refs.Count);
                var pathList = new List<string>(refs.Count);
                foreach ((PointRef r, string path) in refs)
                {
                    refList.Add(r);
                    pathList.Add(path);
                }

                puntos = PointRefResolver.ResolveAll(db, tr, refList, pathList, warnings);
                geometry = Plan(own, puntos, db, tr, warnings);
                if (checkOrphans)
                {
                    string? orphan = PreviewStore.OrphanWarning(doc, db, tr);
                    if (orphan != null)
                    {
                        warnings.Add(orphan);
                    }
                }

                tr.Commit();
            }

            if (geometry.Entidades.Count == 0)
            {
                throw ToolException.InvalidGeometry("la herramienta no produjo ninguna entidad");
            }

            // R4 sobre las cajas del plan (lectura con tope de coste; fuera del UndoGroup).
            PlacementCheck.Run(db, common.Espacio, geometry.CajasParaR4(), common.Forzar, PlacementLabel, warnings);
            return new Prepared(own, common, puntos, geometry, espacio, ins, warnings);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Document doc = ctx.RequireDocument();
            Prepared prep = Prepare(parameters, doc, checkOrphans: true);
            int pid = ctx.Pid;

            if (!prep.Common.Preview)
            {
                return UndoGroup.Run(doc, d => WriteDefinitive(d, prep, pid, null));
            }

            // Previsualización: registro en memoria (nunca escribe por sí mismo) y, si se pide, dibujo en MCP-PREVIEW.
            // Huellas: anclas de los PuntoRef + entidades usadas directamente por handle (eje/contorno, offset, arrays).
            var anclas = new List<AnchorFingerprint>();
            foreach (ResolvedPoint rp in prep.Puntos)
            {
                if (rp.Ancla != null)
                {
                    anclas.Add(rp.Ancla);
                }
            }

            anclas.AddRange(prep.Geometry.AnclasExtra);

            string paramsJson = GenSchema.StripPreviewParams(parameters);
            string docKey = PreviewStore.DocKey(doc);
            if (!prep.Common.DibujarPreview)
            {
                PreviewRecord rec = PreviewStore.Registry.Add(docKey, Name, paramsJson, anclas, Array.Empty<string>(), prep.Espacio, out PreviewRecord? evicted);
                if (evicted != null)
                {
                    prep.Warnings.Add(PreviewRegistry.EvictedWarning(evicted.Id));
                }

                return BuildResponse(pid, prep, rec, false, new GeometryDrawer.Result());
            }

            return UndoGroup.Run(doc, d =>
            {
                Database db = d.Database;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    SpaceResolver.ResolvedSpace space = SpaceResolver.Resolve(db, tr, prep.Common.Espacio);
                    GeometryDrawer.Result drawn = GeometryDrawer.Draw(db, tr, space, prep.Geometry.Entidades, PreviewStore.PreviewLayer, DefaultConcept, prep.Warnings);
                    tr.Commit();
                    PreviewRecord rec = PreviewStore.Registry.Add(docKey, Name, paramsJson, anclas, drawn.Handles, prep.Espacio, out PreviewRecord? evicted);
                    if (evicted != null)
                    {
                        prep.Warnings.Add(PreviewRegistry.EvictedWarning(evicted.Id));
                    }

                    return BuildResponse(pid, prep, rec, true, drawn);
                }
            });
        }

        /// <summary>
        /// Confirmación (la llama el handler <c>confirmar</c>, §5E.5), ya comprobado que el registro pertenece al documento
        /// activo. Recalcula desde <see cref="PreviewRecord.ParamsJson"/>, verifica que las anclas no cambiaron y, en UN paso
        /// de deshacer, borra las entidades de preview que sigan existiendo y dibuja la geometría definitiva. Si algo
        /// falla antes de escribir, no se escribe nada; el registro solo se olvida al terminar con éxito.
        /// </summary>
        public object Confirm(PreviewRecord rec, ExecutionContext ctx)
        {
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            using JsonDocument jd = JsonDocument.Parse(rec.ParamsJson);
            JsonElement parameters = jd.RootElement.Clone();

            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                if (!PointRefResolver.AnchorsUnchanged(db, tr, rec.Anclas, out string? cambiada))
                {
                    throw ToolException.InvalidParams(PreviewRegistry.AnchorChangedDetail(cambiada ?? "?"),
                        new Dictionary<string, object?> { ["preview_id"] = rec.Id, ["handle"] = cambiada });
                }

                tr.Commit();
            }

            Prepared prep = Prepare(parameters, doc, checkOrphans: false);
            int pid = ctx.Pid;
            return UndoGroup.Run(doc, d =>
            {
                object r = WriteDefinitive(d, prep, pid, rec);
                PreviewStore.Registry.Remove(rec.Id);
                return r;
            });
        }

        /// <summary>Escritura definitiva (dentro de UndoGroup): borra la preview (si se confirma) y dibuja en capas definitivas.</summary>
        private object WriteDefinitive(Document doc, Prepared prep, int pid, PreviewRecord? confirming)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                int borradas = 0;
                if (confirming != null)
                {
                    foreach (string h in confirming.HandlesPreview)
                    {
                        Entity? e = AcadUtil.TryGetEntityByHandle(db, h, tr);
                        if (e == null)
                        {
                            continue;
                        }

                        // Solo se borra lo que siga en MCP-PREVIEW: si el usuario lo movió de capa, se conserva y se avisa.
                        if (!string.Equals(e.Layer, PreviewStore.PreviewLayer, StringComparison.OrdinalIgnoreCase))
                        {
                            prep.Warnings.Add("La entidad de previsualización " + h + " ya no está en MCP-PREVIEW (capa " + e.Layer + "); se conservó.");
                            continue;
                        }

                        e.UpgradeOpen();
                        e.Erase();
                        borradas++;
                    }
                }

                SpaceResolver.ResolvedSpace space = SpaceResolver.Resolve(db, tr, prep.Common.Espacio);
                GeometryDrawer.Result drawn = GeometryDrawer.Draw(db, tr, space, prep.Geometry.Entidades, null, DefaultConcept, prep.Warnings);
                tr.Commit();

                Dictionary<string, object?> r = BuildResponse(pid, prep, null, false, drawn);
                if (confirming != null)
                {
                    r["preview_id"] = confirming.Id;
                    r["confirmado"] = true;
                    r["n_preview_borradas"] = borradas;
                    r["herramienta"] = Name;
                }

                return r;
            }
        }

        /// <summary>Respuesta común de una generadora (§5E.0).</summary>
        private Dictionary<string, object?> BuildResponse(int pid, Prepared prep, PreviewRecord? preview, bool previewDrawn, GeometryDrawer.Result drawn)
        {
            var puntos = new List<Dictionary<string, object?>>(prep.Puntos.Count);
            foreach (ResolvedPoint rp in prep.Puntos)
            {
                puntos.Add(rp.ToJson());
            }

            var numeros = new Dictionary<string, object?>(prep.Geometry.Numeros, StringComparer.Ordinal);
            if (!numeros.ContainsKey("n_entidades"))
            {
                numeros["n_entidades"] = prep.Geometry.Entidades.Count;
            }

            if (!numeros.ContainsKey("bbox"))
            {
                numeros["bbox"] = prep.Geometry.Bbox()?.ToArray2D();
            }

            string capa = drawn.Capas.Count > 0 ? drawn.Capas[0] : (preview != null ? PreviewStore.PreviewLayer : "");
            var r = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = pid,
                ["preview"] = preview != null,
                ["preview_id"] = preview?.Id,
                ["preview_dibujada"] = previewDrawn,
                ["caduca"] = preview?.CaducaIso,
                ["handles"] = drawn.Handles,
                ["handles_borrados"] = drawn.HandlesBorrados,   // Fase 4B (§5G.0): vacío en 4A y en previsualización
                ["entidades"] = drawn.Entidades,
                ["capa"] = capa,
                ["capas"] = drawn.Capas,
                ["capas_creadas"] = drawn.CapasCreadas,
                ["espacio"] = prep.Espacio,
                ["puntos_resueltos"] = puntos,
                ["numeros"] = numeros,
                ["insunits"] = prep.Insunits,
                ["unidades"] = UnitsTable.Name(prep.Insunits),
                ["advertencias"] = prep.Warnings,
            };
            AddResponseFields(r, drawn);
            return r;
        }

        /// <summary>Formato invariante de un número para mensajes.</summary>
        protected static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
