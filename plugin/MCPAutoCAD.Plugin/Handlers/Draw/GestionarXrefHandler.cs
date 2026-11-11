using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// gestionar_xref (fallo 37, plugin 0.10.3). Hilo principal; requiere documento; ESCRITURA con vista previa (sin
    /// <c>confirmar: true</c> no cambia nada). Actúa sobre UNA referencia existente por su nombre: recargar (ReloadXrefs),
    /// descargar (UnloadXrefs) o separar (DetachXref, que borra la definición y sus INSERT). La vista previa cuenta las
    /// inserciones que desaparecerían. Un paso de deshacer.
    /// </summary>
    internal sealed class GestionarXrefHandler : IToolHandler
    {
        private static readonly string[] Allowed = { "nombre", "accion", "confirmar" };

        public string Name => "gestionar_xref";

        public string Description =>
            "ESCRIBE sobre una referencia externa (xref) existente del dibujo activo: 'recargar' (vuelve a leer el archivo), " +
            "'descargar' (la descarga; sus INSERT se quedan) o 'separar' (la quita y borra sus INSERT). Sin confirmar: true solo " +
            "previsualiza (estado actual y, en 'separar', cuántas inserciones desaparecerían). Un paso de deshacer; el deshacer de recargar y descargar aún no está verificado en vivo.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs;

        /// <summary>Un timeout puede dejar la acción aplicada: hay que mirar el estado antes de repetirla (§3, regla 7).</summary>
        public string? TimeoutHint(JsonElement parameters)
        {
            string nombre = parameters.TryGetProperty("nombre", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            return "Compruebe con xrefs el estado de la referencia '" + nombre + "' antes de repetir la acción: puede estar ya aplicada.";
        }

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""nombre"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 200, ""description"": ""Nombre de la referencia externa en el dibujo activo (tal como lo lista xrefs)."" },
    ""accion"": { ""type"": ""string"", ""enum"": [""recargar"", ""descargar"", ""separar""], ""description"": ""'recargar' = vuelve a leer el archivo; 'descargar' = la descarga (sus INSERT se conservan); 'separar' = la quita del dibujo y borra sus INSERT."" },
    ""confirmar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Obligatorio para aplicar la acción. Sin él solo previsualiza (estado y número de inserciones afectadas)."" }
  },
  ""required"": [""nombre"", ""accion""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""preview"": { ""type"": ""boolean"" },
    ""ejecutada"": { ""type"": ""boolean"" },
    ""nombre"": { ""type"": ""string"" },
    ""accion"": { ""type"": ""string"", ""enum"": [""recargar"", ""descargar"", ""separar""] },
    ""estado_antes"": { ""type"": ""string"" },
    ""estado_despues"": { ""type"": [""string"", ""null""] },
    ""n_inserciones"": { ""type"": ""integer"" },
    ""inserciones_borradas"": { ""type"": ""integer"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""preview"", ""ejecutada"", ""nombre"", ""accion"", ""estado_antes"", ""estado_despues"", ""n_inserciones"", ""inserciones_borradas"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private sealed class Params
        {
            public string Nombre { get; set; } = "";
            public string Accion { get; set; } = "";
            public bool Confirmar { get; set; }
        }

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string nombre = ParamReader.RequiredText(p, "nombre", 1, XrefRules.MaxNombre);
            if (!p.TryGetProperty("accion", out _))
            {
                throw ToolException.InvalidParams("accion: obligatorio (recargar, descargar o separar)");
            }

            string accion = ParamReader.OptionalEnum(p, "accion", XrefRules.Acciones)
                ?? throw ToolException.InvalidParams("accion: debe ser recargar, descargar o separar");
            return new Params
            {
                Nombre = nombre,
                Accion = accion,
                Confirmar = ParamReader.OptionalBool(p, "confirmar") ?? false,
            };
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            string? errSimbolo = XrefSupport.ErrorSimbolo(p.Nombre);
            if (errSimbolo != null)
            {
                throw ToolException.InvalidParams(errSimbolo, new Dictionary<string, object?> { ["nombre"] = p.Nombre });
            }

            Document doc = ctx.RequireDocument();
            Database db = doc.Database;

            ObjectId btrId;
            string estado;
            int inserciones;
            using (Transaction ro = db.TransactionManager.StartOpenCloseTransaction())
            {
                btrId = XrefSupport.FindBlockId(db, ro, p.Nombre);
                if (btrId.IsNull)
                {
                    throw new ToolException(
                        ErrorCodes.NotFound,
                        "No hay ninguna referencia externa llamada '" + p.Nombre + "' en el dibujo.",
                        new Dictionary<string, object?> { ["nombre"] = p.Nombre });
                }

                var btr = (BlockTableRecord)ro.GetObject(btrId, OpenMode.ForRead);
                if (!btr.IsFromExternalReference)
                {
                    throw ToolException.InvalidParams("nombre: '" + p.Nombre + "' no es una referencia externa (xref)", new Dictionary<string, object?> { ["nombre"] = p.Nombre });
                }

                if (p.Accion == "separar" && XrefSupport.EsAnidada(db, p.Nombre))
                {
                    throw ToolException.InvalidParams(
                        "accion: la referencia '" + p.Nombre + "' está anidada dentro de otra xref; el puente no separa referencias anidadas",
                        new Dictionary<string, object?> { ["nombre"] = p.Nombre });
                }

                estado = XrefsHandler.MapStatus(btr);
                inserciones = AcadUtil.CountInsertions(btr, ro);
                ro.Commit();
            }

            string? error = XrefRules.ErrorAccion(p.Accion, estado);
            if (error != null)
            {
                throw ToolException.InvalidParams(error, new Dictionary<string, object?> { ["nombre"] = p.Nombre, ["estado"] = estado });
            }

            var warnings = new List<string>();
            switch (p.Accion)
            {
                case "recargar":
                    warnings.Add("Recargar vuelve a leer el archivo de '" + p.Nombre + "' y actualiza sus INSERT.");
                    break;
                case "descargar":
                    warnings.Add("Descargar deja las " + inserciones + " inserción(es) de '" + p.Nombre + "' en el dibujo, pero sin geometría visible hasta recargar.");
                    break;
                default:
                    warnings.Add(inserciones == 0
                        ? "Separar quita la referencia '" + p.Nombre + "' del dibujo (no tiene inserciones)."
                        : "Separar borrará " + inserciones + " inserción(es) de '" + p.Nombre + "' y su definición. Un CTRL+Z las restaura.");
                    break;
            }

            if (!p.Confirmar)
            {
                warnings.Add("Previsualización: no se cambió nada. Repita con confirmar: true para aplicar '" + p.Accion + "'.");
                return Respuesta(ctx.Pid, true, false, p, estado, null, inserciones, 0, warnings);
            }

            object resultado = UndoGroup.Run(doc, d => Apply(d, ctx.Pid, p, btrId, estado, inserciones, warnings));
            return resultado;
        }

        private static object Apply(Document doc, int pid, Params p, ObjectId btrId, string estadoAntes, int inserciones, List<string> warnings)
        {
            Database db = doc.Database;
            var ids = new ObjectIdCollection { btrId };
            try
            {
                switch (p.Accion)
                {
                    case "recargar":
                        db.ReloadXrefs(ids);
                        break;
                    case "descargar":
                        db.UnloadXrefs(ids);
                        break;
                    default:
                        db.DetachXref(btrId);
                        break;
                }
            }
            catch (AcRuntimeException ex)
            {
                throw XrefSupport.OperacionFallida(p.Accion, p.Nombre, ex);
            }

            if (p.Accion == "separar")
            {
                return Respuesta(pid, false, true, p, estadoAntes, null, inserciones, inserciones, warnings);
            }

            string estadoDespues;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead, false);
                estadoDespues = XrefsHandler.MapStatus(btr);
                tr.Commit();
            }

            if (estadoDespues == "no_encontrada")
            {
                warnings.Add("La referencia '" + p.Nombre + "' no se encontró al recargarla: revise su ruta guardada (xrefs).");
            }

            return Respuesta(pid, false, true, p, estadoAntes, estadoDespues, inserciones, 0, warnings);
        }

        private static Dictionary<string, object?> Respuesta(
            int pid, bool preview, bool ejecutada, Params p, string estadoAntes, string? estadoDespues,
            int inserciones, int borradas, List<string> warnings)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = pid,
                ["preview"] = preview,
                ["ejecutada"] = ejecutada,
                ["nombre"] = p.Nombre,
                ["accion"] = p.Accion,
                ["estado_antes"] = estadoAntes,
                ["estado_despues"] = estadoDespues,
                ["n_inserciones"] = inserciones,
                ["inserciones_borradas"] = borradas,
                ["advertencias"] = warnings,
            };
        }
    }
}
