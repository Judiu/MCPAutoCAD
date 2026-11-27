using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// cargar_tipo_linea (0.10.7). Hilo principal; requiere documento. Carga uno o varios tipos de línea estándar en el
    /// dibujo desde <c>acadiso.lin</c> / <c>acad.lin</c> en un solo paso de deshacer. Si todos los pedidos ya están
    /// cargados responde sin abrir UndoGroup (<c>escrito: false</c>).
    /// </summary>
    internal sealed class CargarTipoLineaHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "nombre", "nombres", "archivo" };
        private const int MaxLote = 50;

        public string Name => "cargar_tipo_linea";

        public string Description =>
            "CARGA tipos de línea en el dibujo desde acadiso.lin o acad.lin (uno con 'nombre' o hasta 50 con 'nombres') en un solo " +
            "paso de deshacer. Por defecto busca en acadiso.lin/acad.lin según MEASUREMENT y el alternativo. Si todos ya están " +
            "cargados devuelve escrito: false.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""nombre"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Nombre del tipo de línea a cargar (p. ej. DASHDOT, DASHED, CENTER, HIDDEN). Excluyente con 'nombres'."" },
    ""nombres"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 }, ""description"": ""Lista de tipos de línea (1–50) a cargar en un solo paso de deshacer. Excluyente con 'nombre'."" },
    ""archivo"": { ""type"": ""string"", ""enum"": [""acadiso.lin"", ""acad.lin""], ""description"": ""Fichero estándar de definiciones (opcional; por defecto se prueba acadiso.lin o acad.lin según MEASUREMENT y su alternativo)."" }
  },
  ""allOf"": [
    { ""oneOf"": [ { ""required"": [""nombre""] }, { ""required"": [""nombres""] } ] }
  ],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""escrito"": { ""type"": ""boolean"" },
    ""cargados"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""nombre"": { ""type"": ""string"" }, ""archivo"": { ""type"": ""string"" } } } },
    ""ya_cargados"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""no_encontrados"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""disponibles_en_dibujo"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""escrito"", ""cargados"", ""ya_cargados"", ""no_encontrados"", ""disponibles_en_dibujo"", ""insunits"", ""unidades"", ""advertencias""]
}");

        private sealed class Params
        {
            public Params(List<string> nombres, string? archivo)
            {
                Nombres = nombres;
                Archivo = archivo;
            }

            public List<string> Nombres { get; }

            public string? Archivo { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            bool hasNombre = ParamReader.Has(p, "nombre");
            bool hasNombres = ParamReader.Has(p, "nombres");
            if (hasNombre == hasNombres)
            {
                throw ToolException.InvalidParams(hasNombre
                    ? "nombre y nombres son excluyentes: indique uno u otro"
                    : "nombre o nombres: debe indicar un tipo de línea o una lista");
            }

            var rawNames = new List<string>();
            if (hasNombre)
            {
                rawNames.Add(ParamReader.RequiredText(p, "nombre", 1, 255));
            }
            else
            {
                JsonElement arr = p.GetProperty("nombres");
                if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 1 || arr.GetArrayLength() > MaxLote)
                {
                    throw ToolException.InvalidParams("nombres: debe ser un array de 1 a 50 textos");
                }

                int idx = 0;
                foreach (JsonElement el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String)
                    {
                        throw ToolException.InvalidParams("nombres[" + idx + "]: debe ser un texto");
                    }

                    string item = (el.GetString() ?? "").Trim();
                    if (item.Length < 1 || item.Length > 255)
                    {
                        throw ToolException.InvalidParams("nombres[" + idx + "]: debe tener entre 1 y 255 caracteres");
                    }

                    rawNames.Add(item);
                    idx++;
                }
            }

            var nombres = new List<string>(rawNames.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in rawNames)
            {
                string trimmed = n.Trim();
                string? nameErr = EntityPropertyRules.LinetypeNameError(trimmed);
                if (nameErr != null)
                {
                    throw ToolException.InvalidParams(nameErr);
                }

                if (seen.Add(trimmed))
                {
                    nombres.Add(trimmed);
                }
            }

            string? archivo = ParamReader.OptionalString(p, "archivo", 1, 64);
            if (archivo != null && !EntityPropertyRules.IsAllowedStandardLinetypeFile(archivo))
            {
                throw ToolException.InvalidParams("archivo: solo se admiten 'acadiso.lin' o 'acad.lin'");
            }

            return new Params(nombres, archivo?.Trim().ToLowerInvariant());
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            foreach (string n in p.Nombres)
            {
                try
                {
                    SymbolUtilityServices.ValidateSymbolName(n, false);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    throw ToolException.InvalidParams(
                        "tipo_linea: '" + n + "' no es un nombre de símbolo válido.",
                        new Dictionary<string, object?> { ["nombre"] = n });
                }
            }

            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = AcadUtil.InsUnits(db);
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int pid = ctx.Pid;

            var alreadyLoaded = new List<string>();
            var pending = new List<string>();
            foreach (string n in p.Nombres)
            {
                if (string.Equals(n, "Continuous", StringComparison.OrdinalIgnoreCase)
                    || !CambiarPropiedadesHandler.FindLinetype(db, n).IsNull)
                {
                    alreadyLoaded.Add(n);
                }
                else
                {
                    pending.Add(n);
                }
            }

            if (pending.Count == 0)
            {
                return BuildResponse(
                    pid,
                    escrito: false,
                    new List<Dictionary<string, object?>>(),
                    alreadyLoaded,
                    new List<string>(),
                    CambiarPropiedadesHandler.LoadedLinetypes(db),
                    ins,
                    warnings);
            }

            return UndoGroup.Run(doc, d =>
            {
                Database writeDb = d.Database;
                var loaded = new List<Dictionary<string, object?>>();
                var notFound = new List<string>();
                foreach (string n in pending)
                {
                    if (CambiarPropiedadesHandler.TryEnsureLinetypeLoaded(writeDb, n, p.Archivo, out string? file))
                    {
                        loaded.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["nombre"] = CambiarPropiedadesHandler.LoadedName(writeDb, n) ?? n,
                            ["archivo"] = file ?? (p.Archivo ?? EntityPropertyRules.LinetypeFileFor(AcadUtil.Measurement(writeDb))),
                        });
                    }
                    else
                    {
                        notFound.Add(n);
                        string scope = p.Archivo ?? "acadiso.lin/acad.lin";
                        warnings.Add("tipo_linea: '" + n + "' no se encontró en " + scope + ".");
                    }
                }

                return BuildResponse(
                    pid,
                    escrito: loaded.Count > 0,
                    loaded,
                    alreadyLoaded,
                    notFound,
                    CambiarPropiedadesHandler.LoadedLinetypes(writeDb),
                    ins,
                    warnings);
            });
        }

        private static Dictionary<string, object?> BuildResponse(
            int pid,
            bool escrito,
            List<Dictionary<string, object?>> cargados,
            List<string> yaCargados,
            List<string> noEncontrados,
            List<string> disponibles,
            int ins,
            List<string> warnings)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = pid,
                ["escrito"] = escrito,
                ["cargados"] = cargados,
                ["ya_cargados"] = yaCargados,
                ["no_encontrados"] = noEncontrados,
                ["disponibles_en_dibujo"] = disponibles,
                ["insunits"] = ins,
                ["unidades"] = UnitsTable.Name(ins),
                ["advertencias"] = warnings,
            };
        }
    }
}
