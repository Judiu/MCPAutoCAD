using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Logging;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// adjuntar_xref (fallo 37, plugin 0.10.3). Hilo principal; requiere documento; ESCRITURA con vista previa: sin
    /// <c>confirmar: true</c> no escribe nada. Con él adjunta (o superpone) un .dwg como referencia externa y coloca su INSERT
    /// en el espacio indicado, todo en UN paso de deshacer.
    /// <list type="bullet">
    /// <item>El archivo solo se LEE (AttachXref lo carga; nada del archivo se ejecuta). Se aplica carpetas_export_permitidas de
    /// lps.json, como insertar_export, pero no carpetas_solo_lectura: no se escribe en la carpeta de origen.</item>
    /// <item>Se rechaza adjuntar el propio dibujo activo, y un nombre que ya es un bloque no-xref o una referencia de otro
    /// archivo. Si la referencia ya existe apuntando a ese archivo, solo_insertar: true inserta otro INSERT de ella.</item>
    /// <item>Todas las comprobaciones (ruta, nombre, unidades, capa, espacio) van ANTES de AttachXref: esa operación no se
    /// revierte con el Abort de la transacción (como Database.Insert en insertar_export).</item>
    /// <item>ruta_relativa guarda la ruta relativa al dibujo activo si está guardado; si no, guarda la absoluta con aviso.</item>
    /// </list>
    /// </summary>
    internal sealed class AdjuntarXrefHandler : IToolHandler
    {
        private const string TipoAttach = "attach";
        private const string TipoOverlay = "overlay";

        private static readonly string[] Allowed =
        {
            "ruta", "nombre", "tipo", "ruta_relativa", "posicion", "escala", "rotacion", "capa", "espacio", "aceptar_sin_unidades",
            "solo_insertar", "confirmar",
        };

        public string Name => "adjuntar_xref";

        public string Description =>
            "ESCRIBE en el dibujo activo una referencia externa (xref) a un .dwg de solo lectura, adjuntada o superpuesta (overlay), " +
            "con su INSERT en 'posicion' (escala, rotación y capa). Sin confirmar: true solo previsualiza (nombre resuelto, ruta que " +
            "se guardará, INSUNITS del archivo frente al dibujo). Un paso de deshacer. Nunca adjunta el propio dibujo. El deshacer de la referencia y su INSERT aún no está verificado en vivo.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs;

        /// <summary>Un timeout de escritura no la revierte: la referencia puede quedar adjuntada (§3, regla 7).</summary>
        public string? TimeoutHint(JsonElement parameters)
        {
            string nombre = parameters.TryGetProperty("nombre", out JsonElement n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? ""
                : XrefRules.DefaultName(parameters.TryGetProperty("ruta", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "");
            return "Compruebe con xrefs si la referencia '" + nombre + "' ya se adjuntó antes de repetir; un CTRL+Z la retira.";
        }

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""ruta"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1000, ""description"": ""Ruta absoluta en unidad (no UNC) del .dwg a referenciar, sin '..' ni comodines. Solo se lee. Si lps.json define carpetas_export_permitidas, debe estar dentro de una."" },
    ""nombre"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 200, ""description"": ""Nombre de la referencia. Por defecto, el nombre del archivo sin extensión. Un bloque que no es referencia con ese nombre se rechaza."" },
    ""tipo"": { ""type"": ""string"", ""enum"": [""attach"", ""overlay""], ""default"": ""attach"", ""description"": ""'attach' = adjuntar (sus referencias anidadas se cargan); 'overlay' = superponer (sus anidadas no se cargan)."" },
    ""ruta_relativa"": { ""type"": ""boolean"", ""default"": true, ""description"": ""true = guardar la ruta relativa al dibujo activo (requiere que esté guardado; si no, se guarda absoluta con aviso)."" },
    ""posicion"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3, ""default"": [0, 0], ""description"": ""Punto de inserción [x,y] en el WCS del dibujo activo."" },
    ""escala"": { ""type"": ""number"", ""not"": { ""const"": 0 }, ""default"": 1, ""description"": ""Escala del INSERT, distinta de 0 (negativa = espejo). No convierte unidades: si INSUNITS difiere del dibujo, solo se avisa."" },
    ""rotacion"": { ""type"": ""number"", ""minimum"": -3600, ""maximum"": 3600, ""default"": 0, ""description"": ""Rotación en grados, antihoraria desde +X."" },
    ""capa"": " + DrawWrite.SchemaCapa + @",
    ""espacio"": " + DrawWrite.SchemaEspacio + @",
    ""aceptar_sin_unidades"": " + DrawWrite.SchemaAceptarSinUnidades + @",
    ""solo_insertar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = la referencia ya existe con ese nombre y apunta a este mismo archivo: solo añade otro INSERT de ella. Sin esta opción, ese caso se rechaza."" },
    ""confirmar"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Obligatorio para adjuntar e insertar. Sin él solo previsualiza y no escribe nada."" }
  },
  ""required"": [""ruta""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"" },
    ""preview"": { ""type"": ""boolean"" },
    ""adjuntada"": { ""type"": ""boolean"" },
    ""reutilizada"": { ""type"": ""boolean"" },
    ""nombre"": { ""type"": ""string"" },
    ""ruta"": { ""type"": ""string"" },
    ""ruta_guardada"": { ""type"": ""string"" },
    ""ruta_relativa"": { ""type"": ""boolean"" },
    ""tipo"": { ""type"": ""string"", ""enum"": [""attach"", ""overlay""] },
    ""xref_insunits"": { ""type"": [""integer"", ""null""] },
    ""xref_unidades"": { ""type"": [""string"", ""null""] },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""entidad"": { ""type"": [""object"", ""null""], ""properties"": {
      ""handle"": { ""type"": ""string"" },
      ""bloque"": { ""type"": ""string"" },
      ""posicion"": { ""type"": ""array"" },
      ""escala"": { ""type"": ""array"" },
      ""rotacion"": { ""type"": ""number"" } } },
    ""capa"": { ""type"": ""string"" },
    ""capa_creada"": { ""type"": ""boolean"" },
    ""capa_nueva"": { ""type"": ""boolean"" },
    ""espacio"": { ""type"": ""string"" },
    ""insunits"": { ""type"": ""integer"" },
    ""unidades"": { ""type"": ""string"" },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" },
    ""advertencias"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
  },
  ""required"": [""pid"", ""preview"", ""adjuntada"", ""reutilizada"", ""nombre"", ""ruta"", ""ruta_guardada"", ""ruta_relativa"", ""tipo"", ""xref_insunits"", ""xref_unidades"", ""handles"", ""entidad"", ""capa"", ""capa_creada"", ""capa_nueva"", ""espacio"", ""insunits"", ""unidades"", ""origen_texto"", ""advertencias""]
}");

        /// <summary>Parámetros ya validados sin AutoCAD.</summary>
        private sealed class Params
        {
            public string Ruta { get; set; } = "";
            public string Nombre { get; set; } = "";
            public string Tipo { get; set; } = TipoAttach;
            public bool RutaRelativa { get; set; }
            public Point3 Posicion { get; set; }
            public double Escala { get; set; }
            public double Rotacion { get; set; }
            public string? Capa { get; set; }
            public string? Espacio { get; set; }
            public bool AceptarSinUnidades { get; set; }
            public bool SoloInsertar { get; set; }
            public bool Confirmar { get; set; }
        }

        /// <summary>Lo que la lectura previa deja resuelto (una sola transacción de solo lectura).</summary>
        private sealed class Pre
        {
            public XrefRules.Decision Decision { get; set; }
            public ObjectId ExistenteId { get; set; }
            public string? RutaExistente { get; set; }
            public string? GuardadaExistente { get; set; }
            public LayerPlan? Plan { get; set; }
            public bool CapaExiste { get; set; }
            public string EspacioNombre { get; set; } = "";
            public string TipoExistente { get; set; } = TipoAttach;
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            string ruta = ParamReader.RequiredText(p, "ruta", 1, 1000);
            string? formaRuta = XrefRules.ValidateRuta(ruta);
            if (formaRuta != null)
            {
                throw ToolException.InvalidParams(formaRuta, Extra("ruta", ruta));
            }

            string nombre = ParamReader.OptionalString(p, "nombre", 1, XrefRules.MaxNombre) ?? XrefRules.DefaultName(ruta);
            string? formaNombre = XrefRules.ValidateNombre(nombre);
            if (formaNombre != null)
            {
                throw ToolException.InvalidParams(formaNombre, Extra("nombre", nombre));
            }

            double escala = ParamReader.OptionalNumber(p, "escala", -1e9, 1e9) ?? 1.0;
            if (escala == 0.0)
            {
                throw ToolException.InvalidParams("escala: debe ser un número distinto de 0");
            }

            string? capa = ParamReader.OptionalString(p, "capa", 1, 255);
            DrawLayerFactory.Plan(capa, null);   // R3: nombre de capa inválido → INVALID_PARAMS antes de tocar nada.

            return new Params
            {
                Ruta = ruta,
                Nombre = nombre,
                Tipo = ParamReader.OptionalEnum(p, "tipo", XrefRules.Tipos) ?? TipoAttach,
                RutaRelativa = ParamReader.OptionalBool(p, "ruta_relativa") ?? true,
                Posicion = ParamReader.OptionalPoint(p, "posicion") ?? new Point3(0.0, 0.0, 0.0),
                Escala = escala,
                Rotacion = ParamReader.OptionalNumber(p, "rotacion", -XrefRules.MaxRotacionGrados, XrefRules.MaxRotacionGrados) ?? 0.0,
                Capa = capa,
                Espacio = ParamReader.OptionalString(p, "espacio", 1, 255),
                AceptarSinUnidades = ParamReader.OptionalBool(p, "aceptar_sin_unidades") ?? false,
                SoloInsertar = ParamReader.OptionalBool(p, "solo_insertar") ?? false,
                Confirmar = ParamReader.OptionalBool(p, "confirmar") ?? false,
            };
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            var warnings = new List<string>();
            string? errSimbolo = XrefSupport.ErrorSimbolo(p.Nombre);
            if (errSimbolo != null)
            {
                throw ToolException.InvalidParams(errSimbolo, Extra("nombre", p.Nombre));
            }

            string? hostFile = XrefSupport.HostFile(doc);

            if (XrefRules.SamePath(hostFile, p.Ruta))
            {
                throw ToolException.InvalidParams("ruta: es el propio dibujo activo; un dibujo no se adjunta a sí mismo", Extra("ruta", p.Ruta));
            }

            // Mismo filtro de lectura que insertar_export (carpetas_export_permitidas de lps.json).
            string? fuera = ExportRules.ValidatePath(p.Ruta, InsertarExportHandler.ReadAllowedFolders(warnings));
            if (fuera != null)
            {
                throw ToolException.InvalidParams(fuera, Extra("ruta", p.Ruta));
            }

            if (!File.Exists(p.Ruta))
            {
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "El archivo '" + p.Ruta + "' no existe.",
                    Extra("ruta", p.Ruta));
            }

            int ins = DrawWrite.CheckUnits(db, p.AceptarSinUnidades, warnings);                 // R2
            Pre pre = Read(db, p, hostFile, out List<string> avisosCapa);

            string guardada = pre.Decision == XrefRules.Decision.Reutilizar
                ? pre.GuardadaExistente ?? p.Ruta
                : p.Ruta;
            bool relativa = false;
            if (pre.Decision == XrefRules.Decision.Adjuntar && p.RutaRelativa)
            {
                if (hostFile == null)
                {
                    warnings.Add(XrefRules.HostNoGuardado);
                }
                else
                {
                    string? rel = XrefRules.RelativePath(hostFile, p.Ruta);
                    if (rel == null)
                    {
                        warnings.Add(XrefRules.SinRutaRelativa);
                    }
                    else
                    {
                        guardada = rel;
                        relativa = true;
                    }
                }
            }

            if (pre.Decision == XrefRules.Decision.Reutilizar)
            {
                relativa = !Path.IsPathRooted(guardada);   // se informa tal como está guardada
            }

            int? insXref = XrefSupport.LeerInsunits(p.Ruta);
            string? avisoUnidades = XrefRules.UnidadesDistintas(insXref, ins);
            if (avisoUnidades != null)
            {
                warnings.Add(avisoUnidades);
            }

            var capa = new DrawLayerFactory.Result(ObjectId.Null, pre.Plan!.Nombre, false, pre.Plan.Origen, pre.Plan.Concepto);

            if (!p.Confirmar)
            {
                warnings.AddRange(avisosCapa);
                warnings.Add(pre.Decision == XrefRules.Decision.Reutilizar
                    ? "Previsualización: no se escribió nada. Repita con confirmar: true para insertar otra referencia de '" + p.Nombre + "'."
                    : "Previsualización: no se escribió nada. Repita con confirmar: true para " + (p.Tipo == TipoOverlay ? "superponer" : "adjuntar") + " '" + p.Nombre + "' e insertarla.");
                return Respuesta(ctx.Pid, true, p, pre, guardada, relativa, insXref, capa, !pre.CapaExiste, pre.EspacioNombre, ins, new List<string>(), null, warnings);
            }

            // R5/R3: los avisos de capa solo se previsualizan; Ensure, dentro de la escritura, añade los suyos.
            object resultado = UndoGroup.Run(doc, d => Write(d, ctx.Pid, p, pre, guardada, relativa, ins, insXref, warnings));
            return resultado;
        }

        /// <summary>
        /// Lectura previa sin efectos: nombre ya usado (xref de otro archivo, bloque normal o la misma referencia), capa y espacio.
        /// Los avisos de capa van a <paramref name="avisosCapa"/> (solo para la previsualización).
        /// </summary>
        private static Pre Read(Database db, Params p, string? hostFile, out List<string> avisosCapa)
        {
            avisosCapa = new List<string>();
            var pre = new Pre();
            using (Transaction ro = db.TransactionManager.StartOpenCloseTransaction())
            {
                pre.ExistenteId = XrefSupport.FindBlockId(db, ro, p.Nombre);
                bool existe = !pre.ExistenteId.IsNull;
                bool esReferencia = false;
                if (existe)
                {
                    var btr = (BlockTableRecord)ro.GetObject(pre.ExistenteId, OpenMode.ForRead);
                    esReferencia = btr.IsFromExternalReference;
                    if (esReferencia)
                    {
                        pre.GuardadaExistente = XrefSupport.Safe(() => btr.PathName);
                        pre.RutaExistente = XrefRules.AbsoluteFrom(hostFile, pre.GuardadaExistente);
                        pre.TipoExistente = XrefSupport.Safe(() => btr.IsFromOverlayReference ? TipoOverlay : TipoAttach) ?? TipoAttach;
                        if (p.SoloInsertar && XrefSupport.EsAnidada(db, p.Nombre))
                        {
                            throw ToolException.InvalidParams(
                                "solo_insertar: la referencia '" + p.Nombre + "' está anidada dentro de otra xref; el puente no inserta referencias anidadas",
                                Extra("nombre", p.Nombre));
                        }
                    }
                }

                pre.Decision = XrefRules.DecideName(existe, esReferencia, pre.RutaExistente, p.Ruta, p.SoloInsertar, out string? error);
                if (pre.Decision == XrefRules.Decision.Rechazar)
                {
                    throw ToolException.InvalidParams(error ?? "nombre: no se puede usar", Extra("nombre", p.Nombre));
                }

                LayerPlan plan = DrawLayerFactory.Check(db, ro, p.Capa, null, out bool capaExiste, avisosCapa);   // R3
                pre.Plan = plan;
                pre.CapaExiste = capaExiste;
                pre.EspacioNombre = SpaceResolver.Resolve(db, ro, p.Espacio).Name;                              // R5
                ro.Commit();
            }

            return pre;
        }

        /// <summary>Escritura dentro de UN paso de deshacer (UndoGroup). El AttachXref va antes de la transacción de dibujo.</summary>
        private static object Write(
            Document doc, int pid, Params p, Pre pre, string guardada, bool relativa, int ins, int? insXref, List<string> warnings)
        {
            Database db = doc.Database;
            ObjectId btrId = pre.ExistenteId;
            if (pre.Decision == XrefRules.Decision.Adjuntar)
            {
                btrId = Adjuntar(db, p);
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                if (pre.Decision == XrefRules.Decision.Adjuntar && relativa)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForWrite);
                    btr.PathName = guardada;
                }

                DrawLayerFactory.Result layer = DrawLayerFactory.Ensure(db, tr, p.Capa, null, warnings);   // R3
                SpaceResolver.ResolvedSpace resolved = SpaceResolver.Resolve(db, tr, p.Espacio);            // R5
                var space = (BlockTableRecord)tr.GetObject(resolved.Btr.ObjectId, OpenMode.ForWrite);

                var br = new BlockReference(AcadUtil.ToPoint3d(p.Posicion), btrId);
                DrawWrite.ApplyByLayer(br, db, layer.Id);
                br.ScaleFactors = new Scale3d(p.Escala, p.Escala, p.Escala);
                br.Rotation = Angles.ToRadians(Angles.Normalize360(p.Rotacion));
                string handle = DrawWrite.Append(space, tr, br);
                tr.Commit();

                var entidad = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["handle"] = handle,
                    ["bloque"] = p.Nombre,
                    ["posicion"] = AcadUtil.ToArray(br.Position),
                    ["escala"] = new[] { br.ScaleFactors.X, br.ScaleFactors.Y, br.ScaleFactors.Z },
                    ["rotacion"] = AcadUtil.ToDegrees360(br.Rotation),
                };

                return Respuesta(
                    pid, false, p, pre, guardada, relativa, insXref, layer, layer.Creada, resolved.Name, ins,
                    new List<string> { handle }, entidad, warnings);
            }
        }

        private static ObjectId Adjuntar(Database db, Params p)
        {
            try
            {
                ObjectId id = p.Tipo == TipoOverlay
                    ? db.OverlayXref(p.Ruta, p.Nombre)
                    : db.AttachXref(p.Ruta, p.Nombre);
                if (id.IsNull)
                {
                    throw new ToolException(
                        ErrorCodes.Internal,
                        "No se pudo adjuntar la referencia: " + Path.GetFileName(p.Ruta) + ".",
                        new Dictionary<string, object?> { ["detalle"] = "AttachXref devolvió un identificador nulo", ["archivo"] = p.Ruta });
                }

                return id;
            }
            catch (AcRuntimeException ex)
            {
                throw new ToolException(
                    ErrorCodes.Internal,
                    "No se pudo adjuntar la referencia: " + Path.GetFileName(p.Ruta) + ".",
                    new Dictionary<string, object?> { ["detalle"] = "AttachXref: " + ex.ErrorStatus + ": " + ex.Message, ["archivo"] = p.Ruta });
            }
        }

        private static Dictionary<string, object?> Respuesta(
            int pid, bool preview, Params p, Pre pre, string guardada, bool relativa, int? insXref,
            DrawLayerFactory.Result capa, bool capaNueva, string espacio, int ins, List<string> handles,
            Dictionary<string, object?>? entidad, List<string> warnings)
        {
            Dictionary<string, object?> result = DrawWrite.Response(pid, handles, capa, espacio, ins, warnings);
            bool reutilizada = pre.Decision == XrefRules.Decision.Reutilizar;
            result["preview"] = preview;
            result["adjuntada"] = !preview && !reutilizada;
            result["reutilizada"] = reutilizada;
            result["nombre"] = p.Nombre;
            result["ruta"] = p.Ruta;
            result["ruta_guardada"] = guardada;
            result["ruta_relativa"] = relativa;
            result["tipo"] = reutilizada ? pre.TipoExistente : p.Tipo;
            result["xref_insunits"] = insXref;
            result["xref_unidades"] = insXref.HasValue ? UnitsTable.Name(insXref.Value) : null;
            result["capa_creada"] = !preview && capa.Creada;
            result["capa_nueva"] = capaNueva;
            result["entidad"] = entidad;
            result["origen_texto"] = "contenido_del_plano";
            return result;
        }

        private static Dictionary<string, object?> Extra(string campo, string valor) =>
            new Dictionary<string, object?> { [campo] = valor };
    }
}
