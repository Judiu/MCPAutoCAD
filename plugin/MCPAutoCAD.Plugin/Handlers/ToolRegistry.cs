using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// Registro de herramientas por descubrimiento: toda clase concreta del ensamblado que implementa
    /// <see cref="IToolHandler"/> con constructor público sin parámetros (salvo <see cref="ManifestHandler"/>) se
    /// instancia y se publica en el orden del contrato (<see cref="ManifestOrder"/>, protocol.md §4). Así un handler
    /// nuevo se registra solo al existir. Más el método tools/manifest, que no aparece como herramienta.
    /// </summary>
    internal sealed class ToolRegistry
    {
        public const string ManifestMethod = "tools/manifest";

        /// <summary>
        /// Orden del manifest en protocolo 0.8 (protocol.md §4): §5, §5B, §5C, §5D, §5E, §5F, §5G y §5H (56 herramientas).
        /// Tras las cuatro de la Fase 4B (§5G: vano, union_muros, cotas_automaticas, insertar_export) va al final
        /// auditar_archivo (Fase 5, §5H.1). Plugin 0.8.1 añade al final siete herramientas (63 en total); 0.8.2 añade borrar_excepto_seleccion (64); 0.8.4 añade cargar_lisp, ejecutar_comando y cancelar_comando (67); 0.8.5 añade crear_lisp (68); 0.9.0 (fase M) añade listar_layouts, crear_viewport, ajustar_viewport, renombrar_layout, ordenar_layouts, borrar_layout, copiar_layout, crear_layout y configurar_layout (77; detectar_planos se retiró por decisión del usuario); 0.10.0 añade cambiar_propiedades, reemplazar_texto, editar_cota, muros_desde_lineas, guardar, abrir_documento, activar_documento y estilo_cota (85); 0.10.1 añade insertar_imagen (86); 0.10.2, importar_geometria y nuevo_documento (88).
        /// </summary>
        private static readonly string[] ManifestOrder =
        {
            "ping", "estado", "listar_capas", "linea",
            "consultar_entidades", "propiedades", "textos", "bloques", "seleccion_actual", "xrefs",
            "vista_png", "resumen_archivo",
            "auditar_capas", "entidades_en_capa_0", "auditar_bloques", "areas_por_capa", "verificar_cuadro_areas",
            "duplicados", "textos_fuera_de_escala", "auditar_dibujo", "marcar_hallazgos", "limpiar_observaciones",
            "crear_capa", "polilinea", "circulo", "arco", "texto", "mtext", "insertar_bloque", "cota", "hatch",
            "mover", "copiar", "rotar", "escalar", "borrar",
            "punto_de", "distancia", "proyectar",
            "muro", "ejes_rejilla", "recinto", "offset", "array_rectangular", "array_polar",
            "confirmar", "descartar_preview",
            "borrar_fuera_de", "purgar", "normalizar_capas", "corregir_unidades_bloques",
            "vano", "union_muros", "cotas_automaticas", "insertar_export",
            "auditar_archivo",
            "guardar_copia", "editar_mtext", "cambiar_capa", "unir_lineas", "extender_linea", "zoom_a", "resaltar",
            "borrar_excepto_seleccion",
            "cargar_lisp", "ejecutar_comando", "cancelar_comando", "crear_lisp",
            "listar_layouts",
            "crear_viewport", "ajustar_viewport", "renombrar_layout", "ordenar_layouts", "borrar_layout", "copiar_layout",
            "crear_layout", "configurar_layout",
            "cambiar_propiedades", "reemplazar_texto", "editar_cota", "muros_desde_lineas",
            "guardar", "abrir_documento", "activar_documento", "estilo_cota",
            "insertar_imagen",
            "importar_geometria",
            "nuevo_documento",
        };

        public ToolRegistry(int pid)
        {
            Dictionary<string, IToolHandler> discovered = Discover();

            var tools = new List<IToolHandler>(discovered.Count);
            foreach (string name in ManifestOrder)
            {
                if (discovered.TryGetValue(name, out IToolHandler? handler))
                {
                    tools.Add(handler);
                    discovered.Remove(name);
                }
                else
                {
                    PluginLog.Warn("Herramienta '" + name + "' del contrato no está implementada en este ensamblado; se omite del manifest");
                }
            }

            foreach (string extra in discovered.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
            {
                PluginLog.Warn("Herramienta '" + extra + "' no figura en el orden del contrato; se publica al final");
                tools.Add(discovered[extra]);
            }

            Tools = tools;

            var byMethod = new Dictionary<string, IToolHandler>(StringComparer.Ordinal);
            foreach (IToolHandler tool in Tools)
            {
                byMethod.Add(tool.Name, tool);
            }

            byMethod.Add(ManifestMethod, new ManifestHandler(Tools, pid));
            ByMethod = byMethod;
        }

        /// <summary>Herramientas publicadas, en orden.</summary>
        public IReadOnlyList<IToolHandler> Tools { get; }

        /// <summary>Todos los métodos JSON-RPC (herramientas + tools/manifest). Nombres exactos, sensibles a mayúsculas.</summary>
        public Dictionary<string, IToolHandler> ByMethod { get; }

        /// <summary>Instancia los handlers del ensamblado, indexados por nombre. Un handler que lanza se omite.</summary>
        private static Dictionary<string, IToolHandler> Discover()
        {
            Type[] types;
            try
            {
                types = typeof(ToolRegistry).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                PluginLog.Error("No se pudieron cargar todos los tipos del plugin; se registran los disponibles", ex);
                types = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
            }

            var found = new Dictionary<string, IToolHandler>(StringComparer.Ordinal);
            foreach (Type type in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters
                    || !typeof(IToolHandler).IsAssignableFrom(type)
                    || type == typeof(ManifestHandler)
                    || type.GetConstructor(Type.EmptyTypes) == null)
                {
                    continue;
                }

                IToolHandler handler;
                string name;
                try
                {
                    handler = (IToolHandler)Activator.CreateInstance(type)!;
                    name = handler.Name;
                }
                catch (Exception ex)
                {
                    PluginLog.Error("No se pudo crear el handler " + type.FullName + "; se omite", ex);
                    continue;
                }

                if (string.IsNullOrEmpty(name) || string.Equals(name, ManifestMethod, StringComparison.Ordinal))
                {
                    PluginLog.Error("El handler " + type.FullName + " tiene un nombre inválido ('" + name + "'); se omite");
                    continue;
                }

                if (found.ContainsKey(name))
                {
                    PluginLog.Error("Herramienta '" + name + "' duplicada (" + type.FullName + "); se conserva la primera");
                    continue;
                }

                found.Add(name, handler);
            }

            return found;
        }
    }
}
