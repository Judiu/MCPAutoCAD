using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    public class UnitsTableTests
    {
        [Theory]
        [InlineData(0, "desconocida")]
        [InlineData(1, "in")]
        [InlineData(4, "mm")]
        [InlineData(6, "m")]
        [InlineData(8, "µin")]
        [InlineData(11, "Å")]
        [InlineData(20, "pc")]
        [InlineData(24, "us_mi")]
        [InlineData(25, "desconocida")]
        [InlineData(-1, "desconocida")]
        public void Name_SegunTabla(int insunits, string expected)
        {
            Assert.Equal(expected, UnitsTable.Name(insunits));
        }

        [Fact]
        public void Warnings_InsunitsCero_IncluyeAdvertenciaExacta()
        {
            Assert.Equal(
                new[] { "INSUNITS=0: el dibujo no declara unidades; los valores se devuelven en unidades de dibujo sin interpretar." },
                UnitsTable.Warnings(0, 1));
        }

        [Theory]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        public void Warnings_ImperialConInsunitsMetrico(int insunits)
        {
            Assert.Equal(
                new[] { "MEASUREMENT=0 (imperial) con INSUNITS métrico: unidades inconsistentes." },
                UnitsTable.Warnings(insunits, 0));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void Warnings_MetricoConInsunitsImperial(int insunits)
        {
            Assert.Equal(
                new[] { "MEASUREMENT=1 (métrico) con INSUNITS imperial: unidades inconsistentes." },
                UnitsTable.Warnings(insunits, 1));
        }

        [Theory]
        [InlineData(6, 1)]
        [InlineData(4, 1)]
        [InlineData(1, 0)]
        [InlineData(2, 0)]
        [InlineData(10, 1)]
        public void Warnings_UnidadesConcordantes_SinAdvertencias(int insunits, int measurement)
        {
            Assert.Empty(UnitsTable.Warnings(insunits, measurement));
        }

        [Theory]
        [InlineData(0, "desconocida")]
        [InlineData(1, "pulgadas")]
        [InlineData(2, "pies")]
        [InlineData(4, "milímetros")]
        [InlineData(6, "metros")]
        [InlineData(9, "milésimas de pulgada")]
        [InlineData(11, "ångströms")]
        [InlineData(18, "unidades astronómicas")]
        [InlineData(19, "años luz")]
        [InlineData(20, "pársecs")]
        [InlineData(21, "pies US")]
        [InlineData(24, "millas US")]
        [InlineData(25, "desconocida")]
        [InlineData(-1, "desconocida")]
        public void SpanishName_SegunTabla(int insunits, string expected)
        {
            Assert.Equal(expected, UnitsTable.SpanishName(insunits));
        }

        [Fact]
        public void SpanishName_CubreTodoElRango()
        {
            for (int i = 1; i <= 24; i++)
            {
                Assert.True(UnitsTable.IsInRange(i));
                Assert.NotEqual("desconocida", UnitsTable.SpanishName(i));
            }

            Assert.False(UnitsTable.IsInRange(25));
            Assert.False(UnitsTable.IsInRange(-1));
        }
    }
}
