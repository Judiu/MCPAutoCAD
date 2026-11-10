using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// array_polar (protocol.md §5E.3.5): copias independientes de entidades existentes alrededor de un centro (PuntoRef):
    /// n posiciones (original incluido) en 'angulo_total' grados (360/−360 = vuelta completa equiespaciada), girando o no
    /// cada copia. Posiciones en <see cref="ArrayLayout.Polar"/>; protecciones de §5D.8 y cajas en <see cref="ArraySupport"/>.
    /// </summary>
    internal sealed class ArrayPolarHandler : GeneratorBase
    {
        private static readonly string[] AllowedNames =
            ArraySupport.AllowedWith("centro", "n", "angulo_total", "rotar_elementos", "base");

        public override string Name => "array_polar";

        public override string Description =>
            "ESCRIBE copias independientes de entidades existentes (handles) alrededor de un centro (PuntoRef): n posiciones en un ángulo total (360 = vuelta completa), con o sin rotación de los elementos. Tope 500 copias; entidades fuera de capas MCP-* exigen confirmar: true. Con preview: true solo calcula y devuelve números + preview_id.";

        public override JsonElement InputSchema { get; } = GenSchema.Build(
            ArraySupport.SharedProperties + ",\n"
            + @"    ""centro"": " + GenSchema.SchemaPuntoRef + @",
    ""n"": { ""type"": ""integer"", ""minimum"": 2, ""maximum"": 501, ""description"": ""Posiciones totales, original incluido (n − 1 copias)."" },
    ""angulo_total"": { ""type"": ""number"", ""description"": ""Grados (≠ 0; positivos antihorarios). 360 o −360 = vuelta completa con n posiciones equiespaciadas; otro valor = la última copia cae en angulo_total."" },
    ""rotar_elementos"": { ""type"": ""boolean"", ""default"": true, ""description"": ""true = cada copia gira con su posición; false = solo se traslada."" },
    ""base"": " + GenSchema.SchemaPuntoRef,
            "handles", "centro", "n", "angulo_total");

        public override JsonElement? OutputSchema { get; } = GenSchema.Output(ArraySupport.NumerosProperties);

        protected override IReadOnlyCollection<string> Allowed => AllowedNames;

        protected override string? DefaultConcept => null;

        private sealed class Params
        {
            public Params(ArraySupport.Shared shared, PointRef centro, int n, double anguloTotal, bool rotar, PointRef? basePoint)
            {
                Shared = shared;
                Centro = centro;
                N = n;
                AnguloTotal = anguloTotal;
                Rotar = rotar;
                Base = basePoint;
            }

            public ArraySupport.Shared Shared { get; }
            public PointRef Centro { get; }
            public int N { get; }
            public double AnguloTotal { get; }
            public bool Rotar { get; }
            public PointRef? Base { get; }
        }

        protected override object ParseOwn(JsonElement p)
        {
            ArraySupport.Shared shared = ArraySupport.Read(p);
            PointRef centro = GenSchema.RequiredPointRef(p, "centro");
            int n = ArraySupport.RequiredInt(p, "n", 2, 501);
            double anguloTotal = ArraySupport.RequiredNonZero(p, "angulo_total");
            bool rotar = ParamReader.OptionalBool(p, "rotar_elementos") ?? true;
            PointRef? basePoint = GenSchema.OptionalPointRef(p, "base");
            return new Params(shared, centro, n, anguloTotal, rotar, basePoint);
        }

        protected override IReadOnlyList<(PointRef Ref, string Path)> PointRefs(object own)
        {
            var p = (Params)own;
            var list = new List<(PointRef Ref, string Path)>(2) { (p.Centro, "centro") };
            if (p.Base != null)
            {
                list.Add((p.Base, "base"));
            }

            return list;
        }

        protected override GeneratedGeometry Plan(object own, IReadOnlyList<ResolvedPoint> puntos, Database db, Transaction tr, List<string> advertencias)
        {
            var p = (Params)own;
            ArraySupport.Sources src = ArraySupport.Resolve(p.Shared, db, tr, advertencias);
            Point3 centro = puntos[0].Punto;
            double bx, by;
            if (p.Base != null)
            {
                bx = puntos[1].Punto.X;
                by = puntos[1].Punto.Y;
            }
            else
            {
                BBox2 union = ArraySupport.RequireUnion(src);
                bx = (union.Xmin + union.Xmax) / 2.0;
                by = (union.Ymin + union.Ymax) / 2.0;
            }

            IReadOnlyList<ArrayLayout.Placement> placements = ArraySupport.Layout(
                () => ArrayLayout.Polar(centro.X, centro.Y, p.N, p.AnguloTotal, p.Rotar, bx, by));
            GeneratedGeometry g = ArraySupport.Build(p.Shared, src, placements, polar: true);
            g.Numeros["centro"] = centro.ToArray();
            g.Numeros["base"] = new[] { bx, by };
            g.Numeros["angulo_total"] = p.AnguloTotal;
            g.Numeros["rotar_elementos"] = p.Rotar;
            return g;
        }
    }
}
