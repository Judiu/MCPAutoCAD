using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// distancia (protocol.md §5E.2). Hilo principal; requiere documento; SOLO LECTURA (StartOpenCloseTransaction, sin
    /// paso de deshacer). INSUNITS = 0 no se rechaza.
    /// </summary>
    internal sealed class DistanciaHandler : IToolHandler
    {
        /// <summary>Por debajo de esta distancia en planta el ángulo no está definido (null).</summary>
        public const double AngleEpsilon = 1e-9;

        private static readonly string[] Allowed = { "a", "b" };

        public string Name => "distancia";

        public string Description =>
            "LEE la distancia entre dos PuntoRef (coordenadas o puntos de entidades existentes): distancia 3D y en planta, dx, dy, dz y ángulo en grados. No modifica nada.";

        public bool RequiresDocument => true;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""a"": " + PointRef.Schema + @",
    ""b"": " + PointRef.Schema + @"
  },
  ""required"": [""a"", ""b""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""distancia"": { ""type"": ""number"" },
    ""distancia_2d"": { ""type"": ""number"" },
    ""dx"": { ""type"": ""number"" },
    ""dy"": { ""type"": ""number"" },
    ""dz"": { ""type"": ""number"" },
    ""angulo"": { ""type"": [""number"", ""null""], ""description"": ""Grados de a→b en planta, [0, 360); null si la distancia en planta es ≤ 1e-9."" },
    ""a"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""b"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""puntos_resueltos"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""distancia"", ""distancia_2d"", ""dx"", ""dy"", ""dz"", ""angulo"", ""a"", ""b"", ""puntos_resueltos"", ""insunits"", ""unidades"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (PointRef a, PointRef b) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            return (GenSchema.RequiredPointRef(p, "a"), GenSchema.RequiredPointRef(p, "b"));
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (PointRef refA, PointRef refB) = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = AcadUtil.InsUnits(db);
            List<string> advertencias = AcadUtil.UnitWarnings(db);

            ResolvedPoint ra, rb;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                ra = PointRefResolver.Resolve(db, tr, refA, "a", advertencias);
                rb = PointRefResolver.Resolve(db, tr, refB, "b", advertencias);
                tr.Commit();
            }

            double dx = rb.Punto.X - ra.Punto.X;
            double dy = rb.Punto.Y - ra.Punto.Y;
            double dz = rb.Punto.Z - ra.Punto.Z;
            double d2 = Math.Sqrt(dx * dx + dy * dy);
            double d3 = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            object? angulo = d2 <= AngleEpsilon ? null : AcadUtil.ToDegrees360(Math.Atan2(dy, dx));

            return new Dictionary<string, object?>
            {
                ["pid"] = ctx.Pid,
                ["distancia"] = d3,
                ["distancia_2d"] = d2,
                ["dx"] = dx,
                ["dy"] = dy,
                ["dz"] = dz,
                ["angulo"] = angulo,
                ["a"] = ra.Punto.ToArray(),
                ["b"] = rb.Punto.ToArray(),
                ["puntos_resueltos"] = new List<Dictionary<string, object?>> { ra.ToJson(), rb.ToJson() },
                ["insunits"] = ins,
                ["unidades"] = UnitsTable.Name(ins),
                ["advertencias"] = advertencias,
            };
        }
    }
}
