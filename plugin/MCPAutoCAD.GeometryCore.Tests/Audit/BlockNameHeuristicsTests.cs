using MCPAutoCAD.GeometryCore.Audit;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Audit
{
    /// <summary>Heurística de nombres de bloque arbitrarios (protocol.md §5C.3 b).</summary>
    public class BlockNameHeuristicsTests
    {
        [Theory]
        [InlineData("jgytfyuh", BlockNameHeuristics.SinVocales)]
        [InlineData("BCDFG", BlockNameHeuristics.SinVocales)]
        [InlineData("xyzzy", BlockNameHeuristics.SinVocales)] // la Y no es vocal
        [InlineData("HHHH", BlockNameHeuristics.LetraRepetida)]
        [InlineData("AAAAA", BlockNameHeuristics.LetraRepetida)]
        [InlineData("asdf", BlockNameHeuristics.SecuenciaTeclado)]
        [InlineData("jkl", BlockNameHeuristics.SecuenciaTeclado)]
        [InlineData("qwer1", BlockNameHeuristics.SecuenciaTeclado)]
        [InlineData("erere", BlockNameHeuristics.ParRepetido)]
        [InlineData("ABAB", BlockNameHeuristics.ParRepetido)]
        public void Sospechosos_ConSuMotivo(string name, string motivo)
        {
            Assert.Equal(motivo, BlockNameHeuristics.Suspicion(name));
        }

        [Fact]
        public void HHHHH_EsSospechoso_GanaSinVocalesPorOrden()
        {
            // Cinco H son a la vez una racha de 5 consonantes (regla 1) y la misma letra ≥ 4 veces (regla 2):
            // el orden del contrato hace ganar la primera.
            Assert.Equal(BlockNameHeuristics.SinVocales, BlockNameHeuristics.Suspicion("HHHHH"));
        }

        [Theory]
        [InlineData("DP")]
        [InlineData("PUERTA")]
        [InlineData("LPS_ROTULO")]
        [InlineData("A$C1A2B3C4D")]
        [InlineData("WC_1")]
        [InlineData("MESA-COMEDOR")]
        [InlineData("SILLA")]
        [InlineData("TV")]
        [InlineData("VENTANA_2H")]
        [InlineData("*U12")]
        [InlineData("a$c7F3D2E1B")]
        [InlineData("")]
        public void NoSospechosos(string name)
        {
            Assert.Null(BlockNameHeuristics.Suspicion(name));
        }

        [Fact]
        public void LosSeparadoresCortanLaRachaSinVocales()
        {
            // BCD_FGH: dos rachas de 3, ninguna llega a 5.
            Assert.Null(BlockNameHeuristics.Suspicion("BCD_FGH"));
            Assert.Null(BlockNameHeuristics.Suspicion("BCD1FGH"));
        }

        [Fact]
        public void SecuenciaTeclado_TresLetrasSoloSiEsElNombreCompleto()
        {
            Assert.Equal(BlockNameHeuristics.SecuenciaTeclado, BlockNameHeuristics.Suspicion("ZXC"));
            Assert.Null(BlockNameHeuristics.Suspicion("ZXC1"));
            Assert.Null(BlockNameHeuristics.Suspicion("ABC")); // no es subcadena de ninguna fila
        }

        [Fact]
        public void LetraRepetida_TresNoBasta()
        {
            Assert.Null(BlockNameHeuristics.Suspicion("AAA1"));
        }

        [Fact]
        public void ParRepetido_ExigeLetrasDistintas()
        {
            // "AAAA" es letra_repetida, no par_repetido.
            Assert.Equal(BlockNameHeuristics.LetraRepetida, BlockNameHeuristics.Suspicion("AAAA"));
        }
    }
}
