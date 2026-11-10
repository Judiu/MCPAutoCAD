using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class PlacementBudgetTests
    {
        [Theory]
        [InlineData(0, 0.0, false)]
        [InlineData(59999, 2499.9, false)]
        [InlineData(60000, 0.0, true)]
        [InlineData(10, 2500.0, true)]
        public void Exhausted_PorEntidadesOTiempo(int n, double ms, bool esperado)
        {
            Assert.Equal(esperado, PlacementBudget.Exhausted(n, ms));
        }

        [Fact]
        public void Exhausted_TopesPersonalizados()
        {
            Assert.True(PlacementBudget.Exhausted(5, 0, maxEntities: 5, maxMs: 100));
            Assert.False(PlacementBudget.Exhausted(4, 99, maxEntities: 5, maxMs: 100));
        }

        [Fact]
        public void Exhausted_ValoresInvalidos_Lanzan()
        {
            Assert.Throws<ArgumentException>(() => PlacementBudget.Exhausted(1, double.NaN));
            Assert.Throws<ArgumentException>(() => PlacementBudget.Exhausted(1, 0, maxEntities: -1));
        }

        [Fact]
        public void PartialWarning_CitaEspacioEntidadesYTiempo()
        {
            string w = PlacementBudget.PartialWarning("Layout1", 60000, 2512.4);
            Assert.Contains("'Layout1'", w);
            Assert.Contains("60000 entidades", w);
            Assert.Contains("2512 ms", w);
            Assert.Contains("se escribió igualmente", w);
        }

        [Fact]
        public void OutsideMessage_ConEtiqueta_CitaElArrayIndicado()
        {
            BBox2 ext = BBox2.FromCorners(0, 0, 10, 10);
            var marks = new List<BBox2> { BBox2.FromCorners(100, 100, 101, 101) };
            string m = MarkPlacement.OutsideMessage("modelo", true, ext, new List<int> { 0 }, marks, "vertices");
            Assert.StartsWith("vertices[0] [[100, 100], [101, 101]] fuera de la extensión", m);
            Assert.Contains("forzar: true", m);
            // La sobrecarga original sigue diciendo "marcas".
            Assert.StartsWith("marcas[0]", MarkPlacement.OutsideMessage("modelo", true, ext, new List<int> { 0 }, marks));
        }
    }
}
