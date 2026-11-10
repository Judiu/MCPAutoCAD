using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Draw;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Draw
{
    public class AttributeTagsTests
    {
        private static readonly string[] Rotulo = { "PROYECTO", "VIS", "PLANO_N", "PLANO_TOTAL" };

        private static List<KeyValuePair<string, string>> Dados(params (string tag, string valor)[] items)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach ((string tag, string valor) in items)
            {
                list.Add(new KeyValuePair<string, string>(tag, valor));
            }

            return list;
        }

        [Fact]
        public void Match_TodasConocidas_SinDistinguirMayusculas()
        {
            AttributeTags.Result r = AttributeTags.Match(Dados(("proyecto", "VIVIENDA"), ("Plano_N", "3")), Rotulo);
            Assert.True(r.Ok);
            Assert.Null(r.Error);
            Assert.Equal("VIVIENDA", r.Values!["PROYECTO"]);
            Assert.Equal("3", r.Values["PLANO_N"]);
            Assert.Equal(Rotulo, r.Etiquetas);
        }

        [Fact]
        public void Match_EtiquetaDesconocida_MensajeExactoYEtiquetas()
        {
            AttributeTags.Result r = AttributeTags.Match(Dados(("PROYECTO", "x"), ("NO_EXISTE", "y")), Rotulo);
            Assert.False(r.Ok);
            Assert.Null(r.Values);
            Assert.Equal("atributos: etiqueta desconocida 'NO_EXISTE' (etiquetas del bloque: PROYECTO, VIS, PLANO_N, PLANO_TOTAL)", r.Error);
            Assert.Equal(Rotulo, r.Etiquetas);
        }

        [Fact]
        public void Match_BloqueSinAtributos_Ninguna()
        {
            AttributeTags.Result r = AttributeTags.Match(Dados(("A", "1")), Array.Empty<string>());
            Assert.Equal("atributos: etiqueta desconocida 'A' (etiquetas del bloque: ninguna)", r.Error);
            Assert.Empty(r.Etiquetas);
        }

        [Fact]
        public void Match_SinAtributosDelCliente_Ok()
        {
            AttributeTags.Result r = AttributeTags.Match(Dados(), Rotulo);
            Assert.True(r.Ok);
            Assert.Empty(r.Values!);
        }

        [Fact]
        public void Distinct_QuitaRepetidosIgnorandoMayusculas_ConservaLaPrimera()
        {
            Assert.Equal(new[] { "Tag", "OTRO" }, AttributeTags.Distinct(new[] { "Tag", "TAG", "OTRO", "tag" }));
        }

        [Fact]
        public void Match_Nulos_Lanzan()
        {
            Assert.Throws<ArgumentNullException>(() => AttributeTags.Match(null!, Rotulo));
            Assert.Throws<ArgumentNullException>(() => AttributeTags.Distinct(null!));
        }
    }
}
