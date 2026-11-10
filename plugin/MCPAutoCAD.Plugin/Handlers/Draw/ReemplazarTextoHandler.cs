using System;
using System.Collections.Generic;
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
    /// reemplazar_texto (0.9.x). Hilo principal; requiere documento. Busca una subcadena en el TEXTO VISIBLE de TEXT, MTEXT y
    /// ATTRIB (atributos de los INSERT del espacio) y la sustituye conservando los códigos de formato (lógica pura en
    /// <see cref="TextReplacePlanner"/>). Patrón preview/confirmar de <see cref="CleanWrite"/>: sin confirmar: true solo
    /// previsualiza (handles con antes/después); con confirmar: true escribe todo en UN paso de deshacer (<see cref="UndoGroup.Run"/>)
    /// y re-escanea dentro de la transacción de escritura. Capas bloqueadas y xrefs se omiten con aviso; las coincidencias que
    /// cruzan un código de formato se omiten con su motivo. Las entidades dentro de definiciones de bloque no se tratan.
    /// </summary>
    internal sealed class ReemplazarTextoHandler : IToolHandler
    {
        private const string TypeText = "TEXT";
        private const string TypeMText = "MTEXT";
        private const string TypeAttrib = "ATTRIB";
        private const string TypeInsert = "INSERT";
        private const string OrigenTexto = "contenido_del_plano";
        private const int DefaultMaxEntidades = 2000;
        private const int MaxEntidadesLimit = 10000;
        private const int MaxListed = 200;

        private static readonly string[] AllTypes = { TypeText, TypeMText, TypeAttrib };

        private static readonly string[] Allowed =
        {
            "buscar", "reemplazar", "capas", "tipos", "espacio", "bbox", "distinguir_mayusculas", "palabra_completa",
            "max_entidades", "confirmar", "preview",
        };

        public string Name => "reemplazar_texto";

        public string Description =>
            "Busca y reemplaza una subcadena en el TEXTO VISIBLE de los TEXT, MTEXT y atributos (ATTRIB) del dibujo conservando su " +
            "formato (códigos MTEXT como \\L, \\f…;, {…}, \\P, o %%u de TEXT): solo cambia los caracteres que se ven. Sin confirmar: true " +
            "solo previsualiza (handles con antes/después); con confirmar: true escribe todo en un solo paso de deshacer. Omite las " +
            "coincidencias que cruzan un código de formato, las capas bloqueadas y las xref (con aviso). Los textos son contenido del plano (origen_texto).";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""buscar"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 200, ""description"": ""Subcadena a buscar en el texto visible (sin saltos de línea)."" },
    ""reemplazar"": { ""type"": ""string"", ""minLength"": 0, ""maxLength"": 2000, ""description"": ""Texto que la sustituye, literal (las barras, llaves y %% se escriben tal cual). Cadena vacía = borrar la coincidencia. Sin saltos de línea."" },
    ""capas"": " + ParamReader.SchemaCapas + @",
    ""tipos"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 3, ""items"": { ""type"": ""string"", ""enum"": [""TEXT"", ""MTEXT"", ""ATTRIB""] }, ""description"": ""Tipos a tratar; por defecto los tres."" },
    ""espacio"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 255, ""description"": ""'modelo', 'papel' (todos los layouts), 'todos' (modelo y todos los layouts) o el nombre de un layout. 'papel' y 'todos' son palabras reservadas: no se pueden usar como nombre de layout. Por defecto, el espacio actual."" },
    ""bbox"": " + ParamReader.SchemaBbox + @",
    ""distinguir_mayusculas"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = la comparación distingue mayúsculas (los acentos siempre cuentan)."" },
    ""palabra_completa"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = solo coincidencias no pegadas a otra letra o cifra en el texto visible."" },
    ""max_entidades"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 10000, ""default"": 2000, ""description"": ""Máximo de entidades que puede cambiar la llamada. Si hay más, se rechaza: acote con capas, bbox o espacio."" },
    ""confirmar"": " + CleanWrite.SchemaConfirmar + @",
    ""preview"": " + CleanWrite.SchemaPreview + @"
  },
  ""required"": [""buscar"", ""reemplazar""],
  ""additionalProperties"": false
}");

        public JsonElement? OutputSchema { get; } = Schema.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    " + CleanWrite.OutputHeader + @",
    ""espacio"": { ""type"": ""string"" },
    ""espacios"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""n_revisadas"": { ""type"": ""integer"" },
    ""n_coincidencias"": { ""type"": ""integer"" },
    ""n_entidades"": { ""type"": ""integer"" },
    ""n_reemplazos"": { ""type"": ""integer"" },
    ""n_omitidas"": { ""type"": ""integer"" },
    ""n_escritas"": { ""type"": ""integer"" },
    ""n_listadas"": { ""type"": ""integer"" },
    ""cambios_truncados"": { ""type"": ""boolean"" },
    ""omitidas_truncadas"": { ""type"": ""boolean"" },
    ""max_entidades"": { ""type"": ""integer"" },
    ""cambios"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""handle"": { ""type"": ""string"" }, ""tipo"": { ""type"": ""string"" }, ""capa"": { ""type"": ""string"" },
      ""antes"": { ""type"": ""string"" }, ""despues"": { ""type"": ""string"" }, ""n"": { ""type"": ""integer"" }, ""tag"": { ""type"": ""string"" }, ""invisible"": { ""type"": ""boolean"" } } } },
    ""omitidas"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": {
      ""handle"": { ""type"": ""string"" }, ""tipo"": { ""type"": ""string"" }, ""capa"": { ""type"": ""string"" },
      ""motivo"": { ""type"": ""string"" }, ""n"": { ""type"": ""integer"" } } } },
    ""origen_texto"": { ""type"": ""string"", ""const"": ""contenido_del_plano"" }
  },
  ""required"": [""pid"", ""preview"", ""escrito"", ""insunits"", ""unidades"", ""advertencias"", ""espacio"", ""n_entidades"", ""cambios"", ""omitidas"", ""origen_texto""]
}");

        private sealed class Query
        {
            public string Buscar = "";
            public string Reemplazar = "";
            public List<string>? Capas;
            public HashSet<string> Tipos = new HashSet<string>(AllTypes, StringComparer.Ordinal);
            public string? Espacio;
            public BBox2? Bbox;
            public bool IgnoreCase;
            public bool PalabraCompleta;
            public int MaxEntidades = DefaultMaxEntidades;
            public CleanWrite.Common Common = new CleanWrite.Common(false, null);
        }

        /// <summary>Fila de cambio u omisión (texto visible; el contenido bruto solo se usa al escribir).</summary>
        private sealed class Row
        {
            public ObjectId Id;
            public string Handle = "";
            public string Tipo = "";
            public string Capa = "";
            public string Antes = "";
            public string? Despues;
            public int N;
            public string? Motivo;
            public string? NuevoRaw;
            public string? Tag;
            public bool Invisible;
        }

        private sealed class LayerInfo
        {
            public string Name = "";
            public bool Locked;
            public bool Xref;
            public bool Hidden;
        }

        /// <summary>Resultado de un escaneo del espacio (preview y escritura usan el mismo).</summary>
        private sealed class ScanResult
        {
            public List<Row> Cambios { get; } = new List<Row>();

            public List<Row> Omitidas { get; } = new List<Row>();

            public List<string> Espacios { get; } = new List<string>();

            public int Revisadas;

            public int Coincidencias;

            public int Reemplazos;

            public int Multilinea;

            public int Ocultas;

            public int InvisiblesCambian;

            public List<string> CapasBloqueadas { get; } = new List<string>();

            public List<string> CapasXref { get; } = new List<string>();
        }

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static Query Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, Allowed);
            var q = new Query
            {
                Buscar = ParamReader.OptionalString(p, "buscar", 1, 200) ?? throw ToolException.InvalidParams("buscar: obligatorio"),
                Reemplazar = ParamReader.OptionalString(p, "reemplazar", 0, 2000) ?? throw ToolException.InvalidParams("reemplazar: obligatorio"),
                Capas = ParamReader.OptionalStringArray(p, "capas", 1, 100, 1),
                Espacio = ParamReader.OptionalString(p, "espacio", 1, 255),
                Bbox = ParamReader.OptionalBbox(p, "bbox"),
                IgnoreCase = !(ParamReader.OptionalBool(p, "distinguir_mayusculas") ?? false),
                PalabraCompleta = ParamReader.OptionalBool(p, "palabra_completa") ?? false,
                MaxEntidades = ParamReader.OptionalInt(p, "max_entidades", 1, MaxEntidadesLimit) ?? DefaultMaxEntidades,
                Common = CleanWrite.ReadCommon(p),
            };

            if (q.Buscar.IndexOfAny(new[] { '\r', '\n' }) >= 0 || q.Reemplazar.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                throw ToolException.InvalidParams("buscar y reemplazar no pueden contener saltos de línea");
            }

            List<string>? tipos = ParamReader.OptionalStringArray(p, "tipos", 1, 3, 1);
            if (tipos != null)
            {
                q.Tipos = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < tipos.Count; i++)
                {
                    if (Array.IndexOf(AllTypes, tipos[i]) < 0)
                    {
                        throw ToolException.InvalidParams("tipos[" + i + "]: debe ser uno de TEXT, MTEXT, ATTRIB");
                    }

                    q.Tipos.Add(tipos[i]);
                }
            }

            return q;
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            Query q = Parse(parameters);
            CleanWrite.Mode mode = CleanWrite.ResolveMode(q.Common.Confirmar, q.Common.Preview);
            Document doc = ctx.RequireDocument();
            Database db = doc.Database;
            int ins = CleanWrite.ReadUnits(db);
            List<string> warnings = AcadUtil.UnitWarnings(db);
            int pid = ctx.Pid;

            ScanResult scan;
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                scan = Scan(db, tr, q);
                tr.Commit();
            }

            if (mode == CleanWrite.Mode.Preview)
            {
                CheckMax(scan, q);
                return Response(pid, mode, ins, warnings, q, scan, 0);
            }

            return UndoGroup.Run(doc, d => Write(d, q, pid, ins, warnings));
        }

        /// <summary>Escritura: re-escanea dentro de la transacción (R1: un solo paso de deshacer) y cambia solo lo planificado.</summary>
        private static object Write(Document doc, Query q, int pid, int ins, List<string> baseWarnings)
        {
            Database db = doc.Database;
            var warnings = new List<string>(baseWarnings);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ScanResult scan = Scan(db, tr, q);
                CheckMax(scan, q);
                int escritas = 0;
                foreach (Row row in scan.Cambios)
                {
                    if (row.NuevoRaw == null)
                    {
                        continue;
                    }

                    DBObject obj = tr.GetObject(row.Id, OpenMode.ForWrite);
                    // AttributeReference hereda de DBText: su caso va primero.
                    switch (obj)
                    {
                        case AttributeReference attr:
                            attr.TextString = row.NuevoRaw;
                            break;
                        case DBText dt:
                            dt.TextString = row.NuevoRaw;
                            break;
                        case MText mt:
                            mt.Contents = row.NuevoRaw;
                            break;
                        default:
                            throw ToolException.InvalidParams("el handle " + row.Handle + " ya no es un texto editable");
                    }

                    escritas++;
                }

                tr.Commit();
                return Response(pid, CleanWrite.Mode.Write, ins, warnings, q, scan, escritas);
            }
        }

        /// <summary>Un lote de más de max_entidades cambios se rechaza en ambos modos (con el número real).</summary>
        private static void CheckMax(ScanResult scan, Query q)
        {
            if (scan.Cambios.Count > q.MaxEntidades)
            {
                throw ToolException.InvalidParams(
                    "la llamada cambiaría " + scan.Cambios.Count + " entidades y el máximo es " + q.MaxEntidades
                    + " (acote con capas, bbox o espacio, o suba max_entidades)",
                    new Dictionary<string, object?>
                    {
                        ["n_entidades"] = scan.Cambios.Count,
                        ["max_entidades"] = q.MaxEntidades,
                    });
            }
        }

        private static Dictionary<string, object?> Response(
            int pid, CleanWrite.Mode mode, int ins, List<string> warnings, Query q, ScanResult scan, int escritas)
        {
            if (scan.Multilinea > 0)
            {
                warnings.Add(scan.Multilinea + " atributo(s) multilínea (MTEXT) no se tratan.");
            }

            if (scan.CapasBloqueadas.Count > 0)
            {
                warnings.Add("Coincidencias en capas bloqueadas omitidas (capas: " + Join(scan.CapasBloqueadas) + ").");
            }

            if (scan.CapasXref.Count > 0)
            {
                warnings.Add("Coincidencias en capas de xref omitidas (capas: " + Join(scan.CapasXref) + ").");
            }

            if (scan.InvisiblesCambian > 0)
            {
                warnings.Add(scan.InvisiblesCambian + " atributo(s) invisible(s) incluido(s) en el cambio (marcados invisible: true).");
            }

            if (scan.Ocultas > 0)
            {
                warnings.Add(scan.Ocultas + " coincidencia(s) en capas congeladas o apagadas (se modifican igualmente).");
            }

            Dictionary<string, object?> d = CleanWrite.Response(pid, mode, ins, warnings);
            d["espacio"] = q.Espacio ?? (scan.Espacios.Count > 0 ? scan.Espacios[0] : "");
            d["espacios"] = scan.Espacios;
            d["n_revisadas"] = scan.Revisadas;
            d["n_coincidencias"] = scan.Coincidencias;
            d["n_entidades"] = scan.Cambios.Count;
            d["n_reemplazos"] = scan.Reemplazos;
            d["n_omitidas"] = scan.Omitidas.Count;
            d["n_escritas"] = escritas;
            d["max_entidades"] = q.MaxEntidades;
            d["n_listadas"] = Math.Min(scan.Cambios.Count, MaxListed);
            d["cambios_truncados"] = scan.Cambios.Count > MaxListed;
            d["omitidas_truncadas"] = scan.Omitidas.Count > MaxListed;
            d["cambios"] = ListRows(scan.Cambios, changed: true);
            d["omitidas"] = ListRows(scan.Omitidas, changed: false);
            d["origen_texto"] = OrigenTexto;
            return d;
        }

        private static List<Dictionary<string, object?>> ListRows(List<Row> rows, bool changed)
        {
            var list = new List<Dictionary<string, object?>>();
            for (int i = 0; i < rows.Count && i < MaxListed; i++)
            {
                Row r = rows[i];
                var item = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["handle"] = r.Handle,
                    ["tipo"] = r.Tipo,
                    ["capa"] = r.Capa,
                    ["n"] = r.N,
                };
                if (changed)
                {
                    item["antes"] = r.Antes;
                    item["despues"] = r.Despues;
                }
                else
                {
                    item["motivo"] = r.Motivo;
                }

                if (r.Tag != null)
                {
                    item["tag"] = r.Tag;
                    item["invisible"] = r.Invisible;
                }

                list.Add(item);
            }

            return list;
        }

        /// <summary>Espacios a tratar: «papel» = todos los layouts, «todos» = modelo + layouts, si no el espacio pedido (o el actual).</summary>
        private static List<SpaceResolver.ResolvedSpace> ResolveSpaces(Database db, Transaction tr, string? espacio)
        {
            var list = new List<SpaceResolver.ResolvedSpace>();
            bool papel = string.Equals(espacio, "papel", StringComparison.OrdinalIgnoreCase);
            bool todos = string.Equals(espacio, "todos", StringComparison.OrdinalIgnoreCase);
            if (!papel && !todos)
            {
                list.Add(SpaceResolver.Resolve(db, tr, espacio));
                return list;
            }

            if (todos)
            {
                list.Add(SpaceResolver.Resolve(db, tr, SpaceResolver.ModelName));
            }

            var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                if (entry.Value.IsNull || entry.Value.IsErased)
                {
                    continue;
                }

                if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout && !layout.ModelType)
                {
                    list.Add(SpaceResolver.Resolve(db, tr, layout.LayoutName));
                }
            }

            return list;
        }

        private static ScanResult Scan(Database db, Transaction tr, Query q)
        {
            var res = new ScanResult();
            var layers = new Dictionary<ObjectId, LayerInfo>();
            var seen = new HashSet<ObjectId>();
            foreach (SpaceResolver.ResolvedSpace space in ResolveSpaces(db, tr, q.Espacio))
            {
                res.Espacios.Add(space.Name);
                if (!seen.Add(space.Btr.ObjectId))
                {
                    continue;
                }

                foreach (ObjectId id in space.Btr)
                {
                    ScanId(tr, id, q, res, layers);
                }
            }

            return res;
        }

        private static void ScanId(Transaction tr, ObjectId id, Query q, ScanResult res, Dictionary<ObjectId, LayerInfo> layers)
        {
            try
            {
                if (id.IsNull || id.IsErased)
                {
                    return;
                }

                string? quick = AcadUtil.DxfNameFromId(id);
                if (quick != null && quick != TypeText && quick != TypeMText && quick != TypeInsert)
                {
                    return;
                }

                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                {
                    return;
                }

                string tipo = quick ?? AcadUtil.DxfName(ent);
                if (tipo == TypeText && q.Tipos.Contains(TypeText) && ent is DBText t)
                {
                    Visit(tr, ent, t.TextString, TextFlavor.Dbtext, TypeText, q, res, layers, null, null);
                }
                else if (tipo == TypeMText && q.Tipos.Contains(TypeMText) && ent is MText mt)
                {
                    Visit(tr, ent, SafeContents(mt), TextFlavor.Mtext, TypeMText, q, res, layers, null, null);
                }
                else if (tipo == TypeInsert && q.Tipos.Contains(TypeAttrib) && ent is BlockReference br)
                {
                    // La capa del INSERT manda sobre sus atributos: en capa bloqueada o xref, todos se omiten.
                    LayerInfo contenedor = LayerOf(tr, br, layers);
                    foreach (AttributeReference attr in EntityFormatter.Attributes(br, tr))
                    {
                        try
                        {
                            if (attr.IsMTextAttribute)
                            {
                                res.Multilinea++;
                                continue;
                            }

                            Visit(tr, attr, attr.TextString, TextFlavor.Dbtext, TypeAttrib, q, res, layers, attr, contenedor);
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            // Atributo ilegible: se omite.
                        }
                    }
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Entidad ilegible: se omite y se sigue.
            }
        }

        private static void Visit(
            Transaction tr, Entity ent, string raw, TextFlavor flavor, string tipo, Query q, ScanResult res,
            Dictionary<ObjectId, LayerInfo> layers, AttributeReference? attr, LayerInfo? contenedor)
        {
            LayerInfo own = LayerOf(tr, ent, layers);
            LayerInfo li = own;
            string capaAviso = own.Name;
            if (contenedor != null)
            {
                // Un atributo hereda el estado de la capa de su INSERT (bloqueada, xref, congelada o apagada).
                li = new LayerInfo
                {
                    Name = own.Name,
                    Locked = own.Locked || contenedor.Locked,
                    Xref = own.Xref || contenedor.Xref,
                    Hidden = own.Hidden || contenedor.Hidden,
                };
                if (contenedor.Locked || contenedor.Xref)
                {
                    capaAviso = contenedor.Name;
                }
            }

            if (!EntityFilter.MatchesAny(li.Name, q.Capas))
            {
                return;
            }

            if (q.Bbox.HasValue)
            {
                BBox2? box = AcadUtil.TryBBox2(ent);
                if (box == null || !box.Value.Intersects(q.Bbox.Value))
                {
                    return;
                }
            }

            res.Revisadas++;
            ReplaceResult plan = TextReplacePlanner.Plan(raw, flavor, q.Buscar, q.Reemplazar, q.IgnoreCase, q.PalabraCompleta);
            if (plan.Outcome == ReplaceOutcome.SinCoincidencias)
            {
                return;
            }

            res.Coincidencias += plan.Coincidencias;
            var row = new Row
            {
                Id = ent.ObjectId,
                Handle = AcadUtil.HandleToString(ent.Handle),
                Tipo = tipo,
                Capa = li.Name,
                Antes = plan.VisibleAntes,
                Despues = plan.VisibleDespues,
                N = plan.Reemplazos,
                NuevoRaw = plan.NuevoRaw,
                Tag = attr?.Tag,
                Invisible = attr != null && SafeInvisible(attr),
            };

            // Campos (FIELD): reemplazar el texto de un campo lo convierte en texto muerto. Se omite entero.
            if (ent.HasFields)
            {
                row.NuevoRaw = null;
                row.Motivo = "contiene campos (no se modifica)";
                row.N = plan.Coincidencias;
                row.Despues = null;
                res.Omitidas.Add(row);
                return;
            }

            if (plan.Outcome == ReplaceOutcome.Omitida)
            {
                row.Motivo = plan.Error ?? string.Join("; ", plan.Motivos);
                row.N = plan.Coincidencias;
                row.Despues = null;
                res.Omitidas.Add(row);
                return;
            }

            if (plan.NuevoRaw == null || string.Equals(plan.NuevoRaw, raw, StringComparison.Ordinal))
            {
                return;
            }

            if (li.Xref || li.Locked)
            {
                row.NuevoRaw = null;
                row.Motivo = li.Xref ? "capa de xref (no se modifica)" : "capa bloqueada (no se modifica)";
                row.N = plan.Coincidencias;
                row.Despues = null;
                res.Omitidas.Add(row);
                List<string> bucket = li.Xref ? res.CapasXref : res.CapasBloqueadas;
                if (!bucket.Contains(capaAviso))
                {
                    bucket.Add(capaAviso);
                }

                return;
            }

            if (li.Hidden)
            {
                res.Ocultas += plan.Reemplazos;
            }

            if (row.Invisible)
            {
                res.InvisiblesCambian++;
            }

            if (plan.Omitidas > 0)
            {
                res.Omitidas.Add(new Row
                {
                    Id = ent.ObjectId,
                    Handle = row.Handle,
                    Tipo = tipo,
                    Capa = li.Name,
                    N = plan.Omitidas,
                    Motivo = "coincidencia(s) parcialmente omitida(s): " + string.Join("; ", plan.Motivos),
                });
            }

            res.Cambios.Add(row);
            res.Reemplazos += plan.Reemplazos;
        }

        private static LayerInfo LayerOf(Transaction tr, Entity ent, Dictionary<ObjectId, LayerInfo> cache)
        {
            ObjectId lid = ent.LayerId;
            if (cache.TryGetValue(lid, out LayerInfo? hit))
            {
                return hit;
            }

            string name = ent.Layer ?? "";
            bool xref = name.IndexOf('|') >= 0;
            var info = new LayerInfo { Name = name, Xref = xref };
            if (!lid.IsNull && tr.GetObject(lid, OpenMode.ForRead, false) is LayerTableRecord layer)
            {
                info.Name = layer.Name;
                info.Locked = layer.IsLocked;
                info.Xref = xref || layer.IsDependent || layer.Name.IndexOf('|') >= 0;
                info.Hidden = layer.IsFrozen || layer.IsOff;
            }

            cache[lid] = info;
            return info;
        }

        private static bool SafeInvisible(AttributeReference attr)
        {
            try
            {
                return attr.Invisible;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return false;
            }
        }

        private static string SafeContents(MText mt)
        {
            try
            {
                return mt.Contents ?? "";
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return "";
            }
        }

        private static string Join(List<string> items)
        {
            return string.Join(", ", items.GetRange(0, Math.Min(items.Count, 5))) + (items.Count > 5 ? ", …" : "");
        }
    }
}
