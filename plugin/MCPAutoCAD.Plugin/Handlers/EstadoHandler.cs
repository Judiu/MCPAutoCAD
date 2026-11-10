using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.Plugin.Handlers.Documento;
using MCPAutoCAD.Plugin.Logging;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>estado (protocol.md §5.2). Hilo principal; no requiere documento (documento: null).</summary>
    internal sealed class EstadoHandler : IToolHandler, IDegradableDuringCommand
    {
        private static readonly string[] Allowed = Array.Empty<string>();

        public string Name => "estado";

        public string Description =>
            "Devuelve el estado de AutoCAD: documento activo (nombre, ruta, si está modificado), unidades del dibujo " +
            "(INSUNITS/MEASUREMENT) con advertencias, UCS actual y si hay un comando en curso. Úsala antes de dibujar " +
            "para conocer las unidades y el sistema de coordenadas. Incluye el estándar LPS (estandar_lps) y advierte si " +
            "las unidades del dibujo no coinciden con él. Con un comando del usuario en curso (o dentro de otra operación) responde en " +
            "modo degradado (degradado: true): documento, comando_activo y documentos, sin unidades ni UCS.";

        public bool RequiresDocument => false;

        public bool IsWrite => false;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(
            @"{""type"":""object"",""properties"":{},""additionalProperties"":false}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""version_autocad"": { ""type"": ""string"" },
    ""plugin_version"": { ""type"": ""string"" },
    ""documento"": {
      ""type"": [""object"", ""null""],
      ""properties"": {
        ""nombre"": { ""type"": ""string"" },
        ""ruta"": { ""type"": [""string"", ""null""] },
        ""sin_guardar"": { ""type"": ""boolean"" },
        ""modificado"": { ""type"": [""boolean"", ""null""], ""description"": ""null en modo degradado si DBMOD no se puede leer."" },
        ""solo_lectura"": { ""type"": ""boolean"" }
      }
    },
    ""insunits"": { ""type"": [""integer"", ""null""] },
    ""unidades"": { ""type"": [""string"", ""null""] },
    ""measurement"": { ""type"": [""integer"", ""null""], ""description"": ""0 = imperial, 1 = métrico"" },
    ""ucs"": {
      ""type"": [""object"", ""null""],
      ""properties"": {
        ""origen"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
        ""eje_x"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
        ""eje_y"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
        ""es_wcs"": { ""type"": ""boolean"" },
        ""nombre"": { ""type"": ""string"" }
      }
    },
    ""comando_activo"": { ""type"": [""string"", ""null""] },
    ""ocupado"": { ""type"": ""boolean"" },
    ""documentos_abiertos"": { ""type"": ""integer"" },
    ""documentos"": {
      ""type"": ""array"",
      ""description"": ""Documentos abiertos en esta instancia (desde 0.8.1): nombre, ruta (null si nunca se guardó) y si es el activo."",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""nombre"": { ""type"": ""string"" },
          ""ruta"": { ""type"": [""string"", ""null""] },
          ""activo"": { ""type"": ""boolean"" }
        }
      }
    },
    ""degradado"": { ""type"": ""boolean"", ""description"": ""true si hay un comando activo: se responde solo con datos que no leen la base de datos del dibujo (sin unidades, UCS ni previsualizaciones)."" },
    ""estandar_lps"": {
      ""type"": ""object"",
      ""properties"": {
        ""disponible"": { ""type"": ""boolean"" },
        ""insunits"": { ""type"": [""integer"", ""null""] },
        ""unidades"": { ""type"": [""string"", ""null""] }
      },
      ""required"": [""disponible"", ""insunits"", ""unidades""]
    },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""version_autocad"", ""plugin_version"", ""documento"", ""insunits"", ""unidades"", ""measurement"", ""ucs"", ""comando_activo"", ""ocupado"", ""documentos_abiertos"", ""estandar_lps"", ""advertencias""]
}");

        public void ValidateParams(JsonElement parameters) => ParamReader.RequireObject(parameters, Allowed);

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            DocumentCollection docs = AcApp.DocumentManager;
            Document? doc = ctx.Document;
            string? command = ctx.Busy.CurrentCommand;

            object? documento = null;
            int? insunits = null;
            string? unidades = null;
            int? measurement = null;
            object? ucs = null;
            var advertencias = new List<string>();

            // Con comando activo (CMDACTIVE ≠ 0 o eventos de BusyState) no se toca la base de datos: el dibujo puede estar
            // en mitad de un comando. Se devuelve lo que se lee sin bloquear, con degradado: true (el despachador deja pasar
            // estado por IDegradableDuringCommand; ninguna otra herramienta de lectura se libera).
            // Anidado (ctx.Anidado): estado entró mientras otro trabajo bombeaba mensajes (p. ej. DocumentCollection.Open): el
            // documento puede estar a medio abrir, así que también se degrada aunque no haya comando.
            bool comandoActivo = ctx.CmdActive != 0 || ctx.Busy.CommandActive;
            bool degradado = comandoActivo || ctx.Anidado;
            if (doc != null && degradado)
            {
                documento = DocumentoDegradado(doc);
                advertencias.Add(
                    comandoActivo
                        ? "Estado en modo degradado: hay un comando activo" + (command == null ? "" : " (" + command + ")")
                          + ". No se leen unidades, UCS, capas ni previsualizaciones; reintente cuando termine el comando para verlos."
                        : "Estado en modo degradado: petición anidada dentro de otra operación en curso (p. ej. abrir un dibujo); "
                          + "el documento puede estar a medio abrir. No se leen unidades, UCS, capas ni previsualizaciones; reintente cuando termine.");
            }
            else if (doc != null)
            {
                Database db = doc.Database;
                bool untitled = AcadUtil.GetIntSysVar("DWGTITLED") == 0;
                // Para un dibujo sin guardar, Database.Filename apunta a la plantilla: se usa Document.Name.
                string docName = doc.Name ?? "";
                documento = new
                {
                    nombre = Path.GetFileName(docName),
                    ruta = untitled ? null : DocumentoSupport.RutaDe(doc),
                    sin_guardar = untitled,
                    modificado = AcadUtil.GetIntSysVar("DBMOD") != 0,
                    solo_lectura = doc.IsReadOnly,
                };

                int ins = AcadUtil.InsUnits(db);
                int meas = AcadUtil.Measurement(db);
                insunits = ins;
                unidades = UnitsTable.Name(ins);
                measurement = meas;
                // §2.1 + §5.2 + advertencia LPS de §2.2 (al final).
                advertencias.AddRange(AcadUtil.UnitWarnings(db));

                // §5E.5 (0.5): huérfanas de MCP-PREVIEW. Lectura pura (StartOpenCloseTransaction): ningún paso de deshacer.
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    string? orphans = Gen.PreviewStore.OrphanWarning(doc, db, tr);
                    if (orphans != null)
                    {
                        advertencias.Add(orphans);
                    }

                    tr.Commit();
                }

                Matrix3d m = doc.Editor.CurrentUserCoordinateSystem;
                CoordinateSystem3d cs = m.CoordinateSystem3d;
                ucs = new
                {
                    origen = AcadUtil.ToArray(cs.Origin),
                    eje_x = AcadUtil.ToArray(cs.Xaxis),
                    eje_y = AcadUtil.ToArray(cs.Yaxis),
                    es_wcs = AcadUtil.IsIdentity(m, 1e-9),
                    nombre = AcadUtil.GetStringSysVar("UCSNAME"),
                };
            }

            return new
            {
                pid = ctx.Pid,
                version_autocad = AcApp.Version.ToString(),
                plugin_version = PluginInfo.PluginVersion,
                documento,
                insunits,
                unidades,
                measurement,
                ucs,
                comando_activo = command,
                ocupado = command != null || ctx.CmdActive != 0,
                documentos_abiertos = docs.Count,
                documentos = ListDocuments(docs, doc),
                degradado,
                estandar_lps = new
                {
                    disponible = LpsStandard.Disponible,
                    insunits = LpsStandard.Insunits,
                    unidades = LpsStandard.Unidades,
                },
                advertencias,
            };
        }

        /// <summary>
        /// Documento en modo degradado: nombre, ruta, sin_guardar, modificado (DBMOD, null si no se puede leer) y solo lectura.
        /// Solo propiedades de Document y sysvars simples; nada de Database ni de Editor.
        /// </summary>
        private static object DocumentoDegradado(Document doc)
        {
            int? untitled = SafeIntSysVar("DWGTITLED");
            int? dbmod = SafeIntSysVar("DBMOD");
            string docName = doc.Name ?? "";
            bool sinGuardar = untitled == 0;
            return new
            {
                nombre = Path.GetFileName(docName),
                ruta = sinGuardar ? null : DocumentoSupport.RutaDe(doc),
                sin_guardar = sinGuardar,
                modificado = dbmod == null ? (bool?)null : dbmod.Value != 0,
                solo_lectura = doc.IsReadOnly,
            };
        }

        /// <summary>Sysvar entera, o null si AutoCAD no la devuelve (p. ej. en mitad de un comando).</summary>
        private static int? SafeIntSysVar(string name)
        {
            try
            {
                return AcadUtil.GetIntSysVar(name);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("estado (degradado): no se pudo leer " + name + " (" + ex.GetType().Name + ").");
                return null;
            }
        }

        /// <summary>Documentos abiertos (0.8.1, B4): para que el servidor elija la instancia que tiene abierto un archivo.</summary>
        private static List<object> ListDocuments(DocumentCollection docs, Document? active)
        {
            var list = new List<object>();
            try
            {
                foreach (Document d in docs)
                {
                    string name = d.Name ?? "";
                    list.Add(new
                    {
                        nombre = Path.GetFileName(name),
                        ruta = DocumentoSupport.RutaDe(d),
                        activo = ReferenceEquals(d, active),
                    });
                }
            }
            catch (Exception ex)
            {
                // Un documento a medio abrir (estado anidado durante abrir_documento) no debe tumbar estado: lista parcial.
                Logging.PluginLog.Warn("estado: lista de documentos incompleta (" + ex.GetType().Name + ": " + ex.Message + ")");
            }

            return list;
        }
    }
}
