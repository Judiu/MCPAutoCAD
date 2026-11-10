using System;
using System.Collections.Generic;
using System.Linq;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>protocol.md §5E.3.2: rejilla de ejes con burbujas tangentes y rótulos centrados.</summary>
    public class GridLayoutTests
    {
        private const int P = 9;
        private static readonly double[] SepX = { 4, 5 };
        private static readonly double[] SepY = { 3 };

        private static GridLayout.GridResult Build(
            string burbujas = "fin",
            double rot = 0,
            IReadOnlyList<string>? rx = null,
            IReadOnlyList<string>? ry = null,
            string? capa = null,
            Point3? origen = null) =>
            GridLayout.Build(
                origen ?? new Point3(10, 20, 0), SepX, SepY,
                rx ?? GridLayout.DefaultNumericLabels(3), ry ?? GridLayout.DefaultLetterLabels(2),
                1, 0.154, 0.175, burbujas, rot, "75", capa);

        private static void AssertPt(double x, double y, Point3 p)
        {
            Assert.Equal(x, p.X, P);
            Assert.Equal(y, p.Y, P);
        }

        [Fact]
        public void Rejilla3x2_Fin()
        {
            GridLayout.GridResult r = Build();
            Assert.Equal(15, r.Entidades.Count);
            var lines = r.Entidades.Take(5).Cast<PlannedLine>().ToList();
            var circles = r.Entidades.Skip(5).Take(5).Cast<PlannedCircle>().ToList();
            var texts = r.Entidades.Skip(10).Cast<PlannedText>().ToList();
            Assert.Equal(5, r.NBurbujas);

            Assert.Equal(new[] { 10.0, 14.0, 19.0 }, r.PosicionesX);
            Assert.Equal(new[] { 20.0, 23.0 }, r.PosicionesY);
            Assert.Equal(9, r.Ancho, P);
            Assert.Equal(3, r.Alto, P);

            // Verticales: de y0 − ext a yn + ext.
            AssertPt(10, 19, lines[0].Inicio);
            AssertPt(10, 24, lines[0].Fin);
            AssertPt(19, 19, lines[2].Inicio);
            AssertPt(19, 24, lines[2].Fin);
            // Horizontales: de x0 − ext a xn + ext.
            AssertPt(9, 20, lines[3].Inicio);
            AssertPt(20, 20, lines[3].Fin);
            AssertPt(9, 23, lines[4].Inicio);
            AssertPt(20, 23, lines[4].Fin);

            // Burbujas tangentes al extremo (fin = arriba / derecha).
            AssertPt(10, 24.154, circles[0].Centro);
            AssertPt(14, 24.154, circles[1].Centro);
            AssertPt(19, 24.154, circles[2].Centro);
            AssertPt(20.154, 20, circles[3].Centro);
            AssertPt(20.154, 23, circles[4].Centro);
            Assert.All(circles, c => Assert.Equal(0.154, c.Radio));
            Assert.Equal(0.154, circles[0].Centro.Distance(lines[0].Fin), P); // la línea termina en el borde de la burbuja

            Assert.Equal(new[] { "1", "2", "3", "A", "B" }, texts.Select(t => t.Texto));
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(circles[i].Centro, texts[i].Posicion);
                Assert.Equal("medio_centro", texts[i].Justificacion);
                Assert.Equal(0.175, texts[i].Altura);
                Assert.Equal(0.0, texts[i].RotacionGrados);
                Assert.Equal("75", texts[i].Estilo);
            }

            Assert.All(r.Entidades, e =>
            {
                Assert.Equal("ejes", e.Concepto);
                Assert.Null(e.Capa);
            });

            Assert.Equal(9, r.Bbox.Xmin, P);
            Assert.Equal(19, r.Bbox.Ymin, P);
            Assert.Equal(20.308, r.Bbox.Xmax, P);
            Assert.Equal(24.308, r.Bbox.Ymax, P);
        }

        [Fact]
        public void Ambos_DiezBurbujas()
        {
            GridLayout.GridResult r = Build("ambos");
            Assert.Equal(10, r.NBurbujas);
            Assert.Equal(5 + 10 + 10, r.Entidades.Count);
            var circles = r.Entidades.OfType<PlannedCircle>().ToList();
            AssertPt(10, 18.846, circles[0].Centro); // inicio del eje 1
            AssertPt(10, 24.154, circles[1].Centro); // fin del eje 1
            AssertPt(8.846, 20, circles[6].Centro);  // inicio del eje A
            Assert.Equal(8.692, r.Bbox.Xmin, P);
            Assert.Equal(18.692, r.Bbox.Ymin, P);
        }

        [Fact]
        public void Inicio_SoloAbajoYIzquierda()
        {
            GridLayout.GridResult r = Build("inicio");
            var circles = r.Entidades.OfType<PlannedCircle>().ToList();
            Assert.Equal(5, circles.Count);
            AssertPt(14, 18.846, circles[1].Centro);
            AssertPt(8.846, 23, circles[4].Centro);
        }

        [Fact]
        public void Rotacion90_GiraPuntosYTextosSinGiro()
        {
            GridLayout.GridResult r = Build(rot: 90, origen: new Point3(0, 0, 0));
            var lines = r.Entidades.OfType<PlannedLine>().ToList();
            var circles = r.Entidades.OfType<PlannedCircle>().ToList();
            var texts = r.Entidades.OfType<PlannedText>().ToList();
            // Eje vertical x = 4: (4, −1)→(4, 4) girado 90° → (1, 4)→(−4, 4).
            AssertPt(1, 4, lines[1].Inicio);
            AssertPt(-4, 4, lines[1].Fin);
            AssertPt(-4.154, 4, circles[1].Centro);
            Assert.All(texts, t => Assert.Equal(0.0, t.RotacionGrados));
            AssertPt(-4.154, 4, texts[1].Posicion);
            // Posiciones sin girar.
            Assert.Equal(new[] { 0.0, 4.0, 9.0 }, r.PosicionesX);
        }

        [Fact]
        public void Capa_SustituyeAlConcepto()
        {
            GridLayout.GridResult r = Build(capa: "EJES2");
            Assert.All(r.Entidades, e =>
            {
                Assert.Equal("EJES2", e.Capa);
                Assert.Null(e.Concepto);
            });
        }

        [Fact]
        public void UnaSolaDireccion_UnEjeEnElOrigen()
        {
            GridLayout.GridResult r = GridLayout.Build(
                new Point3(0, 0, 0), new double[] { 5 }, Array.Empty<double>(),
                GridLayout.DefaultNumericLabels(2), GridLayout.DefaultLetterLabels(1),
                1, 0.154, 0.175, "fin", 0, null, null);
            Assert.Equal(3, r.Entidades.OfType<PlannedLine>().Count());
            Assert.Equal(new[] { 0.0 }, r.PosicionesY);
            Assert.Equal(0, r.Alto);
            PlannedLine v0 = r.Entidades.OfType<PlannedLine>().First();
            AssertPt(0, -1, v0.Inicio);
            AssertPt(0, 1, v0.Fin);
        }

        [Fact]
        public void RotulosPorDefecto()
        {
            Assert.Equal(new[] { "1", "2", "3" }, GridLayout.DefaultNumericLabels(3));
            Assert.Equal(new[] { "A", "B" }, GridLayout.DefaultLetterLabels(2));
            IReadOnlyList<string> l = GridLayout.DefaultLetterLabels(28);
            Assert.Equal("Z", l[25]);
            Assert.Equal("AA", l[26]);
            Assert.Equal("AB", l[27]);
            Assert.Equal("BA", GridLayout.DefaultLetterLabels(53)[52]);
            Assert.Empty(GridLayout.DefaultNumericLabels(0));
        }

        private static void AssertError(string esperado, Action a)
        {
            ArgumentException e = Assert.ThrowsAny<ArgumentException>(a);
            Assert.Equal(esperado, e.Message);
        }

        private static void B(
            IReadOnlyList<double> sx, IReadOnlyList<double> sy, IReadOnlyList<string>? rx = null, IReadOnlyList<string>? ry = null,
            double ext = 1, double r = 0.154, double h = 0.175, string burb = "fin")
        {
            GridLayout.Build(
                new Point3(0, 0, 0), sx, sy,
                rx ?? GridLayout.DefaultNumericLabels(sx.Count + 1), ry ?? GridLayout.DefaultLetterLabels(sy.Count + 1),
                ext, r, h, burb, 0, null, null);
        }

        [Fact]
        public void Errores()
        {
            double[] muchas = Enumerable.Repeat(1.0, 101).ToArray();
            AssertError("separaciones_x: debe tener entre 0 y 100 elementos", () => B(muchas, SepY));
            AssertError("separaciones_y: debe tener entre 0 y 100 elementos", () => B(SepX, muchas));
            AssertError("separaciones_x[1]: debe ser un número mayor que 0", () => B(new double[] { 1, 0 }, SepY));
            AssertError("separaciones_y[0]: debe ser un número mayor que 0", () => B(SepX, new double[] { -2 }));
            AssertError("separaciones: se necesita al menos un eje en x o en y", () => B(Array.Empty<double>(), Array.Empty<double>()));
            AssertError("rotulos_x: debe tener 3 elementos (separaciones_x + 1)", () => B(SepX, SepY, rx: new[] { "1", "2" }));
            AssertError("rotulos_y: debe tener 2 elementos (separaciones_y + 1)", () => B(SepX, SepY, ry: new[] { "A" }));
            AssertError("rotulos_x[1]: debe tener entre 1 y 10 caracteres", () => B(SepX, SepY, rx: new[] { "1", "", "3" }));
            AssertError("rotulos_y[0]: debe tener entre 1 y 10 caracteres", () => B(SepX, SepY, ry: new[] { "ABCDEFGHIJK", "B" }));
            AssertError("rotulos: rótulo repetido 'A'", () => B(SepX, SepY, rx: new[] { "1", "A", "3" }));
            AssertError("rotulos: rótulo repetido '1'", () => B(SepX, SepY, rx: new[] { "1", "1", "3" }));
            B(SepX, SepY, rx: new[] { "a", "2", "3" }); // comparación ordinal: 'a' ≠ 'A'
            AssertError("extension: debe ser un número mayor o igual que 0", () => B(SepX, SepY, ext: -0.1));
            AssertError("radio_burbuja: debe ser un número mayor que 0", () => B(SepX, SepY, r: 0));
            AssertError("altura_texto: debe ser un número mayor que 0", () => B(SepX, SepY, h: 0));
            AssertError("burbujas: debe ser uno de inicio, fin, ambos", () => B(SepX, SepY, burb: "arriba"));
        }

        [Fact]
        public void ExtensionCero_Valida()
        {
            B(SepX, SepY, ext: 0);
        }
    }
}
