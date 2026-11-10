using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>Utilidades comunes sobre la API de AutoCAD. Solo hilo principal.</summary>
    internal static class AcadUtil
    {
        public static double[] ToArray(Point3d p) => new[] { p.X, p.Y, p.Z };

        public static double[] ToArray(Vector3d v) => new[] { v.X, v.Y, v.Z };

        public static Point3d ToPoint3d(Point3 p) => new Point3d(p.X, p.Y, p.Z);

        public static int InsUnits(Database db) => (int)db.Insunits;

        public static int Measurement(Database db) => (int)db.Measurement;

        /// <summary>
        /// Advertencias de unidades del dibujo, como lista mutable: §2.1/§5.2 y, al final, la advertencia frente al
        /// estándar LPS de §2.2 (si el estándar está disponible y el INSUNITS difiere).
        /// </summary>
        public static List<string> UnitWarnings(Database db)
        {
            int ins = InsUnits(db);
            var warnings = new List<string>(UnitsTable.Warnings(ins, Measurement(db)));
            string? lps = LpsStandard.Warning(ins);
            if (lps != null)
            {
                warnings.Add(lps);
            }

            return warnings;
        }

        /// <summary>Añade insunits, unidades y advertencias (§2.1 + §5.2 + §2.2) del dibujo a una respuesta.</summary>
        public static void AddUnitFields(Dictionary<string, object?> result, Database db)
        {
            int ins = InsUnits(db);
            result["insunits"] = ins;
            result["unidades"] = UnitsTable.Name(ins);
            result["advertencias"] = UnitWarnings(db);
        }

        public static int GetIntSysVar(string name)
        {
            object? value = AcApp.GetSystemVariable(name);
            return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        public static string GetStringSysVar(string name)
        {
            object? value = AcApp.GetSystemVariable(name);
            return value as string ?? "";
        }

        /// <summary>true si la matriz es la identidad con tolerancia absoluta <paramref name="tol"/> por elemento.</summary>
        public static bool IsIdentity(Matrix3d m, double tol)
        {
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    double expected = i == j ? 1.0 : 0.0;
                    if (Math.Abs(m[i, j] - expected) > tol)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // ------------------------------------------------------------------ Fase 1 (lectura)

        /// <summary>Handle como en protocol.md §2.1: hexadecimal en mayúsculas sin prefijo.</summary>
        public static string HandleToString(Handle h) => h.ToString().ToUpperInvariant();

        /// <summary>
        /// Entidad no borrada con ese handle (hexadecimal, ya validado por formato), abierta para lectura en
        /// <paramref name="tr"/>; null si no existe, está borrada o no es una Entity.
        /// </summary>
        public static Entity? TryGetEntityByHandle(Database db, string hex, Transaction tr)
        {
            try
            {
                if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong raw))
                {
                    return null;
                }

                var handle = new Handle(unchecked((long)raw));
                if (!db.TryGetObjectId(handle, out ObjectId id) || id.IsNull || id.IsErased || !id.IsValid)
                {
                    return null;
                }

                return tr.GetObject(id, OpenMode.ForRead, false) as Entity;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return null;
            }
        }

        /// <summary>Radianes → grados.</summary>
        public static double ToDegrees(double radians) => radians * 180.0 / Math.PI;

        /// <summary>Radianes → grados normalizados a [0, 360).</summary>
        public static double ToDegrees360(double radians)
        {
            double d = ToDegrees(radians) % 360.0;
            if (d < 0)
            {
                d += 360.0;
            }

            return d >= 360.0 ? 0.0 : d;
        }

        /// <summary>
        /// Caja de la entidad [[xmin,ymin,zmin],[xmax,ymax,zmax]] (Entity.GeometricExtents), o null si AutoCAD no
        /// la puede calcular o no es finita (protocol.md §2.5).
        /// </summary>
        public static double[][]? ExtentsOrNull(Entity e)
        {
            Extents3d? ext = TryExtents(e);
            if (ext == null)
            {
                return null;
            }

            return new[] { ToArray(ext.Value.MinPoint), ToArray(ext.Value.MaxPoint) };
        }

        /// <summary>Extents3d de la entidad o null (lanza / no finitos).</summary>
        public static Extents3d? TryExtents(Entity e)
        {
            try
            {
                Extents3d ext = e.GeometricExtents;
                if (!IsFinite(ext.MinPoint) || !IsFinite(ext.MaxPoint))
                {
                    return null;
                }

                return ext;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Caja 2D de la entidad para filtros bbox, o null si no tiene extents (no pasa el filtro).</summary>
        public static BBox2? TryBBox2(Entity e)
        {
            Extents3d? ext = TryExtents(e);
            if (ext == null)
            {
                return null;
            }

            try
            {
                return BBox2.FromCorners(ext.Value.MinPoint.X, ext.Value.MinPoint.Y, ext.Value.MaxPoint.X, ext.Value.MaxPoint.Y);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Tipo de §5B.0: GetRXClass().DxfName en mayúsculas (nombre de clase si no tiene DxfName).</summary>
        public static string DxfName(Entity e)
        {
            try
            {
                string? dxf = e.GetRXClass()?.DxfName;
                if (!string.IsNullOrEmpty(dxf))
                {
                    return dxf.ToUpperInvariant();
                }
            }
            catch (Exception)
            {
                // Se usa el nombre de clase .NET.
            }

            return e.GetType().Name.ToUpperInvariant();
        }

        /// <summary>
        /// Tipo §5B.0 a partir del ObjectId SIN abrir el objeto (ObjectClass.DxfName). Sirve para filtrar por tipo
        /// rápido; null si no se puede determinar (entonces hay que abrir la entidad y usar <see cref="DxfName"/>).
        /// </summary>
        public static string? DxfNameFromId(ObjectId id)
        {
            try
            {
                string? dxf = id.ObjectClass?.DxfName;
                return string.IsNullOrEmpty(dxf) ? null : dxf.ToUpperInvariant();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool IsFinite(Point3d p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

        /// <summary>Punto de salida (3 componentes) o null si alguna componente no es finita.</summary>
        public static double[]? PointOrNull(Point3d p) => IsFinite(p) ? ToArray(p) : null;

        /// <summary>Número de salida o null si no es finito (System.Text.Json no serializa NaN/Infinity).</summary>
        public static object? NumberOrNull(double d) => double.IsFinite(d) ? d : null;

        /// <summary>
        /// n_inserciones de protocol.md §5B.4: referencias directas no borradas de la definición más las de sus
        /// bloques anónimos derivados (bloques dinámicos). Nunca lanza (lo que falle no se cuenta).
        /// </summary>
        public static int CountInsertions(BlockTableRecord btr, Transaction tr)
        {
            var ids = new List<ObjectId>();
            CollectInsertions(btr, tr, ids);
            return ids.Count;
        }

        /// <summary>Añade a <paramref name="into"/> las inserciones no borradas de la definición y de sus anónimos.</summary>
        public static void CollectInsertions(BlockTableRecord btr, Transaction tr, List<ObjectId> into)
        {
            AddReferenceIds(btr, into);
            ObjectIdCollection? anon = null;
            try
            {
                anon = btr.GetAnonymousBlockIds();
            }
            catch (Exception)
            {
                anon = null;
            }

            if (anon == null)
            {
                return;
            }

            foreach (ObjectId anonId in anon)
            {
                try
                {
                    if (anonId.IsNull || anonId.IsErased || anonId == btr.ObjectId)
                    {
                        continue;
                    }

                    if (tr.GetObject(anonId, OpenMode.ForRead, false) is BlockTableRecord anonBtr)
                    {
                        AddReferenceIds(anonBtr, into);
                    }
                }
                catch (Exception)
                {
                    // Anónimo ilegible: no se cuenta.
                }
            }
        }

        private static void AddReferenceIds(BlockTableRecord btr, List<ObjectId> into)
        {
            try
            {
                ObjectIdCollection refs = btr.GetBlockReferenceIds(true, false);
                foreach (ObjectId id in refs)
                {
                    if (!id.IsNull && !id.IsErased)
                    {
                        into.Add(id);
                    }
                }
            }
            catch (Exception)
            {
                // Sin referencias legibles.
            }
        }
    }
}
