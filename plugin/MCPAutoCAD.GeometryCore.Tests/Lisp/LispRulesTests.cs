using System.Collections.Generic;
using System.Text;
using MCPAutoCAD.GeometryCore.Lisp;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Lisp
{
    public class LispRulesTests
    {
        private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Theory]
        [InlineData(@"C:\LPSAPP\lisp\BORREXC.lsp")]
        [InlineData(@"C:/LPSAPP/lisp/x.FAS")]
        [InlineData(@"\\servidor\lisp\x.vlx")]
        public void RutaValida(string ruta) => Assert.Null(LispRules.ValidateLispPath(ruta));

        [Theory]
        [InlineData(@"lisp\x.lsp")]
        [InlineData(@"C:\LPSAPP\..\x.lsp")]
        [InlineData(@"C:\LPSAPP\*.lsp")]
        [InlineData("C:\\LPSAPP\\a\"b.lsp")]
        [InlineData(@"C:\LPSAPP\x.dwg")]
        [InlineData(@"C:\LPSAPP\x.lsp.txt")]
        public void RutaInvalida(string ruta) => Assert.NotNull(LispRules.ValidateLispPath(ruta));

        [Fact]
        public void Sha256_Conocido()
        {
            Assert.Equal(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                LispRules.Sha256Hex(Encoding.ASCII.GetBytes("abc")));
        }

        [Fact]
        public void DefinedCommands_DetectaYDeduplica()
        {
            string src = "(defun c:BorrExc ( / ss) (princ))\n(DEFUN  C:otro () nil)\n(defun aux () nil)\n(defun c:BORREXC () nil)";
            Assert.Equal(new List<string> { "BORREXC", "OTRO" }, LispRules.DefinedCommands(src));
        }

        [Fact]
        public void FoundSensitive_LlamadasNoSubcadenas()
        {
            string src = "(startapp \"cmd\") (setq openfile 1) (vl-registry-write \"k\") (dos_dir) (command \"_.LINE\")";
            List<string> f = LispRules.FoundSensitive(src);
            Assert.Contains("startapp", f);
            Assert.Contains("vl-registry-write", f);
            Assert.Contains("dos_", f);
            Assert.Contains("command", f);
            Assert.DoesNotContain("open", f);
            Assert.DoesNotContain("command-s", f);
        }

        [Theory]
        [InlineData("_E", true)]
        [InlineData("Sí", true)]
        [InlineData("", true)]
        [InlineData("@10<45", true)]
        [InlineData("0,0", true)]
        [InlineData("a b", false)]
        [InlineData("(command)", false)]
        [InlineData("!x", false)]
        [InlineData("'zoom", false)]
        [InlineData("a;b", false)]
        [InlineData("\"x\"", false)]
        public void Argumento(string arg, bool ok) => Assert.Equal(ok, LispRules.IsValidArgument(arg));

        [Fact]
        public void Comandos_NombreYProhibidos()
        {
            Assert.True(LispRules.IsValidCommandName("BORREXC"));
            Assert.False(LispRules.IsValidCommandName("_.ZOOM"));
            Assert.False(LispRules.IsValidCommandName("ZOOM E"));
            Assert.True(LispRules.IsDenied("shell"));
            Assert.True(LispRules.IsDenied("NETLOAD"));
            Assert.True(LispRules.IsDenied("SECURELOAD"));
            Assert.False(LispRules.IsDenied("REGEN"));
            Assert.True(LispRules.IsDenied("_SHELL"));
            Assert.True(LispRules.IsDenied("_.NETLOAD"));
            Assert.True(LispRules.IsDenied("-PLOT"));
            Assert.True(LispRules.IsDenied("SCRIPTCALL"));
            Assert.False(LispRules.IsDenied("_E"));
            Assert.False(LispRules.IsDenied("_W"));
            Assert.False(LispRules.IsDenied("-W"));
            Assert.True(LispRules.IsDenied("W"));
            Assert.True(LispRules.IsDenied("_SH"));
        }

        [Theory]
        [InlineData("ATTOUT")]
        [InlineData("_.COMPILE")]
        [InlineData("-RENDER")]
        [InlineData("GOTOURL")]
        [InlineData("logfilemode")]
        [InlineData("CONVERTPSTYLES")]
        public void Comandos_AmpliacionDeLaListaDenegada(string comando) => Assert.True(LispRules.IsDenied(comando));

        [Fact]
        public void ParsePins_RespuestaConPrefijoProhibida()
        {
            PinFile f = LispRules.ParsePins(@"{ ""comandos"": [ { ""comando"": ""BORREXC"", ""argumentos"": [""_SHELL""] } ] }");
            Assert.Empty(f.Comandos);
            Assert.Single(f.Errores);
        }

        [Fact]
        public void DecodeSource_Utf8YLatin1()
        {
            Assert.Equal("(defun c:AÑO ()", LispRules.DecodeSource(Encoding.UTF8.GetBytes("(defun c:AÑO ()")));
            Assert.Equal("(defun c:AÑO ()", LispRules.DecodeSource(Encoding.Latin1.GetBytes("(defun c:AÑO ()")));
        }

        [Theory]
        [InlineData(@"\\?\C:\x.lsp")]
        [InlineData(@"\\.\C:\x.lsp")]
        [InlineData(@"C:x.lsp")]
        public void RutaDispositivoORelativa(string ruta) => Assert.NotNull(LispRules.ValidateLispPath(ruta));

        [Fact]
        public void BuildCommandString_EspaciosComoIntro()
        {
            Assert.Equal("ZOOM _E ", LispRules.BuildCommandString("ZOOM", new[] { "_E" }));
            Assert.Equal("BORREXC ", LispRules.BuildCommandString("BORREXC", new string[0]));
            Assert.Equal("X a  ", LispRules.BuildCommandString("X", new[] { "a", "" }));
        }

        [Fact]
        public void BuildLoadExpression_BarrasNormales()
        {
            Assert.Equal("(load \"C:/LPSAPP/lisp/B.lsp\") ", LispRules.BuildLoadExpression(@"C:\LPSAPP\lisp\B.lsp"));
        }

        [Fact]
        public void ParsePins_EntradasValidasEInvalidas()
        {
            string json = @"{
  // comentario
  ""lisp"": [
    { ""ruta"": ""C:\\LPSAPP\\lisp\\B.lsp"", ""sha256"": """ + Sha.ToUpperInvariant() + @""" },
    { ""ruta"": ""C:\\LPSAPP\\..\\x.lsp"", ""sha256"": """ + Sha + @""" },
    { ""ruta"": ""C:\\LPSAPP\\lisp\\C.lsp"", ""sha256"": ""abc"" }
  ],
  ""comandos"": [
    ""regen"",
    { ""comando"": ""ZOOM"", ""argumentos"": [""_E""] },
    ""SHELL"",
    { ""comando"": ""ZOOM"", ""argumentos"": [""(x)""] },
    { ""comando"": ""BORREXC"", ""argumentos"": [""SHELL""] },
    42,
  ]
}";
            PinFile f = LispRules.ParsePins(json);
            Assert.Single(f.Lisp);
            Assert.Equal(Sha, f.Lisp[0].Sha256);
            Assert.Equal(2, f.Comandos.Count);
            Assert.Equal("REGEN", f.Comandos[0].Comando);
            Assert.Equal(6, f.Errores.Count);

            Assert.True(LispRules.IsPinned(f, @"c:/lpsapp/LISP/b.lsp", Sha));
            Assert.False(LispRules.IsPinned(f, @"C:\LPSAPP\lisp\B.lsp", Sha.Replace('0', '1')));
            Assert.NotNull(LispRules.FindCommand(f, "zoom", new[] { "_E" }));
            Assert.Null(LispRules.FindCommand(f, "ZOOM", new[] { "_e" }));
            Assert.Null(LispRules.FindCommand(f, "ZOOM", new string[0]));
            Assert.NotNull(LispRules.FindCommand(f, "REGEN", new string[0]));
            Assert.Null(LispRules.FindCommand(f, "REGEN", new[] { "" }));
        }

        [Theory]
        [InlineData("BORREXC", true)]
        [InlineData("borrar-excepto_2", true)]
        [InlineData("", false)]
        [InlineData("_x", false)]
        [InlineData("a.lsp", false)]
        [InlineData("..\\x", false)]
        [InlineData("a b", false)]
        public void NombreLisp(string nombre, bool ok) => Assert.Equal(ok, LispRules.IsValidLispName(nombre));

        [Fact]
        public void ParsePins_JsonRoto()
        {
            PinFile f = LispRules.ParsePins("{ no es json");
            Assert.Empty(f.Lisp);
            Assert.Single(f.Errores);
        }

        [Fact]
        public void TrustedPaths_ExactaYSubcarpetas()
        {
            string tp = @"C:\Otra;C:\LPSAPP\lisp\;D:\Raiz\...";
            Assert.True(LispRules.IsInTrustedPaths(@"C:\LPSAPP\lisp\B.lsp", tp));
            Assert.False(LispRules.IsInTrustedPaths(@"C:\LPSAPP\lisp\sub\B.lsp", tp));
            Assert.False(LispRules.IsInTrustedPaths(@"C:\LPSAPP\lisp2\B.lsp", tp));
            Assert.True(LispRules.IsInTrustedPaths(@"D:\Raiz\B.lsp", tp));
            Assert.True(LispRules.IsInTrustedPaths(@"D:\Raiz\a\b\B.lsp", tp));
            Assert.False(LispRules.IsInTrustedPaths(@"D:\Raiz2\B.lsp", tp));
            Assert.False(LispRules.IsInTrustedPaths(@"C:\LPSAPP\lisp\B.lsp", ""));
        }
    }
}
