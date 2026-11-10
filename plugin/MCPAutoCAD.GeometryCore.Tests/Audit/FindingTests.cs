using System;
using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Audit;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Audit
{
    /// <summary>Formato común de hallazgo (protocol.md §5C.0).</summary>
    public class FindingTests
    {
        private static readonly string[] RuleOrder =
        {
            "auditar_capas", "entidades_en_capa_0", "auditar_bloques", "verificar_cuadro_areas",
            "duplicados", "textos_fuera_de_escala", "xrefs_no_resueltas", "unidades",
        };

        [Fact]
        public void ToDictionary_RecortaA50Handles()
        {
            var f = new Finding("duplicados", Severity.Baja, "60 LINE idénticas.");
            for (int i = 0; i < 60; i++)
            {
                f.Handles.Add((i + 1).ToString("X"));
            }

            Dictionary<string, object?> d = f.ToDictionary();
            var handles = (List<string>)d["handles"]!;
            Assert.Equal(50, handles.Count);
            Assert.Equal("1", handles[0]);
            Assert.Equal("32", handles[49]);
            var datos = (Dictionary<string, object?>)d["datos"]!;
            Assert.Equal(true, datos["handles_truncados"]);
            Assert.Equal(60, datos["n_handles"]);
            // El hallazgo original no se modifica.
            Assert.Equal(60, f.Handles.Count);
            Assert.False(f.Datos.ContainsKey("handles_truncados"));
        }

        [Fact]
        public void ToDictionary_CamposDelContrato()
        {
            var f = new Finding("auditar_capas", Severity.Media, "La capa 'X' tiene 3 entidades.")
            {
                Id = "auditar_capas-1",
                Bbox = BBox2.FromCorners(0, 0, 5, 3),
                Espacio = null,
            };

            Dictionary<string, object?> d = f.ToDictionary();
            Assert.Equal(new[] { "id", "regla", "severidad", "mensaje", "handles", "bbox", "espacio", "datos" }, d.Keys);
            Assert.Empty((List<string>)d["handles"]!);
            Assert.Empty((Dictionary<string, object?>)d["datos"]!);
            var bbox = (double[][])d["bbox"]!;
            Assert.Equal(new[] { 0.0, 0.0 }, bbox[0]);
            Assert.Equal(new[] { 5.0, 3.0 }, bbox[1]);
            Assert.Null(d["espacio"]);
        }

        [Fact]
        public void ToDictionary_50Exactos_SinMarcaDeTruncado()
        {
            var f = new Finding("duplicados", Severity.Baja, "x");
            f.Handles.AddRange(Enumerable.Range(1, 50).Select(i => i.ToString("X")));
            var datos = (Dictionary<string, object?>)f.ToDictionary()["datos"]!;
            Assert.False(datos.ContainsKey("handles_truncados"));
        }

        [Fact]
        public void Sort_SeveridadReglaYOrdenDeLlegada()
        {
            var list = new List<Finding>
            {
                new Finding("duplicados", Severity.Baja, "d1"),
                new Finding("auditar_capas", Severity.Baja, "c1"),
                new Finding("unidades", Severity.Alta, "u1"),
                new Finding("auditar_capas", Severity.Media, "c2"),
                new Finding("verificar_cuadro_areas", Severity.Alta, "v1"),
                new Finding("auditar_capas", Severity.Baja, "c3"),
                new Finding("verificar_cuadro_areas", Severity.Alta, "v2"),
            };

            Finding.Sort(list, RuleOrder);
            Assert.Equal(new[] { "v1", "v2", "u1", "c2", "c1", "c3", "d1" }, list.Select(f => f.Mensaje));
        }

        [Fact]
        public void AssignIds_CorrelativoPorRegla()
        {
            var list = new List<Finding>
            {
                new Finding("verificar_cuadro_areas", Severity.Alta, "a"),
                new Finding("duplicados", Severity.Baja, "b"),
                new Finding("verificar_cuadro_areas", Severity.Media, "c"),
                new Finding("duplicados", Severity.Baja, "d"),
            };

            Finding.AssignIds(list);
            Assert.Equal(
                new[] { "verificar_cuadro_areas-1", "duplicados-1", "verificar_cuadro_areas-2", "duplicados-2" },
                list.Select(f => f.Id));
        }

        [Theory]
        [InlineData("", "alta", "m")]
        [InlineData("r", "critica", "m")]
        [InlineData("r", "alta", "")]
        public void Constructor_ValidaCampos(string regla, string severidad, string mensaje)
        {
            Assert.Throws<ArgumentException>(() => new Finding(regla, severidad, mensaje));
        }

        [Fact]
        public void Severidad_Rango()
        {
            Assert.True(Severity.Rank(Severity.Alta) < Severity.Rank(Severity.Media));
            Assert.True(Severity.Rank(Severity.Media) < Severity.Rank(Severity.Baja));
        }
    }
}
