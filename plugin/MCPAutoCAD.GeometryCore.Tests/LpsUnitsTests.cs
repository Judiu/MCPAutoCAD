using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    public class LpsUnitsTests
    {
        [Fact]
        public void MismasUnidades_SinAdvertencia()
        {
            Assert.Null(LpsUnits.Warning(6, 6, "m"));
        }

        [Fact]
        public void Pulgadas_EjemploLiteralDelContrato()
        {
            Assert.Equal(
                "El dibujo está en pulgadas (INSUNITS=1); el estándar LPS es metros (INSUNITS=6).",
                LpsUnits.Warning(1, 6, "m"));
        }

        [Fact]
        public void Milimetros_ConAcento()
        {
            Assert.Equal(
                "El dibujo está en milímetros (INSUNITS=4); el estándar LPS es metros (INSUNITS=6).",
                LpsUnits.Warning(4, 6, "m"));
        }

        [Fact]
        public void InsunitsCero_TextoExacto()
        {
            Assert.Equal(
                "El dibujo no declara unidades (INSUNITS=0); el estándar LPS es metros (INSUNITS=6).",
                LpsUnits.Warning(0, 6, "m"));
        }

        [Fact]
        public void InsunitsDelDibujoFueraDeRango_UsaDesconocida()
        {
            Assert.Equal(
                "El dibujo está en desconocida (INSUNITS=30); el estándar LPS es metros (INSUNITS=6).",
                LpsUnits.Warning(30, 6, "m"));
        }

        [Fact]
        public void EstandarFueraDeRango_UsaNombreCorto()
        {
            Assert.Equal(
                "El dibujo está en metros (INSUNITS=6); el estándar LPS es xx (INSUNITS=99).",
                LpsUnits.Warning(6, 99, "xx"));
        }

        [Fact]
        public void EstandarEnRango_IgnoraNombreCorto()
        {
            Assert.Equal(
                "El dibujo está en metros (INSUNITS=6); el estándar LPS es milímetros (INSUNITS=4).",
                LpsUnits.Warning(6, 4, "cualquier cosa"));
        }
    }
}
