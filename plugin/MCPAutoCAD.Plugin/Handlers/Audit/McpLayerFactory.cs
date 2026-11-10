using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// Capas MCP-* de los métodos de escritura de auditoría (protocol.md §5C.9, §5C.10): validación del nombre y
    /// creación de la capa dentro de la transacción de escritura con las propiedades de lps.json.capas_mcp
    /// (o el respaldo fijo: color 1, Continuous, grosor 35, imprimible). Solo hilo principal salvo
    /// <see cref="RequireMcpLayerName"/>, que no toca AutoCAD.
    /// </summary>
    internal static class McpLayerFactory
    {
        public const string InvalidLayerName = "capa: solo se admiten capas MCP-*";
        public const string LockedLayer = "capa bloqueada";

        private const int FallbackColor = 1;
        private const int FallbackLineWeight = 35;
        private const bool FallbackPlot = true;

        /// <summary>
        /// INVALID_PARAMS "capa: solo se admiten capas MCP-*" si el nombre es "0" o no empieza por "MCP-"
        /// (sin distinguir mayúsculas). No toca AutoCAD: se usa en ValidateParams.
        /// </summary>
        public static void RequireMcpLayerName(string capa)
        {
            if (capa == null
                || string.Equals(capa.Trim(), "0", StringComparison.Ordinal)
                || !capa.StartsWith(PluginInfo.McpLayerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw ToolException.InvalidParams(InvalidLayerName);
            }
        }

        /// <summary>
        /// Capa <paramref name="nombre"/> lista para escribir: si existe (sin mayúsculas) se usa; bloqueada →
        /// INVALID_PARAMS "capa bloqueada"; congelada/apagada → advertencia. Si no existe se crea en
        /// <paramref name="tr"/> (misma transacción = mismo paso de deshacer) con capas_mcp[nombre] o el respaldo.
        /// </summary>
        public static ObjectId EnsureLayer(Database db, Transaction tr, string nombre, List<string> advertencias, out bool creada)
        {
            creada = false;
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            ObjectId id = FindLayer(table, nombre);
            if (!id.IsNull)
            {
                var existing = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                RequireWritable(existing, advertencias);
                return id;
            }

            try
            {
                SymbolUtilityServices.ValidateSymbolName(nombre, false);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                throw ToolException.InvalidParams("capa: nombre de capa no válido", new Dictionary<string, object?> { ["capa"] = nombre });
            }

            LayerSpec spec = SpecFor(nombre);
            var ltr = new LayerTableRecord
            {
                Name = nombre,
                Color = Color.FromColorIndex(ColorMethod.ByAci, (short)spec.Color),
                LinetypeObjectId = ResolveLinetype(db, tr, spec.Linetype),
                LineWeight = spec.LineWeight,
                IsPlottable = spec.Plot,
            };
            table.UpgradeOpen();
            id = table.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            creada = true;
            return id;
        }

        /// <summary>Bloqueada → INVALID_PARAMS "capa bloqueada" (data.capa); congelada/apagada → advertencia de marcas.</summary>
        public static void RequireWritable(LayerTableRecord layer, List<string> advertencias)
        {
            if (layer.IsLocked)
            {
                throw ToolException.InvalidParams(LockedLayer, new Dictionary<string, object?> { ["capa"] = layer.Name });
            }

            if (layer.IsFrozen || layer.IsOff)
            {
                string state = layer.IsFrozen && layer.IsOff ? "congelada y apagada" : layer.IsFrozen ? "congelada" : "apagada";
                advertencias.Add("La capa " + layer.Name + " está " + state + ": las marcas no serán visibles.");
            }
        }

        /// <summary>Busca una capa no borrada por nombre, sin distinguir mayúsculas (como AutoCAD).</summary>
        public static ObjectId FindLayer(LayerTable table, string name)
        {
            bool exists;
            try
            {
                exists = table.Has(name);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Nombre con caracteres no válidos para un símbolo: no puede existir como capa.
                exists = false;
            }

            if (exists)
            {
                ObjectId id = table[name];
                if (!id.IsErased)
                {
                    return id;
                }
            }

            return ObjectId.Null;
        }

        /// <summary>Tipo de línea cargado con ese nombre (sin mayúsculas) o Continuous si no está cargado.</summary>
        private static ObjectId ResolveLinetype(Database db, Transaction tr, string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return db.ContinuousLinetype;
            }

            try
            {
                var lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                if (lt.Has(name))
                {
                    ObjectId id = lt[name];
                    if (!id.IsNull && !id.IsErased)
                    {
                        return id;
                    }
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Nombre inválido o tabla ilegible: se usa Continuous.
            }

            return db.ContinuousLinetype;
        }

        /// <summary>Propiedades de capas_mcp[nombre] (clave sin mayúsculas) o el respaldo fijo.</summary>
        private static LayerSpec SpecFor(string nombre)
        {
            var spec = new LayerSpec(FallbackColor, null, (LineWeight)FallbackLineWeight, FallbackPlot);
            try
            {
                JsonElement? root = LpsStandard.Root;
                if (root == null
                    || !root.Value.TryGetProperty("capas_mcp", out JsonElement mcp)
                    || mcp.ValueKind != JsonValueKind.Object)
                {
                    return spec;
                }

                foreach (JsonProperty layer in mcp.EnumerateObject())
                {
                    if (!string.Equals(layer.Name, nombre, StringComparison.OrdinalIgnoreCase) || layer.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    JsonElement def = layer.Value;
                    int color = FallbackColor;
                    if (def.TryGetProperty("color", out JsonElement c) && c.ValueKind == JsonValueKind.Number
                        && c.TryGetInt32(out int ci) && ci >= 1 && ci <= 255)
                    {
                        color = ci;
                    }

                    string? linetype = null;
                    if (def.TryGetProperty("tipo_linea", out JsonElement t) && t.ValueKind == JsonValueKind.String)
                    {
                        linetype = t.GetString();
                    }

                    LineWeight weight = (LineWeight)FallbackLineWeight;
                    if (def.TryGetProperty("grosor", out JsonElement g) && g.ValueKind == JsonValueKind.Number
                        && g.TryGetInt32(out int gi) && Enum.IsDefined(typeof(LineWeight), gi))
                    {
                        weight = (LineWeight)gi;
                    }

                    bool plot = FallbackPlot;
                    if (def.TryGetProperty("plot", out JsonElement pl))
                    {
                        if (pl.ValueKind == JsonValueKind.True || pl.ValueKind == JsonValueKind.False)
                        {
                            plot = pl.GetBoolean();
                        }
                        else if (pl.ValueKind == JsonValueKind.Number && pl.TryGetInt32(out int pi))
                        {
                            plot = pi != 0;
                        }
                    }

                    return new LayerSpec(color, linetype, weight, plot);
                }
            }
            catch (Exception)
            {
                // lps.json con forma inesperada: respaldo fijo.
            }

            return spec;
        }

        private readonly struct LayerSpec
        {
            public LayerSpec(int color, string? linetype, LineWeight lineWeight, bool plot)
            {
                Color = color;
                Linetype = linetype;
                LineWeight = lineWeight;
                Plot = plot;
            }

            public int Color { get; }
            public string? Linetype { get; }
            public LineWeight LineWeight { get; }
            public bool Plot { get; }
        }
    }
}
