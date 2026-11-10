using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// Formatos de entidad de protocol.md §5B.0 (compacto) y §5B.2 (completo). Devuelve diccionarios con los nombres
    /// JSON exactos del contrato. Cada propiedad opcional se lee dentro de try/catch: si AutoCAD lanza, el campo va
    /// null y la entidad se devuelve igual. Solo lectura (entidades abiertas ForRead); solo hilo principal.
    /// </summary>
    internal static class EntityFormatter
    {
        /// <summary>Máximo de vértices que el formato compacto devuelve en <c>vertices</c> (§5B.0).</summary>
        public const int CompactMaxVertices = 50;

        /// <summary>Formato compacto §5B.0: campos comunes + campos del tipo (nada más).</summary>
        public static Dictionary<string, object?> Compact(Entity e, Transaction tr) => Build(e, tr, CompactMaxVertices, full: false);

        /// <summary>Formato completo §5B.2: compacto sin límite de vértices + propiedades generales + extras por tipo.</summary>
        public static Dictionary<string, object?> Full(Entity e, Transaction tr) => Build(e, tr, int.MaxValue, full: true);

        /// <summary>Fila mínima si una entidad no se puede formatear (nunca se pierde la entidad).</summary>
        public static Dictionary<string, object?> Minimal(ObjectId id)
        {
            string handle = "";
            string? tipo = null;
            try
            {
                handle = AcadUtil.HandleToString(id.Handle);
                tipo = AcadUtil.DxfNameFromId(id);
            }
            catch (Exception)
            {
                // Se devuelve lo que haya.
            }

            return new Dictionary<string, object?>
            {
                ["handle"] = handle,
                ["tipo"] = tipo ?? "",
                ["capa"] = null,
                ["bbox"] = null,
            };
        }

        private static Dictionary<string, object?> Build(Entity e, Transaction tr, int maxVertices, bool full)
        {
            string tipo = AcadUtil.DxfName(e);
            var d = new Dictionary<string, object?>
            {
                ["handle"] = Try(() => AcadUtil.HandleToString(e.Handle)) ?? "",
                ["tipo"] = tipo,
                ["capa"] = Try(() => e.Layer),
                ["bbox"] = AcadUtil.ExtentsOrNull(e),
            };

            switch (tipo)
            {
                case "LINE":
                    if (e is Line line)
                    {
                        d["inicio"] = Try(() => AcadUtil.PointOrNull(line.StartPoint));
                        d["fin"] = Try(() => AcadUtil.PointOrNull(line.EndPoint));
                        d["longitud"] = Num(() => line.Length);
                    }

                    break;

                case "LWPOLYLINE":
                    if (e is Polyline lw)
                    {
                        AddLwPolyline(d, lw, maxVertices, full);
                    }

                    break;

                case "POLYLINE":
                    if (e is Polyline2d p2d)
                    {
                        AddPolyline2d(d, p2d, tr, maxVertices, full);
                    }
                    else if (e is Polyline3d p3d)
                    {
                        AddPolyline3d(d, p3d, tr, maxVertices, full);
                    }

                    // PolyFaceMesh / PolygonMesh: solo campos comunes.
                    break;

                case "CIRCLE":
                    if (e is Circle c)
                    {
                        d["centro"] = Try(() => AcadUtil.PointOrNull(c.Center));
                        d["radio"] = Num(() => c.Radius);
                        d["longitud"] = Num(() => c.Circumference);
                        d["area"] = Num(() => c.Area);
                    }

                    break;

                case "ARC":
                    if (e is Arc a)
                    {
                        d["centro"] = Try(() => AcadUtil.PointOrNull(a.Center));
                        d["radio"] = Num(() => a.Radius);
                        d["angulo_inicio"] = Num(() => AcadUtil.ToDegrees360(a.StartAngle));
                        d["angulo_fin"] = Num(() => AcadUtil.ToDegrees360(a.EndAngle));
                        d["longitud"] = Num(() => a.Length);
                    }

                    break;

                case "TEXT":
                    if (e is DBText t)
                    {
                        d["texto"] = Try(() => t.TextString);
                        d["altura"] = Num(() => t.Height);
                        d["posicion"] = Try(() => AcadUtil.PointOrNull(t.Position));
                        d["rotacion"] = Num(() => AcadUtil.ToDegrees(t.Rotation));
                        if (full)
                        {
                            d["estilo"] = Try(() => t.TextStyleName);
                            d["justificacion"] = Try(() => t.Justify.ToString());
                            d["punto_alineacion"] = Try(() => AcadUtil.PointOrNull(t.AlignmentPoint));
                        }
                    }

                    break;

                case "MTEXT":
                    if (e is MText mt)
                    {
                        d["texto"] = Try(() => mt.Text);
                        d["altura"] = Num(() => mt.TextHeight);
                        d["posicion"] = Try(() => AcadUtil.PointOrNull(mt.Location));
                        d["ancho"] = Num(() => mt.Width);
                        d["rotacion"] = Num(() => AcadUtil.ToDegrees(mt.Rotation));
                        if (full)
                        {
                            d["contenido_formato"] = Try(() => mt.Contents);
                            d["estilo"] = Try(() => mt.TextStyleName);
                            d["justificacion"] = Try(() => mt.Attachment.ToString());
                            d["interlineado"] = Num(() => mt.LineSpacingFactor);
                        }
                    }

                    break;

                case "INSERT":
                    if (e is BlockReference br)
                    {
                        AddInsert(d, br, tr, full);
                    }

                    break;

                case "DIMENSION":
                    if (e is Dimension dim)
                    {
                        AddDimension(d, dim, full);
                    }

                    break;

                case "HATCH":
                    if (e is Hatch h)
                    {
                        d["patron"] = Try(() => h.PatternName);
                        d["area"] = Num(() => h.Area);
                        d["n_bucles"] = Try<object?>(() => h.NumberOfLoops);
                        d["solido"] = Try<object?>(() => h.IsSolidFill);
                        if (full)
                        {
                            d["asociativo"] = Try<object?>(() => h.Associative);
                            d["escala"] = Num(() => h.PatternScale);
                            d["angulo"] = Num(() => AcadUtil.ToDegrees(h.PatternAngle));
                        }
                    }

                    break;

                case "ATTRIB":
                    if (full && e is AttributeReference attr)
                    {
                        AddAttributeExtras(d, attr, tr);
                    }

                    break;

                case "ELLIPSE":
                case "SPLINE":
                    if (full && e is Curve curve)
                    {
                        d["longitud"] = Num(() => CurveLength(curve));
                    }

                    break;
            }

            if (full)
            {
                AddGeneral(d, e, tr);
            }

            return d;
        }

        // ------------------------------------------------------------------ tipos

        private static void AddLwPolyline(Dictionary<string, object?> d, Polyline pl, int maxVertices, bool full)
        {
            int n = Try<int?>(() => pl.NumberOfVertices) ?? 0;
            bool closed = Try<bool?>(() => pl.Closed) ?? false;
            d["n_vertices"] = n;
            d["cerrada"] = closed;
            d["longitud"] = Num(() => pl.Length);
            d["area"] = closed ? Num(() => pl.Area) : null;

            bool hasArcs = false;
            var bulges = new List<object?>(n);
            for (int i = 0; i < n; i++)
            {
                double b = Try<double?>(() => pl.GetBulgeAt(i)) ?? 0.0;
                if (b != 0.0)
                {
                    hasArcs = true;
                }

                bulges.Add(AcadUtil.NumberOrNull(b));
            }

            d["tiene_arcos"] = hasArcs;

            if (n <= maxVertices)
            {
                var vertices = new List<double[]?>(n);
                for (int i = 0; i < n; i++)
                {
                    int idx = i;
                    // GetPoint3dAt devuelve WCS; con normal (0,0,1) su z es la elevación.
                    vertices.Add(Try(() => AcadUtil.PointOrNull(pl.GetPoint3dAt(idx))));
                }

                d["vertices"] = vertices;
            }
            else
            {
                d["vertices"] = null;
            }

            if (full)
            {
                d["bulges"] = bulges;
                d["ancho_constante"] = Num(() => pl.ConstantWidth);
                d["elevacion"] = Num(() => pl.Elevation);
            }
        }

        private static void AddPolyline2d(Dictionary<string, object?> d, Polyline2d pl, Transaction tr, int maxVertices, bool full)
        {
            var points = new List<double[]?>();
            var bulges = new List<object?>();
            bool hasArcs = false;
            try
            {
                foreach (ObjectId vid in pl)
                {
                    try
                    {
                        if (!(tr.GetObject(vid, OpenMode.ForRead, false) is Vertex2d v))
                        {
                            continue;
                        }

                        if (v.VertexType == Vertex2dType.SplineControlVertex)
                        {
                            continue;
                        }

                        double[]? pt;
                        try
                        {
                            pt = AcadUtil.PointOrNull(pl.VertexPosition(v));
                        }
                        catch (Exception)
                        {
                            pt = AcadUtil.PointOrNull(v.Position);
                        }

                        points.Add(pt);
                        double b = v.Bulge;
                        if (b != 0.0)
                        {
                            hasArcs = true;
                        }

                        bulges.Add(AcadUtil.NumberOrNull(b));
                    }
                    catch (Exception)
                    {
                        // Vértice ilegible: se omite.
                    }
                }
            }
            catch (Exception)
            {
                // Iteración fallida: se devuelve lo leído.
            }

            bool closed = Try<bool?>(() => pl.Closed) ?? false;
            d["n_vertices"] = points.Count;
            d["cerrada"] = closed;
            d["longitud"] = Num(() => pl.Length);
            d["area"] = closed ? Num(() => pl.Area) : null;
            d["tiene_arcos"] = hasArcs;
            d["vertices"] = points.Count <= maxVertices ? points : null;
            if (full)
            {
                d["es_3d"] = false;
                d["bulges"] = bulges;
            }
        }

        private static void AddPolyline3d(Dictionary<string, object?> d, Polyline3d pl, Transaction tr, int maxVertices, bool full)
        {
            var points = new List<double[]?>();
            try
            {
                foreach (ObjectId vid in pl)
                {
                    try
                    {
                        if (tr.GetObject(vid, OpenMode.ForRead, false) is PolylineVertex3d v && v.VertexType != Vertex3dType.ControlVertex)
                        {
                            points.Add(AcadUtil.PointOrNull(v.Position));
                        }
                    }
                    catch (Exception)
                    {
                        // Vértice ilegible: se omite.
                    }
                }
            }
            catch (Exception)
            {
                // Iteración fallida: se devuelve lo leído.
            }

            bool closed = Try<bool?>(() => pl.Closed) ?? false;
            d["n_vertices"] = points.Count;
            d["cerrada"] = closed;
            d["longitud"] = Num(() => pl.Length);
            d["area"] = closed ? Num(() => pl.Area) : null;
            d["tiene_arcos"] = false;
            d["vertices"] = points.Count <= maxVertices ? points : null;
            if (full)
            {
                d["es_3d"] = true;
                d["bulges"] = null;
            }
        }

        private static void AddInsert(Dictionary<string, object?> d, BlockReference br, Transaction tr, bool full)
        {
            bool dynamic = Try<bool?>(() => br.IsDynamicBlock) ?? false;
            string? realName = Try(() => BlockName(br.BlockTableRecord, tr));
            string? effective = realName;
            if (dynamic)
            {
                effective = Try(() => BlockName(br.DynamicBlockTableRecord, tr)) ?? realName;
            }

            d["nombre"] = effective;
            d["posicion"] = Try(() => AcadUtil.PointOrNull(br.Position));
            d["escala"] = Try(() =>
            {
                Scale3d s = br.ScaleFactors;
                return double.IsFinite(s.X) && double.IsFinite(s.Y) && double.IsFinite(s.Z) ? new[] { s.X, s.Y, s.Z } : null;
            });
            d["rotacion"] = Num(() => AcadUtil.ToDegrees(br.Rotation));
            d["n_atributos"] = Try<int?>(() => br.AttributeCollection.Count) ?? 0;
            d["dinamico"] = dynamic;

            if (full)
            {
                d["nombre_bloque_real"] = realName;
                var attributes = new List<Dictionary<string, object?>>();
                foreach (AttributeReference attr in Attributes(br, tr))
                {
                    attributes.Add(new Dictionary<string, object?>
                    {
                        ["handle"] = Try(() => AcadUtil.HandleToString(attr.Handle)),
                        ["tag"] = Try(() => attr.Tag),
                        ["valor"] = Try(() => AttributeText(attr)),
                        ["capa"] = Try(() => attr.Layer),
                        ["invisible"] = Try<bool?>(() => attr.Invisible) ?? false,
                    });
                }

                d["atributos"] = attributes;
            }
        }

        private static void AddDimension(Dictionary<string, object?> d, Dimension dim, bool full)
        {
            d["subtipo"] = dim.GetType().Name;
            d["medida"] = Num(() => dim.Measurement);
            d["texto"] = Try(() => dim.DimensionText) ?? "";
            d["estilo"] = Try(() => dim.DimensionStyleName);
            if (!full)
            {
                return;
            }

            var points = new List<double[]?>();
            try
            {
                switch (dim)
                {
                    case AlignedDimension al:
                        points.Add(AcadUtil.PointOrNull(al.XLine1Point));
                        points.Add(AcadUtil.PointOrNull(al.XLine2Point));
                        points.Add(AcadUtil.PointOrNull(al.DimLinePoint));
                        break;
                    case RotatedDimension ro:
                        points.Add(AcadUtil.PointOrNull(ro.XLine1Point));
                        points.Add(AcadUtil.PointOrNull(ro.XLine2Point));
                        points.Add(AcadUtil.PointOrNull(ro.DimLinePoint));
                        break;
                    case RadialDimension ra:
                        points.Add(AcadUtil.PointOrNull(ra.Center));
                        points.Add(AcadUtil.PointOrNull(ra.ChordPoint));
                        break;
                    case DiametricDimension di:
                        // DiametricDimension no expone Center: se dan los dos extremos del diámetro.
                        points.Add(AcadUtil.PointOrNull(di.ChordPoint));
                        points.Add(AcadUtil.PointOrNull(di.FarChordPoint));
                        break;
                }
            }
            catch (Exception)
            {
                points.Clear();
            }

            d["puntos_definicion"] = points;
            d["posicion_texto"] = Try(() => AcadUtil.PointOrNull(dim.TextPosition));
        }

        private static void AddAttributeExtras(Dictionary<string, object?> d, AttributeReference attr, Transaction tr)
        {
            d["tag"] = Try(() => attr.Tag);
            d["valor"] = Try(() => AttributeText(attr));
            d["invisible"] = Try<bool?>(() => attr.Invisible) ?? false;
            string? insertHandle = null;
            string? block = null;
            try
            {
                if (tr.GetObject(attr.OwnerId, OpenMode.ForRead, false) is BlockReference owner)
                {
                    insertHandle = AcadUtil.HandleToString(owner.Handle);
                    block = EffectiveBlockName(owner, tr);
                }
            }
            catch (Exception)
            {
                // Propietario ilegible.
            }

            d["insert_handle"] = insertHandle;
            d["bloque"] = block;
        }

        /// <summary>Propiedades generales de §5B.2 (propietario, color, tipo de línea, grosor, transparencia...).</summary>
        private static void AddGeneral(Dictionary<string, object?> d, Entity e, Transaction tr)
        {
            d["propietario"] = SpaceResolver.OwnerName(e, tr);
            d["color"] = Try<int?>(() => e.Color.ColorIndex);
            d["color_true"] = Try(() => e.Color.IsByColor
                ? string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", e.Color.Red, e.Color.Green, e.Color.Blue)
                : null);
            d["tipo_linea"] = Try(() => e.Linetype);
            d["escala_tipo_linea"] = Num(() => e.LinetypeScale);
            d["grosor"] = Try<int?>(() => (int)e.LineWeight);
            d["transparencia"] = Try<int?>(() =>
            {
                Autodesk.AutoCAD.Colors.Transparency tp = e.Transparency;
                if (!tp.IsByAlpha)
                {
                    return null;
                }

                return (int)Math.Round((255 - tp.Alpha) / 255.0 * 100.0, MidpointRounding.AwayFromZero);
            });
            d["visible"] = Try<bool?>(() => e.Visible);
            d["espacio_papel"] = SpaceResolver.IsInPaperSpace(e, tr);
        }

        // ------------------------------------------------------------------ bloques / atributos

        /// <summary>Atributos del INSERT, en orden de la colección, abiertos ForRead (ilegibles se omiten).</summary>
        public static List<AttributeReference> Attributes(BlockReference br, Transaction tr)
        {
            var list = new List<AttributeReference>();
            try
            {
                foreach (ObjectId id in br.AttributeCollection)
                {
                    try
                    {
                        if (id.IsNull || id.IsErased)
                        {
                            continue;
                        }

                        if (tr.GetObject(id, OpenMode.ForRead, false) is AttributeReference attr)
                        {
                            list.Add(attr);
                        }
                    }
                    catch (Exception)
                    {
                        // Atributo ilegible: se omite.
                    }
                }
            }
            catch (Exception)
            {
                // Colección ilegible.
            }

            return list;
        }

        /// <summary>Texto del atributo (sin códigos de formato si es multilínea).</summary>
        public static string AttributeText(AttributeReference attr)
        {
            try
            {
                if (attr.IsMTextAttribute && attr.MTextAttribute != null)
                {
                    return attr.MTextAttribute.Text;
                }
            }
            catch (Exception)
            {
                // Se usa TextString.
            }

            return attr.TextString;
        }

        /// <summary>Nombre efectivo del bloque del INSERT (§5B.0): DynamicBlockTableRecord si es dinámico.</summary>
        public static string? EffectiveBlockName(BlockReference br, Transaction tr)
        {
            try
            {
                ObjectId id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                return BlockName(id, tr);
            }
            catch (Exception)
            {
                return Try(() => BlockName(br.BlockTableRecord, tr));
            }
        }

        private static string? BlockName(ObjectId btrId, Transaction tr)
        {
            if (btrId.IsNull)
            {
                return null;
            }

            return tr.GetObject(btrId, OpenMode.ForRead, false) is BlockTableRecord btr ? btr.Name : null;
        }

        private static double CurveLength(Curve c) =>
            c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam);

        // ------------------------------------------------------------------ helpers

        private static T? Try<T>(Func<T?> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return default;
            }
        }

        private static object? Num(Func<double> read)
        {
            try
            {
                return AcadUtil.NumberOrNull(read());
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
