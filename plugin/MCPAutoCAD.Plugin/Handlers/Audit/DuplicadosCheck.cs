using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>Grupo de entidades con la misma clave de geometría redondeada.</summary>
    internal sealed class DuplicateGroup
    {
        public DuplicateGroup(string tipo, int firstIndex)
        {
            Tipo = tipo;
            FirstIndex = firstIndex;
        }

        public string Tipo { get; }

        /// <summary>Posición de la primera entidad en el orden de base de datos (desempate del orden de grupos).</summary>
        public int FirstIndex { get; }

        /// <summary>Entidades del grupo en orden de base de datos.</summary>
        public List<ObjectId> Ids { get; } = new List<ObjectId>();

        /// <summary>Capas distintas, en orden de aparición.</summary>
        public List<string> Capas { get; } = new List<string>();

        public BBox2? Bbox { get; set; }

        public int N => Ids.Count;
    }

    /// <summary>Resultado de <see cref="DuplicadosCheck.Analyze"/>.</summary>
    internal sealed class DuplicateAnalysis
    {
        /// <summary>Grupos con n ≥ 2, ordenados por n desc y luego por la primera entidad (orden de base de datos).</summary>
        public List<DuplicateGroup> Grupos { get; } = new List<DuplicateGroup>();

        public bool Completo { get; set; } = true;
    }

    /// <summary>
    /// Regla 5 de auditar_dibujo, <c>duplicados</c> (protocol.md §5C.6): agrupa las entidades del espacio por la clave
    /// canónica de GeometryCore (<see cref="DuplicateHasher"/>) en un diccionario: una pasada, nunca O(n²). La capa no
    /// forma parte de la clave. Limitación aceptada: valores a distancia menor que la tolerancia pueden caer en celdas
    /// contiguas y no agruparse.
    /// </summary>
    internal sealed class DuplicadosCheck : IAuditCheck
    {
        public const string RuleName = "duplicados";

        public string Regla => RuleName;

        public int Orden => 5;

        /// <summary>Lectura completa del espacio de <paramref name="ctx"/> con filtros opcionales.</summary>
        public DuplicateAnalysis Analyze(
            AuditContext ctx, IReadOnlyCollection<string>? capas = null, ISet<string>? tipos = null, double tolerancia = DuplicateHasher.DefaultTolerance)
        {
            Transaction tr = ctx.Tr;
            var wanted = new HashSet<string>(tipos ?? (IEnumerable<string>)DuplicateHasher.Types, StringComparer.Ordinal);
            var byKey = new Dictionary<string, DuplicateGroup>(StringComparer.Ordinal);
            var result = new DuplicateAnalysis();
            int index = 0;
            foreach (ObjectId id in ctx.Espacio.Btr)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                index++;
                if (index % AuditSupport.CheckEvery == 0 && ctx.TimeExceeded)
                {
                    result.Completo = false;
                    break;
                }

                string? quick = AcadUtil.DxfNameFromId(id);
                if (quick != null && !wanted.Contains(quick))
                {
                    continue;
                }

                Entity? e = AuditSupport.Open(tr, id);
                if (e == null)
                {
                    continue;
                }

                string tipo = quick ?? AcadUtil.DxfName(e);
                if (!wanted.Contains(tipo))
                {
                    continue;
                }

                string capa = AuditSupport.Layer(e);
                if (!EntityFilter.MatchesAny(capa, capas))
                {
                    continue;
                }

                DuplicateInput? input = ToInput(e, tr);
                if (input == null)
                {
                    continue;
                }

                string key;
                try
                {
                    key = DuplicateHasher.Key(input, tolerancia);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (!byKey.TryGetValue(key, out DuplicateGroup? g))
                {
                    g = new DuplicateGroup(input.Tipo, index);
                    byKey.Add(key, g);
                }

                g.Ids.Add(id);
                if (!g.Capas.Contains(capa))
                {
                    g.Capas.Add(capa);
                }
            }

            foreach (DuplicateGroup g in byKey.Values)
            {
                if (g.N >= 2)
                {
                    result.Grupos.Add(g);
                }
            }

            result.Grupos.Sort((a, b) =>
            {
                int c = b.N.CompareTo(a.N);
                return c != 0 ? c : a.FirstIndex.CompareTo(b.FirstIndex);
            });

            foreach (DuplicateGroup g in result.Grupos)
            {
                g.Bbox = AuditSupport.UnionBox(tr, g.Ids);
            }

            return result;
        }

        public AuditCheckResult Run(AuditContext ctx)
        {
            DuplicateAnalysis analysis = Analyze(ctx);
            var result = new AuditCheckResult { Completo = analysis.Completo };
            int sobrantes = 0;
            foreach (DuplicateGroup g in analysis.Grupos)
            {
                result.Hallazgos.Add(ToFinding(g, DuplicateHasher.DefaultTolerance, ctx.Espacio.Name));
                sobrantes += g.N - 1;
            }

            result.Resumen["n_grupos"] = analysis.Grupos.Count;
            result.Resumen["n_entidades_sobrantes"] = sobrantes;
            return result;
        }

        /// <summary>Hallazgo del grupo: baja (media si n ≥ 5), datos {tipo, n, capas}.</summary>
        public static Finding ToFinding(DuplicateGroup g, double tolerancia, string espacio)
        {
            string capas = g.Capas.Count == 1 ? "en la capa " + g.Capas[0] : "en las capas " + string.Join(", ", g.Capas);
            string message = AuditSupport.Int(g.N) + " " + g.Tipo + " idénticas (tolerancia " + FormatTolerance(tolerancia)
                + ") " + capas + ".";
            var f = new Finding(RuleName, g.N >= 5 ? Severity.Media : Severity.Baja, message)
            {
                Espacio = espacio,
                Bbox = g.Bbox,
            };

            foreach (ObjectId id in g.Ids)
            {
                f.Handles.Add(AuditSupport.Handle(id));
            }

            f.Datos["tipo"] = g.Tipo;
            f.Datos["n"] = g.N;
            f.Datos["capas"] = g.Capas;
            return f;
        }

        /// <summary>Tolerancia en notación exponencial invariante ("1e-06").</summary>
        public static string FormatTolerance(double tol) => tol.ToString("0.###e-00", CultureInfo.InvariantCulture);

        /// <summary>Datos geométricos de la entidad para el hasher; null si el tipo no aplica o no se puede leer.</summary>
        private static DuplicateInput? ToInput(Entity e, Transaction tr)
        {
            try
            {
                switch (e)
                {
                    case Line l:
                        return new LineInput(P(l.StartPoint), P(l.EndPoint));
                    case Polyline pl:
                    {
                        int n = pl.NumberOfVertices;
                        var verts = new List<(double x, double y, double bulge)>(n);
                        for (int i = 0; i < n; i++)
                        {
                            Point2d v = pl.GetPoint2dAt(i);
                            verts.Add((v.X, v.Y, pl.GetBulgeAt(i)));
                        }

                        return new PolylineInput(pl.Closed, pl.Elevation, verts);
                    }

                    case Circle c:
                        return new CircleInput(P(c.Center), c.Radius, new Point3(c.Normal.X, c.Normal.Y, c.Normal.Z));
                    case Arc a:
                        return new ArcInput(P(a.Center), a.Radius, a.StartAngle, a.EndAngle);
                    case BlockReference br:
                        return new InsertInput(
                            EntityFormatter.EffectiveBlockName(br, tr) ?? "",
                            P(br.Position),
                            new Point3(br.ScaleFactors.X, br.ScaleFactors.Y, br.ScaleFactors.Z),
                            br.Rotation);
                    case MText mt:
                        return new TextInput(true, mt.Contents ?? "", P(mt.Location), mt.TextHeight);
                    case DBText t when !(e is AttributeDefinition) && !(e is AttributeReference):
                        return new TextInput(false, t.TextString ?? "", P(t.Position), t.Height);
                    default:
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Point3 P(Point3d p) => new Point3(p.X, p.Y, p.Z);
    }
}
