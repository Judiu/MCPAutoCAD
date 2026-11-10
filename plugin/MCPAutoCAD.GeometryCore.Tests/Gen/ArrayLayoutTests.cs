using System;
using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>protocol.md §5E.3.5: posiciones de array_rectangular y array_polar.</summary>
    public class ArrayLayoutTests
    {
        private const int P = 9;

        private static void AssertApply(ArrayLayout.Placement p, double x, double y, double ex, double ey)
        {
            (double rx, double ry) = p.Transformacion.Apply(x, y);
            Assert.Equal(ex, rx, P);
            Assert.Equal(ey, ry, P);
        }

        [Fact]
        public void Rectangular_2x3_SinAngulo()
        {
            IReadOnlyList<ArrayLayout.Placement> r = ArrayLayout.Rectangular(2, 3, 1, 2, 0, 0, 0);
            Assert.Equal(5, r.Count);
            var esperado = new (int f, int c, double dx, double dy)[] { (0, 1, 2, 0), (0, 2, 4, 0), (1, 0, 0, 1), (1, 1, 2, 1), (1, 2, 4, 1) };
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(i + 1, r[i].Indice);
                Assert.Equal(esperado[i].f, r[i].Fila);
                Assert.Equal(esperado[i].c, r[i].Columna);
                Assert.Equal(esperado[i].dx, r[i].Dx, P);
                Assert.Equal(esperado[i].dy, r[i].Dy, P);
                Assert.Equal(0, r[i].RotacionGrados);
                AssertApply(r[i], 10, 10, 10 + esperado[i].dx, 10 + esperado[i].dy);
            }
        }

        [Fact]
        public void Rectangular_Angulo90_PosicionesGiradas()
        {
            IReadOnlyList<ArrayLayout.Placement> r = ArrayLayout.Rectangular(2, 3, 1, 2, 90, 5, 5);
            // (f=0, c=1): local (2, 0) girado 90° → (0, 2).
            Assert.Equal(0, r[0].Dx, P);
            Assert.Equal(2, r[0].Dy, P);
            Assert.Equal(90, r[0].RotacionGrados);
            // El punto base va a base + desplazamiento girado; la copia gira con la retícula.
            AssertApply(r[0], 5, 5, 5, 7);
            AssertApply(r[0], 6, 5, 5, 8);
            // (f=1, c=2): local (4, 1) → (−1, 4).
            Assert.Equal(-1, r[4].Dx, P);
            Assert.Equal(4, r[4].Dy, P);
            AssertApply(r[4], 5, 5, 4, 9);
        }

        [Fact]
        public void Rectangular_UnaFilaOUnaColumna()
        {
            Assert.Equal(2, ArrayLayout.Rectangular(1, 3, 1, 1, 0, 0, 0).Count);
            Assert.Single(ArrayLayout.Rectangular(2, 1, 1, 1, 0, 0, 0));
        }

        [Fact]
        public void Rectangular_Errores()
        {
            ArgumentException e1 = Assert.Throws<ArgumentException>(() => ArrayLayout.Rectangular(1, 1, 1, 1, 0, 0, 0));
            Assert.Equal("filas/columnas: se necesita al menos 2 posiciones", e1.Message);
            ArgumentException e2 = Assert.Throws<ArgumentException>(() => ArrayLayout.Rectangular(100, 100, 1, 1, 0, 0, 0));
            Assert.Equal("array: el número de copias (9999) supera el tope de 500", e2.Message);
            Assert.Equal(500, ArrayLayout.Rectangular(1, 501, 1, 1, 0, 0, 0).Count);
            ArgumentException e3 = Assert.Throws<ArgumentException>(() => ArrayLayout.Rectangular(1, 502, 1, 1, 0, 0, 0));
            Assert.Equal("array: el número de copias (501) supera el tope de 500", e3.Message);
        }

        [Fact]
        public void Polar_VueltaCompleta_Rotando()
        {
            IReadOnlyList<ArrayLayout.Placement> r = ArrayLayout.Polar(0, 0, 4, 360, true, 1, 0);
            Assert.Equal(3, r.Count);
            Assert.Equal(new[] { 90.0, 180.0, 270.0 }, r.Select(p => p.RotacionGrados));
            AssertApply(r[0], 1, 0, 0, 1);
            Assert.Equal(-1, r[0].Dx, P);
            Assert.Equal(1, r[0].Dy, P);
            Assert.Equal(1, r[0].Indice);
            Assert.Null(r[0].Fila);
            Assert.Null(r[0].Columna);
            // La copia gira: un punto a la derecha de la base acaba encima.
            AssertApply(r[0], 2, 0, 0, 2);
            AssertApply(r[1], 1, 0, -1, 0);
        }

        [Fact]
        public void Polar_VueltaCompleta_SinRotar_Traslacion()
        {
            IReadOnlyList<ArrayLayout.Placement> r = ArrayLayout.Polar(0, 0, 4, 360, false, 1, 0);
            Assert.Equal(0, r[0].RotacionGrados);
            Assert.Equal(-1, r[0].Dx, P);
            Assert.Equal(1, r[0].Dy, P);
            AssertApply(r[0], 1, 0, 0, 1);
            AssertApply(r[0], 2, 0, 1, 1); // traslación pura (−1, 1)
        }

        [Fact]
        public void Polar_MenosTrescientosSesenta_PasoNegativo()
        {
            IReadOnlyList<ArrayLayout.Placement> r = ArrayLayout.Polar(0, 0, 4, -360, true, 1, 0);
            Assert.Equal(-90, r[0].RotacionGrados, P);
            AssertApply(r[0], 1, 0, 0, -1);
        }

        [Fact]
        public void Polar_AnguloParcial_UltimaEnElTotal()
        {
            IReadOnlyList<ArrayLayout.Placement> r = ArrayLayout.Polar(10, 10, 3, 180, true, 11, 10);
            Assert.Equal(2, r.Count);
            Assert.Equal(90, r[0].RotacionGrados, P);
            Assert.Equal(180, r[1].RotacionGrados, P);
            AssertApply(r[0], 11, 10, 10, 11);
            AssertApply(r[1], 11, 10, 9, 10);
        }

        [Fact]
        public void Polar_Errores()
        {
            Assert.Equal("n: debe ser un entero entre 2 y 501",
                Assert.Throws<ArgumentException>(() => ArrayLayout.Polar(0, 0, 1, 360, true, 1, 0)).Message);
            Assert.Equal("array: el número de copias (501) supera el tope de 500",
                Assert.Throws<ArgumentException>(() => ArrayLayout.Polar(0, 0, 502, 360, true, 1, 0)).Message);
            Assert.Equal("angulo_total: no puede ser 0",
                Assert.Throws<ArgumentException>(() => ArrayLayout.Polar(0, 0, 4, 0, true, 1, 0)).Message);
            Assert.Equal(500, ArrayLayout.Polar(0, 0, 501, 360, true, 1, 0).Count);
        }
    }
}
