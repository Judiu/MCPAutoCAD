using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class TextFormatParserTests
    {
        private static List<TextUnit> Mtext(string raw) => TextFormatParser.Parse(raw, TextFlavor.Mtext);

        private static List<TextUnit> Db(string raw) => TextFormatParser.Parse(raw, TextFlavor.Dbtext);

        /// <summary>Invariante: los trozos brutos cubren el contenido sin huecos ni solapes.</summary>
        private static void AssertCoversRaw(string raw, List<TextUnit> units)
        {
            int pos = 0;
            var sb = new System.Text.StringBuilder();
            foreach (TextUnit u in units)
            {
                Assert.Equal(pos, u.RawStart);
                pos = u.RawEnd;
                sb.Append(u.Raw);
            }

            Assert.Equal(raw.Length, pos);
            Assert.Equal(raw, sb.ToString());
        }

        [Theory]
        [InlineData("AREA TECNICA", "AREA TECNICA")]
        [InlineData("", "")]
        [InlineData("\\LAREA TECNICA", "AREA TECNICA")]
        [InlineData("{\\fArial|b1;TEXTO}", "TEXTO")]
        [InlineData("A\\PB", "A\nB")]
        [InlineData("A\\X\\NB", "A\n\nB")]
        [InlineData("\\\\ \\{ \\}", "\\ { }")]
        [InlineData("\\S1^2;", "1/2")]
        [InlineData("\\S1/2;", "1/2")]
        [InlineData("\\S1#2;", "1/2")]
        [InlineData("\\S12;", "12")]
        [InlineData("a\\~b", "a b")]
        [InlineData("\\U+00F1", "\u00F1")]
        [InlineData("\\H2.5;\\C1;\\pxql;texto\\A1;", "texto")]
        [InlineData("\\Lx\\lY\\Oz\\oW\\Kq\\kr", "xYzWqr")]
        [InlineData("ab\r\ncd", "ab\ncd")]
        public void Mtext_VisibleText(string raw, string visible)
        {
            List<TextUnit> units = Mtext(raw);
            Assert.Equal(visible, TextFormatParser.VisibleText(units));
            AssertCoversRaw(raw, units);
        }

        [Fact]
        public void Mtext_LOnlyCodeIsFormat()
        {
            List<TextUnit> units = Mtext("\\LAREA");
            Assert.Equal(UnitKind.Format, units[0].Kind);
            Assert.Equal("\\L", units[0].Raw);
            Assert.Equal(UnitKind.Literal, units[1].Kind);
            Assert.Equal("A", units[1].Visible);
        }

        [Fact]
        public void Mtext_BracesAndFontAreFormatUnits()
        {
            List<TextUnit> units = Mtext("{\\fArial|b1;T}");
            Assert.Equal(new[] { UnitKind.Format, UnitKind.Format, UnitKind.Literal, UnitKind.Format }, Kinds(units));
            Assert.Equal("\\fArial|b1;", units[1].Raw);
        }

        [Fact]
        public void Mtext_StackedIsOpaqueAndParagraphIsOpaque()
        {
            List<TextUnit> units = Mtext("\\S1^2;\\P");
            Assert.Equal(UnitKind.Opaque, units[0].Kind);
            Assert.Equal("\\S1^2;", units[0].Raw);
            Assert.Equal(UnitKind.Opaque, units[1].Kind);
            Assert.Equal("\n", units[1].Visible);
        }

        [Fact]
        public void Mtext_EscapedBackslashIsLiteralAndTwoCharsRaw()
        {
            List<TextUnit> units = Mtext("\\\\");
            Assert.Single(units);
            Assert.Equal(UnitKind.Literal, units[0].Kind);
            Assert.Equal("\\", units[0].Visible);
            Assert.Equal("\\\\", units[0].Raw);
        }

        [Theory]
        [InlineData("\\zZ")]
        [InlineData("\\M+1")]
        [InlineData("\\fArial")]
        [InlineData("\\H2.5")]
        [InlineData("{abc")]
        [InlineData("abc}")]
        [InlineData("a}b{c")]
        [InlineData("\\U+12")]
        [InlineData("\\U00F1")]
        [InlineData("\\U+GGGG")]
        [InlineData("abc\\")]
        public void Mtext_MalformedThrows(string raw)
        {
            Assert.Throws<TextFormatException>(() => Mtext(raw));
            Assert.NotNull(TextFormatParser.ValidateMText(raw));
        }

        [Theory]
        [InlineData("\\LAREA TECNICA}")]
        [InlineData("{\\L{\\fA;x}")]
        public void Mtext_BracesUnbalancedOrClosedTooEarlyThrow(string raw)
        {
            Assert.NotNull(TextFormatParser.ValidateMText(raw));
        }

        [Theory]
        [InlineData("\\LAREA")]
        [InlineData("{\\fArial|b1;TEXTO}")]
        [InlineData("a\\\\b\\{c\\}")]
        [InlineData("\\S1^2;")]
        [InlineData("\\U+00F1x")]
        [InlineData("")]
        public void Mtext_ValidIsNullFromValidate(string raw)
        {
            Assert.Null(TextFormatParser.ValidateMText(raw));
        }

        [Fact]
        public void Mtext_ValidateReportsPosition()
        {
            string? msg = TextFormatParser.ValidateMText("ab\\zc");
            Assert.NotNull(msg);
            Assert.Contains("posición 2", msg);
        }

        [Fact]
        public void Mtext_UnicodeEscapeYieldsLiteralCharAndRawSeven()
        {
            List<TextUnit> units = Mtext("x\\U+00F1y");
            Assert.Equal(UnitKind.Literal, units[1].Kind);
            Assert.Equal("\u00F1", units[1].Visible);
            Assert.Equal("\\U+00F1", units[1].Raw);
            AssertCoversRaw("x\\U+00F1y", units);
        }

        [Theory]
        [InlineData("%%d", "\u00B0")]
        [InlineData("%%D", "\u00B0")]
        [InlineData("%%c", "\u2300")]
        [InlineData("%%p", "\u00B1")]
        [InlineData("%%%", "%")]
        [InlineData("%%%%%%", "%%")]
        [InlineData("50%", "50%")]
        [InlineData("%%123", "{")]
        [InlineData("%%065B", "AB")]
        [InlineData("a%%x", "a%%x")]
        [InlineData("%%12", "%%12")]
        [InlineData("%%", "%%")]
        [InlineData("%", "%")]
        [InlineData("a%%%b", "a%b")]
        public void Dbtext_VisibleText(string raw, string visible)
        {
            List<TextUnit> units = Db(raw);
            Assert.Equal(visible, TextFormatParser.VisibleText(units));
            AssertCoversRaw(raw, units);
        }

        [Fact]
        public void Dbtext_UnderlineOverlineAreFormat()
        {
            List<TextUnit> units = Db("%%uA%%o");
            Assert.Equal(new[] { UnitKind.Format, UnitKind.Literal, UnitKind.Format }, Kinds(units));
            Assert.Equal("%%u", units[0].Raw);
        }

        [Fact]
        public void Dbtext_DegreeIsLiteralWithThreeRawChars()
        {
            List<TextUnit> units = Db("%%d");
            Assert.Single(units);
            Assert.Equal(UnitKind.Literal, units[0].Kind);
            Assert.Equal("%%d", units[0].Raw);
        }

        [Fact]
        public void Dbtext_NeverThrowsOnBackslashOrBraces()
        {
            List<TextUnit> units = Db("\\L{x}\\P");
            Assert.Equal("\\L{x}\\P", TextFormatParser.VisibleText(units));
        }

        [Fact]
        public void Mtext_FieldIsOpaqueAndNestingCounted()
        {
            string raw = "A%<\\AcObjProp Object(%<\\_ObjId 2199>%).Text >%B";
            List<TextUnit> units = Mtext(raw);
            Assert.Equal(new[] { UnitKind.Literal, UnitKind.Opaque, UnitKind.Literal }, Kinds(units));
            Assert.Equal("AB", TextFormatParser.VisibleText(units));
            AssertCoversRaw(raw, units);
        }

        [Fact]
        public void Mtext_UnclosedFieldIsLiteralPercentLess()
        {
            // "%<" sin ">%" no es un campo: se trata como texto literal, sin omitir la entidad.
            List<TextUnit> units = Mtext("A%<AcObjProp");
            Assert.Null(TextFormatParser.ValidateMText("A%<AcObjProp"));
            Assert.Equal("A%<AcObjProp", TextFormatParser.VisibleText(units));
            Assert.All(units, u => Assert.NotEqual(UnitKind.Opaque, u.Kind));
        }

        [Fact]
        public void Mtext_MPlusFiveDigitsIsOpaque()
        {
            List<TextUnit> units = Mtext("\\M+12345X");
            Assert.Equal(UnitKind.Opaque, units[0].Kind);
            Assert.Equal("\\M+12345", units[0].Raw);
            Assert.Equal("X", TextFormatParser.VisibleText(units));
            Assert.NotNull(TextFormatParser.ValidateMText("\\M+1234X"));
        }

        [Fact]
        public void Dbtext_FieldIsOpaque()
        {
            List<TextUnit> units = Db("A%<\\AcObjProp>%B");
            Assert.Equal(UnitKind.Opaque, units[1].Kind);
            Assert.Equal("AB", TextFormatParser.VisibleText(units));
        }

        [Theory]
        [InlineData("{abc}", null)]
        [InlineData("a{b", "'{' sin '}' de cierre.")]
        [InlineData("a\\{b", null)]
        [InlineData("a\\}b", null)]
        [InlineData("}", "'}' sin '{' de apertura (para un corchete literal use \\}).")]
        public void ValidateBraces_Balance(string raw, string? esperado)
        {
            Assert.Equal(esperado, TextFormatParser.ValidateBraces(raw));
        }

        private static UnitKind[] Kinds(List<TextUnit> units)
        {
            var arr = new UnitKind[units.Count];
            for (int i = 0; i < units.Count; i++)
            {
                arr[i] = units[i].Kind;
            }

            return arr;
        }
    }
}
