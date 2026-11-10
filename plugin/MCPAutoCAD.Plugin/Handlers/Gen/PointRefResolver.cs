using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// Resolución de PuntoRef (protocol.md §5E.1) a coordenadas WCS: SOLO LECTURA, en una transacción de lectura del
    /// llamador (StartOpenCloseTransaction, fuera de todo UndoGroup). Solo hilo principal. Reglas:
    /// <list type="bullet">
    /// <item>inicio/fin/medio: cualquier <c>Curve</c> (LINE, LWPOLYLINE, POLYLINE, ARC, CIRCLE, ELLIPSE, SPLINE…):
    /// StartPoint / EndPoint / GetPointAtDist(longitud/2). Otra entidad → INVALID_PARAMS.</item>
    /// <item>centro: Circle/Arc/Ellipse → Center; cualquier otra entidad → centro de su caja (con advertencia).</item>
    /// <item>vertice: Polyline/Polyline2d/Polyline3d → vértice <c>indice</c> (defecto 0); LINE → 0 = inicio, 1 = fin;
    /// otra → INVALID_PARAMS. Índice fuera de rango → INVALID_PARAMS con el número de vértices.</item>
    /// <item>interseccion_con: Curve.IntersectWith(OnBothOperands) entre dos curvas; 0 → INVALID_GEOMETRY; varias sin
    /// cerca_de → INVALID_PARAMS listando hasta 5; con cerca_de se toma la más cercana.</item>
    /// </list>
    /// Cada punto resuelto lleva la huella de su ancla (<see cref="AnchorFingerprint"/>) para que <c>confirmar</c> detecte cambios.
    /// </summary>
    internal static class PointRefResolver
    {
        public const string NoCurveDetail = "no es una curva: no tiene inicio, fin ni medio";

        /// <summary>Resuelve una lista de PuntoRef en el orden dado (los índices de los mensajes son los de <paramref name="paths"/>).</summary>
        public static List<ResolvedPoint> ResolveAll(Database db, Transaction tr, IReadOnlyList<PointRef> refs, IReadOnlyList<string> paths, List<string> advertencias)
        {
            var list = new List<ResolvedPoint>(refs.Count);
            for (int i = 0; i < refs.Count; i++)
            {
                list.Add(Resolve(db, tr, refs[i], paths[i], advertencias));
            }

            return list;
        }

        public static ResolvedPoint Resolve(Database db, Transaction tr, PointRef r, string path, List<string> advertencias)
        {
            switch (r.Kind)
            {
                case PointRefKind.Coordenada:
                    return new ResolvedPoint(r, r.Coordenada!.Value, null);
                case PointRefKind.PuntoDeEntidad:
                    {
                        Entity e = Require(db, tr, r.Handle!);
                        Point3 p = EntityPoint(e, r, path, advertencias);
                        return new ResolvedPoint(r, p, Fingerprint(e, r.Handle!));
                    }
                default:
                    {
                        Entity a = Require(db, tr, r.Handle!);
                        Entity b = Require(db, tr, r.Handle2!);
                        Point3 p = Intersection(a, b, r, path);
                        // Ancla compuesta: la huella de ambas curvas, separadas por '+'.
                        AnchorFingerprint fa = Fingerprint(a, r.Handle!);
                        AnchorFingerprint fb = Fingerprint(b, r.Handle2!);
                        var anchor = new AnchorFingerprint(fa.Handle + "+" + fb.Handle, fa.Tipo + "+" + fb.Tipo, fa.Firma + "+" + fb.Firma);
                        return new ResolvedPoint(r, p, anchor);
                    }
            }
        }

        /// <summary>Entidad viva por handle o NOT_FOUND (§6).</summary>
        public static Entity Require(Database db, Transaction tr, string handle) =>
            AcadUtil.TryGetEntityByHandle(db, handle, tr) ?? throw ToolException.EntityNotFound(handle);

        /// <summary>
        /// Huella actual de una entidad: tipo + caja + extremos y longitud si es curva (GetDistanceAtParameter(EndParam))
        /// + número de vértices si es polilínea (LWPOLYLINE, POLYLINE 2D/3D). La longitud y los vértices detectan la
        /// edición de un vértice interior que no cambia ni la caja ni los extremos.
        /// </summary>
        public static AnchorFingerprint Fingerprint(Entity e, string handle)
        {
            Point3? s = null, f = null;
            double? longitud = null;
            if (e is Curve c)
            {
                try
                {
                    s = ToPoint3(c.StartPoint);
                    f = ToPoint3(c.EndPoint);
                }
                catch (Exception)
                {
                    // Curvas cerradas sin extremos definidos: solo la caja.
                }

                try
                {
                    double len = c.GetDistanceAtParameter(c.EndParam);
                    if (double.IsFinite(len))
                    {
                        longitud = len;
                    }
                }
                catch (Exception)
                {
                    // Curva sin longitud calculable (p. ej. RAY/XLINE): la huella no la lleva.
                }
            }

            int? nVertices = null;
            switch (e)
            {
                case Polyline pl:
                    nVertices = pl.NumberOfVertices;
                    break;
                case Polyline2d p2:
                    nVertices = CountIds(p2);
                    break;
                case Polyline3d p3:
                    nVertices = CountIds(p3);
                    break;
            }

            return new AnchorFingerprint(handle, AcadUtil.DxfName(e), AnchorFingerprint.BuildFirma(AcadUtil.TryBBox2(e), s, f, longitud, nVertices));
        }

        /// <summary>Número de subentidades (vértices) de una POLYLINE 2D/3D sin abrirlas (enumera sus ObjectId).</summary>
        private static int CountIds(System.Collections.IEnumerable owner)
        {
            int n = 0;
            foreach (object _ in owner)
            {
                n++;
            }

            return n;
        }

        /// <summary>
        /// true si todas las anclas siguen existiendo con la misma huella. <paramref name="cambiada"/> = handle de la
        /// primera que difiere o falta (null si todas iguales). Anclas compuestas (intersección) se comprueban por partes.
        /// </summary>
        public static bool AnchorsUnchanged(Database db, Transaction tr, IReadOnlyList<AnchorFingerprint> anclas, out string? cambiada)
        {
            cambiada = null;
            foreach (AnchorFingerprint a in anclas)
            {
                string[] handles = a.Handle.Split('+');
                string[] firmas = a.Firma.Split('+');
                for (int i = 0; i < handles.Length; i++)
                {
                    Entity? e = AcadUtil.TryGetEntityByHandle(db, handles[i], tr);
                    if (e == null)
                    {
                        cambiada = handles[i];
                        return false;
                    }

                    AnchorFingerprint now = Fingerprint(e, handles[i]);
                    if (i < firmas.Length && !string.Equals(now.Firma, firmas[i], StringComparison.Ordinal))
                    {
                        cambiada = handles[i];
                        return false;
                    }
                }
            }

            return true;
        }

        private static Point3 EntityPoint(Entity e, PointRef r, string path, List<string> advertencias)
        {
            string tipo = AcadUtil.DxfName(e);
            switch (r.Punto)
            {
                case PointRef.PuntoInicio:
                case PointRef.PuntoFin:
                case PointRef.PuntoMedio:
                    {
                        if (e is not Curve c)
                        {
                            throw ToolException.InvalidParams(path + ": la entidad " + r.Handle + " (" + tipo + ") " + NoCurveDetail,
                                new Dictionary<string, object?> { ["handle"] = r.Handle });
                        }

                        try
                        {
                            if (r.Punto == PointRef.PuntoInicio)
                            {
                                return ToPoint3(c.StartPoint);
                            }

                            if (r.Punto == PointRef.PuntoFin)
                            {
                                return ToPoint3(c.EndPoint);
                            }

                            double len = c.GetDistanceAtParameter(c.EndParam);
                            return ToPoint3(c.GetPointAtDist(len / 2.0));
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex)
                        {
                            throw ToolException.InvalidParams(path + ": no se pudo obtener el punto '" + r.Punto + "' de " + r.Handle + " (" + tipo + "): " + ex.ErrorStatus,
                                new Dictionary<string, object?> { ["handle"] = r.Handle });
                        }
                    }

                case PointRef.PuntoCentro:
                    {
                        switch (e)
                        {
                            case Circle ci:
                                return ToPoint3(ci.Center);
                            case Arc ar:
                                return ToPoint3(ar.Center);
                            case Ellipse el:
                                return ToPoint3(el.Center);
                        }

                        BBox2? box = AcadUtil.TryBBox2(e)
                            ?? throw ToolException.InvalidParams(path + ": la entidad " + r.Handle + " (" + tipo + ") no tiene centro ni caja calculable",
                                new Dictionary<string, object?> { ["handle"] = r.Handle });
                        advertencias.Add(path + ": " + r.Handle + " (" + tipo + ") no es un círculo ni un arco; se usó el centro de su caja.");
                        return new Point3((box.Value.Xmin + box.Value.Xmax) / 2.0, (box.Value.Ymin + box.Value.Ymax) / 2.0, 0);
                    }

                default:
                    {
                        int idx = r.Indice ?? 0;
                        int n;
                        Point3d p;
                        switch (e)
                        {
                            case Polyline pl:
                                n = pl.NumberOfVertices;
                                if (idx >= n)
                                {
                                    throw IndexError(path, r.Handle!, idx, n);
                                }

                                p = pl.GetPoint3dAt(idx);
                                break;
                            case Line ln:
                                n = 2;
                                if (idx >= n)
                                {
                                    throw IndexError(path, r.Handle!, idx, n);
                                }

                                p = idx == 0 ? ln.StartPoint : ln.EndPoint;
                                break;
                            case Polyline2d p2:
                                {
                                    var pts = new List<Point3d>();
                                    foreach (ObjectId vid in p2)
                                    {
                                        if (vid.GetObject(OpenMode.ForRead) is Vertex2d v2)
                                        {
                                            pts.Add(v2.Position);
                                        }
                                    }

                                    n = pts.Count;
                                    if (idx >= n)
                                    {
                                        throw IndexError(path, r.Handle!, idx, n);
                                    }

                                    p = pts[idx];
                                    break;
                                }

                            case Polyline3d p3:
                                {
                                    var pts = new List<Point3d>();
                                    foreach (ObjectId vid in p3)
                                    {
                                        if (vid.GetObject(OpenMode.ForRead) is PolylineVertex3d v3)
                                        {
                                            pts.Add(v3.Position);
                                        }
                                    }

                                    n = pts.Count;
                                    if (idx >= n)
                                    {
                                        throw IndexError(path, r.Handle!, idx, n);
                                    }

                                    p = pts[idx];
                                    break;
                                }

                            default:
                                throw ToolException.InvalidParams(path + ": la entidad " + r.Handle + " (" + tipo + ") no tiene vértices (use inicio/fin/medio/centro)",
                                    new Dictionary<string, object?> { ["handle"] = r.Handle });
                        }

                        return ToPoint3(p);
                    }
            }
        }

        private static ToolException IndexError(string path, string handle, int idx, int n) =>
            ToolException.InvalidParams(
                path + ".indice: " + idx.ToString(CultureInfo.InvariantCulture) + " fuera de rango; la entidad " + handle + " tiene "
                + n.ToString(CultureInfo.InvariantCulture) + " vértice(s) (índices 0 a " + (n - 1).ToString(CultureInfo.InvariantCulture) + ")",
                new Dictionary<string, object?> { ["handle"] = handle, ["n_vertices"] = n });

        private static Point3 Intersection(Entity a, Entity b, PointRef r, string path)
        {
            if (a is not Curve ca)
            {
                throw ToolException.InvalidParams(path + ": la entidad " + r.Handle + " (" + AcadUtil.DxfName(a) + ") no es una curva",
                    new Dictionary<string, object?> { ["handle"] = r.Handle });
            }

            if (b is not Curve cb)
            {
                throw ToolException.InvalidParams(path + ": la entidad " + r.Handle2 + " (" + AcadUtil.DxfName(b) + ") no es una curva",
                    new Dictionary<string, object?> { ["handle"] = r.Handle2 });
            }

            var pts = new Point3dCollection();
            try
            {
                ca.IntersectWith(cb, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                throw ToolException.InvalidGeometry(path + ": no se pudo intersecar " + r.Handle + " con " + r.Handle2 + ": " + ex.ErrorStatus);
            }

            if (pts.Count == 0)
            {
                throw ToolException.InvalidGeometry(path + ": las entidades " + r.Handle + " y " + r.Handle2 + " no se intersecan",
                    new Dictionary<string, object?> { ["handle"] = r.Handle, ["interseccion_con"] = r.Handle2 });
            }

            if (pts.Count == 1)
            {
                return ToPoint3(pts[0]);
            }

            if (r.CercaDe == null)
            {
                var lista = new List<double[]>();
                for (int i = 0; i < Math.Min(5, pts.Count); i++)
                {
                    lista.Add(AcadUtil.ToArray(pts[i]));
                }

                throw ToolException.InvalidParams(
                    path + ": hay " + pts.Count.ToString(CultureInfo.InvariantCulture) + " intersecciones entre " + r.Handle + " y " + r.Handle2 + "; indique cerca_de",
                    new Dictionary<string, object?> { ["intersecciones"] = lista, ["n_intersecciones"] = pts.Count });
            }

            Point3d near = AcadUtil.ToPoint3d(r.CercaDe.Value);
            Point3d best = pts[0];
            double bestD = best.DistanceTo(near);
            for (int i = 1; i < pts.Count; i++)
            {
                double d = pts[i].DistanceTo(near);
                if (d < bestD)
                {
                    best = pts[i];
                    bestD = d;
                }
            }

            return ToPoint3(best);
        }

        public static Point3 ToPoint3(Point3d p) => new Point3(p.X, p.Y, p.Z);
    }
}
