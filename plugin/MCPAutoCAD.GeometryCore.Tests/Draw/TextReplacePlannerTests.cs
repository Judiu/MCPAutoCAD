using System;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class TextReplacePlannerTests
    {
        private static ReplaceResult M(string raw, string buscar, string reemplazar, bool ic = false, bool palabra = false)
            => TextReplacePlanner.Plan(raw, TextFlavor.Mtext, buscar, reemplazar, ic, palabra);

        private static ReplaceResult D(string raw, string buscar, string reemplazar, bool ic = false, bool palabra = false)
            => TextReplacePlanner.Plan(raw, TextFlavor.Dbtext, buscar, reemplazar, ic, palabra);

        [Fact]
        public void Mtext_UnderlineCodeIsKeptWhenReplacingWholeText()
        {
            // Caso real del brief: 92 rótulos "\LAREA TECNICA" → "\LCUARTO TECNICO" (el \L se conserva).
            ReplaceResult r = M("\\LAREA TECNICA", "AREA TECNICA", "CUARTO TECNICO");
            Assert.Equal(ReplaceOutcome.Reemplazar, r.Outcome);
            Assert.Equal("\\LCUARTO TECNICO", r.NuevoRaw);
            Assert.Equal("CUARTO TECNICO", r.VisibleDespues);
            Assert.Equal("AREA TECNICA", r.VisibleAntes);
            Assert.Equal(1, r.Reemplazos);
            Assert.Equal(0, r.Omitidas);
        }

        [Fact]
        public void Mtext_ReplacementIsEscapedAsLiteral()
        {
            ReplaceResult r = M("\\LX", "X", "A{B}\\C");
            Assert.Equal(ReplaceOutcome.Reemplazar, r.Outcome);
            Assert.Equal("\\LA\\{B\\}\\\\C", r.NuevoRaw);
            Assert.Equal("A{B}\\C", r.VisibleDespues);
        }

        [Fact]
        public void Mtext_MatchInsideGroupKeepsGroupFormat()
        {
            ReplaceResult r = M("AREA {\\fArial|b1;TECNICA}", "TECNICA", "CUARTO");
            Assert.Equal(ReplaceOutcome.Reemplazar, r.Outcome);
            Assert.Equal("AREA {\\fArial|b1;CUARTO}", r.NuevoRaw);
        }

        [Fact]
        public void Mtext_MatchCrossingGroupBoundaryIsOmitted()
        {
            ReplaceResult r = M("{\\fArial;AREA} TECNICA", "AREA TECNICA", "CUARTO TECNICO");
            Assert.Equal(ReplaceOutcome.Omitida, r.Outcome);
            Assert.Equal(0, r.Reemplazos);
            Assert.Equal(1, r.Omitidas);
            Assert.Null(r.NuevoRaw);
            Assert.Contains(TextReplacePlanner.MotivoCruzaFormato, r.Motivos);
        }

        [Fact]
        public void Mtext_MatchCrossingUnderlineCodeIsOmitted()
        {
            ReplaceResult r = M("AREA\\LTECNICA", "AREATECNICA", "X");
            Assert.Equal(ReplaceOutcome.Omitida, r.Outcome);
            Assert.Equal(1, r.Omitidas);
            Assert.Null(r.NuevoRaw);
        }

        [Fact]
        public void Mtext_MixedOccurrencesReplacesLiteralOnesAndReportsTheRest()
        {
            ReplaceResult r = M("AREA TECNICA AR{E}A", "AREA", "X");
            Assert.Equal(ReplaceOutcome.Reemplazar, r.Outcome);
            Assert.Equal(1, r.Reemplazos);
            Assert.Equal(1, r.Omitidas);
            Assert.Equal("X TECNICA AR{E}A", r.NuevoRaw);
            Assert.Equal("X TECNICA AREA", r.VisibleDespues);
        }

        [Fact]
        public void Mtext_AllOccurrencesOmittedMeansNoWrite()
        {
            ReplaceResult r = M("AR{E}A", "AREA", "X");
            Assert.Equal(ReplaceOutcome.Omitida, r.Outcome);
            Assert.Equal(0, r.Reemplazos);
            Assert.Equal(1, r.Omitidas);
            Assert.Null(r.NuevoRaw);
            Assert.Null(r.Error);
        }

        [Fact]
        public void Mtext_StackedFractionCannotBeTouched()
        {
            ReplaceResult r = M("\\S1^2; X", "1", "3");
            Assert.Equal(ReplaceOutcome.Omitida, r.Outcome);
            Assert.Contains(TextReplacePlanner.MotivoNoSustituible, r.Motivos);
        }

        [Fact]
        public void Mtext_NonBreakingSpaceBlocksMatchAcrossIt()
        {
            ReplaceResult r = M("A\\~B", "A B", "X");
            Assert.Equal(ReplaceOutcome.Omitida, r.Outcome);
            Assert.Equal(1, r.Omitidas);
        }

        [Fact]
        public void Mtext_ParagraphBreakBlocksMatchAcrossIt()
        {
            ReplaceResult r = M("AREA\\PTECNICA AREA", "AREA", "X");
            Assert.Equal(ReplaceOutcome.Reemplazar, r.Outcome);
            Assert.Equal(2, r.Reemplazos);
            Assert.Equal("X\\PTECNICA X", r.NuevoRaw);
        }

        [Fact]
        public void Mtext_MultipleOccurrencesAllReplaced()
        {
            ReplaceResult r = M("AREA \\LAREA", "AREA", "X");
            Assert.Equal(2, r.Reemplazos);
            Assert.Equal("X \\LX", r.NuevoRaw);
            Assert.Equal("X X", r.VisibleDespues);
        }

        [Fact]
        public void Mtext_NoMatchGivesSinCoincidencias()
        {
            ReplaceResult r = M("\\LCOCINA", "AREA", "X");
            Assert.Equal(ReplaceOutcome.SinCoincidencias, r.Outcome);
            Assert.Equal(0, r.Coincidencias);
            Assert.Null(r.NuevoRaw);
        }

        [Fact]
        public void Mtext_CaseSensitiveByDefault()
        {
            Assert.Equal(ReplaceOutcome.SinCoincidencias, M("\\Larea", "AREA", "X").Outcome);
            ReplaceResult r = M("\\Larea", "AREA", "X", ic: true);
            Assert.Equal("\\LX", r.NuevoRaw);
        }

        [Fact]
        public void Mtext_AccentsCountWhenIgnoringCase()
        {
            Assert.Equal(ReplaceOutcome.SinCoincidencias, M("canción", "cancion", "x", ic: true).Outcome);
            Assert.Equal("x", M("CANCIÓN", "canción", "x", ic: true).NuevoRaw);
        }

        [Fact]
        public void Mtext_WholeWordOption()
        {
            ReplaceResult r = M("AREAS AREA", "AREA", "X", palabra: true);
            Assert.Equal(1, r.Reemplazos);
            Assert.Equal("AREAS X", r.NuevoRaw);
        }

        [Fact]
        public void Mtext_WholeWordLooksAcrossFormatCodesVisibly()
        {
            // «AREA» seguido de un código invisible y luego «S»: visualmente la palabra continúa, no es palabra completa.
            ReplaceResult r = M("AREA\\LS", "AREA", "X", palabra: true);
            Assert.Equal(ReplaceOutcome.SinCoincidencias, r.Outcome);
        }

        [Fact]
        public void Mtext_EmptyReplacementDeletesVisibleText()
        {
            ReplaceResult r = M("\\LAREA TECNICA", "AREA ", "");
            Assert.Equal("\\LTECNICA", r.NuevoRaw);
            Assert.Equal("TECNICA", r.VisibleDespues);
        }

        [Fact]
        public void Mtext_IdenticalReplacementLeavesRawUnchanged()
        {
            ReplaceResult r = M("\\LAREA", "AREA", "AREA");
            Assert.Equal(ReplaceOutcome.Reemplazar, r.Outcome);
            Assert.Equal("\\LAREA", r.NuevoRaw);
        }

        [Fact]
        public void Mtext_MalformedContentIsWholeEntityError()
        {
            ReplaceResult r = M("\\zAREA", "AREA", "X");
            Assert.Equal(ReplaceOutcome.Omitida, r.Outcome);
            Assert.NotNull(r.Error);
            Assert.Null(r.NuevoRaw);
            Assert.Contains("códigos no reconocidos", r.Error);
        }

        [Fact]
        public void Mtext_UnbalancedBraceIsWholeEntityError()
        {
            ReplaceResult r = M("AREA}", "AREA", "X");
            Assert.Equal(ReplaceOutcome.Omitida, r.Outcome);
            Assert.NotNull(r.Error);
        }

        [Fact]
        public void Mtext_ReplacementWithFormatPrefixStaysBeforeGroup()
        {
            // Reemplazo que empieza por una letra tras un código: no se confunde con el código.
            ReplaceResult r = M("\\fArial;AREA", "AREA", "LX");
            Assert.Equal("\\fArial;LX", r.NuevoRaw);
            Assert.Equal("LX", r.VisibleDespues);
        }

        [Fact]
        public void Mtext_UnicodeEscapeMatchIsReplacedAsLiteral()
        {
            // \U+00F1 es un carácter literal para la búsqueda; se sustituye por texto plano.
            ReplaceResult r = M("A\\U+00F1O", "AñO", "ANIO", ic: false);
            Assert.Equal(ReplaceOutcome.Reemplazar, r.Outcome);
            Assert.Equal("ANIO", r.NuevoRaw);
        }

        [Fact]
        public void Mtext_NewlineInParametersThrows()
        {
            Assert.Throws<ArgumentException>(() => M("AREA", "A\nB", "X"));
            Assert.Throws<ArgumentException>(() => M("AREA", "AREA", "X\r"));
        }

        [Fact]
        public void Mtext_EmptyBuscarThrows()
        {
            Assert.Throws<ArgumentException>(() => M("AREA", "", "X"));
        }

        [Fact]
        public void Mtext_MatchCrossingFieldIsOmitted()
        {
            ReplaceResult r = M("AB%<\\AcObjProp>%C", "ABC", "X");
            Assert.Equal(ReplaceOutcome.Omitida, r.Outcome);
            Assert.Equal(1, r.Omitidas);
        }

        [Fact]
        public void Dbtext_PlainReplacement()
        {
            ReplaceResult r = D("AREA TECNICA", "AREA TECNICA", "CUARTO TECNICO");
            Assert.Equal("CUARTO TECNICO", r.NuevoRaw);
        }

        [Fact]
        public void Dbtext_DegreeSymbolIsMatchedByVisibleAndReplacedWhole()
        {
            ReplaceResult r = D("%%dC", "°C", "grados C");
            Assert.Equal(ReplaceOutcome.Reemplazar, r.Outcome);
            Assert.Equal("grados C", r.NuevoRaw);
            Assert.Equal("grados C", r.VisibleDespues);
        }

        [Fact]
        public void Dbtext_ReplacementPercentIsEscaped()
        {
            ReplaceResult r = D("X", "X", "%%d");
            Assert.Equal("%%%%%%d", r.NuevoRaw);
            Assert.Equal("%%d", r.VisibleDespues);
        }

        [Fact]
        public void Dbtext_UnderlineCodeKept()
        {
            ReplaceResult r = D("%%uAREA%%u", "AREA", "B");
            Assert.Equal("%%uB%%u", r.NuevoRaw);
        }

        [Fact]
        public void Dbtext_MatchCrossingUnderlineToggleIsOmitted()
        {
            ReplaceResult r = D("AR%%uEA", "AREA", "B");
            Assert.Equal(ReplaceOutcome.Omitida, r.Outcome);
            Assert.Equal(1, r.Omitidas);
        }

        [Fact]
        public void Dbtext_PercentCharacterReplacement()
        {
            ReplaceResult r = D("50%%%", "%", "por ciento");
            Assert.Equal("50por ciento", r.NuevoRaw);
        }

        [Fact]
        public void Dbtext_BackslashAndBracesAreLiteral()
        {
            ReplaceResult r = D("A\\B{C}", "B{C}", "X");
            Assert.Equal("A\\X", r.NuevoRaw);
        }

        [Fact]
        public void Dbtext_ReplacementCannotCreateNewCodes()
        {
            // Sustituir "A" por "%%d" dentro de texto escapado: el resultado se escribe como literal "%%d" visible.
            ReplaceResult r = D("A", "A", "%%d");
            Assert.Equal(ReplaceOutcome.Reemplazar, r.Outcome);
            Assert.Equal("%%d", r.VisibleDespues);
            Assert.Equal("%%%%%%d", r.NuevoRaw);
        }

        [Fact]
        public void Mtext_CruzarFormato_CasoRealRotuloFallo49()
        {
            // Caso real fallo 49: "{\\H2x;1} de 4" buscando "1 de" -> "2 de" (y "10 de").
            ReplaceResult r1 = TextReplacePlanner.Plan("{\\H2x;1} de 4", TextFlavor.Mtext, "1 de", "2 de", false, false, cruzarFormato: true);
            Assert.Equal(ReplaceOutcome.Reemplazar, r1.Outcome);
            Assert.Equal("{\\H2x;2} de 4", r1.NuevoRaw);
            Assert.Equal("2 de 4", r1.VisibleDespues);

            // Inserción pura en prefijo común: "1 de" -> "10 de"
            ReplaceResult r2 = TextReplacePlanner.Plan("{\\H2x;1} de 4", TextFlavor.Mtext, "1 de", "10 de", false, false, cruzarFormato: true);
            Assert.Equal(ReplaceOutcome.Reemplazar, r2.Outcome);
            Assert.Equal("{\\H2x;10} de 4", r2.NuevoRaw);
            Assert.Equal("10 de 4", r2.VisibleDespues);

            // Cambio completo cruzando grupo: conserva las llaves y formato del primer carácter.
            ReplaceResult r3 = TextReplacePlanner.Plan("{\\fArial;AREA} TECNICA", TextFlavor.Mtext, "AREA TECNICA", "CUARTO TECNICO", false, false, cruzarFormato: true);
            Assert.Equal(ReplaceOutcome.Reemplazar, r3.Outcome);
            Assert.Equal("{\\fArial;CUARTO TECNICO}", r3.NuevoRaw);
            Assert.Equal("CUARTO TECNICO", r3.VisibleDespues);

            // Sin cruzarFormato (defecto), sigue omitiéndose
            ReplaceResult r4 = TextReplacePlanner.Plan("{\\H2x;1} de 4", TextFlavor.Mtext, "1 de", "2 de", false, false);
            Assert.Equal(ReplaceOutcome.Omitida, r4.Outcome);
            Assert.Contains(TextReplacePlanner.MotivoCruzaFormato, r4.Motivos);
        }

        [Fact]
        public void Mtext_CruzarFormato_OpaqueYSinFlag()
        {
            ReplaceResult rOpaque = TextReplacePlanner.Plan("A\\S1^2;B", TextFlavor.Mtext, "A1/2B", "X", false, false, cruzarFormato: true);
            Assert.Equal(ReplaceOutcome.Omitida, rOpaque.Outcome);
            Assert.Contains(TextReplacePlanner.MotivoNoSustituible, rOpaque.Motivos);

            ReplaceResult rDb = TextReplacePlanner.Plan("AR%%uEA", TextFlavor.Dbtext, "AREA", "B", false, false, cruzarFormato: true);
            Assert.Equal(ReplaceOutcome.Reemplazar, rDb.Outcome);
            Assert.Equal("B%%u", rDb.NuevoRaw);
            Assert.Equal("B", rDb.VisibleDespues);
        }
    }
}
