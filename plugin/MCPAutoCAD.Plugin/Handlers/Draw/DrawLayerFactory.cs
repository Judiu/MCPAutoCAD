using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Audit;
using MCPAutoCAD.GeometryCore.Draw;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Audit;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// Capa destino de las escrituras de Fase 3 (protocol.md §5D.0, regla R3). Solo hilo principal, dentro de la
    /// transacción de escritura (la capa creada forma parte del mismo paso de deshacer). Resolución pura en
    /// <see cref="LayerResolver"/>; aquí solo se consulta/crea en la LayerTable.
    /// Rechazos (INVALID_PARAMS, data.detalle exacto): "capa bloqueada", "capa congelada", "capa dependiente de xref".
    /// Apagada → se escribe con advertencia. Inexistente y no creable → NOT_FOUND (data.capa, data.detalle).
    /// </summary>
    internal static class DrawLayerFactory
    {
        public const string LockedLayer = "capa bloqueada";
        public const string FrozenLayer = "capa congelada";
        public const string XrefLayer = "capa dependiente de xref";
        public const string NotCreatable = "no está en lps.json (capas_permitidas/capas_mcp) ni empieza por MCP-: solo se crean capas del estándar";

        /// <summary>Resultado de <see cref="Ensure"/>.</summary>
        internal sealed class Result
        {
            public Result(ObjectId id, string nombre, bool creada, string origen, string? concepto)
            {
                Id = id;
                Nombre = nombre;
                Creada = creada;
                Origen = origen;
                Concepto = concepto;
            }

            public ObjectId Id { get; }

            /// <summary>Nombre real de la capa (el del dibujo si existía, con sus mayúsculas).</summary>
            public string Nombre { get; }

            public bool Creada { get; }

            /// <summary>"concepto" | "nombre" | "defecto" (<see cref="LayerPlan"/>).</summary>
            public string Origen { get; }

            public string? Concepto { get; }
        }

        /// <summary>true si el nombre es de una capa del puente (MCP-*, sin distinguir mayúsculas).</summary>
        public static bool IsMcpName(string? nombre) =>
            nombre != null && nombre.StartsWith(LayerResolver.McpPrefix, System.StringComparison.OrdinalIgnoreCase);

        /// <summary>Plan puro para el parámetro 'capa' (se puede llamar en ValidateParams: no toca AutoCAD).</summary>
        public static LayerPlan Plan(string? capa, string? conceptoPorDefecto)
        {
            try
            {
                return LayerResolver.Resolve(capa, conceptoPorDefecto, AuditSupport.LayerRules);
            }
            catch (ArgumentException ex)
            {
                throw ToolException.InvalidParams(ex.Message);
            }
        }

        /// <summary>
        /// Capa lista para escribir según R3. <paramref name="capa"/> = parámetro del cliente (null si no se dio);
        /// <paramref name="conceptoPorDefecto"/> = concepto de la herramienta ("cotas", "achurados") o null (MCP-DIBUJO).
        /// Crea la capa en <paramref name="tr"/> si no existe y es creable.
        /// </summary>
        public static Result Ensure(Database db, Transaction tr, string? capa, string? conceptoPorDefecto, List<string> advertencias)
        {
            LayerPlan plan = Plan(capa, conceptoPorDefecto);
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            ObjectId id = McpLayerFactory.FindLayer(table, plan.Nombre);
            if (!id.IsNull)
            {
                var existing = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                RequireWritable(existing, advertencias);
                return new Result(id, existing.Name, false, plan.Origen, plan.Concepto);
            }

            if (!plan.Creable)
            {
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "La capa '" + plan.Nombre + "' no existe en el dibujo.",
                    new Dictionary<string, object?> { ["capa"] = plan.Nombre, ["detalle"] = NotCreatable });
            }

            try
            {
                SymbolUtilityServices.ValidateSymbolName(plan.Nombre, false);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                throw ToolException.InvalidParams(LayerResolver.InvalidName, new Dictionary<string, object?> { ["capa"] = plan.Nombre });
            }

            LayerSpec spec = plan.Especificacion!;
            var ltr = new LayerTableRecord
            {
                Name = plan.Nombre,
                Color = Color.FromColorIndex(ColorMethod.ByAci, (short)Math.Clamp(spec.Color, 1, 255)),
                LinetypeObjectId = ResolveLinetype(db, tr, spec.TipoLinea, plan.Nombre, advertencias),
                LineWeight = Enum.IsDefined(typeof(LineWeight), spec.Grosor) ? (LineWeight)spec.Grosor : LineWeight.ByLineWeightDefault,
                IsPlottable = spec.Plot,
            };
            table.UpgradeOpen();
            id = table.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return new Result(id, ltr.Name, true, plan.Origen, plan.Concepto);
        }

        /// <summary>
        /// Las mismas comprobaciones que <see cref="Ensure"/> pero SIN escribir (lectura en <paramref name="tr"/>, que
        /// puede ser una StartOpenCloseTransaction previa al UndoGroup): capa existente escribible (R3.5), o inexistente
        /// y creable con nombre de símbolo válido. Las advertencias van a <paramref name="advertencias"/> si se pasa (previsualización); si no, se descartan (Ensure las vuelve a añadir al escribir).
        /// Devuelve el plan y si la capa ya existe en el dibujo. Para escrituras que hacen algo irreversible antes de
        /// Ensure (la importación de insertar_bloque).
        /// </summary>
        public static LayerPlan Check(Database db, Transaction tr, string? capa, string? conceptoPorDefecto, out bool existe, List<string>? advertencias = null)
        {
            LayerPlan plan = Plan(capa, conceptoPorDefecto);
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            ObjectId id = McpLayerFactory.FindLayer(table, plan.Nombre);
            existe = !id.IsNull;
            if (existe)
            {
                RequireWritable((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead), advertencias ?? new List<string>());
                return plan;
            }

            if (!plan.Creable)
            {
                throw new ToolException(
                    ErrorCodes.NotFound,
                    "La capa '" + plan.Nombre + "' no existe en el dibujo.",
                    new Dictionary<string, object?> { ["capa"] = plan.Nombre, ["detalle"] = NotCreatable });
            }

            try
            {
                SymbolUtilityServices.ValidateSymbolName(plan.Nombre, false);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                throw ToolException.InvalidParams(LayerResolver.InvalidName, new Dictionary<string, object?> { ["capa"] = plan.Nombre });
            }

            return plan;
        }

        /// <summary>R3: xref → INVALID_PARAMS; bloqueada → INVALID_PARAMS; congelada → INVALID_PARAMS; apagada → advertencia.</summary>
        public static void RequireWritable(LayerTableRecord layer, List<string> advertencias)
        {
            var data = new Dictionary<string, object?> { ["capa"] = layer.Name };
            if (layer.IsDependent || layer.Name.IndexOf('|') >= 0)
            {
                throw ToolException.InvalidParams(XrefLayer, data);
            }

            if (layer.IsLocked)
            {
                throw ToolException.InvalidParams(LockedLayer, data);
            }

            if (layer.IsFrozen)
            {
                throw ToolException.InvalidParams(FrozenLayer, data);
            }

            if (layer.IsOff)
            {
                advertencias.Add("La capa " + layer.Name + " está apagada: las entidades no serán visibles.");
            }
        }

        /// <summary>Tipo de línea cargado con ese nombre (sin mayúsculas) o Continuous con advertencia si no está cargado.</summary>
        private static ObjectId ResolveLinetype(Database db, Transaction tr, string? name, string layer, List<string> advertencias)
        {
            if (string.IsNullOrEmpty(name) || string.Equals(name, "Continuous", StringComparison.OrdinalIgnoreCase))
            {
                return db.ContinuousLinetype;
            }

            try
            {
                var lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                if (lt.Has(name))
                {
                    ObjectId id = lt[name];
                    if (!id.IsNull && !id.IsErased)
                    {
                        return id;
                    }
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Nombre inválido o tabla ilegible: Continuous.
            }

            advertencias.Add("La capa " + layer + " se creó con tipo de línea Continuous: '" + name + "' no está cargado en el dibujo.");
            return db.ContinuousLinetype;
        }
    }
}
