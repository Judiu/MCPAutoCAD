using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    /// <summary>protocol.md §5G.1 y §5G.6: lógica pura de las propiedades dinámicas.</summary>
    public class DynamicPropertyRulesTests
    {
        private static DynamicPropertyInfo Prop(
            string nombre,
            object? valor,
            bool soloLectura = false,
            string unidades = DynamicPropertyRules.UnidadesDistancia) =>
            new DynamicPropertyInfo(nombre, valor, soloLectura, unidades, null, "");

        private static DynamicPropertyInfo? Find(IReadOnlyList<DynamicPropertyInfo> props, double nominal) =>
            DynamicPropertyRules.FindWidthProperty(props, nominal).Propiedad;

        [Fact]
        public void Deduccion_PorValorDentroDeTolerancia()
        {
            Assert.Equal("D", Find(new[] { Prop("D", 0.9 * 1.0009) }, 0.9)!.Nombre);
        }

        [Fact]
        public void Deduccion_FueraDeTolerancia_Null()
        {
            Assert.Null(Find(new[] { Prop("D", 0.9 * 1.0011) }, 0.9));
        }

        [Fact]
        public void Deduccion_SoloPropiedadesDeDistancia()
        {
            Assert.Null(Find(
                new[] { Prop("Angulo", 0.9, unidades: DynamicPropertyRules.UnidadesAngular), Prop("Area", 0.9, unidades: DynamicPropertyRules.UnidadesArea) }, 0.9));
        }

        [Fact]
        public void Deduccion_DescartaLasDeSoloLectura()
        {
            Assert.Null(Find(new[] { Prop("D", 0.9, soloLectura: true) }, 0.9));
        }

        [Fact]
        public void Deduccion_VariasCandidatas_NoElige()
        {
            WidthMatch m = DynamicPropertyRules.FindWidthProperty(new[] { Prop("Primera", 0.9), Prop("Segunda", 0.9) }, 0.9);
            Assert.Null(m.Propiedad);
            Assert.True(m.Ambigua);
            Assert.Equal(new[] { "Primera", "Segunda" }, m.Candidatas);
        }

        [Fact]
        public void MismoNombreRepetido_NoEsAmbiguo()
        {
            WidthMatch m = DynamicPropertyRules.FindWidthProperty(new[] { Prop("Ancho", 0.9), Prop("ANCHO", 0.9) }, 0.9);
            Assert.Equal("Ancho", m.Propiedad!.Nombre);
            Assert.False(m.Ambigua);
        }

        [Fact]
        public void UnaCandidata_NoEsAmbigua_YLaInformaComoCandidata()
        {
            WidthMatch m = DynamicPropertyRules.FindWidthProperty(new[] { Prop("D", 0.9), Prop("Otra", 0.5) }, 0.9);
            Assert.Equal(new[] { "D" }, m.Candidatas);
            Assert.False(m.Ambigua);
        }

        [Fact]
        public void Ninguna_SinCandidatas()
        {
            WidthMatch m = DynamicPropertyRules.FindWidthProperty(new[] { Prop("D", 0.5) }, 0.9);
            Assert.Null(m.Propiedad);
            Assert.Empty(m.Candidatas);
        }

        [Fact]
        public void ScaledWidthWarning_TextoLiteral()
        {
            Assert.Equal(
                "vano: el bloque 'PuertaBaño' no es dinámico (o no se encontró su propiedad de ancho); se escaló 1.231 a lo largo del vano: las jambas y la hoja quedan deformadas en proporción.",
                DynamicPropertyRules.ScaledWidthWarning("PuertaBaño", 0.8 / 0.65));
        }

        [Fact]
        public void AmbiguousWidthWarning_ListaLasCandidatasYPideFijarLaPropiedad()
        {
            Assert.Equal(
                "vano: el bloque 'P' tiene varias propiedades que pueden ser el ancho (A, B); no se eligió ninguna y se escaló 0.889 a lo largo del vano: las jambas y la hoja quedan deformadas en proporción. Fije propiedad_ancho en lps.json biblioteca_vanos.",
                DynamicPropertyRules.AmbiguousWidthWarning("P", new[] { "A", "B" }, 0.8 / 0.9));
        }

        [Fact]
        public void WidthIgnoredWarning_TextoLiteral()
        {
            Assert.Equal(
                "propiedades_dinamicas: el ancho lo fija la herramienta; se ignoró el valor de 'A'.",
                DynamicPropertyRules.WidthIgnoredWarning("A"));
        }

        [Fact]
        public void PendingWidthWarning_TextoLiteral()
        {
            Assert.Equal(
                "vano: la definición 'P' aún no está en el dibujo; su propiedad de ancho se deducirá al crear la referencia en confirmar (ancho_por: pendiente).",
                DynamicPropertyRules.PendingWidthWarning("P"));
        }

        [Fact]
        public void Ninguna_Null()
        {
            Assert.Null(Find(new[] { Prop("D", 0.5), Prop("Vis", "Abierta", unidades: DynamicPropertyRules.UnidadesNinguna) }, 0.9));
            Assert.Null(Find(new List<DynamicPropertyInfo>(), 0.9));
        }

        [Fact]
        public void Deduccion_ValorDouble()
        {
            Assert.NotNull(Find(new[] { Prop("D", 2.0d) }, 2.0));
        }

        [Fact]
        public void Deduccion_ValorShort()
        {
            Assert.NotNull(Find(new[] { Prop("D", (short)2) }, 2.0));
        }

        [Fact]
        public void Deduccion_ValorInt()
        {
            Assert.NotNull(Find(new[] { Prop("D", 2) }, 2.0));
        }

        [Fact]
        public void Deduccion_ValorNoNumerico_NoCuenta()
        {
            Assert.Null(Find(new[] { Prop("D", "2") }, 2.0));
            Assert.Null(Find(new[] { Prop("D", null) }, 2.0));
        }

        [Fact]
        public void ToJson_ClavesYValores()
        {
            var admitidos = new List<object?> { 0.8, 0.9 };
            List<Dictionary<string, object?>> j = DynamicPropertyRules.ToJson(new[]
            {
                new DynamicPropertyInfo("Distancia1", 0.9, false, DynamicPropertyRules.UnidadesDistancia, admitidos, "ancho"),
            });
            Dictionary<string, object?> e = Assert.Single(j);
            Assert.Equal(new[] { "nombre", "valor", "solo_lectura", "unidades", "valores_permitidos", "descripcion" }, e.Keys);
            Assert.Equal("Distancia1", e["nombre"]);
            Assert.Equal(0.9, e["valor"]);
            Assert.Equal(false, e["solo_lectura"]);
            Assert.Equal("distancia", e["unidades"]);
            Assert.Same(admitidos, e["valores_permitidos"]);
            Assert.Equal("ancho", e["descripcion"]);
        }

        [Fact]
        public void ToJson_SinValoresPermitidos_Null()
        {
            Assert.Null(DynamicPropertyRules.ToJson(new[] { Prop("D", 1.0) })[0]["valores_permitidos"]);
        }

        [Fact]
        public void ToJson_Vacio()
        {
            Assert.Empty(DynamicPropertyRules.ToJson(new List<DynamicPropertyInfo>()));
        }

        [Theory]
        [InlineData(1.5d, 1.5)]
        [InlineData(1.5f, 1.5)]
        [InlineData(2, 2.0)]
        [InlineData((short)3, 3.0)]
        [InlineData(4L, 4.0)]
        public void TryNumber_TiposNumericos(object valor, double esperado)
        {
            Assert.True(DynamicPropertyRules.TryNumber(valor, out double n));
            Assert.Equal(esperado, n);
        }

        [Fact]
        public void TryNumber_NoNumerico()
        {
            Assert.False(DynamicPropertyRules.TryNumber("1", out _));
            Assert.False(DynamicPropertyRules.TryNumber(null, out _));
            Assert.False(DynamicPropertyRules.TryNumber(new[] { 1.0 }, out _));
        }

        [Fact]
        public void SameValue_NumerosDeTipoDistintoYCadenas()
        {
            Assert.True(DynamicPropertyRules.SameValue((short)2, 2.0));
            Assert.True(DynamicPropertyRules.SameValue(2.0, 2.0 + 1e-10));
            Assert.False(DynamicPropertyRules.SameValue(2.0, 2.001));
            Assert.True(DynamicPropertyRules.SameValue("Abierta", "ABIERTA"));
            Assert.False(DynamicPropertyRules.SameValue("2", 2.0));
        }

        [Fact]
        public void ConvertNumber_ConservaElTipoSiCabe()
        {
            Assert.IsType<short>(DynamicPropertyRules.ConvertNumber(3, (short)1));
            Assert.IsType<int>(DynamicPropertyRules.ConvertNumber(3, 1));
            Assert.IsType<double>(DynamicPropertyRules.ConvertNumber(3, 1.0));
        }

        [Fact]
        public void ConvertNumber_NoCabe_Double()
        {
            Assert.IsType<double>(DynamicPropertyRules.ConvertNumber(1.5, (short)1));
            Assert.IsType<double>(DynamicPropertyRules.ConvertNumber(40000, (short)1));
            Assert.IsType<double>(DynamicPropertyRules.ConvertNumber(3e10, 1));
        }

        [Fact]
        public void NormalizeValue_ShortYFloatYOtros()
        {
            Assert.IsType<int>(DynamicPropertyRules.NormalizeValue((short)3));
            Assert.IsType<double>(DynamicPropertyRules.NormalizeValue(1.5f));
            Assert.Null(DynamicPropertyRules.NormalizeValue(null));
            Assert.Equal("7", DynamicPropertyRules.NormalizeValue(7L));
            Assert.Equal(new[] { 1.0, 2.0 }, DynamicPropertyRules.NormalizeValue(new[] { 1.0, 2.0 }));
        }

        [Fact]
        public void Format_NumeroInvarianteYPunto()
        {
            Assert.Equal("0.8", DynamicPropertyRules.Format(0.8));
            Assert.Equal("3", DynamicPropertyRules.Format((short)3));
            Assert.Equal("[1, 2.5]", DynamicPropertyRules.Format(new[] { 1.0, 2.5 }));
            Assert.Equal("Abierta", DynamicPropertyRules.Format("Abierta"));
            Assert.Equal("", DynamicPropertyRules.Format(null));
        }
    }
}
