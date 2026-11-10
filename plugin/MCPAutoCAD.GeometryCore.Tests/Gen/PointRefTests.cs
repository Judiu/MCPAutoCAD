using System;
using System.Collections.Generic;
using System.Text.Json;
using MCPAutoCAD.GeometryCore.Gen;
using Xunit;

namespace MCPAutoCAD.GeometryCore.Tests.Gen
{
    /// <summary>PointRef / AnchorFingerprint / ResolvedPoint (protocol.md §5E.1). Puro, sin AutoCAD.</summary>
    public class PointRefTests
    {
        private static JsonElement J(string json)
        {
            using JsonDocument d = JsonDocument.Parse(json);
            return d.RootElement.Clone();
        }

        private static string ParseError(string json, string path = "p")
        {
            var ex = Assert.Throws<ArgumentException>(() => PointRef.Parse(J(json), path));
            return ex.Message;
        }

        private static string Serialize(object o) => JsonSerializer.Serialize(o);

        // ---- Coordenadas ----

        [Fact]
        public void Parse_XY_ZDefaultsToZero()
        {
            PointRef r = PointRef.Parse(J("[1.5, -2]"), "p");
            Assert.Equal(PointRefKind.Coordenada, r.Kind);
            Assert.False(r.TieneAncla);
            Assert.Equal(new Point3(1.5, -2, 0), r.Coordenada!.Value);
            Assert.Null(r.Handle);
        }

        [Fact]
        public void Parse_XYZ()
        {
            PointRef r = PointRef.Parse(J("[1, 2, 3]"), "p");
            Assert.Equal(new Point3(1, 2, 3), r.Coordenada!.Value);
        }

        [Theory]
        [InlineData("[1]")]
        [InlineData("[1, 2, 3, 4]")]
        [InlineData("[1, \"a\"]")]
        public void Parse_BadArray(string json)
        {
            Assert.Equal("p: debe ser un array de 2 o 3 números", ParseError(json));
        }

        [Theory]
        [InlineData("\"2A3F\"")]
        [InlineData("12")]
        [InlineData("null")]
        [InlineData("true")]
        public void Parse_WrongShape(string json)
        {
            Assert.Equal(PointRef.ShapeError("p"), ParseError(json));
            Assert.Equal("p: debe ser [x, y] o un objeto {handle, punto} / {handle, interseccion_con}", PointRef.ShapeError("p"));
        }

        // ---- Punto de entidad ----

        [Theory]
        [InlineData("inicio")]
        [InlineData("fin")]
        [InlineData("medio")]
        [InlineData("centro")]
        [InlineData("vertice")]
        public void Parse_EntityPoint_EachValue(string punto)
        {
            PointRef r = PointRef.Parse(J("{\"handle\": \"2A3F\", \"punto\": \"" + punto + "\"}"), "p");
            Assert.Equal(PointRefKind.PuntoDeEntidad, r.Kind);
            Assert.True(r.TieneAncla);
            Assert.Equal("2A3F", r.Handle);
            Assert.Equal(punto, r.Punto);
            Assert.Null(r.Indice);
            Assert.Null(r.Handle2);
        }

        [Fact]
        public void Parse_VerticeWithIndice()
        {
            PointRef r = PointRef.Parse(J("{\"handle\": \"ab\", \"punto\": \"vertice\", \"indice\": 3}"), "p");
            Assert.Equal(3, r.Indice);
            Assert.Equal(PointRef.PuntoVertice, r.Punto);
        }

        [Fact]
        public void Parse_HandleLowercase_IsUppercased()
        {
            PointRef r = PointRef.Parse(J("{\"handle\": \"2a3f\", \"punto\": \"inicio\"}"), "p");
            Assert.Equal("2A3F", r.Handle);
        }

        [Fact]
        public void Parse_Intersection()
        {
            PointRef r = PointRef.Parse(J("{\"handle\": \"2a3f\", \"interseccion_con\": \"2a40\", \"cerca_de\": [1, 2]}"), "p");
            Assert.Equal(PointRefKind.Interseccion, r.Kind);
            Assert.Equal("2A3F", r.Handle);
            Assert.Equal("2A40", r.Handle2);
            Assert.Equal(new Point3(1, 2, 0), r.CercaDe!.Value);
            Assert.Null(r.Punto);
        }

        [Fact]
        public void Parse_IntersectionWithoutCercaDe()
        {
            PointRef r = PointRef.Parse(J("{\"handle\": \"A\", \"interseccion_con\": \"B\"}"), "p");
            Assert.Null(r.CercaDe);
        }

        // ---- Mensajes de error exactos (§5E.1) ----

        [Fact]
        public void Error_Punto()
        {
            Assert.Equal("p.punto: debe ser uno de inicio, fin, medio, centro, vertice", ParseError("{\"handle\": \"A\", \"punto\": \"esquina\"}"));
            Assert.Equal(PointRef.PuntoError("p"), ParseError("{\"handle\": \"A\", \"punto\": 3}"));
        }

        [Fact]
        public void Error_IndiceSoloVertice()
        {
            Assert.Equal("p.indice: solo aplica con punto 'vertice'", ParseError("{\"handle\": \"A\", \"punto\": \"inicio\", \"indice\": 1}"));
            Assert.Equal(PointRef.IndiceSoloVertice("p"), ParseError("{\"handle\": \"A\", \"interseccion_con\": \"B\", \"indice\": 0}"));
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("1.5")]
        [InlineData("\"2\"")]
        [InlineData("3000000000")]
        public void Error_Indice(string value)
        {
            Assert.Equal("p.indice: debe ser un entero mayor o igual que 0",
                ParseError("{\"handle\": \"A\", \"punto\": \"vertice\", \"indice\": " + value + "}"));
        }

        [Theory]
        [InlineData("\"\"")]
        [InlineData("\"XYZ\"")]
        [InlineData("\"12345678901234567\"")]
        [InlineData("42")]
        public void Error_Handle(string value)
        {
            Assert.Equal("p.handle: formato inválido", ParseError("{\"handle\": " + value + ", \"punto\": \"inicio\"}"));
            Assert.Equal("p.interseccion_con: formato inválido", ParseError("{\"handle\": \"A\", \"interseccion_con\": " + value + "}"));
        }

        [Fact]
        public void Error_Mixed()
        {
            Assert.Equal("p: no puede llevar punto e interseccion_con a la vez",
                ParseError("{\"handle\": \"A\", \"punto\": \"inicio\", \"interseccion_con\": \"B\"}"));
        }

        [Fact]
        public void Error_SameHandle_CaseInsensitive()
        {
            Assert.Equal("p: interseccion_con debe ser una entidad distinta de handle",
                ParseError("{\"handle\": \"2a3f\", \"interseccion_con\": \"2A3F\"}"));
        }

        [Fact]
        public void Error_UnknownProperty()
        {
            Assert.Equal("p.otro: propiedad no permitida", ParseError("{\"handle\": \"A\", \"punto\": \"inicio\", \"otro\": 1}"));
        }

        [Fact]
        public void Error_CercaDeWithoutIntersection_IsUnknownProperty()
        {
            Assert.Equal(PointRef.UnknownProperty("p", "cerca_de"),
                ParseError("{\"handle\": \"A\", \"punto\": \"inicio\", \"cerca_de\": [0, 0]}"));
        }

        [Fact]
        public void Error_ObjectWithoutHandleOrPunto_IsShape()
        {
            Assert.Equal(PointRef.ShapeError("p"), ParseError("{\"punto\": \"inicio\"}"));
            Assert.Equal(PointRef.ShapeError("p"), ParseError("{\"handle\": \"A\"}"));
            Assert.Equal(PointRef.ShapeError("p"), ParseError("{}"));
        }

        [Fact]
        public void Error_CercaDeBadArray_UsesNestedPath()
        {
            Assert.Equal("p.cerca_de: debe ser un array de 2 o 3 números",
                ParseError("{\"handle\": \"A\", \"interseccion_con\": \"B\", \"cerca_de\": [1]}"));
        }

        [Fact]
        public void Error_PathIsUsedVerbatim()
        {
            Assert.Equal("eje[2].punto: debe ser uno de inicio, fin, medio, centro, vertice",
                ParseError("{\"handle\": \"A\", \"punto\": \"x\"}", "eje[2]"));
        }

        // ---- ParseList ----

        [Fact]
        public void ParseList_Ok()
        {
            List<PointRef> list = PointRef.ParseList(J("[[0, 0], {\"handle\": \"a\", \"punto\": \"fin\"}, [1, 1, 1]]"), "eje", 2, 5);
            Assert.Equal(3, list.Count);
            Assert.Equal(PointRefKind.Coordenada, list[0].Kind);
            Assert.Equal("A", list[1].Handle);
            Assert.Equal(new Point3(1, 1, 1), list[2].Coordenada!.Value);
        }

        [Theory]
        [InlineData("[[0, 0]]")]
        [InlineData("[[0, 0], [1, 1], [2, 2], [3, 3]]")]
        [InlineData("{\"a\": 1}")]
        [InlineData("[]")]
        public void ParseList_Limits(string json)
        {
            var ex = Assert.Throws<ArgumentException>(() => PointRef.ParseList(J(json), "eje", 2, 3));
            Assert.Equal("eje: debe ser un array de 2 a 3 puntos", ex.Message);
        }

        [Fact]
        public void ParseList_ItemErrorUsesIndexedPath()
        {
            var ex = Assert.Throws<ArgumentException>(() => PointRef.ParseList(J("[[0, 0], {\"handle\": \"A\", \"punto\": \"x\"}]"), "eje", 2, 3));
            Assert.Equal("eje[1].punto: debe ser uno de inicio, fin, medio, centro, vertice", ex.Message);
        }

        // ---- ToJson ----

        [Fact]
        public void ToJson_Coordinate_IsArray3()
        {
            Assert.Equal("[1,2,0]", Serialize(PointRef.Parse(J("[1, 2]"), "p").ToJson()));
        }

        [Fact]
        public void ToJson_EntityPoint_OriginalShape()
        {
            Assert.Equal("{\"handle\":\"2A3F\",\"punto\":\"inicio\"}", Serialize(PointRef.Parse(J("{\"handle\": \"2a3f\", \"punto\": \"inicio\"}"), "p").ToJson()));
            Assert.Equal("{\"handle\":\"A\",\"punto\":\"vertice\",\"indice\":2}",
                Serialize(PointRef.Parse(J("{\"handle\": \"a\", \"punto\": \"vertice\", \"indice\": 2}"), "p").ToJson()));
        }

        [Fact]
        public void ToJson_Intersection_OriginalShape()
        {
            Assert.Equal("{\"handle\":\"A\",\"interseccion_con\":\"B\"}", Serialize(PointRef.Parse(J("{\"handle\": \"a\", \"interseccion_con\": \"b\"}"), "p").ToJson()));
            Assert.Equal("{\"handle\":\"A\",\"interseccion_con\":\"B\",\"cerca_de\":[1,2,0]}",
                Serialize(PointRef.Parse(J("{\"handle\": \"a\", \"interseccion_con\": \"b\", \"cerca_de\": [1, 2]}"), "p").ToJson()));
        }

        [Fact]
        public void ToJson_RoundTripsThroughParse()
        {
            PointRef r = PointRef.Parse(J("{\"handle\": \"a\", \"punto\": \"vertice\", \"indice\": 4}"), "p");
            PointRef again = PointRef.Parse(J(Serialize(r.ToJson())), "p");
            Assert.Equal(r.Kind, again.Kind);
            Assert.Equal(r.Handle, again.Handle);
            Assert.Equal(r.Punto, again.Punto);
            Assert.Equal(r.Indice, again.Indice);
        }

        [Fact]
        public void Factories_UppercaseHandles()
        {
            Assert.Equal("AB", PointRef.FromEntity("ab", PointRef.PuntoFin).Handle);
            PointRef i = PointRef.FromIntersection("ab", "cd");
            Assert.Equal("AB", i.Handle);
            Assert.Equal("CD", i.Handle2);
        }

        [Fact]
        public void Schema_IsValidJson()
        {
            using JsonDocument d = JsonDocument.Parse(PointRef.Schema);
            Assert.Equal(3, d.RootElement.GetProperty("oneOf").GetArrayLength());
        }

        // ---- AnchorFingerprint / ResolvedPoint ----

        [Fact]
        public void BuildFirma_StableAndRounded()
        {
            BBox2 box = BBox2.FromCorners(0, 0, 10, 5);
            string a = AnchorFingerprint.BuildFirma(box, new Point3(0, 0, 0), new Point3(10, 5, 0));
            string b = AnchorFingerprint.BuildFirma(box, new Point3(1e-9, 0, 0), new Point3(10, 5 + 1e-9, 0));
            Assert.Equal(a, b);
            Assert.Equal("0,0,10,5|0,0,0|10,5,0|-|-", a);
        }

        [Fact]
        public void BuildFirma_DetectaVerticeInteriorEditado()
        {
            // Un vértice interior movido dentro de la caja (otro vértice fija el máximo en Y): la caja y los extremos
            // no cambian; la longitud sí.
            BBox2 box = BBox2.FromCorners(0, 0, 10, 5);
            string antes = AnchorFingerprint.BuildFirma(box, new Point3(0, 0, 0), new Point3(10, 0, 0), 12.345678, 4);
            string despues = AnchorFingerprint.BuildFirma(box, new Point3(0, 0, 0), new Point3(10, 0, 0), 12.5, 4);
            Assert.NotEqual(antes, despues);
            Assert.Equal("0,0,10,5|0,0,0|10,0,0|12.345678|4", antes);
        }

        [Fact]
        public void BuildFirma_DetectaCambioDeNumeroDeVertices()
        {
            BBox2 box = BBox2.FromCorners(0, 0, 10, 5);
            string a = AnchorFingerprint.BuildFirma(box, new Point3(0, 0, 0), new Point3(10, 0, 0), 20, 4);
            string b = AnchorFingerprint.BuildFirma(box, new Point3(0, 0, 0), new Point3(10, 0, 0), 20, 5);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void BuildFirma_LongitudRedondeadaYSinExponente()
        {
            BBox2 box = BBox2.FromCorners(0, 0, 1e16, 1);
            string a = AnchorFingerprint.BuildFirma(box, null, null, 3.0000000001, null);
            string b = AnchorFingerprint.BuildFirma(box, null, null, 3.0, null);
            Assert.Equal(a, b);
            Assert.DoesNotContain("+", a);
            Assert.DoesNotContain("E", a);
            Assert.Equal("0,0,1,0|-|-|-|-", AnchorFingerprint.BuildFirma(BBox2.FromCorners(-1e-9, 0, 1, 0), null, null));
        }

        [Fact]
        public void BuildFirma_DetectsChanges()
        {
            BBox2 box = BBox2.FromCorners(0, 0, 10, 5);
            string a = AnchorFingerprint.BuildFirma(box, new Point3(0, 0, 0), new Point3(10, 5, 0));
            string moved = AnchorFingerprint.BuildFirma(box, new Point3(0, 0, 0), new Point3(10, 5.001, 0));
            Assert.NotEqual(a, moved);
        }

        [Fact]
        public void BuildFirma_NullsUseDash()
        {
            Assert.Equal("-|-|-|-|-", AnchorFingerprint.BuildFirma(null, null, null));
        }

        [Fact]
        public void AnchorFingerprint_JsonRoundTrip()
        {
            var f = new AnchorFingerprint("2A3F", "LINE", AnchorFingerprint.BuildFirma(BBox2.FromCorners(0, 0, 1, 1), new Point3(0, 0, 0), new Point3(1, 1, 0)));
            AnchorFingerprint back = AnchorFingerprint.FromJson(J(Serialize(f.ToJson())));
            Assert.Equal(f, back);
        }

        [Fact]
        public void ResolvedPoint_ToJson()
        {
            var rp = new ResolvedPoint(PointRef.FromEntity("2a3f", PointRef.PuntoInicio), new Point3(1, 2, 3), new AnchorFingerprint("2A3F", "LINE", "x"));
            Assert.Equal("{\"ref\":{\"handle\":\"2A3F\",\"punto\":\"inicio\"},\"punto\":[1,2,3],\"ancla\":\"2A3F\"}", Serialize(rp.ToJson()));

            var lit = new ResolvedPoint(PointRef.FromPoint(new Point3(1, 2, 0)), new Point3(1, 2, 0), null);
            Assert.Equal("{\"ref\":[1,2,0],\"punto\":[1,2,0],\"ancla\":null}", Serialize(lit.ToJson()));
        }
    }
}
