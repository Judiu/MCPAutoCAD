using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// Convierte entidades planificadas (<see cref="PlannedEntity"/>, GeometryCore) en entidades reales de AutoCAD dentro
    /// de la transacción de escritura del llamador (ya dentro de UndoGroup.Run: un CTRL+Z). Dos modos:
    /// <list type="bullet">
    /// <item><b>Previsualización</b> (<paramref name="previewLayer"/> ≠ null): TODO va a MCP-PREVIEW (se crea si falta con las
    /// propiedades de capas_mcp), incluidos los clones de copias y las curvas de offset.</item>
    /// <item><b>Definitivo</b>: cada entidad va a la capa de su Concepto/Capa (R3 vía <see cref="DrawLayerFactory.Ensure"/>,
    /// con caché por nombre); las copias y los offsets sin Concepto/Capa conservan la capa de su original.</item>
    /// </list>
    /// Todas las entidades nuevas salen ByLayer (<see cref="DrawWrite.ApplyByLayer"/>). Solo hilo principal.
    /// </summary>
    internal static partial class GeometryDrawer
    {
        /// <summary>Resultado del dibujo: handles en orden de creación y descripción por entidad para la respuesta.</summary>
        internal sealed class Result
        {
            public List<string> Handles { get; } = new List<string>();

            /// <summary>Una entrada por entidad creada: {handle, tipo, capa} + medidas propias (longitud, area, radio…).</summary>
            public List<Dictionary<string, object?>> Entidades { get; } = new List<Dictionary<string, object?>>();

            /// <summary>Capas distintas usadas, en orden de aparición (nombre real del dibujo).</summary>
            public List<string> Capas { get; } = new List<string>();

            /// <summary>Capas que esta llamada creó.</summary>
            public List<string> CapasCreadas { get; } = new List<string>();

            /// <summary>Fase 4B (§5G.0): handles de entidades EXISTENTES borradas por <see cref="PlannedErase"/> (solo en modo definitivo).</summary>
            public List<string> HandlesBorrados { get; } = new List<string>();

            /// <summary>Fase 4B (§5G.1): capas que trajo la importación de un bloque de la biblioteca (vano).</summary>
            public List<string> CapasImportadas { get; } = new List<string>();

            /// <summary>Fase 4B (§5G.1): true si esta llamada importó alguna definición de la biblioteca.</summary>
            public bool DefinicionImportada { get; set; }

            /// <summary>Fase 4B (§5G.1): lo que se decidió al dibujar sobre un <see cref="PendingWidth"/>; null si el plan ya lo decidió.</summary>
            public ResolvedWidth? AnchoResuelto { get; set; }

            /// <summary>Fase 4B (§5G.1): lista §5G.6 de la referencia creada, leída después de aplicar; vacía si no hay INSERT.</summary>
            public List<Dictionary<string, object?>> PropiedadesDinamicas { get; set; } = new List<Dictionary<string, object?>>();
        }

        /// <summary>Ancho decidido en la referencia creada: propiedad (o null si fue por escala), escala y factor.</summary>
        internal sealed record ResolvedWidth(string? Propiedad, IReadOnlyList<double> Escala, double? FactorAncho);

        private sealed class LayerCache
        {
            private readonly Dictionary<string, DrawLayerFactory.Result> _byKey = new Dictionary<string, DrawLayerFactory.Result>(StringComparer.OrdinalIgnoreCase);

            public DrawLayerFactory.Result Get(Database db, Transaction tr, string? capa, string? concepto, List<string> warnings, Result result)
            {
                string key = (capa ?? "") + "\u0001" + (concepto ?? "");
                if (!_byKey.TryGetValue(key, out DrawLayerFactory.Result? r))
                {
                    r = DrawLayerFactory.Ensure(db, tr, capa, concepto, warnings);
                    _byKey[key] = r;
                    if (r.Creada)
                    {
                        result.CapasCreadas.Add(r.Nombre);
                    }
                }

                if (!result.Capas.Contains(r.Nombre, StringComparer.OrdinalIgnoreCase))
                {
                    result.Capas.Add(r.Nombre);
                }

                return r;
            }
        }

        /// <summary>
        /// Dibuja <paramref name="plan"/> en <paramref name="space"/>. <paramref name="previewLayer"/> = nombre de la capa de
        /// previsualización (MCP-PREVIEW) o null para el modo definitivo. <paramref name="defaultConcept"/> = concepto de la
        /// herramienta para las entidades sin Concepto/Capa (null → MCP-DIBUJO).
        /// </summary>
        public static Result Draw(
            Database db,
            Transaction tr,
            SpaceResolver.ResolvedSpace space,
            IReadOnlyList<PlannedEntity> plan,
            string? previewLayer,
            string? defaultConcept,
            List<string> warnings)
        {
            var result = new Result();
            var cache = new LayerCache();

            // Fase 4B (§5G.1): las definiciones de biblioteca se importan ANTES de abrir cualquier tabla en esta
            // transacción (BlockLibrary.Import). Solo si el plan trae algún INSERT planificado.
            bool hasBlocks = false;
            foreach (PlannedEntity pe0 in plan)
            {
                if (pe0 is PlannedBlockRef)
                {
                    hasBlocks = true;
                    break;
                }
            }

            if (hasBlocks)
            {
                EnsureBlockDefinitions(db, tr, plan, result, warnings);
            }

            BlockTableRecord btr = PrimitiveSupport.OpenForWrite(tr, space);
            var created = new List<ObjectId>(plan.Count);   // por índice del plan (Null si no aplica)

            DrawLayerFactory.Result? preview = previewLayer == null ? null : cache.Get(db, tr, previewLayer, null, warnings, result);

            for (int i = 0; i < plan.Count; i++)
            {
                PlannedEntity pe = plan[i];
                DrawLayerFactory.Result? layer = preview;
                if (layer == null && !(pe is PlannedErase) && (pe.Capa != null || pe.Concepto != null || !(pe is PlannedCopy || pe is PlannedOffset)))
                {
                    layer = cache.Get(db, tr, pe.Capa, pe.Concepto ?? defaultConcept, warnings, result);
                }

                ObjectId id = ObjectId.Null;
                switch (pe)
                {
                    case PlannedPolyline pl:
                        id = DrawPolyline(db, tr, btr, pl, layer!, result);
                        break;
                    case PlannedLine ln:
                        id = DrawLine(db, tr, btr, ln, layer!, result);
                        break;
                    case PlannedCircle ci:
                        id = DrawCircle(db, tr, btr, ci, layer!, result);
                        break;
                    case PlannedText tx:
                        id = DrawText(db, tr, btr, tx, layer!, result);
                        break;
                    case PlannedMText mt:
                        id = DrawMText(db, tr, btr, mt, layer!, result);
                        break;
                    case PlannedHatch ha:
                        {
                            var loops = new List<ObjectId>(ha.IndicesContorno.Count);
                            foreach (int k in ha.IndicesContorno)
                            {
                                if (k < 0 || k >= i || created[k].IsNull)
                                {
                                    throw new InvalidOperationException("plan: el HATCH " + i + " referencia un contorno inexistente (" + k + ").");
                                }

                                loops.Add(created[k]);
                            }

                            if (loops.Count == 0)
                            {
                                throw new InvalidOperationException("plan: el HATCH " + i + " no tiene contornos.");
                            }

                            id = DrawHatch(db, tr, btr, ha, loops, created, layer!, result);
                            break;
                        }

                    case PlannedCopy cp:
                        id = DrawCopy(db, tr, btr, cp, layer, result);
                        break;
                    case PlannedOffset of:
                        id = DrawOffset(db, tr, btr, of, layer, result, warnings);
                        break;
                    case PlannedBlockRef br:
                        // §5G.1: en previsualización el INSERT va a MCP-PREVIEW (su contenido conserva sus capas internas).
                        id = DrawBlockRef(db, tr, btr, br, layer!, result, warnings);
                        break;
                    case PlannedErase er:
                        // §5G.0: la previsualización nunca borra entidades del usuario; el definitivo (o confirmar) sí.
                        id = previewLayer == null ? DrawErase(db, tr, er, result, warnings) : ObjectId.Null;
                        break;
                    case PlannedDimension dm:
                        id = DrawDimension(db, tr, btr, dm, layer!, result, warnings);
                        break;
                    default:
                        throw new InvalidOperationException("plan: tipo de entidad planificada no soportado: " + pe.GetType().Name);
                }

                created.Add(id);
            }

            return result;
        }

        private static void Register(Entity e, string tipo, Result result, Dictionary<string, object?>? extra)
        {
            string handle = AcadUtil.HandleToString(e.Handle);
            result.Handles.Add(handle);
            var d = new Dictionary<string, object?>(StringComparer.Ordinal) { ["handle"] = handle, ["tipo"] = tipo, ["capa"] = e.Layer };
            if (extra != null)
            {
                foreach (var kv in extra)
                {
                    d[kv.Key] = kv.Value;
                }
            }

            d["bbox"] = PrimitiveSupport.Box2D(e);
            result.Entidades.Add(d);
            if (!result.Capas.Contains(e.Layer, StringComparer.OrdinalIgnoreCase))
            {
                result.Capas.Add(e.Layer);
            }
        }

        private static ObjectId DrawPolyline(Database db, Transaction tr, BlockTableRecord btr, PlannedPolyline p, DrawLayerFactory.Result layer, Result result)
        {
            var pl = new Polyline(p.Vertices.Count);
            DrawWrite.ApplyByLayer(pl, db, layer.Id);
            for (int i = 0; i < p.Vertices.Count; i++)
            {
                double bulge = p.Bulges == null ? 0.0 : p.Bulges[i];
                pl.AddVertexAt(i, new Point2d(p.Vertices[i].x, p.Vertices[i].y), bulge, 0, 0);
            }

            pl.Closed = p.Cerrada;
            pl.Elevation = 0;
            pl.Normal = Vector3d.ZAxis;
            pl.ConstantWidth = p.Ancho;
            DrawWrite.Append(btr, tr, pl);
            Register(pl, "LWPOLYLINE", result, new Dictionary<string, object?>
            {
                ["n_vertices"] = pl.NumberOfVertices,
                ["cerrada"] = pl.Closed,
                ["longitud"] = PrimitiveSupport.TryNumber(() => pl.Length),
                ["area"] = p.Cerrada ? PrimitiveSupport.TryNumber(() => pl.Area) : null,
            });
            return pl.ObjectId;
        }

        private static ObjectId DrawLine(Database db, Transaction tr, BlockTableRecord btr, PlannedLine p, DrawLayerFactory.Result layer, Result result)
        {
            var ln = new Line(AcadUtil.ToPoint3d(p.Inicio), AcadUtil.ToPoint3d(p.Fin));
            DrawWrite.ApplyByLayer(ln, db, layer.Id);
            ApplyLook(ln, p.AspectoOriginal, layer);
            DrawWrite.Append(btr, tr, ln);
            Register(ln, "LINE", result, new Dictionary<string, object?> { ["longitud"] = PrimitiveSupport.TryNumber(() => ln.Length) });
            return ln.ObjectId;
        }

        private static ObjectId DrawCircle(Database db, Transaction tr, BlockTableRecord btr, PlannedCircle p, DrawLayerFactory.Result layer, Result result)
        {
            var c = new Circle(AcadUtil.ToPoint3d(p.Centro), Vector3d.ZAxis, p.Radio);
            DrawWrite.ApplyByLayer(c, db, layer.Id);
            DrawWrite.Append(btr, tr, c);
            Register(c, "CIRCLE", result, new Dictionary<string, object?> { ["radio"] = p.Radio, ["centro"] = AcadUtil.ToArray(c.Center) });
            return c.ObjectId;
        }

        private static ObjectId DrawText(Database db, Transaction tr, BlockTableRecord btr, PlannedText p, DrawLayerFactory.Result layer, Result result)
        {
            (ObjectId styleId, string styleName) = PrimitiveSupport.ResolveTextStyle(db, tr, p.Estilo);
            var t = new DBText();
            DrawWrite.ApplyByLayer(t, db, layer.Id);
            t.TextStyleId = styleId;
            t.Height = p.Altura;
            t.Rotation = Angles.ToRadians(Angles.Normalize360(p.RotacionGrados));
            t.TextString = TextEscape.EscapeDbText(TextEscape.CollapseNewlines(p.Texto, out _));
            Point3d point = AcadUtil.ToPoint3d(p.Posicion);
            t.Position = point;
            bool aligned = p.Justificacion != "izquierda";
            if (aligned)
            {
                switch (p.Justificacion)
                {
                    case "centro":
                        t.HorizontalMode = TextHorizontalMode.TextCenter;
                        t.VerticalMode = TextVerticalMode.TextBase;
                        break;
                    case "derecha":
                        t.HorizontalMode = TextHorizontalMode.TextRight;
                        t.VerticalMode = TextVerticalMode.TextBase;
                        break;
                    case "medio_centro":
                        // Rótulos de eje de la oficina (copia_real.dwg: TEXT halign 1 / valign 2): centro geométrico.
                        t.HorizontalMode = TextHorizontalMode.TextCenter;
                        t.VerticalMode = TextVerticalMode.TextVerticalMid;
                        break;
                    default:
                        t.HorizontalMode = TextHorizontalMode.TextMid;
                        t.VerticalMode = TextVerticalMode.TextBase;
                        break;
                }

                t.AlignmentPoint = point;
            }
            else
            {
                t.HorizontalMode = TextHorizontalMode.TextLeft;
                t.VerticalMode = TextVerticalMode.TextBase;
            }

            DrawWrite.Append(btr, tr, t);
            if (aligned)
            {
                t.AdjustAlignment(db);
            }

            Register(t, "TEXT", result, new Dictionary<string, object?>
            {
                ["texto"] = p.Texto,
                ["altura"] = t.Height,
                ["posicion"] = AcadUtil.ToArray(t.Position),
                ["justificacion"] = p.Justificacion,
                ["estilo"] = styleName,
            });
            return t.ObjectId;
        }

        private static readonly string[] Attachments =
        {
            "superior_izquierda", "superior_centro", "superior_derecha",
            "medio_izquierda", "medio_centro", "medio_derecha",
            "inferior_izquierda", "inferior_centro", "inferior_derecha",
        };

        private static readonly AttachmentPoint[] AttachmentValues =
        {
            AttachmentPoint.TopLeft, AttachmentPoint.TopCenter, AttachmentPoint.TopRight,
            AttachmentPoint.MiddleLeft, AttachmentPoint.MiddleCenter, AttachmentPoint.MiddleRight,
            AttachmentPoint.BottomLeft, AttachmentPoint.BottomCenter, AttachmentPoint.BottomRight,
        };

        private static ObjectId DrawMText(Database db, Transaction tr, BlockTableRecord btr, PlannedMText p, DrawLayerFactory.Result layer, Result result)
        {
            (ObjectId styleId, string styleName) = PrimitiveSupport.ResolveTextStyle(db, tr, p.Estilo);
            int idx = Array.IndexOf(Attachments, p.Adjunto);
            if (idx < 0)
            {
                throw new InvalidOperationException("plan: adjunto de MTEXT no válido: " + p.Adjunto);
            }

            var mt = new MText();
            DrawWrite.ApplyByLayer(mt, db, layer.Id);
            mt.TextStyleId = styleId;
            mt.TextHeight = p.Altura;
            mt.Width = p.Ancho;
            mt.Attachment = AttachmentValues[idx];
            mt.Location = AcadUtil.ToPoint3d(p.Posicion);
            mt.Rotation = Angles.ToRadians(Angles.Normalize360(p.RotacionGrados));
            mt.Contents = MTextEscape.Escape(p.Texto);
            DrawWrite.Append(btr, tr, mt);
            Register(mt, "MTEXT", result, new Dictionary<string, object?>
            {
                ["texto"] = p.Texto,
                ["altura"] = mt.TextHeight,
                ["posicion"] = AcadUtil.ToArray(mt.Location),
                ["adjunto"] = p.Adjunto,
                ["estilo"] = styleName,
            });
            return mt.ObjectId;
        }

        private static ObjectId DrawHatch(Database db, Transaction tr, BlockTableRecord btr, PlannedHatch p, List<ObjectId> contornos, List<ObjectId> created, DrawLayerFactory.Result layer, Result result)
        {
            bool solid = string.Equals(p.Patron, "SOLID", StringComparison.OrdinalIgnoreCase);
            string patternName = solid ? "SOLID" : p.Patron.ToUpperInvariant();
            var hatch = new Hatch();
            DrawWrite.Append(btr, tr, hatch);
            DrawWrite.ApplyByLayer(hatch, db, layer.Id);
            ApplyLook(hatch, p.AspectoOriginal, layer);

            // Orden verificado en el gate de Fase 3 (§5D.7): PatternScale → SetHatchPattern → [PatternAngle → SetHatchPattern].
            if (!solid)
            {
                hatch.PatternScale = p.Escala;
            }

            try
            {
                hatch.SetHatchPattern(HatchPatternType.PreDefined, patternName);
            }
            catch (Exception ex) when (!(ex is ToolException))
            {
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "El patrón de sombreado '" + p.Patron + "' no existe.",
                    new Dictionary<string, object?> { ["patron"] = p.Patron, ["detalle"] = ex.GetType().Name + ": " + ex.Message });
            }

            if (!solid && p.AnguloGrados != 0.0)
            {
                hatch.PatternAngle = Angles.ToRadians(p.AnguloGrados);
                hatch.SetHatchPattern(HatchPatternType.PreDefined, patternName);
            }

            var contornoHandles = new List<string>(contornos.Count);
            bool asociativo = true;
            if (p.Bucles != null)
            {
                asociativo = AppendPlannedLoops(db, tr, hatch, p.Bucles, created, IsPreview(layer), contornoHandles);
            }
            else
            {
                hatch.Associative = true;
                foreach (ObjectId contorno in contornos)
                {
                    hatch.AppendLoop(HatchLoopTypes.External, new ObjectIdCollection { contorno });
                    contornoHandles.Add(AcadUtil.HandleToString(contorno.Handle));
                }
            }

            hatch.EvaluateHatch(true);
            Register(hatch, "HATCH", result, new Dictionary<string, object?>
            {
                ["patron"] = patternName,
                ["area"] = PrimitiveSupport.TryNumber(() => hatch.Area),
                ["asociativo"] = asociativo,
                ["contorno_handles"] = contornoHandles,
            });
            return hatch.ObjectId;
        }

        private static bool IsPreview(DrawLayerFactory.Result layer) =>
            string.Equals(layer.Nombre, PreviewStore.PreviewLayer, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// §5G.0 y §5G.1: una entidad que sustituye a otra conserva su aspecto propio. La previsualización no: se distingue
        /// por MCP-PREVIEW.
        /// </summary>
        private static void ApplyLook(Entity e, object? aspectoOriginal, DrawLayerFactory.Result layer)
        {
            if (aspectoOriginal is WallEntitySupport.EntityLook aspecto && !IsPreview(layer))
            {
                e.Color = aspecto.Color;
                e.Transparency = aspecto.Transparencia;
                e.LineWeight = aspecto.Grosor;
                e.Linetype = aspecto.TipoLinea;
                e.LinetypeScale = aspecto.EscalaTipoLinea;
            }
        }

        /// <summary>
        /// Bucles de un sombreado rehecho (§5G.0): entidades nuevas, existentes o vértices. Un bucle por vértices deja el
        /// sombreado sin asociatividad. En previsualización tampoco es asociativo: un reactor en una entidad existente del
        /// usuario la modificaría, y la previsualización no toca nada. Devuelve si quedó asociativo.
        /// </summary>
        private static bool AppendPlannedLoops(
            Database db,
            Transaction tr,
            Hatch hatch,
            IReadOnlyList<PlannedHatchLoop> bucles,
            List<ObjectId> created,
            bool preview,
            List<string> contornoHandles)
        {
            bool asociativo = !preview;
            foreach (PlannedHatchLoop b in bucles)
            {
                asociativo &= b.Vertices == null;
            }

            hatch.Associative = asociativo;
            foreach (PlannedHatchLoop b in bucles)
            {
                if (b.Vertices != null)
                {
                    var puntos = new Point2dCollection();
                    var bulges = new DoubleCollection();
                    for (int i = 0; i < b.Vertices.Count; i++)
                    {
                        puntos.Add(new Point2d(b.Vertices[i].x, b.Vertices[i].y));
                        bulges.Add(b.Bulges == null ? 0.0 : b.Bulges[i]);
                    }

                    hatch.AppendLoop((HatchLoopTypes)b.TipoBucle, puntos, bulges);
                    continue;
                }

                var ids = new ObjectIdCollection();
                foreach (int k in b.Indices)
                {
                    ids.Add(created[k]);
                }

                foreach (string h in b.Handles)
                {
                    Entity e = AcadUtil.TryGetEntityByHandle(db, h, tr) ?? throw ToolException.EntityNotFound(h);
                    ids.Add(e.ObjectId);
                }

                hatch.AppendLoop((HatchLoopTypes)b.TipoBucle, ids);
                foreach (ObjectId id in ids)
                {
                    contornoHandles.Add(AcadUtil.HandleToString(id.Handle));
                }
            }

            return asociativo;
        }

        /// <summary>Affine2 (planta XY) → Matrix3d (z intacta).</summary>
        public static Matrix3d ToMatrix3d(Affine2 m) =>
            new Matrix3d(new[]
            {
                m.A, m.B, 0.0, m.Tx,
                m.C, m.D, 0.0, m.Ty,
                0.0, 0.0, 1.0, 0.0,
                0.0, 0.0, 0.0, 1.0,
            });

        private static ObjectId DrawCopy(Database db, Transaction tr, BlockTableRecord btr, PlannedCopy p, DrawLayerFactory.Result? layer, Result result)
        {
            Entity source = PointRefResolver.Require(db, tr, p.HandleOrigen);
            var ids = new ObjectIdCollection { source.ObjectId };
            var mapping = new IdMapping();
            db.DeepCloneObjects(ids, btr.ObjectId, mapping, false);
            IdPair pair = mapping[source.ObjectId];
            if (!pair.IsCloned || pair.Value.IsNull)
            {
                throw new InvalidOperationException("copia: AutoCAD no clonó la entidad " + p.HandleOrigen + ".");
            }

            var clone = (Entity)tr.GetObject(pair.Value, OpenMode.ForWrite);
            clone.TransformBy(ToMatrix3d(p.Transformacion));
            if (layer != null)
            {
                clone.LayerId = layer.Id;
            }

            Register(clone, AcadUtil.DxfName(clone), result, new Dictionary<string, object?> { ["origen"] = p.HandleOrigen });
            return clone.ObjectId;
        }

        private static ObjectId DrawOffset(Database db, Transaction tr, BlockTableRecord btr, PlannedOffset p, DrawLayerFactory.Result? layer, Result result, List<string> warnings)
        {
            Entity source = PointRefResolver.Require(db, tr, p.HandleOrigen);
            if (source is not Curve curve)
            {
                throw ToolException.InvalidParams("handle: la entidad " + p.HandleOrigen + " (" + AcadUtil.DxfName(source) + ") no es una curva: no admite offset",
                    new Dictionary<string, object?> { ["handle"] = p.HandleOrigen });
            }

            double d = Math.Abs(p.Distancia);
            DBObjectCollection best = BuildOffset(curve, p.HandleOrigen, d, AcadUtil.ToPoint3d(p.LadoPunto))
                ?? throw OffsetNotBuilt(p.HandleOrigen, d);

            ObjectId first = ObjectId.Null;
            int n = 0;
            foreach (DBObject o in best)
            {
                if (o is not Entity e)
                {
                    o.Dispose();
                    continue;
                }

                e.SetDatabaseDefaults(db);
                e.LayerId = layer != null ? layer.Id : source.LayerId;
                e.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByLayer, 256);
                e.Linetype = "ByLayer";
                e.LineWeight = LineWeight.ByLayer;
                DrawWrite.Append(btr, tr, e);
                var extra = new Dictionary<string, object?> { ["origen"] = p.HandleOrigen };
                if (e is Curve ec)
                {
                    extra["longitud"] = PrimitiveSupport.TryNumber(() => ec.GetDistanceAtParameter(ec.EndParam));
                }

                Register(e, AcadUtil.DxfName(e), result, extra);
                if (first.IsNull)
                {
                    first = e.ObjectId;
                }

                n++;
            }

            if (n > 1)
            {
                warnings.Add("offset: la curva paralela a " + p.HandleOrigen + " se creó en " + n + " entidades.");
            }

            return first;
        }

        /// <summary>
        /// Paralela elegida de <paramref name="curve"/> a <paramref name="distancia"/> (§5E.3.4): prueba
        /// GetOffsetCurves(+d) y (−d) y se queda con el candidato más cercano a <paramref name="lado"/>
        /// (<see cref="OffsetChoice.Choose"/>). La usan la LECTURA (OffsetHandler.Plan, para la caja del resultado) y el
        /// DIBUJO (<see cref="DrawOffset"/>): ambos eligen el mismo candidato. Devuelve objetos NO residentes que el
        /// llamador añade a la base de datos o libera con <see cref="DisposeAll"/>; null si no hay resultado.
        /// O3 (2026-10-27): si la elegida no está del lado pedido (a más de distancia/2 del punto lateral esperado,
        /// <see cref="OffsetChoice.LateralPoint"/>: p. ej. hacia dentro de un arco con distancia ≥ radio, donde solo existe
        /// la del otro lado) → INVALID_GEOMETRY "lado: no se puede construir la paralela a &lt;d&gt; del lado pedido (…)".
        /// </summary>
        internal static DBObjectCollection? BuildOffset(Curve curve, string handle, double distancia, Point3d lado)
        {
            double d = Math.Abs(distancia);
            var candidatos = new List<DBObjectCollection?>(2);
            var distancias = new List<double>(2);
            foreach (double sign in new[] { 1.0, -1.0 })
            {
                DBObjectCollection? cand;
                try
                {
                    cand = curve.GetOffsetCurves(sign * d);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    cand = null;
                }

                double dist = double.PositiveInfinity;
                if (cand != null)
                {
                    foreach (DBObject o in cand)
                    {
                        if (o is Curve oc)
                        {
                            try
                            {
                                dist = Math.Min(dist, oc.GetClosestPointTo(lado, false).DistanceTo(lado));
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception)
                            {
                                // Curva sin punto más cercano: no decide.
                            }
                        }
                    }
                }

                candidatos.Add(cand);
                distancias.Add(dist);
            }

            int elegido = OffsetChoice.Choose(distancias);
            for (int i = 0; i < candidatos.Count; i++)
            {
                if (i != elegido)
                {
                    DisposeAll(candidatos[i]);
                }
            }

            if (elegido < 0)
            {
                return null;
            }

            DBObjectCollection best = candidatos[elegido]!;
            CheckRequestedSide(curve, handle, d, lado, best);
            return best;
        }

        /// <summary>O3: la paralela elegida debe pasar a ≤ distancia/2 del punto lateral esperado; si no, libera y lanza.</summary>
        private static void CheckRequestedSide(Curve curve, string handle, double d, Point3d lado, DBObjectCollection best)
        {
            Point3? lateral;
            try
            {
                Point3d proy = curve.GetClosestPointTo(lado, false);
                lateral = OffsetChoice.LateralPoint(new Point3(lado.X, lado.Y, lado.Z), new Point3(proy.X, proy.Y, proy.Z), d);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return;   // sin proyección no se puede comprobar: se conserva la conducta anterior
            }

            if (lateral == null)
            {
                return;   // punto de lado sobre la curva: lado ambiguo
            }

            Point3d lat = new Point3d(lateral.Value.X, lateral.Value.Y, lateral.Value.Z);
            double dist = double.PositiveInfinity;
            foreach (DBObject o in best)
            {
                if (o is Curve oc)
                {
                    try
                    {
                        dist = Math.Min(dist, oc.GetClosestPointTo(lat, false).DistanceTo(lat));
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        // pieza sin punto más cercano
                    }
                }
            }

            if (OffsetChoice.IsOnRequestedSide(dist, d))
            {
                return;
            }

            string motivo = curve switch
            {
                Circle c => OffsetChoice.RadiusReason(c.Radius),
                Arc a => OffsetChoice.RadiusReason(a.Radius),
                _ => OffsetChoice.GenericReason,
            };
            DisposeAll(best);
            throw ToolException.InvalidGeometry(OffsetChoice.WrongSideDetail(d, motivo), new Dictionary<string, object?> { ["handle"] = handle });
        }

        /// <summary>Libera una colección de objetos NO residentes y su contenido.</summary>
        internal static void DisposeAll(DBObjectCollection? objetos)
        {
            if (objetos == null)
            {
                return;
            }

            foreach (DBObject o in objetos)
            {
                o.Dispose();
            }

            objetos.Dispose();
        }

        /// <summary>INVALID_GEOMETRY de §5E.3.4 cuando AutoCAD no construye la paralela.</summary>
        internal static ToolException OffsetNotBuilt(string handle, double distancia) =>
            ToolException.InvalidGeometry(
                "offset: AutoCAD no pudo construir una curva paralela a " + handle + " a distancia " + Math.Abs(distancia).ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Dictionary<string, object?> { ["handle"] = handle });
    }
}
