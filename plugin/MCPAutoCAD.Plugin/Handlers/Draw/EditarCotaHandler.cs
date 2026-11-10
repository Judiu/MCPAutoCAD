using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Clean;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// editar_cota (0.9.x). Hilo principal; requiere documento. Edita cotas EXISTENTES (<c>Dimension</c>, por handle o lote de hasta
    /// 200): texto de sustitución (<c>texto_sustituto</c>, con «&lt;&gt;» = medida; «» = quitar el texto), estilo de cota, posición del
    /// texto y escala global (DIMSCALE de la cota). Patrón preview/confirmar de <see cref="CleanWrite"/>: sin confirmar: true solo
    /// previsualiza; con confirmar: true escribe todo en UN paso de deshacer. Las reglas de protección (capas bloqueadas o xref,
    /// atributos, bloques, entidades del usuario) son las de <see cref="HandleGuard"/>. Texto de sustitución: mismo tratamiento que
    /// texto_sustituto se escribe TAL CUAL (sin escapar): el texto de cota admite códigos (%%c, %%d, %%p, \X, \P...). Solo se
    /// valida que no tenga saltos de línea y que las llaves estén balanceadas. Preview y escritura usan el mismo valor.
    /// </summary>
    internal sealed class EditarCotaHandler : IToolHandler
    {
        private static readonly string[] Allowed =
        {
            "handle", "handles", "texto_sustituto", "estilo", "posicion_texto", "escala_global", "confirmar", "preview",
        };

        private const int MaxLote = 200;
        private const int MaxTexto = 500;
        private const string OrigenTexto = "contenido_del_plano";

        public string Name => "editar_cota";

        public string Description =>
            "EDITA cotas existentes (Dimension, por handle o lote de hasta 200): cambia el texto de sustitución (con '<>' = medida; '' = " +
            "quitar), el estilo de cota, la posición del texto y la escala global (DIMSCALE de la cota), en un solo paso de deshacer. " +
            "Sin confirmar: true solo previsualiza. Rechaza entidades que no son cotas, atributos, entidades de bloques y capas bloqueadas o de xref.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""handle"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"", ""description"": ""Handle de la cota a editar (excluyente con handles)."" },
    ""handles"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""string"", ""pattern"": ""^[0-9A-Fa-f]{1,16}$"" }, ""description"": ""Lote de cotas (1–200, sin repetidos) con los mismos cambios. Excluyente con handle."" },
    ""texto_sustituto"": { ""type"": ""string"", ""minLength"": 0, ""maxLength"": 500, ""description"": ""Texto que sustituye a la medida; '<>' representa la medida real (p. ej. '<> m'). Cadena vacía = quitar el texto de sustitución (vuelve la medida). Se escribe tal cual: admite códigos de cota (%%c, %%d, %%p, \\X, \\P, {…}); sin saltos de línea, llaves balanceadas."" },
    ""estilo"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre de un estilo de cota existente (sin distinguir mayúsculas)."" },
    ""posicion_texto"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3, ""description"": ""Nueva posición del texto de la cota [x, y] o [x, y, z] (WCS)."" },
    ""escala_global"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Escala global (DIMSCALE) de esta cota, como override de la del estilo (> 0)."" },
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""allOf"": [
    { ""oneOf"": [ { ""required"": [""handle""] }, { ""required"": [""handles""] } ] },
    { ""anyOf"": [ { ""required"": [""texto_sustituto""] }, { ""required"": [""estilo""] }, { ""required"": [""posicion_texto""] }, { ""required"": [""escala_global""] } ] }
  ],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""n"": { ""type"": ""integer"" },
    ""n_usuario"": { ""type"": ""integer"" },
    ""espacio"": { ""type"": ""string"" },
    ""capas"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""handle"": { ""type"": ""string"" },
    ""antes"": { ""type"": ""object"" },
    ""despues"": { ""type"": ""object"" },
    ""cambios"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""handle"": { ""type"": ""string"" }, ""antes"": { ""type"": ""object"" }, ""despues"": { ""type"": ""object"" } } } },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""handles"", ""n"", ""cambios"", ""origen_texto""]
}");

        private sealed class Params
        {
            public Params(List<string> handles, string? texto, string? estilo, Point3? posicion, double? escala, CleanWrite.Common common)
            {
                Handles = handles;
                Texto = texto;
                Estilo = estilo;
                Posicion = posicion;
                Escala = escala;
                Common = common;
            }

            public List<string> Handles { get; }

            public string? Texto { get; }

            public string? Estilo { get; }

            public Point3? Posicion { get; }

            public double? Escala { get; }

            public CleanWrite.Common Common { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            bool hasHandle = ParamReader.Has(p, "handle");
            bool hasHandles = ParamReader.Has(p, "handles");
            if (hasHandle == hasHandles)
            {
                throw ToolException.InvalidParams("indique exactamente uno: handle (una cota) o handles (lote)");
            }

            List<string> handles = hasHandle
                ? new List<string> { ParamReader.OptionalHandle(p, "handle")?.ToUpperInvariant() ?? throw ToolException.InvalidParams("handle: obligatorio") }
                : ParamReader.RequiredHandles(p, "handles", 1, MaxLote);

            string? texto = ParamReader.OptionalString(p, "texto_sustituto", 0, MaxTexto);
            if (texto != null && texto.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                throw ToolException.InvalidParams("texto_sustituto: sin saltos de línea");
            }

            if (texto != null)
            {
                string? llaves = TextFormatParser.ValidateBraces(texto);
                if (llaves != null)
                {
                    throw ToolException.InvalidParams("texto_sustituto: " + llaves);
                }
            }

            string? estilo = ParamReader.OptionalString(p, "estilo", 1, 255);
            Point3? posicion = ParamReader.OptionalPoint(p, "posicion_texto");
            double? escala = ParamReader.OptionalNumber(p, "escala_global", 0, double.PositiveInfinity, exclusiveMin: true);
            if (texto == null && estilo == null && posicion == null && escala == null)
            {
                throw ToolException.InvalidParams("debe indicar al menos uno de: texto_sustituto, estilo, posicion_texto, escala_global");
            }

            CleanWrite.Common common = CleanWrite.ReadCommon(p);
            return new Params(handles, texto, estilo, posicion, escala, common);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(p.Common.Confirmar, p.Common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = CleanWrite.ReadUnits(db);
            int pid = ctx.Pid;

            if (mode == CleanWrite.Mode.Preview)
            {
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    // confirmar: true aquí solo evita el rechazo de entidades del usuario: la previsualización no escribe.
                    HandleGuard.Resolved res = HandleGuard.Resolve(db, tr, p.Handles, true, warnings);
                    var cambios = new List<Dictionary<string, object?>>(res.Items.Count);
                    for (int i = 0; i < res.Items.Count; i++)
                    {
                        Dimension dim = RequireDimension(res.Items[i].Entity, p.Handles[i]);
                        Dictionary<string, object?> antes = Snapshot(dim);
                        cambios.Add(Row(p.Handles[i], antes, Proposed(p, antes)));
                    }

                    if (p.Estilo != null)
                    {
                        ResolveDimStyleId(db, tr, p.Estilo);
                    }

                    tr.Commit();
                    return Response(pid, mode, ins, warnings, p, res, cambios);
                }
            }

            return UndoGroup.Run(doc, d => Write(d, p, pid, ins, warnings));
        }

        /// <summary>Escritura en UNA transacción (R1): resuelve de nuevo los handles y aplica los cambios sobre las cotas.</summary>
        private static object Write(Document doc, Params p, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                HandleGuard.Resolved res = HandleGuard.Resolve(db, tr, p.Handles, true, new List<string>());
                ObjectId styleId = p.Estilo != null ? ResolveDimStyleId(db, tr, p.Estilo) : ObjectId.Null;
                var cambios = new List<Dictionary<string, object?>>(res.Items.Count);
                for (int i = 0; i < res.Items.Count; i++)
                {
                    Dimension dim = RequireDimension(res.Items[i].Entity, p.Handles[i]);
                    Dictionary<string, object?> antes = Snapshot(dim);
                    dim.UpgradeOpen();
                    if (!styleId.IsNull)
                    {
                        dim.DimensionStyle = styleId;
                    }

                    if (p.Texto != null)
                    {
                        dim.DimensionText = p.Texto;   // tal cual: "" = quitar; "<>" = medida; admite códigos de cota
                    }

                    if (p.Posicion != null)
                    {
                        dim.UsingDefaultTextPosition = false;   // sin esto AutoCAD ignora la posición fijada
                        dim.TextPosition = AcadUtil.ToPoint3d(p.Posicion.Value);
                    }

                    if (p.Escala != null)
                    {
                        dim.Dimscale = p.Escala.Value;
                    }

                    try
                    {
                        dim.RecomputeDimensionBlock(true);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        // Sin bloque de cota regenerado: los valores se escriben igualmente.
                    }

                    cambios.Add(Row(p.Handles[i], antes, Snapshot(dim)));
                }

                tr.Commit();
                return Response(pid, CleanWrite.Mode.Write, ins, warnings, p, res, cambios);
            }
        }

        private static Dictionary<string, object?> Row(string handle, Dictionary<string, object?> antes, Dictionary<string, object?> despues)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["handle"] = handle,
                ["antes"] = antes,
                ["despues"] = despues,
            };
        }

        private static Dictionary<string, object?> Response(
            int pid, CleanWrite.Mode mode, int ins, List<string> warnings, Params p, HandleGuard.Resolved res,
            List<Dictionary<string, object?>> cambios)
        {
            Dictionary<string, object?> d = CleanWrite.Response(pid, mode, ins, warnings);
            d["handles"] = p.Handles;
            d["n"] = cambios.Count;
            d["n_usuario"] = res.NUsuario;
            d["espacio"] = res.Espacio;
            d["capas"] = res.Capas;
            d["cambios"] = cambios;
            d["handle"] = cambios[0]["handle"];
            d["antes"] = cambios[0]["antes"];
            d["despues"] = cambios[0]["despues"];
            d["origen_texto"] = OrigenTexto;
            return d;
        }

        private static Dimension RequireDimension(Entity e, string handle)
        {
            if (e is Dimension dim)
            {
                return dim;
            }

            string tipo = AcadUtil.DxfName(e);
            throw ToolException.InvalidParams(
                "handle: el tipo " + tipo + " no es una cota (DIMENSION)",
                new Dictionary<string, object?> { ["handle"] = handle, ["tipo"] = tipo });
        }

        /// <summary>Valores de la cota: texto de sustitución, estilo, posición del texto y escala global (DIMSCALE de la cota).</summary>
        private static Dictionary<string, object?> Snapshot(Dimension dim)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tipo"] = AcadUtil.DxfName(dim),
                ["capa"] = dim.Layer,
                ["texto"] = SafeText(() => dim.DimensionText),
                ["estilo"] = SafeText(() => dim.DimensionStyleName),
                ["posicion_texto"] = AcadUtil.PointOrNull(dim.TextPosition),
                ["escala_global"] = AcadUtil.NumberOrNull(dim.Dimscale),
                ["medida"] = SafeMeasurement(dim),
            };
        }

        /// <summary>Valores que quedarían tras aplicar los cambios pedidos (solo previsualización).</summary>
        private static Dictionary<string, object?> Proposed(Params p, Dictionary<string, object?> antes)
        {
            var d = new Dictionary<string, object?>(antes, StringComparer.Ordinal);
            if (p.Texto != null)
            {
                d["texto"] = p.Texto;
            }

            if (p.Estilo != null)
            {
                d["estilo"] = p.Estilo;
            }

            if (p.Posicion != null)
            {
                d["posicion_texto"] = AcadUtil.ToArray(AcadUtil.ToPoint3d(p.Posicion.Value));
            }

            if (p.Escala != null)
            {
                d["escala_global"] = p.Escala.Value;
            }

            return d;
        }

        /// <summary>Medida de la cota; una cota corrupta devuelve null en vez de tumbar la previsualización.</summary>
        private static object? SafeMeasurement(Dimension dim)
        {
            try
            {
                return AcadUtil.NumberOrNull(dim.Measurement);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return null;
            }
        }

        private static string SafeText(Func<string> read)
        {
            try
            {
                return read() ?? "";
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return "";
            }
        }

        /// <summary>Estilo de cota por nombre (sin mayúsculas). Inexistente → NOT_FOUND data.estilo.</summary>
        private static ObjectId ResolveDimStyleId(Database db, Transaction tr, string name)
        {
            var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            foreach (ObjectId candidate in table)
            {
                if (candidate.IsErased)
                {
                    continue;
                }

                var rec = (DimStyleTableRecord)tr.GetObject(candidate, OpenMode.ForRead);
                if (string.Equals(rec.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            throw new ToolException(
                ErrorCodes.NotFound,
                "El estilo de cota '" + name + "' no existe en el dibujo.",
                new Dictionary<string, object?> { ["estilo"] = name });
        }
    }
}
