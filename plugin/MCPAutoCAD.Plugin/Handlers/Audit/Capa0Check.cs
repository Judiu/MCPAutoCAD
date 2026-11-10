using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>Entidades en capa 0 de un espacio.</summary>
    internal sealed class Layer0Space
    {
        public Layer0Space(string name)
        {
            Name = name;
        }

        public string Name { get; }

        /// <summary>Entidades en orden de base de datos, con su tipo §5B.0.</summary>
        public List<(ObjectId id, string tipo)> Entidades { get; } = new List<(ObjectId, string)>();

        public Dictionary<string, int> PorTipo { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>Resultado de <see cref="Capa0Check.Analyze"/>.</summary>
    internal sealed class Layer0Result
    {
        /// <summary>Espacios recorridos en el orden común (incluidos los que no tienen ninguna).</summary>
        public List<Layer0Space> Espacios { get; } = new List<Layer0Space>();

        public bool Completo { get; set; } = true;

        public int Total
        {
            get
            {
                int n = 0;
                foreach (Layer0Space s in Espacios)
                {
                    n += s.Entidades.Count;
                }

                return n;
            }
        }
    }

    /// <summary>
    /// Regla 2 de auditar_dibujo, <c>entidades_en_capa_0</c> (protocol.md §5C.2): entidades no borradas con capa 0 en
    /// todos los espacios (modelo + layouts por TabOrder). No se recorren definiciones de bloque (allí la capa 0 es
    /// correcta) ni se cuentan los ATTRIB. Un hallazgo media por espacio con entidades.
    /// </summary>
    internal sealed class Capa0Check : IAuditCheck
    {
        public const string RuleName = "entidades_en_capa_0";

        private const int MaxTypesInMessage = 5;

        public string Regla => RuleName;

        public int Orden => 2;

        /// <summary>Recorre los espacios dados (null = todos los espacios).</summary>
        public Layer0Result Analyze(AuditContext ctx, IReadOnlyList<SpaceResolver.ResolvedSpace>? spaces = null)
        {
            Transaction tr = ctx.Tr;
            spaces ??= AuditSupport.AllSpaces(ctx.Db, tr);
            ObjectId layerZero = ctx.Db.LayerZero;
            var result = new Layer0Result();
            int seen = 0;
            foreach (SpaceResolver.ResolvedSpace space in spaces)
            {
                var s = new Layer0Space(space.Name);
                result.Espacios.Add(s);
                if (!result.Completo)
                {
                    continue;
                }

                foreach (ObjectId id in space.Btr)
                {
                    if (id.IsNull || id.IsErased)
                    {
                        continue;
                    }

                    if (++seen % AuditSupport.CheckEvery == 0 && ctx.TimeExceeded)
                    {
                        result.Completo = false;
                        break;
                    }

                    Entity? e = AuditSupport.Open(tr, id);
                    if (e == null)
                    {
                        continue;
                    }

                    bool onZero;
                    try
                    {
                        onZero = e.LayerId == layerZero;
                    }
                    catch (Exception)
                    {
                        onZero = false;
                    }

                    if (!onZero)
                    {
                        continue;
                    }

                    string tipo = AcadUtil.DxfNameFromId(id) ?? AcadUtil.DxfName(e);
                    s.Entidades.Add((id, tipo));
                    AuditSupport.Increment(s.PorTipo, tipo);
                }
            }

            return result;
        }

        public AuditCheckResult Run(AuditContext ctx)
        {
            Layer0Result analysis = Analyze(ctx);
            var result = new AuditCheckResult { Completo = analysis.Completo };
            foreach (Layer0Space s in analysis.Espacios)
            {
                if (s.Entidades.Count > 0)
                {
                    result.Hallazgos.Add(ToFinding(s, ctx.Tr));
                }
            }

            result.Resumen["total"] = analysis.Total;
            return result;
        }

        /// <summary>Hallazgo media del espacio: primeros 50 handles, caja de esos 50 y conteo por tipo.</summary>
        public static Finding ToFinding(Layer0Space s, Transaction tr)
        {
            int n = s.Entidades.Count;
            Dictionary<string, object?> porTipo = AuditSupport.SortedCounts(s.PorTipo);
            var parts = new List<string>();
            foreach (KeyValuePair<string, object?> kv in porTipo)
            {
                if (parts.Count == MaxTypesInMessage)
                {
                    parts.Add("…");
                    break;
                }

                parts.Add(kv.Key + " " + Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture));
            }

            string message = AuditSupport.Int(n) + (n == 1 ? " entidad" : " entidades") + " en la capa 0 en el espacio "
                + s.Name + " (" + string.Join(", ", parts) + ").";
            var f = new Finding(RuleName, Severity.Media, message) { Espacio = s.Name };
            var first = new List<ObjectId>();
            for (int i = 0; i < n && i < Finding.MaxHandles; i++)
            {
                first.Add(s.Entidades[i].id);
                f.Handles.Add(AuditSupport.Handle(s.Entidades[i].id));
            }

            f.Bbox = AuditSupport.UnionBox(tr, first);
            f.Datos["n"] = n;
            f.Datos["por_tipo"] = porTipo;
            f.Datos["handles_truncados"] = n > Finding.MaxHandles;
            f.Datos["n_handles"] = n;
            return f;
        }
    }
}
