using System;
using System.Collections.Generic;
using MCPAutoCAD.GeometryCore.Imagenes;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Imagenes
{
    /// <summary>Lectura de dimensiones desde cabeceras sintéticas (PNG, JPEG, BMP, TIFF) de insertar_imagen.</summary>
    public class ImageHeaderTests
    {
        [Fact]
        public void Png_LeeAnchoYAltoBigEndian()
        {
            byte[] png = Png(640, 480);
            Assert.True(ImageHeader.TryDimensiones(".png", png, out int w, out int h));
            Assert.Equal((640, 480), (w, h));
        }

        [Fact]
        public void Jpeg_LeeSofTrasUnSegmentoApp0()
        {
            byte[] jpg = Jpeg(width: 200, height: 100);
            Assert.True(ImageHeader.TryDimensiones(".jpg", jpg, out int w, out int h));
            Assert.Equal((200, 100), (w, h));
        }

        [Fact]
        public void Jpeg_AceptaExtensionJpeg()
        {
            Assert.True(ImageHeader.TryDimensiones(".JPEG", Jpeg(7, 9), out int w, out int h));
            Assert.Equal((7, 9), (w, h));
        }

        [Theory]
        [InlineData(40, 30)]
        [InlineData(40, -30)]
        public void Bmp_LeeCabeceraWindowsConAltoAbsoluto(int width, int height)
        {
            byte[] bmp = Bmp(width, height);
            Assert.True(ImageHeader.TryDimensiones(".bmp", bmp, out int w, out int h));
            Assert.Equal((40, 30), (w, h));
        }

        [Fact]
        public void Bmp_CabeceraOs2_LeeDimensionesDe16Bits()
        {
            byte[] bmp = new byte[26];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            bmp[14] = 12;               // tamaño de la cabecera OS/2
            bmp[18] = 0x10; bmp[19] = 0x00;   // ancho 16
            bmp[20] = 0x08; bmp[21] = 0x00;   // alto 8
            Assert.True(ImageHeader.TryDimensiones(".bmp", bmp, out int w, out int h));
            Assert.Equal((16, 8), (w, h));
        }

        [Theory]
        [InlineData(true, 300, 150)]
        [InlineData(false, 300, 150)]
        public void Tiff_LeeEtiquetas256y257(bool littleEndian, int width, int height)
        {
            byte[] tif = Tiff(littleEndian, width, height);
            Assert.True(ImageHeader.TryDimensiones(".tif", tif, out int w, out int h));
            Assert.Equal((width, height), (w, h));
            Assert.True(ImageHeader.TryDimensiones(".tiff", tif, out _, out _));
        }

        [Fact]
        public void FirmaNoCorrespondeAExtension_NoSeAcepta()
        {
            Assert.False(ImageHeader.TryDimensiones(".jpg", Png(10, 10), out _, out _));
            Assert.False(ImageHeader.TryDimensiones(".png", Jpeg(10, 10), out _, out _));
            Assert.False(ImageHeader.TryDimensiones(".gif", Png(10, 10), out _, out _));
        }

        [Fact]
        public void DatosTruncadosOBasura_NoLanzanYDevuelvenFalse()
        {
            byte[] png = Png(10, 10);
            Assert.False(ImageHeader.TryDimensiones(".png", png[..20], out _, out _));
            Assert.False(ImageHeader.TryDimensiones(".png", Array.Empty<byte>(), out _, out _));
            Assert.False(ImageHeader.TryDimensiones(".png", null!, out _, out _));
            Assert.False(ImageHeader.TryDimensiones(".jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xC0 }, out _, out _));
            Assert.False(ImageHeader.TryDimensiones(".tif", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, out _, out _));
        }

        [Fact]
        public void Png_DimensionCero_NoSeAcepta()
        {
            Assert.False(ImageHeader.TryDimensiones(".png", Png(0, 10), out _, out _));
        }

        private static byte[] Png(int width, int height)
        {
            var d = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
            AddBe32(d, (uint)width);
            AddBe32(d, (uint)height);
            d.AddRange(new byte[] { 8, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            return d.ToArray();
        }

        private static byte[] Jpeg(int width, int height)
        {
            var d = new List<byte> { 0xFF, 0xD8 };
            // APP0: longitud 16 (incluye los dos bytes de longitud) + 14 bytes de contenido.
            d.AddRange(new byte[] { 0xFF, 0xE0, 0x00, 0x10 });
            d.AddRange(new byte[14]);
            // SOF0: longitud 17, precisión 8, alto, ancho (big-endian), 3 componentes.
            d.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width });
            d.AddRange(new byte[] { 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xD9 });
            return d.ToArray();
        }

        private static byte[] Bmp(int width, int height)
        {
            var d = new byte[54];
            d[0] = (byte)'B';
            d[1] = (byte)'M';
            d[10] = 54;                 // offset de datos
            d[14] = 40;                 // cabecera BITMAPINFOHEADER
            WriteLe32(d, 18, width);
            WriteLe32(d, 22, height);
            return d;
        }

        private static byte[] Tiff(bool le, int width, int height)
        {
            var d = new List<byte>();
            if (le)
            {
                d.AddRange(new byte[] { (byte)'I', (byte)'I', 0x2A, 0x00, 8, 0, 0, 0, 2, 0 });
            }
            else
            {
                d.AddRange(new byte[] { (byte)'M', (byte)'M', 0x00, 0x2A, 0, 0, 0, 8, 0, 2 });
            }

            AddEntry(d, le, 256, 4, (uint)width);   // ImageWidth, LONG
            AddEntry(d, le, 257, 3, (uint)height);  // ImageLength, SHORT
            d.AddRange(new byte[4]);                // sin IFD siguiente
            return d.ToArray();
        }

        private static void AddEntry(List<byte> d, bool le, ushort tag, ushort type, uint value)
        {
            AddU16(d, le, tag);
            AddU16(d, le, type);
            AddU32(d, le, 1);
            if (type == 3)
            {
                AddU16(d, le, (ushort)value);
                AddU16(d, le, 0);
            }
            else
            {
                AddU32(d, le, value);
            }
        }

        private static void AddU16(List<byte> d, bool le, ushort v)
        {
            if (le)
            {
                d.Add((byte)v);
                d.Add((byte)(v >> 8));
            }
            else
            {
                d.Add((byte)(v >> 8));
                d.Add((byte)v);
            }
        }

        private static void AddU32(List<byte> d, bool le, uint v)
        {
            if (le)
            {
                AddLe32(d, v);
            }
            else
            {
                AddBe32(d, v);
            }
        }

        private static void AddBe32(List<byte> d, uint v)
        {
            d.Add((byte)(v >> 24));
            d.Add((byte)(v >> 16));
            d.Add((byte)(v >> 8));
            d.Add((byte)v);
        }

        private static void AddLe32(List<byte> d, uint v)
        {
            d.Add((byte)v);
            d.Add((byte)(v >> 8));
            d.Add((byte)(v >> 16));
            d.Add((byte)(v >> 24));
        }

        private static void WriteLe32(byte[] d, int o, int v)
        {
            d[o] = (byte)v;
            d[o + 1] = (byte)(v >> 8);
            d[o + 2] = (byte)(v >> 16);
            d[o + 3] = (byte)(v >> 24);
        }
    }
}
