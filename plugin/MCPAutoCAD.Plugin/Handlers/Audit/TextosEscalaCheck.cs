using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>Texto leído del espacio (TEXT, MTEXT o ATTRIB de un INSERT del espacio).</summary>
    internal sealed class TextHeightItem
    {
        public TextHeightItem(ObjectId id, string tipo, string capa, double altura)
        {
            Id = id;
            Tipo = tipo;
            Capa = capa;
            Altura = altura;
        }

        public ObjectId Id { get; }
        public string Tipo { get; }
        public string Capa { get; }
        public double Altura { get; }
    }

    /// <summary>Resultado de <see cref="TextosEscalaCheck.Analyze"/>.</summary>
    internal sealed class TextScaleAnalysis
    {
        public TextScaleAnalysis(List<TextHeightItem> textos, HeightOutliersResult outliers, bool completo)
        {
            Textos = textos;
            Outliers = outliers;
            Completo = completo;
        }

        /// <summary>Todos los textos del espacio en orden de base de datos (ATTRIB tras su INSERT).</summary>
        public List<TextHeightItem> Textos { get; }

        /// <summary>Resultado de GeometryCore; <see cref="HeightOutlier.Index"/> apunta a <see cref="Textos"/>.</summary>
        public HeightOutliersResult Outliers { get; }

        public bool Completo { get; }
    }

    /// <summary>
    /// Regla 6 de auditar_dibujo, <c>textos_fuera_de_escala</c> (protocol.md §5C.7): alturas de TEXT (Height), MTEXT
    /// (TextHeight) y ATTRIB (Height, de los INSERT del espacio) frente a la mediana del espacio, con GeometryCore
    /// (<see cref="HeightOutliers"/>). Un hallazgo baja por motivo con casos.
    /// </summary>
    internal sealed class TextosEscalaCheck : IAuditCheck
    {
        public const string RuleName = "textos_fuera_de_escala";

        public string Regla => RuleName;

        public int Orden => 6;

        public TextScaleAnalysis Analyze(AuditContext ctx)
        {
            Transaction tr = ctx.Tr;
            var items = new List<TextHeightItem>();
            bool completo = true;
            int seen = 0;
            foreach (ObjectId id in ctx.Espacio.Btr)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                if (++seen % AuditSupport.CheckEvery == 0 && ctx.TimeExceeded)
                {
                    completo = false;
                    break;
                }

                string? quick = AcadUtil.DxfNameFromId(id);
                if (quick != null && quick != "TEXT" && quick != "MTEXT" && quick != "INSERT")
                {
                    continue;
                }

                Entity? e = AuditSupport.Open(tr, id);
                try
                {
                    switch (e)
                    {
                        case MText mt:
                            items.Add(new TextHeightItem(id, "MTEXT", AuditSupport.Layer(mt), mt.TextHeight));
                            break;
                        case DBText t when !(e is AttributeDefinition) && !(e is AttributeReference):
                            items.Add(new TextHeightItem(id, "TEXT", AuditSupport.Layer(t), t.Height));
                            break;
                        case BlockReference br:
                            foreach (AttributeReference attr in EntityFormatter.Attributes(br, tr))
                            {
                                try
                                {
                                    items.Add(new TextHeightItem(attr.ObjectId, "ATTRIB", AuditSupport.Layer(attr), attr.Height));
                                }
                                catch (Exception)
                                {
                                    // Atributo ilegible: se omite.
                                }
                            }

                            break;
                    }
                }
                catch (Exception)
                {
                    // Entidad ilegible: se omite.
                }
            }

            var heights = new List<(string handle, double altura)>(items.Count);
            foreach (TextHeightItem it in items)
            {
                heights.Add((AuditSupport.Handle(it.Id), it.Altura));
            }

            return new TextScaleAnalysis(items, HeightOutliers.Analyze(heights), completo);
        }

        public AuditCheckResult Run(AuditContext ctx)
        {
            TextScaleAnalysis analysis = Analyze(ctx);
            var result = new AuditCheckResult { Completo = analysis.Completo };
            foreach (string motivo in new[] { HeightOutliers.Menor, HeightOutliers.Mayor, HeightOutliers.NoPositiva })
            {
                Finding? f = ToFinding(analysis, motivo, ctx.Espacio.Name, ctx.Tr);
                if (f != null)
                {
                    result.Hallazgos.Add(f);
                }
            }

            result.Resumen["n_textos"] = analysis.Outliers.N;
            result.Resumen["mediana"] = analysis.Outliers.Mediana;
            return result;
        }

        /// <summary>Hallazgo baja del motivo (null si no hay casos): primeros 50 handles en el orden del contrato.</summary>
        public static Finding? ToFinding(TextScaleAnalysis analysis, string motivo, string espacio, Transaction tr)
        {
            var ids = new List<ObjectId>();
            foreach (HeightOutlier o in analysis.Outliers.Outliers)
            {
                if (o.Motivo == motivo)
                {
                    ids.Add(analysis.Textos[o.Index].Id);
                }
            }

            int n = ids.Count;
            if (n == 0)
            {
                return null;
            }

            double? mediana = analysis.Outliers.Mediana;
            string med = mediana == null ? "?" : AuditSupport.Num(mediana.Value);
            string what = motivo switch
            {
                HeightOutliers.Menor => "con altura menor que 0.2 × la mediana (" + med + ")",
                HeightOutliers.Mayor => "con altura mayor que 5 × la mediana (" + med + ")",
                _ => "con altura no positiva (≤ 0)",
            };

            string message = AuditSupport.Int(n) + (n == 1 ? " texto " : " textos ") + what + " en el espacio " + espacio + ".";
            var f = new Finding(RuleName, Severity.Baja, message) { Espacio = espacio };
            var first = new List<ObjectId>();
            for (int i = 0; i < n && i < Finding.MaxHandles; i++)
            {
                first.Add(ids[i]);
                f.Handles.Add(AuditSupport.Handle(ids[i]));
            }

            f.Bbox = AuditSupport.UnionBox(tr, first);
            f.Datos["motivo"] = motivo;
            f.Datos["n"] = n;
            f.Datos["mediana"] = mediana;
            f.Datos["handles_truncados"] = n > Finding.MaxHandles;
            f.Datos["n_handles"] = n;
            return f;
        }
    }
}
