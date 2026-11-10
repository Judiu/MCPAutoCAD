using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Clean;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Clean
{
    public class PurgeFixedPointScopeTests
    {
        [Fact]
        public void RaizFueraDeAmbitoQueReferenciaUnNodoEnAmbito_NoLoPurga()
        {
            // N (en ámbito, sin uso propio) es referenciado por R (raíz fuera de 'solo'): debe sobrevivir.
            var nodos = new List<(string, string)> { (PurgeCategories.Bloques, "N") };
            var refs = new List<((string, string) desde, (string, string) hacia)>
            {
                ((PurgeCategories.Capas, "R"), (PurgeCategories.Bloques, "N")),
            };
            var raices = new List<(string, string)> { (PurgeCategories.Capas, "R") };

            IReadOnlyList<IReadOnlyList<(string categoria, string nombre)>> rondas = PurgeRules.FixedPoint(nodos, refs, raices);

            Assert.Empty(rondas.SelectMany(r => r));
        }

        [Fact]
        public void SinReferenciaDeRaiz_SeHaceCandidatoYSePurga()
        {
            // Control: el mismo nodo sin la raíz que lo referencia sí se purga en la ronda 1.
            var nodos = new List<(string, string)> { (PurgeCategories.Bloques, "N") };
            var refs = new List<((string, string) desde, (string, string) hacia)>();
            var raices = new List<(string, string)> { (PurgeCategories.Capas, "R") };

            IReadOnlyList<IReadOnlyList<(string categoria, string nombre)>> rondas = PurgeRules.FixedPoint(nodos, refs, raices);

            Assert.Single(rondas);
            Assert.Equal((PurgeCategories.Bloques, "N"), rondas[0][0]);
        }
    }
}
