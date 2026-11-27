using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Clean;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// <c>explotar</c> (fallo 55, plugin 0.10.6). Hilo principal; requiere documento; ESCRITURA solo con
    /// <c>confirmar: true</c> (sin él previsualiza las inserciones, estimación de entidades resultantes y avisos).
    /// Explota inserciones (<c>INSERT</c>) por <c>handles</c> o por <c>bloques</c> en un espacio, opcionalmente de forma
    /// recursiva (<c>anidados: true</c>), en un solo paso de deshacer.
    /// </summary>
    internal sealed class ExplotarHandler : IToolHandler
    {
        private static readonly string[] Allowed =
        {
            "handles", "bloques", "espacio", "anidados", "profundidad_max", "conservar_original",
            "confirmar", "preview", "n_esperado",
        };

        public string Name => "explotar";

        public string Description =>
            "EXPLOTA inserciones de bloque (INSERT) por 'handles' (1–500) o por patrones de nombre 'bloques' en 'espacio' " +
            "('modelo' por defecto), convirtiéndolas en geometría editable en el mismo paso de deshacer (CTRL+Z). Con " +
            "anidados: true explota recursivamente los bloques anidados hasta profundidad_max (defecto 10). Excluye xrefs y " +
            "bloques no explotables. Sin confirmar: true solo PREVISUALIZA. Límite de 20000 entidades resultantes.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public int MainThreadTimeoutMs => PluginInfo.SlowToolTimeoutMs;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""handles"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": " + ParamReader.SchemaHandle + @", ""description"": ""Handles de los INSERT a explotar (excluyente con bloques y espacio)."" },
    ""bloques"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 }, ""description"": ""Nombres o patrones (*, ?) de definiciones cuyas inserciones en 'espacio' se explotarán (excluyente con handles)."" },
    ""espacio"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""default"": ""modelo"", ""description"": ""Solo con 'bloques': 'modelo' (defecto) o nombre de un layout."" },
    ""anidados"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = explotar también los INSERT anidados resultantes de forma recursiva."" },
    ""profundidad_max"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 20, ""default"": 10, ""description"": ""Profundidad máxima de recursión cuando anidados: true."" },
    ""conservar_original"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = conservar el INSERT original después de crear la geometría explotada."" },
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @",
    ""n_esperado"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 20000, ""description"": ""Solo con confirmar: true. Pase el n_inserts de la previsualización."" }
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""espacio"": { ""type"": ""string"" },
    ""n_inserts"": { ""type"": ""integer"" },
    ""n_explotados"": { ""type"": [""integer"", ""null""] },
    ""n_entidades_estimadas"": { ""type"": ""integer"" },
    ""n_creadas"": { ""type"": [""integer"", ""null""] },
    ""n_omitidas"": { ""type"": ""integer"" },
    ""omitidas_por_motivo"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""n_usuario"": { ""type"": ""integer"" },
    ""por_bloque"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""creadas_por_tipo"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } },
    ""handles_muestra"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""espacio"", ""n_inserts"", ""n_explotados"", ""n_entidades_estimadas"", ""n_creadas"", ""n_omitidas"", ""omitidas_por_motivo"", ""n_usuario"", ""por_bloque"", ""origen_texto""]
}");

        private sealed class Params
        {
            public List<string>? Handles;
            public List<string>? Bloques;
            public string Espacio = SpaceResolver.ModelName;
            public bool Anidados;
            public int ProfundidadMax = BlockEditRules.DefaultDepth;
            public bool ConservarOriginal;
            public CleanWrite.Mode Mode;
            public int? NEsperado;
        }

        private sealed class TargetInsert
        {
            public TargetInsert(ObjectId id, string handle, string blockName, string layer, ObjectId defId, bool nonUniform, bool hasAttributes)
            {
                Id = id;
                Handle = handle;
                BlockName = blockName;
                Layer = layer;
                DefId = defId;
                NonUniform = nonUniform;
                HasAttributes = hasAttributes;
            }

            public ObjectId Id { get; }
            public string Handle { get; }
            public string BlockName { get; }
            public string Layer { get; }
            public ObjectId DefId { get; }
            public bool NonUniform { get; }
            public bool HasAttributes { get; }
        }

        private sealed class ScanResult
        {
            public string Espacio = SpaceResolver.ModelName;
            public ObjectId SpaceId;
            public List<TargetInsert> Targets = new List<TargetInsert>();
            public Dictionary<string, int> Omitidas = new Dictionary<string, int>(StringComparer.Ordinal);
            public int EstimatedEntities;
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            bool hasHandles = ParamReader.Has(p, "handles");
            bool hasBloques = ParamReader.Has(p, "bloques");
            if (hasHandles == hasBloques)
            {
                throw ToolException.InvalidParams(hasHandles
                    ? "handles y bloques son excluyentes: indique uno u otro"
                    : "handles o bloques: indique al menos uno");
            }

            string? espacio = ParamReader.OptionalString(p, "espacio", 1, 255);
            if (hasHandles && espacio != null)
            {
                throw ToolException.InvalidParams("espacio: no aplica con handles");
            }

            List<string>? handles = hasHandles ? ParamReader.RequiredHandles(p, "handles", 1, BlockEditRules.MaxHandles) : null;
            List<string>? bloques = null;
            if (hasBloques)
            {
                List<string>? raw = ParamReader.OptionalStringArray(p, "bloques", 1, BlockEditRules.MaxBlocks, 1);
                try
                {
                    bloques = BlockEditRules.NormalizeBlockPatterns(raw!);
                }
                catch (ArgumentException ex)
                {
                    throw ToolException.InvalidParams(ex.Message);
                }
            }

            bool anidados = ParamReader.OptionalBool(p, "anidados") ?? false;
            int depth = ParamReader.OptionalInt(p, "profundidad_max", 1, BlockEditRules.MaxDepth) ?? BlockEditRules.DefaultDepth;
            bool conservar = ParamReader.OptionalBool(p, "conservar_original") ?? false;
            CleanWrite.Common cw = CleanWrite.ReadCommon(p);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(cw.Confirmar, cw.Preview);

            int? nEsperado = ParamReader.OptionalInt(p, "n_esperado", 0, BlockEditRules.MaxEntities);
            if (nEsperado.HasValue && mode == CleanWrite.Mode.Preview)
            {
                throw ToolException.InvalidParams("n_esperado: solo aplica con confirmar: true");
            }

            return new Params
            {
                Handles = handles,
                Bloques = bloques,
                Espacio = espacio ?? SpaceResolver.ModelName,
                Anidados = anidados,
                ProfundidadMax = depth,
                ConservarOriginal = conservar,
                Mode = mode,
                NEsperado = nEsperado,
            };
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = CleanWrite.ReadUnits(db);

            ScanResult scan;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                scan = ScanTargets(db, tr, p, warnings);
                tr.Commit();
            }

            if (p.Mode == CleanWrite.Mode.Preview)
            {
                return BuildResponse(ctx.Pid, CleanWrite.Mode.Preview, ins, warnings, scan, nExplotados: null, nCreadas: null, creadasPorTipo: null, handlesMuestra: null);
            }

            string? expErr = BlockEditRules.CheckExpected(p.NEsperado, scan.Targets.Count, "explotó");
            if (expErr != null)
            {
                throw ToolException.InvalidParams(
                    expErr,
                    new Dictionary<string, object?> { ["n_esperado"] = p.NEsperado, ["n_inserts"] = scan.Targets.Count });
            }

            if (scan.Targets.Count == 0)
            {
                warnings.Add("Ningún INSERT cumple el criterio tras las exclusiones: no se escribió nada.");
                Dictionary<string, object?> empty = BuildResponse(ctx.Pid, CleanWrite.Mode.Write, ins, warnings, scan, nExplotados: 0, nCreadas: 0, new Dictionary<string, int>(), new List<string>());
                empty["escrito"] = false;
                empty["preview"] = false;
                return empty;
            }

            int pid = ctx.Pid;
            return UndoGroup.Run(doc, d => Write(d, p, pid, ins, warnings));
        }

        private static ScanResult ScanTargets(Database db, Transaction tr, Params p, List<string> warnings)
        {
            var scan = new ScanResult();

            if (p.Handles != null)
            {
                HandleGuard.Resolved res = HandleGuard.Resolve(db, tr, p.Handles, confirmar: true, warnings);
                scan.Espacio = res.Espacio;
                scan.SpaceId = res.OwnerId;

                foreach (HandleGuard.Item item in res.Items)
                {
                    if (!(item.Entity is BlockReference br))
                    {
                        throw ToolException.InvalidParams(
                            "handle '" + item.Info.Handle + "': es de tipo " + AcadUtil.DxfName(item.Entity) + ", no es un INSERT",
                            new Dictionary<string, object?> { ["handle"] = item.Info.Handle, ["tipo"] = AcadUtil.DxfName(item.Entity) });
                    }

                    ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                    if (defId.IsNull || defId.IsErased || !(tr.GetObject(defId, OpenMode.ForRead, false) is BlockTableRecord btr))
                    {
                        throw ToolException.InvalidParams("handle '" + item.Info.Handle + "': definición de bloque nula o borrada");
                    }

                    if (btr.IsFromExternalReference || btr.IsFromOverlayReference || btr.IsDependent)
                    {
                        throw ToolException.InvalidParams("handle '" + item.Info.Handle + "': el bloque '" + btr.Name + "' es una referencia externa (xref); use gestionar_xref");
                    }

                    if (!btr.Explodable)
                    {
                        throw ToolException.InvalidParams("handle '" + item.Info.Handle + "': la definición '" + btr.Name + "' tiene Explodable=false");
                    }

                    scan.Targets.Add(BuildTarget(br, btr, item.Info.Handle, item.Info.Capa));
                }
            }
            else
            {
                SpaceResolver.ResolvedSpace space = SpaceResolver.Resolve(db, tr, p.Espacio);
                if (!space.Btr.IsLayout)
                {
                    throw ToolException.InvalidParams("espacio: debe ser el modelo o un layout (no una definición de bloque)");
                }

                scan.Espacio = space.Name;
                scan.SpaceId = space.Btr.ObjectId;
                var blockMatchCache = new Dictionary<ObjectId, bool>();

                foreach (ObjectId id in space.Btr)
                {
                    if (id.IsNull || id.IsErased)
                    {
                        continue;
                    }

                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is BlockReference br))
                    {
                        continue;
                    }

                    ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                    if (defId.IsNull || defId.IsErased || !(tr.GetObject(defId, OpenMode.ForRead, false) is BlockTableRecord btr))
                    {
                        continue;
                    }

                    if (!blockMatchCache.TryGetValue(defId, out bool matched))
                    {
                        matched = LayerPattern.MatchesAny(btr.Name, p.Bloques) || LayerPattern.MatchesAny(br.Name, p.Bloques);
                        blockMatchCache[defId] = matched;
                    }

                    if (!matched)
                    {
                        continue;
                    }

                    var layer = tr.GetObject(br.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
                    if (layer == null || layer.IsDependent || layer.Name.IndexOf('|') >= 0)
                    {
                        CleanWrite.Increment(scan.Omitidas, "capa_xref");
                        continue;
                    }

                    if (layer.IsLocked)
                    {
                        CleanWrite.Increment(scan.Omitidas, "capa_bloqueada");
                        continue;
                    }

                    if (btr.IsFromExternalReference || btr.IsFromOverlayReference || btr.IsDependent)
                    {
                        CleanWrite.Increment(scan.Omitidas, "xref");
                        continue;
                    }

                    if (!btr.Explodable)
                    {
                        CleanWrite.Increment(scan.Omitidas, "no_explotable");
                        continue;
                    }

                    scan.Targets.Add(BuildTarget(br, btr, AcadUtil.HandleToString(id.Handle), layer.Name));
                }
            }

            int maxDepth = p.Anidados ? p.ProfundidadMax : 1;
            var memo = new Dictionary<(ObjectId DefId, int Depth), int>();
            long estimated = 0;
            int nonUniformCount = 0;
            int withAttribsCount = 0;

            foreach (TargetInsert t in scan.Targets)
            {
                estimated += EstimateBlockEntities(tr, t.DefId, maxDepth, memo, new HashSet<ObjectId>());
                if (t.NonUniform) nonUniformCount++;
                if (t.HasAttributes) withAttribsCount++;
            }

            if (estimated > BlockEditRules.MaxEntities)
            {
                throw ToolException.InvalidParams(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "explotar: se estiman {0} entidades resultantes (máximo {1} por llamada); seleccione menos inserciones o baje profundidad_max.",
                        estimated, BlockEditRules.MaxEntities),
                    new Dictionary<string, object?> { ["n_entidades_estimadas"] = estimated, ["maximo"] = BlockEditRules.MaxEntities });
            }

            scan.EstimatedEntities = (int)estimated;
            if (nonUniformCount > 0)
            {
                warnings.Add("explotar: " + nonUniformCount.ToString(CultureInfo.InvariantCulture)
                    + " INSERT(s) tienen escala no uniforme; AutoCAD puede rechazar su explosión (eCannotScaleNonUniformly) y se omitirán.");
            }

            if (withAttribsCount > 0)
            {
                warnings.Add("explotar: " + withAttribsCount.ToString(CultureInfo.InvariantCulture)
                    + " INSERT(s) contienen atributos; al explotarlos se sustituyen por definiciones ATTDEF de la definición.");
            }

            return scan;
        }

        private static TargetInsert BuildTarget(BlockReference br, BlockTableRecord btr, string handle, string layer)
        {
            double sx = Math.Abs(br.ScaleFactors.X);
            double sy = Math.Abs(br.ScaleFactors.Y);
            double sz = Math.Abs(br.ScaleFactors.Z);
            bool nonUniform = Math.Abs(sx - sy) > 1e-4 || Math.Abs(sx - sz) > 1e-4;
            bool hasAttrs = br.AttributeCollection != null && br.AttributeCollection.Count > 0;
            return new TargetInsert(br.ObjectId, handle, btr.Name, layer, btr.ObjectId, nonUniform, hasAttrs);
        }

        private static int EstimateBlockEntities(
            Transaction tr, ObjectId defId, int depthRemaining,
            Dictionary<(ObjectId, int), int> memo, HashSet<ObjectId> visiting)
        {
            if (depthRemaining <= 0 || defId.IsNull || defId.IsErased || !visiting.Add(defId))
            {
                return 0;
            }

            if (memo.TryGetValue((defId, depthRemaining), out int cached))
            {
                visiting.Remove(defId);
                return cached;
            }

            int total = 0;
            if (tr.GetObject(defId, OpenMode.ForRead, false) is BlockTableRecord btr
                && !btr.IsFromExternalReference && !btr.IsFromOverlayReference && !btr.IsDependent)
            {
                foreach (ObjectId id in btr)
                {
                    if (id.IsNull || id.IsErased)
                    {
                        continue;
                    }

                    if (depthRemaining > 1 && tr.GetObject(id, OpenMode.ForRead, false) is BlockReference childBr)
                    {
                        ObjectId childDef = childBr.IsDynamicBlock ? childBr.DynamicBlockTableRecord : childBr.BlockTableRecord;
                        if (!childDef.IsNull && !childDef.IsErased
                            && tr.GetObject(childDef, OpenMode.ForRead, false) is BlockTableRecord childBtr
                            && !childBtr.IsFromExternalReference && !childBtr.IsFromOverlayReference && !childBtr.IsDependent
                            && childBtr.Explodable)
                        {
                            total += EstimateBlockEntities(tr, childDef, depthRemaining - 1, memo, visiting);
                            continue;
                        }
                    }

                    total++;
                }
            }

            visiting.Remove(defId);
            memo[(defId, depthRemaining)] = total;
            return total;
        }

        private static object Write(Document doc, Params p, int pid, int ins, List<string> warnings)
        {
            Database db = doc.Database;
            int maxDepth = p.Anidados ? p.ProfundidadMax : 1;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ScanResult scan = ScanTargets(db, tr, p, new List<string>());
                var space = (BlockTableRecord)tr.GetObject(scan.SpaceId, OpenMode.ForWrite);

                var queue = new Queue<(BlockReference Br, int Depth, bool TopLevel)>();
                foreach (TargetInsert t in scan.Targets)
                {
                    if (!t.Id.IsNull && !t.Id.IsErased
                        && tr.GetObject(t.Id, OpenMode.ForRead, false) is BlockReference br
                        && !BorrarPorFiltroHandler.CapaProtegida(tr, br.LayerId))
                    {
                        queue.Enqueue((br, 1, true));
                    }
                    else
                    {
                        CleanWrite.Increment(scan.Omitidas, "cambiada");
                    }
                }

                int explotadosTopLevel = 0;
                int totalCreadas = 0;
                var creadasPorTipo = new Dictionary<string, int>(StringComparer.Ordinal);
                var createdRecords = new List<(ObjectId Id, string Tipo)>();

                while (queue.Count > 0)
                {
                    (BlockReference curBr, int depth, bool topLevel) = queue.Dequeue();
                    if (curBr.IsErased)
                    {
                        continue;
                    }

                    using var exploded = new DBObjectCollection();
                    try
                    {
                        curBr.Explode(exploded);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        foreach (DBObject obj in exploded)
                        {
                            obj?.Dispose();
                        }

                        CleanWrite.Increment(scan.Omitidas, "error_al_explotar");
                        continue;
                    }

                    var pendingChildren = new List<Entity>(exploded.Count);
                    try
                    {
                        foreach (DBObject obj in exploded)
                        {
                            if (obj is Entity ent)
                            {
                                pendingChildren.Add(ent);
                            }
                            else
                            {
                                obj?.Dispose();
                            }
                        }

                        if (totalCreadas + pendingChildren.Count > BlockEditRules.MaxEntities)
                        {
                            throw ToolException.InvalidParams(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "explotar: superó el máximo de {0} entidades resultantes durante la explosión; operación revertida.",
                                    BlockEditRules.MaxEntities));
                        }

                        foreach (Entity child in pendingChildren)
                        {
                            space.AppendEntity(child);
                            tr.AddNewlyCreatedDBObject(child, true);

                            if (depth < maxDepth && child is BlockReference nestedBr && CanExplodeInTx(tr, nestedBr))
                            {
                                queue.Enqueue((nestedBr, depth + 1, false));
                            }
                            else
                            {
                                string tipo = AcadUtil.DxfName(child);
                                totalCreadas++;
                                CleanWrite.Increment(creadasPorTipo, tipo);
                                createdRecords.Add((child.ObjectId, tipo));
                            }
                        }

                        pendingChildren.Clear();
                    }
                    finally
                    {
                        foreach (Entity unappended in pendingChildren)
                        {
                            unappended.Dispose();
                        }
                    }

                    if (!topLevel || !p.ConservarOriginal)
                    {
                        curBr.UpgradeOpen();
                        curBr.Erase();
                    }

                    if (topLevel)
                    {
                        explotadosTopLevel++;
                    }
                }

                var handlesMuestra = new List<string>(BlockEditRules.MaxSample);
                foreach ((ObjectId id, string _) in createdRecords)
                {
                    if (!id.IsNull && !id.IsErased)
                    {
                        handlesMuestra.Add(AcadUtil.HandleToString(id.Handle));
                        if (handlesMuestra.Count >= BlockEditRules.MaxSample)
                        {
                            break;
                        }
                    }
                }

                tr.Commit();
                return BuildResponse(pid, CleanWrite.Mode.Write, ins, warnings, scan, explotadosTopLevel, totalCreadas, creadasPorTipo, handlesMuestra);
            }
        }

        private static bool CanExplodeInTx(Transaction tr, BlockReference br)
        {
            ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            if (defId.IsNull || defId.IsErased || !(tr.GetObject(defId, OpenMode.ForRead, false) is BlockTableRecord btr))
            {
                return false;
            }

            return !btr.IsFromExternalReference && !btr.IsFromOverlayReference && !btr.IsDependent && btr.Explodable;
        }

        private static Dictionary<string, object?> BuildResponse(
            int pid,
            CleanWrite.Mode mode,
            int ins,
            List<string> warnings,
            ScanResult scan,
            int? nExplotados,
            int? nCreadas,
            Dictionary<string, int>? creadasPorTipo,
            List<string>? handlesMuestra)
        {
            var porBloque = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int nUsuario = 0;
            foreach (TargetInsert t in scan.Targets)
            {
                CleanWrite.Increment(porBloque, t.BlockName);
                if (!EntityProtection.IsMcpLayer(t.Layer))
                {
                    nUsuario++;
                }
            }

            var porBloqueTop = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in BlockEditRules.TopBlocks(porBloque))
            {
                porBloqueTop[kv.Key] = kv.Value;
            }

            var omitidasOut = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in scan.Omitidas.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                omitidasOut[kv.Key] = kv.Value;
            }

            Dictionary<string, object?> r = CleanWrite.Response(pid, mode, ins, warnings);
            r["espacio"] = scan.Espacio;
            r["n_inserts"] = scan.Targets.Count;
            r["n_explotados"] = nExplotados;
            r["n_entidades_estimadas"] = scan.EstimatedEntities;
            r["n_creadas"] = nCreadas;
            r["n_omitidas"] = scan.Omitidas.Values.Sum();
            r["omitidas_por_motivo"] = omitidasOut;
            r["n_usuario"] = nUsuario;
            r["por_bloque"] = porBloqueTop;
            if (creadasPorTipo != null)
            {
                r["creadas_por_tipo"] = CleanWrite.SortedCounts(creadasPorTipo);
            }

            if (handlesMuestra != null)
            {
                r["handles_muestra"] = handlesMuestra;
            }

            r["origen_texto"] = "contenido_del_plano";
            return r;
        }
    }
}
