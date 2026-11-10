using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Audit;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers.Clean
{
    /// <summary>
    /// corregir_unidades_bloques (protocol.md §5F.4). Hilo principal; requiere documento; ESCRITURA con previsualización
    /// sin estado (§5F.0 L1: sin confirmar: true no se escribe nada). Fija <c>BlockTableRecord.Units</c> = INSUNITS en las
    /// definiciones que <c>auditar_bloques</c> marca con <c>datos.metadato_unidades_incorrecto = true</c>: el criterio es
    /// EXACTAMENTE el de <see cref="BloquesCheck.Analyze"/> (no se duplica la heurística). No cambia geometría ni
    /// inserciones. Flujo §5F.0: Parse → RequireDocument → CleanWrite.CheckUnits → lectura (StartOpenCloseTransaction)
    /// con todos los rechazos → previsualización (CleanWrite.Response) o UndoGroup.Run con UNA Transaction.
    /// </summary>
    internal sealed class CorregirUnidadesBloquesHandler : IToolHandler
    {
        public const string OrigenAuditar = "auditar_bloques";
        public const string OrigenParametro = "parametro";

        public const string MotivoUnidadesIguales = "unidades iguales a las del dibujo";
        public const string MotivoSinInserciones = "sin inserciones";
        public const string MotivoCompensadas = "inserciones compensadas";
        public const string MotivoNoPlausible = "tamaño real no plausible";
        public const string MotivoNoMedible = "tamaño no medible";

        private const int MaxBloques = 200;

        private static readonly string[] Allowed = { "bloques", "confirmar", "preview" };

        public string Name => "corregir_unidades_bloques";

        public string Description =>
            "CORRIGE el metadato de unidades de definiciones de bloque cuya geometría ya está en las unidades del dibujo " +
            "(las que auditar_bloques marca con metadato_unidades_incorrecto): fija BlockTableRecord.Units = INSUNITS del " +
            "dibujo, sin tocar geometría ni inserciones, en un solo paso de deshacer. Sin confirmar: true solo PREVISUALIZA " +
            "(unidades actuales y propuestas por bloque) y no modifica nada.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""bloques"": {
      ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200,
      ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255 },
      ""description"": ""Nombres de definiciones (sin distinguir mayúsculas). Por defecto, exactamente las que auditar_bloques marca con datos.metadato_unidades_incorrecto = true; con lista, cada una debe existir, no ser layout/xref/anónima y estar marcada así.""
    },
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""origen_lista"": { ""type"": ""string"", ""enum"": [""auditar_bloques"", ""parametro""] },
    ""bloques"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""nombre"": { ""type"": ""string"" }, ""unidades_actuales"": { ""type"": ""integer"" },
      ""unidades_actuales_nombre"": { ""type"": ""string"" }, ""unidades_propuestas"": { ""type"": ""integer"" },
      ""n_inserciones"": { ""type"": ""integer"" },
      ""tamano_real_m"": { ""type"": [""array"", ""null""], ""items"": { ""type"": ""number"" } },
      ""tamano_por_anidada"": { ""type"": ""boolean"" }, ""hallazgo_id"": { ""type"": ""string"" } } } },
    ""n"": { ""type"": ""integer"" },
    ""corregidos"": { ""type"": [""array"", ""null""], ""items"": { ""type"": ""string"" } },
    ""duracion_ms"": { ""type"": ""object"", ""properties"": {
      ""lectura"": { ""type"": ""integer"" }, ""escritura"": { ""type"": [""integer"", ""null""] } } },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""origen_lista"", ""bloques"", ""n"", ""corregidos"", ""duracion_ms"", ""origen_texto"", ""insunits"", ""unidades"", ""advertencias""]
}");

        /// <summary>Parámetros ya validados (sin AutoCAD).</summary>
        private sealed class Params
        {
            public Params(List<string>? bloques, CleanWrite.Common common)
            {
                Bloques = bloques;
                Common = common;
            }

            /// <summary>Nombres pedidos en el orden del cliente (null = los de auditar_bloques).</summary>
            public List<string>? Bloques { get; }

            public CleanWrite.Common Common { get; }
        }

        /// <summary>Bloque a corregir: lo que se informa y el id que se escribe.</summary>
        private sealed class Candidate
        {
            public Candidate(ObjectId id, string nombre, int unidades, int nInserciones, object? tamanoRealM, bool porAnidada, string hallazgoId)
            {
                Id = id;
                Nombre = nombre;
                Unidades = unidades;
                NInserciones = nInserciones;
                TamanoRealM = tamanoRealM;
                PorAnidada = porAnidada;
                HallazgoId = hallazgoId;
            }

            public ObjectId Id { get; }
            public string Nombre { get; }
            public int Unidades { get; }
            public int NInserciones { get; }
            public object? TamanoRealM { get; }
            public bool PorAnidada { get; }
            public string HallazgoId { get; }
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Params Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            CleanWrite.Common common = CleanWrite.ReadCommon(p);
            List<string>? bloques = ParamReader.OptionalStringArray(p, "bloques", 1, MaxBloques, 1);
            if (bloques != null)
            {
                for (int i = 0; i < bloques.Count; i++)
                {
                    if (bloques[i].Length > 255)
                    {
                        throw ToolException.InvalidParams("bloques[" + I(i) + "]: debe tener entre 1 y 255 caracteres");
                    }
                }
            }

            return new Params(bloques, common);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Params p = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(p.Common.Confirmar, p.Common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int ins = CleanWrite.CheckUnits(db, mode, warnings);

            var swRead = Stopwatch.StartNew();
            List<Candidate> candidates = Read(db, ins, p.Bloques, warnings);
            long lecturaMs = swRead.ElapsedMilliseconds;
            string origenLista = p.Bloques == null ? OrigenAuditar : OrigenParametro;

            if (mode == CleanWrite.Mode.Preview)
            {
                return BuildResponse(ctx.Pid, mode, ins, warnings, origenLista, candidates, null, lecturaMs, null);
            }

            return UndoGroup.Run(doc, d => Write(d, ctx.Pid, ins, warnings, origenLista, candidates, lecturaMs));
        }

        /// <summary>
        /// Lectura previa (sin efectos): rechazos de la lista explícita y candidatos según BloquesCheck.Analyze. Con
        /// INSUNITS = 0 (solo llega aquí en previsualización: la escritura ya se rechazó en CheckUnits) → lista vacía con
        /// la advertencia de §5C.3.
        /// </summary>
        private static List<Candidate> Read(Database db, int ins, List<string>? requested, List<string> warnings)
        {
            var result = new List<Candidate>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                // 1. Lista explícita: existencia (todas las que faltan) y tipo de definición.
                Dictionary<string, ObjectId>? byName = null;
                var requestedIds = new List<(int index, ObjectId id)>();
                if (requested != null)
                {
                    byName = BlockNames(db, tr);
                    var missing = new List<string>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < requested.Count; i++)
                    {
                        if (!byName.TryGetValue(requested[i], out ObjectId id))
                        {
                            if (seen.Add(requested[i]))
                            {
                                missing.Add(requested[i]);
                            }

                            continue;
                        }

                        if (seen.Add(requested[i]))
                        {
                            requestedIds.Add((i, id));
                        }
                    }

                    if (missing.Count > 0)
                    {
                        string detalle = "bloques: definiciones inexistentes: " + string.Join(", ", missing);
                        throw new ToolException(
                            ErrorCodes.NotFound,
                            detalle,
                            new Dictionary<string, object?> { ["bloques"] = missing, ["detalle"] = detalle });
                    }

                    foreach ((int index, ObjectId id) in requestedIds)
                    {
                        var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead, false);
                        if (!IsPlainDefinition(btr))
                        {
                            throw ToolException.InvalidParams(
                                "bloques[" + I(index) + "]: '" + requested[index] + "' es un layout, una xref o un bloque anónimo",
                                new Dictionary<string, object?> { ["bloque"] = btr.Name });
                        }
                    }
                }

                // 2. Criterio de auditar_bloques (misma transacción de lectura, mismo presupuesto). Ids como en
                //    auditar_bloques sin filtro de sección: todos los hallazgos ordenados y numerados.
                BlockAuditResult analysis = new BloquesCheck().Analyze(
                    AuditSupport.CreateContext(db, tr, SpaceResolver.Resolve(db, tr, null)));
                var findings = new List<Finding>();
                var unitFindings = new Dictionary<string, Finding>(StringComparer.OrdinalIgnoreCase);
                foreach (BlockFinding bf in analysis.Hallazgos)
                {
                    findings.Add(bf.Finding);
                    if (bf.Seccion == BloquesCheck.SeccionUnidades && bf.Finding.Datos.TryGetValue("bloque", out object? b) && b is string name)
                    {
                        unitFindings[name] = bf.Finding;
                    }
                }

                Finding.Sort(findings, AuditSupport.RuleOrder);
                Finding.AssignIds(findings);
                foreach (string w in analysis.Advertencias)
                {
                    AddOnce(warnings, w);
                }

                if (!analysis.Completo)
                {
                    AddOnce(warnings, AuditSupport.WarningPartial);
                }

                if (ins == 0)
                {
                    // §5F.4: con INSUNITS = 0 no se comparan unidades (Analyze ya añadió WarningUnitsZero).
                    AddOnce(warnings, BloquesCheck.WarningUnitsZero);
                    tr.Commit();
                    return result;
                }

                if (requested == null)
                {
                    foreach (Finding f in unitFindings.Values)
                    {
                        if (IsMetadataWrong(f) && TryCandidate(db, tr, f, out Candidate? c))
                        {
                            result.Add(c!);
                        }
                    }
                }
                else
                {
                    foreach ((int index, ObjectId id) in requestedIds)
                    {
                        var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead, false);
                        unitFindings.TryGetValue(btr.Name, out Finding? f);
                        if (f != null && IsMetadataWrong(f) && TryCandidate(db, tr, f, out Candidate? c))
                        {
                            result.Add(c!);
                            continue;
                        }

                        string motivo = Motive(btr, tr, ins, f);
                        throw ToolException.InvalidParams(
                            "bloques[" + I(index) + "]: '" + requested[index]
                            + "' no está marcado por auditar_bloques como metadato de unidades incorrecto (" + motivo + ")",
                            new Dictionary<string, object?> { ["bloque"] = btr.Name });
                    }
                }

                tr.Commit();
            }

            result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Nombre, b.Nombre));
            return result;
        }

        /// <summary>Escritura: UNA Transaction; revalida cada definición (viva y definición normal) antes de tocarla.</summary>
        private static object Write(
            Document doc, int pid, int ins, List<string> warnings, string origenLista, List<Candidate> candidates, long lecturaMs)
        {
            Database db = doc.Database;
            var sw = Stopwatch.StartNew();
            var corregidos = new List<string>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (Candidate c in candidates)
                {
                    if (c.Id.IsNull || c.Id.IsErased
                        || !(tr.GetObject(c.Id, OpenMode.ForRead, false) is BlockTableRecord btr) || !IsPlainDefinition(btr))
                    {
                        warnings.Add("La definición '" + c.Nombre + "' ya no existe o cambió desde la lectura; no se corrigió.");
                        continue;
                    }

                    btr.UpgradeOpen();
                    btr.Units = (UnitsValue)ins;
                    corregidos.Add(btr.Name);
                }

                tr.Commit();
            }

            long escrituraMs = sw.ElapsedMilliseconds;
            PluginLog.Info("corregir_unidades_bloques: " + I(corregidos.Count) + " definición(es) corregida(s) en " + escrituraMs.ToString(CultureInfo.InvariantCulture) + " ms");
            return BuildResponse(pid, CleanWrite.Mode.Write, ins, warnings, origenLista, candidates, corregidos, lecturaMs, escrituraMs);
        }

        private static Dictionary<string, object?> BuildResponse(
            int pid, CleanWrite.Mode mode, int ins, List<string> warnings, string origenLista, List<Candidate> candidates,
            List<string>? corregidos, long lecturaMs, long? escrituraMs)
        {
            Dictionary<string, object?> r = CleanWrite.Response(pid, mode, ins, warnings);
            var rows = new List<Dictionary<string, object?>>(candidates.Count);
            foreach (Candidate c in candidates)
            {
                rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["nombre"] = c.Nombre,
                    ["unidades_actuales"] = c.Unidades,
                    ["unidades_actuales_nombre"] = UnitsTable.Name(c.Unidades),
                    ["unidades_propuestas"] = ins,
                    ["n_inserciones"] = c.NInserciones,
                    ["tamano_real_m"] = c.TamanoRealM,
                    ["tamano_por_anidada"] = c.PorAnidada,
                    ["hallazgo_id"] = c.HallazgoId,
                });
            }

            r["origen_lista"] = origenLista;
            r["bloques"] = rows;
            r["n"] = rows.Count;
            r["corregidos"] = corregidos;
            r["duracion_ms"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["lectura"] = lecturaMs,
                ["escritura"] = escrituraMs,
            };
            r["origen_texto"] = AuditSupport.OrigenTexto;
            return r;
        }

        // ------------------------------------------------------------------ apoyo

        /// <summary>Todos los registros del BlockTable por nombre (sin distinguir mayúsculas), incluidos layouts/xrefs/anónimos.</summary>
        private static Dictionary<string, ObjectId> BlockNames(Database db, Transaction tr)
        {
            var map = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead, false) is BlockTableRecord btr && !string.IsNullOrEmpty(btr.Name))
                    {
                        map.TryAdd(btr.Name, id);
                    }
                }
                catch (Exception)
                {
                    // Registro ilegible: no se puede pedir por nombre.
                }
            }

            return map;
        }

        /// <summary>Definición con nombre que no es layout, xref (ni dependiente de xref) ni anónima.</summary>
        private static bool IsPlainDefinition(BlockTableRecord btr)
        {
            string name = btr.Name ?? "";
            return !btr.IsLayout && !btr.IsFromExternalReference && !btr.IsDependent && !btr.IsAnonymous
                && !name.StartsWith("*", StringComparison.Ordinal) && name.IndexOf('|') < 0;
        }

        private static bool IsMetadataWrong(Finding f) =>
            f.Datos.TryGetValue("metadato_unidades_incorrecto", out object? v) && v is bool b && b;

        /// <summary>Candidato a partir del hallazgo (datos de auditar_bloques; el id de la definición se busca por nombre).</summary>
        private static bool TryCandidate(Database db, Transaction tr, Finding f, out Candidate? candidate)
        {
            candidate = null;
            if (!(f.Datos.TryGetValue("bloque", out object? b) && b is string name))
            {
                return false;
            }

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(name))
            {
                return false;
            }

            ObjectId id = bt[name];
            candidate = new Candidate(
                id,
                name,
                DatoInt(f, "unidades_bloque"),
                DatoInt(f, "n_inserciones"),
                f.Datos.TryGetValue("tamano_real_m", out object? t) ? t : null,
                f.Datos.TryGetValue("tamano_por_anidada", out object? a) && a is bool ab && ab,
                f.Id);
            return true;
        }

        /// <summary>
        /// Motivo de §5F.4 por el que una definición pedida no es candidata. Sin hallazgo de unidades: unidades iguales a
        /// las del dibujo (o sin unidades, que se inserta sin factor) o sin inserciones (si no, el plazo de la auditoría
        /// cortó antes de evaluarla → no medible). Con hallazgo: compensadas (n_no_compensadas = 0 con factor y alguna
        /// inserción evaluada), alta (tamaño medido no plausible) o no medible (sin factor, sin escala legible o sin tamaño).
        /// </summary>
        private static string Motive(BlockTableRecord btr, Transaction tr, int ins, Finding? f)
        {
            if (f == null)
            {
                int units;
                try
                {
                    units = (int)btr.Units;
                }
                catch (Exception)
                {
                    units = 0;
                }

                if (units == ins || units == 0)
                {
                    return MotivoUnidadesIguales;
                }

                return AcadUtil.CountInsertions(btr, tr) == 0 ? MotivoSinInserciones : MotivoNoMedible;
            }

            bool sinFactor = !f.Datos.TryGetValue("factor_escala", out object? factor) || factor == null;
            if (DatoInt(f, "n_no_compensadas") == 0)
            {
                return sinFactor || DatoInt(f, "inserciones_evaluadas") == 0 ? MotivoNoMedible : MotivoCompensadas;
            }

            if (f.Severidad == Severity.Alta)
            {
                return MotivoNoPlausible;
            }

            return MotivoNoMedible;
        }

        private static int DatoInt(Finding f, string key) =>
            f.Datos.TryGetValue(key, out object? v) && v is int n ? n : 0;

        private static void AddOnce(List<string> warnings, string w)
        {
            if (!string.IsNullOrEmpty(w) && !warnings.Contains(w))
            {
                warnings.Add(w);
            }
        }

        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
