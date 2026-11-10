using System.Text;
using MCPAutoCAD.GeometryCore.Lisp;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Lisp
{
    /// <summary>Plugin 0.8.5: lo que se muestra en el diálogo de aprobación debe ser fielmente lo que se carga.</summary>
    public class LispDisplayTests
    {
        [Fact]
        public void CodigoNormal_SinProblemas()
        {
            Assert.Empty(LispRules.DisplayProblems("; comentario\r\n(defun c:X ()\n\t(princ \"ñ\"))\n"));
            Assert.Empty(LispRules.DisplayProblems("\uFEFF(princ)"));
        }

        [Theory]
        [InlineData(";comentario\r(vl-file-delete \"x\")")]
        [InlineData("(princ)\u0007")]
        [InlineData("(princ \"a\u202Eb\")")]
        [InlineData("(princ \"a\u200Bb\")")]
        [InlineData("(princ)\uFEFF")]
        [InlineData("(princ \"a\u00ADb\")")]
        [InlineData("(princ \"a\u061Cb\")")]
        [InlineData("(princ \"a\u2028b\")")]
        [InlineData("(princ \"a\uFE0Fb\")")]
        [InlineData("(princ \"a\u3164b\")")]
        [InlineData("(princ \"a\uDB40\uDC41b\")")]
        public void CodigoEngañoso_Rechazado(string codigo) => Assert.NotEmpty(LispRules.DisplayProblems(codigo));

        [Fact]
        public void LineaLarga_Rechazada()
        {
            Assert.Empty(LispRules.DisplayProblems(new string(' ', 200) + "\n"));
            Assert.Single(LispRules.DisplayProblems(new string(' ', 201) + "(startapp \"cmd\")"));
        }

        [Fact]
        public void LineasEnBlanco_HastaDiez()
        {
            Assert.Empty(LispRules.DisplayProblems("(princ)" + new string('\n', 11) + "(princ)"));
            Assert.Single(LispRules.DisplayProblems("(princ)" + new string('\n', 12) + "(startapp \"cmd\")"));
            Assert.Single(LispRules.DisplayProblems("(princ)\n" + string.Concat(System.Linq.Enumerable.Repeat("   \r\n", 11)) + "(x)"));
        }

        [Fact]
        public void Utf8Valido()
        {
            Assert.True(LispRules.IsValidUtf8(Encoding.UTF8.GetBytes("(princ \"año\")")));
            Assert.False(LispRules.IsValidUtf8(Encoding.Latin1.GetBytes("(princ \"año\")")));
        }

        [Theory]
        [InlineData("NUL")]
        [InlineData("con")]
        [InlineData("COM1")]
        [InlineData("lpt9")]
        [InlineData("acad")]
        [InlineData("acaddoc")]
        [InlineData("ACAD2027doc")]
        [InlineData("abc\n")]
        public void NombreReservadoOConSalto_Rechazado(string nombre) => Assert.False(LispRules.IsValidLispName(nombre));

        [Theory]
        [InlineData("COM")]
        [InlineData("COM10")]
        [InlineData("console")]
        [InlineData("borrexc2")]
        public void NombreParecido_Admitido(string nombre) => Assert.True(LispRules.IsValidLispName(nombre));

        [Fact]
        public void ComandoYHashConSaltoFinal_Rechazados()
        {
            Assert.False(LispRules.IsValidCommandName("REGEN\n"));
            Assert.False(LispRules.IsValidArgument("_E\n"));
            PinFile f = LispRules.ParsePins("{ \"lisp\": [ { \"ruta\": \"C:\\\\x\\\\a.lsp\", \"sha256\": \"" + new string('a', 64) + "\\n\" } ] }");
            Assert.Empty(f.Lisp);
        }
    }
}
