using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class UnitsGateTests
    {
        [Theory]
        [InlineData(0, false, true)]
        [InlineData(0, true, false)]
        [InlineData(6, false, false)]
        [InlineData(1, false, false)]
        public void MustReject(int ins, bool aceptar, bool esperado)
        {
            Assert.Equal(esperado, UnitsGate.MustReject(ins, aceptar));
        }

        [Theory]
        [InlineData(0, true, true)]
        [InlineData(0, false, false)]
        [InlineData(6, true, false)]
        public void AcceptedUnknown(int ins, bool aceptar, bool esperado)
        {
            Assert.Equal(esperado, UnitsGate.AcceptedUnknown(ins, aceptar));
        }

        [Fact]
        public void AdvertenciaFija()
        {
            Assert.Contains("INSUNITS=0", UnitsGate.AcceptedWithoutUnits);
            Assert.StartsWith("aceptar_sin_unidades: true", UnitsGate.AcceptedWithoutUnits);
        }
    }
}
