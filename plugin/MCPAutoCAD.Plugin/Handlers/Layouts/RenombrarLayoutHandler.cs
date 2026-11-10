using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Clean;

namespace MCPAutoCAD.Plugin.Handlers.Layouts
{
    /// <summary>
    /// renombrar_layout (plugin 0.9.0, fase M2). Sin confirmar: true solo previsualiza. Con confirmar: true renombra con
    /// LayoutManager.RenameLayout en un paso de deshacer. No cambia la pestaña activa.
    /// </summary>
    internal sealed class RenombrarLayoutHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "nombre", "nuevo_nombre", "confirmar", "preview" };

        public string Name => "renombrar_layout";

        public string Description =>
            "Renombra un layout de papel. Sin confirmar: true solo previsualiza (no escribe). Con confirmar: true lo renombra en un " +
            "paso de deshacer; no toca el espacio modelo ni cambia la pestaña activa. Se admite cambiar solo mayúsculas/minúsculas.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""nombre"": " + LayoutWrite.SchemaLayout + @",
    ""nuevo_nombre"": " + LayoutWrite.SchemaNuevoNombre + @",
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""nombre"", ""nuevo_nombre""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""nombre_anterior"": { ""type"": ""string"" },
    ""nombre_nuevo"": { ""type"": ""string"" },
    ""es_actual"": { ""type"": ""boolean"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""nombre_anterior"", ""nombre_nuevo"", ""es_actual""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static (string nombre, string nuevo, CleanWrite.Common common) Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string nombre = LayoutWrite.ReadLayoutName(p, "nombre");
            string nuevo = LayoutWrite.ReadNewName(p, "nuevo_nombre");
            if (string.Equals(nombre, nuevo, StringComparison.Ordinal))
            {
                throw ToolException.InvalidParams("nuevo_nombre: es igual al nombre actual");
            }

            return (nombre, nuevo, CleanWrite.ReadCommon(p));
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            (string nombre, string nuevo, CleanWrite.Common common) = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(common.Confirmar, common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = CleanWrite.ReadUnits(db);
            var warnings = new List<string>(AcadUtil.UnitWarnings(db));
            string real;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                real = LayoutWrite.FindLayout(db, tr, nombre).Name;
                if (string.Equals(real, nuevo, StringComparison.Ordinal))
                {
                    throw ToolException.InvalidParams("nuevo_nombre: es igual al nombre actual");
                }

                string? clash = LayoutWrite.Clash(db, tr, nuevo, real);
                if (clash != null)
                {
                    throw ToolException.InvalidParams("nuevo_nombre: ya existe el layout '" + clash + "'");
                }

                tr.Commit();
            }

            bool current = LayoutWrite.IsCurrent(real, LayoutWrite.CurrentTab());
            Dictionary<string, object?> Build(CleanWrite.Mode m)
            {
                Dictionary<string, object?> r = CleanWrite.Response(ctx.Pid, m, ins, warnings);
                r["nombre_anterior"] = real;
                r["nombre_nuevo"] = nuevo;
                r["es_actual"] = current;
                return r;
            }

            if (mode == CleanWrite.Mode.Preview)
            {
                return Build(mode);
            }

            return UndoGroup.Run(doc, d =>
            {
                // Comprobación repetida dentro del paso de deshacer (el dibujo pudo cambiar desde la lectura). La
                // Transaction se cierra antes de llamar a LayoutManager, que abre el Layout por su cuenta.
                string again;
                string? temporal = null;
                using (Transaction tr = d.Database.TransactionManager.StartTransaction())
                {
                    again = LayoutWrite.FindLayout(d.Database, tr, real).Name;
                    if (LayoutWrite.Clash(d.Database, tr, nuevo, again) != null)
                    {
                        throw ToolException.InvalidParams("nuevo_nombre: ya existe el layout '" + nuevo + "'");
                    }

                    if (string.Equals(again, nuevo, StringComparison.OrdinalIgnoreCase))
                    {
                        // Solo cambian mayúsculas: RenameLayout podría encontrarse a sí mismo como duplicado; se pasa
                        // por un nombre temporal libre (los dos renombrados quedan en el mismo paso de deshacer).
                        for (int i = 0; i < 100 && temporal == null; i++)
                        {
                            string candidate = "~mcp" + Guid.NewGuid().ToString("N").Substring(0, 8);
                            if (LayoutWrite.Clash(d.Database, tr, candidate, null) == null)
                            {
                                temporal = candidate;
                            }
                        }

                        if (temporal == null)
                        {
                            throw new InvalidOperationException("No se encontró un nombre temporal libre para renombrar el layout.");
                        }
                    }

                    tr.Commit();
                }

                if (temporal != null)
                {
                    LayoutManager.Current.RenameLayout(again, temporal);
                    try
                    {
                        LayoutManager.Current.RenameLayout(temporal, nuevo);
                    }
                    catch (Exception ex)
                    {
                        // UndoGroup solo revierte con UNDOCTL completo: se intenta devolver el nombre original aquí mismo.
                        string estado;
                        try
                        {
                            LayoutManager.Current.RenameLayout(temporal, again);
                            estado = "se restauró el nombre '" + again + "'";
                        }
                        catch (Exception)
                        {
                            estado = "el layout quedó con el nombre temporal '" + temporal + "': renómbrelo a mano";
                        }

                        throw new ToolException(
                            ErrorCodes.Internal,
                            "No se pudo renombrar el layout '" + again + "' a '" + nuevo + "'.",
                            new Dictionary<string, object?> { ["detalle"] = ex.Message + "; " + estado });
                    }
                }
                else
                {
                    LayoutManager.Current.RenameLayout(again, nuevo);
                }

                return Build(CleanWrite.Mode.Write);
            });
        }
    }
}
