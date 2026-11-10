using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// array_rectangular (protocol.md §5E.3.5): copias independientes de entidades existentes en una retícula de
    /// filas × columnas (la posición (0, 0) es el original), girada 'angulo' alrededor del punto base. Posiciones en
    /// <see cref="ArrayLayout.Rectangular"/>; protecciones de §5D.8 y cajas en <see cref="ArraySupport"/>.
    /// </summary>
    internal sealed class ArrayRectangularHandler : GeneratorBase
    {
        private static readonly string[] AllowedNames =
            ArraySupport.AllowedWith("filas", "columnas", "sep_filas", "sep_columnas", "angulo", "base");

        public override string Name => "array_rectangular";

        public override string Description =>
            "ESCRIBE copias independientes de entidades existentes (handles) en una retícula de filas × columnas con separaciones y ángulo opcional (no es una matriz asociativa). Tope 500 copias; entidades fuera de capas MCP-* exigen confirmar: true. Con preview: true solo calcula y devuelve números + preview_id.";

        public override JsonElement InputSchema { get; } = GenSchema.Build(
            ArraySupport.SharedProperties + ",\n"
            + @"    ""filas"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 100, ""description"": ""Número de filas (eje Y antes de girar), contando la del original."" },
    ""columnas"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 100, ""description"": ""Número de columnas (eje X antes de girar), contando la del original. filas × columnas ≥ 2."" },
    ""sep_filas"": { ""type"": ""number"", ""description"": ""Separación entre filas (≠ 0; negativa = hacia −Y)."" },
    ""sep_columnas"": { ""type"": ""number"", ""description"": ""Separación entre columnas (≠ 0; negativa = hacia −X)."" },
    ""angulo"": { ""type"": ""number"", ""default"": 0, ""description"": ""Grados antihorarios desde +X: gira la retícula alrededor del punto base."" },
    ""base"": " + GenSchema.SchemaPuntoRef,
            "handles", "filas", "columnas", "sep_filas", "sep_columnas");

        public override JsonElement? OutputSchema { get; } = GenSchema.Output(ArraySupport.NumerosProperties);

        protected override IReadOnlyCollection<string> Allowed => AllowedNames;

        protected override string? DefaultConcept => null;

        private sealed class Params
        {
            public Params(ArraySupport.Shared shared, int filas, int columnas, double sepFilas, double sepColumnas, double angulo, PointRef? basePoint)
            {
                Shared = shared;
                Filas = filas;
                Columnas = columnas;
                SepFilas = sepFilas;
                SepColumnas = sepColumnas;
                Angulo = angulo;
                Base = basePoint;
            }

            public ArraySupport.Shared Shared { get; }
            public int Filas { get; }
            public int Columnas { get; }
            public double SepFilas { get; }
            public double SepColumnas { get; }
            public double Angulo { get; }
            public PointRef? Base { get; }
        }

        protected override object ParseOwn(JsonElement p)
        {
            ArraySupport.Shared shared = ArraySupport.Read(p);
            int filas = ArraySupport.RequiredInt(p, "filas", 1, 100);
            int columnas = ArraySupport.RequiredInt(p, "columnas", 1, 100);
            double sepFilas = ArraySupport.RequiredNonZero(p, "sep_filas");
            double sepColumnas = ArraySupport.RequiredNonZero(p, "sep_columnas");
            double angulo = PrimitiveSupport.OptionalFinite(p, "angulo") ?? 0.0;
            PointRef? basePoint = GenSchema.OptionalPointRef(p, "base");
            return new Params(shared, filas, columnas, sepFilas, sepColumnas, angulo, basePoint);
        }

        protected override IReadOnlyList<(PointRef Ref, string Path)> PointRefs(object own)
        {
            var p = (Params)own;
            var list = new List<(PointRef Ref, string Path)>(1);
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
            double bx, by;
            if (p.Base != null)
            {
                bx = puntos[0].Punto.X;
                by = puntos[0].Punto.Y;
            }
            else
            {
                BBox2 union = ArraySupport.RequireUnion(src);
                bx = union.Xmin;
                by = union.Ymin;
            }

            IReadOnlyList<ArrayLayout.Placement> placements = ArraySupport.Layout(
                () => ArrayLayout.Rectangular(p.Filas, p.Columnas, p.SepFilas, p.SepColumnas, p.Angulo, bx, by));
            GeneratedGeometry g = ArraySupport.Build(p.Shared, src, placements, polar: false);
            g.Numeros["filas"] = p.Filas;
            g.Numeros["columnas"] = p.Columnas;
            g.Numeros["base"] = new[] { bx, by };
            return g;
        }
    }
}
