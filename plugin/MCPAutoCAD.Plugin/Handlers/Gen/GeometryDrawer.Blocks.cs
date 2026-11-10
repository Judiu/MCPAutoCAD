using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;
using MCPAutoCAD.Plugin.Logging;
using DrawLibrary = MCPAutoCAD.Plugin.Handlers.Draw.BlockLibrary;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// Parte de <see cref="GeometryDrawer"/> de la Fase 4B (protocol.md §5G.0): INSERT planificados y borrados planificados
    /// (vano, union_muros). Las firmas son las que llama GeometryDrawer.Draw.
    /// </summary>
    internal static partial class GeometryDrawer
    {
        /// <summary>
        /// Pre-pasada ANTES de abrir ninguna tabla en <paramref name="tr"/> (exigencia de <see cref="DrawLibrary.Import"/>; por
        /// eso la existencia se mira en una transacción propia): para cada <see cref="PlannedBlockRef"/> con DesdeBiblioteca
        /// cuya definición no exista en el dibujo, la importa con DrawLibrary.Import (mismo paso de deshacer; capas
        /// importadas → <see cref="Result.CapasImportadas"/>). Definición inexistente y no importable → NOT_FOUND de §5D.5.
        /// También importa en previsualización: el aviso lo da <see cref="DrawBlockRef"/>, que sí sabe el modo.
        /// </summary>
        private static void EnsureBlockDefinitions(Database db, Transaction tr, IReadOnlyList<PlannedEntity> plan, Result result, List<string> warnings)
        {
            var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PlannedEntity pe in plan)
            {
                if (pe is not PlannedBlockRef { DesdeBiblioteca: true } r || !vistos.Add(r.Nombre) || DefinitionExists(db, r.Nombre))
                {
                    continue;
                }

                string ruta = DrawLibrary.FindFile(r.Nombre) ?? throw LibraryNotFound(r.Nombre);
                DrawLibrary.Import(db, tr, r.Nombre, ruta, null, out _, out List<string> capas);
                result.DefinicionImportada = true;
                foreach (string capa in capas)
                {
                    if (!result.CapasImportadas.Contains(capa, StringComparer.OrdinalIgnoreCase))
                    {
                        result.CapasImportadas.Add(capa);
                    }
                }
            }
        }

        private static bool DefinitionExists(Database db, string nombre)
        {
            using (Transaction ro = db.TransactionManager.StartOpenCloseTransaction())
            {
                var bt = (BlockTable)ro.GetObject(db.BlockTableId, OpenMode.ForRead);
                bool existe = bt.Has(nombre) && !bt[nombre].IsErased;
                ro.Commit();
                return existe;
            }
        }

        /// <summary>NOT_FOUND de §5D.5: ni en el dibujo ni en la biblioteca (data.bloque, data.archivo).</summary>
        internal static ToolException LibraryNotFound(string nombre)
        {
            string esperado;
            try
            {
                esperado = BlockNameRules.LibraryFile(DrawLibrary.Folder, nombre);
            }
            catch (ArgumentException ex)
            {
                return ToolException.InvalidParams(ex.Message);
            }

            return new ToolException(
                ErrorCodes.NotFound,
                "El bloque '" + nombre + "' no está en el dibujo ni en la biblioteca (" + DrawLibrary.Folder + ").",
                new Dictionary<string, object?> { ["bloque"] = nombre, ["archivo"] = esperado });
        }

        /// <summary>
        /// Crea el INSERT (posición, rotación, escala, capa <paramref name="layer"/> = MCP-PREVIEW en previsualización), sus
        /// atributos con los valores por defecto si la definición tiene ATTDEF, aplica
        /// <see cref="PlannedBlockRef.PropiedadesDinamicas"/> y registra {handle, tipo "INSERT", capa, bloque, posicion, rotacion,
        /// escala, dinamico, propiedades_aplicadas, bbox}. La lista propiedades_dinamicas, leída después de aplicar (§5G.6),
        /// queda en <see cref="Result.PropiedadesDinamicas"/>: va en la raíz de la respuesta (§5G.1), no en esta entrada.
        /// </summary>
        private static ObjectId DrawBlockRef(Database db, Transaction tr, BlockTableRecord btr, PlannedBlockRef p, DrawLayerFactory.Result layer, Result result, List<string> warnings)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(p.Nombre) || bt[p.Nombre].IsErased)
            {
                throw ToolException.BlockNotFound(p.Nombre);
            }

            ObjectId defId = bt[p.Nombre];
            var def = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);

            var br = new BlockReference(new Point3d(p.Posicion.X, p.Posicion.Y, p.Posicion.Z), defId);
            DrawWrite.ApplyByLayer(br, db, layer.Id);
            br.ScaleFactors = new Scale3d(p.Escala[0], p.Escala[1], p.Escala[2]);
            br.Rotation = Angles.ToRadians(Angles.Normalize360(p.RotacionGrados));
            DrawWrite.Append(btr, tr, br);

            // Antes de los atributos: si cae a escala, su posición debe salir de la escala definitiva.
            IReadOnlyDictionary<string, object>? valores = p.AnchoPendiente != null
                ? ResolvePendingWidth(br, tr, p, p.AnchoPendiente, result, warnings)
                : p.PropiedadesDinamicas;

            if (def.HasAttributeDefinitions)
            {
                foreach (ObjectId id in def)
                {
                    if (!id.IsNull && !id.IsErased && tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant)
                    {
                        var ar = new AttributeReference();
                        ar.SetAttributeFromBlock(ad, br.BlockTransform);
                        br.AttributeCollection.AppendAttribute(ar);
                        tr.AddNewlyCreatedDBObject(ar, true);
                    }
                }
            }

            var aplicadas = new List<string>();
            if (valores != null)
            {
                try
                {
                    aplicadas = DynamicBlockSupport.Apply(br, tr, p.Nombre, valores, warnings);
                }
                catch (ToolException ex) when (p.DesdeBiblioteca)
                {
                    // Riesgo residual de §5G.6: Database.Insert no se revierte con el Abort de la transacción.
                    PluginLog.Warn("vano: propiedades_dinamicas rechazadas tras importar '" + p.Nombre +
                        "' de la biblioteca; la definición podría quedar en el dibujo. " + ex.Message);
                    throw;
                }
            }

            // El plan marca DesdeBiblioteca solo si la definición faltaba: en previsualización acaba de importarse.
            if (p.DesdeBiblioteca && string.Equals(layer.Nombre, PreviewStore.PreviewLayer, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("La definición '" + p.Nombre + "' se importó de la biblioteca durante la previsualización; si descarta la previsualización quedará sin uso (purgar la elimina).");
            }

            result.PropiedadesDinamicas = ReadProperties(br, tr, p.Nombre);
            Register(br, "INSERT", result, new Dictionary<string, object?>
            {
                ["bloque"] = p.Nombre,
                ["posicion"] = AcadUtil.ToArray(br.Position),
                ["rotacion"] = AcadUtil.ToDegrees360(br.Rotation),
                ["escala"] = new[] { br.ScaleFactors.X, br.ScaleFactors.Y, br.ScaleFactors.Z },
                ["dinamico"] = DynamicBlockSupport.IsDynamic(br, tr),
                ["propiedades_aplicadas"] = aplicadas,
            });
            return br.ObjectId;
        }

        /// <summary>
        /// Lista §5G.6 de la respuesta. El vano ya está hecho: si AutoCAD no da las propiedades, se informa vacía y queda en
        /// plugin.log; una lectura de la respuesta no debe abortar la escritura.
        /// </summary>
        private static List<Dictionary<string, object?>> ReadProperties(BlockReference br, Transaction tr, string nombre)
        {
            try
            {
                return DynamicPropertyRules.ToJson(DynamicBlockSupport.Read(br, tr));
            }
            catch (Exception ex)
            {
                PluginLog.Warn("vano: no se pudieron leer las propiedades dinámicas de '" + nombre + "' (" + ex.GetType().Name + ": " + ex.Message + ")");
                return new List<Dictionary<string, object?>>();
            }
        }

        /// <summary>
        /// §5G.1: definición dinámica recién importada y sin propiedad_ancho configurada. Deduce la propiedad en la referencia
        /// creada, con las propiedades reales, y devuelve los valores a aplicar con el ancho incluido. Si no hay una única
        /// propiedad, o AutoCAD no las da, el vano cae a escala: pone la escala por ancho y avisa. No es un rechazo.
        /// </summary>
        private static IReadOnlyDictionary<string, object>? ResolvePendingWidth(
            BlockReference br, Transaction tr, PlannedBlockRef p, PendingWidth pendiente, Result result, List<string> warnings)
        {
            WidthMatch? encontrado = null;
            try
            {
                encontrado = DynamicPropertyRules.FindWidthProperty(DynamicBlockSupport.Read(br, tr), pendiente.ValorNominal);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("vano: la referencia de '" + p.Nombre + "' no dio sus propiedades dinámicas (" + ex.GetType().Name + ": " + ex.Message + ")");
            }

            if (encontrado?.Propiedad != null)
            {
                string propiedad = encontrado.Propiedad.Nombre;
                var valores = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, object> item in p.PropiedadesDinamicas ?? new Dictionary<string, object>())
                {
                    valores[item.Key] = item.Value;
                }

                if (valores.ContainsKey(propiedad))
                {
                    warnings.Add(DynamicPropertyRules.WidthIgnoredWarning(propiedad));
                }

                valores[propiedad] = pendiente.ValorPropiedad;
                result.AnchoResuelto = new ResolvedWidth(propiedad, p.Escala, null);
                return valores;
            }

            IReadOnlyList<double> e = pendiente.EscalaSinPropiedad;
            br.ScaleFactors = new Scale3d(e[0], e[1], e[2]);
            warnings.Add(encontrado != null && encontrado.Ambigua
                ? DynamicPropertyRules.AmbiguousWidthWarning(p.Nombre, encontrado.Candidatas, pendiente.FactorAncho)
                : DynamicPropertyRules.ScaledWidthWarning(p.Nombre, pendiente.FactorAncho));
            result.AnchoResuelto = new ResolvedWidth(null, e, pendiente.FactorAncho);
            return p.PropiedadesDinamicas;
        }

        /// <summary>
        /// Modo definitivo: abre la entidad por handle (NOT_FOUND si no existe; INVALID_PARAMS "borrar: la entidad &lt;h&gt; ya no
        /// es &lt;TipoEsperado&gt;" si cambió de tipo), la borra y añade su handle a <see cref="Result.HandlesBorrados"/>. Los HATCH
        /// asociativos que reaccionaban a ella (GetPersistentReactorIds) NO se borran aquí: el planificador los incluye
        /// como PlannedErase propios (y planifica los nuevos). Devuelve ObjectId.Null (no crea nada).
        /// </summary>
        private static ObjectId DrawErase(Database db, Transaction tr, PlannedErase p, Result result, List<string> warnings)
        {
            Entity e = AcadUtil.TryGetEntityByHandle(db, p.Handle, tr) ?? throw ToolException.EntityNotFound(p.Handle);
            if (!string.Equals(AcadUtil.DxfName(e), p.TipoEsperado, StringComparison.Ordinal))
            {
                throw ToolException.InvalidParams("borrar: la entidad " + p.Handle + " ya no es " + p.TipoEsperado,
                    new Dictionary<string, object?> { ["handle"] = p.Handle });
            }

            e.UpgradeOpen();
            e.Erase();
            result.HandlesBorrados.Add(p.Handle);
            return ObjectId.Null;
        }
    }
}
