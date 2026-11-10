using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// Piezas comunes de vano y union_muros (protocol.md §5G.0): lectura del contorno cerrado de un muro existente, sombreados
    /// asociativos (política "rehacer") y comprobación de espacio. Solo lectura: se ejecuta dentro de Plan, en la transacción
    /// de lectura de <see cref="GeneratorBase"/>; los borrados y la geometría nueva los hace <see cref="GeometryDrawer"/>.
    /// </summary>
    internal static class WallEntitySupport
    {
        /// <summary>Parámetro 'confirmar' de vano y union_muros (no confundir con la herramienta confirmar(preview_id)).</summary>
        public const string SchemaConfirmar =
            @"{ ""type"": ""boolean"", ""default"": false, ""description"": ""true = autoriza modificar entidades del usuario (capas que no empiezan por MCP-): el contorno original se borra y se sustituye. Se exige ya en la previsualización. Es distinto de la herramienta confirmar(preview_id), que consolida una previsualización."" }";

        private const double ElevationTol = 1e-9;

        private static readonly RXClass HatchClass = RXObject.GetClass(typeof(Hatch));

        private static readonly RXClass LineClass = RXObject.GetClass(typeof(Line));

        /// <summary>Contorno cerrado y recto de un muro existente, ya sin vértices duplicados.</summary>
        internal sealed record ClosedContour(
            string Handle,
            string Tipo,
            string Capa,
            Entity Entidad,
            IReadOnlyList<(double x, double y)> Vertices,
            AnchorFingerprint Huella);

        /// <summary>Aspecto propio de una entidad que se sustituye, tal como lo leyó el plan (§5G.0, §5G.1).</summary>
        internal sealed record EntityLook(Color Color, Transparency Transparencia, LineWeight Grosor, string TipoLinea, double EscalaTipoLinea)
        {
            public static EntityLook Of(Entity e) => new EntityLook(e.Color, e.Transparency, e.LineWeight, e.Linetype, e.LinetypeScale);
        }

        /// <summary>Cara LINE de un muro (§5G.1), en planta.</summary>
        internal sealed record WallLine(string Handle, string Capa, Entity Entidad, LineWallOpening.Face Cara, EntityLook Aspecto, AnchorFingerprint Huella);

        /// <summary>
        /// Bucle de un sombreado: sus entidades de contorno (<c>Ids</c>; vacío si no tiene), su tipo, sus vértices si se pudieron
        /// leer (cerrados) y su relación con los muros: <c>TocaMuro</c> = alguna entidad es de un muro, <c>SoloMuro</c> = todas.
        /// </summary>
        internal sealed record HatchLoopInfo(
            List<ObjectId> Ids,
            int Tipo,
            List<(double x, double y)>? Vertices,
            List<double>? Bulges,
            bool TocaMuro,
            bool SoloMuro)
        {
            /// <summary>El bucle tal como estaba: por sus entidades o, si no tiene, por sus vértices.</summary>
            public PlannedHatchLoop Conservado()
            {
                var handles = new List<string>(Ids.Count);
                foreach (ObjectId id in Ids)
                {
                    handles.Add(AcadUtil.HandleToString(id.Handle));
                }

                return Ids.Count > 0
                    ? new PlannedHatchLoop(Array.Empty<int>(), handles, Tipo)
                    : new PlannedHatchLoop(Array.Empty<int>(), handles, Tipo, Vertices, Bulges);
            }
        }

        /// <summary>HATCH asociativo que reacciona a un contorno de muro, con lo necesario para rehacerlo.</summary>
        internal sealed record AssociatedHatch(
            ObjectId Id,
            string Handle,
            string Patron,
            double Escala,
            double AnguloGrados,
            string Capa,
            IReadOnlyList<HatchLoopInfo> Bucles,
            bool PatronPredefinido,
            EntityLook Aspecto,
            Entity Entidad)
        {
            /// <summary>true si todos sus bucles son de los muros: se rehace como uno o más sombreados de un bucle.</summary>
            public bool SoloEsteContorno => Bucles.Count > 0 && Bucles.All(b => b.SoloMuro);

            /// <summary>Tipo de bucle de los contornos nuevos.</summary>
            public const int BucleExterior = (int)HatchLoopTypes.External;

            /// <summary>
            /// El HATCH nuevo con <paramref name="bucles"/> explícitos: los contornos que sustituyen al muro y los demás bucles
            /// del original. <paramref name="contornos"/> = entidades nuevas que lo limitan, para R4.
            /// </summary>
            public PlannedHatch ToPlanned(IReadOnlyList<int> contornos, IReadOnlyList<PlannedHatchLoop> bucles) =>
                new PlannedHatch(contornos, Patron, Escala, AnguloGrados, "achurados", Capa) { AspectoOriginal = Aspecto, Bucles = bucles };

            /// <summary>Sustituye por <paramref name="nuevos"/> los bucles del muro y conserva los demás.</summary>
            public PlannedHatch ReplacingWallLoops(params int[] nuevos)
            {
                var bucles = new List<PlannedHatchLoop>();
                foreach (int k in nuevos)
                {
                    bucles.Add(new PlannedHatchLoop(new[] { k }, Array.Empty<string>(), BucleExterior));
                }

                bucles.AddRange(Bucles.Where(b => !b.TocaMuro).Select(b => b.Conservado()));
                return ToPlanned(nuevos, bucles);
            }

            /// <summary>El HATCH nuevo (mismo patrón, escala, ángulo, capa y aspecto propio) sobre los contornos planificados indicados.</summary>
            public PlannedHatch ToPlanned(params int[] indicesContorno) =>
                new PlannedHatch(indicesContorno, Patron, Escala, AnguloGrados, "achurados", Capa) { AspectoOriginal = Aspecto };
        }

        /// <summary>Sombreados que se van a rehacer: sus borrados y sus huellas.</summary>
        internal sealed class HatchRebuild
        {
            public HatchRebuild(List<AssociatedHatch> hatches)
            {
                Hatches = hatches;
                foreach (AssociatedHatch h in hatches)
                {
                    Borrados.Add(new PlannedErase(h.Handle, "HATCH"));
                    Huellas.Add(PointRefResolver.Fingerprint(h.Entidad, h.Handle));
                }
            }

            public List<AssociatedHatch> Hatches { get; }

            public List<PlannedErase> Borrados { get; } = new List<PlannedErase>();

            public List<AnchorFingerprint> Huellas { get; } = new List<AnchorFingerprint>();
        }

        /// <summary>Cara LINE del muro: lee sus extremos y avisa si no está a elevación 0, como un contorno.</summary>
        public static WallLine ReadWallLine(Line l, string handle, string etiqueta, List<string> advertencias)
        {
            double z = Math.Abs(l.StartPoint.Z) > Math.Abs(l.EndPoint.Z) ? l.StartPoint.Z : l.EndPoint.Z;
            if (Math.Abs(z) > ElevationTol)
            {
                advertencias.Add(etiqueta + ": el contorno " + handle + " está a elevación "
                    + z.ToString("0.###", CultureInfo.InvariantCulture) + "; la geometría nueva se crea a elevación 0.");
            }

            var cara = new LineWallOpening.Face(handle, (l.StartPoint.X, l.StartPoint.Y), (l.EndPoint.X, l.EndPoint.Y));
            return new WallLine(handle, l.Layer, l, cara, EntityLook.Of(l), PointRefResolver.Fingerprint(l, handle));
        }

        /// <summary>LINE no borradas de la capa <paramref name="capa"/> en el espacio <paramref name="espacio"/>, para buscar la cara opuesta.</summary>
        public static List<Line> LinesOnLayer(Transaction tr, ObjectId espacio, string capa)
        {
            var lineas = new List<Line>();
            var btr = (BlockTableRecord)tr.GetObject(espacio, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                if (!id.IsErased && id.ObjectClass.IsDerivedFrom(LineClass)
                    && tr.GetObject(id, OpenMode.ForRead) is Line l
                    && string.Equals(l.Layer, capa, StringComparison.OrdinalIgnoreCase))
                {
                    lineas.Add(l);
                }
            }

            return lineas;
        }

        /// <summary>
        /// §5G.1: lo que queda bajo el hueco. Avisa si cae sobre entidades de la capa de columnas (lps.json
        /// capas_preferidas.columnas) y si su caja toca algún HATCH, asociativo o no: los no asociativos no se ven por
        /// reactores y quedarían cruzando el hueco. Salvo <paramref name="excluidos"/>, los sombreados que se rehacen. No
        /// cambia ninguna escritura.
        /// </summary>
        public static void WarnHoleOverlaps(Database db, Transaction tr, ObjectId espacio, WallOpening.OpeningResult hueco, IReadOnlyCollection<string> excluidos, List<string> advertencias)
        {
            string capa = DrawLayerFactory.Plan(null, "columnas").Nombre;
            var capas = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            ObjectId capaId = capas.Has(capa) ? capas[capa] : ObjectId.Null;
            var columnas = new List<string>();
            var sombreados = new List<string>();
            foreach (ObjectId id in (BlockTableRecord)tr.GetObject(espacio, OpenMode.ForRead))
            {
                if (id.IsErased || !(tr.GetObject(id, OpenMode.ForRead) is Entity e))
                {
                    continue;
                }

                bool esColumna = e.LayerId == capaId;
                bool esSombreado = e is Hatch;
                string handle = AcadUtil.HandleToString(e.Handle);
                if ((!esColumna && !esSombreado) || excluidos.Contains(handle))
                {
                    continue;
                }

                BBox2? caja = AcadUtil.TryBBox2(e);
                if (caja == null || !WallOpening.HoleTouches(hueco, caja.Value))
                {
                    continue;
                }

                (esSombreado ? sombreados : columnas).Add(handle);
            }

            if (columnas.Count > 0)
            {
                advertencias.Add(WallOpening.ColumnWarning(capa, columnas));
            }

            if (sombreados.Count > 0)
            {
                advertencias.Add(WallOpening.HatchesCrossingWarning(sombreados));
            }
        }

        /// <summary>
        /// Vértices de la LWPOLYLINE cerrada <paramref name="handle"/> (§5G.1): otra entidad o polilínea abierta →
        /// INVALID_PARAMS "&lt;etiqueta&gt;: la entidad &lt;h&gt; no es una polilínea cerrada (&lt;TIPO&gt;)"; con arcos → INVALID_PARAMS
        /// "&lt;etiqueta&gt;: la polilínea &lt;h&gt; tiene arcos; solo se admiten contornos rectos" (ambos con data.handle). Los
        /// vértices consecutivos duplicados se colapsan con la advertencia de §5E.3.3. GeometryDrawer dibuja a elevación 0:
        /// si el contorno no lo está, se avisa porque el resultado baja a Z = 0.
        /// </summary>
        public static ClosedContour ReadClosedContour(Database db, Transaction tr, string handle, string etiqueta, List<string> advertencias)
        {
            Entity e = PointRefResolver.Require(db, tr, handle);
            string tipo = AcadUtil.DxfName(e);
            var data = new Dictionary<string, object?> { ["handle"] = handle };
            AxisReader.Vertices? read = e is Polyline ? AxisReader.Read(e, tr) : null;
            if (read == null || !read.Cerrada)
            {
                throw ToolException.InvalidParams(etiqueta + ": la entidad " + handle + " no es una polilínea cerrada (" + tipo + ")", data);
            }

            if (read.TieneArcos)
            {
                throw ToolException.InvalidParams(etiqueta + ": la polilínea " + handle + " tiene arcos; solo se admiten contornos rectos", data);
            }

            List<(double x, double y)> vertices = PolygonMetrics.CollapseConsecutiveDuplicates(read.Puntos, out int colapsados, lazoCerrado: true);
            if (colapsados > 0)
            {
                advertencias.Add(PolygonMetrics.CollapsedDuplicatesWarning(handle, colapsados));
            }

            if (Math.Abs(read.Z) > ElevationTol)
            {
                advertencias.Add(etiqueta + ": el contorno " + handle + " está a elevación "
                    + read.Z.ToString("0.###", CultureInfo.InvariantCulture) + "; la geometría nueva se crea a elevación 0.");
            }

            return new ClosedContour(handle, tipo, e.Layer, e, vertices, PointRefResolver.Fingerprint(e, handle));
        }

        /// <summary>
        /// HATCH asociativos no borrados que reaccionan a <paramref name="contornos"/> (GetPersistentReactorIds), sin repetir y
        /// en el orden de los contornos, con sus bucles (GetAssociatedObjectIdsAt) clasificados respecto a esos contornos.
        /// </summary>
        public static List<AssociatedHatch> AssociatedHatches(Transaction tr, params Entity[] contornos)
        {
            var propios = new HashSet<ObjectId>();
            foreach (Entity c in contornos)
            {
                propios.Add(c.ObjectId);
            }

            var list = new List<AssociatedHatch>();
            var seen = new HashSet<ObjectId>();
            foreach (Entity c in contornos)
            {
                ObjectIdCollection? reactors = c.GetPersistentReactorIds();
                if (reactors == null)
                {
                    continue;
                }

                foreach (ObjectId r in reactors)
                {
                    if (r.IsNull || r.IsErased || !r.ObjectClass.IsDerivedFrom(HatchClass) || !seen.Add(r))
                    {
                        continue;
                    }

                    var h = (Hatch)tr.GetObject(r, OpenMode.ForRead);
                    if (h.Associative)
                    {
                        list.Add(new AssociatedHatch(
                            r,
                            AcadUtil.HandleToString(h.Handle),
                            h.PatternName,
                            h.PatternScale,
                            AcadUtil.ToDegrees360(h.PatternAngle),
                            h.Layer,
                            ReadLoops(h, propios),
                            h.PatternType == HatchPatternType.PreDefined && !h.IsGradient,
                            EntityLook.Of(h),
                            h));
                    }
                }
            }

            return list;
        }

        private static List<HatchLoopInfo> ReadLoops(Hatch h, HashSet<ObjectId> propios)
        {
            var bucles = new List<HatchLoopInfo>();
            int n = h.NumberOfLoops;
            for (int i = 0; i < n; i++)
            {
                var ids = new List<ObjectId>();
                foreach (ObjectId id in h.GetAssociatedObjectIdsAt(i) ?? new ObjectIdCollection())
                {
                    ids.Add(id);
                }

                int propias = ids.Count(propios.Contains);
                (List<(double x, double y)>? vertices, List<double>? bulges) = ids.Count == 0 ? ReadLoopVertices(h, i) : (null, null);
                bucles.Add(new HatchLoopInfo(ids, LoopKind(h.LoopTypeAt(i)), vertices, bulges, propias > 0, ids.Count > 0 && propias == ids.Count));
            }

            return bucles;
        }

        /// <summary>Del tipo de bucle solo cuenta su papel en el sombreado; las marcas de cómo se construyó no se reescriben.</summary>
        private static int LoopKind(HatchLoopTypes tipo)
        {
            HatchLoopTypes papel = tipo & (HatchLoopTypes.External | HatchLoopTypes.Outermost);
            return (int)(papel == 0 ? HatchLoopTypes.Default : papel);
        }

        /// <summary>Vértices de un bucle sin entidad de contorno, cerrados; (null, null) si tiene curvas que no son rectas o polilínea.</summary>
        private static (List<(double x, double y)>?, List<double>?) ReadLoopVertices(Hatch h, int i)
        {
            var puntos = new List<(double x, double y)>();
            var bulges = new List<double>();
            try
            {
                HatchLoop bucle = h.GetLoopAt(i);
                if (bucle.IsPolyline)
                {
                    foreach (BulgeVertex v in bucle.Polyline)
                    {
                        puntos.Add((v.Vertex.X, v.Vertex.Y));
                        bulges.Add(v.Bulge);
                    }
                }
                else
                {
                    foreach (Curve2d c in bucle.Curves)
                    {
                        if (c is not LineSegment2d s)
                        {
                            return (null, null);
                        }

                        puntos.Add((s.StartPoint.X, s.StartPoint.Y));
                        bulges.Add(0.0);
                    }
                }
            }
            catch (System.Exception)
            {
                return (null, null);
            }

            if (puntos.Count < 3)
            {
                return (null, null);
            }

            if (puntos[0] != puntos[puntos.Count - 1])
            {
                puntos.Add(puntos[0]);
                bulges.Add(0.0);
            }

            return (puntos, bulges);
        }

        /// <summary>Entidades del bucle ajenas a <paramref name="muros"/>, con sus extremos; null si alguna no es una curva.</summary>
        public static List<(string Handle, (double x, double y) P, (double x, double y) Q)>? OtherLoopCurves(Transaction tr, HatchLoopInfo bucle, IReadOnlyCollection<ObjectId> muros)
        {
            var otras = new List<(string Handle, (double x, double y) P, (double x, double y) Q)>();
            foreach (ObjectId id in bucle.Ids)
            {
                if (muros.Contains(id))
                {
                    continue;
                }

                if (!(tr.GetObject(id, OpenMode.ForRead) is Curve c))
                {
                    return null;
                }

                otras.Add((AcadUtil.HandleToString(id.Handle), (c.StartPoint.X, c.StartPoint.Y), (c.EndPoint.X, c.EndPoint.Y)));
            }

            return otras;
        }

        /// <summary>
        /// Política "rehacer" de §5G.0: sustituye los bucles del muro y conserva los demás. Un bucle que no se puede reconstruir
        /// (combina el muro con otras entidades, o no tiene entidades ni vértices legibles) deja el sombreado sin rehacer con
        /// una advertencia, nunca en silencio; tampoco se rehace uno degradado o de patrón no predefinido, que
        /// GeometryDrawer.DrawHatch no sabe recrear. <paramref name="bucleDelMuro"/> decide los bucles que tocan el muro cuando
        /// la herramienta sabe repartirlos (caras LINE); sin él, solo valen los que son enteramente del muro. Los que se
        /// borran pasan por <see cref="HandleGuard.Resolve"/> (también son entidades del usuario: capa bloqueada, confirmar)
        /// y se valida que su capa admita el HATCH nuevo: mejor rechazar aquí que dentro de la escritura.
        /// </summary>
        public static HatchRebuild PlanHatchRebuild(
            Database db,
            Transaction tr,
            List<AssociatedHatch> hatches,
            bool confirmar,
            List<string> advertencias,
            Func<HatchLoopInfo, bool>? bucleDelMuro = null)
        {
            var rehacer = new List<AssociatedHatch>();
            var handles = new List<string>();
            foreach (AssociatedHatch h in hatches)
            {
                if (!h.PatronPredefinido)
                {
                    advertencias.Add("El sombreado " + h.Handle + " no usa un patrón predefinido; no se rehízo: revíselo.");
                }
                else if (!h.Bucles.All(b => Rebuildable(b, bucleDelMuro)))
                {
                    advertencias.Add("El sombreado " + h.Handle + " tiene un bucle que no se puede reconstruir; no se rehízo y puede quedar cruzando el hueco o la unión: revíselo.");
                }
                else
                {
                    rehacer.Add(h);
                    handles.Add(h.Handle);
                    if (h.Bucles.Any(b => !b.TocaMuro && b.Ids.Count == 0))
                    {
                        advertencias.Add("El sombreado " + h.Handle + " se rehízo sin asociatividad: uno de sus bucles no tiene entidad de contorno.");
                    }
                }
            }

            if (handles.Count > 0)
            {
                HandleGuard.Resolve(db, tr, handles, confirmar, advertencias);
                foreach (AssociatedHatch h in rehacer)
                {
                    DrawLayerFactory.Check(db, tr, h.Capa, "achurados", out _);
                }
            }

            var plan = new HatchRebuild(rehacer);
            foreach (AssociatedHatch h in rehacer)
            {
                foreach (HatchLoopInfo b in h.Bucles.Where(b => !b.TocaMuro))
                {
                    foreach (ObjectId id in b.Ids)
                    {
                        plan.Huellas.Add(PointRefResolver.Fingerprint((Entity)tr.GetObject(id, OpenMode.ForRead), AcadUtil.HandleToString(id.Handle)));
                    }
                }
            }

            return plan;
        }

        private static bool Rebuildable(HatchLoopInfo b, Func<HatchLoopInfo, bool>? bucleDelMuro)
        {
            if (b.TocaMuro)
            {
                return bucleDelMuro != null ? bucleDelMuro(b) : b.SoloMuro;
            }

            return b.Ids.Count > 0 || b.Vertices != null;
        }

        /// <summary>
        /// El destino de la geometría debe ser el espacio del muro: <paramref name="espacio"/> null es el espacio activo, que
        /// también puede no serlo. Distinto → INVALID_PARAMS "espacio: el muro &lt;h&gt; está en '&lt;e&gt;'" (data.handle).
        /// </summary>
        public static void CheckSpace(Database db, Transaction tr, HandleGuard.Resolved muro, string? espacio)
        {
            string destino = SpaceResolver.Resolve(db, tr, espacio).Name;
            if (!string.Equals(destino, muro.Espacio, StringComparison.OrdinalIgnoreCase))
            {
                string handle = muro.Items[0].Info.Handle;
                throw ToolException.InvalidParams("espacio: el muro " + handle + " está en '" + muro.Espacio + "'",
                    new Dictionary<string, object?> { ["handle"] = handle });
            }
        }

        /// <summary>WallGeometryException / ArgumentException de GeometryCore → INVALID_GEOMETRY / INVALID_PARAMS con su texto.</summary>
        public static T Geometry<T>(Func<T> calcular)
        {
            try
            {
                return calcular();
            }
            catch (WallGeometryException ex)
            {
                throw ex.EsParametro ? ToolException.InvalidParams(ex.Message) : ToolException.InvalidGeometry(ex.Message);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }
        }
    }
}
