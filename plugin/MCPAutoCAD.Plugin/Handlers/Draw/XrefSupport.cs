using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.Plugin.Bridge;
using MCPAutoCAD.Plugin.Handlers.Documento;
using MCPAutoCAD.Plugin.Logging;
using AcRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// Utilidades de adjuntar_xref y gestionar_xref (fallo 37). Todo lo que toca AutoCAD va aquí para que los dos handlers
    /// usen las mismas reglas: ruta del dibujo guardado (DWGTITLED), búsqueda de definiciones que ignora las borradas (tras
    /// separar o un CTRL+Z una entrada borrada haría fallar bt[nombre] con eWasErased), comprobación del nombre como símbolo
    /// y detección de xrefs anidadas.
    /// </summary>
    internal static class XrefSupport
    {
        /// <summary>
        /// Ruta del dibujo activo si está guardado con nombre (DWGTITLED = 1). Un dibujo nuevo no tiene ruta propia: su
        /// db.Filename es la plantilla y no debe usarse como anfitrión. Null si no está guardado.
        /// </summary>
        public static string? HostFile(Document doc)
        {
            if (DocumentoSupport.SafeIntSysVar("DWGTITLED") != 1)
            {
                return null;
            }

            return DocumentoSupport.RutaDe(doc);
        }

        /// <summary>
        /// Id de la definición de bloque llamada <paramref name="name"/> (sin distinguir mayúsculas) que NO está borrada, o
        /// ObjectId.Null. Primero la búsqueda directa; si da una entrada borrada o falla, recorre la tabla.
        /// </summary>
        public static ObjectId FindBlockId(Database db, Transaction tr, string name)
        {
            var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            try
            {
                if (table.Has(name))
                {
                    ObjectId direct = table[name];
                    if (!direct.IsNull && !direct.IsErased)
                    {
                        return direct;
                    }
                }
            }
            catch (AcRuntimeException)
            {
                // Nombre que la tabla no acepta como clave: se busca recorriendo.
            }

            foreach (ObjectId candidate in table)
            {
                if (candidate.IsNull || candidate.IsErased)
                {
                    continue;
                }

                var btr = (BlockTableRecord)tr.GetObject(candidate, OpenMode.ForRead, false);
                if (string.Equals(btr.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return ObjectId.Null;
        }

        /// <summary>Null si <paramref name="nombre"/> es un nombre de símbolo válido en AutoCAD; si no, el detalle "nombre: ...".</summary>
        public static string? ErrorSimbolo(string nombre)
        {
            try
            {
                SymbolUtilityServices.ValidateSymbolName(nombre, false);
                return null;
            }
            catch (AcRuntimeException)
            {
                return "nombre: '" + nombre + "' no es un nombre de símbolo válido en AutoCAD";
            }
        }

        /// <summary>
        /// true si la referencia <paramref name="nombre"/> está anidada dentro de otra xref. Se lee del grafo de xrefs del
        /// dibujo activo. Si el grafo no se puede leer lanza INTERNAL: no se adivina.
        /// </summary>
        public static bool EsAnidada(Database db, string nombre)
        {
            try
            {
                XrefGraph grafo = db.GetHostDwgXrefGraph(true);
                for (int i = 0; i < grafo.NumNodes; i++)
                {
                    XrefGraphNode nodo = grafo.GetXrefNode(i);
                    if (nodo.IsNested && string.Equals(nodo.Name, nombre, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (AcRuntimeException ex)
            {
                throw new ToolException(
                    ErrorCodes.Internal,
                    "No se pudo comprobar si la referencia '" + nombre + "' está anidada.",
                    new Dictionary<string, object?> { ["detalle"] = "GetHostDwgXrefGraph: " + ex.ErrorStatus + ": " + ex.Message, ["nombre"] = nombre });
            }
        }

        /// <summary>INSUNITS del archivo de la referencia, leído en una base lateral que se descarta. Null si no se puede leer.</summary>
        public static int? LeerInsunits(string ruta)
        {
            try
            {
                using (var side = new Database(false, true))
                {
                    side.ReadDwgFile(ruta, FileOpenMode.OpenForReadAndAllShare, true, "");
                    return (int)side.Insunits;
                }
            }
            catch (Exception ex) when (ex is AcRuntimeException || ex is IOException || ex is UnauthorizedAccessException)
            {
                PluginLog.Warn("xref: no se pudo leer INSUNITS de '" + ruta + "' (" + ex.GetType().Name + ").");
                return null;
            }
        }

        /// <summary>Lectura de una propiedad que puede lanzar; null si falla.</summary>
        public static string? Safe(Func<string> read)
        {
            try
            {
                return read();
            }
            catch (AcRuntimeException)
            {
                return null;
            }
        }

        /// <summary>Diccionario de datos de error con un solo campo.</summary>
        public static Dictionary<string, object?> Extra(string campo, string valor) =>
            new Dictionary<string, object?> { [campo] = valor };

        /// <summary>INTERNAL con el detalle de AutoCAD (ErrorStatus) cuando falla una operación de xref.</summary>
        public static ToolException OperacionFallida(string operacion, string nombre, AcRuntimeException ex) =>
            new ToolException(
                ErrorCodes.Internal,
                "No se pudo " + operacion + " la referencia '" + nombre + "'.",
                new Dictionary<string, object?>
                {
                    ["detalle"] = operacion + ": " + ex.ErrorStatus + ": " + ex.Message,
                    ["nombre"] = nombre,
                });
    }
}
