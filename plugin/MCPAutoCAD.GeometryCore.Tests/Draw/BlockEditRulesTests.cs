using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class BlockEditRulesTests
    {
        [Fact]
        public void NormalizeBlockPatterns_TrimsDeduplicatesAndRejectsEmpty()
        {
            List<string> list = BlockEditRules.NormalizeBlockPatterns(new[] { " Muro* ", "muro*", "Puerta" });
            Assert.Equal(new[] { "Muro*", "Puerta" }, list);

            Assert.Throws<ArgumentException>(() => BlockEditRules.NormalizeBlockPatterns(Array.Empty<string>()));
            Assert.Throws<ArgumentException>(() => BlockEditRules.NormalizeBlockPatterns(new[] { "  " }));
        }

        [Fact]
        public void ValidateDeleteFilter_RejectsEmptyOrUniversalWithoutTypes()
        {
            Assert.NotNull(BlockEditRules.ValidateDeleteFilter(null, false));
            Assert.NotNull(BlockEditRules.ValidateDeleteFilter(new[] { "*" }, false));
            Assert.Null(BlockEditRules.ValidateDeleteFilter(new[] { "*" }, true));
            Assert.Null(BlockEditRules.ValidateDeleteFilter(new[] { "*DIMS*" }, false));
            Assert.Null(BlockEditRules.ValidateDeleteFilter(null, true));
            Assert.Null(BlockEditRules.ValidateDeleteFilter(null, false, hasHandles: true, hasContiene: false));
            Assert.Null(BlockEditRules.ValidateDeleteFilter(null, false, hasHandles: false, hasContiene: true));
        }

        [Fact]
        public void MatchesAnySubstring_CaseSensitivityAndNullSafe()
        {
            var needles = new[] { "Parqueadero", "Pozo de Luz" };
            Assert.True(BlockEditRules.MatchesAnySubstring("parqueadero privado", needles, ignoreCase: true));
            Assert.False(BlockEditRules.MatchesAnySubstring("parqueadero privado", needles, ignoreCase: false));
            Assert.True(BlockEditRules.MatchesAnySubstring("Parqueadero Privado", needles, ignoreCase: false));
            Assert.False(BlockEditRules.MatchesAnySubstring("Habitación 1", needles, ignoreCase: true));
            Assert.False(BlockEditRules.MatchesAnySubstring(null, needles, ignoreCase: true));
            Assert.False(BlockEditRules.MatchesAnySubstring("", needles, ignoreCase: true));
        }

        [Fact]
        public void CheckExpectedAndCheckCap_ValidateExactMatchesAndCeiling()
        {
            Assert.Null(BlockEditRules.CheckExpected(null, 42));
            Assert.Null(BlockEditRules.CheckExpected(42, 42));
            Assert.Contains("42", BlockEditRules.CheckExpected(10, 42)!);

            Assert.Null(BlockEditRules.CheckCap(BlockEditRules.MaxEntities));
            Assert.Contains("20000", BlockEditRules.CheckCap(BlockEditRules.MaxEntities + 1)!);
        }

        [Fact]
        public void HasWildcards_DetectsStarAndQuestionMark()
        {
            Assert.True(BlockEditRules.HasWildcards("A-*"));
            Assert.True(BlockEditRules.HasWildcards("P?"));
            Assert.False(BlockEditRules.HasWildcards("PuertaSimple"));
        }
    }
}
