using MCPAutoCAD.GeometryCore;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests
{
    public class UndoCtlRulesTests
    {
        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(3, false)]
        [InlineData(5, true)]
        [InlineData(13, true)]
        [InlineData(7, false)]
        public void IsFullUndo_Bit1SinBit2(int undoctl, bool expected)
        {
            Assert.Equal(expected, UndoCtlRules.IsFullUndo(undoctl));
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(5, true)]
        [InlineData(3, false)]
        [InlineData(9, false)]
        [InlineData(13, false)]
        [InlineData(21, true)]
        public void CanRunNeutral_RequiereDeshacerCompletoYSinGrupoAbierto(int undoctl, bool expected)
        {
            Assert.Equal(expected, UndoCtlRules.CanRunNeutral(undoctl));
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(5, false)]
        [InlineData(3, true)]
        [InlineData(13, true)]
        public void NeutralSkipNeedsWarning_SoloConDeshacerActivo(int undoctl, bool expected)
        {
            Assert.Equal(expected, UndoCtlRules.NeutralSkipNeedsWarning(undoctl));
        }

        [Theory]
        [InlineData(5, null)]
        [InlineData(0, "deshacer desactivado")]
        [InlineData(3, "deshacer limitado a un paso")]
        [InlineData(13, "hay un grupo UNDO BEGIN abierto")]
        public void NeutralSkipReason_SegunBits(int undoctl, string? expected)
        {
            Assert.Equal(expected, UndoCtlRules.NeutralSkipReason(undoctl));
        }

        [Fact]
        public void NeutralSkippedWarning_TextoExacto()
        {
            Assert.Equal(
                "No se aisló el trazado en un grupo de deshacer neutro (UNDOCTL=13): el trazado podría haber dejado un paso en el historial de deshacer.",
                UndoCtlRules.NeutralSkippedWarning(13));
        }

        [Fact]
        public void NeutralCloseFailedWarning_TextoExacto()
        {
            Assert.Equal(
                "No se pudo cerrar o deshacer el grupo de deshacer neutro del trazado (X: y): puede quedar un paso de deshacer vacío en el historial.",
                UndoCtlRules.NeutralCloseFailedWarning("X: y"));
        }
    }
}
