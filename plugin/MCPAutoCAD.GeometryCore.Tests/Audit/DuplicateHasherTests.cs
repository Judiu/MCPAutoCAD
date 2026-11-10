using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using MCPAutoCAD.GeometryCore.Audit;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Audit
{
    /// <summary>Clave canónica de duplicados (protocol.md §5C.6).</summary>
    public class DuplicateHasherTests
    {
        private const double Tol = 1e-6;

        private static Point3 P(double x, double y, double z = 0) => new Point3(x, y, z);

        [Fact]
        public void Linea_AB_IgualA_BA()
        {
            string ab = DuplicateHasher.Key(new LineInput(P(0, 0), P(5, 3)), Tol);
            string ba = DuplicateHasher.Key(new LineInput(P(5, 3), P(0, 0)), Tol);
            Assert.Equal(ab, ba);
        }

        [Fact]
        public void Linea_Distinta_ClaveDistinta()
        {
            Assert.NotEqual(
                DuplicateHasher.Key(new LineInput(P(0, 0), P(5, 3)), Tol),
                DuplicateHasher.Key(new LineInput(P(0, 0), P(5, 3.001)), Tol));
        }

        [Fact]
        public void Linea_DiferenciaMenorQueTolerancia_MismaClave()
        {
            Assert.Equal(
                DuplicateHasher.Key(new LineInput(P(0, 0), P(5, 3)), Tol),
                DuplicateHasher.Key(new LineInput(P(0, 0), P(5, 3 + 1e-9)), Tol));
        }

        [Fact]
        public void MismaGeometria_ConToleranciasDistintas()
        {
            var a = new LineInput(P(0, 0), P(1.0004, 2));
            var b = new LineInput(P(0, 0), P(1.0001, 2));
            // Con 1e-3 ambas redondean a 1.000; con 1e-6 no.
            Assert.Equal(DuplicateHasher.Key(a, 1e-3), DuplicateHasher.Key(b, 1e-3));
            Assert.NotEqual(DuplicateHasher.Key(a, 1e-6), DuplicateHasher.Key(b, 1e-6));
        }

        [Fact]
        public void PolilineaAbierta_DirectaIgualAInvertida()
        {
            var direct = new PolylineInput(false, 0, new List<(double, double, double)>
            {
                (0, 0, 0.5), (10, 0, 0), (10, 5, -0.25), (0, 5, 0),
            });

            // Invertida: v3..v0 con bulges −b2, −b1, −b0 y el último (sin tramo) 0.
            var reversed = new PolylineInput(false, 0, new List<(double, double, double)>
            {
                (0, 5, 0.25), (10, 5, 0), (10, 0, -0.5), (0, 0, 0),
            });

            Assert.Equal(DuplicateHasher.Key(direct, Tol), DuplicateHasher.Key(reversed, Tol));
        }

        [Fact]
        public void PolilineaAbierta_ElBulgeDelUltimoVerticeNoCuenta()
        {
            var a = new PolylineInput(false, 0, new List<(double, double, double)> { (0, 0, 0), (1, 0, 0.7) });
            var b = new PolylineInput(false, 0, new List<(double, double, double)> { (0, 0, 0), (1, 0, 0) });
            Assert.Equal(DuplicateHasher.Key(a, Tol), DuplicateHasher.Key(b, Tol));
        }

        [Fact]
        public void PolilineaCerrada_DirectaIgualAInvertida_ConTramoDeCierre()
        {
            var direct = new PolylineInput(true, 2.0, new List<(double, double, double)>
            {
                (0, 0, 0.1), (4, 0, 0.2), (4, 3, 0.3),
            });

            // Invertida cerrada: v2, v1, v0 con bulges −b1, −b0 y −b2 (tramo de cierre v0 → v2).
            var reversed = new PolylineInput(true, 2.0, new List<(double, double, double)>
            {
                (4, 3, -0.2), (4, 0, -0.1), (0, 0, -0.3),
            });

            Assert.Equal(DuplicateHasher.Key(direct, Tol), DuplicateHasher.Key(reversed, Tol));
        }

        [Fact]
        public void PolilineaCerrada_BulgeDeCierreDistinto_NoEsDuplicada()
        {
            var a = new PolylineInput(true, 0, new List<(double, double, double)> { (0, 0, 0), (4, 0, 0), (4, 3, 0) });
            var b = new PolylineInput(true, 0, new List<(double, double, double)> { (0, 0, 0), (4, 0, 0), (4, 3, 0.5) });
            Assert.NotEqual(DuplicateHasher.Key(a, Tol), DuplicateHasher.Key(b, Tol));
        }

        [Fact]
        public void Polilinea_CerradaYAbierta_Distintas()
        {
            var verts = new List<(double, double, double)> { (0, 0, 0), (4, 0, 0), (4, 3, 0) };
            Assert.NotEqual(
                DuplicateHasher.Key(new PolylineInput(true, 0, verts), Tol),
                DuplicateHasher.Key(new PolylineInput(false, 0, verts), Tol));
        }

        [Fact]
        public void Polilinea_ElevacionDistinta()
        {
            var verts = new List<(double, double, double)> { (0, 0, 0), (4, 0, 0) };
            Assert.NotEqual(
                DuplicateHasher.Key(new PolylineInput(false, 0, verts), Tol),
                DuplicateHasher.Key(new PolylineInput(false, 1, verts), Tol));
        }

        [Fact]
        public void Text_ConDistintoTexto_Distintos()
        {
            Assert.NotEqual(
                DuplicateHasher.Key(new TextInput(false, "COCINA", P(1, 2), 0.2), Tol),
                DuplicateHasher.Key(new TextInput(false, "COCINA ", P(1, 2), 0.2), Tol));
            Assert.NotEqual(
                DuplicateHasher.Key(new TextInput(false, "cocina", P(1, 2), 0.2), Tol),
                DuplicateHasher.Key(new TextInput(false, "COCINA", P(1, 2), 0.2), Tol));
        }

        [Fact]
        public void Text_YMText_ConMismoContenido_Distintos()
        {
            Assert.NotEqual(
                DuplicateHasher.Key(new TextInput(false, "A", P(1, 2), 0.2), Tol),
                DuplicateHasher.Key(new TextInput(true, "A", P(1, 2), 0.2), Tol));
        }

        [Fact]
        public void Text_SeparadoresEnElTexto_NoProducenColisiones()
        {
            // Sin prefijo de longitud, "A|1" con altura X podría confundirse con "A" seguido de campos.
            Assert.NotEqual(
                DuplicateHasher.Key(new TextInput(false, "A|0,0,0", P(0, 0), 1), Tol),
                DuplicateHasher.Key(new TextInput(false, "A", P(0, 0), 1), Tol));
        }

        [Fact]
        public void Insert_ConDistintaRotacion_Distintos()
        {
            Assert.NotEqual(
                DuplicateHasher.Key(new InsertInput("DP", P(1, 1), P(1, 1, 1), 0), Tol),
                DuplicateHasher.Key(new InsertInput("DP", P(1, 1), P(1, 1, 1), Math.PI / 2), Tol));
        }

        [Fact]
        public void Insert_NombreSinMayusculasYRotacionNormalizada()
        {
            Assert.Equal(
                DuplicateHasher.Key(new InsertInput("dp", P(1, 1), P(1, 1, 1), 2 * Math.PI + 0.5), Tol),
                DuplicateHasher.Key(new InsertInput("DP", P(1, 1), P(1, 1, 1), 0.5), Tol));
        }

        [Fact]
        public void Circulo_YArco()
        {
            Assert.Equal(
                DuplicateHasher.Key(new CircleInput(P(1, 1), 2, P(0, 0, 1)), Tol),
                DuplicateHasher.Key(new CircleInput(P(1, 1), 2, P(0, 0, 1)), Tol));
            Assert.NotEqual(
                DuplicateHasher.Key(new CircleInput(P(1, 1), 2, P(0, 0, 1)), Tol),
                DuplicateHasher.Key(new CircleInput(P(1, 1), 2, P(0, 0, -1)), Tol));
            Assert.Equal(
                DuplicateHasher.Key(new ArcInput(P(0, 0), 1, -Math.PI / 2, Math.PI), Tol),
                DuplicateHasher.Key(new ArcInput(P(0, 0), 1, 3 * Math.PI / 2, Math.PI), Tol));
        }

        [Fact]
        public void TiposDistintos_NuncaColisionan()
        {
            Assert.NotEqual(
                DuplicateHasher.Key(new CircleInput(P(0, 0), 1, P(0, 0, 1)), Tol),
                DuplicateHasher.Key(new ArcInput(P(0, 0), 1, 0, 0), Tol));
        }

        [Fact]
        public void Formato_IndependienteDeLaCultura()
        {
            var input = new LineInput(P(-1.5, 2.25), P(3.125, 1e5));
            string invariant = DuplicateHasher.Key(input, 1e-3);
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("es-CO");
                Assert.Equal(invariant, DuplicateHasher.Key(input, 1e-3));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }

            Assert.Contains("-1500,2250,0", invariant);
            Assert.DoesNotContain(" ", invariant);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1e-6)]
        [InlineData(1e-13)]
        [InlineData(1.5)]
        [InlineData(double.NaN)]
        public void ToleranciaInvalida_Lanza(double tol)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => DuplicateHasher.Key(new LineInput(P(0, 0), P(1, 1)), tol));
        }

        [Fact]
        public void Agrupacion_ConDiccionario_EsLineal()
        {
            // 20 000 líneas con 10 000 claves distintas: agrupar con diccionario debe ser inmediato.
            var groups = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < 20000; i++)
            {
                int k = i % 10000;
                string key = (i < 10000)
                    ? DuplicateHasher.Key(new LineInput(P(k, 0), P(k, 1)), Tol)
                    : DuplicateHasher.Key(new LineInput(P(k, 1), P(k, 0)), Tol);
                groups.TryGetValue(key, out int n);
                groups[key] = n + 1;
            }

            Assert.Equal(10000, groups.Count);
            Assert.All(groups.Values, n => Assert.Equal(2, n));
        }
    }
}
