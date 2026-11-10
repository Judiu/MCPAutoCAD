using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Gen;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Draw;

namespace MCPAutoCAD.Plugin.Handlers.Gen
{
    /// <summary>
    /// ejes_rejilla (protocol.md §5E.3.2): rejilla de ejes estructurales (LINE) con burbujas (CIRCLE) tangentes al extremo
    /// y rótulos TEXT 'medio_centro' en el centro de cada burbuja, girada 'rotacion' alrededor del origen (los textos sin
    /// girar). Geometría en <see cref="GridLayout.Build"/>; aquí solo se leen y validan los parámetros. Concepto 'ejes'.
    /// </summary>
    internal sealed class EjesRejillaHandler : GeneratorBase
    {
        private const int MaxSeparaciones = 100;

        private static readonly string[] Burbujas = { "inicio", "fin", "ambos" };
        private static readonly string[] AllowedNames = BuildAllowed();

        public override string Name => "ejes_rejilla";

        public override string Description =>
            "ESCRIBE una rejilla de ejes estructurales en la capa EJES: líneas con burbujas tangentes al extremo y rótulos centrados (TEXT), a partir de un origen (PuntoRef), listas de separaciones en x e y (no uniformes), extensión, radio de burbuja y altura de texto obligatoria. Rótulos por defecto 1, 2, 3… (x) y A, B, C… (y). Con preview: true solo calcula y devuelve números + preview_id.";

        public override JsonElement InputSchema { get; } = GenSchema.Build(
            @"    ""origen"": " + GenSchema.SchemaPuntoRef + @",
    ""separaciones_x"": { ""type"": ""array"", ""minItems"": 0, ""maxItems"": 100, ""items"": { ""type"": ""number"", ""exclusiveMinimum"": 0 }, ""description"": ""Distancias entre ejes VERTICALES consecutivos (n + 1 ejes a lo largo de X); [] = un solo eje."" },
    ""separaciones_y"": { ""type"": ""array"", ""minItems"": 0, ""maxItems"": 100, ""items"": { ""type"": ""number"", ""exclusiveMinimum"": 0 }, ""description"": ""Distancias entre ejes HORIZONTALES consecutivos (n + 1 ejes a lo largo de Y); [] = un solo eje."" },
    ""rotulos_x"": { ""type"": ""array"", ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 10 }, ""description"": ""Rótulos de los ejes verticales (separaciones_x + 1 elementos); por defecto 1, 2, 3…"" },
    ""rotulos_y"": { ""type"": ""array"", ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 10 }, ""description"": ""Rótulos de los ejes horizontales (separaciones_y + 1 elementos); por defecto A, B, …, Z, AA, AB…"" },
    ""extension"": { ""type"": ""number"", ""minimum"": 0, ""description"": ""Cuánto sobresale cada eje más allá del último eje perpendicular, por cada extremo."" },
    ""radio_burbuja"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Radio de la burbuja (tangente al extremo del eje)."" },
    ""altura_texto"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Altura de los rótulos (obligatoria; la oficina usa 0,175 m)."" },
    ""burbujas"": { ""type"": ""string"", ""enum"": [""inicio"", ""fin"", ""ambos""], ""default"": ""fin"", ""description"": ""Extremo(s) con burbuja: fin = arriba (ejes x) / derecha (ejes y)."" },
    ""rotacion"": { ""type"": ""number"", ""default"": 0, ""description"": ""Grados antihorarios: gira toda la rejilla alrededor del origen (los rótulos no giran)."" },
    ""estilo"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Estilo de texto existente de los rótulos. Por defecto el de la oficina, '75', si existe en el dibujo; si no, el actual (con advertencia)."" },
    ""capa"": " + DrawWrite.SchemaCapa,
            "origen", "separaciones_x", "separaciones_y", "extension", "radio_burbuja", "altura_texto");

        public override JsonElement? OutputSchema { get; } = GenSchema.Output(
            @"        ""n_ejes_x"": { ""type"": ""integer"" },
        ""n_ejes_y"": { ""type"": ""integer"" },
        ""ancho"": { ""type"": ""number"" },
        ""alto"": { ""type"": ""number"" },
        ""posiciones_x"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
        ""posiciones_y"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
        ""n_burbujas"": { ""type"": ""integer"" }");

        protected override IReadOnlyCollection<string> Allowed => AllowedNames;

        protected override string? DefaultConcept => "ejes";

        private static string[] BuildAllowed()
        {
            var list = new List<string>
            {
                "origen", "separaciones_x", "separaciones_y", "rotulos_x", "rotulos_y", "extension", "radio_burbuja",
                "altura_texto", "burbujas", "rotacion", "estilo", "capa",
            };
            list.AddRange(GenSchema.CommonParams);
            return list.ToArray();
        }

        private sealed class Params
        {
            public Params(PointRef origen, List<double> sepX, List<double> sepY, List<string>? rotX, List<string>? rotY, double extension, double radio, double altura, string burbujas, double rotacion, string? estilo, string? capa)
            {
                Origen = origen;
                SepX = sepX;
                SepY = sepY;
                RotX = rotX;
                RotY = rotY;
                Extension = extension;
                Radio = radio;
                Altura = altura;
                Burbujas = burbujas;
                Rotacion = rotacion;
                Estilo = estilo;
                Capa = capa;
            }

            public PointRef Origen { get; }
            public List<double> SepX { get; }
            public List<double> SepY { get; }
            public List<string>? RotX { get; }
            public List<string>? RotY { get; }
            public double Extension { get; }
            public double Radio { get; }
            public double Altura { get; }
            public string Burbujas { get; }
            public double Rotacion { get; }
            public string? Estilo { get; }
            public string? Capa { get; }
        }

        protected override object ParseOwn(JsonElement p)
        {
            PointRef origen = GenSchema.RequiredPointRef(p, "origen");
            List<double> sepX = ReadSeparations(p, "separaciones_x");
            List<double> sepY = ReadSeparations(p, "separaciones_y");
            List<string>? rotX = ReadLabels(p, "rotulos_x");
            List<string>? rotY = ReadLabels(p, "rotulos_y");
            if (!p.TryGetProperty("extension", out _))
            {
                throw ToolException.InvalidParams("extension: obligatorio");
            }

            double extension = PrimitiveSupport.OptionalNonNegative(p, "extension")!.Value;
            double radio = ParamReader.RequiredNumber(p, "radio_burbuja", 0, double.PositiveInfinity, exclusiveMin: true);
            double altura = ParamReader.RequiredNumber(p, "altura_texto", 0, double.PositiveInfinity, exclusiveMin: true);
            string burbujas = ParamReader.OptionalEnum(p, "burbujas", Burbujas) ?? "fin";
            double rotacion = PrimitiveSupport.OptionalFinite(p, "rotacion") ?? 0.0;
            string? estilo = ParamReader.OptionalString(p, "estilo", 1, 255);
            string? capa = ParamReader.OptionalString(p, "capa", 1, 255);
            if (capa != null)
            {
                DrawLayerFactory.Plan(capa, "ejes");
            }

            return new Params(origen, sepX, sepY, rotX, rotY, extension, radio, altura, burbujas, rotacion, estilo, capa);
        }

        /// <summary>Array obligatorio de 0–100 números &gt; 0 con los mensajes de §5E.3.2.</summary>
        private static List<double> ReadSeparations(JsonElement p, string name)
        {
            if (!p.TryGetProperty(name, out JsonElement el))
            {
                throw ToolException.InvalidParams(name + ": obligatorio");
            }

            if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() > MaxSeparaciones)
            {
                throw ToolException.InvalidParams(name + ": debe tener entre 0 y 100 elementos");
            }

            var list = new List<double>(el.GetArrayLength());
            int i = 0;
            foreach (JsonElement item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double v) || !double.IsFinite(v) || v <= 0)
                {
                    throw ToolException.InvalidParams(name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]: debe ser un número mayor que 0");
                }

                list.Add(v);
                i++;
            }

            return list;
        }

        /// <summary>
        /// Array opcional de cadenas (tipo); el número de elementos, la longitud 1–10 y los repetidos los valida
        /// GridLayout.Build con los textos del contrato.
        /// </summary>
        private static List<string>? ReadLabels(JsonElement p, string name) =>
            ParamReader.OptionalStringArray(p, name, 0, MaxSeparaciones + 1, 0);

        protected override IReadOnlyList<(PointRef Ref, string Path)> PointRefs(object own)
        {
            var p = (Params)own;
            return new List<(PointRef Ref, string Path)> { (p.Origen, "origen") };
        }

        protected override GeneratedGeometry Plan(object own, IReadOnlyList<ResolvedPoint> puntos, Database db, Transaction tr, List<string> advertencias)
        {
            var p = (Params)own;

            // Estilo dado e inexistente → NOT_FOUND antes de escribir nada.
            if (p.Estilo != null)
            {
                PrimitiveSupport.ResolveTextStyle(db, tr, p.Estilo);
            }

            // Sin 'estilo' (corrección del gate en vivo de Fase 4A, 2026-10-27): el de los rótulos de eje de la oficina
            // ("75", o lps.json estilos_texto.ejes si existe la clave) cuando existe en el dibujo; si no, el actual con
            // advertencia. confirmar recalcula con esta misma regla.
            (string? estilo, string? aviso) = AxisLabelStyle.Choose(
                p.Estilo,
                LpsStandard.TextStyleFor("ejes"),
                nombre => FindTextStyle(db, tr, nombre),
                PrimitiveSupport.ResolveTextStyle(db, tr, null).name);
            if (aviso != null)
            {
                advertencias.Add(aviso);
            }

            GridLayout.GridResult r;
            try
            {
                IReadOnlyList<string> rotX = p.RotX ?? GridLayout.DefaultNumericLabels(p.SepX.Count + 1);
                IReadOnlyList<string> rotY = p.RotY ?? GridLayout.DefaultLetterLabels(p.SepY.Count + 1);
                r = GridLayout.Build(puntos[0].Punto, p.SepX, p.SepY, rotX, rotY, p.Extension, p.Radio, p.Altura, p.Burbujas, p.Rotacion, estilo, p.Capa);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }

            var numeros = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["n_ejes_x"] = r.PosicionesX.Count,
                ["n_ejes_y"] = r.PosicionesY.Count,
                ["ancho"] = r.Ancho,
                ["alto"] = r.Alto,
                ["posiciones_x"] = r.PosicionesX,
                ["posiciones_y"] = r.PosicionesY,
                ["n_burbujas"] = r.NBurbujas,
                ["bbox"] = r.Bbox.ToArray2D(),
            };
            return new GeneratedGeometry(r.Entidades, numeros);
        }

        /// <summary>Nombre real de un estilo de texto existente (sin distinguir mayúsculas) o null si no existe (lectura).</summary>
        private static string? FindTextStyle(Database db, Transaction tr, string nombre)
        {
            try
            {
                return PrimitiveSupport.ResolveTextStyle(db, tr, nombre).name;
            }
            catch (ToolException ex) when (ex.Code == ErrorCodes.NotFound)
            {
                return null;
            }
        }
    }
}
