using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Audit;
using MCPAutoCAD.Plugin.Handlers.Clean;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// <c>editar_bloque</c> (fallo 55, plugin 0.10.6). Hilo principal; requiere documento; ESCRITURA solo con
    /// <c>confirmar: true</c> (sin él previsualiza conteos y muestra sin modificar el dibujo). Permite borrar, cambiar
    /// de capa o cambiar propiedades de entidades DENTRO de definiciones de bloque (<see cref="BlockTableRecord"/>),
    /// filtrando por nombres/patrones de bloque, capas y tipos DXF, en un solo paso de deshacer.
    /// </summary>
    internal sealed class EditarBloqueHandler : IToolHandler
    {
        private static readonly string[] Allowed =
        {
            "bloques", "incluir_anonimos", "incluir_anidados", "accion", "capas", "tipos", "handles", "contiene", "ignorar_mayusculas",
            "capa_destino", "libre",
            "color", "color_rgb", "tipo_linea", "escala_tipo_linea", "grosor", "transparencia",
            "confirmar", "preview", "n_esperado",
        };

        private static readonly string[] PropParamKeys =
        {
            "color", "color_rgb", "tipo_linea", "escala_tipo_linea", "grosor", "transparencia",
        };

        private static readonly Autodesk.AutoCAD.Runtime.RXClass ClaseInsert =
            Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(BlockReference));

        public string Name => "editar_bloque";

        public string Description =>
            "EDITA o BORRA entidades DENTRO de definiciones de bloque (no las referencias INSERT del modelo): acepta lista " +
            "de bloques o patrones con comodines (* y ?), opcionalmente incluyendo anónimos y bloques anidados, y aplica " +
            "accion: 'borrar' (filtrando por capas, tipos, handles y/o subcadenas 'contiene' en textos), 'cambiar_capa' " +
            "(capa_destino) o 'cambiar_propiedades' (color, tipo_linea, grosor, transparencia, escala_tipo_linea). Sin " +
            "confirmar: true solo PREVISUALIZA. Con confirmar: true ejecuta todo en un solo paso de deshacer y regenera la vista. Máximo 20000 entidades.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""bloques"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 }, ""description"": ""Nombres exactos o patrones con comodines (*, ?) de definiciones de bloque a editar (sin distinguir mayúsculas; layouts y xrefs se excluyen)."" },
    ""incluir_anonimos"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = incluir bloques anónimos (*U, *D, *X...) al resolver patrones."" },
    ""incluir_anidados"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = incluir también las definiciones de bloque referenciadas dentro de los bloques coincidentes."" },
    ""accion"": { ""type"": ""string"", ""enum"": [""borrar"", ""cambiar_capa"", ""cambiar_propiedades""], ""description"": ""Operación a aplicar sobre las entidades coincidentes dentro de cada definición."" },
    ""capas"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""description"": ""Filtro por capa de la entidad dentro del bloque (admite comodines * y ?)."" },
    ""tipos"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""description"": ""Filtro por tipo DXF (LINE, LWPOLYLINE, DIMENSION, TEXT, MTEXT, INSERT...)."" },
    ""handles"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": " + ParamReader.SchemaHandle + @", ""description"": ""Filtro por handles concretos de entidades dentro de los bloques seleccionados (1–500)."" },
    ""contiene"": { ""anyOf"": [ { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 200 }, { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 200 } } ], ""description"": ""Filtro por contenido de texto (TEXT, MTEXT, ATTDEF, DIMENSION): coincide si el texto plano contiene la subcadena o alguna de la lista."" },
    ""ignorar_mayusculas"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Para 'contiene': true ignora mayúsculas/minúsculas (defecto); false distingue."" },
    ""capa_destino"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""Solo con accion='cambiar_capa': capa destino (concepto LPS, existente, MCP-* o arbitraria con libre: true)."" },
    ""libre"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Con accion='cambiar_capa': true permite crear o usar capa_destino fuera del estándar LPS."" },
    ""color"": " + EntityColor.SchemaColor + @",
    ""color_rgb"": " + EntityColor.SchemaColorRgb + @",
    ""tipo_linea"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 },
    ""escala_tipo_linea"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
    ""grosor"": { ""anyOf"": [ { ""type"": ""number"", ""enum"": [0, 0.05, 0.09, 0.13, 0.15, 0.18, 0.2, 0.25, 0.3, 0.35, 0.4, 0.5, 0.53, 0.6, 0.7, 0.8, 0.9, 1, 1.06, 1.2, 1.4, 1.58, 2, 2.11] }, { ""type"": ""string"", ""enum"": [""porcapa"", ""porbloque"", ""defecto""] } ] },
    ""transparencia"": { ""anyOf"": [ { ""type"": ""number"", ""minimum"": 0, ""maximum"": 90 }, { ""type"": ""string"", ""enum"": [""porcapa"", ""porbloque""] } ] },
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @",
    ""n_esperado"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 20000, ""description"": ""Solo con confirmar: true. Pase el n_afectadas de la previsualización."" }
  },
  ""required"": [""bloques"", ""accion""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""accion"": { ""type"": ""string"" },
    ""n_bloques_escaneados"": { ""type"": ""integer"" },
    ""n_bloques_afectados"": { ""type"": ""integer"" },
    ""n_afectadas"": { ""type"": ""integer"" },
    ""n_aplicadas"": { ""type"": [""integer"", ""null""] },
    ""n_omitidas"": { ""type"": ""integer"" },
    ""omitidas_por_motivo"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""n_usuario"": { ""type"": ""integer"" },
    ""por_bloque"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""por_tipo"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""por_capa"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""muestra"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""capa_destino"": { ""type"": [""string"", ""null""] },
    ""regenerado"": { ""type"": [""boolean"", ""null""] },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""accion"", ""n_bloques_escaneados"", ""n_bloques_afectados"", ""n_afectadas"", ""n_aplicadas"", ""n_omitidas"", ""omitidas_por_motivo"", ""n_usuario"", ""por_bloque"", ""por_tipo"", ""por_capa"", ""muestra"", ""origen_texto""]
}");

        private sealed class Params
        {
            public List<string> Bloques = new List<string>();
            public bool IncluirAnonimos;
            public bool IncluirAnidados;
            public string Accion = "";
            public List<string>? Capas;
            public HashSet<string>? Tipos;
            public HashSet<string>? Handles;
            public List<string>? HandleList;
            public List<string>? Contiene;
            public bool IgnorarMayusculas = true;
            public string? CapaDestino;
            public bool Libre;
            public CambiarPropiedadesHandler.Props? Props;
            public CleanWrite.Mode Mode;
            public int? NEsperado;
        }

        private sealed class Candidate
        {
            public Candidate(ObjectId id, string blockName, string handle, string tipo, string capa, string? texto)
            {
                Id = id;
                BlockName = blockName;
                Handle = handle;
                Tipo = tipo;
                Capa = capa;
                Texto = texto;
            }

            public ObjectId Id { get; }
            public string BlockName { get; }
            public string Handle { get; }
            public string Tipo { get; }
            public string Capa { get; }
            public string? Texto { get; }
        }

        private sealed class Plan
        {
            public int BloquesEscaneados;
            public List<Candidate> Targets = new List<Candidate>();
            public Dictionary<string, int> Omitidas = new Dictionary<string, int>(StringComparer.Ordinal);
            public string? CapaDestinoFinal;
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);

            List<string>? rawBloques = ParamReader.OptionalStringArray(p, "bloques", 1, BlockEditRules.MaxBlocks, 1);
            if (rawBloques == null)
            {
                throw ToolException.InvalidParams("bloques: obligatorio");
            }

            List<string> bloques;
            try
            {
                bloques = BlockEditRules.NormalizeBlockPatterns(rawBloques);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }

            string accion = ParamReader.OptionalEnum(p, "accion", BlockEditRules.ValidActions)
                ?? throw ToolException.InvalidParams("accion: obligatorio (borrar, cambiar_capa o cambiar_propiedades)");

            bool incluirAnonimos = ParamReader.OptionalBool(p, "incluir_anonimos") ?? false;
            bool incluirAnidados = ParamReader.OptionalBool(p, "incluir_anidados") ?? false;

            List<string>? capas = ParamReader.OptionalStringArray(p, "capas", 1, 100, 1);
            List<string>? tiposRaw = ParamReader.OptionalStringArray(p, "tipos", 1, 50, 1);
            HashSet<string>? tipos = tiposRaw == null
                ? null
                : new HashSet<string>(tiposRaw.Select(EntityFilter.NormalizeType), StringComparer.Ordinal);
            List<string>? handleList = ParamReader.Has(p, "handles")
                ? ParamReader.RequiredHandles(p, "handles", 1, BlockEditRules.MaxHandles)
                : null;
            HashSet<string>? handles = handleList == null
                ? null
                : new HashSet<string>(handleList, StringComparer.OrdinalIgnoreCase);
            List<string>? contiene = ParseContiene(p);
            bool ignorarMayusculas = ParamReader.OptionalBool(p, "ignorar_mayusculas") ?? true;

            string? capaDestino = ParamReader.OptionalString(p, "capa_destino", 1, 255);
            bool libre = ParamReader.OptionalBool(p, "libre") ?? false;
            bool hasAnyPropParam = PropParamKeys.Any(k => ParamReader.Has(p, k));

            if (accion == BlockEditRules.ActionDelete)
            {
                string? errDel = BlockEditRules.ValidateDeleteFilter(capas, tipos != null, handles != null, contiene != null);
                if (errDel != null)
                {
                    throw ToolException.InvalidParams(errDel);
                }

                if (capaDestino != null || libre || hasAnyPropParam)
                {
                    throw ToolException.InvalidParams("accion='borrar': no admite capa_destino ni propiedades de apariencia");
                }
            }
            else if (accion == BlockEditRules.ActionLayer)
            {
                if (capaDestino == null)
                {
                    throw ToolException.InvalidParams("capa_destino: obligatorio con accion='cambiar_capa'");
                }

                if (!libre)
                {
                    DrawLayerFactory.Plan(capaDestino, null);
                }
                else if (string.IsNullOrWhiteSpace(capaDestino))
                {
                    throw ToolException.InvalidParams(LayerResolver.InvalidName);
                }

                if (hasAnyPropParam)
                {
                    throw ToolException.InvalidParams("accion='cambiar_capa': no admite propiedades de apariencia");
                }
            }
            else
            {
                if (capaDestino != null || libre)
                {
                    throw ToolException.InvalidParams("accion='cambiar_propiedades': no admite capa_destino");
                }

                // CambiarPropiedadesHandler.ParseProps exige al menos una propiedad y las valida igual que cambiar_propiedades.
            }

            CambiarPropiedadesHandler.Props? props = accion == BlockEditRules.ActionProps
                ? CambiarPropiedadesHandler.ParseProps(p)
                : null;

            CleanWrite.Common cw = CleanWrite.ReadCommon(p);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(cw.Confirmar, cw.Preview);

            int? nEsperado = ParamReader.OptionalInt(p, "n_esperado", 0, BlockEditRules.MaxEntities);
            if (nEsperado.HasValue && mode == CleanWrite.Mode.Preview)
            {
                throw ToolException.InvalidParams("n_esperado: solo aplica con confirmar: true");
            }

            return new Params
            {
                Bloques = bloques,
                IncluirAnonimos = incluirAnonimos,
                IncluirAnidados = incluirAnidados,
                Accion = accion,
                Capas = capas,
                Tipos = tipos,
                Handles = handles,
                HandleList = handleList,
                Contiene = contiene,
                IgnorarMayusculas = ignorarMayusculas,
                CapaDestino = capaDestino,
                Libre = libre,
                Props = props,
                Mode = mode,
                NEsperado = nEsperado,
            };
        }

        private static List<string>? ParseContiene(JsonElement p)
        {
            if (!p.TryGetProperty("contiene", out JsonElement el))
            {
                return null;
            }

            if (el.ValueKind == JsonValueKind.String)
            {
                string v = el.GetString() ?? "";
                if (v.Length < 1 || v.Length > BlockEditRules.MaxContainsLength)
                {
                    throw ToolException.InvalidParams(
                        "contiene: debe tener entre 1 y " + BlockEditRules.MaxContainsLength + " caracteres");
                }

                return new List<string> { v };
            }

            if (el.ValueKind == JsonValueKind.Array)
            {
                int count = el.GetArrayLength();
                if (count < 1 || count > BlockEditRules.MaxContains)
                {
                    throw ToolException.InvalidParams(
                        "contiene: debe ser un texto o un array de 1 a " + BlockEditRules.MaxContains + " textos");
                }

                var list = new List<string>(count);
                int idx = 0;
                foreach (JsonElement item in el.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        throw ToolException.InvalidParams("contiene[" + idx + "]: debe ser un texto");
                    }

                    string v = item.GetString() ?? "";
                    if (v.Length < 1 || v.Length > BlockEditRules.MaxContainsLength)
                    {
                        throw ToolException.InvalidParams(
                            "contiene[" + idx + "]: debe tener entre 1 y " + BlockEditRules.MaxContainsLength + " caracteres");
                    }

                    list.Add(v);
                    idx++;
                }

                return list;
            }

            throw ToolException.InvalidParams("contiene: debe ser un texto o un array de 1 a 50 textos");
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = CleanWrite.ReadUnits(db);

            Plan plan;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                plan = BuildPlan(db, tr, p, warnings);
                tr.Commit();
            }

            if (p.Mode == CleanWrite.Mode.Preview)
            {
                if (p.Props?.Tipo != null && !CambiarPropiedadesHandler.IsKeyword(p.Props.Tipo))
                {
                    string? real = CambiarPropiedadesHandler.LoadedName(db, p.Props.Tipo);
                    if (real == null)
                    {
                        warnings.Add("tipo_linea: '" + p.Props.Tipo + "' no está cargado: al escribir se intentará cargar de "
                            + EntityPropertyRules.LinetypeFileFor(AcadUtil.Measurement(db)) + ".");
                    }
                }

                return BuildResponse(ctx.Pid, CleanWrite.Mode.Preview, ins, warnings, p, plan, nAplicadas: null, regenerado: null);
            }

            string? expectedErr = BlockEditRules.CheckExpected(p.NEsperado, plan.Targets.Count, "aplicó");
            if (expectedErr != null)
            {
                throw ToolException.InvalidParams(
                    expectedErr,
                    new Dictionary<string, object?> { ["n_esperado"] = p.NEsperado, ["n_afectadas"] = plan.Targets.Count });
            }

            if (plan.Targets.Count == 0)
            {
                warnings.Add("Ninguna entidad dentro de los bloques cumple el filtro tras las exclusiones: no se escribió nada.");
                Dictionary<string, object?> empty = BuildResponse(ctx.Pid, CleanWrite.Mode.Write, ins, warnings, p, plan, nAplicadas: 0, regenerado: null);
                empty["escrito"] = false;
                empty["preview"] = false;
                return empty;
            }

            int pid = ctx.Pid;
            return UndoGroup.Run(doc, d => Write(d, p, pid, ins, warnings));
        }

        private static Plan BuildPlan(Database db, Transaction tr, Params p, List<string> warnings)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var nonLayoutDefs = new List<(ObjectId Id, string Name)>();
            foreach (ObjectId btrId in bt)
            {
                if (btrId.IsNull || btrId.IsErased)
                {
                    continue;
                }

                if (!(tr.GetObject(btrId, OpenMode.ForRead, false) is BlockTableRecord btr))
                {
                    continue;
                }

                if (btr.IsLayout || btr.IsFromExternalReference || btr.IsFromOverlayReference || btr.IsDependent)
                {
                    continue;
                }

                bool isAnon = btr.IsAnonymous || btr.Name.StartsWith("*", StringComparison.Ordinal);
                if (isAnon && !p.IncluirAnonimos)
                {
                    continue;
                }

                nonLayoutDefs.Add((btrId, btr.Name));
            }

            var matchedQueue = new Queue<ObjectId>();
            var visitedBlocks = new HashSet<ObjectId>();
            var literalNotFound = new List<string>();

            foreach (string pat in p.Bloques)
            {
                bool hit = false;
                foreach ((ObjectId btrId, string bName) in nonLayoutDefs)
                {
                    if (LayerPattern.Matches(bName, pat))
                    {
                        hit = true;
                        if (visitedBlocks.Add(btrId))
                        {
                            matchedQueue.Enqueue(btrId);
                        }
                    }
                }

                if (!hit && !BlockEditRules.HasWildcards(pat))
                {
                    literalNotFound.Add(pat);
                }
            }

            if (literalNotFound.Count > 0)
            {
                warnings.Add("editar_bloque: bloque(s) no encontrados o excluidos: "
                    + string.Join(", ", literalNotFound.Select(n => "'" + n + "'")) + ".");
            }

            var blockOrder = new List<ObjectId>();
            while (matchedQueue.Count > 0)
            {
                ObjectId curId = matchedQueue.Dequeue();
                blockOrder.Add(curId);
                if (!p.IncluirAnidados)
                {
                    continue;
                }

                if (!(tr.GetObject(curId, OpenMode.ForRead, false) is BlockTableRecord curBtr))
                {
                    continue;
                }

                foreach (ObjectId entId in curBtr)
                {
                    if (entId.IsNull || entId.IsErased)
                    {
                        continue;
                    }

                    Autodesk.AutoCAD.Runtime.RXClass? cls = entId.ObjectClass;
                    if (cls == null || !cls.IsDerivedFrom(ClaseInsert))
                    {
                        continue;
                    }

                    if (tr.GetObject(entId, OpenMode.ForRead, false) is BlockReference br)
                    {
                        ObjectId childDef = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                        if (!childDef.IsNull && !childDef.IsErased
                            && tr.GetObject(childDef, OpenMode.ForRead, false) is BlockTableRecord childBtr
                            && !childBtr.IsLayout && !childBtr.IsFromExternalReference
                            && !childBtr.IsFromOverlayReference && !childBtr.IsDependent
                            && visitedBlocks.Add(childDef))
                        {
                            matchedQueue.Enqueue(childDef);
                        }
                    }
                }
            }

            if (blockOrder.Count == 0)
            {
                throw ToolException.InvalidParams(
                    "bloques: ninguna definición de bloque no-xref coincide con " + string.Join(", ", p.Bloques)
                    + (p.IncluirAnonimos ? "" : " (si busca bloques anónimos *U/*D, pase incluir_anonimos: true)"));
            }

            string? capaDestFinal = null;
            if (p.Accion == BlockEditRules.ActionLayer)
            {
                capaDestFinal = CheckTargetLayer(db, tr, p.CapaDestino!, p.Libre, warnings);
            }

            bool explicitAttdef = p.Tipos != null && p.Tipos.Contains("ATTDEF");
            var layerCache = new Dictionary<ObjectId, (string Name, bool Locked, bool Xref)>();
            var layerPatternCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var foundHandles = p.Handles != null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
            var targets = new List<Candidate>();
            var omitidas = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (ObjectId btrId in blockOrder)
            {
                if (!(tr.GetObject(btrId, OpenMode.ForRead, false) is BlockTableRecord btr))
                {
                    continue;
                }

                string blockName = btr.Name;
                foreach (ObjectId entId in btr)
                {
                    if (entId.IsNull || entId.IsErased)
                    {
                        continue;
                    }

                    string handle = AcadUtil.HandleToString(entId.Handle);
                    if (p.Handles != null)
                    {
                        if (!p.Handles.Contains(handle))
                        {
                            continue;
                        }

                        foundHandles!.Add(handle);
                    }

                    if (!(tr.GetObject(entId, OpenMode.ForRead, false) is Entity ent))
                    {
                        continue;
                    }

                    string tipo = AcadUtil.DxfName(ent);
                    if (p.Tipos != null && !p.Tipos.Contains(tipo))
                    {
                        continue;
                    }

                    (string layerName, bool locked, bool xref) = ReadLayer(tr, ent.LayerId, layerCache);
                    if (p.Capas != null && !MatchesLayer(layerName, p.Capas, layerPatternCache))
                    {
                        continue;
                    }

                    string? plainText = ExtractEntityText(ent);
                    if (p.Contiene != null && !BlockEditRules.MatchesAnySubstring(plainText, p.Contiene, p.IgnorarMayusculas))
                    {
                        continue;
                    }

                    if (p.Accion == BlockEditRules.ActionDelete && ent is AttributeDefinition && !explicitAttdef)
                    {
                        CleanWrite.Increment(omitidas, "attdef");
                        continue;
                    }

                    if (xref)
                    {
                        CleanWrite.Increment(omitidas, "capa_xref");
                        continue;
                    }

                    if (locked)
                    {
                        CleanWrite.Increment(omitidas, "capa_bloqueada");
                        continue;
                    }

                    targets.Add(new Candidate(entId, blockName, handle, tipo, layerName, TrimPreviewText(plainText)));
                }
            }

            if (p.HandleList != null && foundHandles != null)
            {
                var missing = p.HandleList.Where(h => !foundHandles.Contains(h)).ToList();
                if (missing.Count > 0)
                {
                    warnings.Add(
                        "editar_bloque: handle(s) no encontrados dentro de los bloques escaneados: "
                        + string.Join(", ", missing.Take(20)) + (missing.Count > 20 ? "…" : "") + ".");
                }
            }

            string? capErr = BlockEditRules.CheckCap(targets.Count);
            if (capErr != null)
            {
                throw ToolException.InvalidParams(
                    capErr,
                    new Dictionary<string, object?> { ["n_afectadas"] = targets.Count, ["maximo"] = BlockEditRules.MaxEntities });
            }

            return new Plan
            {
                BloquesEscaneados = blockOrder.Count,
                Targets = targets,
                Omitidas = omitidas,
                CapaDestinoFinal = capaDestFinal,
            };
        }

        private static string CheckTargetLayer(Database db, Transaction tr, string capaDestino, bool libre, List<string> warnings)
        {
            if (!libre)
            {
                return DrawLayerFactory.Check(db, tr, capaDestino, null, out _, warnings).Nombre;
            }

            string literal = capaDestino.Trim();
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            ObjectId id = McpLayerFactory.FindLayer(lt, literal);
            if (!id.IsNull)
            {
                var existing = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                DrawLayerFactory.RequireWritable(existing, warnings);
                return existing.Name;
            }

            try
            {
                SymbolUtilityServices.ValidateSymbolName(literal, false);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                throw ToolException.InvalidParams(LayerResolver.InvalidName, new Dictionary<string, object?> { ["capa"] = literal });
            }

            return literal;
        }

        private static ObjectId EnsureTargetLayer(Database db, Transaction tr, string capaDestino, bool libre, List<string> warnings, out string finalName)
        {
            if (!libre)
            {
                DrawLayerFactory.Result res = DrawLayerFactory.Ensure(db, tr, capaDestino, null, warnings);
                finalName = res.Nombre;
                return res.Id;
            }

            string literal = capaDestino.Trim();
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            ObjectId id = McpLayerFactory.FindLayer(lt, literal);
            if (!id.IsNull)
            {
                var existing = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                DrawLayerFactory.RequireWritable(existing, warnings);
                finalName = existing.Name;
                return id;
            }

            LayerSpec? spec = LayerResolver.SpecFor(literal, AuditSupport.LayerRules);
            var ltr = new LayerTableRecord
            {
                Name = literal,
                Color = Color.FromColorIndex(ColorMethod.ByAci, (short)Math.Clamp(spec?.Color ?? 7, 1, 255)),
                LinetypeObjectId = db.ContinuousLinetype,
                LineWeight = spec != null && LayerProps.IsValidLineWeight(spec.Grosor) ? (LineWeight)spec.Grosor : LineWeight.ByLineWeightDefault,
                IsPlottable = spec?.Plot ?? true,
            };
            lt.UpgradeOpen();
            id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            finalName = ltr.Name;
            return id;
        }

        private static object Write(Document doc, Params p, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;

            ObjectId linetypeId = ObjectId.Null;
            if (p.Props?.Tipo != null && !CambiarPropiedadesHandler.IsKeyword(p.Props.Tipo))
            {
                linetypeId = CambiarPropiedadesHandler.ResolveLinetype(db, p.Props.Tipo, warnings, load: true);
            }

            Plan plan;
            int aplicadas = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                plan = BuildPlan(db, tr, p, new List<string>());
                ObjectId targetLayerId = ObjectId.Null;
                if (p.Accion == BlockEditRules.ActionLayer)
                {
                    targetLayerId = EnsureTargetLayer(db, tr, p.CapaDestino!, p.Libre, warnings, out string finalLayer);
                    plan.CapaDestinoFinal = finalLayer;
                }

                var appliedTargets = new List<Candidate>(plan.Targets.Count);
                foreach (Candidate item in plan.Targets)
                {
                    if (item.Id.IsNull || item.Id.IsErased
                        || !(tr.GetObject(item.Id, OpenMode.ForRead, false) is Entity ent)
                        || BorrarPorFiltroHandler.CapaProtegida(tr, ent.LayerId))
                    {
                        CleanWrite.Increment(plan.Omitidas, "cambiada");
                        continue;
                    }

                    ent.UpgradeOpen();
                    if (p.Accion == BlockEditRules.ActionDelete)
                    {
                        ent.Erase();
                    }
                    else if (p.Accion == BlockEditRules.ActionLayer)
                    {
                        ent.LayerId = targetLayerId;
                    }
                    else
                    {
                        CambiarPropiedadesHandler.Apply(ent, p.Props!, db, linetypeId);
                    }

                    appliedTargets.Add(item);
                    aplicadas++;
                }

                plan.Targets = appliedTargets;
                tr.Commit();
            }

            bool regenerado = false;
            if (aplicadas > 0)
            {
                try
                {
                    doc.Editor.Regen();
                    regenerado = true;
                }
                catch (Exception ex)
                {
                    warnings.Add("editar_bloque: Editor.Regen falló (" + ex.Message + "); ejecute REGEN si las referencias no se actualizan en pantalla.");
                    PluginLog.Warn("editar_bloque: Editor.Regen falló (" + ex.GetType().Name + ": " + ex.Message + ").");
                }
            }

            return BuildResponse(ctxPid: pid, mode: CleanWrite.Mode.Write, ins: ins, warnings: warnings, p: p, plan: plan, nAplicadas: aplicadas, regenerado: regenerado);
        }

        private static (string Name, bool Locked, bool Xref) ReadLayer(
            Transaction tr, ObjectId layerId, Dictionary<ObjectId, (string, bool, bool)> cache)
        {
            if (cache.TryGetValue(layerId, out (string, bool, bool) cached))
            {
                return cached;
            }

            var ltr = tr.GetObject(layerId, OpenMode.ForRead, false) as LayerTableRecord;
            (string, bool, bool) info = ltr == null
                ? (string.Empty, true, false)
                : (ltr.Name, ltr.IsLocked, ltr.IsDependent || ltr.Name.IndexOf('|') >= 0);
            cache[layerId] = info;
            return info;
        }

        private static bool MatchesLayer(string layerName, List<string> patterns, Dictionary<string, bool> cache)
        {
            if (!cache.TryGetValue(layerName, out bool ok))
            {
                ok = LayerPattern.MatchesAny(layerName, patterns);
                cache[layerName] = ok;
            }

            return ok;
        }

        private static Dictionary<string, object?> BuildResponse(
            int ctxPid, CleanWrite.Mode mode, int ins, List<string> warnings, Params p, Plan plan, int? nAplicadas, bool? regenerado)
        {
            var porBloque = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var porTipo = new Dictionary<string, int>(StringComparer.Ordinal);
            var porCapa = new Dictionary<string, int>(StringComparer.Ordinal);
            var muestra = new List<object>();
            int nUsuario = 0;

            foreach (Candidate c in plan.Targets)
            {
                CleanWrite.Increment(porBloque, c.BlockName);
                CleanWrite.Increment(porTipo, c.Tipo);
                CleanWrite.Increment(porCapa, c.Capa);
                if (!EntityProtection.IsMcpLayer(c.Capa))
                {
                    nUsuario++;
                }

                if (muestra.Count < BlockEditRules.MaxSample)
                {
                    var sample = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["bloque"] = c.BlockName,
                        ["handle"] = c.Handle,
                        ["tipo"] = c.Tipo,
                        ["capa"] = c.Capa,
                    };
                    if (c.Texto != null)
                    {
                        sample["texto"] = c.Texto;
                    }

                    muestra.Add(sample);
                }
            }

            var porBloqueTop = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in BlockEditRules.TopBlocks(porBloque))
            {
                porBloqueTop[kv.Key] = kv.Value;
            }

            var omitidasOut = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in plan.Omitidas.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                omitidasOut[kv.Key] = kv.Value;
            }

            Dictionary<string, object?> r = CleanWrite.Response(ctxPid, mode, ins, warnings);
            r["accion"] = p.Accion;
            r["n_bloques_escaneados"] = plan.BloquesEscaneados;
            r["n_bloques_afectados"] = porBloque.Count;
            r["n_afectadas"] = plan.Targets.Count;
            r["n_aplicadas"] = nAplicadas;
            r["n_omitidas"] = plan.Omitidas.Values.Sum();
            r["omitidas_por_motivo"] = omitidasOut;
            r["n_usuario"] = nUsuario;
            r["por_bloque"] = porBloqueTop;
            r["por_tipo"] = CleanWrite.SortedCounts(porTipo);
            r["por_capa"] = CleanWrite.SortedCounts(porCapa);
            r["muestra"] = muestra;
            if (plan.CapaDestinoFinal != null)
            {
                r["capa_destino"] = plan.CapaDestinoFinal;
            }

            if (regenerado.HasValue)
            {
                r["regenerado"] = regenerado.Value;
            }

            r["origen_texto"] = "contenido_del_plano";
            return r;
        }

        private static string? ExtractEntityText(Entity ent)
        {
            try
            {
                return ent switch
                {
                    AttributeDefinition ad => ad.TextString,
                    DBText dt => dt.TextString,
                    MText mt => string.IsNullOrEmpty(mt.Text) ? mt.Contents : mt.Text,
                    Dimension dim => dim.DimensionText,
                    _ => null,
                };
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return null;
            }
        }

        private static string? TrimPreviewText(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string trimmed = raw.Replace("\r", " ").Replace("\n", " ").Trim();
            return trimmed.Length <= 120 ? trimmed : trimmed.Substring(0, 120) + "…";
        }
    }
}
