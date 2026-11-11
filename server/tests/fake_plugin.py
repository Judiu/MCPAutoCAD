"""Plugin simulado en memoria que sigue protocol.md 0.8 / plugin 0.8.0 (framing real de 4 bytes LE).

Incluye las 56 herramientas del manifest (§4) y un dibujo ficticio pequeño para
las herramientas de lectura de Fase 1 (§5B), de auditoría de Fase 2 (§5C), de
dibujo primitivo de Fase 3 (§5D) y de geometría paramétrica de Fase 4A (§5E):

- 3 LINE en MUROS, 1 LWPOLYLINE cerrada en 0 (área 15.0),
- 2 INSERT del bloque DP (atributos ANCHO/TIPO) en PUERTAS,
- 1 TEXT "COCINA" y 1 MTEXT "BAÑO PRINCIPAL" en TEXTOS,
- 1 INSERT del xref TOPO en 0,
- Fase 2: 2 LINE idénticas a 1A0 (1B0, 1B1) en MUROS (duplicados), 4 TEXT de un
  cuadro de áreas mínimo en Rotulo (1C0–1C3) y 1 TEXT de altura 2.5 (1D0) fuera de escala.

Bloques: DP en milímetros (unidades_bloque 4, unidades_distintas) y
"BASURA_jgytfyuh" sin inserciones (nombres_sospechosos + sin_uso).
Las respuestas de Fase 2 son simulaciones con la FORMA de los ejemplos de §5C;
no reproducen todas las heurísticas del plugin real.

Correcciones 0.3.1 (gate de Fase 2, F1–F8) reflejadas aquí: vista_png con 'espacio'
y 'layout' (§5B.7), resumen de auditar_capas sobre todas las capas (§5C.1),
compensación por inserción en auditar_bloques (§5C.3), en_layout / reglas_evaluadas /
reglas_no_evaluables y dos pasadas de cuadros_discrepantes (§5C.5), handles de grupo
acotados a 50 en duplicados (§5C.6) y VIEWPORT conservados por limpiar_observaciones
(§5C.10). Los escenarios extra se activan con banderas de FakePluginState
(bloques_compensacion, cuadros_fuera_de_layout, duplicados_masivos,
viewport_observaciones) para no alterar el dibujo base.

Correcciones 0.3.2 (gate en vivo de 0.3.1: F5, F9 y bloque C): vista_png solo captura el espacio activo
(otro espacio → INVALID_PARAMS), marcar_hallazgos rechaza marcas fuera de la extensión del espacio destino
salvo 'forzar', y auditar_bloques mide inserciones anidadas (o 'media' no medible, nunca 'alta' sin medida).

Fase 3 (protocolo 0.4, §5D): las 14 herramientas de escritura (crear_capa … borrar) modifican el dibujo ficticio
en memoria (entidades con handles nuevos; mover/copiar/rotar/escalar/borrar sobre state.entities) con la FORMA de
las respuestas del contrato (R6) y reproducen sus errores: UNITS_UNKNOWN con insunits=0 (campo de
FakePluginState) salvo aceptar_sin_unidades, NOT_FOUND de capa no creable y de handle, INVALID_PARAMS de capa
bloqueada/congelada, de confirmar (§5D.8) y de ubicación (R4) salvo forzar. insertar_bloque importa LPS_ROTULO de
una biblioteca simulada (25 atributos reales; PLANO_N conserva el formato en línea). linea adopta R2–R5.
Capas y conceptos salen de standards/lps.json (solo lectura). Simula la forma, no la geometría exacta de AutoCAD.

Fase 4A (protocolo 0.5, §5E): punto_de/distancia/proyectar resuelven PuntoRef sobre el dibujo ficticio (coordenada,
inicio/fin/medio/vertice/centro de LINE, LWPOLYLINE, CIRCLE y ARC, intersección de rectas/segmentos/circunferencias,
con los mensajes exactos de §5E.1 e INVALID_GEOMETRY −32005); las seis generadoras (muro con un WallOutline
simplificado de caras paralelas e ingletes, ejes_rejilla, recinto con área "12,35 m²", offset, array_rectangular y
array_polar con la protección confirmar de §5D.8 y el tope de 500) responden con la forma G6, aplican G2 estricta
(UNITS_UNKNOWN con insunits=0, sin aceptar_sin_unidades) y R4 salvo forzar; preview: true registra la previsualización
en FakePluginState.previews (preview_id "prev-" + 12 hex, caducidad = reloj simulado state.ahora + 15 min, tope 20
por documento con desalojo, anclas con huella de su geometría) y, con dibujar_preview, la dibuja en MCP-PREVIEW;
confirmar/descartar_preview siguen §5E.5. state.pasos_deshacer cuenta los pasos de deshacer simulados de las
escrituras de Fase 4A (para comprobar la tabla de §5E.7); state.doc_key simula el documento activo.

Fase L (protocolo 0.6, §5F): borrar_fuera_de, purgar, normalizar_capas y corregir_unidades_bloques con la
previsualización SIN estado de L1 (sin confirmar: true nunca cambian el dibujo ni pasos_deshacer; preview: false sin
confirmar → INVALID_PARAMS exacto; confirmar + preview: true sigue siendo previsualización), un paso de deshacer
simulado por escritura, UNITS_UNKNOWN solo en escritura con insunits=0 y la cabecera L6. La bandera faseL sustituye el
dibujo base por uno propio (contorno 1C0, entidades dentro/fuera/cruza/sin caja, VIEWPORT 1C8 en Layout1, capas MUEBLE,
Layer7, CAPAVACIA con DASHED, CAPABLOQUE y DEFPOINTS, bloques 'erere' y BASURA_ANIDADO, estilos ROMANS y COTA_VIEJA,
regapp ACAD_PSEXT); purga_dinamica añade un bloque dinámico que la escritura conserva (advertencia de diferencia).
El criterio dentro/fuera es el de §5F.1 por caja; la purga es el grafo de referencias de la previsualización (la
escritura repite el grafo con los bloques dinámicos como raíces, como Database.Purge); corregir_unidades_bloques
reutiliza _bloques_auditoria (como el plugin reutiliza BloquesCheck).

Fase 4B (protocolo 0.7, §5G): vano, union_muros y cotas_automaticas son generadoras (_generar con entidades planificadas
BORRAR, INSERT y DIMENSION: la previsualización no borra, el definitivo y confirmar sí, y el muro lleva huella de ancla);
insertar_export es una escritura R1–R7 sin previsualización con exports simulados (FakePluginState.exports). La
bandera fase4b añade al dibujo base muros y contornos con los handles 2B0–2C0 (MUROS y MCP-DIBUJO, T, L, punto, sin
contacto, polilínea abierta, con arcos, contorno en L, sombreados asociativos) y el estilo de cota actual COTA75.
insertar_bloque admite propiedades_dinamicas sobre la biblioteca ampliada (§5G.6) y toda respuesta G6 lleva
handles_borrados. normalizar_capas admite conservar_aspecto (§5G.5). Lee biblioteca_vanos del lps.json real. La
geometría (WallOpening, WallJoin, DimensionChain, OpeningPlacement) es una versión simplificada de la de GeometryCore.

Fase 5 (protocolo 0.8, §5H): auditar_archivo responde la forma de §5H.1 para las rutas de FakePluginState.archivos
(dict ruta → datos simulados: insunits, measurement, capas_ajenas, bloques_sospechosos, n_capa_0, mediana, n_duplicados,
layouts, bloques_mas_usados; 'danado' simula un DWG ilegible). Valida la ruta como insertar_export con la clave
carpetas_auditoria_permitidas (FakePluginState.carpetas_auditoria) y, con archivos_por_nombre, busca por nombre de
archivo: el lote audita copias en una carpeta temporal cuya ruta no se conoce de antemano.
"""

from __future__ import annotations

import copy
import fnmatch
import json
import math
import ntpath
import re
import statistics
import struct
import threading
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

from mcp_autocad.errors import PipeBrokenError, PipeTimeoutError
from mcp_autocad.framing import FrameReader, encode_frame
from mcp_autocad.transport import Transport

FAKE_PROTOCOL_VERSION = "0.8"
FAKE_PLUGIN_VERSION = "0.8.0"

# PNG 1×1 válido (lo que devuelve vista_png del plugin simulado).
PNG_1X1_BASE64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="

# Advertencia LPS de ejemplo (texto exacto de protocol.md §2.2).
ADVERTENCIA_LPS_PULGADAS = "El dibujo está en pulgadas (INSUNITS=1); el estándar LPS es metros (INSUNITS=6)."

# --------------------------------------------------------------------------
# Schemas (JSON Schema estricto, additionalProperties: false)
# --------------------------------------------------------------------------

EMPTY_SCHEMA: dict[str, Any] = {"type": "object", "properties": {}, "additionalProperties": False}
POINT_SCHEMA: dict[str, Any] = {
    "type": "array",
    "items": {"type": "number"},
    "minItems": 2,
    "maxItems": 3,
}
BBOX_SCHEMA: dict[str, Any] = {
    "type": "array",
    "description": "Caja [[xmin, ymin], [xmax, ymax]] en WCS (z opcional e ignorado).",
    "items": POINT_SCHEMA,
    "minItems": 2,
    "maxItems": 2,
}
LIMIT_SCHEMA: dict[str, Any] = {"type": "integer", "minimum": 1, "maximum": 1000, "default": 200}
CURSOR_SCHEMA: dict[str, Any] = {"type": "string", "description": "Valor de cursor_siguiente de la página anterior."}
ESPACIO_SCHEMA: dict[str, Any] = {
    "type": "string",
    "minLength": 1,
    "description": "'modelo' o nombre de un layout. Defecto: espacio actual.",
}
CAPAS_FILTRO_SCHEMA: dict[str, Any] = {
    "type": "array",
    "items": {"type": "string", "minLength": 1},
    "minItems": 1,
    "maxItems": 100,
}

LINEA_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "lineas": {
            "type": "array",
            "minItems": 1,
            "maxItems": 100,
            "items": {
                "type": "object",
                "properties": {"inicio": POINT_SCHEMA, "fin": POINT_SCHEMA},
                "required": ["inicio", "fin"],
                "additionalProperties": False,
            },
        },
        "inicio": POINT_SCHEMA,
        "fin": POINT_SCHEMA,
        # Desde 0.4 (§5.4): capa con R3, espacio (R5), forzar (R4), aceptar_sin_unidades (R2); exigir_unidades obsoleto.
        "capa": {"type": "string", "minLength": 1, "maxLength": 255},
        "ucs": {"type": "boolean", "default": False},
        "exigir_unidades": {"type": "boolean", "default": False},
        "aceptar_sin_unidades": {"type": "boolean", "default": False},
        "espacio": {"type": "string", "minLength": 1, "maxLength": 255},
        "forzar": {"type": "boolean", "default": False},
    },
    "additionalProperties": False,
}
LISTAR_CAPAS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "limit": LIMIT_SCHEMA,
        "cursor": CURSOR_SCHEMA,
        "incluir_xref": {"type": "boolean", "default": False},
    },
    "additionalProperties": False,
}
CONSULTAR_ENTIDADES_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "espacio": ESPACIO_SCHEMA,
        "tipos": {
            "type": "array",
            "items": {"type": "string", "minLength": 1},
            "minItems": 1,
            "maxItems": 50,
        },
        "capas": CAPAS_FILTRO_SCHEMA,
        "bbox": BBOX_SCHEMA,
        "limit": LIMIT_SCHEMA,
        "cursor": CURSOR_SCHEMA,
        "resumen": {"type": "boolean", "default": False},
    },
    "additionalProperties": False,
}
PROPIEDADES_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {"handle": {"type": "string", "pattern": "^[0-9A-Fa-f]{1,16}$"}},
    "required": ["handle"],
    "additionalProperties": False,
}
TEXTOS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "espacio": ESPACIO_SCHEMA,
        "tipos": {
            "type": "array",
            "items": {"type": "string", "enum": ["TEXT", "MTEXT", "ATTRIB"]},
            "minItems": 1,
        },
        "capas": CAPAS_FILTRO_SCHEMA,
        "bbox": BBOX_SCHEMA,
        "contiene": {"type": "string", "minLength": 1, "maxLength": 200},
        "limit": LIMIT_SCHEMA,
        "cursor": CURSOR_SCHEMA,
    },
    "additionalProperties": False,
}
BLOQUES_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "nombre": {"type": "string", "minLength": 1, "maxLength": 255},
        "incluir_anonimos": {"type": "boolean", "default": False},
        "limit": LIMIT_SCHEMA,
        "cursor": CURSOR_SCHEMA,
    },
    "additionalProperties": False,
}
SELECCION_ACTUAL_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {"limit": LIMIT_SCHEMA, "cursor": CURSOR_SCHEMA},
    "additionalProperties": False,
}
XREFS_SCHEMA: dict[str, Any] = EMPTY_SCHEMA
# Descripciones exigidas por protocol.md §5B.7 desde 0.3.2 (texto literal del contrato; F5).
VISTA_PNG_DESCRIPCION = (
    "Devuelve una imagen PNG del dibujo activo sin alterar la vista del usuario ni el historial de deshacer. "
    "Sin bbox captura la vista actual en pantalla; con bbox [[xmin,ymin],[xmax,ymax]] (WCS) plotea esa zona "
    "ampliada al aspecto ancho:alto. Solo captura el espacio activo (la pestaña abierta por el usuario) y nunca "
    "cambia de pestaña: si 'espacio' indica otro, devuelve INVALID_PARAMS. Úsela para revisar visualmente; los "
    "números se verifican con las demás herramientas."
)
VISTA_PNG_MECANISMO_DESCRIPCION = (
    "auto (defecto): sin bbox, captura nativa de la pantalla (si falla, plot); con bbox, siempre plot. "
    "captura: solo la API gráfica (sin bbox). plot: PublishToWeb PNG, tamaños fijos del PC3 (ancho/alto "
    "reales pueden diferir). Siempre sobre el espacio activo."
)
VISTA_PNG_ESPACIO_DESCRIPCION = (
    "'modelo' o nombre de un layout; debe ser el espacio activo (AutoCAD solo plotea el layout actual y la "
    "herramienta no cambia de pestaña). Si es otro, INVALID_PARAMS: pida al usuario que cambie a esa pestaña "
    "u omita 'espacio'. Defecto: espacio activo."
)
VISTA_PNG_BBOX_DESCRIPCION = (
    "Ventana a capturar en WCS: [[xmin, ymin], [xmax, ymax]] (z opcional e ignorado). Sin bbox se captura "
    "la vista actual."
)
VISTA_PNG_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "bbox": {**BBOX_SCHEMA, "description": VISTA_PNG_BBOX_DESCRIPCION},
        "ancho": {"type": "integer", "minimum": 64, "maximum": 2048, "default": 1024},
        "alto": {"type": "integer", "minimum": 64, "maximum": 2048, "default": 768},
        "mecanismo": {
            "type": "string",
            "enum": ["auto", "captura", "plot"],
            "default": "auto",
            "description": VISTA_PNG_MECANISMO_DESCRIPCION,
        },
        "espacio": {
            "type": "string",
            "minLength": 1,
            "maxLength": 255,
            "description": VISTA_PNG_ESPACIO_DESCRIPCION,
        },
    },
    "additionalProperties": False,
}
RESUMEN_ARCHIVO_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {"ruta": {"type": "string", "minLength": 1, "maxLength": 32767}},
    "required": ["ruta"],
    "additionalProperties": False,
}

# -- Fase 2 (§5C) --------------------------------------------------------------

# Clasificaciones de capa de §5C.1 (orden de evaluación).
CLASIFICACIONES = [
    "mcp",
    "estandar",
    "estandar_con_diferencias",
    "cype",
    "ajena_revit_aia",
    "ajena_exportacion",
    "generica",
    "fuera_de_estandar",
]
# Orden de las claves de resumen.por_clasificacion (ejemplo de §5C.1).
POR_CLASIFICACION_ORDER = [
    "estandar",
    "estandar_con_diferencias",
    "cype",
    "mcp",
    "ajena_revit_aia",
    "ajena_exportacion",
    "generica",
    "fuera_de_estandar",
]
# Reglas de auditar_dibujo en el orden fijo de la tabla de §5C.8.
REGLAS_AUDITORIA = [
    "auditar_capas",
    "entidades_en_capa_0",
    "auditar_bloques",
    "verificar_cuadro_areas",
    "duplicados",
    "textos_fuera_de_escala",
    "xrefs_no_resueltas",
    "unidades",
]
SECCIONES_BLOQUES = ["unidades_distintas", "nombres_sospechosos", "sin_uso"]
TIPOS_DUPLICADOS = ["LINE", "LWPOLYLINE", "CIRCLE", "ARC", "INSERT", "TEXT", "MTEXT"]
SEVERIDADES = ["alta", "media", "baja"]
CLAVES_HALLAZGO = ["id", "regla", "severidad", "mensaje", "handles", "bbox", "espacio", "datos"]
# Claves válidas de verificar_cuadro_areas.contornos (§5C.5, desviación aceptada en el gate de Fase 2):
# solo las etiquetas que son áreas; TITULO, OCUPACION y CONSTRUCCION no se admiten.
ETIQUETA_CONTORNO_RE = r"^(LOTE|LIBRE|TOTAL CONSTRUIDO|REMATE PUNTO FIJO|AREA PISO [1-9][0-9]{0,2})$"
# Reglas internas de §5C.5, en el orden de reglas_evaluadas.
REGLAS_CUADRO = ["suma_pisos", "libre", "ocupacion", "construccion"]
SUFIJO_FUERA_DE_LAYOUT = " (cuadro fuera de los layouts: posible versión anterior)"
CAPA_OBSERVACIONES = "MCP-OBSERVACIONES"

HANDLE_SCHEMA: dict[str, Any] = {"type": "string", "pattern": "^[0-9A-Fa-f]{1,16}$"}
BBOX2D_SCHEMA: dict[str, Any] = {
    "type": "array",
    "description": "Caja [[xmin, ymin], [xmax, ymax]] en WCS; una caja de tamaño cero se trata como punto.",
    "items": POINT_SCHEMA,
    "minItems": 2,
    "maxItems": 2,
}

AUDITAR_CAPAS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "clasificacion": {
            "type": "array",
            "items": {"type": "string", "enum": CLASIFICACIONES},
            "minItems": 1,
            "maxItems": 9,
            "description": "Filtra las capas devueltas por clasificación.",
        },
        "incluir_vacias": {"type": "boolean", "default": True},
        "limit": LIMIT_SCHEMA,
        "cursor": CURSOR_SCHEMA,
    },
    "additionalProperties": False,
}
ENTIDADES_EN_CAPA_0_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "espacio": {
            "type": "string",
            "minLength": 1,
            "description": "'modelo' o nombre de un layout. Defecto: todos los espacios.",
        },
        "limit": LIMIT_SCHEMA,
        "cursor": CURSOR_SCHEMA,
    },
    "additionalProperties": False,
}
AUDITAR_BLOQUES_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "seccion": {
            "type": "array",
            "items": {"type": "string", "enum": SECCIONES_BLOQUES},
            "minItems": 1,
            "maxItems": 3,
        },
        "limit": LIMIT_SCHEMA,
        "cursor": CURSOR_SCHEMA,
    },
    "additionalProperties": False,
}
AREAS_POR_CAPA_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "capas": CAPAS_FILTRO_SCHEMA,
        "espacio": ESPACIO_SCHEMA,
        "detalle": {"type": "boolean", "default": False},
        "limit": LIMIT_SCHEMA,
        "cursor": {**CURSOR_SCHEMA, "description": "Solo con detalle: true."},
    },
    "additionalProperties": False,
}
VERIFICAR_CUADRO_AREAS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "espacio": ESPACIO_SCHEMA,
        "bbox": BBOX_SCHEMA,
        "contornos": {
            "type": "object",
            "description": "Etiqueta del cuadro → handle de la entidad cerrada cuyo área se compara con el valor declarado.",
            "minProperties": 1,
            "maxProperties": 20,
            "propertyNames": {"pattern": ETIQUETA_CONTORNO_RE},
            "additionalProperties": HANDLE_SCHEMA,
        },
    },
    "additionalProperties": False,
}
DUPLICADOS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "espacio": ESPACIO_SCHEMA,
        "capas": CAPAS_FILTRO_SCHEMA,
        "tipos": {
            "type": "array",
            "items": {"type": "string", "enum": TIPOS_DUPLICADOS},
            "minItems": 1,
            "maxItems": len(TIPOS_DUPLICADOS),
        },
        "tolerancia": {"type": "number", "minimum": 1e-12, "maximum": 1, "default": 1e-6},
        "limit": LIMIT_SCHEMA,
        "cursor": CURSOR_SCHEMA,
    },
    "additionalProperties": False,
}
TEXTOS_FUERA_DE_ESCALA_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {"espacio": ESPACIO_SCHEMA, "limit": LIMIT_SCHEMA, "cursor": CURSOR_SCHEMA},
    "additionalProperties": False,
}
AUDITAR_DIBUJO_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "espacio": ESPACIO_SCHEMA,
        "reglas": {
            "type": "array",
            "items": {"type": "string", "enum": REGLAS_AUDITORIA},
            "minItems": 1,
            "maxItems": 8,
        },
        "limit": LIMIT_SCHEMA,
        "cursor": CURSOR_SCHEMA,
    },
    "additionalProperties": False,
}
MARCAR_HALLAZGOS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "marcas": {
            "type": "array",
            "minItems": 1,
            "maxItems": 100,
            "items": {
                "type": "object",
                "properties": {
                    "bbox": BBOX2D_SCHEMA,
                    "texto": {"type": "string", "minLength": 1, "maxLength": 500},
                    "severidad": {"type": "string", "enum": SEVERIDADES},
                },
                "required": ["bbox", "texto"],
                "additionalProperties": False,
            },
        },
        "capa": {
            "type": "string",
            "default": CAPA_OBSERVACIONES,
            "description": "Capa de las marcas; debe empezar por MCP-.",
        },
        "espacio": ESPACIO_SCHEMA,
        "altura_texto": {"type": "number", "exclusiveMinimum": 0, "default": 0.2},
        "forzar": {
            "type": "boolean",
            "default": False,
            "description": "true = escribir aunque alguna marca quede fuera de la extensión del espacio destino.",
        },
    },
    "required": ["marcas"],
    "additionalProperties": False,
}
LIMPIAR_OBSERVACIONES_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "confirmar": {"type": "boolean", "description": "Obligatorio y debe ser true."},
        "capa": {
            "type": "string",
            "default": CAPA_OBSERVACIONES,
            "description": "Capa MCP-* cuyas entidades se borran (la capa se conserva).",
        },
    },
    "additionalProperties": False,
}

# -- Fase 3 (§5D, dibujo primitivo; protocolo 0.4) -----------------------------

# Las 14 herramientas de escritura de Fase 3 en el orden de protocol.md §4.
HERRAMIENTAS_FASE3 = [
    "crear_capa",
    "polilinea",
    "circulo",
    "arco",
    "texto",
    "mtext",
    "insertar_bloque",
    "cota",
    "hatch",
    "mover",
    "copiar",
    "rotar",
    "escalar",
    "borrar",
]
HERRAMIENTAS_MODIFICACION = ["mover", "copiar", "rotar", "escalar", "borrar"]

# Copia de standards/lps.json (solo lectura): el simulador resuelve conceptos y capas creables como R3 (§5D.0).
LPS_JSON = Path(__file__).resolve().parents[2] / "standards" / "lps.json"


def _cargar_lps() -> dict[str, Any]:
    try:
        data = json.loads(LPS_JSON.read_text(encoding="utf-8"))
        if isinstance(data, dict):
            return data
    except (OSError, ValueError):
        pass
    # Respaldo mínimo si el repositorio no trae el estándar.
    return {
        "capas_preferidas": {"cotas": "Cotas", "achurados": "Achurados", "rotulo": "Rotulo", "muros": "MUROS"},
        "capas_permitidas": {
            "Cotas": {"color": 8, "tipo_linea": "Continuous", "grosor": 13, "plot": 1},
            "Achurados": {"color": 151, "tipo_linea": "Continuous", "grosor": 9, "plot": 1},
        },
        "capas_mcp": {"MCP-DIBUJO": {"color": 3, "tipo_linea": "Continuous", "grosor": -3, "plot": 1}},
    }


LPS = _cargar_lps()

# --- patron_ajeno / sugerencia_patron de auditar_capas (§5C.1, §5F.5), como GeometryCore/Audit/LayerClassifier ---
# Réplica de LayerClassifier.ForeignPattern, Suggest y SuggestForPattern, con la prioridad de
# lps.json.mapa_normalizacion del estándar v2 (2026-10-28) leída como LpsLayerRules.FromJson.
_RE_REVIT_AIA = re.compile(r"^[A-Z]-[A-Z0-9]{3,4}(-|$)")
_RE_GENERICA = re.compile(r"^(Layer|Capa)\d+$", re.IGNORECASE)
_TABLA_SUGERENCIA: list[tuple[tuple[str, ...], str]] = [
    (("MURO",), "muros"),
    (("COTA",), "cotas"),
    (("TEXT",), "textos"),
    (("EJE",), "ejes"),
    (("PUERTA",), "puertas"),
    (("VENTANA",), "ventanas"),
    (("ACHUR", "HATCH"), "achurados"),
    (("COLUMN",), "columnas"),
    (("VIGA",), "vigas"),
    (("NIVEL",), "niveles"),
    (("ESCALER",), "escaleras"),
    (("SANIT",), "sanitarios"),
    (("MUEBLE", "MOBIL"), "mobiliario_fijo"),
]
_CODIGOS_AIA: dict[str, str] = {
    "WALL": "muros", "COLS": "columnas", "COLM": "columnas", "DOOR": "puertas", "GLAZ": "ventanas",
    "WIND": "ventanas", "FURN": "mobiliario_fijo", "CASE": "mobiliario_fijo", "SANR": "sanitarios",
    "PLMB": "sanitarios", "FIXT": "sanitarios", "DIMS": "cotas", "TEXT": "textos", "ANNO": "textos",
    "IDEN": "textos", "GRID": "ejes", "BEAM": "vigas", "STRS": "escaleras", "PATT": "achurados",
    "HATCH": "achurados", "LEVL": "niveles", "ELEV": "niveles",
}


def _sin_acentos(texto: str) -> str:
    import unicodedata

    descompuesto = unicodedata.normalize("NFD", texto)
    return unicodedata.normalize("NFC", "".join(c for c in descompuesto if unicodedata.category(c) != "Mn"))


def _preferidas(lps: dict[str, Any]) -> dict[str, str]:
    prefs = lps.get("capas_preferidas") or {}
    return {k: v for k, v in prefs.items() if isinstance(v, str) and v}


def _mapa_normalizacion(lps: dict[str, Any]) -> dict[str, str]:
    """LpsLayerRules.MapaNormalizacion: clave casefold; sin pares mal formados ni de origen permitido/MCP."""
    mapa = lps.get("mapa_normalizacion")
    if not isinstance(mapa, dict):
        return {}
    protegidas = {k.casefold() for sec in ("capas_permitidas", "capas_mcp") for k in (lps.get(sec) or {})}
    prefs = _preferidas(lps)
    out: dict[str, str] = {}
    for origen, destino in mapa.items():
        clave = origen.casefold()
        if not origen.strip() or not isinstance(destino, str) or not destino.strip():
            continue
        if clave in out or clave in protegidas:
            continue
        concepto = next((k for k in prefs if k.casefold() == destino.casefold()), None)
        out[clave] = prefs[concepto] if concepto is not None else destino
    return out


def patron_ajeno(nombre: str) -> str | None:
    """LayerClassifier.ForeignPattern: "revit_aia" | "exportacion" | "generica" | None según el nombre."""
    if not nombre:
        return None
    upper = nombre.upper()
    lower = nombre.lower()
    if _RE_REVIT_AIA.match(upper):
        return "revit_aia"
    if (
        "_pen_no__" in lower
        or "_pluma_n" in lower
        or upper.startswith("PDF_")
        or (len(upper) > 3 and upper.startswith("PDF") and "0" <= upper[3] <= "9")
        or lower.startswith("dibujo 2d - general")
        or lower.startswith("2d drafting - general")
        or lower.startswith("3d ")
    ):
        return "exportacion"
    if _RE_GENERICA.match(nombre):
        return "generica"
    return None


def sugerencia_por_palabra(nombre: str, lps: dict[str, Any] | None = None) -> str | None:
    """LayerClassifier.Suggest: capa preferida por la primera palabra clave de la tabla de §5C.1."""
    prefs = _preferidas(LPS if lps is None else lps)
    if not nombre or not prefs:
        return None
    clave = _sin_acentos(nombre.upper())
    for palabras, concepto in _TABLA_SUGERENCIA:
        for palabra in palabras:
            if palabra in clave:
                if concepto in prefs:
                    return prefs[concepto]
                break
    return None


def sugerencia_por_patron(nombre: str, lps: dict[str, Any] | None = None) -> str | None:
    """LayerClassifier.SuggestForPattern: palabra clave y, si no, códigos AIA por campo ('-' y '_')."""
    lps = LPS if lps is None else lps
    if not nombre:
        return None
    por_palabra = sugerencia_por_palabra(nombre, lps)
    if por_palabra is not None:
        return por_palabra
    prefs = _preferidas(lps)
    for campo in re.split(r"[-_]", _sin_acentos(nombre.upper())):
        concepto = _CODIGOS_AIA.get(campo.strip())
        if concepto is not None and concepto in prefs:
            return prefs[concepto]
    return None


def campos_patron(nombre: str, lps: dict[str, Any] | None = None) -> tuple[str | None, str | None]:
    """(patron_ajeno, sugerencia_patron) como LayerClassifier.Classify: el destino de mapa_normalizacion gana."""
    lps = LPS if lps is None else lps
    patron = patron_ajeno(nombre)
    if patron is None:
        return None, None
    decidida = _mapa_normalizacion(lps).get(nombre.casefold())
    return patron, decidida if decidida is not None else sugerencia_por_patron(nombre, lps)

# Textos fijos de §5D.0 (R2, R3) y §5D.2/§5D.4.
ADVERTENCIA_INSUNITS_0 = (
    "INSUNITS=0: el dibujo no declara unidades; los valores se devuelven en unidades de dibujo sin interpretar."
)
ADVERTENCIA_LPS_SIN_UNIDADES = "El dibujo no declara unidades (INSUNITS=0); el estándar LPS es metros (INSUNITS=6)."
ADVERTENCIA_ACEPTAR_SIN_UNIDADES = (
    "aceptar_sin_unidades: true: se dibujó en un dibujo sin unidades declaradas (INSUNITS=0); las coordenadas se "
    "tomaron tal cual."
)
ADVERTENCIA_Z_IGNORADA = "vertices: la componente z se ignoró (LWPOLYLINE en el plano XY)."
ADVERTENCIA_SALTOS_TEXTO = (
    "texto: los saltos de línea se sustituyeron por espacios (TEXT es de una sola línea); use mtext."
)
DETALLE_CAPA_NO_CREABLE = (
    "no está en lps.json (capas_permitidas/capas_mcp) ni empieza por MCP-: solo se crean capas del estándar"
)
DETALLE_CREAR_CAPA_NO_ESTANDAR = "nombre: solo se crean capas de lps.json (capas_permitidas/capas_mcp) o MCP-*"
DETALLE_NOMBRE_CAPA_INVALIDO = "capa: nombre de capa no válido"
DETALLE_BLOQUE_CON_RUTA = "nombre: solo se admite el nombre del bloque, sin rutas"
DETALLE_ESCALA_BLOQUE = "escala: debe ser un número distinto de 0 o un array [sx, sy] / [sx, sy, sz]"
ADVERTENCIA_PLANO_N = "PLANO_N: no se reconoció el formato en línea; se escribió el valor sin formato."
# Caracteres que AutoCAD no admite en nombres de símbolo ('|' se trata aparte: capas de xref).
CARACTERES_NO_VALIDOS_CAPA = set('<>/\\":;?*=,`')
# Tipos de línea cargados en el dibujo ficticio (sin mayúsculas).
TIPOS_LINEA_CARGADOS = {"continuous", "bylayer", "byblock", "center"}
PATRONES_SOMBREADO = {"SOLID", "ANSI31", "ANSI32", "ANSI37", "AR-CONC", "AR-SAND", "NET", "DOTS"}
MCP_DEFECTO = {"color": 3, "tipo_linea": "Continuous", "grosor": -3, "plot": 1}

# Biblioteca de bloques (§5D.5): carpeta biblioteca\ junto a la DLL instalada. Ruta ficticia del simulador.
BIBLIOTECA_CARPETA = "C:\\Users\\usuario\\AppData\\Roaming\\Autodesk\\ApplicationPlugins\\MCPAutoCAD.bundle\\Contents\\biblioteca"
# Contenido real del ATTDEF PLANO_N de standards/LPS_ROTULO.dwg (leído con ODA + ezdxf el 2026-10-26).
PLANO_N_CONTENIDO = "\\A1;{\\pqc;\\fArial|b1|i0|c0|p34;1\\fArial|b1|i0|c0|p34;\\H0.500000x; de}"
# Atributos de LPS_ROTULO en el orden de la definición (tag, multilínea, valor por defecto). Etiquetas y
# multilínea son los del archivo real; los valores por defecto del simulador son neutros (sin nombres de personas).
LPS_ROTULO_ATRIBUTOS: list[tuple[str, bool, str]] = [
    ("MUNICIPIO", True, "MUNICIPIO"),
    ("PROYECTISTA", True, "NOMBRE DEL PROYECTISTA"),
    ("FECHA", True, "MES DE AAAA"),
    ("ESCALA", True, "INDICADA"),
    ("PLANO_N", True, PLANO_N_CONTENIDO),
    ("PLANO_TOTAL", True, "1"),
    ("VERSION", True, "VERSIÓN 1"),
    ("DIGITALIZO", True, "NOMBRE DE QUIEN DIGITALIZÓ"),
    ("CONTIENE_1", False, "PLANTA PRIMER PISO"),
    ("CONTIENE_2", False, "PLANTA SEGUNDO PISO"),
    ("CONTIENE_3", False, "PLANTA TERCER PISO"),
    ("CONTIENE_4", False, "CORTE"),
    ("CONTIENE_5", False, "FACHADA"),
    ("CONTIENE_6", False, "LOCALIZACIÓN"),
    ("CONTIENE_8", False, "CUADRO DE AREAS"),
    ("PROYECTO", False, "VIVIENDA UNIFAMILIAR"),
    ("CONTIENE_7", False, "PERFIL VIAL"),
    ("DISENADOR_NE", True, "NOMBRE DEL DISEÑADOR"),
    ("UBICACION", False, "DIRECCION DEL PREDIO"),
    ("CODIGO_CATASTRAL", False, "0000000000000000000000000"),
    ("VIS", False, "NO (V.I.S.)"),
    ("CC_2", False, ""),
    ("PROPIETARIO_2", False, ""),
    ("CC_1", False, "C.C. 0.000.000"),
    ("PROPIETARIO_1", False, "NOMBRE PROPIETARIO 1"),
]
LPS_ROTULO_TAGS = [t for t, _m, _v in LPS_ROTULO_ATRIBUTOS]
# Capas que trae LPS_ROTULO.dwg, §5D.5. Rótulo v2 (2026-10-31, tools/normalizar_capas_rotulo.py): solo 0 (logo) y
# Rotulo (el resto del contenido, con color/tipo de línea/grosor de su capa de origen fijados en cada entidad). La v1
# traía 3D EJES ESTRUCTURALES_Pen_No__7, 3D LINEAS DE PLANTA_Pen_No__7, Cotas y Dibujo 2D - General_Pen_No__102/142.
LPS_ROTULO_CAPAS = ["0", "Rotulo"]
BIBLIOTECA_BLOQUES: dict[str, dict[str, Any]] = {
    "LPS_ROTULO": {
        "nombre": "LPS_ROTULO",
        "unidades_bloque": 6,
        "tamano": [0.109, 0.666],
        "n_entidades": 175,
        "capas": LPS_ROTULO_CAPAS,
        "atributos": LPS_ROTULO_ATRIBUTOS,
    },
    # Fase 4B (§5G.6): los cuatro bloques extraídos del 6X12, INSUNITS 6. Los nombres de las propiedades dinámicas son
    # los medidos en el gate de Fase 4B (0.7).
    "Puerta Dinamica LM": {
        "nombre": "Puerta Dinamica LM",
        "unidades_bloque": 6,
        "tamano": [0.9, 0.961],
        "n_entidades": 10,
        "capas": ["PUERTAS"],
        "atributos": [],
        "dinamico": True,
        "propiedades": [
            {
                "nombre": "DistanceGeneralVano",
                "valor": 0.9,
                "solo_lectura": False,
                "unidades": "distancia",
                "valores_permitidos": None,
                "descripcion": "Ancho total",
            },
            {
                "nombre": "DistanceHojaDoble",
                "valor": 0.9,
                "solo_lectura": False,
                "unidades": "distancia",
                "valores_permitidos": None,
                "descripcion": "Hoja doble",
            },
            {
                "nombre": "Visibilidad1",
                "valor": "Abierta",
                "solo_lectura": False,
                "unidades": "ninguna",
                "valores_permitidos": ["Abierta", "Cerrada"],
                "descripcion": "Estado de visibilidad",
            },
            {
                "nombre": "Origin",
                "valor": "0,0,0",
                "solo_lectura": True,
                "unidades": "ninguna",
                "valores_permitidos": None,
                "descripcion": "Origen del bloque",
            },
        ],
    },
    "PuertaBaño": {
        "nombre": "PuertaBaño",
        "unidades_bloque": 6,
        "tamano": [0.65, 0.61],
        "n_entidades": 20,
        "capas": ["0"],
        "atributos": [],
    },
    "VENTANA01": {
        "nombre": "VENTANA01",
        "unidades_bloque": 6,
        "tamano": [0.12, 0.3],
        "n_entidades": 8,
        "capas": ["PUERTAS", "ventanas"],
        "atributos": [],
        "dinamico": True,
        "propiedades": [
            {
                "nombre": "Distance1",
                "valor": 0.3045,
                "solo_lectura": False,
                "unidades": "distancia",
                "valores_permitidos": None,
                "descripcion": "Longitud a lo largo del vano",
            },
        ],
    },
    "TANQUE 1000 LTS": {
        "nombre": "TANQUE 1000 LTS",
        "unidades_bloque": 6,
        "tamano": [1.39, 1.48],
        "n_entidades": 19,
        "capas": ["AC"],
        "atributos": [],
    },
}
# Atributos (tag, multilínea, defecto) y tamaño de los bloques que ya existen en el dibujo ficticio.
ATRIBUTOS_BLOQUES_DIBUJO: dict[str, list[tuple[str, bool, str]]] = {
    "DP": [("ANCHO", False, "0.90"), ("TIPO", False, "P1")],
}
TAMANO_BLOQUES_DIBUJO: dict[str, list[float]] = {"DP": [900.0, 900.0], "BASURA_JGYTFYUH": [1.0, 1.0]}

# PlanoNFormat (GeometryCore.Draw) en Python: mismo patrón que el plugin.
PLANO_N_RE = re.compile(
    r"^(?P<pre>(?:\\[A-Za-z][^;\\{}]*;)*)\{(?P<codes>(?:\\[A-Za-z][^;]*;)+?)(?P<num>(?:\\\\|\\\{|\\\}|[^\\{}])*)"
    r"(?P<rest>\\f[^;]*;\\H[^;]*;\s*de\s*\})$"
)
PLANO_N_SUFIJO_RE = re.compile(r"^(?P<n>.*?)(?:\s+|(?<=\d))de$", re.IGNORECASE | re.DOTALL)

# Descripciones de los parámetros comunes (texto de DrawWrite.Schema* del plugin, §5D.0).
DRAW_CAPA_DESCRIPCION = (
    "Capa destino: un concepto de lps.json (muros, cotas, textos, ejes, columnas, vigas, puertas, ventanas, filos, "
    "achurados, mobiliario_fijo, sanitarios, estructura, mamposteria, niveles, escaleras, proyeccion, rotulo), el "
    "nombre de una capa existente, o una capa del estándar LPS / MCP-* que se crea si falta. Otro nombre inexistente "
    "→ NOT_FOUND. Bloqueada, congelada o de xref → INVALID_PARAMS."
)
DRAW_ESPACIO_DESCRIPCION = (
    "'modelo' o nombre de un layout. Por defecto, el espacio activo. Nunca cambia la pestaña activa ni la vista."
)
DRAW_FORZAR_DESCRIPCION = (
    "true = escribir aunque la geometría quede fuera de la extensión (ampliada un 10 %) del espacio destino; por "
    "defecto se rechaza con INVALID_PARAMS sin escribir nada."
)
DRAW_ACEPTAR_DESCRIPCION = (
    "true = dibujar aunque el dibujo no declare unidades (INSUNITS=0); por defecto se rechaza con UNITS_UNKNOWN."
)
CAPA_DIBUJO_SCHEMA: dict[str, Any] = {"type": "string", "minLength": 1, "maxLength": 255, "description": DRAW_CAPA_DESCRIPCION}
ESPACIO_DIBUJO_SCHEMA: dict[str, Any] = {
    "type": "string",
    "minLength": 1,
    "maxLength": 255,
    "description": DRAW_ESPACIO_DESCRIPCION,
}
FORZAR_SCHEMA: dict[str, Any] = {"type": "boolean", "default": False, "description": DRAW_FORZAR_DESCRIPCION}
ACEPTAR_SIN_UNIDADES_SCHEMA: dict[str, Any] = {"type": "boolean", "default": False, "description": DRAW_ACEPTAR_DESCRIPCION}
COMUNES_DIBUJO: dict[str, Any] = {
    "capa": CAPA_DIBUJO_SCHEMA,
    "espacio": ESPACIO_DIBUJO_SCHEMA,
    "forzar": FORZAR_SCHEMA,
    "aceptar_sin_unidades": ACEPTAR_SIN_UNIDADES_SCHEMA,
}
COMUNES_MODIFICACION: dict[str, Any] = {
    "handles": {
        "type": "array",
        "items": HANDLE_SCHEMA,
        "minItems": 1,
        "maxItems": 500,
        "description": "Handles de las entidades (modelo o un mismo layout), sin repetidos.",
    },
    "confirmar": {
        "type": "boolean",
        "default": False,
        "description": "true = modificar también entidades que no están en capas MCP-* (entidades del usuario).",
    },
    "forzar": FORZAR_SCHEMA,
    "aceptar_sin_unidades": ACEPTAR_SIN_UNIDADES_SCHEMA,
}
GRADOS_SCHEMA: dict[str, Any] = {"type": "number", "description": "Grados, antihorario desde +X."}
POSITIVO_SCHEMA: dict[str, Any] = {"type": "number", "exclusiveMinimum": 0}
ESTILO_SCHEMA: dict[str, Any] = {"type": "string", "minLength": 1, "maxLength": 255}
TEXTO_USUARIO_SCHEMA: dict[str, Any] = {"type": "string", "minLength": 1, "maxLength": 2000}
JUSTIFICACIONES = ["izquierda", "centro", "derecha", "medio"]
ADJUNTOS = [
    "superior_izquierda",
    "superior_centro",
    "superior_derecha",
    "medio_izquierda",
    "medio_centro",
    "medio_derecha",
    "inferior_izquierda",
    "inferior_centro",
    "inferior_derecha",
]


def _schema(props: dict[str, Any], required: list[str], comunes: dict[str, Any] | None = None) -> dict[str, Any]:
    schema: dict[str, Any] = {"type": "object", "properties": {**props, **(comunes or {})}}
    if required:
        schema["required"] = required
    schema["additionalProperties"] = False
    return schema


CREAR_CAPA_SCHEMA = _schema(
    {
        "nombre": {
            "type": "string",
            "minLength": 1,
            "maxLength": 255,
            "description": "Concepto de lps.json (p. ej. 'cotas'), nombre de capas_permitidas/capas_mcp o nombre MCP-*.",
        }
    },
    ["nombre"],
)
POLILINEA_SCHEMA = _schema(
    {
        "vertices": {"type": "array", "items": POINT_SCHEMA, "minItems": 2, "maxItems": 1000},
        "cerrada": {"type": "boolean", "default": False},
        "bulges": {"type": "array", "items": {"type": "number"}, "maxItems": 1000},
        "ancho": {"type": "number", "minimum": 0, "default": 0},
    },
    ["vertices"],
    COMUNES_DIBUJO,
)
CIRCULO_SCHEMA = _schema({"centro": POINT_SCHEMA, "radio": POSITIVO_SCHEMA}, ["centro", "radio"], COMUNES_DIBUJO)
ARCO_SCHEMA = _schema(
    {"centro": POINT_SCHEMA, "radio": POSITIVO_SCHEMA, "angulo_inicio": GRADOS_SCHEMA, "angulo_fin": GRADOS_SCHEMA},
    ["centro", "radio", "angulo_inicio", "angulo_fin"],
    COMUNES_DIBUJO,
)
TEXTO_SCHEMA = _schema(
    {
        "posicion": POINT_SCHEMA,
        "texto": TEXTO_USUARIO_SCHEMA,
        "altura": POSITIVO_SCHEMA,
        "rotacion": {**GRADOS_SCHEMA, "default": 0},
        "justificacion": {"type": "string", "enum": JUSTIFICACIONES, "default": "izquierda"},
        "estilo": ESTILO_SCHEMA,
    },
    ["posicion", "texto", "altura"],
    COMUNES_DIBUJO,
)
MTEXT_SCHEMA = _schema(
    {
        "posicion": POINT_SCHEMA,
        "texto": TEXTO_USUARIO_SCHEMA,
        "altura": POSITIVO_SCHEMA,
        "ancho": {"type": "number", "minimum": 0, "default": 0},
        "rotacion": {**GRADOS_SCHEMA, "default": 0},
        "adjunto": {"type": "string", "enum": ADJUNTOS, "default": "superior_izquierda"},
        "estilo": ESTILO_SCHEMA,
    },
    ["posicion", "texto", "altura"],
    COMUNES_DIBUJO,
)
INSERTAR_BLOQUE_SCHEMA = _schema(
    {
        "nombre": {"type": "string", "minLength": 1, "maxLength": 255, "description": "Nombre del bloque, sin rutas."},
        "posicion": POINT_SCHEMA,
        "escala": {
            "oneOf": [
                {"type": "number"},
                {"type": "array", "items": {"type": "number"}, "minItems": 2, "maxItems": 3},
            ],
            "default": 1,
        },
        "rotacion": {**GRADOS_SCHEMA, "default": 0},
        "atributos": {
            "type": "object",
            "minProperties": 1,
            "maxProperties": 100,
            "additionalProperties": {"type": "string", "maxLength": 500},
            "description": "Valores por etiqueta (sin distinguir mayúsculas). PLANO_N de LPS_ROTULO: solo el número.",
        },
        "desde_biblioteca": {"type": "boolean", "default": False},
        "escala_unidades": {"type": "string", "enum": ["auto", "ninguna"], "default": "auto"},
        "propiedades_dinamicas": {
            "type": "object",
            "minProperties": 1,
            "maxProperties": 20,
            "additionalProperties": {"oneOf": [{"type": "number"}, {"type": "string", "maxLength": 500}]},
            "description": (
                "Propiedades del bloque dinámico: nombre → número o cadena (sin distinguir mayúsculas); se aplican tras "
                "crear la referencia. Con un bloque no dinámico o una propiedad desconocida o de solo lectura → "
                "INVALID_PARAMS."
            ),
        },
    },
    ["nombre", "posicion"],
    COMUNES_DIBUJO,
)
COTA_SCHEMA = _schema(
    {
        "tipo": {"type": "string", "enum": ["lineal", "alineada"]},
        "p1": POINT_SCHEMA,
        "p2": POINT_SCHEMA,
        "posicion_linea": POINT_SCHEMA,
        "rotacion": GRADOS_SCHEMA,
        "estilo": ESTILO_SCHEMA,
        "texto_sustituto": {"type": "string", "minLength": 1, "maxLength": 500},
    },
    ["tipo", "p1", "p2", "posicion_linea"],
    COMUNES_DIBUJO,
)
HATCH_SCHEMA = _schema(
    {
        "contorno": {
            "type": "object",
            "properties": {
                "handles": {"type": "array", "items": HANDLE_SCHEMA, "minItems": 1, "maxItems": 50},
                "vertices": {"type": "array", "items": POINT_SCHEMA, "minItems": 3, "maxItems": 1000},
            },
            "additionalProperties": False,
            "description": "Exactamente uno: handles de entidades cerradas (asociativo) o vertices de un polígono.",
        },
        "patron": {"type": "string", "minLength": 1, "maxLength": 255},
        "escala": {"type": "number", "exclusiveMinimum": 0, "default": 1},
        "angulo": {**GRADOS_SCHEMA, "default": 0},
    },
    ["contorno", "patron"],
    COMUNES_DIBUJO,
)
DESPLAZAMIENTO_SCHEMA: dict[str, Any] = {**POINT_SCHEMA, "description": "[dx, dy] o [dx, dy, dz] en unidades del dibujo."}
MOVER_SCHEMA = _schema({"desplazamiento": DESPLAZAMIENTO_SCHEMA}, ["handles", "desplazamiento"], COMUNES_MODIFICACION)
COPIAR_SCHEMA = _schema({"desplazamiento": DESPLAZAMIENTO_SCHEMA}, ["handles", "desplazamiento"], COMUNES_MODIFICACION)
ROTAR_SCHEMA = _schema({"base": POINT_SCHEMA, "angulo": GRADOS_SCHEMA}, ["handles", "base", "angulo"], COMUNES_MODIFICACION)
ESCALAR_SCHEMA = _schema({"base": POINT_SCHEMA, "factor": POSITIVO_SCHEMA}, ["handles", "base", "factor"], COMUNES_MODIFICACION)
BORRAR_SCHEMA = _schema({}, ["handles"], COMUNES_MODIFICACION)


# -- Fase 4A (§5E, geometría paramétrica; protocolo 0.5) ------------------------

# Las 11 herramientas de Fase 4A en el orden de protocol.md §4: 3 lecturas, 6 generadoras y 2 de previsualización.
HERRAMIENTAS_LECTURA_4A = ["punto_de", "distancia", "proyectar"]
HERRAMIENTAS_GENERADORAS = ["muro", "ejes_rejilla", "recinto", "offset", "array_rectangular", "array_polar"]
HERRAMIENTAS_PREVIEW = ["confirmar", "descartar_preview"]
HERRAMIENTAS_FASE4A = HERRAMIENTAS_LECTURA_4A + HERRAMIENTAS_GENERADORAS + HERRAMIENTAS_PREVIEW

# Almacén de previsualizaciones (§5E.5; PreviewRegistry.DefaultTtl / DefaultMaxPerDoc).
CAPA_PREVIEW = "MCP-PREVIEW"
PREVIEW_TTL = timedelta(minutes=15)
PREVIEW_MAX_POR_DOC = 20
PREVIEW_ID_RE = re.compile(r"^prev-[0-9a-f]{12}$")
# Reloj simulado inicial (las pruebas lo avanzan con state.ahora += timedelta(...)).
RELOJ_INICIAL = datetime(2026, 10, 26, 10, 0, 0, tzinfo=timezone(timedelta(hours=-5)))
# Tope de copias de array_rectangular / array_polar (ArrayLayout.MaxCopies).
MAX_COPIAS_ARRAY = 500
# Claves de la respuesta común G6 (§5E.0) en el orden del contrato.
CLAVES_G6 = [
    "pid",
    "preview",
    "preview_id",
    "preview_dibujada",
    "caduca",
    "handles",
    "handles_borrados",
    "entidades",
    "capa",
    "capas",
    "capas_creadas",
    "espacio",
    "puntos_resueltos",
    "numeros",
    "insunits",
    "unidades",
    "advertencias",
]

# Descripciones del manifest, texto LITERAL de protocol.md §5E.6.
DESCRIPCIONES_FASE4A: dict[str, str] = {
    "punto_de": (
        "LEE un punto del dibujo: resuelve un PuntoRef ([x, y], {handle, punto: inicio|fin|medio|centro|vertice, "
        "indice}, o {handle, interseccion_con, cerca_de}) a coordenadas WCS. No modifica nada."
    ),
    "distancia": (
        "LEE la distancia entre dos PuntoRef (coordenadas o puntos de entidades existentes): distancia 3D y en "
        "planta, dx, dy, dz y ángulo en grados. No modifica nada."
    ),
    "proyectar": (
        "LEE el punto más cercano de una curva existente (handle) a un PuntoRef: punto proyectado, distancia, "
        "parámetro y distancia desde el inicio de la curva. No modifica nada."
    ),
    "muro": (
        "ESCRIBE un muro arquitectónico como polilínea cerrada (contorno con esquinas a inglete) en la capa MUROS a "
        "partir de su eje (PuntoRef[] o handle de línea/polilínea), espesor y justificación (centro|izquierda|"
        "derecha); achurado opcional en Achurados. Rechaza espesores mayores que los tramos, ejes autointersecados "
        "e ingletes imposibles. Con preview: true solo calcula y devuelve números + preview_id (dibujado en "
        "MCP-PREVIEW). No une con muros existentes."
    ),
    "ejes_rejilla": (
        "ESCRIBE una rejilla de ejes estructurales en la capa EJES: líneas con burbujas tangentes al extremo y "
        "rótulos centrados (TEXT), a partir de un origen (PuntoRef), listas de separaciones en x e y (no "
        "uniformes), extensión, radio de burbuja y altura de texto obligatoria. Rótulos por defecto 1, 2, 3… (x) y "
        "A, B, C… (y). Con preview: true solo calcula y devuelve números + preview_id."
    ),
    "recinto": (
        "ESCRIBE un recinto: polilínea cerrada (si se dan vértices PuntoRef[]) o solo la etiqueta (si se da el "
        "handle de una polilínea cerrada existente) + MTEXT centrado con el nombre y el área en m² con coma "
        "decimal, en un punto interior del polígono. Valida cierre, autointersección y área > 0. Con preview: true "
        "solo calcula y devuelve números + preview_id."
    ),
    "offset": (
        "ESCRIBE una curva paralela (offset) a una entidad existente (handle) a una distancia, del lado indicado "
        "(PuntoRef o izquierda|derecha), en la capa de la original o la indicada. Con preview: true solo calcula y "
        "devuelve números + preview_id."
    ),
    "array_rectangular": (
        "ESCRIBE copias independientes de entidades existentes (handles) en una retícula de filas × columnas con "
        "separaciones y ángulo opcional (no es una matriz asociativa). Tope 500 copias; entidades fuera de capas "
        "MCP-* exigen confirmar: true. Con preview: true solo calcula y devuelve números + preview_id."
    ),
    "array_polar": (
        "ESCRIBE copias independientes de entidades existentes (handles) alrededor de un centro (PuntoRef): n "
        "posiciones en un ángulo total (360 = vuelta completa), con o sin rotación de los elementos. Tope 500 "
        "copias; entidades fuera de capas MCP-* exigen confirmar: true. Con preview: true solo calcula y devuelve "
        "números + preview_id."
    ),
    "confirmar": (
        "ESCRIBE la geometría definitiva de una previsualización (preview_id): en un solo paso de deshacer borra "
        "las entidades de MCP-PREVIEW que sigan existiendo y recalcula y dibuja la geometría en sus capas "
        "definitivas. Falla sin escribir si el preview_id caducó (15 min), es de otro documento o sus entidades "
        "ancla cambiaron."
    ),
    "descartar_preview": (
        "BORRA las entidades de MCP-PREVIEW de una previsualización (preview_id) y la olvida, en un solo paso de "
        "deshacer (nada si ya no queda ninguna). Las previsualizaciones caducadas o de sesiones anteriores se "
        "limpian con limpiar_observaciones y capa MCP-PREVIEW."
    ),
}


# Mensajes del contrato (PreviewRegistry / ToolException, §5E.5 y §6), textos exactos.
def mensaje_preview_no_encontrada(preview_id: str) -> str:
    return (
        f"No existe ninguna previsualización con id '{preview_id}' (caducada, descartada, confirmada o de otra sesión)."
    )


def detalle_preview_otro_documento(preview_id: str) -> str:
    return (
        f"preview_id: la previsualización '{preview_id}' pertenece a otro documento; active ese documento o vuelva "
        "a previsualizar"
    )


def detalle_capa_heredada(handle: str, estado: str, capa: str) -> str:
    """offset sin 'capa' sobre un original en capa no escribible (R3): §5E.3.4."""
    base = f"capa: la curva {handle} está en la capa {estado} '{capa}' y la paralela heredaría esa capa (regla R3)"
    if estado == "bloqueada":
        return f"{base}; indique capa con otra capa de destino o desbloquee '{capa}'"
    if estado == "congelada":
        return f"{base}; indique capa con otra capa de destino o descongele '{capa}'"
    return f"{base}; indique capa con otra capa de destino"


# Estilo de texto de los rótulos de eje de la oficina (AxisLabelStyle.OfficeDefault; TEXT 859F0 de copia_real.dwg).
# TODO del plugin: moverlo a lps.json como "estilos_texto": {"ejes": "75"} cuando se autorice cambiar standards/.
ESTILO_EJES_OFICINA = "75"
ESTILO_EJES_DESCRIPCION = (
    "Estilo de texto existente de los rótulos. Por defecto el de la oficina, '75', si existe en el dibujo; si no, "
    "el actual (con advertencia)."
)


def advertencia_estilo_ejes(preferido: str, actual: str) -> str:
    """ejes_rejilla sin 'estilo' cuando el estilo de la oficina no existe: §5E.3.2 (AxisLabelStyle.MissingWarning)."""
    return (
        f"ejes_rejilla: el estilo de texto '{preferido}' de los rótulos de eje de la oficina no existe en el dibujo; "
        f"los rótulos usan el estilo actual '{actual}' (indique estilo para otro)."
    )


def _fmt_g(v: float) -> str:
    """Formato "0.######" de C# (hasta 6 decimales, sin ceros finales)."""
    s = f"{abs(v):.6f}".rstrip("0").rstrip(".")
    return s if v >= 0 else "-" + s


def detalle_offset_lado(distancia: float, motivo: str) -> str:
    """offset cuyo lado pedido no es construible (O3, 2026-10-27): §5E.3.4 (OffsetChoice.WrongSideDetail)."""
    return f"lado: no se puede construir la paralela a {_fmt_g(distancia)} del lado pedido ({motivo})"


def _proyectar_segmento(p: tuple[float, float], a: tuple[float, float], b: tuple[float, float]) -> tuple[float, float]:
    """Punto más cercano a p del segmento ab."""
    dx, dy = b[0] - a[0], b[1] - a[1]
    l2 = dx * dx + dy * dy
    t = 0.0 if l2 <= 0 else max(0.0, min(1.0, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / l2))
    return (a[0] + t * dx, a[1] + t * dy)


def advertencia_eje_duplicados_colapsados(handle: str, n: int) -> str:
    """muro con eje por handle con vértices consecutivos duplicados: §5E.3.1."""
    return f"eje: la polilínea {handle} tiene {n} vértice(s) consecutivo(s) duplicado(s); se ignoraron."


def advertencia_duplicados_colapsados(handle: str, n: int) -> str:
    """recinto por handle con vértices consecutivos duplicados: §5E.3.3."""
    return (
        f"contorno: la polilínea {handle} tiene {n} vértice(s) consecutivo(s) duplicado(s); se ignoraron para el área, "
        "el centroide y la etiqueta."
    )


def detalle_ancla_cambiada(handle: str) -> str:
    return (
        f"preview_id: la entidad ancla {handle} cambió o ya no existe desde la previsualización; no se escribió nada; "
        "vuelva a previsualizar"
    )


def advertencia_desalojo(preview_id: str) -> str:
    return (
        f"Se desalojó la previsualización más antigua '{preview_id}' (tope por documento alcanzado); sus entidades de "
        "MCP-PREVIEW quedan huérfanas: use descartar_preview o limpiar_observaciones con capa MCP-PREVIEW."
    )


def advertencia_huerfanas(n: int) -> str:
    return (
        f"Hay {n} entidad(es) en MCP-PREVIEW que no pertenecen a ninguna previsualización viva (caducadas o de una "
        'sesión anterior): limpie con limpiar_observaciones {"confirmar": true, "capa": "MCP-PREVIEW"}.'
    )


def advertencia_preview_movida(handle: str, capa: str) -> str:
    return f"La entidad de previsualización {handle} ya no está en MCP-PREVIEW (capa {capa}); se conservó."


MENSAJE_GEOMETRIA_PREFIJO = "Geometría no construible: "
DETALLE_ARRAY_TOPE = "array: el número de copias ({n}) supera el tope de 500"
DETALLE_ARRAY_POSICIONES = "filas/columnas: se necesita al menos 2 posiciones"


# PuntoRef (§5E.1; PointRef de GeometryCore.Gen): mensajes exactos.
def detalle_punto_ref_forma(path: str) -> str:
    return f"{path}: debe ser [x, y] o un objeto {{handle, punto}} / {{handle, interseccion_con}}"


PUNTOS_REF = ["inicio", "fin", "medio", "centro", "vertice"]

# Schema de un PuntoRef: el mismo fragmento que PointRef.Schema (oneOf de tres formas).
PUNTO_REF_SCHEMA: dict[str, Any] = {
    "description": (
        'PuntoRef: [x, y] en WCS, o {"handle": "2A3F", "punto": "inicio|fin|medio|centro|vertice", "indice": n} '
        "(punto de una entidad existente; indice solo con 'vertice', desde 0), o {\"handle\": \"2A3F\", "
        '"interseccion_con": "2A40", "cerca_de": [x, y]} (intersección de dos curvas; cerca_de elige si hay varias).'
    ),
    "oneOf": [
        {"type": "array", "items": {"type": "number"}, "minItems": 2, "maxItems": 3},
        {
            "type": "object",
            "properties": {
                "handle": {"type": "string", "pattern": "^[0-9A-Fa-f]{1,16}$"},
                "punto": {"type": "string", "enum": ["inicio", "fin", "medio", "centro", "vertice"]},
                "indice": {"type": "integer", "minimum": 0},
            },
            "required": ["handle", "punto"],
            "additionalProperties": False,
        },
        {
            "type": "object",
            "properties": {
                "handle": {"type": "string", "pattern": "^[0-9A-Fa-f]{1,16}$"},
                "interseccion_con": {"type": "string", "pattern": "^[0-9A-Fa-f]{1,16}$"},
                "cerca_de": {"type": "array", "items": {"type": "number"}, "minItems": 2, "maxItems": 3},
            },
            "required": ["handle", "interseccion_con"],
            "additionalProperties": False,
        },
    ],
}

# Comunes de las seis generadoras (GenSchema.CommonParams; descripciones de GenSchema.Schema*). Sin aceptar_sin_unidades.
PREVIEW_SCHEMA: dict[str, Any] = {
    "type": "boolean",
    "default": False,
    "description": (
        "true = NO escribe la geometría definitiva: la calcula, devuelve sus números (bbox, longitudes, áreas, "
        "n_entidades, puntos resueltos) y un preview_id; después confirmar(preview_id) la dibuja definitivamente o "
        "descartar_preview la olvida. Los preview_id caducan a los 15 minutos."
    ),
}
DIBUJAR_PREVIEW_SCHEMA: dict[str, Any] = {
    "type": "boolean",
    "default": True,
    "description": (
        "Solo con preview: true. true = dibuja la previsualización en la capa MCP-PREVIEW (color 4, no imprimible) "
        "en su propio paso de deshacer; false = solo devuelve números."
    ),
}
COMUNES_GENERADORA: dict[str, Any] = {
    "preview": PREVIEW_SCHEMA,
    "dibujar_preview": DIBUJAR_PREVIEW_SCHEMA,
    "espacio": ESPACIO_DIBUJO_SCHEMA,
    "forzar": FORZAR_SCHEMA,
}
PREVIEW_ID_SCHEMA: dict[str, Any] = {
    "type": "string",
    "minLength": 1,
    "maxLength": 64,
    "description": "preview_id devuelto por una generadora con preview: true.",
}
NO_CERO_SCHEMA: dict[str, Any] = {"type": "number", "not": {"const": 0}}

PUNTO_DE_SCHEMA = _schema({"ref": PUNTO_REF_SCHEMA}, ["ref"])
DISTANCIA_SCHEMA = _schema({"a": PUNTO_REF_SCHEMA, "b": PUNTO_REF_SCHEMA}, ["a", "b"])
PROYECTAR_SCHEMA = _schema({"punto": PUNTO_REF_SCHEMA, "handle": HANDLE_SCHEMA}, ["punto", "handle"])
MURO_SCHEMA = _schema(
    {
        "eje": {
            "oneOf": [
                {"type": "array", "items": PUNTO_REF_SCHEMA, "minItems": 2, "maxItems": 200},
                HANDLE_SCHEMA,
            ],
            "description": "Eje del muro: PuntoRef[] en orden, o handle de una LINE/LWPOLYLINE/POLYLINE 2D existente.",
        },
        "espesor": POSITIVO_SCHEMA,
        "justificacion": {"type": "string", "enum": ["centro", "izquierda", "derecha"], "default": "centro"},
        "cerrado": {"type": "boolean", "default": False},
        "representacion": {"type": "string", "enum": ["contorno", "caras"], "default": "contorno"},
        "achurado": {
            "type": "object",
            "properties": {
                "patron": {"type": "string", "minLength": 1, "maxLength": 255, "default": "SOLID"},
                "escala": {"type": "number", "exclusiveMinimum": 0, "default": 1},
                "angulo": {**GRADOS_SCHEMA, "default": 0},
            },
            "additionalProperties": False,
        },
        "capa": CAPA_DIBUJO_SCHEMA,
        "capa_achurado": CAPA_DIBUJO_SCHEMA,
        "limite_inglete": {"type": "number", "minimum": 1, "default": 4},
    },
    ["eje", "espesor"],
    COMUNES_GENERADORA,
)
EJES_REJILLA_SCHEMA = _schema(
    {
        "origen": PUNTO_REF_SCHEMA,
        "separaciones_x": {"type": "array", "items": POSITIVO_SCHEMA, "minItems": 0, "maxItems": 100},
        "separaciones_y": {"type": "array", "items": POSITIVO_SCHEMA, "minItems": 0, "maxItems": 100},
        "rotulos_x": {"type": "array", "items": {"type": "string", "minLength": 1, "maxLength": 10}},
        "rotulos_y": {"type": "array", "items": {"type": "string", "minLength": 1, "maxLength": 10}},
        "extension": {"type": "number", "minimum": 0},
        "radio_burbuja": POSITIVO_SCHEMA,
        "altura_texto": POSITIVO_SCHEMA,
        "burbujas": {"type": "string", "enum": ["inicio", "fin", "ambos"], "default": "fin"},
        "rotacion": {**GRADOS_SCHEMA, "default": 0},
        "estilo": {**ESTILO_SCHEMA, "description": ESTILO_EJES_DESCRIPCION},
        "capa": CAPA_DIBUJO_SCHEMA,
    },
    ["origen", "separaciones_x", "separaciones_y", "extension", "radio_burbuja", "altura_texto"],
    COMUNES_GENERADORA,
)
RECINTO_SCHEMA = _schema(
    {
        "contorno": {
            "oneOf": [
                {"type": "array", "items": PUNTO_REF_SCHEMA, "minItems": 3, "maxItems": 200},
                HANDLE_SCHEMA,
            ],
            "description": "Vértices PuntoRef[] del recinto, o handle de una LWPOLYLINE/POLYLINE 2D cerrada existente.",
        },
        "nombre": {"type": "string", "minLength": 1, "maxLength": 100},
        "altura_texto": POSITIVO_SCHEMA,
        "decimales": {"type": "integer", "minimum": 0, "maximum": 3, "default": 2},
        "etiqueta_en": PUNTO_REF_SCHEMA,
        "ancho_texto": {"type": "number", "minimum": 0, "default": 0},
        "estilo": ESTILO_SCHEMA,
        "capa_contorno": CAPA_DIBUJO_SCHEMA,
        "capa_texto": CAPA_DIBUJO_SCHEMA,
    },
    ["contorno", "nombre", "altura_texto"],
    COMUNES_GENERADORA,
)
OFFSET_SCHEMA = _schema(
    {
        "handle": HANDLE_SCHEMA,
        "distancia": POSITIVO_SCHEMA,
        "lado": {"oneOf": [PUNTO_REF_SCHEMA, {"type": "string", "enum": ["izquierda", "derecha"]}]},
        "capa": CAPA_DIBUJO_SCHEMA,
    },
    ["handle", "distancia", "lado"],
    COMUNES_GENERADORA,
)
HANDLES_ARRAY_SCHEMA: dict[str, Any] = {
    "type": "array",
    "items": HANDLE_SCHEMA,
    "minItems": 1,
    "maxItems": 100,
    "description": "Handles de las entidades a copiar (modelo o un mismo layout), sin repetidos.",
}
CONFIRMAR_ARRAY_SCHEMA: dict[str, Any] = {
    "type": "boolean",
    "default": False,
    "description": "true = copiar también entidades que no están en capas MCP-* (entidades del usuario).",
}
ARRAY_RECTANGULAR_SCHEMA = _schema(
    {
        "handles": HANDLES_ARRAY_SCHEMA,
        "filas": {"type": "integer", "minimum": 1, "maximum": 100},
        "columnas": {"type": "integer", "minimum": 1, "maximum": 100},
        "sep_filas": NO_CERO_SCHEMA,
        "sep_columnas": NO_CERO_SCHEMA,
        "angulo": {**GRADOS_SCHEMA, "default": 0},
        "base": PUNTO_REF_SCHEMA,
        "confirmar": CONFIRMAR_ARRAY_SCHEMA,
        "capa": CAPA_DIBUJO_SCHEMA,
    },
    ["handles", "filas", "columnas", "sep_filas", "sep_columnas"],
    COMUNES_GENERADORA,
)
ARRAY_POLAR_SCHEMA = _schema(
    {
        "handles": HANDLES_ARRAY_SCHEMA,
        "centro": PUNTO_REF_SCHEMA,
        "n": {"type": "integer", "minimum": 2, "maximum": 501},
        "angulo_total": NO_CERO_SCHEMA,
        "rotar_elementos": {"type": "boolean", "default": True},
        "base": PUNTO_REF_SCHEMA,
        "confirmar": CONFIRMAR_ARRAY_SCHEMA,
        "capa": CAPA_DIBUJO_SCHEMA,
    },
    ["handles", "centro", "n", "angulo_total"],
    COMUNES_GENERADORA,
)
CONFIRMAR_SCHEMA = _schema({"preview_id": PREVIEW_ID_SCHEMA}, ["preview_id"])
DESCARTAR_PREVIEW_SCHEMA = _schema({"preview_id": PREVIEW_ID_SCHEMA}, ["preview_id"])
SCHEMAS_FASE4A: dict[str, dict[str, Any]] = {
    "punto_de": PUNTO_DE_SCHEMA,
    "distancia": DISTANCIA_SCHEMA,
    "proyectar": PROYECTAR_SCHEMA,
    "muro": MURO_SCHEMA,
    "ejes_rejilla": EJES_REJILLA_SCHEMA,
    "recinto": RECINTO_SCHEMA,
    "offset": OFFSET_SCHEMA,
    "array_rectangular": ARRAY_RECTANGULAR_SCHEMA,
    "array_polar": ARRAY_POLAR_SCHEMA,
    "confirmar": CONFIRMAR_SCHEMA,
    "descartar_preview": DESCARTAR_PREVIEW_SCHEMA,
}

# -- Fase L (§5F, limpieza de planos; protocolo 0.6) -----------------------------

# Las 4 herramientas de Fase L en el orden de protocol.md §4 (todas de escritura, §5F).
HERRAMIENTAS_FASEL = ["borrar_fuera_de", "purgar", "normalizar_capas", "corregir_unidades_bloques"]
# Cabecera común de la respuesta (L6, CleanWrite.Response).
CLAVES_L6 = ["pid", "preview", "escrito", "insunits", "unidades", "advertencias"]

# Descripciones del manifest, texto LITERAL de protocol.md §5F.1–§5F.4.
DESCRIPCIONES_FASEL: dict[str, str] = {
    "borrar_fuera_de": (
        "BORRA todas las entidades de un espacio que quedan FUERA de un contorno (handle de una polilínea cerrada o un "
        "círculo), juzgando cada una por su caja: por defecto conserva las que cruzan el borde (criterio caja_cruza). "
        "Sin confirmar: true solo PREVISUALIZA (inventario por tipo y capa, muestra de handles y viewports afectados) "
        "y no modifica nada; con confirmar: true borra en un solo paso de deshacer. Nunca borra el contorno, viewports "
        "ni entidades sin caja; avisa de los viewports de papel que miran la zona borrada."
    ),
    "purgar": (
        "PURGA símbolos sin uso (bloques con nombre y anónimos, capas, tipos de línea, estilos de texto y de cota, "
        "regapps) iterando hasta el punto fijo, como -PURGE repetido, en un solo paso de deshacer. Nunca purga la capa "
        "0, DEFPOINTS, la capa/estilos/tipo de línea actuales, capas MCP-*, los espacios ni xrefs. Sin confirmar: true "
        "solo PREVISUALIZA (nombres por categoría y entidades de cada bloque) y no modifica nada."
    ),
    "normalizar_capas": (
        "REASIGNA a otra capa todas las entidades de cada capa origen de un mapa {origen: destino} (destino = concepto "
        "de lps.json, capa existente o capa del estándar que se crea), en modelo y layouts (espacios limita el ámbito, "
        "p. ej. solo al modelo) y, con incluir_bloques, dentro de las definiciones de bloque, en un solo paso de "
        "deshacer; conserva colores y tipos de línea propios. Nunca reasigna el viewport n.º 1 de un layout y los demás "
        "VIEWPORT solo con incluir_viewports: true. Rechaza orígenes inexistentes, capas bloqueadas/congeladas/xref y "
        "mapas con cadenas o ciclos. Sin confirmar: true solo PREVISUALIZA (conteo por capa origen, tipo y espacio, y lo "
        "excluido) y no modifica nada. Con conservar_aspecto: true fija en cada entidad las propiedades que heredaba de "
        "su capa origen para que no cambie de aspecto."
    ),
    "corregir_unidades_bloques": (
        "CORRIGE el metadato de unidades de definiciones de bloque cuya geometría ya está en las unidades del dibujo "
        "(las que auditar_bloques marca con metadato_unidades_incorrecto): fija BlockTableRecord.Units = INSUNITS del "
        "dibujo, sin tocar geometría ni inserciones, en un solo paso de deshacer. Sin confirmar: true solo PREVISUALIZA "
        "(unidades actuales y propuestas por bloque) y no modifica nada."
    ),
}

# Fragmentos de schema comunes (texto de CleanWrite.SchemaConfirmar / SchemaPreview, §5F.0).
CONFIRMAR_LIMPIEZA_SCHEMA: dict[str, Any] = {
    "type": "boolean",
    "default": False,
    "description": (
        "true = ESCRIBIR (las entidades afectadas son del usuario: sin confirmar: true la herramienta solo "
        "previsualiza y no modifica nada)."
    ),
}
PREVIEW_LIMPIEZA_SCHEMA: dict[str, Any] = {
    "type": "boolean",
    "description": (
        "true = solo previsualizar (inventario exacto de lo que se haría, sin escribir) aunque se pase confirmar: true. "
        "Por defecto, true si confirmar no es true. preview: false sin confirmar: true → INVALID_PARAMS."
    ),
}
COMUNES_LIMPIEZA: dict[str, Any] = {"confirmar": CONFIRMAR_LIMPIEZA_SCHEMA, "preview": PREVIEW_LIMPIEZA_SCHEMA}
NOMBRE_SCHEMA: dict[str, Any] = {"type": "string", "minLength": 1, "maxLength": 255}

# L1: detalle exacto de preview: false sin confirmar: true (CleanWrite.PreviewFalseWithoutConfirm).
DETALLE_PREVIEW_SIN_CONFIRMAR = (
    "confirmar: debe ser true para escribir; omita preview (o pase preview: true) para solo previsualizar"
)
DETALLE_CRITERIO = "criterio: debe ser caja_cruza o caja_dentro"
CRITERIOS = ["caja_cruza", "caja_dentro"]
# §5F.2: categorías de purgar en el orden de la respuesta (PurgeCategories.All) + "todo".
CATEGORIAS_PURGA = ["bloques", "capas", "tipos_linea", "estilos_texto", "estilos_cota", "regapps"]
# BoxPolygon.CircleSides.
LADOS_CIRCULO = 64
ADVERTENCIA_BLOQUES_SIN_UNIDADES = "INSUNITS=0: no se comparan las unidades de los bloques."
MOTIVOS_UNIDADES = [
    "unidades iguales a las del dibujo",
    "sin inserciones",
    "inserciones compensadas",
    "tamaño real no plausible",
    "tamaño no medible",
]

BORRAR_FUERA_DE_SCHEMA = _schema(
    {
        "contorno": {
            **HANDLE_SCHEMA,
            "description": "Handle de una LWPOLYLINE cerrada o de un CIRCLE (modelo o layout, no dentro de un bloque).",
        },
        "espacio": {
            **NOMBRE_SCHEMA,
            "description": "\"modelo\" o nombre de layout; por defecto el espacio donde vive el contorno.",
        },
        "criterio": {
            "type": "string",
            "enum": CRITERIOS,
            "default": "caja_cruza",
            "description": "caja_cruza = conserva las entidades cuya caja cruza el borde; caja_dentro = las borra también.",
        },
        "excluir_capas": {
            "type": "array",
            "items": NOMBRE_SCHEMA,
            "minItems": 1,
            "maxItems": 100,
            "description": "Capas cuyas entidades nunca se borran.",
        },
        "muestra": {"type": "integer", "minimum": 1, "maximum": 50, "default": 10},
    },
    ["contorno"],
    COMUNES_LIMPIEZA,
)
PURGAR_SCHEMA = _schema(
    {
        "categorias": {
            "type": "array",
            "items": {"type": "string", "enum": CATEGORIAS_PURGA + ["todo"]},
            "minItems": 1,
            "maxItems": 7,
            "default": ["todo"],
        },
        "excluir": {"type": "array", "items": NOMBRE_SCHEMA, "minItems": 1, "maxItems": 200},
        "muestra": {"type": "integer", "minimum": 1, "maximum": 200, "default": 50},
    },
    [],
    COMUNES_LIMPIEZA,
)
# Descripción LITERAL del parámetro conservar_aspecto (§5G.5).
CONSERVAR_ASPECTO_DESCRIPCION = (
    "true = antes de mover cada entidad fija en ella lo que heredaba ByLayer de su capa origen (color, tipo de línea, "
    "grosor, transparencia) cuando la capa destino tiene otro valor, para que no cambie de aspecto (como la cadena del "
    "rótulo v2). Por defecto no se toca y la previsualización avisa cuántas cambiarían de aspecto."
)
NORMALIZAR_CAPAS_SCHEMA = _schema(
    {
        "mapa": {
            "type": "object",
            "minProperties": 1,
            "maxProperties": 200,
            "additionalProperties": NOMBRE_SCHEMA,
            "description": (
                "capa_origen → destino (concepto de lps.json, capa existente o capa del estándar que se crea); "
                "nunca capas MCP-* como origen ni como destino."
            ),
        },
        "espacios": {
            "type": "array",
            "items": NOMBRE_SCHEMA,
            "minItems": 1,
            "maxItems": 100,
            "description": "Limita el mapa a estos espacios: modelo y/o nombres de layout; por defecto todos.",
        },
        "incluir_viewports": {"type": "boolean", "default": False},
        "incluir_bloques": {"type": "boolean", "default": False},
        "borrar_capas_origen_vacias": {"type": "boolean", "default": False},
        "conservar_aspecto": {"type": "boolean", "default": False, "description": CONSERVAR_ASPECTO_DESCRIPCION},
    },
    ["mapa"],
    COMUNES_LIMPIEZA,
)
CORREGIR_UNIDADES_BLOQUES_SCHEMA = _schema(
    {"bloques": {"type": "array", "items": NOMBRE_SCHEMA, "minItems": 1, "maxItems": 200}},
    [],
    COMUNES_LIMPIEZA,
)
SCHEMAS_FASEL: dict[str, dict[str, Any]] = {
    "borrar_fuera_de": BORRAR_FUERA_DE_SCHEMA,
    "purgar": PURGAR_SCHEMA,
    "normalizar_capas": NORMALIZAR_CAPAS_SCHEMA,
    "corregir_unidades_bloques": CORREGIR_UNIDADES_BLOQUES_SCHEMA,
}

# -- Fase 4B (§5G): vano, union_muros, cotas_automaticas e insertar_export ------------------------------------------
HERRAMIENTAS_FASE4B = ["vano", "union_muros", "cotas_automaticas", "insertar_export"]

# Descripciones del manifest, texto LITERAL de protocol.md §5G.1–§5G.4.
DESCRIPCIONES_FASE4B: dict[str, str] = {
    "vano": (
        "ESCRIBE un vano (puerta, ventana o hueco) en un muro existente dado por el handle de su contorno cerrado "
        "(polilínea de 'muro' o rectángulo de la oficina) o de una de sus caras LINE (con cara_opuesta, o buscando la LINE "
        "paralela más cercana de la misma capa a 0,40 o menos): parte el contorno en dos contornos cerrados, o las dos "
        "líneas en cuatro con dos tapas, con tapa de jamba a cada lado del hueco (rehace sus sombreados asociativos) e "
        "inserta el bloque de biblioteca (Puerta "
        "Dinamica LM / VENTANA01 por defecto, o 'bloque') centrado y orientado con el muro, con el ancho fijado por su "
        "propiedad dinámica (o escalado con advertencia). Rechaza huecos que se salen del tramo, cruzan una esquina u "
        "otro vano. El muro es del usuario: exige confirmar: true si no está en una capa MCP-*. Con preview: true solo "
        "calcula y devuelve números + preview_id (no modifica el muro hasta confirmar)."
    ),
    "union_muros": (
        "ESCRIBE la unión de dos muros rectos (handles de sus contornos cerrados de 4 vértices) en un solo contorno "
        "limpio: recorta el solape o la tapa apoyada y encadena las caras (encuentros en L, T, cruce o colineales, a "
        "cualquier ángulo; cadenas de más muros se unen por pares), rehaciendo sus sombreados asociativos. Los muros "
        "son del usuario: exige confirmar: true si no están en capas MCP-*. Con preview: true solo calcula y devuelve "
        "números + preview_id."
    ),
    "cotas_automaticas": (
        "ESCRIBE cotas lineales automáticas en la capa Cotas: proyecta los vértices/extremos/centros de las entidades "
        "dadas (muros, polilíneas, líneas y burbujas de ejes) sobre una dirección (x, y, grados o auto) y crea una "
        "cadena de cotas (o una total con cadena: false) a 'separacion' del lado pedido, con el estilo de cota indicado "
        "(o el actual, con advertencia). Devuelve las medidas. Con preview: true solo calcula y devuelve números + "
        "preview_id."
    ),
    "insertar_export": (
        "ESCRIBE en el dibujo un export de CYPE (archivo .dwg, ruta absoluta de solo lectura: nunca se modifica el "
        "origen) como un bloque con nombre derivado del archivo, en 'posicion' (PuntoRef), aplicando el factor de "
        "unidades export→dibujo (mm→m) salvo escala_unidades 'ninguna'. Conserva las capas CYPE (ARM_*, ALZ_*, CIM_*…) "
        "y avisa de las capas ajenas que trae (filtrar_capas 'cype' las deja fuera). Un paso de deshacer; sin "
        "previsualización."
    ),
}

CONFIRMAR_MURO_SCHEMA: dict[str, Any] = {
    "type": "boolean",
    "default": False,
    "description": (
        "true = modificar muros que no están en capas MCP-* (entidades del usuario); se exige ya con preview: true. "
        "No confundir con la herramienta confirmar(preview_id), que materializa una previsualización."
    ),
}
PROPIEDADES_DINAMICAS_SCHEMA: dict[str, Any] = {
    "type": "object",
    "minProperties": 1,
    "maxProperties": 20,
    "additionalProperties": {"oneOf": [{"type": "number"}, {"type": "string", "maxLength": 500}]},
    "description": "Otras propiedades del bloque dinámico (p. ej. visibilidad); el ancho no se pasa aquí: lo fija la herramienta.",
}
POSICION_VANO_SCHEMA: dict[str, Any] = {
    "description": (
        'Centro del hueco: un PuntoRef (se proyecta sobre la línea media del tramo recto más cercano) o {"distancia": d, '
        '"desde": "inicio"|"fin"} sobre el eje, solo en contornos de 4 vértices.'
    ),
    "oneOf": [
        *PUNTO_REF_SCHEMA["oneOf"],
        {
            "type": "object",
            "properties": {
                "distancia": {"type": "number", "minimum": 0},
                "desde": {"type": "string", "enum": ["inicio", "fin"], "default": "inicio"},
            },
            "required": ["distancia"],
            "additionalProperties": False,
        },
    ],
}
VANO_SCHEMA = _schema(
    {
        "muro": {**HANDLE_SCHEMA, "description": "Handle del contorno cerrado del muro (LWPOLYLINE de al menos 4 vértices, sin arcos) o de una de sus caras LINE."},
        "cara_opuesta": {**HANDLE_SCHEMA, "description": "Solo si muro es una LINE: handle de la otra cara del muro, también una LINE. Si falta, se busca la LINE paralela más cercana de la misma capa que solape el muro a 0,40 o menos."},
        "posicion": POSICION_VANO_SCHEMA,
        "ancho": {**POSITIVO_SCHEMA, "description": "Ancho del hueco a lo largo del muro, de jamba exterior a jamba exterior."},
        "tipo": {"type": "string", "enum": ["puerta", "ventana", "hueco"], "description": "hueco = solo partir el contorno, sin bloque."},
        "bloque": {
            "type": "string",
            "minLength": 1,
            "maxLength": 255,
            "description": "Bloque de lps.json biblioteca_vanos; por defecto el primero del tipo. No aplica con tipo hueco.",
        },
        "cara_apertura": {
            "type": "string",
            "enum": ["izquierda", "derecha"],
            "default": "izquierda",
            "description": "Cara del muro hacia la que abre la hoja, respecto a la dirección del tramo (numeros.hueco.direccion).",
        },
        "bisagra": {
            "type": "string",
            "enum": ["inicio", "fin"],
            "default": "inicio",
            "description": "Esquina del hueco donde va el punto base (bisagra): la más cercana al inicio o al fin del eje.",
        },
        "propiedades_dinamicas": PROPIEDADES_DINAMICAS_SCHEMA,
        "confirmar": CONFIRMAR_MURO_SCHEMA,
        "capa": CAPA_DIBUJO_SCHEMA,
    },
    ["muro", "posicion", "ancho", "tipo"],
    COMUNES_GENERADORA,
)
UNION_MUROS_SCHEMA = _schema(
    {
        "muro_a": {**HANDLE_SCHEMA, "description": "Primer muro: LWPOLYLINE cerrada de 4 vértices convexa."},
        "muro_b": {**HANDLE_SCHEMA, "description": "Segundo muro, distinto del primero y en el mismo espacio."},
        "tipo": {
            "type": "string",
            "enum": ["L", "T", "cruce", "auto"],
            "default": "auto",
            "description": "Comprobación del encuentro detectado.",
        },
        "confirmar": CONFIRMAR_MURO_SCHEMA,
        "capa": CAPA_DIBUJO_SCHEMA,
    },
    ["muro_a", "muro_b"],
    COMUNES_GENERADORA,
)
COTAS_AUTOMATICAS_SCHEMA = _schema(
    {
        "handles": {
            "type": "array",
            "items": HANDLE_SCHEMA,
            "minItems": 1,
            "maxItems": 100,
            "description": "Entidades cuyos puntos característicos se acotan (solo lectura: no exige confirmar).",
        },
        "direccion": {
            "oneOf": [{"type": "string", "enum": ["x", "y", "auto"]}, {"type": "number"}],
            "default": "auto",
            "description": "Dirección de la línea de cota: x, y, grados o auto (la dominante de la primera entidad).",
        },
        "lado": {
            "description": "izquierda | derecha respecto a la dirección, o un PuntoRef (el lado de ese punto).",
            "oneOf": [{"type": "string", "enum": ["izquierda", "derecha"]}, *PUNTO_REF_SCHEMA["oneOf"]],
            "default": "izquierda",
        },
        "separacion": {
            **POSITIVO_SCHEMA,
            "description": "Distancia de la línea de cota al punto más extremo del conjunto en ese lado (la oficina usa ≈ 0,15–0,19; segunda cadena ≈ 0,35).",
        },
        "estilo": {**ESTILO_SCHEMA, "description": "Estilo de cota existente; sin él, el actual con advertencia (la oficina usa COTA75)."},
        "cadena": {"type": "boolean", "default": True, "description": "true = una cota por tramo; false = una sola cota total."},
        "tolerancia": {"type": "number", "exclusiveMinimum": 0, "default": 0.001, "description": "Puntos proyectados a menos de esta distancia se funden en uno."},
        "capa": CAPA_DIBUJO_SCHEMA,
    },
    ["handles", "separacion"],
    COMUNES_GENERADORA,
)
INSERTAR_EXPORT_SCHEMA = _schema(
    {
        "ruta": {
            "type": "string",
            "minLength": 1,
            "maxLength": 1000,
            "description": "Ruta absoluta del .dwg de CYPE. Solo lectura: el archivo nunca se modifica ni se ejecuta.",
        },
        "posicion": {**PUNTO_REF_SCHEMA},
        "rotacion": {**GRADOS_SCHEMA, "default": 0},
        "escala": {**NO_CERO_SCHEMA, "default": 1, "description": "Escala pedida (se multiplica por el factor de unidades con 'auto')."},
        "escala_unidades": {"type": "string", "enum": ["auto", "ninguna"], "default": "auto"},
        "nombre_bloque": {"type": "string", "minLength": 1, "maxLength": 200, "description": "Por defecto CYPE_<nombre del archivo>."},
        "filtrar_capas": {
            "type": "string",
            "enum": ["todas", "cype"],
            "default": "todas",
            "description": "cype = deja fuera las entidades de capas que no son 0 ni del grupo CYPE de lps.json.",
        },
        "capa_destino": CAPA_DIBUJO_SCHEMA,
        "como": {"type": "string", "enum": ["bloque", "xref"], "default": "bloque", "description": "Solo 'bloque' en 0.7."},
        "espacio": ESPACIO_DIBUJO_SCHEMA,
        "forzar": FORZAR_SCHEMA,
        "aceptar_sin_unidades": ACEPTAR_SIN_UNIDADES_SCHEMA,
    },
    ["ruta", "posicion"],
)
SCHEMAS_FASE4B: dict[str, dict[str, Any]] = {
    "vano": VANO_SCHEMA,
    "union_muros": UNION_MUROS_SCHEMA,
    "cotas_automaticas": COTAS_AUTOMATICAS_SCHEMA,
    "insertar_export": INSERTAR_EXPORT_SCHEMA,
}

# Textos exactos de §5G y §6 (fijos y dependientes de datos).
ADVERTENCIA_SIN_CARPETAS = (
    "lps.json no define carpetas_export_permitidas: se admite cualquier ruta absoluta .dwg legible; añada la clave para "
    "limitar las carpetas."
)
ADVERTENCIA_EXPORT_SIN_UNIDADES = (
    "El export no declara unidades (INSUNITS=0): no se aplicó factor; pase escala explícita si el export está en mm."
)
DETALLE_RUTA_ABSOLUTA = "ruta: debe ser una ruta absoluta"
DETALLE_RUTA_DWG = "ruta: solo se admiten archivos .dwg"
DETALLE_RUTA_COMODINES = "ruta: no se admiten '..' ni comodines"
DETALLE_RUTA_FUERA = "ruta: fuera de las carpetas permitidas de lps.json (carpetas_export_permitidas)"
# Sin carpetas explícitas, _validar_ruta_export usa las de insertar_export.
_SIN_CARPETAS: Any = object()
DETALLE_COMO_XREF = "como: xref no se admite en insertar_export; use adjuntar_xref para adjuntar una referencia externa"
DETALLE_MURO_POLIGONO = "muro: el contorno debe ser un polígono cerrado simple de al menos 4 vértices"
DETALLE_MURO_SIN_TRAMO = "muro: no se encontró un tramo recto (dos caras paralelas) cerca del punto indicado"
DETALLE_MURO_DISTANCIA_4V = "muro: posicion por distancia solo se admite en contornos de 4 vértices"
DETALLE_ANCHO_VERTICE = "ancho: el hueco cruza un vértice del contorno (esquina u otro vano)"
DETALLE_ANCHO_MENOR = "ancho: debe ser menor que la longitud del tramo"
DETALLE_UNION_NO_TOCAN = "union: los muros no se tocan ni se solapan"
DETALLE_UNION_PUNTO = "union: los muros solo se tocan en un punto; no hay encuentro que unir"
DETALLE_UNION_CONTENIDO = "union: un muro está contenido en el otro"
DETALLE_UNION_NO_SIMPLE = "union: el contorno unido no es un polígono simple"
DETALLE_PUNTOS_COTA = "puntos: se necesitan al menos 2 puntos distintos en la dirección de la cota"


def detalle_no_cabe(ancho: float, longitud: float) -> str:
    return f"ancho: el hueco ({_fmt3(ancho)}) no cabe en el tramo (longitud {_fmt3(longitud)}): se sale del tramo o cruza una esquina"


def detalle_distancia_fuera(distancia: float, longitud: float) -> str:
    return f"posicion: la distancia {_fmt3(distancia)} se sale del tramo (longitud {_fmt3(longitud)})"


def detalle_bloque_no_catalogo(bloque: str) -> str:
    return f"bloque: '{bloque}' no está en lps.json biblioteca_vanos; no se conoce su orientación ni su ancho nominal"


def detalle_no_dinamico(bloque: str) -> str:
    return f"propiedades_dinamicas: el bloque '{bloque}' no es dinámico"


def detalle_propiedad_desconocida(propiedad: str, nombres: list[str]) -> str:
    return f"propiedades_dinamicas: propiedad desconocida '{propiedad}' (propiedades del bloque: {', '.join(nombres)})"


def detalle_convexo(campo: str) -> str:
    return f"{campo}: el contorno debe ser un cuadrilátero convexo (muro recto)"


def advertencia_espesor_vano(bloque: str, nominal: float, medido: float) -> str:
    return (
        f"vano: el bloque '{bloque}' está dibujado para muros de {_fmt3(nominal)}; el muro mide {_fmt3(medido)}; las jambas "
        "no cubren todo el espesor."
    )


def advertencia_hatch_no_reconstruible(handle: str) -> str:
    return (
        f"El sombreado {handle} tiene un bucle que no se puede reconstruir; no se rehízo y puede quedar cruzando el hueco o "
        "la unión: revíselo."
    )


def advertencia_columnas(capa: str, handles: list[str]) -> str:
    resto = len(handles) - 5
    lista = ", ".join(handles[:5]) + (f" y {resto} más" if resto > 0 else "")
    return f"vano: el hueco cae sobre {len(handles)} entidad(es) de la capa '{capa}' ({lista}): revise la estructura antes de abrirlo."


def advertencia_sombreados_en_hueco(handles: list[str]) -> str:
    resto = len(handles) - 5
    lista = ", ".join(handles[:5]) + (f" y {resto} más" if resto > 0 else "")
    return f"vano: {len(handles)} sombreado(s) cruzan el hueco y no se rehacen ({lista}): revíselos."


def detalle_ninguna_cara(handle: str, capa: str) -> str:
    return f"cara_opuesta: ninguna LINE paralela de la capa '{capa}' solapa el muro {handle} a 0.4 o menos; indique cara_opuesta"


def detalle_varias_caras(capa: str, candidatas: list[tuple[str, float]]) -> str:
    lista = ", ".join(f"{h} ({_fmt3(s)})" for h, s in candidatas)
    return f"cara_opuesta: hay varias LINE candidatas en la capa '{capa}': {lista}; indique cara_opuesta"


def advertencia_ancho_ambiguo(bloque: str, candidatas: list[str], factor: float) -> str:
    return (
        f"vano: el bloque '{bloque}' tiene varias propiedades que pueden ser el ancho ({', '.join(candidatas)}); no se eligió "
        f"ninguna y se escaló {_fmt3(factor)} a lo largo del vano: las jambas y la hoja quedan deformadas en proporción. "
        "Fije propiedad_ancho en lps.json biblioteca_vanos."
    )


def advertencia_ancho_pendiente(bloque: str) -> str:
    return (
        f"vano: la definición '{bloque}' aún no está en el dibujo; su propiedad de ancho se deducirá al crear la referencia "
        "en confirmar (ancho_por: pendiente)."
    )


def buscar_propiedad_ancho(
    props: list[dict[str, Any]], preferida: str | None, nominal: float
) -> tuple[str | None, list[str]]:
    """VanoHandler.WidthProperty: el nombre configurado tal cual (si es erróneo lo rechaza Apply, nunca se deduce) o, por valor
    (DynamicPropertyRules.FindWidthProperty), una única candidata; con varias no elige."""
    if preferida is not None:
        return str(preferida), []
    nombres: list[str] = []
    for p in props:
        numero = isinstance(p["valor"], (int, float)) and not isinstance(p["valor"], bool)
        if not p["solo_lectura"] and p["unidades"] == "distancia" and numero and abs(float(p["valor"]) - nominal) <= 1e-3 * nominal:
            if p["nombre"].casefold() not in [n.casefold() for n in nombres]:
                nombres.append(p["nombre"])
    return (nombres[0] if len(nombres) == 1 else None), nombres


def advertencia_ancho_escalado(bloque: str, factor: float) -> str:
    return (
        f"vano: el bloque '{bloque}' no es dinámico (o no se encontró su propiedad de ancho); se escaló {_fmt3(factor)} a lo "
        "largo del vano: las jambas y la hoja quedan deformadas en proporción."
    )


def advertencia_hatch_otras_entidades(handle: str) -> str:
    return f"El sombreado {handle} tiene bucles en otras entidades; no se rehízo: revíselo."


def advertencia_capas_muros(a: str, b: str) -> str:
    return f"union_muros: los muros están en capas distintas ('{a}', '{b}'); el contorno unido va a '{a}'."


def advertencia_estilo_cota(estilo: str) -> str:
    return f"cotas_automaticas: se usó el estilo de cota actual '{estilo}'; indique estilo para fijarlo (la oficina usa COTA75)."


def advertencia_capas_ajenas(otras: list[str]) -> str:
    """ExportRules.ForeignLayersWarning: hasta 5 nombres."""
    lista = ", ".join(otras[:5])
    resto = f" (y {len(otras) - 5} más)" if len(otras) > 5 else ""
    return f'El export trae {len(otras)} capa(s) fuera del grupo CYPE: {lista}{resto}; pase filtrar_capas: "cype" para dejarlas fuera.'


def advertencia_cambiarian_aspecto(n: int) -> str:
    return (
        f"{n} entidad(es) cambiarían de aspecto al pasar a su capa destino (color, tipo de línea, grosor o transparencia "
        "heredados de la capa origen); pase conservar_aspecto: true para fijarlos en cada entidad antes de moverla."
    )


def advertencia_aspecto_fijado(n: int, k: int) -> str:
    texto = f"conservar_aspecto: se fijaron color, tipo de línea, grosor o transparencia en {n} entidad(es) para conservar su aspecto"
    if k > 0:
        texto += f"; {k} pierden el 'no imprimir' de su capa origen (no se fija por entidad)"
    return texto + "."


# Exports de CYPE simulados: INSUNITS, entidades del modelo por capa, bloques con nombre y tamaño en unidades del export.
EXPORTS_SIMULADOS: dict[str, dict[str, Any]] = {
    "c:\\copias\\file1.dwg": {
        "insunits": 4,
        "capas": {"ARM_PILARES": 3, "CIM_ZAPATAS": 2, "TEXTO_CYPE": 1, "0": 1},
        "bloques": ["PILAR_C1"],
        "tamano": [1500.0, 1500.0],
    },
    "c:\\copias\\sin_unidades.dwg": {"insunits": 0, "capas": {"ARM_VIGAS": 2}, "bloques": [], "tamano": [3.0, 2.0]},
    "c:\\copias\\ajenas.dwg": {
        "insunits": 4,
        "capas": {"A1": 1, "A2": 1, "A3": 1, "A4": 1, "A5": 1, "A6": 1, "A7": 1},
        "bloques": [],
        "tamano": [10.0, 10.0],
    },
    "c:\\copias\\ilegible.dwg": {"ilegible": True},
    # Lámina real de CYPE (gate 0.7): INSUNITS 4, pero en centímetros de papel a 1:50.
    "c:\\copias\\lamina.dwg": {
        "insunits": 4,
        "capas": {"RECUAD_LIMITES": 2, "COLUMNA_DE_CONCRETO": 3, "ZAP_CONTORNO": 2},
        "bloques": [],
        "tamano": [84.0, 59.4],
        "recuadros": [[84.0, 59.4], [42.0, 29.7]],
        "textos": ["Escala: 1:50", "Escala 1:50", "ESTRIBOS"],
    },
    "c:\\copias\\lamina_escalas.dwg": {
        "insunits": 4,
        "capas": {"RECUAD_LIMITES": 1},
        "bloques": [],
        "tamano": [59.4, 42.0],
        "recuadros": [[59.4, 42.0]],
        "textos": ["Escala 1:50", "Escala 1:20"],
    },
    "c:\\copias\\lamina_sin_escala.dwg": {
        "insunits": 4,
        "capas": {"RECUAD_MARCO": 1},
        "bloques": [],
        "tamano": [42.0, 29.7],
        "recuadros": [[42.0, 29.7]],
        "textos": [],
    },
}


def deteccion_lamina(export: dict[str, Any]) -> tuple[list[str], list[int]]:
    """ExportRules.DetectSheet: formatos normalizados en cm y escalas de los textos."""
    formatos = []
    for nombre, largo, corto in (("A0", 118.9, 84.1), ("A1", 84.1, 59.4), ("A2", 59.4, 42.0), ("A3", 42.0, 29.7), ("A4", 29.7, 21.0)):
        if any(abs(max(r) - largo) <= 0.5 and abs(min(r) - corto) <= 0.5 for r in export.get("recuadros", [])):
            formatos.append(nombre)
    escalas = sorted({int(m) for t in export.get("textos", []) for m in re.findall(r"(?i)escala\s*:?\s*1\s*[:/]\s*(\d{1,6})", t)})
    return formatos, escalas


def _numero_corto(v: float) -> str:
    return f"{v:.6f}".rstrip("0").rstrip(".")


def detalle_lamina(formatos: list[str], escalas: list[int]) -> str:
    """ExportRules.SheetRejection con un dibujo en metros."""
    inicio = f"escala_unidades: el export es una lámina de papel en centímetros ({', '.join(formatos)})"
    motivo = ", no un modelo a escala real: su INSUNITS no sirve y el factor automático la dejaría a una escala equivocada."
    if len(escalas) == 1:
        return f"{inicio} a escala 1:{escalas[0]}{motivo} Pase escala: {_numero_corto(escalas[0] * 0.01)} y escala_unidades: 'ninguna'"
    if escalas:
        lista = ", ".join(f"1:{n} → {_numero_corto(n * 0.01)}" for n in escalas)
        return f"{inicio} con varias escalas ({lista}){motivo} Elija la del plano que vaya a usar y pase esa escala y escala_unidades: 'ninguna'"
    return f"{inicio} sin ningún texto 'Escala 1:N' legible{motivo} Pase escala: N × 0.01 con la escala 1:N de la lámina y escala_unidades: 'ninguna'"


def muro_rectangular(handle: str, capa: str, x0: float, y0: float, x1: float, y1: float) -> dict[str, Any]:
    """LWPOLYLINE cerrada de 4 vértices (un muro de la oficina o de 'muro')."""
    vertices = [[x0, y0, 0.0], [x1, y0, 0.0], [x1, y1, 0.0], [x0, y1, 0.0]]
    ancho, alto = x1 - x0, y1 - y0
    return {
        "handle": handle,
        "tipo": "LWPOLYLINE",
        "capa": capa,
        "bbox": [[x0, y0, 0.0], [x1, y1, 0.0]],
        "n_vertices": 4,
        "cerrada": True,
        "longitud": 2 * (ancho + alto),
        "area": ancho * alto,
        "tiene_arcos": False,
        "vertices": vertices,
    }


def build_entidades_fase4b() -> list[dict[str, Any]]:
    """Entidades de la bandera fase4b, añadidas al dibujo base (handles 2B0–2BE y 2C0; 2BF va en Layout1).

    2B0 muro del usuario de 5 × 0,15 en MUROS. 2B1 muro de 5 × 0,12 en MCP-DIBUJO con sombreado asociativo 2BA. 2B2 apoya
    su tapa en la cara de 2B0 (T). 2B3 forma L con 2B0. 2B4 no toca a nadie y comparte el sombreado 2BB con 2B5. 2B5 toca
    a 2B0 en un solo punto. 2B6 polilínea abierta en U. 2B7 contorno con arcos. 2B8 contorno en L de 6 vértices. 2B9 muro
    contenido en 2B0 (MCP-DIBUJO). 2C0 contorno con una ranura desde su extremo (vértices entre las caras). 2BC línea de eje. 2BD círculo de burbuja. 2BE SPLINE sin puntos acotables.
    """
    abierta = muro_rectangular("2B6", "MUROS", 20.0, 0.0, 20.15, 1.0)
    abierta["cerrada"] = False
    arcos = muro_rectangular("2B7", "MCP-DIBUJO", 22.0, 0.0, 25.0, 0.15)
    arcos["tiene_arcos"] = True
    forma_l = muro_rectangular("2B8", "MCP-DIBUJO", 30.0, 0.0, 35.0, 3.0)
    forma_l["vertices"] = [[30.0, 0.0, 0.0], [35.0, 0.0, 0.0], [35.0, 3.0, 0.0], [34.85, 3.0, 0.0], [34.85, 0.15, 0.0], [30.0, 0.15, 0.0]]
    forma_l["n_vertices"] = 6
    forma_l["longitud"] = 16.0
    forma_l["area"] = 5.0 * 0.15 + 0.15 * 2.85
    ranura = muro_rectangular("2C0", "MCP-DIBUJO", 50.0, 0.0, 55.0, 0.15)
    ranura["vertices"] = [
        [50.0, 0.0, 0.0], [55.0, 0.0, 0.0], [55.0, 0.05, 0.0], [53.0, 0.05, 0.0],
        [53.0, 0.1, 0.0], [55.0, 0.1, 0.0], [55.0, 0.15, 0.0], [50.0, 0.15, 0.0],
    ]
    ranura["n_vertices"] = 8
    hatch_1 = {
        "handle": "2BA",
        "tipo": "HATCH",
        "capa": "MCP-DIBUJO",
        "bbox": [[0.0, 4.0, 0.0], [5.0, 4.12, 0.0]],
        "patron": "ANSI31",
        "area": 0.6,
        "asociativo": True,
        "_contorno": ["2B1"],
        "_escala": 0.05,
        "_angulo": 45.0,
    }
    hatch_2 = {
        "handle": "2BB",
        "tipo": "HATCH",
        "capa": "MCP-DIBUJO",
        "bbox": [[40.0, 0.0, 0.0], [45.0, 0.15, 0.0]],
        "patron": "SOLID",
        "area": 0.75,
        "asociativo": True,
        "_contorno": ["2B4", "2B5"],
        "_escala": 1.0,
        "_angulo": 0.0,
    }
    return [
        muro_rectangular("2B0", "MUROS", 0.0, 0.0, 5.0, 0.15),
        muro_rectangular("2B1", "MCP-DIBUJO", 0.0, 4.0, 5.0, 4.12),
        muro_rectangular("2B2", "MUROS", 2.0, 0.15, 2.12, 3.0),
        muro_rectangular("2B3", "MUROS", 4.85, 0.0, 5.0, 3.0),
        muro_rectangular("2B4", "MCP-DIBUJO", 40.0, 0.0, 45.0, 0.15),
        muro_rectangular("2B5", "MCP-DIBUJO", 5.0, 0.15, 6.0, 0.3),
        abierta,
        arcos,
        forma_l,
        muro_rectangular("2B9", "MCP-DIBUJO", 1.0, 0.05, 2.0, 0.1),
        ranura,
        hatch_1,
        hatch_2,
        _line("2BC", "MCP-DIBUJO", [0.0, 6.0, 0.0], [10.0, 6.0, 0.0]),
        {
            "handle": "2BD",
            "tipo": "CIRCLE",
            "capa": "MCP-DIBUJO",
            "bbox": [[2.9, 5.9, 0.0], [3.1, 6.1, 0.0]],
            "centro": [3.0, 6.0, 0.0],
            "radio": 0.1,
            "longitud": 2 * math.pi * 0.1,
            "area": math.pi * 0.01,
        },
        {"handle": "2BE", "tipo": "SPLINE", "capa": "MCP-DIBUJO", "bbox": [[0.0, 7.0, 0.0], [1.0, 8.0, 0.0]]},
    ]


# -- Lógica pura de Fase 4B (equivalente simplificado de GeometryCore.Gen y GeometryCore.Draw) --


def _norm_vector(a: tuple[float, float], b: tuple[float, float]) -> tuple[float, float]:
    d = math.dist(a, b)
    return (b[0] - a[0]) / d, (b[1] - a[1]) / d


def _direccion_eje(ux: float, uy: float) -> tuple[float, float]:
    """Sentido del eje: del extremo de menor x al de mayor x (a igual x, de menor a mayor y)."""
    if ux > 1e-9 or (abs(ux) <= 1e-9 and uy > 0):
        return ux, uy
    return -ux, -uy


def cortar_muro(v: list[tuple[float, float]], centro: tuple[float, float], ancho: float, tol: float = 1e-9) -> dict[str, Any]:
    """WallOpening.Cut: parte el contorno en dos por un hueco centrado en la proyección de 'centro'.

    Tramo = solape de la cara más cercana (a igual distancia, la más larga) y su cara antiparalela opuesta."""
    n = len(v)
    if n < 4 or _primer_cruce(v, True, tol) is not None:
        raise _WallError(DETALLE_MURO_POLIGONO, False)
    i = min(range(n), key=lambda k: (round(_dist_segmento(centro, v[k], v[(k + 1) % n]), 9), -math.dist(v[k], v[(k + 1) % n])))
    a, b = v[i], v[(i + 1) % n]
    largo = math.dist(a, b)
    ux, uy = _norm_vector(a, b)
    t = (centro[0] - a[0]) * ux + (centro[1] - a[1]) * uy
    # Cara opuesta: antiparalela y con solape; primero la que abarca el centro, luego la más cercana.
    mejor: tuple[Any, ...] | None = None
    for j in range(n):
        c, d = v[j], v[(j + 1) % n]
        if j == i or math.dist(c, d) <= tol:
            continue
        wx, wy = _norm_vector(c, d)
        if abs(ux * wy - uy * wx) > 1e-6 or ux * wx + uy * wy >= 0:
            continue
        t0 = (d[0] - a[0]) * ux + (d[1] - a[1]) * uy
        t1 = (c[0] - a[0]) * ux + (c[1] - a[1]) * uy
        desde, hasta = max(0.0, t0), min(largo, t1)
        espesor = abs((c[0] - a[0]) * -uy + (c[1] - a[1]) * ux)
        if hasta - desde <= tol or espesor <= tol:
            continue
        clave = (0 if desde - tol <= t <= hasta + tol else 1, espesor)
        if mejor is None or clave < mejor[0]:
            mejor = (clave, j, desde, hasta, espesor)
    if mejor is None:
        raise _WallError(DETALLE_MURO_SIN_TRAMO, False)
    _clave, j, desde, hasta, espesor = mejor
    c = v[j]
    signo = 1.0 if (c[0] - a[0]) * -uy + (c[1] - a[1]) * ux > 0 else -1.0
    nx, ny = -uy * signo, ux * signo
    longitud = hasta - desde
    if ancho >= longitud - tol:
        raise _WallError(DETALLE_ANCHO_MENOR, False)
    t1, t2 = t - ancho / 2, t + ancho / 2
    if t1 - desde < 0.01 or t2 > hasta - 0.01:
        raise _WallError(detalle_no_cabe(ancho, longitud), False)
    for k, p in enumerate(v):
        if k in (i, (i + 1) % n, j, (j + 1) % n):
            continue
        tv = (p[0] - a[0]) * ux + (p[1] - a[1]) * uy
        ev = (p[0] - a[0]) * nx + (p[1] - a[1]) * ny
        if t1 - tol <= tv <= t2 + tol and -tol <= ev <= espesor + tol:
            raise _WallError(DETALLE_ANCHO_VERTICE, False)

    def en_cara(s: float) -> tuple[float, float]:
        return a[0] + ux * s, a[1] + uy * s

    def en_opuesta(s: float) -> tuple[float, float]:
        return a[0] + ux * s + nx * espesor, a[1] + uy * s + ny * espesor

    def tramo(ini: int, fin: int) -> list[tuple[float, float]]:
        r = [v[ini % n]]
        k = ini % n
        while k != fin % n:
            k = (k + 1) % n
            r.append(v[k])
        return r

    bucle_mas = [en_cara(t2), *tramo(i + 1, j), en_opuesta(t2)]
    bucle_menos = [en_opuesta(t1), *tramo(j + 1, i), en_cara(t1)]
    dx, dy = _direccion_eje(ux, uy)
    alineado = dx * ux + dy * uy > 0
    lado_inicio, lado_fin = (bucle_menos, bucle_mas) if alineado else (bucle_mas, bucle_menos)
    t_inicio, t_fin = (t1, t2) if alineado else (t2, t1)
    opuesta_izquierda = nx * -dy + ny * dx > 0

    def esquinas(s: float) -> tuple[tuple[float, float], tuple[float, float]]:
        return (en_opuesta(s), en_cara(s)) if opuesta_izquierda else (en_cara(s), en_opuesta(s))

    for bucle in (lado_inicio, lado_fin):
        cruce = _primer_cruce(bucle, True, tol)
        if cruce is not None:
            raise _WallError(f"muro: el contorno resultante se autointerseca (tramos {cruce[0]} y {cruce[1]})", False)
    a1, a2 = esquinas(t_inicio)
    b1, b2 = esquinas(t_fin)
    return {
        "lado_inicio": lado_inicio,
        "lado_fin": lado_fin,
        "a1": a1,
        "a2": a2,
        "b1": b1,
        "b2": b2,
        "centro": (a[0] + ux * t + nx * espesor / 2, a[1] + uy * t + ny * espesor / 2),
        "direccion": (dx, dy),
        "espesor": espesor,
        "longitud_tramo": longitud,
        "distancia_desde_inicio": t - desde if alineado else hasta - t,
    }


def centro_por_distancia(v: list[tuple[float, float]], distancia: float, desde_fin: bool) -> tuple[float, float]:
    """WallOpening.CenterFromDistance: el eje es la línea media entre las dos caras largas de un contorno de 4 vértices."""
    if len(v) != 4:
        raise _WallError(DETALLE_MURO_DISTANCIA_4V, False)
    if math.dist(v[0], v[1]) >= math.dist(v[1], v[2]):
        e0 = ((v[0][0] + v[3][0]) / 2, (v[0][1] + v[3][1]) / 2)
        e1 = ((v[1][0] + v[2][0]) / 2, (v[1][1] + v[2][1]) / 2)
    else:
        e0 = ((v[0][0] + v[1][0]) / 2, (v[0][1] + v[1][1]) / 2)
        e1 = ((v[2][0] + v[3][0]) / 2, (v[2][1] + v[3][1]) / 2)
    ini, fin = sorted([e0, e1])
    longitud = math.dist(ini, fin)
    if distancia > longitud:
        raise _WallError(detalle_distancia_fuera(distancia, longitud), True)
    ux, uy = _norm_vector(ini, fin)
    base, signo = (fin, -1.0) if desde_fin else (ini, 1.0)
    return base[0] + signo * ux * distancia, base[1] + signo * uy * distancia


VECTORES_LOCALES = {"x": (1.0, 0.0), "y": (0.0, 1.0), "+x": (1.0, 0.0), "+y": (0.0, 1.0), "-x": (-1.0, 0.0), "-y": (0.0, -1.0)}


def colocar_bloque(
    hueco: dict[str, Any], spec: dict[str, Any], cara: str, bisagra: str, por_propiedad: bool, ancho: float
) -> dict[str, Any]:
    """OpeningPlacement.Solve + Place: punto base en la cara opuesta a la de apertura y eje de ancho a lo largo del hueco."""
    dx, dy = hueco["direccion"]
    lx, ly = -dy, dx
    # Abre a la izquierda → punto base en la cara derecha (A2/B2) y el espesor entra hacia la izquierda.
    izquierda = cara == "izquierda"
    posicion = hueco[("a" if bisagra == "inicio" else "b") + ("2" if izquierda else "1")]
    a_lo_largo = (dx, dy) if bisagra == "inicio" else (-dx, -dy)
    hacia_dentro = (lx, ly) if izquierda else (-lx, -ly)
    u = VECTORES_LOCALES[spec["eje_ancho"]]
    v = VECTORES_LOCALES[spec["direccion_espesor"]]
    giro = math.atan2(a_lo_largo[1], a_lo_largo[0]) - math.atan2(u[1], u[0])
    cos_g, sin_g = math.cos(giro), math.sin(giro)
    vr = (v[0] * cos_g - v[1] * sin_g, v[0] * sin_g + v[1] * cos_g)
    sy = 1.0 if vr[0] * hacia_dentro[0] + vr[1] * hacia_dentro[1] > 0 else -1.0
    factor = None if por_propiedad else ancho / float(spec["ancho_nominal"])
    eje_ancho = 0 if abs(u[0]) > 0 else 1
    escala = [1.0, 1.0, 1.0]
    escala[1 - eje_ancho] = sy
    escala[eje_ancho] = 1.0 if factor is None else factor
    return {
        "posicion": [posicion[0], posicion[1], 0.0],
        "rotacion": _norm360(math.degrees(giro)),
        "escala": escala,
        "espejo": sy < 0,
        "factor_ancho": factor,
    }


def caja_bloque_vano(hueco: dict[str, Any], cara: str, alcance: float) -> list[list[float]]:
    """OpeningPlacement.EstimatedBox: rectángulo del hueco ampliado hacia la cara de apertura."""
    dx, dy = hueco["direccion"]
    signo = 1.0 if cara == "izquierda" else -1.0
    ox, oy = -dy * signo * alcance, dx * signo * alcance
    esquinas = [hueco[k] for k in ("a1", "a2", "b1", "b2")]
    puntos = [list(p) for p in esquinas] + [[p[0] + ox, p[1] + oy] for p in esquinas]
    return _box_of(puntos)


def _marco_caras(muro: tuple[Any, Any], opuesta: tuple[Any, Any], tol: float = 1e-9) -> dict[str, Any] | None:
    """LineWallOpening.TryFrame: eje con inicio en el extremo de menor x, solape y separación con signo."""
    (m1, m2), (o1, o2) = muro, opuesta
    if math.dist(m1, m2) <= tol or math.dist(o1, o2) <= tol:
        return None
    ux, uy = _norm_vector(m1, m2)
    vx, vy = _norm_vector(o1, o2)
    if abs(ux * vy - uy * vx) > 1e-4:
        return None
    dx, dy = (ux, uy) if (ux > 1e-9 or (abs(ux) <= 1e-9 and uy > 0)) else (-ux, -uy)
    nx, ny = -dy, dx
    eje = lambda p: (p[0] - m1[0]) * dx + (p[1] - m1[1]) * dy  # noqa: E731
    normal = lambda p: (p[0] - m1[0]) * nx + (p[1] - m1[1]) * ny  # noqa: E731
    tm, to = [eje(m1), eje(m2)], [eje(o1), eje(o2)]
    lo, hi = max(min(tm), min(to)), min(max(tm), max(to))
    if hi - lo <= tol:
        return None
    return {
        "dir": (dx, dy), "normal": (nx, ny), "origen": m1, "lo": lo, "hi": hi, "sep": (normal(o1) + normal(o2)) / 2,
        "tm": tm, "to": to, "muro": muro, "opuesta": opuesta,
    }


def _punto_cara(cara: tuple[Any, Any], ts: list[float], t: float) -> tuple[float, float]:
    u = (t - ts[0]) / (ts[1] - ts[0])
    return (cara[0][0] + u * (cara[1][0] - cara[0][0]), cara[0][1] + u * (cara[1][1] - cara[0][1]))


def _marco_caras_o_rechazo(muro: tuple[Any, Any], opuesta: tuple[Any, Any], hm: str, ho: str) -> dict[str, Any]:
    (m1, m2), (o1, o2) = muro, opuesta
    if math.dist(m1, m2) <= 1e-9 or math.dist(o1, o2) <= 1e-9:
        raise _WallError("muro: la línea tiene longitud 0", False)
    ux, uy = _norm_vector(m1, m2)
    vx, vy = _norm_vector(o1, o2)
    if abs(ux * vy - uy * vx) > 1e-4:
        raise _WallError(f"cara_opuesta: la línea {ho} no es paralela a la cara del muro", True)
    f = _marco_caras(muro, opuesta)
    if f is None:
        raise _WallError(f"cara_opuesta: las líneas {hm} y {ho} no se solapan a lo largo del tramo", True)
    if abs(f["sep"]) <= 1e-9:
        raise _WallError(f"cara_opuesta: las líneas {hm} y {ho} coinciden", True)
    return f


def _centro_linea_media(f: dict[str, Any], t: float) -> tuple[float, float]:
    (dx, dy), (nx, ny), o = f["dir"], f["normal"], f["origen"]
    return (o[0] + (f["lo"] + t) * dx + f["sep"] / 2 * nx, o[1] + (f["lo"] + t) * dy + f["sep"] / 2 * ny)


def centro_caras_por_distancia(muro: Any, opuesta: Any, hm: str, ho: str, distancia: float, desde_fin: bool) -> tuple[float, float]:
    """LineWallOpening.CenterFromDistance."""
    f = _marco_caras_o_rechazo(muro, opuesta, hm, ho)
    largo = f["hi"] - f["lo"]
    if distancia > largo + 1e-9:
        raise _WallError(f"posicion: la distancia {_fmt3(distancia)} se sale del tramo (longitud {_fmt3(largo)})", True)
    return _centro_linea_media(f, largo - distancia if desde_fin else distancia)


def cortar_caras(muro: Any, opuesta: Any, hm: str, ho: str, centro: tuple[float, float], ancho: float) -> dict[str, Any]:
    """LineWallOpening.Cut: el hueco, las cuatro piezas y las dos tapas."""
    f = _marco_caras_o_rechazo(muro, opuesta, hm, ho)
    largo = f["hi"] - f["lo"]
    if ancho >= largo - 1e-9:
        raise _WallError("ancho: debe ser menor que la longitud del tramo", False)
    (dx, dy), o = f["dir"], f["origen"]
    t = (centro[0] - o[0]) * dx + (centro[1] - o[1]) * dy - f["lo"]
    t1, t2 = t - ancho / 2, t + ancho / 2
    if t1 < 0.01 or t2 > largo - 0.01:
        raise _WallError(f"ancho: el hueco ({_fmt3(ancho)}) no cabe en el tramo (longitud {_fmt3(largo)}): se sale del tramo o cruza una esquina", False)
    m1, m2 = _punto_cara(muro, f["tm"], f["lo"] + t1), _punto_cara(muro, f["tm"], f["lo"] + t2)
    o1, o2 = _punto_cara(opuesta, f["to"], f["lo"] + t1), _punto_cara(opuesta, f["to"], f["lo"] + t2)
    izq = f["sep"] > 0

    def extremos(cara: Any, ts: list[float]) -> tuple[Any, Any]:
        return (cara[0], cara[1]) if ts[0] <= ts[1] else (cara[1], cara[0])

    mi, mf = extremos(muro, f["tm"])
    oi, of_ = extremos(opuesta, f["to"])
    return {
        "a1": o1 if izq else m1, "a2": m1 if izq else o1, "b1": o2 if izq else m2, "b2": m2 if izq else o2,
        "centro": _centro_linea_media(f, t), "direccion": f["dir"], "espesor": abs(f["sep"]), "longitud_tramo": largo,
        "distancia_desde_inicio": t,
        "piezas": {
            "muro_inicio": (mi, m1), "muro_fin": (m2, mf), "opuesta_inicio": (oi, o1), "opuesta_fin": (o2, of_),
            "tapa_inicio": (m1, o1), "tapa_fin": (m2, o2),
        },
    }


def buscar_cara_opuesta(
    muro: tuple[str, Any], lineas: list[tuple[str, Any]], centro: tuple[float, float] | None, ancho: float
) -> tuple[str | None, list[tuple[str, float]]]:
    """LineWallOpening.FindOpposite: la paralela más cercana de las que abarcan el hueco o, si ninguna, de todas."""
    candidatas: list[tuple[str, float]] = []
    caras = dict(lineas)
    for h, cara in lineas:
        if h.upper() == muro[0].upper():
            continue
        f = _marco_caras(muro[1], cara)
        if f is not None and 1e-9 < abs(f["sep"]) <= 0.4 + 1e-9:
            candidatas.append((h, abs(f["sep"])))
    if not candidatas:
        return None, []

    def abarca(h: str) -> bool:
        f = _marco_caras(muro[1], caras[h])
        assert f is not None and centro is not None
        t = (centro[0] - f["origen"][0]) * f["dir"][0] + (centro[1] - f["origen"][1]) * f["dir"][1]
        return t - ancho / 2 >= f["lo"] - 1e-9 and t + ancho / 2 <= f["hi"] + 1e-9

    abarcan = [c for c in candidatas if abarca(c[0])] if centro is not None else []
    cuentan = abarcan or candidatas
    minimo = min(s for _h, s in cuentan)
    cercanas = [c for c in cuentan if c[1] <= minimo + 1e-3]
    return (cercanas[0][0], candidatas) if len(cercanas) == 1 else (None, cercanas)


def repartir_bucle(corte: dict[str, Any], otras: list[tuple[str, Any, Any]]) -> tuple[list[str], list[str]] | None:
    """LineWallOpening.SplitHatchLoop: cadenas que cierran cada lado del hueco, de la cara opuesta al muro."""
    libres = list(otras)

    def cadena(desde: Any, hasta: Any) -> list[str] | None:
        r: list[str] = []
        actual = desde
        while math.dist(actual, hasta) > 1e-6:
            k = next((i for i, (_h, p, q) in enumerate(libres) if math.dist(p, actual) <= 1e-6 or math.dist(q, actual) <= 1e-6), None)
            if k is None:
                return None
            h, p, q = libres.pop(k)
            actual = q if math.dist(p, actual) <= 1e-6 else p
            r.append(h)
        return r

    p = corte["piezas"]
    inicio = cadena(p["opuesta_inicio"][0], p["muro_inicio"][0])
    fin = None if inicio is None else cadena(p["opuesta_fin"][1], p["muro_fin"][1])
    return None if inicio is None or fin is None or libres else (inicio, fin)


def es_cuadrilatero_convexo(v: list[tuple[float, float]], tol: float = 1e-6) -> bool:
    """WallJoin.IsConvexQuad."""
    if len(v) != 4 or abs(_shoelace(v)) <= tol * tol:
        return False
    signos = []
    for i in range(4):
        a, b, c = v[i], v[(i + 1) % 4], v[(i + 2) % 4]
        cruz = (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0])
        if abs(cruz) <= tol * tol:
            return False
        signos.append(cruz > 0)
    return len(set(signos)) == 1


def _antihorario(v: list[tuple[float, float]]) -> list[tuple[float, float]]:
    area = sum(v[i][0] * v[(i + 1) % len(v)][1] - v[(i + 1) % len(v)][0] * v[i][1] for i in range(len(v)))
    return list(v) if area > 0 else list(reversed(v))


def _recortar_convexo(sujeto: list[tuple[float, float]], recorte: list[tuple[float, float]]) -> list[tuple[float, float]]:
    """Sutherland–Hodgman: intersección de dos polígonos convexos antihorarios (para area_solape)."""
    salida = list(sujeto)
    for i in range(len(recorte)):
        a, b = recorte[i], recorte[(i + 1) % len(recorte)]

        def lado(p: tuple[float, float]) -> float:
            return (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0])

        entrada, salida = salida, []
        for k, p in enumerate(entrada):
            q = entrada[(k + 1) % len(entrada)]
            lp, lq = lado(p), lado(q)
            if lp >= 0:
                salida.append(p)
            if (lp > 0 and lq < 0) or (lp < 0 and lq > 0):
                s = lp / (lp - lq)
                salida.append((p[0] + (q[0] - p[0]) * s, p[1] + (q[1] - p[1]) * s))
        if not salida:
            return []
    return salida


def _tramos_de_arista(
    p: tuple[float, float], q: tuple[float, float], otro: list[tuple[float, float]], tol: float
) -> list[tuple[tuple[float, float], tuple[float, float]]]:
    """Parte la arista p→q por el borde del otro polígono."""
    longitud = math.dist(p, q)
    ux, uy = _norm_vector(p, q)
    cortes = [0.0, longitud]
    for i in range(len(otro)):
        a, b = otro[i], otro[(i + 1) % len(otro)]
        if _dist_segmento(a, p, q) <= tol:
            cortes.append((a[0] - p[0]) * ux + (a[1] - p[1]) * uy)
        wx, wy = b[0] - a[0], b[1] - a[1]
        den = ux * wy - uy * wx
        if abs(den) <= 1e-12:
            continue
        s = ((a[0] - p[0]) * wy - (a[1] - p[1]) * wx) / den
        r = ((a[0] - p[0]) * uy - (a[1] - p[1]) * ux) / den
        if 0 < s < longitud and 0 <= r <= 1:
            cortes.append(s)
    cortes = sorted(cortes)
    tramos = []
    for s0, s1 in zip(cortes, cortes[1:]):
        if s1 - s0 > tol:
            tramos.append(((p[0] + ux * s0, p[1] + uy * s0), (p[0] + ux * s1, p[1] + uy * s1)))
    return tramos


def _simplificar_lazo(v: list[tuple[float, float]], tol: float) -> list[tuple[float, float]]:
    """WallJoin.SimplifyLoop: quita duplicados y colineales."""
    r, _n = _colapsar_duplicados(v, tol, True)
    cambio = True
    while cambio and len(r) > 2:
        cambio = False
        for i in range(len(r)):
            a, b, c = r[i - 1], r[i], r[(i + 1) % len(r)]
            if _dist_segmento(b, a, c) <= tol:
                r.pop(i)
                cambio = True
                break
    return r


def _contacto_muro(
    poly: list[tuple[float, float]], otro: list[tuple[float, float]], tol: float
) -> tuple[dict[int, tuple[float, float]], int]:
    """WallJoin.ReadContact: tramo en contacto por arista y número de trozos del borde que quedan fuera del otro."""
    aristas: dict[int, tuple[float, float]] = {}
    fuera: list[bool] = []
    for i in range(len(poly)):
        p, q = poly[i], poly[(i + 1) % len(poly)]
        for r, s in _tramos_de_arista(p, q, otro, tol):
            mx, my = (r[0] + s[0]) / 2, (r[1] + s[1]) / 2
            es_fuera = _dist_contorno(otro, mx, my) > tol and not _contiene(otro, mx, my)
            fuera.append(es_fuera)
            if not es_fuera:
                d0, d1 = math.dist(p, r), math.dist(p, s)
                previo = aristas.get(i, (d0, d1))
                aristas[i] = (min(previo[0], d0), max(previo[1], d1))
    tramos = sum(1 for j, f in enumerate(fuera) if f and not fuera[j - 1])
    return aristas, max(1, tramos)


def _es_muro_pasante(
    muro: list[tuple[float, float]], aristas: dict[int, tuple[float, float]], tramos_otro: int, tol: float
) -> bool:
    """WallJoin.IsThroughWall: el otro termina en una cara (o dos opuestas) y la cara sigue a los dos lados más que el ancho."""
    if tramos_otro != 1 or not 1 <= len(aristas) <= 2:
        return False
    indices = sorted(aristas)
    if len(indices) == 2 and indices[1] - indices[0] != 2:
        return False
    for i, (desde, hasta) in aristas.items():
        p, q = muro[i], muro[(i + 1) % len(muro)]
        longitud = math.dist(p, q)
        ancho = max(abs((q[0] - p[0]) * (v[1] - p[1]) - (q[1] - p[1]) * (v[0] - p[0])) / longitud for v in muro)
        if min(desde, longitud - hasta) <= ancho + tol:
            return False
    return True


def _tipo_encuentro(a: list[tuple[float, float]], b: list[tuple[float, float]], n: int, tol: float) -> str:
    """WallJoin.Classify: tipo por la topología del contacto, no por el número de vértices (§5G.2)."""
    if n == 4:
        return "colineal"
    aristas_a, tramos_a = _contacto_muro(a, b, tol)
    aristas_b, tramos_b = _contacto_muro(b, a, tol)
    if tramos_a > 2 or tramos_b > 2:
        raise _WallError(f"union: encuentro no reconocido ({n} vértices)", False)
    if tramos_a == 2 and tramos_b == 2:
        return "cruce"
    return "T" if _es_muro_pasante(a, aristas_a, tramos_b, tol) or _es_muro_pasante(b, aristas_b, tramos_a, tol) else "L"


def unir_cuadrilateros(
    va: list[tuple[float, float]], vb: list[tuple[float, float]], tipo: str, tol: float = 1e-6
) -> dict[str, Any]:
    """WallJoin.Union: aristas de A fuera de B + aristas de B fuera de A, encadenadas; lo apoyado se descarta."""
    a, b = _antihorario(va), _antihorario(vb)
    area_a, area_b = abs(_shoelace(a)), abs(_shoelace(b))
    comun = _recortar_convexo(a, b)
    area_solape = abs(_shoelace(comun)) if len(comun) >= 3 else 0.0
    if area_solape >= min(area_a, area_b) * (1 - 1e-9):
        raise _WallError(DETALLE_UNION_CONTENIDO, False)
    conservados: list[tuple[tuple[float, float], tuple[float, float]]] = []
    apoyo = 0.0

    def recorrer(p: list[tuple[float, float]], q: list[tuple[float, float]], de_a: bool) -> None:
        nonlocal apoyo
        for i in range(len(p)):
            for r, s in _tramos_de_arista(p[i], p[(i + 1) % len(p)], q, tol):
                m = ((r[0] + s[0]) / 2, (r[1] + s[1]) / 2)
                k = min(range(len(q)), key=lambda e: _dist_segmento(m, q[e], q[(e + 1) % len(q)]))
                if _dist_segmento(m, q[k], q[(k + 1) % len(q)]) <= tol:
                    wx, wy = _norm_vector(q[k], q[(k + 1) % len(q)])
                    if (s[0] - r[0]) * wx + (s[1] - r[1]) * wy < 0:
                        apoyo += math.dist(r, s)  # Tapa apoyada en la cara: queda dentro de la unión.
                    elif de_a:
                        conservados.append((r, s))  # Mismo sentido: el borde común se conserva solo desde A.
                elif not _contiene(q, m[0], m[1]):
                    conservados.append((r, s))

    recorrer(a, b, True)
    recorrer(b, a, False)
    if area_solape <= tol * tol and apoyo <= tol:
        dist = min(_dist_contorno(b, p[0], p[1]) for p in a)
        dist = min(dist, min(_dist_contorno(a, p[0], p[1]) for p in b))
        raise _WallError(DETALLE_UNION_PUNTO if dist <= tol else DETALLE_UNION_NO_TOCAN, False)
    # Encadenado por extremos.
    lazo = [conservados[0][0]]
    actual = conservados.pop(0)[1]
    while conservados:
        k = next((i for i, (r, _s) in enumerate(conservados) if math.dist(r, actual) <= tol), None)
        if k is None:
            raise _WallError(DETALLE_UNION_NO_SIMPLE, False)
        lazo.append(conservados[k][0])
        actual = conservados.pop(k)[1]
    if math.dist(actual, lazo[0]) > tol:
        raise _WallError(DETALLE_UNION_NO_SIMPLE, False)
    contorno = _simplificar_lazo(lazo, tol)
    area_union = abs(_shoelace(contorno))
    if len(contorno) < 3 or _primer_cruce(contorno, True) is not None:
        raise _WallError(DETALLE_UNION_NO_SIMPLE, False)
    margen = tol * sum(math.dist(p[i], p[(i + 1) % len(p)]) for p in (a, b) for i in range(len(p)))
    if abs(area_union - (area_a + area_b - area_solape)) > 1e-6 * area_union + margen:
        raise _WallError(DETALLE_UNION_NO_SIMPLE, False)
    n = len(contorno)
    detectado = _tipo_encuentro(a, b, n, tol)
    if tipo != "auto" and tipo != detectado:
        raise _WallError(f"tipo: los muros forman un encuentro en {detectado}, no en {tipo}", False)
    return {
        "contorno": contorno,
        "tipo_detectado": detectado,
        "area_a": area_a,
        "area_b": area_b,
        "area_solape": area_solape,
        "area_union": area_union,
        "n_vertices": n,
    }


def direccion_dominante(puntos: list[tuple[float, float]], tol: float = 1e-3) -> float:
    """DimensionChain.DominantDirection: grados en [0, 180) del segmento consecutivo más largo."""
    mejor, grados = tol, 0.0
    for p, q in zip(puntos, puntos[1:]):
        d = math.dist(p, q)
        if d > mejor:
            mejor = d
            grados = math.degrees(math.atan2(q[1] - p[1], q[0] - p[0])) % 180.0
    return grados


def cadena_de_cotas(
    puntos: list[tuple[float, float]],
    grados: float,
    lado: str | None,
    lado_punto: list[float] | None,
    separacion: float,
    cadena: bool,
    tol: float,
) -> dict[str, Any]:
    """DimensionChain.Build: proyecta, ordena, funde y coloca la línea de cota a 'separacion' del punto extremo."""
    ux, uy = math.cos(math.radians(grados)), math.sin(math.radians(grados))
    nx, ny = -uy, ux
    if lado_punto is not None:
        cx = sum(p[0] for p in puntos) / len(puntos)
        cy = sum(p[1] for p in puntos) / len(puntos)
        signo = 1.0 if (lado_punto[0] - cx) * nx + (lado_punto[1] - cy) * ny >= 0 else -1.0
    else:
        signo = 1.0 if lado == "izquierda" else -1.0
    proyectados = sorted(puntos, key=lambda p: p[0] * ux + p[1] * uy)
    extremo = max(signo * (p[0] * nx + p[1] * ny) for p in puntos) + separacion
    grupos: list[list[tuple[float, float]]] = []
    for p in proyectados:
        t = p[0] * ux + p[1] * uy
        if grupos and t - (grupos[-1][0][0] * ux + grupos[-1][0][1] * uy) < tol:
            grupos[-1].append(p)
        else:
            grupos.append([p])
    if len(grupos) < 2:
        raise _invalid(DETALLE_PUNTOS_COTA)
    # P1/P2 = el punto de cada grupo más cercano a la línea de cota.
    reps = [max(g, key=lambda p: signo * (p[0] * nx + p[1] * ny)) for g in grupos]
    pares = list(zip(reps, reps[1:])) if cadena else [(reps[0], reps[-1])]
    cotas = []
    for p, q in pares:
        tp, tq = p[0] * ux + p[1] * uy, q[0] * ux + q[1] * uy
        t = (tp + tq) / 2
        cotas.append(
            {
                "p1": [p[0], p[1], 0.0],
                "p2": [q[0], q[1], 0.0],
                "linea": [ux * t + nx * signo * extremo, uy * t + ny * signo * extremo, 0.0],
                "medida": abs(tq - tp),
            }
        )
    return {
        "cotas": cotas,
        "medidas": [c["medida"] for c in cotas],
        "total": sum(c["medida"] for c in cotas),
        "lado": "izquierda" if signo > 0 else "derecha",
        "n_fundidos": len(puntos) - len(grupos),
    }


def calcular_aspecto(ent: dict[str, Any], origen: dict[str, Any], destino: dict[str, Any]) -> dict[str, Any]:
    """LayerAppearance.Compute: solo se fija lo que es ByLayer y difiere entre la capa origen y la destino."""
    fijar: dict[str, Any] = {}
    color = ent.get("_color")
    if (color is None or color == 256) and origen["color"] != destino["color"]:
        fijar["_color"] = origen["color"]
    tipo = ent.get("_tipo_linea")
    if (tipo is None or str(tipo).casefold() == "bylayer") and str(origen["tipo_linea"]).casefold() != str(destino["tipo_linea"]).casefold():
        fijar["_tipo_linea"] = origen["tipo_linea"]
    if ent.get("_grosor") is None and origen["grosor"] != destino["grosor"]:
        fijar["_grosor"] = origen["grosor"]
    if ent.get("_transparencia") is None and abs(origen.get("transparencia", 0.0) - destino.get("transparencia", 0.0)) > 1e-9:
        fijar["_transparencia"] = origen.get("transparencia", 0.0)
    return {"fijar": fijar, "pierde_no_plot": origen.get("plot", True) is False and destino.get("plot", True) is not False}

# Textos exactos de §5F (los que dependen de datos).
def coord_l(v: float) -> str:
    """ViewportImpact.Coord: formato "0.##" InvariantCulture."""
    txt = f"{round(v, 2):.2f}".rstrip("0").rstrip(".")
    return "0" if txt in ("-0", "") else txt


def advertencia_viewport(layout: str, handle: str, estado: str, ventana: list[list[float]], nb: int, nc: int) -> str | None:
    """ViewportImpact.Warning (§5F.1); None para "sin_cambio"."""
    caja = f"[[{coord_l(ventana[0][0])}, {coord_l(ventana[0][1])}], [{coord_l(ventana[1][0])}, {coord_l(ventana[1][1])}]]"
    base = f"El viewport {handle} del layout '{layout}' mira la ventana de modelo {caja} que quedará "
    if estado == "vacia":
        return base + f"vacía: se borran {nb} entidades que la intersecan."
    if estado == "parcial":
        return base + f"parcialmente borrada: se borran {nb} entidades que la intersecan y se conservan {nc}."
    return None


def estado_viewport(nb: int, nc: int) -> str:
    """ViewportImpact.Classify."""
    if nb == 0:
        return "sin_cambio"
    return "vacia" if nc == 0 else "parcial"


def advertencia_sin_caja(por_tipo: dict[str, int]) -> str:
    n = sum(por_tipo.values())
    detalle = ", ".join(f"{t} {c}" for t, c in por_tipo.items())
    return f"{n} entidad(es) sin caja calculable se conservaron ({detalle})."


def advertencia_tramos_curvos(n: int) -> str:
    return f"contorno: la polilínea tiene {n} tramo(s) curvo(s); se evaluaron por sus cuerdas."


def advertencia_viewports_conservados(n: int, layout: str) -> str:
    return f"Se conservaron {n} viewport(s) del layout {layout}: borrar_fuera_de no borra viewports."


def advertencia_congeladas_borradas(n: int) -> str:
    return f"{n} entidad(es) en capas congeladas o apagadas se borraron."


def detalle_capas_bloqueadas(conteo: dict[str, int]) -> str:
    partes = ", ".join(f"{c} ({n})" for c, n in conteo.items())
    return f"capas bloqueadas o de xref con entidades a borrar: {partes}; desbloquéelas o páselas en excluir_capas"


def detalle_categoria(i: int) -> str:
    return f"categorias[{i}]: debe ser uno de bloques, capas, tipos_linea, estilos_texto, estilos_cota, regapps, todo"


def advertencia_excluir_inexistente(nombre: str) -> str:
    return f"excluir: no existe ningún símbolo llamado '{nombre}'."


def advertencia_purga_difiere(no_purgados: int, no_previstos: int) -> str:
    return (
        f"La purga real difiere de la previsualización: {no_purgados} símbolo(s) previstos no se purgaron "
        f"(referenciados por xdata, diccionarios o bloques dinámicos) y {no_previstos} no previstos sí."
    )


def advertencia_propiedades_forzadas(n: int) -> str:
    """LayerMapValidator.ForcedPropertiesWarning."""
    return f"{n} entidad(es) reasignada(s) conservan color, tipo de línea o grosor propios (no ByLayer); no se cambiaron."


def advertencia_anonimos_dinamicos(n: int) -> str:
    return f"{n} definición(es) anónima(s) de bloques dinámicos conservan sus capas."


def advertencia_origen_no_borrable(capa: str) -> str:
    """Texto de NormalizarCapasHandler (§5F.3; hasta 0.6.0 el simulador usaba un texto propio)."""
    return (
        f"borrar_capas_origen_vacias: la capa '{capa}' se conserva aunque quede vacía (capa 0, DEFPOINTS o capa actual)."
    )


# Ámbito de normalizar_capas (§5F.3, 0.6.1; textos de GeometryCore LayerMapScope).
DETALLE_ESPACIOS_FORMA = 'espacios: debe ser un array de 1 a 100 nombres ("modelo" o nombres de layout)'


def detalle_espacios_item(i: int) -> str:
    return f"espacios[{i}]: debe ser una cadena de 1 a 255 caracteres"


def detalle_espacio_repetido(x: str) -> str:
    return f"espacios: espacio repetido '{x}'"


def detalle_espacios_inexistentes(faltan: list[str], layouts: list[str]) -> str:
    return f"espacios: layouts inexistentes: {', '.join(faltan)} (espacios del dibujo: {', '.join(['modelo', *layouts])})"


def _viewport_papel(entities: list[dict[str, Any]]) -> dict[str, Any] | None:
    """LayoutViewports.PaperViewportId / ViewportWindow.TryResolvePaperViewport (0.6.1): (1) el primero de
    Layout.GetViewports() (aquí, la marca "_papel_getviewports"); (2) Number = 1 ("_numero"); (3) respaldo: el primero
    del layout. Simplificación del simulador: su dibujo base no tiene viewport de papel (1C8 sin "_numero" = 2), así
    que el respaldo solo se aplica si ese primero no tiene número (≤ 0), como ocurre en AutoCAD en un layout no actual."""
    vps = [e for e in entities if e["tipo"] == "VIEWPORT"]
    for e in vps:
        if e.get("_papel_getviewports"):
            return e
    for e in vps:
        if e.get("_numero", 2) == 1:
            return e
    if vps and vps[0].get("_numero", 2) <= 0:
        return vps[0]
    return None


def advertencia_viewports_excluidos(por_layout: list[tuple[str, int]]) -> str | None:
    """LayerMapScope.ExcludedViewportsWarning (omite los layouts con 0)."""
    partes = [(l, n) for l, n in por_layout if n > 0]
    total = sum(n for _l, n in partes)
    if total == 0:
        return None
    detalle = ", ".join(f"{l} {n}" for l, n in partes)
    return (
        f"{total} viewport(s) de layout en capas origen no se reasignaron ({detalle}); "
        "pase incluir_viewports: true para reasignarlos."
    )


def detalle_destino_no_creable(origen: str, destino: str) -> str:
    """LayerMapValidator.MissingDestination (propio de normalizar_capas; el de la Fase 3 no cambia)."""
    return f"mapa['{origen}']: la capa destino '{destino}' no existe y no está en lps.json (capas_permitidas)"


def detalle_destino_mcp(origen: str, destino: str) -> str:
    """LayerMapValidator.McpDestination."""
    return (
        f"mapa['{origen}']: la capa destino '{destino}' es una capa del MCP (MCP-*); las entidades del usuario no se "
        "mueven a capas del MCP"
    )


def detalle_origen_mcp(origen: str, real: str) -> str:
    """LayerMapValidator.McpOrigin."""
    return (
        f"mapa['{origen}']: la capa origen '{real}' es una capa del MCP (MCP-*); sus entidades no se mueven a capas "
        "del usuario"
    )


def es_capa_mcp(nombre: str) -> bool:
    """LayerMapValidator.IsMcpLayer: empieza por MCP- (sin distinguir mayúsculas) o está en capas_mcp de lps.json."""
    if nombre.casefold().startswith("mcp-"):
        return True
    return any(k.casefold() == nombre.casefold() for k in (LPS.get("capas_mcp") or {}))


def detalle_cadena(destino: str) -> str:
    return (
        f"mapa: el destino '{destino}' es también origen (cadena o ciclo); aplane el mapa para que cada origen vaya "
        "directamente a su capa final"
    )


def detalle_no_marcado(i: int, nombre: str, motivo: str) -> str:
    return (
        f"bloques[{i}]: '{nombre}' no está marcado por auditar_bloques como metadato de unidades incorrecto ({motivo})"
    )


# Dibujo ficticio de la Fase L (bandera faseL de FakePluginState; sustituye al dibujo base para no colisionar con
# los handles 1C0–1C3 del cuadro de áreas): contorno 1C0 (rectángulo [10,10]-[20,20] en MCP-DIBUJO), dos entidades
# dentro (1C1 LINE MUROS, 1C2 TEXT TEXTOS), tres fuera (1C3 LINE MUEBLE con color propio, 1C4 INSERT 'erere' en
# MUEBLE con un ATTRIB en MUEBLE y otro en 0, 1C5 CIRCLE Layer7), una que cruza (1C6 LINE MUROS [15,15]-[25,15]) y una
# sin caja (1C7 REGION). En Layout1 el VIEWPORT 1C8 (capa Layer7) mira la ventana de modelo [0,0]-[30,30] → parcial.
def build_drawing_faseL() -> list[dict[str, Any]]:
    contorno = {
        "handle": "1C0",
        "tipo": "LWPOLYLINE",
        "capa": "MCP-DIBUJO",
        "bbox": [[10.0, 10.0, 0.0], [20.0, 20.0, 0.0]],
        "n_vertices": 4,
        "cerrada": True,
        "longitud": 40.0,
        "area": 100.0,
        "tiene_arcos": False,
        "vertices": [[10.0, 10.0, 0.0], [20.0, 10.0, 0.0], [20.0, 20.0, 0.0], [10.0, 20.0, 0.0]],
    }
    texto = {
        "handle": "1C2",
        "tipo": "TEXT",
        "capa": "TEXTOS",
        "bbox": [[13.0, 15.0, 0.0], [16.0, 15.5, 0.0]],
        "texto": "SALA",
        "altura": 0.5,
        "posicion": [13.0, 15.0, 0.0],
        "rotacion": 0.0,
        "_estilo": "Arial",
    }
    mueble = _line("1C3", "MUEBLE", [25.0, 25.0, 0.0], [28.0, 25.0, 0.0])
    mueble["_color"] = 1
    attrib_mueble = _attrib("1CA", "REF", "M-01", [2.0, 2.2, 0.0])
    attrib_mueble["capa"] = "MUEBLE"
    insert = _insert(
        "1C4", "MUEBLE", "erere", [2.0, 2.0, 0.0], 0.0, [[1.0, 1.0, 0.0], [3.0, 3.0, 0.0]],
        [attrib_mueble, _attrib("1CB", "COD", "X", [2.0, 1.8, 0.0])],
    )
    circulo = {
        "handle": "1C5",
        "tipo": "CIRCLE",
        "capa": "Layer7",
        "bbox": [[39.0, 39.0, 0.0], [41.0, 41.0, 0.0]],
        "centro": [40.0, 40.0, 0.0],
        "radio": 1.0,
        "longitud": 2 * math.pi,
        "area": math.pi,
    }
    region = {"handle": "1C7", "tipo": "REGION", "capa": "MUROS", "bbox": None, "area": None}
    return [
        contorno,
        _line("1C1", "MUROS", [12.0, 12.0, 0.0], [18.0, 12.0, 0.0]),
        texto,
        mueble,
        insert,
        circulo,
        _line("1C6", "MUROS", [15.0, 15.0, 0.0], [25.0, 15.0, 0.0]),
        region,
    ]


VIEWPORT_FASEL = {"handle": "1C8", "tipo": "VIEWPORT", "capa": "Layer7", "bbox": [[10.0, 10.0, 0.0], [200.0, 150.0, 0.0]]}
VIEWPORTS_LAYOUT_FASEL = [{"layout": "Layout1", "handle": "1C8", "ventana": [[0.0, 0.0], [30.0, 30.0]]}]


def _capas_faseL() -> list[dict[str, Any]]:
    """Capas que añade el dibujo de Fase L (además de FAKE_LAYERS)."""
    capas = [
        # Estándar v2 (2026-10-28): MUEBLE ya no está en capas_permitidas (sí en la v1) → fuera_de_estandar con la
        # sugerencia por palabra clave MUEBLE → mobiliario_fijo, como LayerClassifier.
        _layer("MUEBLE", 2, -3, "fuera_de_estandar", "muebles fijos"),
        _layer("Layer7", 7, -3, "generica"),
        _layer("CAPAVACIA", 7, -3, "fuera_de_estandar"),
        _layer("CAPABLOQUE", 7, -3, "fuera_de_estandar"),
        _layer("DEFPOINTS", 7, -3, "estandar"),
    ]
    capas[2]["tipo_linea"] = "DASHED"  # solo la usa CAPAVACIA → la libera la ronda 2 de purgar
    return capas


# Definiciones de bloque que añade el dibujo de Fase L. "_contenido" = entidades del interior de la definición
# (capa, tipo, bloque anidado): 'erere' contiene una LWPOLYLINE cerrada 1C9 (contorno dentro de un bloque) en MUEBLE;
# BASURA_jgytfyuh (ya en BLOCK_DEFINITIONS) contiene en Fase L una inserción de BASURA_ANIDADO en CAPABLOQUE
# (bloque y capa que libera la ronda 2); *U1 (anónimo de un bloque dinámico) contiene una LINE en MUEBLE.
BLOQUES_FASEL: list[dict[str, Any]] = [
    {
        "nombre": "erere",
        "n_inserciones": 1,
        "tags": ["REF", "COD"],
        "tiene_atributos": True,
        "n_entidades": 3,
        "es_xref": False,
        "anonimo": False,
        "dinamico": False,
        "unidades_bloque": 6,
        "comentario": "",
        "_contenido": [
            {
                "handle": "1C9",
                "tipo": "LWPOLYLINE",
                "capa": "MUEBLE",
                "cerrada": True,
                "vertices": [[0.0, 0.0, 0.0], [1.0, 0.0, 0.0], [1.0, 1.0, 0.0]],
                "_color": 3,
            }
        ],
    },
    {
        "nombre": "BASURA_ANIDADO",
        "n_inserciones": 1,
        "tags": [],
        "tiene_atributos": False,
        "n_entidades": 2,
        "es_xref": False,
        "anonimo": False,
        "dinamico": False,
        "unidades_bloque": 6,
        "comentario": "",
        "_contenido": [{"handle": "1CE", "tipo": "LINE", "capa": "CAPABLOQUE"}],
    },
]
CONTENIDO_FASEL: dict[str, list[dict[str, Any]]] = {
    "BASURA_jgytfyuh": [{"handle": "1CC", "tipo": "INSERT", "capa": "CAPABLOQUE", "nombre": "BASURA_ANIDADO"}],
    "*U1": [{"handle": "1CD", "tipo": "LINE", "capa": "MUEBLE"}],
}
# Bandera purga_dinamica: definición con nombre de un bloque dinámico sin inserciones directas. El grafo de la
# previsualización la da por purgable; Database.Purge (escritura) la conserva por sus representaciones anónimas.
BLOQUE_DINAMICO_FASEL: dict[str, Any] = {
    "nombre": "PUERTA_DIN",
    "n_inserciones": 0,
    "tags": [],
    "tiene_atributos": False,
    "n_entidades": 7,
    "es_xref": False,
    "anonimo": False,
    "dinamico": True,
    "unidades_bloque": 6,
    "comentario": "",
}


# -- Geometría de borrar_fuera_de (BoxPolygon.Classify simplificado) --
def _clip_segmento(a: tuple[float, float], b: tuple[float, float], box: tuple[float, float, float, float], tol: float):
    """Liang–Barsky: (t0, t1) de la parte del segmento a-b dentro de la caja cerrada (ampliada tol), o None."""
    x0, y0, x1, y1 = box[0] - tol, box[1] - tol, box[2] + tol, box[3] + tol
    dx, dy = b[0] - a[0], b[1] - a[1]
    t0, t1 = 0.0, 1.0
    for p, q in ((-dx, a[0] - x0), (dx, x1 - a[0]), (-dy, a[1] - y0), (dy, y1 - a[1])):
        if abs(p) <= 1e-15:
            if q < 0:
                return None
            continue
        r = q / p
        if p < 0:
            t0 = max(t0, r)
        else:
            t1 = min(t1, r)
    return (t0, t1) if t0 <= t1 else None


def clasificar_caja(poly: list[tuple[float, float]], bbox: Any, tol: float = 1e-9) -> str:
    """'dentro' | 'fuera' | 'cruza' de una caja 2D frente al polígono del contorno (§5F.1)."""
    x0, y0, x1, y1 = bbox[0][0], bbox[0][1], bbox[1][0], bbox[1][1]
    box = (x0, y0, x1, y1)
    esquinas = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
    dentro_o_borde = [_contiene(poly, x, y) for x, y in esquinas]
    n = len(poly)
    aristas = [(poly[i], poly[(i + 1) % n]) for i in range(n)]
    if all(dentro_o_borde):
        corta_interior = False
        for a, b in aristas:
            clip = _clip_segmento(a, b, box, tol)
            if clip is None:
                continue
            t0, t1 = clip
            if (t1 - t0) * math.dist(a, b) <= tol:
                continue
            tm = (t0 + t1) / 2
            mx, my = a[0] + tm * (b[0] - a[0]), a[1] + tm * (b[1] - a[1])
            if x0 + tol < mx < x1 - tol and y0 + tol < my < y1 - tol:
                corta_interior = True
                break
        if not corta_interior:
            return "dentro"
    if not any(dentro_o_borde) and all(_clip_segmento(a, b, box, tol) is None for a, b in aristas):
        return "fuera"
    return "cruza"


def poligono_circulo(cx: float, cy: float, r: float, lados: int = LADOS_CIRCULO) -> list[tuple[float, float]]:
    """BoxPolygon.CirclePolygon: polígono regular inscrito."""
    return [(cx + r * math.cos(2 * math.pi * k / lados), cy + r * math.sin(2 * math.pi * k / lados)) for k in range(lados)]


def _conteo_ordenado(valores: list[str]) -> dict[str, int]:
    """CleanWrite.SortedCounts: n descendente y nombre sin mayúsculas."""
    acc: dict[str, int] = {}
    for v in valores:
        acc[v] = acc.get(v, 0) + 1
    return dict(sorted(acc.items(), key=lambda kv: (-kv[1], kv[0].casefold())))


ERROR_TABLE: dict[str, tuple[int, str]] = {
    "BUSY": (-32001, "Hay un comando activo en AutoCAD: LINE. Reintente cuando termine."),
    "BUSY_COLA": (-32001, "AutoCAD está ocupado (posible diálogo abierto). Reintente cuando el usuario termine."),
    "NO_DOC": (-32002, "No hay ningún documento abierto en AutoCAD."),
    "NOT_FOUND": (-32003, "La capa 'NOEXISTE' no existe en el dibujo."),
    "INVALID_PARAMS": (-32602, "Parámetros inválidos: lineas[0]: longitud cero"),
    # Fase 2: variantes programables con program_error (data.code = el de ERROR_ALIASES).
    "NOT_FOUND_HANDLE": (-32003, "No existe ninguna entidad con handle 'FFFF'."),
    "INVALID_PARAMS_CONFIRMAR": (-32602, "Parámetros inválidos: confirmar: debe ser true"),
    "UNITS_UNKNOWN": (-32004, "El dibujo no declara unidades (INSUNITS=0)."),
}
# Código simbólico (data.code) de las variantes de ERROR_TABLE.
ERROR_ALIASES: dict[str, str] = {
    "BUSY_COLA": "BUSY",
    "NOT_FOUND_HANDLE": "NOT_FOUND",
    "INVALID_PARAMS_CONFIRMAR": "INVALID_PARAMS",
}

ORIGEN_TEXTO = "contenido_del_plano"


def _tool(name: str, description: str, schema: dict[str, Any], *, escritura: bool = False, requiere_documento: bool = True) -> dict[str, Any]:
    return {
        "name": name,
        "description": description,
        "escritura": escritura,
        "requiere_documento": requiere_documento,
        "inputSchema": schema,
    }


def manifest_tools() -> list[dict[str, Any]]:
    """Las 56 herramientas en el orden exacto de protocol.md §4 (§5, §5B, §5C, §5D, §5E, §5F, §5G y §5H)."""
    return _manifest_hasta_fase3() + _manifest_fase4a() + _manifest_faseL() + _manifest_fase4b() + _manifest_fase5()


COMPROBACIONES_FASE5 = [
    "auditar_capas",
    "entidades_en_capa_0",
    "auditar_bloques",
    "verificar_cuadro_areas",
    "duplicados",
    "textos_fuera_de_escala",
    "xrefs_no_resueltas",
    "unidades",
    "resumen",
]
DESCRIPCION_AUDITAR_ARCHIVO = (
    "Audita un archivo .dwg CERRADO sin abrirlo en pantalla ni modificarlo (nunca se guarda; trabaja sobre una copia "
    "temporal local y la borra): las mismas reglas y severidades que auditar_dibujo (capas, capa 0, bloques, cuadro de "
    "áreas, duplicados, textos fuera de escala, xrefs, unidades) más el resumen de resumen_archivo. Devuelve 'origen', "
    "las comprobaciones no disponibles y los hallazgos paginados. INSUNITS se informa como declarado, no como unidad "
    "real. Funciona sin documentos abiertos."
)
AUDITAR_ARCHIVO_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "ruta": {"type": "string", "minLength": 1, "maxLength": 1000},
        "comprobaciones": {
            "type": "array",
            "minItems": 1,
            "maxItems": 9,
            "uniqueItems": True,
            "items": {"type": "string", "enum": COMPROBACIONES_FASE5},
        },
        "espacio": {"type": "string", "minLength": 1, "default": "modelo"},
        "copiar": {"type": "boolean", "default": True},
        "presupuesto_ms": {"type": "integer", "minimum": 1000, "maximum": 30000, "default": 8000},
        "limit": LIMIT_SCHEMA,
        "cursor": CURSOR_SCHEMA,
    },
    "required": ["ruta"],
    "additionalProperties": False,
}
ADVERTENCIA_SIN_CARPETAS_AUDITORIA = (
    "lps.json no define carpetas_auditoria_permitidas: se admite cualquier ruta absoluta .dwg legible; añada la clave "
    "para limitar las carpetas."
)
DETALLE_RUTA_FUERA_AUDITORIA = "ruta: fuera de las carpetas permitidas de lps.json (carpetas_auditoria_permitidas)"
MOTIVO_XREFS_NO_DISPONIBLE = (
    "se leyó una copia temporal: las rutas relativas de las xrefs se resolverían contra la carpeta temporal; pase "
    "copiar: false para comprobarlas"
)


def _manifest_fase5() -> list[dict[str, Any]]:
    """auditar_archivo (§5H.1): lectura, sin documento; descripción literal y schema sin 'origen' (lo añade el servidor)."""
    return [_tool("auditar_archivo", DESCRIPCION_AUDITAR_ARCHIVO, AUDITAR_ARCHIVO_SCHEMA, requiere_documento=False)]


def _manifest_fase4b() -> list[dict[str, Any]]:
    """Las 4 de Fase 4B (§5G): descripciones literales de §5G.1–§5G.4; todas de escritura."""
    return [_tool(nombre, DESCRIPCIONES_FASE4B[nombre], SCHEMAS_FASE4B[nombre], escritura=True) for nombre in HERRAMIENTAS_FASE4B]


def _manifest_faseL() -> list[dict[str, Any]]:
    """Las 4 de Fase L (§5F): descripciones literales de §5F.1–§5F.4; todas de escritura."""
    return [_tool(nombre, DESCRIPCIONES_FASEL[nombre], SCHEMAS_FASEL[nombre], escritura=True) for nombre in HERRAMIENTAS_FASEL]


def _manifest_fase4a() -> list[dict[str, Any]]:
    """Las 11 de Fase 4A (§5E): descripciones literales de §5E.6; 3 lecturas y 8 escrituras."""
    return [
        _tool(nombre, DESCRIPCIONES_FASE4A[nombre], SCHEMAS_FASE4A[nombre], escritura=nombre not in HERRAMIENTAS_LECTURA_4A)
        for nombre in HERRAMIENTAS_FASE4A
    ]


def _manifest_hasta_fase3() -> list[dict[str, Any]]:
    """Las 36 herramientas de 0.4 en el orden exacto de protocol.md §4 (§5, §5B, §5C y §5D)."""
    return [
        _tool("ping", "Comprueba que el plugin responde. No toca AutoCAD.", EMPTY_SCHEMA, requiere_documento=False),
        _tool(
            "estado",
            "Estado de AutoCAD: documento activo, unidades, UCS y comando en curso.",
            EMPTY_SCHEMA,
            requiere_documento=False,
        ),
        _tool("listar_capas", "Lista las capas del dibujo activo con paginación.", LISTAR_CAPAS_SCHEMA),
        _tool(
            "linea",
            "Crea una o varias líneas en el espacio activo o en 'espacio' (un solo paso de deshacer). Rechaza "
            "dibujos sin unidades salvo aceptar_sin_unidades y geometría fuera de la extensión salvo forzar.",
            LINEA_SCHEMA,
            escritura=True,
        ),
        _tool(
            "consultar_entidades",
            "Lista entidades del espacio con filtros por tipo, capa y caja, paginadas, o solo sus conteos.",
            CONSULTAR_ENTIDADES_SCHEMA,
        ),
        _tool("propiedades", "Devuelve todas las propiedades de una entidad por su handle.", PROPIEDADES_SCHEMA),
        _tool("textos", "Lista textos, mtextos y atributos del espacio, con búsqueda por subcadena.", TEXTOS_SCHEMA),
        _tool("bloques", "Lista definiciones de bloque o las inserciones de un bloque concreto.", BLOQUES_SCHEMA),
        _tool("seleccion_actual", "Devuelve las entidades seleccionadas por el usuario en AutoCAD.", SELECCION_ACTUAL_SCHEMA),
        _tool("xrefs", "Lista las referencias externas del dibujo y su estado.", XREFS_SCHEMA),
        _tool("vista_png", VISTA_PNG_DESCRIPCION, VISTA_PNG_SCHEMA),
        _tool(
            "resumen_archivo",
            "Resume un DWG cerrado (unidades, capas, entidades, bloques, layouts) sin abrirlo ni guardarlo.",
            RESUMEN_ARCHIVO_SCHEMA,
            requiere_documento=False,
        ),
        # -- Fase 2: auditoría (§5C) --
        _tool(
            "auditar_capas",
            "Compara las capas del dibujo con el estándar LPS y las clasifica (estándar, ajena, genérica…) "
            "con sugerencia de capa preferida. Solo propone: no modifica nada.",
            AUDITAR_CAPAS_SCHEMA,
        ),
        _tool(
            "entidades_en_capa_0",
            "Cuenta y lista las entidades dibujadas en la capa 0 (por defecto en todos los espacios), "
            "sin entrar en definiciones de bloque.",
            ENTIDADES_EN_CAPA_0_SCHEMA,
        ),
        _tool(
            "auditar_bloques",
            "Audita las definiciones de bloque: unidades distintas a las del dibujo, nombres sospechosos "
            "y bloques sin uso.",
            AUDITAR_BLOQUES_SCHEMA,
        ),
        _tool(
            "areas_por_capa",
            "Suma por capa el área de las entidades cerradas (polilíneas, círculos, sombreados, regiones) "
            "del espacio; con detalle lista cada entidad.",
            AREAS_POR_CAPA_SCHEMA,
        ),
        _tool(
            "verificar_cuadro_areas",
            "Detecta cuadros de áreas en los textos del espacio y comprueba su coherencia aritmética; "
            "opcionalmente compara un valor declarado con el área de un contorno.",
            VERIFICAR_CUADRO_AREAS_SCHEMA,
        ),
        _tool(
            "duplicados",
            "Agrupa entidades geométricamente idénticas (con tolerancia) del espacio, por hash de geometría.",
            DUPLICADOS_SCHEMA,
        ),
        _tool(
            "textos_fuera_de_escala",
            "Detecta textos cuya altura se aleja mucho de la mediana del espacio (menor, mayor o no positiva).",
            TEXTOS_FUERA_DE_ESCALA_SCHEMA,
        ),
        _tool(
            "auditar_dibujo",
            "Ejecuta las reglas de auditoría y devuelve un resumen y la lista priorizada de hallazgos "
            "(alta → media → baja), paginada.",
            AUDITAR_DIBUJO_SCHEMA,
        ),
        _tool(
            "marcar_hallazgos",
            "Dibuja una nube de revisión y un texto por cada marca en una capa MCP-* (por defecto "
            "MCP-OBSERVACIONES). Un solo paso de deshacer. Rechaza marcas fuera de la extensión del espacio "
            "destino salvo forzar: true.",
            MARCAR_HALLAZGOS_SCHEMA,
            escritura=True,
        ),
        _tool(
            "limpiar_observaciones",
            "Borra todas las entidades de una capa MCP-* en el modelo y los layouts (la capa se conserva). "
            "Exige confirmar: true. Un solo paso de deshacer.",
            LIMPIAR_OBSERVACIONES_SCHEMA,
            escritura=True,
        ),
        # -- Fase 3: dibujo primitivo (§5D), todas de escritura --
        _tool(
            "crear_capa",
            "Crea una capa del estándar LPS (concepto de lps.json, capa de capas_permitidas/capas_mcp o MCP-*). "
            "Idempotente: si ya existe devuelve sus propiedades sin escribir nada.",
            CREAR_CAPA_SCHEMA,
            escritura=True,
        ),
        _tool(
            "polilinea",
            "Crea una LWPOLYLINE (abierta o cerrada, con bulges opcionales) en el espacio indicado. Rechaza vértices "
            "duplicados y polilíneas cerradas autointersecadas. Un solo paso de deshacer.",
            POLILINEA_SCHEMA,
            escritura=True,
        ),
        _tool("circulo", "Crea un círculo (centro y radio). Un solo paso de deshacer.", CIRCULO_SCHEMA, escritura=True),
        _tool(
            "arco",
            "Crea un arco antihorario de angulo_inicio a angulo_fin (grados). Un solo paso de deshacer.",
            ARCO_SCHEMA,
            escritura=True,
        ),
        _tool(
            "texto",
            "Crea un TEXT de una línea; el texto se escribe literal (los códigos %% no se interpretan). Un solo paso "
            "de deshacer.",
            TEXTO_SCHEMA,
            escritura=True,
        ),
        _tool(
            "mtext",
            "Crea un MTEXT; el texto se escribe literal (\\, { y } se escapan; los saltos de línea se conservan). Un "
            "solo paso de deshacer.",
            MTEXT_SCHEMA,
            escritura=True,
        ),
        _tool(
            "insertar_bloque",
            "Inserta un bloque del dibujo o, con desde_biblioteca: true, de la biblioteca del plugin (hoy LPS_ROTULO, "
            "Puerta Dinamica LM, PuertaBaño, VENTANA01 y TANQUE 1000 LTS), con atributos, propiedades dinámicas y "
            "conversión de unidades. Definición, inserción y atributos en un solo paso de deshacer.",
            INSERTAR_BLOQUE_SCHEMA,
            escritura=True,
        ),
        _tool(
            "cota",
            "Crea una cota lineal o alineada con el estilo de cota actual (o el indicado) y devuelve su medida. Capa "
            "por defecto: Cotas.",
            COTA_SCHEMA,
            escritura=True,
        ),
        _tool(
            "hatch",
            "Crea un sombreado sobre entidades cerradas existentes (asociativo) o sobre un polígono de vértices. Capa "
            "por defecto: Achurados.",
            HATCH_SCHEMA,
            escritura=True,
        ),
        _tool(
            "mover",
            "Desplaza entidades por handle. Exige confirmar: true si alguna no está en una capa MCP-*. Un solo paso "
            "de deshacer.",
            MOVER_SCHEMA,
            escritura=True,
        ),
        _tool(
            "copiar",
            "Copia entidades por handle con un desplazamiento y devuelve los handles nuevos. Exige confirmar: true si "
            "alguna no está en una capa MCP-*.",
            COPIAR_SCHEMA,
            escritura=True,
        ),
        _tool(
            "rotar",
            "Rota entidades por handle alrededor de 'base' (grados, antihorario). Exige confirmar: true si alguna no "
            "está en una capa MCP-*.",
            ROTAR_SCHEMA,
            escritura=True,
        ),
        _tool(
            "escalar",
            "Escala entidades por handle desde 'base' con un factor > 0. Exige confirmar: true si alguna no está en "
            "una capa MCP-*.",
            ESCALAR_SCHEMA,
            escritura=True,
        ),
        _tool(
            "borrar",
            "Borra entidades por handle (todo o nada). Exige confirmar: true si alguna no está en una capa MCP-*. Un "
            "solo paso de deshacer.",
            BORRAR_SCHEMA,
            escritura=True,
        ),
    ]


# --------------------------------------------------------------------------
# Dibujo ficticio
# --------------------------------------------------------------------------


def _line(handle: str, capa: str, a: list[float], b: list[float]) -> dict[str, Any]:
    lo = [min(a[i], b[i]) for i in range(3)]
    hi = [max(a[i], b[i]) for i in range(3)]
    return {
        "handle": handle,
        "tipo": "LINE",
        "capa": capa,
        "bbox": [lo, hi],
        "inicio": a,
        "fin": b,
        "longitud": math.dist(a, b),
    }


def _insert(handle: str, capa: str, nombre: str, posicion: list[float], rotacion: float, bbox: Any, atributos: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "handle": handle,
        "tipo": "INSERT",
        "capa": capa,
        "bbox": bbox,
        "nombre": nombre,
        "posicion": posicion,
        "escala": [1.0, 1.0, 1.0],
        "rotacion": rotacion,
        "n_atributos": len(atributos),
        "dinamico": False,
        # Privado del simulador (no sale en el formato compacto).
        "_atributos": atributos,
    }


def _attrib(handle: str, tag: str, valor: str, posicion: list[float]) -> dict[str, Any]:
    return {
        "handle": handle,
        "tipo": "ATTRIB",
        "capa": "0",
        "texto": valor,
        "altura": 0.15,
        "posicion": posicion,
        "rotacion": 0.0,
        "bbox": None,
        "tag": tag,
        "invisible": False,
    }


def build_drawing() -> list[dict[str, Any]]:
    """Entidades del espacio modelo en orden de base de datos."""
    return [
        _line("1A0", "MUROS", [0.0, 0.0, 0.0], [5.0, 0.0, 0.0]),
        _line("1A1", "MUROS", [5.0, 0.0, 0.0], [5.0, 3.0, 0.0]),
        _line("1A2", "MUROS", [5.0, 3.0, 0.0], [0.0, 3.0, 0.0]),
        {
            "handle": "1A3",
            "tipo": "LWPOLYLINE",
            "capa": "0",
            "bbox": [[0.0, 0.0, 0.0], [5.0, 3.0, 0.0]],
            "n_vertices": 4,
            "cerrada": True,
            "longitud": 16.0,
            "area": 15.0,
            "tiene_arcos": False,
            "vertices": [[0.0, 0.0, 0.0], [5.0, 0.0, 0.0], [5.0, 3.0, 0.0], [0.0, 3.0, 0.0]],
        },
        _insert(
            "1A4",
            "PUERTAS",
            "DP",
            [1.0, 0.0, 0.0],
            90.0,
            [[0.1, 0.0, 0.0], [1.0, 0.9, 0.0]],
            [_attrib("1A5", "ANCHO", "0.90", [0.5, 0.4, 0.0]), _attrib("1A6", "TIPO", "P1", [0.5, 0.2, 0.0])],
        ),
        _insert(
            "1A7",
            "PUERTAS",
            "DP",
            [4.0, 3.0, 0.0],
            0.0,
            [[4.0, 3.0, 0.0], [4.8, 3.8, 0.0]],
            [_attrib("1A8", "ANCHO", "0.80", [4.4, 3.4, 0.0]), _attrib("1A9", "TIPO", "P2", [4.4, 3.2, 0.0])],
        ),
        {
            "handle": "1AA",
            "tipo": "TEXT",
            "capa": "TEXTOS",
            "bbox": [[1.0, 1.5, 0.0], [2.1, 1.7, 0.0]],
            "texto": "COCINA",
            "altura": 0.2,
            "posicion": [1.0, 1.5, 0.0],
            "rotacion": 0.0,
        },
        {
            "handle": "1AB",
            "tipo": "MTEXT",
            "capa": "TEXTOS",
            "bbox": [[3.0, 1.5, 0.0], [4.6, 1.9, 0.0]],
            "texto": "BAÑO PRINCIPAL",
            "altura": 0.2,
            "posicion": [3.0, 1.9, 0.0],
            "ancho": 1.6,
            "rotacion": 0.0,
        },
        _insert("1AC", "0", "TOPO", [0.0, 0.0, 0.0], 0.0, [[-50.0, -50.0, 0.0], [50.0, 50.0, 0.0]], []),
        # -- Fase 2 --
        # Duplicados exactos de 1A0 (mismo trazo; 1B1 recorrido al revés).
        _line("1B0", "MUROS", [0.0, 0.0, 0.0], [5.0, 0.0, 0.0]),
        _line("1B1", "MUROS", [5.0, 0.0, 0.0], [0.0, 0.0, 0.0]),
        # Cuadro de áreas mínimo de una planta: cada TEXT combina etiqueta y valor ("LOTE: 72.00").
        # Coherente por dentro (52.50 = 52.50; 52.5/72 = 0.729 → 0.73); faltan LIBRE y CONSTRUCCION.
        _area_text("1C0", "LOTE", 72.0, 10.0),
        _area_text("1C1", "AREA PISO 1", 52.5, 9.0),
        _area_text("1C2", "TOTAL CONSTRUIDO", 52.5, 8.0),
        _area_text("1C3", "OCUPACION", 0.73, 7.0),
        # Texto fuera de escala (la mediana del espacio es 0.18).
        {
            "handle": "1D0",
            "tipo": "TEXT",
            "capa": "TEXTOS",
            "bbox": [[0.0, 5.0, 0.0], [30.0, 7.5, 0.0]],
            "texto": "PLANTA PRIMER PISO",
            "altura": 2.5,
            "posicion": [0.0, 5.0, 0.0],
            "rotacion": 0.0,
        },
    ]


def _area_text(handle: str, etiqueta: str, valor: float, y: float, x: float = 20.0, grupo: str = "A") -> dict[str, Any]:
    """TEXT de fila de cuadro de áreas (etiqueta + valor en el mismo texto, con decimales: F1)."""
    texto = f"{etiqueta}: {valor:.2f}"
    return {
        "handle": handle,
        "tipo": "TEXT",
        "capa": "Rotulo",
        # Extensión geométrica del texto (F6: el bbox de cuadro usa GeometricExtents, no la inserción).
        "bbox": [[x, y, 0.0], [x + 4.0, y + 0.18, 0.0]],
        "texto": texto,
        "altura": 0.18,
        "posicion": [x, y, 0.0],
        "rotacion": 0.0,
        # Privado del simulador: fila reconocida del cuadro y cuadro al que pertenece.
        "_cuadro": {"etiqueta": etiqueta, "valor": valor, "grupo": grupo},
    }


# Escenario F3 (bandera cuadros_fuera_de_layout): un segundo cuadro en layout (B, LOTE 73.11)
# y restos de una versión anterior (C, x≈-2190, fuera de toda ventana de viewport).
CUADRO_B = [("1E0", "LOTE", 73.11, 10.0), ("1E1", "AREA PISO 1", 52.5, 9.0), ("1E2", "TOTAL CONSTRUIDO", 52.5, 8.0), ("1E3", "OCUPACION", 0.72, 7.0)]
CUADRO_C = [("1E8", "LOTE", 72.10, 3830.0), ("1E9", "TOTAL CONSTRUIDO", 161.87, 3829.0)]
# Ventana de modelo del único viewport evaluable del escenario F3 (cubre A y B, no C).
VENTANAS_LAYOUT_F3 = [[[0.0, 0.0], [40.0, 40.0]]]


BLOCK_DEFINITIONS: list[dict[str, Any]] = [
    {
        "nombre": "*U1",
        "n_inserciones": 0,
        "tags": [],
        "tiene_atributos": False,
        "n_entidades": 1,
        "es_xref": False,
        "anonimo": True,
        "dinamico": False,
        "unidades_bloque": 6,
        "comentario": "",
    },
    {
        "nombre": "BASURA_jgytfyuh",
        "n_inserciones": 0,
        "tags": [],
        "tiene_atributos": False,
        "n_entidades": 3,
        "es_xref": False,
        "anonimo": False,
        "dinamico": False,
        "unidades_bloque": 6,
        "comentario": "",
    },
    {
        "nombre": "DP",
        "n_inserciones": 2,
        "tags": ["ANCHO", "TIPO"],
        "tiene_atributos": True,
        "n_entidades": 6,
        "es_xref": False,
        "anonimo": False,
        "dinamico": False,
        # Milímetros en un dibujo en metros: hallazgo unidades_distintas (§5C.3 a).
        "unidades_bloque": 4,
        "comentario": "Puerta",
    },
    {
        "nombre": "TOPO",
        "n_inserciones": 1,
        "tags": [],
        "tiene_atributos": False,
        "n_entidades": 0,
        "es_xref": True,
        "anonimo": False,
        "dinamico": False,
        "unidades_bloque": 6,
        "comentario": "",
    },
]

# Escenario F4 (bandera bloques_compensacion): VENTANA_MM en mm con sus inserciones a escala 0.001
# (compensadas → 'baja'); LAV_MM en mm con una inserción a 0.001 y otra a 1 de 0,45 × 0,51 m (tamaño real
# plausible → 'media', metadato de unidades incorrecto); MESA_MM en mm a escala 1 con la geometría de 450 × 510
# unidades (= m) (tamaño real fuera de 0,02–200 m → 'alta', escala realmente incorrecta).
BLOQUES_COMPENSACION: list[dict[str, Any]] = [
    {
        "nombre": "VENTANA_MM",
        "n_inserciones": 2,
        "tags": [],
        "tiene_atributos": False,
        "n_entidades": 4,
        "es_xref": False,
        "anonimo": False,
        "dinamico": False,
        "unidades_bloque": 4,
        "comentario": "",
    },
    {
        "nombre": "LAV_MM",
        "n_inserciones": 2,
        "tags": [],
        "tiene_atributos": False,
        "n_entidades": 5,
        "es_xref": False,
        "anonimo": False,
        "dinamico": False,
        "unidades_bloque": 4,
        "comentario": "",
    },
    {
        "nombre": "MESA_MM",
        "n_inserciones": 1,
        "tags": [],
        "tiene_atributos": False,
        "n_entidades": 4,
        "es_xref": False,
        "anonimo": False,
        "dinamico": False,
        "unidades_bloque": 4,
        "comentario": "",
    },
    # 0.3.2 (bloque 'C' del plano real): inserciones ANIDADAS en bloques anónimos a escala 0.045.
    # C_MM: medible a través del contenedor (0,045 × 0,045 m → 'media', tamano_por_anidada true).
    # FLECHA_MM: no medible (ningún contenedor llega a un espacio) → 'media' "no medible (inserciones anidadas)".
    {
        "nombre": "C_MM",
        "n_inserciones": 2,
        "tags": [],
        "tiene_atributos": False,
        "n_entidades": 3,
        "es_xref": False,
        "anonimo": False,
        "dinamico": False,
        "unidades_bloque": 4,
        "comentario": "",
    },
    {
        "nombre": "FLECHA_MM",
        "n_inserciones": 1,
        "tags": [],
        "tiene_atributos": False,
        "n_entidades": 2,
        "es_xref": False,
        "anonimo": False,
        "dinamico": False,
        "unidades_bloque": 4,
        "comentario": "",
    },
]

# §5C.3 (decisión F4): rango plausible (m) de la dimensión mayor del tamaño real de una inserción no compensada.
TAMANO_PLAUSIBLE_M = (0.02, 200.0)

XREFS: list[dict[str, Any]] = [
    {
        "nombre": "TOPO",
        "ruta_guardada": "..\\base\\TOPO.dwg",
        "ruta_resuelta": "C:\\proy\\base\\TOPO.dwg",
        "estado": "resuelta",
        "tipo": "attach",
        "n_inserciones": 1,
        "anidado": False,
    }
]

LAYOUTS = ["Layout1"]


def _layer(nombre: str, color: int, grosor: int, clasificacion: str, sugerencia: str | None = None) -> dict[str, Any]:
    return {
        "nombre": nombre,
        "color": color,
        "tipo_linea": "Continuous",
        "grosor": grosor,
        "clasificacion": clasificacion,
        "diferencias": [],
        "sugerencia": sugerencia,
    }


# Tabla de capas del dibujo ficticio con la clasificación §5C.1 ya resuelta
# (colores/grosores de las estándar = los de standards/lps.json).
FAKE_LAYERS: list[dict[str, Any]] = [
    _layer("0", 7, -3, "estandar"),
    # 'sugerencia' es solo por palabras clave en español (§5C.1): A-WALL-FULL no tiene ninguna → null, como
    # LayerClassifier (la deducción por el código AIA WALL va en sugerencia_patron, que el simulador calcula aparte).
    _layer("A-WALL-FULL", 113, 18, "ajena_revit_aia"),
    _layer("Layer9", 7, -3, "generica"),
    _layer("MCP-DIBUJO", 3, -3, "mcp"),
    _layer("MUROS", 155, 50, "estandar"),
    _layer("PUERTAS", 9, -3, "estandar"),
    _layer("Rotulo", 150, 80, "estandar"),
    _layer("TEXTOS", 152, 15, "estandar"),
]

# Tabla §8: unidades cortas, nombre en español y metros por unidad (None sin equivalencia).
UNIDADES: dict[int, tuple[str, str, float | None]] = {
    0: ("desconocida", "desconocida", None),
    1: ("in", "pulgadas", 0.0254),
    2: ("ft", "pies", 0.3048),
    4: ("mm", "milímetros", 0.001),
    5: ("cm", "centímetros", 0.01),
    6: ("m", "metros", 1.0),
    7: ("km", "kilómetros", 1000.0),
}
INSUNITS_DIBUJO = 6
INSUNITS_LPS = 6
SEVERIDAD_ORDEN = {s: i for i, s in enumerate(SEVERIDADES)}


def _bbox2d(bbox: Any) -> list[list[float]] | None:
    if bbox is None:
        return None
    return [[float(bbox[0][0]), float(bbox[0][1])], [float(bbox[1][0]), float(bbox[1][1])]]


def _fmt3(v: float) -> str:
    """Número con hasta 3 decimales, como MarkPlacement.Format del plugin ("0.###")."""
    txt = f"{round(float(v), 3):.3f}".rstrip("0").rstrip(".")
    return "0" if txt in ("-0", "") else txt


def _union2d(bboxes: list[Any]) -> list[list[float]] | None:
    """Unión de cajas en salida 2D (§2.5); None si ninguna es calculable."""
    valid = [_bbox2d(b) for b in bboxes if b is not None]
    if not valid:
        return None
    return [
        [min(b[0][0] for b in valid), min(b[0][1] for b in valid)],
        [max(b[1][0] for b in valid), max(b[1][1] for b in valid)],
    ]


def _hallazgo(
    regla: str,
    severidad: str,
    mensaje: str,
    handles: list[str],
    bbox: Any,
    espacio: str | None,
    datos: dict[str, Any],
) -> dict[str, Any]:
    """Hallazgo con el formato común §5C.0 (el id se asigna con _numerar)."""
    return {
        "id": "",
        "regla": regla,
        "severidad": severidad,
        "mensaje": mensaje,
        "handles": handles[:50],
        "bbox": bbox,
        "espacio": espacio,
        "datos": datos,
    }


def _numerar(hallazgos: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Asigna id "<regla>-<n>" correlativo por regla dentro de la llamada."""
    contador: dict[str, int] = {}
    for h in hallazgos:
        contador[h["regla"]] = contador.get(h["regla"], 0) + 1
        h["id"] = f"{h['regla']}-{contador[h['regla']]}"
    return hallazgos


def _ordenar(hallazgos: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Orden §5C.0: severidad, regla (tabla §5C.8) e id."""

    def key(h: dict[str, Any]) -> tuple[int, int, int]:
        regla = REGLAS_AUDITORIA.index(h["regla"]) if h["regla"] in REGLAS_AUDITORIA else len(REGLAS_AUDITORIA)
        return (SEVERIDAD_ORDEN[h["severidad"]], regla, int(h["id"].rsplit("-", 1)[1]))

    return sorted(hallazgos, key=key)


def _sospecha_nombre(nombre: str) -> str | None:
    """Heurística simplificada de §5C.3 (b) para el simulador."""
    if nombre.startswith("*") or nombre.upper().startswith("A$C"):
        return None
    up = nombre.upper()
    for racha in re.findall(r"[A-Z]+", up):
        if re.search(r"[B-DF-HJ-NP-TV-Z]{5,}", racha):
            return "sin_vocales"
    if re.search(r"([A-Z])\1{3,}", up):
        return "letra_repetida"
    filas = ("QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM")
    for racha in re.findall(r"[A-Z]+", up):
        for i in range(len(racha) - 3):
            if any(racha[i : i + 4] in fila for fila in filas):
                return "secuencia_teclado"
    if re.fullmatch(r"[A-Z]{3}", up) and any(up in fila for fila in filas):
        return "secuencia_teclado"
    if re.search(r"([A-Z])(?!\1)([A-Z])(\1\2)+", up):
        return "par_repetido"
    return None


def _revision_cloud(bbox: list[list[float]], altura: float) -> tuple[list[list[float]], list[list[float]]]:
    """Vértices (antihorario) y caja de la nube de revisión de §5C.9."""
    (x0, y0), (x1, y1) = bbox[0][:2], bbox[1][:2]
    m = max(altura, 0.05 * max(x1 - x0, y1 - y0))
    rx0, ry0, rx1, ry1 = x0 - m, y0 - m, x1 + m, y1 + m
    w, h = rx1 - rx0, ry1 - ry0
    c = max(altura * 1.5, 0.08 * max(w, h))
    nx, ny = max(4, math.ceil(w / c)), max(4, math.ceil(h / c))
    vertices: list[list[float]] = []
    vertices += [[rx0 + i * w / nx, ry0] for i in range(nx)]
    vertices += [[rx1, ry0 + j * h / ny] for j in range(ny)]
    vertices += [[rx1 - i * w / nx, ry1] for i in range(nx)]
    vertices += [[rx0, ry1 - j * h / ny] for j in range(ny)]
    return vertices, [[rx0, ry0], [rx1, ry1]]

# --------------------------------------------------------------------------
# Fase 3: lógica pura del simulador (equivalente a GeometryCore.Draw)
# --------------------------------------------------------------------------


def _p3(p: list[float]) -> list[float]:
    return [float(p[0]), float(p[1]), float(p[2]) if len(p) > 2 else 0.0]


def _norm360(a: float) -> float:
    """Angles.Normalize360: −90 → 270, 360 → 0, 450 → 90."""
    r = math.fmod(float(a), 360.0)
    if r < 0:
        r += 360.0
    return 0.0 if abs(r - 360.0) < 1e-12 else r


def _box_of(points: list[list[float]]) -> list[list[float]]:
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    return [[min(xs), min(ys)], [max(xs), max(ys)]]


def _box3(box2: list[list[float]]) -> list[list[float]]:
    return [[box2[0][0], box2[0][1], 0.0], [box2[1][0], box2[1][1], 0.0]]


def _fmt_box(box: list[list[float]]) -> str:
    return f"[[{_fmt3(box[0][0])}, {_fmt3(box[0][1])}], [{_fmt3(box[1][0])}, {_fmt3(box[1][1])}]]"


def _escape_mtext(texto: str) -> str:
    """MTextEscape.Escape: \\ → \\\\, { → \\{, } → \\}; saltos de línea → \\P."""
    t = texto.replace("\\", "\\\\").replace("{", "\\{").replace("}", "\\}")
    return t.replace("\r\n", "\\P").replace("\r", "\\P").replace("\n", "\\P")


def _escape_dbtext(texto: str) -> str:
    """TextEscape.EscapeDbText: cada %% → %%%%%% (%%d se ve como %%d, no como °); 50% no cambia."""
    return texto.replace("%%", "%%%%%%")


def _orient(a: tuple[float, float], b: tuple[float, float], c: tuple[float, float], tol: float) -> int:
    v = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
    return 0 if abs(v) <= tol else (1 if v > 0 else -1)


def _on_segment(a: tuple[float, float], b: tuple[float, float], p: tuple[float, float], tol: float) -> bool:
    return min(a[0], b[0]) - tol <= p[0] <= max(a[0], b[0]) + tol and min(a[1], b[1]) - tol <= p[1] <= max(a[1], b[1]) + tol


def _segments_touch(p1: Any, p2: Any, q1: Any, q2: Any, tol: float) -> bool:
    o1, o2 = _orient(p1, p2, q1, tol), _orient(p1, p2, q2, tol)
    o3, o4 = _orient(q1, q2, p1, tol), _orient(q1, q2, p2, tol)
    if o1 != o2 and o3 != o4:
        return True
    return (
        (o1 == 0 and _on_segment(p1, p2, q1, tol))
        or (o2 == 0 and _on_segment(p1, p2, q2, tol))
        or (o3 == 0 and _on_segment(q1, q2, p1, tol))
        or (o4 == 0 and _on_segment(q1, q2, p2, tol))
    )


def _validar_polilinea(
    xy: list[tuple[float, float]], cerrada: bool, bulges: Any, etiqueta: str = "vertices", tol: float = 1e-9
) -> str | None:
    """PolylineValidator.Validate (§5D.2): primer error con el texto exacto del contrato, o None."""
    n = len(xy)
    for i in range(1, n):
        if math.dist(xy[i], xy[i - 1]) <= tol:
            return f"{etiqueta}[{i}]: vértice duplicado con el anterior"
    if cerrada and n < 3:
        return f"{etiqueta}: una polilínea cerrada necesita al menos 3 vértices"
    if cerrada and math.dist(xy[-1], xy[0]) <= tol:
        return f"{etiqueta}[{n - 1}]: coincide con el primero (la polilínea cerrada no debe repetir el vértice inicial)"
    if bulges is not None:
        if not isinstance(bulges, list) or len(bulges) != n:
            return "bulges: debe tener tantos elementos como vertices"
        for i, b in enumerate(bulges):
            if isinstance(b, bool) or not isinstance(b, (int, float)) or not math.isfinite(b):
                return f"bulges[{i}]: debe ser un número finito"
    if cerrada:
        tramos = [(xy[i], xy[(i + 1) % n]) for i in range(n)]
        for i in range(n):
            for j in range(i + 2, n):
                if i == 0 and j == n - 1:
                    continue  # adyacentes por el cierre
                if _segments_touch(tramos[i][0], tramos[i][1], tramos[j][0], tramos[j][1], tol):
                    return f"{etiqueta}: la polilínea cerrada se autointerseca (tramos {i} y {j})"
    return None


def _longitud_area(xy: list[tuple[float, float]], cerrada: bool, bulges: list[float] | None) -> tuple[float, float | None]:
    """Longitud (con arcos por bulge) y área (shoelace + segmentos circulares) de una LWPOLYLINE."""
    n = len(xy)
    bulges = bulges or [0.0] * n
    longitud, area2 = 0.0, 0.0
    tramos = n if cerrada else n - 1
    for i in range(tramos):
        a, b = xy[i], xy[(i + 1) % n]
        c = math.dist(a, b)
        bulge = float(bulges[i])
        if abs(bulge) < 1e-12:
            longitud += c
            seg = 0.0
        else:
            theta = 4.0 * math.atan(bulge)
            r = c / (2.0 * math.sin(abs(theta) / 2.0))
            longitud += abs(theta) * r
            seg = math.copysign(0.5 * r * r * (abs(theta) - math.sin(abs(theta))), theta)
        area2 += a[0] * b[1] - b[0] * a[1] + 2.0 * seg
    return longitud, (abs(area2) / 2.0 if cerrada else None)


def _transform_box(box: Any, fn: Any) -> list[list[float]] | None:
    """Transform2D.TransformBox: caja de las 4 esquinas transformadas."""
    if box is None:
        return None
    (x0, y0), (x1, y1) = box[0][:2], box[1][:2]
    pts = [fn([x, y, 0.0]) for x, y in ((x0, y0), (x1, y0), (x1, y1), (x0, y1))]
    return _box_of(pts)


def _transformar_entidad(e: dict[str, Any], fn: Any, giro: float = 0.0, factor: float = 1.0) -> None:
    """Aplica la transformación (TransformBy) a los campos geométricos de una entidad del simulador."""
    for key in ("inicio", "fin", "posicion", "centro", "p1", "p2", "posicion_linea"):
        if isinstance(e.get(key), list):
            e[key] = fn(e[key])
    if isinstance(e.get("vertices"), list):
        e["vertices"] = [fn(v) for v in e["vertices"]]
    if e.get("bbox") is not None:
        z0, z1 = (e["bbox"][0][2], e["bbox"][1][2]) if len(e["bbox"][0]) > 2 else (0.0, 0.0)
        b = _transform_box(e["bbox"], fn)
        e["bbox"] = [[b[0][0], b[0][1], z0], [b[1][0], b[1][1], z1]]
    if giro and isinstance(e.get("rotacion"), (int, float)):
        e["rotacion"] = _norm360(e["rotacion"] + giro)
    if factor != 1.0:
        for key in ("radio", "altura", "longitud", "ancho", "medida"):
            if isinstance(e.get(key), (int, float)) and not isinstance(e.get(key), bool):
                e[key] = e[key] * factor
        if isinstance(e.get("area"), (int, float)):
            e["area"] = e["area"] * factor * factor
        if isinstance(e.get("escala"), list):
            e["escala"] = [s * factor for s in e["escala"]]
    for attrib in e.get("_atributos", []):
        _transformar_entidad(attrib, fn, giro, factor)


def _plano_n_sustituir(contenido: str, numero: str) -> str | None:
    """PlanoNFormat.TrySubstitute: sustituye solo el número conservando el formato en línea, o None."""
    m = PLANO_N_RE.match(contenido)
    if m is None:
        return None
    return m.group("pre") + "{" + m.group("codes") + _escape_mtext(numero) + m.group("rest")


def _plano_n_numero(valor: str) -> str:
    """'3 de' → '3' (se admite el sufijo ' de')."""
    t = valor.strip()
    m = PLANO_N_SUFIJO_RE.match(t)
    return m.group("n").strip() if m else t


def _factor_unidades(ub: int, ud: int) -> float | None:
    """UnitsScale.Factor: metros por unidad del bloque / del dibujo; None si alguna es 0 o sin equivalencia."""
    mb = UNIDADES.get(ub, (None, None, None))[2] if ub else None
    md = UNIDADES.get(ud, (None, None, None))[2] if ud else None
    if mb is None or md is None:
        return None
    return mb / md


def _fmt_g10(v: float) -> str:
    """Formato G10 de BlockInsertScale.Format."""
    return format(float(v), ".10g")


# --------------------------------------------------------------------------
# Fase 4A: lógica pura del simulador (equivalente simplificado de GeometryCore.Gen)
# --------------------------------------------------------------------------


def _invalid_geometry(detalle: str, **extra: Any) -> _RpcError:
    """INVALID_GEOMETRY (−32005, §6): message "Geometría no construible: <detalle>", data.detalle = detalle."""
    return _RpcError(-32005, MENSAJE_GEOMETRIA_PREFIJO + detalle, {"code": "INVALID_GEOMETRY", "detalle": detalle, **extra})


def _units_unknown() -> _RpcError:
    rpc_code, message = ERROR_TABLE["UNITS_UNKNOWN"]
    return _RpcError(rpc_code, message, {"code": "UNITS_UNKNOWN"})


def _es_numero(v: Any) -> bool:
    return not isinstance(v, bool) and isinstance(v, (int, float)) and math.isfinite(v)


def _leer_punto_ref_coord(el: Any, path: str) -> list[float]:
    """Coordenada literal de un PuntoRef (PointRef.ReadPoint)."""
    if not isinstance(el, list):
        raise _invalid(detalle_punto_ref_forma(path))
    if len(el) not in (2, 3) or any(isinstance(c, bool) or not isinstance(c, (int, float)) for c in el):
        raise _invalid(f"{path}: debe ser un array de 2 o 3 números")
    if any(not math.isfinite(c) for c in el):
        raise _invalid(f"{path}: coordenada no finita")
    return _p3(el)


def _leer_handle_ref(v: Any, path: str, campo: str) -> str:
    if not isinstance(v, str) or not re.fullmatch(r"[0-9A-Fa-f]{1,16}", v):
        raise _invalid(f"{path}.{campo}: formato inválido")
    return v.upper()


def _parse_punto_ref(el: Any, path: str) -> dict[str, Any]:
    """PointRef.Parse (§5E.1): las tres formas, con los mensajes exactos del contrato."""
    if isinstance(el, list):
        return {"kind": "coord", "punto": _leer_punto_ref_coord(el, path)}
    if not isinstance(el, dict):
        raise _invalid(detalle_punto_ref_forma(path))
    handle = punto = inter = None
    indice: int | None = None
    cerca: list[float] | None = None
    tiene_indice = tiene_cerca = False
    for clave, valor in el.items():
        if clave == "handle":
            handle = _leer_handle_ref(valor, path, "handle")
        elif clave == "punto":
            if not isinstance(valor, str) or valor not in PUNTOS_REF:
                raise _invalid(f"{path}.punto: debe ser uno de inicio, fin, medio, centro, vertice")
            punto = valor
        elif clave == "indice":
            tiene_indice = True
            if not _es_numero(valor) or math.floor(valor) != valor or valor < 0:
                raise _invalid(f"{path}.indice: debe ser un entero mayor o igual que 0")
            indice = int(valor)
        elif clave == "interseccion_con":
            inter = _leer_handle_ref(valor, path, "interseccion_con")
        elif clave == "cerca_de":
            tiene_cerca = True
            cerca = _leer_punto_ref_coord(valor, f"{path}.cerca_de")
        else:
            raise _invalid(f"{path}.{clave}: propiedad no permitida")
    if handle is None:
        raise _invalid(detalle_punto_ref_forma(path))
    if punto is not None and inter is not None:
        raise _invalid(f"{path}: no puede llevar punto e interseccion_con a la vez")
    if inter is not None:
        if tiene_indice:
            raise _invalid(f"{path}.indice: solo aplica con punto 'vertice'")
        if inter == handle:
            raise _invalid(f"{path}: interseccion_con debe ser una entidad distinta de handle")
        return {"kind": "inter", "handle": handle, "handle2": inter, "cerca_de": cerca}
    if punto is None:
        raise _invalid(detalle_punto_ref_forma(path))
    if tiene_cerca:
        raise _invalid(f"{path}.cerca_de: propiedad no permitida")
    if tiene_indice and punto != "vertice":
        raise _invalid(f"{path}.indice: solo aplica con punto 'vertice'")
    return {"kind": "ent", "handle": handle, "punto": punto, "indice": indice}


def _parse_lista_refs(el: Any, path: str, minimo: int, maximo: int) -> list[dict[str, Any]]:
    """PointRef.ParseList: "<path>: debe ser un array de <a> a <b> puntos"."""
    if not isinstance(el, list) or not minimo <= len(el) <= maximo:
        raise _invalid(f"{path}: debe ser un array de {minimo} a {maximo} puntos")
    return [_parse_punto_ref(x, f"{path}[{i}]") for i, x in enumerate(el)]


def _ref_json(ref: dict[str, Any]) -> Any:
    """PointRef.ToJson: la referencia tal como la escribió el cliente (handles en mayúsculas)."""
    if ref["kind"] == "coord":
        return list(ref["punto"])
    if ref["kind"] == "ent":
        d: dict[str, Any] = {"handle": ref["handle"], "punto": ref["punto"]}
        if ref["indice"] is not None:
            d["indice"] = ref["indice"]
        return d
    i: dict[str, Any] = {"handle": ref["handle"], "interseccion_con": ref["handle2"]}
    if ref["cerca_de"] is not None:
        i["cerca_de"] = list(ref["cerca_de"])
    return i


def _firma(e: dict[str, Any]) -> str:
    """Huella de un ancla (AnchorFingerprint simplificada): tipo + caja + geometría redondeada a 1e-6."""

    def r(v: Any) -> list[float]:
        return [round(float(c), 6) for c in v]

    partes: list[Any] = [e.get("tipo")]
    partes.append([r(p) for p in e["bbox"]] if e.get("bbox") else None)
    for clave in ("inicio", "fin", "centro", "posicion"):
        partes.append(r(e[clave]) if isinstance(e.get(clave), list) else None)
    partes.append(round(float(e["radio"]), 6) if _es_numero(e.get("radio")) else None)
    partes.append([r(v) for v in e["vertices"]] if isinstance(e.get("vertices"), list) else None)
    return json.dumps(partes)


def _ancla_directa(e: dict[str, Any]) -> dict[str, Any]:
    """Huella de una entidad usada directamente por handle (eje/contorno, offset, arrays): §5E.5."""
    return {"handles": [e["handle"]], "firmas": [_firma(e)], "tipo": e["tipo"]}


def _colapsar_duplicados(
    v: list[tuple[float, float]], tol: float = 1e-9, lazo_cerrado: bool = True
) -> tuple[list[tuple[float, float]], int]:
    """PolygonMetrics.CollapseConsecutiveDuplicates: quita consecutivos duplicados (distancia euclídea ≤ tol) y, en un
    lazo cerrado, los finales que repiten el primero."""

    def igual(a: tuple[float, float], b: tuple[float, float]) -> bool:
        return math.dist(a, b) <= tol

    r: list[tuple[float, float]] = []
    for p in v:
        if r and igual(r[-1], p):
            continue
        r.append(p)
    while lazo_cerrado and len(r) > 1 and igual(r[-1], r[0]):
        r.pop()
    return r, len(v) - len(r)


def _shoelace(v: list[tuple[float, float]]) -> float:
    """Área con signo (positiva antihoraria)."""
    s = 0.0
    n = len(v)
    for i in range(n):
        x0, y0 = v[i]
        x1, y1 = v[(i + 1) % n]
        s += x0 * y1 - x1 * y0
    return s / 2.0


def _perimetro(v: list[tuple[float, float]], cerrada: bool) -> float:
    n = len(v)
    tramos = n if cerrada else n - 1
    return sum(math.dist(v[i], v[(i + 1) % n]) for i in range(tramos))


def _primer_cruce(xy: list[tuple[float, float]], cerrada: bool, tol: float = 1e-9) -> tuple[int, int] | None:
    """Primer par de tramos no adyacentes que se cruzan o se tocan (PolylineValidator sobre las cuerdas)."""
    n = len(xy)
    m = n if cerrada else n - 1
    tramos = [(xy[i], xy[(i + 1) % n]) for i in range(m)]
    for i in range(m):
        for j in range(i + 2, m):
            if cerrada and i == 0 and j == m - 1:
                continue
            if _segments_touch(tramos[i][0], tramos[i][1], tramos[j][0], tramos[j][1], tol):
                return i, j
    return None


def _interseccion_rectas(p: Any, d: Any, q: Any, e: Any) -> tuple[float, float, float, float] | None:
    """p + t·d = q + s·e → (x, y, t, s), o None si son paralelas."""
    den = d[0] * e[1] - d[1] * e[0]
    if abs(den) <= 1e-12 * max(1.0, math.hypot(*d) * math.hypot(*e)):
        return None
    wx, wy = q[0] - p[0], q[1] - p[1]
    t = (wx * e[1] - wy * e[0]) / den
    s = (wx * d[1] - wy * d[0]) / den
    return p[0] + t * d[0], p[1] + t * d[1], t, s


def _desplazar_camino(pts: list[tuple[float, float]], off: float, cerrada: bool) -> list[tuple[float, float]]:
    """Paralela a una polilínea a 'off' (positivo = izquierda del sentido) con esquinas a inglete."""
    n = len(pts)
    m = n if cerrada else n - 1
    rectas = []
    for i in range(m):
        a, b = pts[i], pts[(i + 1) % n]
        dx, dy = b[0] - a[0], b[1] - a[1]
        largo = math.hypot(dx, dy)
        nx, ny = -dy / largo, dx / largo
        rectas.append(((a[0] + nx * off, a[1] + ny * off), (dx, dy), (nx, ny)))
    out: list[tuple[float, float]] = []
    for i in range(n):
        if not cerrada and i == 0:
            out.append(rectas[0][0])
            continue
        if not cerrada and i == n - 1:
            p, d, _n = rectas[-1]
            out.append((p[0] + d[0], p[1] + d[1]))
            continue
        r1, r2 = rectas[i - 1], rectas[i % m]
        x = _interseccion_rectas(r1[0], r1[1], r2[0], r2[1])
        if x is None:
            v = pts[i]
            out.append((v[0] + r2[2][0] * off, v[1] + r2[2][1] * off))
        else:
            out.append((x[0], x[1]))
    return out


class _WallError(Exception):
    """WallGeometryException: detalle exacto del contrato; es_parametro → INVALID_PARAMS, si no INVALID_GEOMETRY."""

    def __init__(self, detalle: str, es_parametro: bool) -> None:
        super().__init__(detalle)
        self.detalle = detalle
        self.es_parametro = es_parametro


def _contorno_muro(
    eje: list[tuple[float, float]], espesor: float, justificacion: str, cerrado: bool, limite: float, tol: float = 1e-9
) -> dict[str, Any]:
    """WallOutline.Build simplificado (§5E.3.1): caras paralelas, esquinas a inglete y validación en el orden del contrato."""
    n = len(eje)
    if n < 2:
        raise _WallError("eje: se necesitan al menos 2 vértices", True)
    if cerrado and n < 3:
        raise _WallError("eje: un muro cerrado necesita al menos 3 vértices", True)
    for i in range(1, n):
        if math.dist(eje[i], eje[i - 1]) <= tol:
            raise _WallError(f"eje[{i}]: vértice duplicado con el anterior", True)
    cruce = _primer_cruce(eje, cerrado, tol)
    if cruce is not None:
        raise _WallError(f"eje: el eje se autointerseca (tramos {cruce[0]} y {cruce[1]})", False)
    m = n if cerrado else n - 1
    for i in range(m):
        largo = math.dist(eje[i], eje[(i + 1) % n])
        if largo < espesor:
            raise _WallError(
                f"espesor: el tramo {i} (longitud {_fmt3(largo)}) es más corto que el espesor {_fmt3(espesor)}", False
            )
    interiores = range(n) if cerrado else range(1, n - 1)
    for i in interiores:
        v, a, b = eje[i], eje[i - 1], eje[(i + 1) % n]
        u = (a[0] - v[0], a[1] - v[1])
        w = (b[0] - v[0], b[1] - v[1])
        cos_g = (u[0] * w[0] + u[1] * w[1]) / (math.hypot(*u) * math.hypot(*w))
        g = math.degrees(math.acos(max(-1.0, min(1.0, cos_g))))
        # Medido desde la LÍNEA MEDIA del muro (independiente de la justificación): el vértice de inglete de cada cara
        # se aleja (e/2)/sin(g/2) del de la línea media; no más de limite × e/2.
        if math.sin(math.radians(g) / 2.0) < 1.0 / limite - 1e-12:
            raise _WallError(
                f"eje: el ángulo en el vértice {i} ({g:.1f}°) es demasiado agudo para el inglete "
                f"(límite {_fmt3(limite)}× el espesor)",
                False,
            )
    o_izq, o_der = {"centro": (espesor / 2, -espesor / 2), "izquierda": (espesor, 0.0), "derecha": (0.0, -espesor)}[
        justificacion
    ]
    izq = _desplazar_camino(eje, o_izq, cerrado)
    der = _desplazar_camino(eje, o_der, cerrado)
    if cerrado:
        a1, a2 = abs(_shoelace(izq)), abs(_shoelace(der))
        exterior, interior = (izq, der) if a1 >= a2 else (der, izq)
        poligonos = [exterior, interior]
        area = abs(a1 - a2)
        contorno: list[tuple[float, float]] = exterior
        interior_out: list[tuple[float, float]] | None = interior
        longitud_contorno = _perimetro(exterior, True) + _perimetro(interior, True)
    else:
        contorno = izq + der[::-1]
        poligonos = [contorno]
        area = abs(_shoelace(contorno))
        interior_out = None
        longitud_contorno = _perimetro(contorno, True)
    for poligono in poligonos:
        cruce = _primer_cruce(poligono, True, tol)
        if cruce is not None:
            raise _WallError(f"eje: el contorno resultante se autointerseca (tramos {cruce[0]} y {cruce[1]})", False)
    return {
        "izquierda": izq,
        "derecha": der,
        "contorno": contorno,
        "interior": interior_out,
        "longitud_eje": _perimetro(eje, cerrado),
        "longitud_contorno": longitud_contorno,
        "area": area,
        "n_tramos": m,
    }


def _formatear_area(area: float, decimales: int) -> str:
    """PolygonMetrics.FormatArea: "12,35 m²" (coma decimal, sin separador de miles)."""
    return f"{area:.{decimales}f}".replace(".", ",") + " m²"


def _centroide(v: list[tuple[float, float]]) -> tuple[float, float]:
    a = _shoelace(v)
    if abs(a) <= 1e-12:
        xs, ys = [p[0] for p in v], [p[1] for p in v]
        return (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2
    cx = cy = 0.0
    n = len(v)
    for i in range(n):
        x0, y0 = v[i]
        x1, y1 = v[(i + 1) % n]
        f = x0 * y1 - x1 * y0
        cx += (x0 + x1) * f
        cy += (y0 + y1) * f
    return cx / (6 * a), cy / (6 * a)


def _dist_segmento(p: tuple[float, float], a: tuple[float, float], b: tuple[float, float]) -> float:
    dx, dy = b[0] - a[0], b[1] - a[1]
    l2 = dx * dx + dy * dy
    t = 0.0 if l2 <= 0 else max(0.0, min(1.0, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / l2))
    return math.dist(p, (a[0] + t * dx, a[1] + t * dy))


def _dist_contorno(v: list[tuple[float, float]], x: float, y: float) -> float:
    n = len(v)
    return min(_dist_segmento((x, y), v[i], v[(i + 1) % n]) for i in range(n))


def _contiene(v: list[tuple[float, float]], x: float, y: float) -> bool:
    """Ray casting; el borde cuenta como dentro (PolygonMetrics.Contains)."""
    if _dist_contorno(v, x, y) <= 1e-9:
        return True
    dentro = False
    n = len(v)
    for i in range(n):
        x0, y0 = v[i]
        x1, y1 = v[(i + 1) % n]
        if (y0 > y) != (y1 > y) and x < x0 + (y - y0) * (x1 - x0) / (y1 - y0):
            dentro = not dentro
    return dentro


def _punto_interior(v: list[tuple[float, float]]) -> tuple[tuple[float, float], bool]:
    """PolygonMetrics.InteriorPoint: centroide si cae dentro; si no, polo de inaccesibilidad por rejilla refinada."""
    c = _centroide(v)
    if _contiene(v, *c):
        return c, True
    xs, ys = [p[0] for p in v], [p[1] for p in v]
    x0, y0, x1, y1 = min(xs), min(ys), max(xs), max(ys)
    mejor, mejor_d = c, -1.0
    k = 32
    for _ in range(3):
        sx, sy = (x1 - x0) / k, (y1 - y0) / k
        for i in range(k):
            for j in range(k):
                x, y = x0 + (i + 0.5) * sx, y0 + (j + 0.5) * sy
                if _contiene(v, x, y):
                    d = _dist_contorno(v, x, y)
                    if d > mejor_d:
                        mejor, mejor_d = (x, y), d
        x0, x1 = mejor[0] - 2 * sx, mejor[0] + 2 * sx
        y0, y1 = mejor[1] - 2 * sy, mejor[1] + 2 * sy
    return mejor, False


def _rotulos_letras(n: int) -> list[str]:
    """GridLayout.DefaultLetterLabels: A..Z, AA, AB, …"""
    out = []
    for i in range(n):
        k, s = i + 1, ""
        while k > 0:
            k, r = divmod(k - 1, 26)
            s = chr(ord("A") + r) + s
        out.append(s)
    return out


def _girar(p: Any, grados: float, c: Any) -> list[float]:
    a = math.radians(grados)
    x, y = p[0] - c[0], p[1] - c[1]
    z = p[2] if len(p) > 2 else 0.0
    return [c[0] + x * math.cos(a) - y * math.sin(a), c[1] + x * math.sin(a) + y * math.cos(a), z]


def _caja_texto(posicion: list[float], texto: str, altura: float, ancho: float = 0.0) -> list[list[float]]:
    """Caja estimada de un texto centrado en 'posicion' (justificación medio_centro)."""
    lineas = texto.split("\n")
    w = ancho if ancho > 0 else 0.8 * altura * max(len(l) for l in lineas)
    h = altura * (1.5 * len(lineas) - 0.5)
    return [[posicion[0] - w / 2, posicion[1] - h / 2], [posicion[0] + w / 2, posicion[1] + h / 2]]


def _patron_desconocido(patron: str) -> bool:
    """El simulador solo conoce PATRONES_SOMBREADO (en AutoCAD, SetHatchPattern lanzaría → NOT_FOUND)."""
    return patron.upper() not in PATRONES_SOMBREADO


RESUMEN_ARCHIVO_FIXTURE: dict[str, Any] = {
    "nombre": "PLANO.dwg",
    "tamano_bytes": 2345678,
    "modificado": "2026-10-20T10:11:12-05:00",
    "version_dwg": "AC1032",
    "abierto_en_sesion": False,
    "insunits": 1,
    "unidades": "in",
    "measurement": 1,
    "n_capas": 187,
    "entidades_modelo": {"total": 29500, "por_tipo": {"LINE": 20000, "LWPOLYLINE": 9000, "INSERT": 500}},
    "n_bloques": 120,
    "bloques_mas_usados": [{"nombre": "DP", "n_inserciones": 140}],
    "layouts": ["Layout1", "PLANTA"],
    "n_xrefs": 2,
    "origen_texto": ORIGEN_TEXTO,
    "advertencias": [
        "MEASUREMENT=1 (métrico) con INSUNITS imperial: unidades inconsistentes.",
        ADVERTENCIA_LPS_PULGADAS,
    ],
}


def _public(entity: dict[str, Any]) -> dict[str, Any]:
    """Formato compacto §5B.0: sin los campos privados del simulador."""
    return {k: copy.deepcopy(v) for k, v in entity.items() if not k.startswith("_")}


class _RpcError(Exception):
    """Error JSON-RPC que un handler simulado devuelve al cliente."""

    def __init__(self, rpc_code: int, message: str, data: dict[str, Any]) -> None:
        super().__init__(message)
        self.rpc_code = rpc_code
        self.message = message
        self.data = data


def _invalid(detalle: str) -> _RpcError:
    return _RpcError(-32602, f"Parámetros inválidos: {detalle}", {"code": "INVALID_PARAMS", "detalle": detalle})


def _not_found(message: str, **extra: Any) -> _RpcError:
    return _RpcError(-32003, message, {"code": "NOT_FOUND", **extra})


def paginate(rows: list[Any], params: dict[str, Any]) -> tuple[list[Any], int, str | None]:
    """Paginación común §2.4: cursor = índice decimal en la lista filtrada."""
    limit = int(params.get("limit", 200))
    total = len(rows)
    cursor = params.get("cursor")
    start = 0
    if cursor is not None:
        if not isinstance(cursor, str) or not re.fullmatch(r"\d{1,9}", cursor):
            raise _invalid("cursor: valor inválido")
        start = int(cursor)
        if total > 0 and start >= total:
            raise _invalid("cursor: fuera de rango")
    page = rows[start : start + limit]
    end = start + len(page)
    return page, total, (str(end) if end < total else None)


def _bbox_hits(entity_bbox: Any, filtro: list[list[float]]) -> bool:
    if entity_bbox is None:
        return False
    (fx0, fy0), (fx1, fy1) = filtro[0][:2], filtro[1][:2]
    (ex0, ey0), (ex1, ey1) = entity_bbox[0][:2], entity_bbox[1][:2]
    return ex0 <= fx1 and ex1 >= fx0 and ey0 <= fy1 and ey1 >= fy0


def _check_bbox(params: dict[str, Any]) -> list[list[float]] | None:
    bbox = params.get("bbox")
    if bbox is None:
        return None
    if bbox[0][0] > bbox[1][0] or bbox[0][1] > bbox[1][1]:
        raise _invalid("bbox: mínimo mayor que máximo")
    return bbox


def _counts(values: list[str]) -> dict[str, int]:
    acc: dict[str, int] = {}
    for v in values:
        acc[v] = acc.get(v, 0) + 1
    return dict(sorted(acc.items(), key=lambda kv: (-kv[1], kv[0])))


@dataclass
class FakePluginState:
    """Estado compartido entre conexiones de una misma 'instancia' simulada."""

    pid: int = 18244
    # Errores programados por método: lista de códigos que se consumen en orden.
    errors: dict[str, list[str]] = field(default_factory=dict)
    # Métodos que no responden (simula lentitud/timeout).
    slow: set[str] = field(default_factory=set)
    # Desconexión abrupta al recibir este método (se consume una vez).
    disconnect_on: set[str] = field(default_factory=set)
    # Respuestas con id equivocado antes de la buena (correlación).
    stale_reply_before: set[str] = field(default_factory=set)
    # Número de envíos que fallarán con conexión rota (antes de llegar al plugin).
    fail_send: int = 0
    # Si False, connect() falla como pipe inexistente.
    alive: bool = True
    # protocol_version que declara el manifest (para probar el aviso de versión).
    protocol_version: str = FAKE_PROTOCOL_VERSION
    # Si True, vista_png responde sin png_base64 (respuesta defectuosa).
    vista_png_sin_imagen: bool = False
    # Handles seleccionados por el "usuario" (seleccion_actual).
    seleccion: list[str] = field(default_factory=lambda: ["1A0", "1AA"])
    # Archivos DWG que "existen" para resumen_archivo y auditar_archivo (comparación sin mayúsculas): ruta → datos simulados.
    archivos: dict[str, dict[str, Any]] = field(
        default_factory=lambda: {"C:\\copias\\PLANO.dwg": {}, "C:\\copias\\FILE1.dwg": {}}
    )
    entities: list[dict[str, Any]] = field(default_factory=build_drawing)
    # Entidades de cada layout (espacio papel), vacías al empezar.
    layout_entities: dict[str, list[dict[str, Any]]] = field(default_factory=lambda: {l: [] for l in LAYOUTS})
    # Capas creadas durante la sesión (marcar_hallazgos), además de FAKE_LAYERS.
    capas_creadas: list[dict[str, Any]] = field(default_factory=list)
    requests: list[dict[str, Any]] = field(default_factory=list)
    connections: int = 0
    next_handle: int = 0x2A3F
    lock: threading.Lock = field(default_factory=threading.Lock)
    # Espacio activo del "usuario" ("modelo" o un layout): decide si vista_png con 'espacio' es distinto (§5B.7).
    espacio_activo: str = "modelo"
    # Definiciones de bloque del dibujo (copia por estado).
    block_defs: list[dict[str, Any]] = field(default_factory=lambda: copy.deepcopy(BLOCK_DEFINITIONS))
    # Ventanas de modelo de los viewports de papel (§5C.5, F3). None = sin viewports evaluables.
    ventanas_layout: list[list[list[float]]] | None = None
    # Banderas de escenarios 0.3.1 (desactivadas: dibujo base sin cambios).
    bloques_compensacion: bool = False
    cuadros_fuera_de_layout: bool = False
    duplicados_masivos: bool = False
    viewport_observaciones: bool = False
    # Inserciones anidadas dentro de otros bloques (no viven en ningún espacio; §5C.3, 0.3.2).
    inserciones_anidadas: list[dict[str, Any]] = field(default_factory=list)
    # -- Fase 3 (§5D) --
    # INSUNITS del dibujo ficticio (0 = sin unidades: las escrituras responden UNITS_UNKNOWN salvo aceptar_sin_unidades).
    insunits: int = INSUNITS_DIBUJO
    # Estado de capas (nombres, sin distinguir mayúsculas) para R3 y §5D.8.
    capas_bloqueadas: set[str] = field(default_factory=set)
    capas_congeladas: set[str] = field(default_factory=set)
    capas_apagadas: set[str] = field(default_factory=set)
    # Límites de papel por layout (Layout.Limits), parte de la extensión de R4. Vacío = sin límites.
    limites_layout: dict[str, list[list[float]]] = field(default_factory=dict)
    # Estilos de texto y de cota del dibujo; el primero es el actual (Database.Textstyle / Dimstyle).
    estilos_texto: list[str] = field(default_factory=lambda: ["Standard", "Arial"])
    estilos_cota: list[str] = field(default_factory=lambda: ["ISO-25", "Standard"])
    # Biblioteca de bloques simulada (Contents/biblioteca) y atributos de las definiciones del dibujo.
    biblioteca: dict[str, dict[str, Any]] = field(default_factory=lambda: copy.deepcopy(BIBLIOTECA_BLOQUES))
    atributos_bloques: dict[str, list[tuple[str, bool, str]]] = field(
        default_factory=lambda: copy.deepcopy(ATRIBUTOS_BLOQUES_DIBUJO)
    )
    # -- Fase 4A (§5E) --
    # Reloj simulado del almacén de previsualizaciones (las pruebas lo avanzan para la caducidad de 15 min).
    ahora: datetime = RELOJ_INICIAL
    # Identidad del documento activo (DocKey); cambiarla simula que el usuario activó otro documento.
    doc_key: str = "doc-PLANO_Ñandú.dwg"
    # Previsualizaciones vivas por id (PreviewRegistry): herramienta, parámetros sin preview/dibujar_preview,
    # anclas con su huella, handles dibujados en MCP-PREVIEW, espacio, creación y caducidad.
    previews: dict[str, dict[str, Any]] = field(default_factory=dict)
    next_preview: int = 0
    # Pasos de deshacer simulados de las escrituras de Fase 4A (tabla de §5E.7) y de Fase L (§5F.6).
    pasos_deshacer: int = 0
    # -- Fase L (§5F) --
    # Dibujo de limpieza (build_drawing_faseL) en lugar del base; purga_dinamica añade PUERTA_DIN (bloque dinámico).
    faseL: bool = False
    purga_dinamica: bool = False
    # Capa actual (CLAYER) y tipo de línea actual (CELTYPE): exclusiones fijas de purgar.
    capa_actual: str = "0"
    tipo_linea_actual: str = "ByLayer"
    tipos_linea: list[str] = field(default_factory=lambda: ["ByBlock", "ByLayer", "CENTER", "Continuous"])
    regapps: list[str] = field(default_factory=lambda: ["ACAD"])
    regapps_usadas: set[str] = field(default_factory=set)
    # Capas propias del dibujo de Fase L (además de FAKE_LAYERS) y capas borradas (purgar / normalizar_capas).
    capas_extra: list[dict[str, Any]] = field(default_factory=list)
    capas_borradas: set[str] = field(default_factory=set)
    # Viewports de papel con su ventana de modelo (LayoutViewports.Windows) para borrar_fuera_de.
    viewports_layout: list[dict[str, Any]] = field(default_factory=list)
    # -- Fase 4B (§5G) --
    # Muros y contornos de prueba (build_entidades_fase4b) además del dibujo base; estilo de cota actual COTA75.
    fase4b: bool = False
    # Exports que "existen" para insertar_export (ruta sin mayúsculas → contenido) y carpetas_export_permitidas.
    exports: dict[str, dict[str, Any]] = field(default_factory=lambda: copy.deepcopy(EXPORTS_SIMULADOS))
    carpetas_export: list[str] | None = field(default_factory=lambda: LPS.get("carpetas_export_permitidas"))
    # Propiedades dinámicas de las definiciones del dibujo (nombre en mayúsculas → lista §5G.6).
    propiedades_bloques: dict[str, list[dict[str, Any]]] = field(default_factory=dict)
    # DIMSCALE y CANNOSCALE informados por cotas_automaticas.
    dimscale: float = 1.0
    cannoscale: str = "1:1"
    # -- Fase 5 (§5H) --
    # carpetas_auditoria_permitidas de lps.json; archivos_por_nombre busca por nombre (el lote audita copias temporales).
    carpetas_auditoria: list[str] | None = field(default_factory=lambda: LPS.get("carpetas_auditoria_permitidas"))
    archivos_por_nombre: bool = False

    def __post_init__(self) -> None:
        if self.fase4b:
            self.entities.extend(build_entidades_fase4b())
            self.layout_entities[LAYOUTS[0]].append(muro_rectangular("2BF", "MCP-DIBUJO", 1.0, 1.0, 4.0, 1.15))
            self.estilos_cota.insert(0, "COTA75")
        if self.faseL:
            self.entities = build_drawing_faseL()
            self.capas_extra = _capas_faseL()
            self.block_defs.extend(copy.deepcopy(BLOQUES_FASEL))
            for d in self.block_defs:
                if d["nombre"] in CONTENIDO_FASEL:
                    d["_contenido"] = copy.deepcopy(CONTENIDO_FASEL[d["nombre"]])
            if self.purga_dinamica:
                self.block_defs.append(copy.deepcopy(BLOQUE_DINAMICO_FASEL))
            self.layout_entities[LAYOUTS[0]].append(copy.deepcopy(VIEWPORT_FASEL))
            self.viewports_layout = copy.deepcopy(VIEWPORTS_LAYOUT_FASEL)
            self.tipos_linea.append("DASHED")
            self.estilos_texto.append("ROMANS")
            self.estilos_cota.append("COTA_VIEJA")
            self.regapps.extend(["ACAD_PSEXT", "AcadAnnotative"])
            self.regapps_usadas.add("AcadAnnotative")
        if self.bloques_compensacion:
            # F4: VENTANA_MM con 2 inserciones a 0.001 (compensadas); LAV_MM con 1 a 0.001 y 1 a escala 1
            # (0,45 × 0,51 m); MESA_MM con 1 a escala 1 y geometría de 450 × 510 m.
            self.block_defs.extend(copy.deepcopy(BLOQUES_COMPENSACION))
            for handle, nombre, x, escala, ancho, alto in (
                ("1F0", "VENTANA_MM", 50.0, 0.001, 0.45, 0.51),
                ("1F1", "VENTANA_MM", 52.0, 0.001, 0.45, 0.51),
                ("1F2", "LAV_MM", 54.0, 0.001, 0.45, 0.51),
                ("1F3", "LAV_MM", 56.0, 1.0, 0.45, 0.51),
                ("1F4", "MESA_MM", 600.0, 1.0, 450.0, 510.0),
            ):
                ins = _insert(handle, "PUERTAS", nombre, [x, 0.0, 0.0], 0.0, [[x, 0.0, 0.0], [x + ancho, alto, 0.0]], [])
                ins["escala"] = [escala, escala, escala]
                self.entities.append(ins)
            # 0.3.2: inserciones anidadas (el simulador las guarda aparte: no viven en ningún espacio).
            # "_tamano_anidada" = caja en el espacio tras componer los BlockTransform de los contenedores (o None).
            for handle, nombre, tamano in (
                ("1F5", "C_MM", [0.045, 0.045]),
                ("1F6", "C_MM", [0.045, 0.045]),
                ("1F7", "FLECHA_MM", None),
            ):
                ins = _insert(handle, "0", nombre, [0.0, 0.0, 0.0], 0.0, None, [])
                ins["escala"] = [0.045, 0.045, 0.045]
                ins["_tamano_anidada"] = tamano
                self.inserciones_anidadas.append(ins)
        if self.cuadros_fuera_de_layout:
            # F3: cuadro B (en layout) y restos C (fuera de toda ventana de viewport).
            self.entities.extend(_area_text(h, e, v, y, x=30.0, grupo="B") for h, e, v, y in CUADRO_B)
            self.entities.extend(_area_text(h, e, v, y, x=-2190.0, grupo="C") for h, e, v, y in CUADRO_C)
            self.ventanas_layout = copy.deepcopy(VENTANAS_LAYOUT_F3)
        if self.duplicados_masivos:
            # §5C.6: 51 LINE idénticas → handles acotados a 50 con handles_truncados/n_handles.
            for i in range(51):
                self.entities.append(_line(f"{0x3000 + i:X}", "MUROS", [100.0, 100.0, 0.0], [101.0, 100.0, 0.0]))
        if self.viewport_observaciones:
            # §5C.10: un VIEWPORT del layout en la capa MCP-OBSERVACIONES (no se borra).
            self.capas_creadas.append(_layer(CAPA_OBSERVACIONES, 1, 35, "mcp"))
            self.layout_entities[LAYOUTS[0]].append(
                {
                    "handle": "1FA",
                    "tipo": "VIEWPORT",
                    "capa": CAPA_OBSERVACIONES,
                    "bbox": [[10.0, 10.0, 0.0], [200.0, 150.0, 0.0]],
                }
            )

    def program_error(self, method: str, code: str, times: int = 1) -> None:
        self.errors.setdefault(method, []).extend([code] * times)


class FakeTransport(Transport):
    """Transporte en memoria: parsea los frames enviados y encola frames de respuesta."""

    def __init__(self, state: FakePluginState) -> None:
        self.state = state
        self._connected = False
        self._reader = FrameReader()
        self._outbox = bytearray()
        self._broken = False
        # Para probar el framing en recepción: trocear la salida en recv pequeños.
        self.max_chunk: int | None = 7
        self.sent_frames: list[bytes] = []

    @property
    def is_connected(self) -> bool:
        return self._connected and not self._broken

    def connect(self) -> None:
        from mcp_autocad.errors import NoInstanceError

        if not self.state.alive:
            raise NoInstanceError()
        self._connected = True
        self._broken = False
        self.state.connections += 1

    def close(self) -> None:
        self._connected = False

    def send(self, data: bytes) -> None:
        if not self.is_connected:
            raise PipeBrokenError("no conectado")
        if self.state.fail_send > 0:
            self.state.fail_send -= 1
            self._broken = True
            raise PipeBrokenError("ERROR_NO_DATA simulado")
        self.sent_frames.append(bytes(data))
        # Comprobación explícita del prefijo uint32 LE.
        (length,) = struct.unpack_from("<I", data, 0)
        assert length == len(data) - 4, "prefijo de longitud incorrecto"
        for request in self._reader.feed(data):
            self._handle(request)

    def recv(self, max_bytes: int, timeout: float) -> bytes:
        if self._broken:
            self._connected = False
            raise PipeBrokenError("el plugin cerró la conexión")
        if not self._outbox:
            # Nada que leer: equivale a que el plugin no responde en el plazo.
            raise PipeTimeoutError(timeout)
        size = min(max_bytes, len(self._outbox), self.max_chunk or len(self._outbox))
        chunk = bytes(self._outbox[:size])
        del self._outbox[:size]
        return chunk

    # -- simulación del plugin -----------------------------------------------
    def _reply(self, obj: dict[str, Any]) -> None:
        self._outbox.extend(encode_frame(obj))

    def _error(self, request_id: Any, code: str, extra: dict[str, Any] | None = None) -> None:
        rpc_code, message = ERROR_TABLE[code]
        symbolic = ERROR_ALIASES.get(code, code)
        data: dict[str, Any] = {"code": symbolic}
        if code == "BUSY":
            data.update({"motivo": "comando", "comando": "LINE"})
        elif code == "BUSY_COLA":
            data.update({"motivo": "cola", "comando": ""})
        elif code == "NOT_FOUND":
            data["capa"] = "NOEXISTE"
        elif code == "NOT_FOUND_HANDLE":
            data["handle"] = "FFFF"
        elif code == "INVALID_PARAMS":
            data["detalle"] = "lineas[0]: longitud cero"
        elif code == "INVALID_PARAMS_CONFIRMAR":
            data["detalle"] = "confirmar: debe ser true"
        if extra:
            data.update(extra)
        self._reply({"jsonrpc": "2.0", "id": request_id, "error": {"code": rpc_code, "message": message, "data": data}})

    def _handle(self, request: dict[str, Any]) -> None:
        state = self.state
        state.requests.append(request)
        method = request.get("method")
        request_id = request.get("id")
        params = request.get("params") or {}
        assert request.get("jsonrpc") == "2.0"
        assert isinstance(params, dict), "params debe ser objeto"

        if method in state.disconnect_on:
            state.disconnect_on.discard(method)
            self._broken = True
            return
        if method in state.slow:
            return
        if method in state.stale_reply_before:
            state.stale_reply_before.discard(method)
            self._reply({"jsonrpc": "2.0", "id": -999, "result": {"pid": state.pid, "viejo": True}})
        queued = state.errors.get(str(method))
        if queued:
            self._error(request_id, queued.pop(0))
            return

        handler = {
            "tools/manifest": self._manifest,
            "ping": self._ping,
            "estado": self._estado,
            "listar_capas": self._listar_capas,
            "linea": self._linea,
            "consultar_entidades": self._consultar_entidades,
            "propiedades": self._propiedades,
            "textos": self._textos,
            "bloques": self._bloques,
            "seleccion_actual": self._seleccion_actual,
            "xrefs": self._xrefs,
            "vista_png": self._vista_png,
            "resumen_archivo": self._resumen_archivo,
            "auditar_capas": self._auditar_capas,
            "entidades_en_capa_0": self._entidades_en_capa_0,
            "auditar_bloques": self._auditar_bloques,
            "areas_por_capa": self._areas_por_capa,
            "verificar_cuadro_areas": self._verificar_cuadro_areas,
            "duplicados": self._duplicados,
            "textos_fuera_de_escala": self._textos_fuera_de_escala,
            "auditar_dibujo": self._auditar_dibujo,
            "marcar_hallazgos": self._marcar_hallazgos,
            "limpiar_observaciones": self._limpiar_observaciones,
            "crear_capa": self._crear_capa,
            "polilinea": self._polilinea,
            "circulo": self._circulo,
            "arco": self._arco,
            "texto": self._texto,
            "mtext": self._mtext,
            "insertar_bloque": self._insertar_bloque,
            "cota": self._cota,
            "hatch": self._hatch,
            "mover": self._mover,
            "copiar": self._copiar,
            "rotar": self._rotar,
            "escalar": self._escalar,
            "borrar": self._borrar,
            "punto_de": self._punto_de,
            "distancia": self._distancia,
            "proyectar": self._proyectar,
            "muro": self._muro,
            "ejes_rejilla": self._ejes_rejilla,
            "recinto": self._recinto,
            "offset": self._offset,
            "array_rectangular": self._array_rectangular,
            "array_polar": self._array_polar,
            "confirmar": self._confirmar,
            "descartar_preview": self._descartar_preview,
            "borrar_fuera_de": self._borrar_fuera_de,
            "purgar": self._purgar,
            "normalizar_capas": self._normalizar_capas,
            "corregir_unidades_bloques": self._corregir_unidades_bloques,
            "vano": self._vano,
            "union_muros": self._union_muros,
            "cotas_automaticas": self._cotas_automaticas,
            "insertar_export": self._insertar_export,
            "auditar_archivo": self._auditar_archivo,
        }.get(str(method))
        if handler is None:
            self._reply(
                {
                    "jsonrpc": "2.0",
                    "id": request_id,
                    "error": {
                        "code": -32601,
                        "message": f"Método desconocido: {method}",
                        "data": {"code": "METHOD_NOT_FOUND", "metodo": method},
                    },
                }
            )
            return
        try:
            result = handler(request_id, params)
        except _RpcError as err:
            self._reply(
                {
                    "jsonrpc": "2.0",
                    "id": request_id,
                    "error": {"code": err.rpc_code, "message": err.message, "data": err.data},
                }
            )
            return
        if result is not None:
            self._reply({"jsonrpc": "2.0", "id": request_id, "result": result})

    def _units(self) -> dict[str, Any]:
        """insunits/unidades/advertencias (§2.1 + §2.2) según state.insunits (6 = m por defecto)."""
        ins = self.state.insunits
        corto, nombre, _m = UNIDADES.get(ins, ("desconocida", "desconocida", None))
        advertencias: list[str] = []
        if ins == 0:
            advertencias += [ADVERTENCIA_INSUNITS_0, ADVERTENCIA_LPS_SIN_UNIDADES]
        elif ins != INSUNITS_LPS:
            advertencias.append(f"El dibujo está en {nombre} (INSUNITS={ins}); el estándar LPS es metros (INSUNITS=6).")
        return {"insunits": ins, "unidades": corto, "advertencias": advertencias}

    # -- Fase 0 ---------------------------------------------------------------
    def _manifest(self, _id: Any, _params: dict[str, Any]) -> dict[str, Any]:
        return {
            "protocol_version": self.state.protocol_version,
            "plugin_version": FAKE_PLUGIN_VERSION,
            "pid": self.state.pid,
            "tools": manifest_tools(),
        }

    def _ping(self, _id: Any, _params: dict[str, Any]) -> dict[str, Any]:
        return {
            "pong": True,
            "plugin_version": FAKE_PLUGIN_VERSION,
            "protocol_version": FAKE_PROTOCOL_VERSION,
            "pid": self.state.pid,
            "hora": "2026-10-25T15:04:05.123-05:00",
        }

    def _estado(self, _id: Any, _params: dict[str, Any]) -> dict[str, Any]:
        return {
            "pid": self.state.pid,
            "version_autocad": "25.1s (LMS Tech)",
            "plugin_version": FAKE_PLUGIN_VERSION,
            "documento": {
                "nombre": "PLANO_Ñandú.dwg",
                "ruta": "C:\\proyectos\\PLANO_Ñandú.dwg",
                "sin_guardar": False,
                "modificado": True,
                "solo_lectura": False,
            },
            "insunits": 6,
            "unidades": "m",
            "measurement": 1,
            "ucs": {
                "origen": [0.0, 0.0, 0.0],
                "eje_x": [1.0, 0.0, 0.0],
                "eje_y": [0.0, 1.0, 0.0],
                "es_wcs": True,
                "nombre": "",
            },
            "comando_activo": None,
            "ocupado": False,
            "documentos_abiertos": 2,
            "estandar_lps": {"disponible": True, "insunits": 6, "unidades": "m"},
            # Desde 0.5 (§5.2, §5E.5): al final, la advertencia de huérfanas de MCP-PREVIEW cuando las hay.
            "advertencias": [advertencia_huerfanas(n)] if (n := self._huerfanas()) else [],
        }

    def _listar_capas(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        capas = [
            {
                "nombre": nombre,
                "color": 7,
                "color_true": None,
                "tipo_linea": "Continuous",
                "grosor": -3,
                "plot": True,
                "apagada": False,
                "congelada": False,
                "bloqueada": False,
                "es_xref": False,
                "actual": nombre == "0",
            }
            for nombre in ["0", "MUROS", "MCP-DIBUJO"]
        ]
        limit = int(params.get("limit", 200))
        return {
            "pid": self.state.pid,
            "capas": capas[:limit],
            "total": len(capas),
            "cursor_siguiente": None if limit >= len(capas) else str(limit),
            "capa_actual": "0",
            "insunits": 6,
            "unidades": "m",
            "advertencias": [],
        }

    # -- Fase 1 (lectura) -----------------------------------------------------
    def _space(self, params: dict[str, Any]) -> tuple[str, list[dict[str, Any]]]:
        espacio = params.get("espacio")
        if espacio is None or espacio.casefold() == "modelo":
            return "modelo", self.state.entities
        for layout in LAYOUTS:
            if layout.casefold() == espacio.casefold():
                return layout, self.state.layout_entities[layout]
        disponibles = ["modelo", *LAYOUTS]
        raise _not_found(
            f"El layout '{espacio}' no existe en el dibujo. Disponibles: {', '.join(disponibles)}"
            " (nombre de la pestaña, no el título del plano; listar_layouts da sus viewports).",
            espacio=espacio,
            disponibles=disponibles,
        )

    def _consultar_entidades(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        resumen = bool(params.get("resumen", False))
        if resumen and "cursor" in params:
            raise _invalid("cursor: no aplica con resumen")
        espacio, entities = self._space(params)
        bbox = _check_bbox(params)
        tipos = {t.upper() for t in params.get("tipos", [])}
        capas = {c.casefold() for c in params.get("capas", [])}
        rows = [
            e
            for e in entities
            if (not tipos or e["tipo"] in tipos)
            and (not capas or e["capa"].casefold() in capas)
            and (bbox is None or _bbox_hits(e["bbox"], bbox))
        ]
        base = {"pid": self.state.pid, "espacio": espacio}
        if resumen:
            return {
                **base,
                "total": len(rows),
                "por_tipo": _counts([e["tipo"] for e in rows]),
                "por_capa": _counts([e["capa"] for e in rows]),
                "origen_texto": ORIGEN_TEXTO,
                **self._units(),
            }
        page, total, nxt = paginate(rows, params)
        return {
            **base,
            "entidades": [_public(e) for e in page],
            "total": total,
            "cursor_siguiente": nxt,
            "origen_texto": ORIGEN_TEXTO,
            **self._units(),
        }

    def _propiedades(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        handle = params.get("handle")
        if not isinstance(handle, str) or not re.fullmatch(r"[0-9A-Fa-f]{1,16}", handle):
            raise _invalid("handle: debe ser hexadecimal de 1 a 16 caracteres")
        wanted = handle.upper()
        comunes = {
            "propietario": "modelo",
            "color": 256,
            "color_true": None,
            "tipo_linea": "ByLayer",
            "escala_tipo_linea": 1.0,
            "grosor": -1,
            "transparencia": None,
            "visible": True,
            "espacio_papel": False,
        }
        for entity in self.state.entities:
            for attrib in entity.get("_atributos", []):
                if attrib["handle"] == wanted:
                    props = _public(attrib)
                    props.update(comunes)
                    props.update({"valor": attrib["texto"], "insert_handle": entity["handle"], "bloque": entity["nombre"]})
                    return {"pid": self.state.pid, **props, "origen_texto": ORIGEN_TEXTO, **self._units()}
            if entity["handle"] != wanted:
                continue
            props = _public(entity)
            props.update(comunes)
            tipo = entity["tipo"]
            if tipo == "LWPOLYLINE":
                props.update({"bulges": [0.0] * entity["n_vertices"], "ancho_constante": 0.0, "elevacion": 0.0})
            elif tipo == "TEXT":
                props.update({"estilo": "Standard", "justificacion": "Left", "punto_alineacion": [0.0, 0.0, 0.0]})
            elif tipo == "MTEXT":
                props.update(
                    {
                        "contenido_formato": "{\\fArial|b1;" + entity["texto"] + "}",
                        "estilo": "Standard",
                        "justificacion": "TopLeft",
                        "interlineado": 1.0,
                    }
                )
            elif tipo == "INSERT":
                atributos = entity["_atributos"]
                props.update(
                    {
                        "nombre_bloque_real": entity["nombre"],
                        "atributos": [
                            {"handle": a["handle"], "tag": a["tag"], "valor": a["texto"], "capa": a["capa"], "invisible": a["invisible"]}
                            for a in atributos
                        ],
                        "n_atributos": len(atributos),
                    }
                )
            return {"pid": self.state.pid, **props, "origen_texto": ORIGEN_TEXTO, **self._units()}
        raise _not_found(f"No existe ninguna entidad con handle '{handle}'.", handle=handle)

    def _textos(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        espacio, entities = self._space(params)
        bbox = _check_bbox(params)
        tipos = set(params.get("tipos", ["TEXT", "MTEXT", "ATTRIB"]))
        capas = {c.casefold() for c in params.get("capas", [])}
        contiene = params.get("contiene")
        candidates: list[dict[str, Any]] = []
        for entity in entities:
            if entity["tipo"] in ("TEXT", "MTEXT"):
                candidates.append(_public(entity))
            elif entity["tipo"] == "INSERT":
                for attrib in entity["_atributos"]:
                    row = _public(attrib)
                    row.update({"bloque": entity["nombre"], "insert_handle": entity["handle"]})
                    candidates.append(row)
        rows = [
            r
            for r in candidates
            if r["tipo"] in tipos
            and (not capas or r["capa"].casefold() in capas)
            and (bbox is None or _bbox_hits(r["bbox"], bbox))
            and (contiene is None or contiene.casefold() in r["texto"].casefold())
        ]
        page, total, nxt = paginate(rows, params)
        return {
            "pid": self.state.pid,
            "espacio": espacio,
            "textos": page,
            "total": total,
            "cursor_siguiente": nxt,
            "origen_texto": ORIGEN_TEXTO,
            **self._units(),
        }

    def _bloques(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        nombre = params.get("nombre")
        if nombre is None:
            incluir_anonimos = bool(params.get("incluir_anonimos", False))
            defs = [d for d in self.state.block_defs if incluir_anonimos or not d["anonimo"]]
            defs.sort(key=lambda d: d["nombre"].casefold())
            page, total, nxt = paginate(defs, params)
            return {
                "pid": self.state.pid,
                "bloques": copy.deepcopy(page),
                "total": total,
                "cursor_siguiente": nxt,
                "origen_texto": ORIGEN_TEXTO,
                **self._units(),
            }
        definition = next((d for d in self.state.block_defs if d["nombre"].casefold() == nombre.casefold()), None)
        if definition is None:
            raise _not_found(f"El bloque '{nombre}' no existe en el dibujo.", bloque=nombre)
        inserciones = []
        for entity in self.state.entities:
            if entity["tipo"] == "INSERT" and entity["nombre"].casefold() == definition["nombre"].casefold():
                row = _public(entity)
                row["espacio"] = "modelo"
                row["atributos"] = {a["tag"]: a["texto"] for a in entity["_atributos"]}
                inserciones.append(row)
        page, total, nxt = paginate(inserciones, params)
        return {
            "pid": self.state.pid,
            "bloque": copy.deepcopy(definition),
            "inserciones": page,
            "total": total,
            "cursor_siguiente": nxt,
            "origen_texto": ORIGEN_TEXTO,
            **self._units(),
        }

    def _seleccion_actual(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        by_handle = {e["handle"]: e for e in self.state.entities}
        rows = [_public(by_handle[h]) for h in self.state.seleccion if h in by_handle]
        page, total, nxt = paginate(rows, params)
        return {
            "pid": self.state.pid,
            "espacio": "modelo",
            "modo": "entidades",
            "entidades": page,
            "total": total,
            "cursor_siguiente": nxt,
            "mecanismo": "aplicacion",
            "origen_texto": ORIGEN_TEXTO,
            **self._units(),
        }

    def _xrefs(self, _id: Any, _params: dict[str, Any]) -> dict[str, Any]:
        return {"pid": self.state.pid, "xrefs": copy.deepcopy(XREFS), "total": len(XREFS), **self._units()}

    @staticmethod
    def _ampliar(bbox: list[list[float]], ancho: int, alto: int) -> list[list[float]]:
        """Amplía la caja al aspecto ancho:alto sin recortarla (§5B.7, ViewWindow.FromBbox)."""
        (x0, y0), (x1, y1) = bbox[0][:2], bbox[1][:2]
        w, h = max(x1 - x0, 1e-9), max(y1 - y0, 1e-9)
        aspect = ancho / alto
        if w / h < aspect:
            w = h * aspect
        else:
            h = w / aspect
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        return [[cx - w / 2, cy - h / 2], [cx + w / 2, cy + h / 2]]

    def _vista_png(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        ancho = int(params.get("ancho", 1024))
        alto = int(params.get("alto", 768))
        mecanismo = params.get("mecanismo", "auto")
        bbox = _check_bbox(params)
        advertencias: list[str] = []
        # §5B.7 (F5, 0.3.2): 'espacio' = "modelo" o un layout; inexistente → NOT_FOUND con data.espacio; distinto
        # del activo → INVALID_PARAMS (AutoCAD solo plotea el layout actual y no se cambia de pestaña).
        activo, _ = self._space({"espacio": self.state.espacio_activo})
        capturado = activo
        if "espacio" in params:
            pedido, _ = self._space(params)
            if pedido.casefold() != activo.casefold():
                raise _invalid(
                    f"vista_png solo puede capturar el espacio activo ({activo}); el usuario debe cambiar a la "
                    f"pestaña '{pedido}' o se debe omitir 'espacio'."
                )
        if mecanismo == "captura" and bbox is not None:
            raise _invalid(
                "mecanismo 'captura' no admite bbox: la captura nativa no respeta la ventana pedida; use 'plot' o 'auto'"
            )
        # Con bbox siempre plot; si no, captura (salvo 'plot' explícito).
        usado = "plot" if (bbox is not None or mecanismo == "plot") else "captura"
        if bbox is not None:
            ventana = self._ampliar(bbox, ancho, alto)
        else:
            ventana = [[0.0, 0.0], [20.0, 15.0]]
        es_papel = capturado != "modelo"
        if es_papel:
            advertencias.insert(0, f"La captura corresponde al espacio papel (layout {capturado}), no al modelo.")
        units = self._units()
        units["advertencias"].extend(advertencias)
        result: dict[str, Any] = {
            "pid": self.state.pid,
            "mime": "image/png",
            "png_base64": PNG_1X1_BASE64,
            "ancho": ancho,
            "alto": alto,
            "bbox": ventana,
            "mecanismo": usado,
            "espacio": "papel" if es_papel else "modelo",
            "layout": capturado if es_papel else "Model",
            "vista_restaurada": True,
            **units,
        }
        if self.state.vista_png_sin_imagen:
            del result["png_base64"]
        return result

    def _resumen_archivo(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        ruta = params.get("ruta")
        if not isinstance(ruta, str) or not ntpath.isabs(ruta):
            raise _invalid("ruta: debe ser absoluta")
        if not ruta.casefold().endswith(".dwg"):
            raise _invalid("ruta: debe terminar en .dwg")
        if ruta.casefold() not in {a.casefold() for a in self.state.archivos}:
            raise _invalid("ruta: el archivo no existe")
        return {"pid": self.state.pid, "ruta": ruta, **copy.deepcopy(RESUMEN_ARCHIVO_FIXTURE)}

    # -- Fase 2 (auditoría, §5C) ---------------------------------------------
    def _read(self, payload: dict[str, Any], *advertencias: str) -> dict[str, Any]:
        """Respuesta de lectura de §5C: pid + payload + origen_texto + unidades/advertencias."""
        units = self._units()
        units["advertencias"].extend(advertencias)
        return {"pid": self.state.pid, **payload, "origen_texto": ORIGEN_TEXTO, **units}

    def _all_spaces(self) -> list[tuple[str, list[dict[str, Any]]]]:
        """Todos los espacios en el orden común: modelo y layouts por TabOrder."""
        return [("modelo", self.state.entities)] + [(l, self.state.layout_entities[l]) for l in LAYOUTS]

    def _layers(self) -> list[dict[str, Any]]:
        capas = FAKE_LAYERS + self.state.capas_extra + self.state.capas_creadas
        if not self.state.capas_borradas:
            return capas
        return [c for c in capas if c["nombre"].casefold() not in self.state.capas_borradas]

    def _find_layer(self, nombre: str) -> dict[str, Any] | None:
        return next((c for c in self._layers() if c["nombre"].casefold() == nombre.casefold()), None)

    def _find_entity(self, handle: str) -> tuple[str, dict[str, Any]] | None:
        wanted = handle.upper()
        for espacio, entities in self._all_spaces():
            for entity in entities:
                if entity["handle"] == wanted:
                    return espacio, entity
        return None

    @staticmethod
    def _area_of(entity: dict[str, Any]) -> float | None:
        """Área de una entidad candidata (§5C.4) o None si no tiene área."""
        tipo = entity["tipo"]
        if tipo in ("LWPOLYLINE", "POLYLINE") and not entity.get("cerrada"):
            return None
        if tipo not in ("LWPOLYLINE", "POLYLINE", "CIRCLE", "ELLIPSE", "HATCH", "REGION"):
            return None
        return entity.get("area")

    # auditar_capas (§5C.1)
    def _capa_rows(self) -> list[dict[str, Any]]:
        counts: dict[str, int] = {}
        for _esp, entities in self._all_spaces():
            for entity in entities:
                key = entity["capa"].casefold()
                counts[key] = counts.get(key, 0) + 1
        rows = []
        for capa in sorted(self._layers(), key=lambda c: c["nombre"].casefold()):
            n = counts.get(capa["nombre"].casefold(), 0)
            rows.append(
                {
                    "nombre": capa["nombre"],
                    "color": capa["color"],
                    "tipo_linea": capa["tipo_linea"],
                    "grosor": capa["grosor"],
                    "n_entidades": n,
                    "vacia": n == 0,
                    "clasificacion": capa["clasificacion"],
                    "diferencias": list(capa["diferencias"]),
                    "sugerencia": capa["sugerencia"],
                    # Desde 0.6 (§5F.5), calculados sobre el nombre como LayerClassifier (+ mapa_normalizacion, v2).
                    "patron_ajeno": campos_patron(capa["nombre"])[0],
                    "sugerencia_patron": campos_patron(capa["nombre"])[1],
                }
            )
        return rows

    def _hallazgos_capas(self) -> list[dict[str, Any]]:
        ajenas = {"ajena_revit_aia", "ajena_exportacion", "generica", "fuera_de_estandar"}
        out = []
        for row in self._capa_rows():
            clasif, nombre, n = row["clasificacion"], row["nombre"], row["n_entidades"]
            if clasif == "estandar_con_diferencias":
                severidad = "baja"
                mensaje = f"La capa '{nombre}' difiere del estándar LPS: {', '.join(row['diferencias'])}."
            elif clasif in ajenas:
                severidad = "media" if n > 0 else "baja"
                mensaje = f"La capa '{nombre}' ({clasif}) tiene {n} entidades"
                mensaje += f"; capa preferida: {row['sugerencia']}." if row["sugerencia"] else "."
            else:
                continue
            datos = {
                k: row[k]
                for k in ("clasificacion", "n_entidades", "diferencias", "sugerencia", "patron_ajeno", "sugerencia_patron")
            }
            out.append(_hallazgo("auditar_capas", severidad, mensaje, [], None, None, datos))
        return out

    def _auditar_capas(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        rows = self._capa_rows()
        filtro = set(params.get("clasificacion") or [])
        incluir_vacias = params.get("incluir_vacias", True)
        filtradas = [
            r for r in rows if (not filtro or r["clasificacion"] in filtro) and (incluir_vacias or not r["vacia"])
        ]
        page, total, nxt = paginate(filtradas, params)
        # §5C.1 (F8): el resumen se calcula SIEMPRE sobre todas las capas; el filtro solo afecta a capas/total.
        por_clasificacion = {c: 0 for c in POR_CLASIFICACION_ORDER}
        for r in rows:
            por_clasificacion[r["clasificacion"]] += 1
        resumen = {
            "n_capas": len(rows),
            "n_vacias": sum(1 for r in rows if r["vacia"]),
            "n_capas_xref": 0,
            "por_clasificacion": por_clasificacion,
        }
        return self._read(
            {
                "capas": page,
                "total": total,
                "cursor_siguiente": nxt,
                "resumen": resumen,
                "estandar_disponible": True,
            }
        )

    # entidades_en_capa_0 (§5C.2)
    def _capa0_por_espacio(self, params: dict[str, Any]) -> list[tuple[str, list[dict[str, Any]]]]:
        espacios = self._all_spaces() if params.get("espacio") is None else [self._space(params)]
        return [(esp, [e for e in ents if e["capa"] == "0"]) for esp, ents in espacios]

    def _hallazgos_capa0(self) -> list[dict[str, Any]]:
        out = []
        for espacio, entities in self._capa0_por_espacio({}):
            n = len(entities)
            if n == 0:
                continue
            por_tipo = _counts([e["tipo"] for e in entities])
            tipos = ", ".join(f"{t} {c}" for t, c in list(por_tipo.items())[:5])
            if len(por_tipo) > 5:
                tipos += ", …"
            datos = {"n": n, "por_tipo": por_tipo, "handles_truncados": n > 50, "n_handles": n}
            out.append(
                _hallazgo(
                    "entidades_en_capa_0",
                    "media",
                    f"{n} entidades en la capa 0 en el espacio {espacio} ({tipos}).",
                    [e["handle"] for e in entities],
                    _union2d([e["bbox"] for e in entities[:50]]),
                    espacio,
                    datos,
                )
            )
        return out

    def _entidades_en_capa_0(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        por_espacio_ents = self._capa0_por_espacio(params)
        rows = [
            {"handle": e["handle"], "tipo": e["tipo"], "espacio": esp, "bbox": copy.deepcopy(e["bbox"])}
            for esp, ents in por_espacio_ents
            for e in ents
        ]
        page, total, nxt = paginate(rows, params)
        return self._read(
            {
                "total": total,
                "por_tipo": _counts([r["tipo"] for r in rows]),
                "por_espacio": {esp: len(ents) for esp, ents in por_espacio_ents if ents},
                "entidades": page,
                "cursor_siguiente": nxt,
            }
        )

    # auditar_bloques (§5C.3)
    def _inserciones(self, nombre: str) -> list[dict[str, Any]]:
        directas = [
            e
            for _esp, entities in self._all_spaces()
            for e in entities
            if e["tipo"] == "INSERT" and e["nombre"].casefold() == nombre.casefold()
        ]
        anidadas = [e for e in self.state.inserciones_anidadas if e["nombre"].casefold() == nombre.casefold()]
        return directas + anidadas

    def _bloques_auditoria(self, secciones: list[str] | None = None) -> tuple[dict[str, Any], list[dict[str, Any]]]:
        secciones = secciones or SECCIONES_BLOQUES
        all_defs = self.state.block_defs
        defs = [d for d in all_defs if not d["anonimo"] and not d["es_xref"]]
        unidades, sospechosos, sin_uso = [], [], []
        for d in defs:
            nombre, u = d["nombre"], d["unidades_bloque"]
            inserts = self._inserciones(nombre)
            n_ins = len(inserts)
            handles = [e["handle"] for e in inserts]
            if u not in (0, INSUNITS_DIBUJO) and n_ins > 0:
                unidades.append(self._hallazgo_unidades_bloque(nombre, u, inserts))
            motivo = _sospecha_nombre(nombre)
            if motivo is not None:
                sospechosos.append(
                    _hallazgo(
                        "auditar_bloques",
                        "media" if n_ins == 0 else "baja",
                        f"El nombre de bloque '{nombre}' parece arbitrario ({motivo}); {n_ins} inserciones.",
                        handles,
                        _union2d([e["bbox"] for e in inserts[:50]]),
                        None,
                        {"bloque": nombre, "motivo": motivo, "n_inserciones": n_ins},
                    )
                )
            if n_ins == 0:
                sin_uso.append(
                    _hallazgo(
                        "auditar_bloques",
                        "baja",
                        f"El bloque '{nombre}' no tiene inserciones.",
                        [],
                        None,
                        None,
                        {"bloque": nombre, "n_entidades": d["n_entidades"], "unidades_bloque": u},
                    )
                )
        resumen = {
            "n_definiciones": len(defs),
            "n_anonimos": sum(1 for d in all_defs if d["anonimo"]),
            "n_xrefs": sum(1 for d in all_defs if d["es_xref"]),
            "unidades_distintas": len(unidades),
            "nombres_sospechosos": len(sospechosos),
            "sin_uso": len(sin_uso),
        }
        por_seccion = {"unidades_distintas": unidades, "nombres_sospechosos": sospechosos, "sin_uso": sin_uso}
        hallazgos = [h for s in SECCIONES_BLOQUES if s in secciones for h in por_seccion[s]]
        return resumen, hallazgos

    @staticmethod
    def _hallazgo_unidades_bloque(nombre: str, u: int, inserts: list[dict[str, Any]]) -> dict[str, Any]:
        """Hallazgo unidades_distintas con compensación por inserción (§5C.3, F4)."""
        corto, nombre_es, metros = UNIDADES.get(u, ("desconocida", "desconocida", None))
        metros_dib = UNIDADES[INSUNITS_DIBUJO][2]
        factor = metros / metros_dib if metros is not None and metros_dib else None
        n_ins = len(inserts)
        # Hasta 500 inserciones en orden de handle (muestreo acotado de §5C.3).
        evaluadas = sorted(inserts, key=lambda e: int(e["handle"], 16))[:500]
        compensadas: list[dict[str, Any]] = []
        no_compensadas: list[dict[str, Any]] = []
        for e in evaluadas:
            escala = abs(float(e["escala"][0]))
            if factor is not None and abs(escala / factor - 1) <= 0.01:
                compensadas.append(e)
            else:
                no_compensadas.append(e)
        escalas = list(dict.fromkeys(round(abs(float(e["escala"][0])), 6) for e in no_compensadas))[:5]
        primera = next((e["bbox"] for e in evaluadas if e.get("bbox") is not None), None)
        tamano = None if primera is None else [primera[1][0] - primera[0][0], primera[1][1] - primera[0][1]]
        # Tamaño real (m) de la primera inserción no compensada en un espacio; si todas están anidadas (0.3.2), el de
        # la primera anidada medible a través de sus contenedores ("_tamano_anidada").
        tamano_real_m: list[float] | None = None
        por_anidada = False
        directas_nc = [e for e in no_compensadas if "_tamano_anidada" not in e]
        anidadas_nc = [e for e in no_compensadas if "_tamano_anidada" in e]
        if directas_nc and metros_dib:
            caja = directas_nc[0]["bbox"]
            tamano_real_m = [(caja[1][0] - caja[0][0]) * metros_dib, (caja[1][1] - caja[0][1]) * metros_dib]
        elif anidadas_nc and metros_dib:
            medida = next((e["_tamano_anidada"] for e in anidadas_nc if e["_tamano_anidada"] is not None), None)
            if medida is not None:
                tamano_real_m = [medida[0] * metros_dib, medida[1] * metros_dib]
                por_anidada = True
                tamano = tamano or list(medida)
        todas_anidadas = bool(no_compensadas) and not directas_nc and tamano_real_m is None
        plausible = tamano_real_m is not None and (
            TAMANO_PLAUSIBLE_M[0] <= max(abs(v) for v in tamano_real_m) <= TAMANO_PLAUSIBLE_M[1]
        )
        metadato_incorrecto = False
        base = (
            f"El bloque '{nombre}' está definido en {nombre_es} (INSUNITS={u}) y el dibujo en "
            f"{UNIDADES[INSUNITS_DIBUJO][1]} (INSUNITS={INSUNITS_DIBUJO})"
        )
        if factor is None:
            severidad, implicadas = "media", evaluadas
            mensaje = f"{base}; {n_ins} inserciones, sin factor de escala métrico."
        elif not no_compensadas:
            severidad, implicadas = "baja", []
            mensaje = (
                f"{base}; sus {len(evaluadas)} inserciones están escaladas a {factor:g}: compensado; riesgo solo "
                "al redefinir el bloque o insertarlo sin escala automática."
            )
        elif plausible and tamano_real_m is not None:
            # Decisión F4: geometría ya en unidades del dibujo; lo erróneo es el metadato de unidades del bloque.
            severidad, implicadas, metadato_incorrecto = "media", no_compensadas, True
            veces = 1 / factor if factor < 1 else factor
            efecto = (
                "del mismo tamaño"
                if abs(factor - 1) < 1e-12
                else f"{round(veces, 4):g}× " + ("más pequeño" if factor < 1 else "más grande")
            )
            ancho_txt, alto_txt = (f"{round(v, 2):g}" for v in tamano_real_m)
            mensaje = (
                f"El bloque '{nombre}' declara unidades {corto} pero su geometría ya está en las unidades del dibujo "
                f"(tamaño real ≈ {ancho_txt}×{alto_txt} m): el metadato de unidades es incorrecto; si se reinserta con "
                f"escala automática quedará {efecto}."
            )
        elif tamano_real_m is None:
            # 0.3.2: "alta" solo con un tamaño MEDIDO implausible; sin medida → media "no medible".
            severidad, implicadas = "media", no_compensadas
            txt_esc = (
                f"escala {escalas[0]:g}" if len(escalas) == 1 else "escalas " + ", ".join(f"{s:g}" for s in escalas[:3])
            )
            mensaje = (
                f"El bloque '{nombre}' declara unidades {corto} y {len(no_compensadas)} de sus inserciones evaluadas "
                f"no están compensadas ({txt_esc}), pero su tamaño real es no medible"
                f"{' (inserciones anidadas)' if todas_anidadas else ''}: revise el metadato de unidades del bloque."
            )
        else:
            severidad, implicadas = "alta", no_compensadas
            txt_esc = (
                f"escala {escalas[0]:g}" if len(escalas) == 1 else "escalas " + ", ".join(f"{s:g}" for s in escalas[:3])
            )
            mensaje = (
                f"{base}; factor de escala implícito {factor:g}, pero {len(no_compensadas)} de sus {len(evaluadas)} "
                f"inserciones tienen {txt_esc} (no compensadas; p. ej. {no_compensadas[0]['handle']})."
            )
        datos: dict[str, Any] = {
            "bloque": nombre,
            "unidades_bloque": u,
            "unidades_bloque_nombre": corto,
            "insunits_dibujo": INSUNITS_DIBUJO,
            "n_inserciones": n_ins,
            "factor_escala": factor,
            "n_compensadas": len(compensadas),
            "n_no_compensadas": len(no_compensadas),
            "escalas_no_compensadas": escalas,
            "inserciones_evaluadas": len(evaluadas),
            "muestra_parcial": n_ins > len(evaluadas),
            "tamano_ejemplo": tamano,
            "tamano_real_m": tamano_real_m,
            "metadato_unidades_incorrecto": metadato_incorrecto,
            "tamano_por_anidada": por_anidada,
        }
        if len(implicadas) > 50:
            datos["handles_truncados"] = True
            datos["n_handles"] = len(implicadas)
        return _hallazgo(
            "auditar_bloques",
            severidad,
            mensaje,
            [e["handle"] for e in implicadas],
            _union2d([e["bbox"] for e in implicadas[:50]]),
            None,
            datos,
        )

    def _auditar_bloques(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        resumen, hallazgos = self._bloques_auditoria(params.get("seccion"))
        ordenados = _ordenar(_numerar(hallazgos))
        page, total, nxt = paginate(ordenados, params)
        return self._read({"resumen": resumen, "hallazgos": page, "total": total, "cursor_siguiente": nxt})

    # areas_por_capa (§5C.4)
    def _areas_por_capa(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        detalle = bool(params.get("detalle", False))
        if "cursor" in params and not detalle:
            raise _invalid("cursor: solo aplica con detalle")
        espacio, entities = self._space(params)
        capas_filtro = {c.casefold() for c in params.get("capas", [])}
        candidatas = [
            e
            for e in entities
            if self._area_of(e) is not None or (e["tipo"] in ("HATCH", "REGION") and e.get("area") is None)
            if not capas_filtro or e["capa"].casefold() in capas_filtro
        ]
        por_capa: dict[str, dict[str, Any]] = {}
        for e in candidatas:
            fila = por_capa.setdefault(e["capa"], {"capa": e["capa"], "n": 0, "area_total": 0.0, "n_sin_area": 0, "_tipos": []})
            fila["n"] += 1
            fila["_tipos"].append(e["tipo"])
            area = self._area_of(e)
            if area is None:
                fila["n_sin_area"] += 1
            else:
                fila["area_total"] += float(area)
        filas = []
        for fila in por_capa.values():
            tipos = fila.pop("_tipos")
            filas.append({**fila, "por_tipo": _counts(tipos)})
        filas.sort(key=lambda f: (-f["area_total"], f["capa"].casefold()))
        payload: dict[str, Any] = {
            "espacio": espacio,
            "capas": filas[:500],
            "capas_truncadas": len(filas) > 500,
            "unidad_area": f"{UNIDADES[INSUNITS_DIBUJO][0]}²",
        }
        if detalle:
            rows = [
                {"handle": e["handle"], "tipo": e["tipo"], "capa": e["capa"], "area": self._area_of(e), "bbox": copy.deepcopy(e["bbox"])}
                for e in candidatas
            ]
            page, total, nxt = paginate(rows, params)
            payload.update({"entidades": page, "total": total, "cursor_siguiente": nxt})
        return self._read(payload)

    # verificar_cuadro_areas (§5C.5)
    def _cuadros(self, params: dict[str, Any]) -> tuple[str, list[dict[str, Any]], int, list[dict[str, Any]], list[str]]:
        espacio, entities = self._space(params)
        bbox = _check_bbox(params)
        contornos = params.get("contornos") or {}
        # Validación de contornos antes de leer textos (errores del contrato §5C.5).
        areas_contorno: dict[str, tuple[str, float, Any]] = {}
        for etiqueta, handle in contornos.items():
            if not re.fullmatch(ETIQUETA_CONTORNO_RE, str(etiqueta)):
                raise _invalid(f"contornos: etiqueta no reconocida '{etiqueta}'")
            found = self._find_entity(str(handle))
            if found is None:
                raise _not_found(f"No existe ninguna entidad con handle '{handle}'.", handle=handle)
            area = self._area_of(found[1])
            if area is None:
                raise _invalid(f"contornos.{etiqueta}: la entidad no tiene área")
            areas_contorno[etiqueta] = (found[1]["handle"], float(area), found[1]["bbox"])
        textos = [
            e
            for e in entities
            if e["tipo"] in ("TEXT", "MTEXT") and len(e["texto"]) <= 120 and "\n" not in e["texto"]
            if bbox is None or _bbox_hits(e["bbox"], bbox)
        ]
        filas = [e for e in textos if "_cuadro" in e]
        advertencias: list[str] = []
        cuadros = self._agrupar_cuadros(filas, espacio)
        if not cuadros:
            advertencias.append(f"No se detectó ningún cuadro de áreas en el espacio {espacio}.")
        fuera = [c["id"] for c in cuadros if not c["en_layout"]]
        if fuera:
            advertencias.append(
                f"{len(fuera)} cuadro(s) fuera de los viewports de los layouts ({', '.join(fuera)}): "
                "posible versión anterior del plano."
            )
        bbox_de = {e["handle"]: e["bbox"] for e in filas}
        hallazgos = self._reglas_cuadro(cuadros, espacio, bbox_de) + self._discrepancias(cuadros, espacio, bbox_de)
        for etiqueta, (h_contorno, area, bbox_contorno) in areas_contorno.items():
            con_etiqueta = [c for c in cuadros if etiqueta in c["valores"]]
            if not con_etiqueta:
                advertencias.append(f"contornos.{etiqueta}: ningún cuadro declara esa etiqueta.")
                continue
            for cuadro in con_etiqueta:
                declarado = cuadro["valores"][etiqueta]
                if abs(area - declarado) <= max(0.01, 0.001 * declarado):
                    continue
                h_texto = cuadro["handles"][etiqueta]["etiqueta"]
                texto_bbox = next(e["bbox"] for e in filas if e["handle"] == h_texto)
                hallazgos.append(
                    _hallazgo(
                        "verificar_cuadro_areas",
                        "alta",
                        f"{etiqueta} declara {declarado:.2f} en {cuadro['id']} y el contorno {h_contorno} mide {area:.2f}.",
                        list(dict.fromkeys([h_texto, cuadro["handles"][etiqueta]["valor"], h_contorno])),
                        _union2d([texto_bbox, bbox_contorno]),
                        espacio,
                        {
                            "regla": "contorno_vs_declarado",
                            "cuadro": cuadro["id"],
                            "etiqueta": etiqueta,
                            "esperado": area,
                            "declarado": declarado,
                            "diferencia": declarado - area,
                        },
                    )
                )
        return espacio, cuadros, len(textos), hallazgos, advertencias

    def _agrupar_cuadros(self, filas: list[dict[str, Any]], espacio: str) -> list[dict[str, Any]]:
        """Cuadros del simulador (uno por grupo) con en_layout y reglas evaluadas (§5C.5, 0.3.1)."""
        grupos: dict[str, list[dict[str, Any]]] = {}
        for e in filas:
            grupos.setdefault(e["_cuadro"]["grupo"], []).append(e)

        def ancla(miembros: list[dict[str, Any]]) -> tuple[float, float]:
            lote = [m for m in miembros if m["_cuadro"]["etiqueta"] == "LOTE"]
            ref = lote[0] if lote else max(miembros, key=lambda m: m["posicion"][1])
            return (-ref["posicion"][1], ref["posicion"][0])

        ordenados = sorted(grupos.values(), key=ancla)[:20]
        ventanas = self.state.ventanas_layout
        cuadros = []
        for k, miembros in enumerate(ordenados, start=1):
            valores = {m["_cuadro"]["etiqueta"]: m["_cuadro"]["valor"] for m in miembros}
            caja = _union2d([m["bbox"] for m in miembros])
            if espacio != "modelo" or not ventanas or caja is None:
                en_layout = True
            else:
                en_layout = any(_bbox_hits(caja, v) for v in ventanas)
            evaluadas, no_evaluables = self._reglas_aplicables(valores)
            cuadros.append(
                {
                    "id": f"cuadro-{k}",
                    "titulo": None,
                    "bbox": caja,
                    "valores": valores,
                    "handles": {m["_cuadro"]["etiqueta"]: {"etiqueta": m["handle"], "valor": m["handle"]} for m in miembros},
                    "etiquetas_sin_valor": [],
                    "en_layout": en_layout,
                    "reglas_evaluadas": evaluadas,
                    "reglas_no_evaluables": no_evaluables,
                }
            )
        return cuadros

    @staticmethod
    def _reglas_aplicables(valores: dict[str, float]) -> tuple[list[str], list[dict[str, str]]]:
        """reglas_evaluadas / reglas_no_evaluables (motivo = primera etiqueta que falta, §5C.5)."""
        tiene_piso = any(k.startswith("AREA PISO ") for k in valores)
        necesarias = {
            "suma_pisos": ["TOTAL CONSTRUIDO", "AREA PISO n"],
            "libre": ["LOTE", "AREA PISO 1", "LIBRE"],
            "ocupacion": ["OCUPACION", "AREA PISO 1", "LOTE"],
            "construccion": ["CONSTRUCCION", "TOTAL CONSTRUIDO", "LOTE"],
        }
        evaluadas: list[str] = []
        no_evaluables: list[dict[str, str]] = []
        for regla in REGLAS_CUADRO:
            falta = next(
                (et for et in necesarias[regla] if not (tiene_piso if et == "AREA PISO n" else et in valores)), None
            )
            if falta is not None:
                no_evaluables.append({"regla": regla, "motivo": f"falta {falta}"})
            elif regla in ("ocupacion", "construccion") and valores["LOTE"] <= 0:
                no_evaluables.append({"regla": regla, "motivo": "LOTE no es mayor que 0"})
            else:
                evaluadas.append(regla)
        return evaluadas, no_evaluables

    @staticmethod
    def _handles_de(cuadro: dict[str, Any], etiquetas: list[str]) -> list[str]:
        out: list[str] = []
        for et in etiquetas:
            par = cuadro["handles"].get(et)
            if par:
                out.extend([par["etiqueta"], par["valor"]])
        return list(dict.fromkeys(out))

    def _reglas_cuadro(self, cuadros: list[dict[str, Any]], espacio: str, bbox_de: dict[str, Any]) -> list[dict[str, Any]]:
        """Reglas internas suma_pisos / libre / ocupacion / construccion (§5C.5)."""
        out = []
        for c in cuadros:
            v = c["valores"]
            pisos = sorted(k for k in v if k.startswith("AREA PISO "))
            casos: list[tuple[str, str, str, float, float, list[str], float]] = []
            if "suma_pisos" in c["reglas_evaluadas"]:
                total = v["TOTAL CONSTRUIDO"]
                esperado = sum(v[k] for k in pisos) + v.get("REMATE PUNTO FIJO", 0.0)
                tol = max(0.01, 0.001 * total)
                casos.append(("suma_pisos", "alta", "TOTAL CONSTRUIDO", esperado, total, pisos + ["REMATE PUNTO FIJO", "TOTAL CONSTRUIDO"], tol))
            if "libre" in c["reglas_evaluadas"]:
                esperado = v["LOTE"] - v["AREA PISO 1"]
                casos.append(("libre", "media", "LIBRE", esperado, v["LIBRE"], ["LOTE", "AREA PISO 1", "LIBRE"], max(0.01, 0.001 * v["LOTE"])))
            for regla, etiqueta, num in (("ocupacion", "OCUPACION", "AREA PISO 1"), ("construccion", "CONSTRUCCION", "TOTAL CONSTRUIDO")):
                if regla in c["reglas_evaluadas"]:
                    esperado = round(v[num] / v["LOTE"], 2)
                    casos.append((regla, "media", etiqueta, esperado, round(v[etiqueta], 2), [etiqueta, num, "LOTE"], 0.005 + 1e-9))
            for regla, severidad, etiqueta, esperado, declarado, implicadas, tol in casos:
                if abs(declarado - esperado) <= tol:
                    continue
                handles = self._handles_de(c, implicadas)
                out.append(
                    _hallazgo(
                        "verificar_cuadro_areas",
                        severidad,
                        f"{etiqueta} en {c['id']}: declarado {declarado:.2f}, esperado {esperado:.2f} ({regla}).",
                        handles,
                        _union2d([bbox_de[h] for h in handles]),
                        espacio,
                        {
                            "regla": regla,
                            "cuadro": c["id"],
                            "etiqueta": etiqueta,
                            "esperado": esperado,
                            "declarado": declarado,
                            "diferencia": declarado - esperado,
                        },
                    )
                )
        return out

    def _discrepancias(self, cuadros: list[dict[str, Any]], espacio: str, bbox_de: dict[str, Any]) -> list[dict[str, Any]]:
        """cuadros_discrepantes en dos pasadas (§5C.5, F3): (i) entre cuadros en layout → alta;
        (ii) si un cuadro fuera de layout aporta un valor distinto → media con sufijo."""
        if len(cuadros) < 2:
            return []
        etiquetas: list[str] = []
        for c in cuadros:
            etiquetas.extend(k for k in c["valores"] if k not in etiquetas)
        out = []
        for et in etiquetas:
            tol = 0.005 if et in ("OCUPACION", "CONSTRUCCION") else 0.01
            con = [c for c in cuadros if et in c["valores"]]
            if len(con) < 2:
                continue

            def difieren(grupo: list[dict[str, Any]]) -> bool:
                vals = [c["valores"][et] for c in grupo]
                return len(grupo) >= 2 and max(vals) - min(vals) > tol

            def hallazgo(grupo: list[dict[str, Any]], severidad: str, ambito: str) -> dict[str, Any]:
                partes = ", ".join(f"{c['id']} = {c['valores'][et]:.2f}" for c in grupo)
                fuera = [c["id"] for c in grupo if not c["en_layout"]]
                mensaje = f"{et} difiere entre cuadros: {partes}"
                mensaje += SUFIJO_FUERA_DE_LAYOUT if fuera else "."
                datos: dict[str, Any] = {
                    "regla": "cuadros_discrepantes",
                    "etiqueta": et,
                    "valores": [{"cuadro": c["id"], "valor": c["valores"][et]} for c in grupo],
                    "ambito": ambito,
                }
                if fuera:
                    datos["cuadros_fuera_de_layout"] = fuera
                handles = [h for c in grupo for h in self._handles_de(c, [et])]
                return _hallazgo(
                    "verificar_cuadro_areas",
                    severidad,
                    mensaje,
                    handles,
                    _union2d([bbox_de[h] for h in handles]),
                    espacio,
                    datos,
                )

            dentro = [c for c in con if c["en_layout"]]
            fuera = [c for c in con if not c["en_layout"]]
            if difieren(dentro):
                out.append(hallazgo(dentro, "alta", "en_layout"))
            valores_dentro = [c["valores"][et] for c in dentro]
            aporta = any(all(abs(c["valores"][et] - w) > tol for w in valores_dentro) for c in fuera)
            if fuera and difieren(con) and aporta:
                out.append(hallazgo(con, "media", "con_fuera_de_layout"))
        return out

    def _verificar_cuadro_areas(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        espacio, cuadros, n_leidos, hallazgos, advertencias = self._cuadros(params)
        hallazgos = _ordenar(_numerar(hallazgos))
        return self._read(
            {
                "espacio": espacio,
                "cuadros": cuadros,
                "n_textos_leidos": n_leidos,
                "hallazgos": hallazgos[:200],
                "total": len(hallazgos),
            },
            *advertencias,
        )

    # duplicados (§5C.6)
    @staticmethod
    def _dup_key(entity: dict[str, Any], tol: float) -> tuple[Any, ...] | None:
        def r(values: list[float]) -> tuple[int, ...]:
            return tuple(int(round(float(v) / tol)) for v in values)

        tipo = entity["tipo"]
        if tipo == "LINE":
            return ("LINE", *sorted([r(entity["inicio"]), r(entity["fin"])]))
        if tipo == "LWPOLYLINE" and entity.get("vertices"):
            directa = tuple(r(p[:2]) for p in entity["vertices"])
            return ("LWPOLYLINE", entity["cerrada"], min(directa, directa[::-1]))
        if tipo == "CIRCLE":
            return ("CIRCLE", r(entity["centro"]), r([entity["radio"]]))
        if tipo == "INSERT":
            return ("INSERT", entity["nombre"].upper(), r(entity["posicion"]), r(entity["escala"]), r([entity["rotacion"]]))
        if tipo in ("TEXT", "MTEXT"):
            return (tipo, entity["texto"], r(entity["posicion"]), r([entity["altura"]]))
        return None

    def _grupos_duplicados(self, params: dict[str, Any]) -> tuple[str, float, list[dict[str, Any]]]:
        tol = params.get("tolerancia", 1e-6)
        if isinstance(tol, bool) or not isinstance(tol, (int, float)) or not math.isfinite(tol) or not (1e-12 <= tol <= 1):
            raise _invalid("tolerancia: debe ser un número entre 1e-12 y 1")
        espacio, entities = self._space(params)
        tipos = set(params.get("tipos") or TIPOS_DUPLICADOS)
        capas = {c.casefold() for c in params.get("capas", [])}
        grupos: dict[tuple[Any, ...], list[dict[str, Any]]] = {}
        for e in entities:
            if e["tipo"] not in tipos or (capas and e["capa"].casefold() not in capas):
                continue
            key = self._dup_key(e, float(tol))
            if key is not None:
                grupos.setdefault(key, []).append(e)
        orden = {e["handle"]: i for i, e in enumerate(entities)}
        filas = []
        for miembros in grupos.values():
            if len(miembros) < 2:
                continue
            fila: dict[str, Any] = {
                "tipo": miembros[0]["tipo"],
                "n": len(miembros),
                # §5C.6 (desviación aceptada): como mucho 50 handles por grupo.
                "handles": [m["handle"] for m in miembros[:50]],
                "capas": list(dict.fromkeys(m["capa"] for m in miembros)),
                "bbox": _union2d([m["bbox"] for m in miembros]),
            }
            if len(miembros) > 50:
                fila["handles_truncados"] = True
                fila["n_handles"] = len(miembros)
            filas.append(fila)
        filas.sort(key=lambda g: (-g["n"], orden[g["handles"][0]]))
        return espacio, float(tol), filas

    def _hallazgos_duplicados(self, params: dict[str, Any]) -> list[dict[str, Any]]:
        espacio, tol, grupos = self._grupos_duplicados({k: v for k, v in params.items() if k == "espacio"})
        out = []
        for g in grupos:
            capas = ", ".join(g["capas"])
            out.append(
                _hallazgo(
                    "duplicados",
                    "media" if g["n"] >= 5 else "baja",
                    f"{g['n']} {g['tipo']} idénticas (tolerancia {tol:.0e}) en la capa {capas}.",
                    g["handles"],
                    g["bbox"],
                    espacio,
                    {
                        "tipo": g["tipo"],
                        "n": g["n"],
                        "capas": g["capas"],
                        **({"handles_truncados": True, "n_handles": g["n"]} if g["n"] > 50 else {}),
                    },
                )
            )
        return out

    def _duplicados(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        espacio, tol, grupos = self._grupos_duplicados(params)
        page, total, nxt = paginate(grupos, params)
        resumen = {
            "n_grupos": len(grupos),
            "n_entidades_sobrantes": sum(g["n"] - 1 for g in grupos),
            "por_tipo": _counts([g["tipo"] for g in grupos]),
        }
        return self._read(
            {
                "espacio": espacio,
                "tolerancia": tol,
                "resumen": resumen,
                "grupos": page,
                "total": total,
                "cursor_siguiente": nxt,
            }
        )

    # textos_fuera_de_escala (§5C.7)
    def _alturas(self, params: dict[str, Any]) -> tuple[str, float | None, int, list[dict[str, Any]]]:
        espacio, entities = self._space(params)
        items: list[tuple[dict[str, Any], dict[str, Any]]] = []
        for e in entities:
            if e["tipo"] in ("TEXT", "MTEXT"):
                items.append((e, e))
            elif e["tipo"] == "INSERT":
                items.extend((a, a) for a in e.get("_atributos", []))
        alturas = [float(t["altura"]) for t, _ in items]
        mediana = statistics.median(alturas) if alturas else None
        filas: list[dict[str, Any]] = []
        if len(alturas) >= 2 and mediana is not None:
            for t, _ in items:
                h = float(t["altura"])
                if h <= 0:
                    motivo = "no_positiva"
                elif h < 0.2 * mediana:
                    motivo = "menor"
                elif h > 5 * mediana:
                    motivo = "mayor"
                else:
                    continue
                texto = t["texto"] if len(t["texto"]) <= 80 else t["texto"][:80] + "…"
                filas.append(
                    {
                        "handle": t["handle"],
                        "tipo": t["tipo"],
                        "capa": t["capa"],
                        "altura": h,
                        "ratio": h / mediana if mediana else None,
                        "motivo": motivo,
                        "texto": texto,
                        "posicion": copy.deepcopy(t["posicion"]),
                        "_bbox": t.get("bbox"),
                    }
                )
        grupo = {"mayor": 0, "menor": 1, "no_positiva": 2}

        def key(f: dict[str, Any]) -> tuple[int, float]:
            ratio = f["ratio"] or 0.0
            return (grupo[f["motivo"]], -ratio if f["motivo"] == "mayor" else ratio)

        filas.sort(key=key)  # sort estable: empate por orden de base de datos
        return espacio, mediana, len(alturas), filas

    def _hallazgos_textos(self, params: dict[str, Any]) -> list[dict[str, Any]]:
        espacio, mediana, _n, filas = self._alturas({k: v for k, v in params.items() if k == "espacio"})
        frases = {
            "menor": "con altura menor que 0.2 × la mediana ({m:g})",
            "mayor": "con altura mayor que 5 × la mediana ({m:g})",
            "no_positiva": "con altura no positiva (mediana {m:g})",
        }
        out = []
        for motivo in ("menor", "mayor", "no_positiva"):
            grupo = [f for f in filas if f["motivo"] == motivo]
            if not grupo:
                continue
            n = len(grupo)
            out.append(
                _hallazgo(
                    "textos_fuera_de_escala",
                    "baja",
                    f"{n} textos {frases[motivo].format(m=mediana)} en el espacio {espacio}.",
                    [f["handle"] for f in grupo],
                    _union2d([f["_bbox"] for f in grupo[:50]]),
                    espacio,
                    {"motivo": motivo, "n": n, "mediana": mediana, "handles_truncados": n > 50, "n_handles": n},
                )
            )
        return out

    def _textos_fuera_de_escala(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        espacio, mediana, n, filas = self._alturas(params)
        resumen = {m: sum(1 for f in filas if f["motivo"] == m) for m in ("menor", "mayor", "no_positiva")}
        publicas = [{k: v for k, v in f.items() if not k.startswith("_")} for f in filas]
        page, total, nxt = paginate(publicas, params)
        return self._read(
            {
                "espacio": espacio,
                "mediana": mediana,
                "n_textos": n,
                "resumen": resumen,
                "textos": page,
                "total": total,
                "cursor_siguiente": nxt,
            }
        )

    # auditar_dibujo (§5C.8)
    def _hallazgos_xrefs(self) -> list[dict[str, Any]]:
        out = []
        for x in XREFS:
            if x["estado"] in ("no_encontrada", "sin_cargar"):
                severidad = "alta"
            elif x["estado"] == "descargada":
                severidad = "media"
            else:
                continue
            inserts = self._inserciones(x["nombre"])
            out.append(
                _hallazgo(
                    "xrefs_no_resueltas",
                    severidad,
                    f"La referencia externa '{x['nombre']}' está {x['estado']}.",
                    [e["handle"] for e in inserts],
                    _union2d([e["bbox"] for e in inserts[:50]]),
                    "modelo" if inserts else None,
                    {k: x[k] for k in ("nombre", "estado", "ruta_guardada", "ruta_resuelta")},
                )
            )
        return out

    def _hallazgos_unidades(self) -> list[dict[str, Any]]:
        datos = {"insunits": INSUNITS_DIBUJO, "measurement": 1, "insunits_lps": INSUNITS_LPS}
        if INSUNITS_DIBUJO == 0:
            return [_hallazgo("unidades", "alta", "El dibujo no declara unidades (INSUNITS=0).", [], None, None, datos)]
        if INSUNITS_DIBUJO != INSUNITS_LPS:
            mensaje = f"El dibujo usa INSUNITS={INSUNITS_DIBUJO}; el estándar LPS es INSUNITS={INSUNITS_LPS}."
            return [_hallazgo("unidades", "media", mensaje, [], None, None, datos)]
        return []

    def _auditar_dibujo(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        espacio, _ents = self._space(params)
        pedidas = set(params.get("reglas") or REGLAS_AUDITORIA)
        reglas = [r for r in REGLAS_AUDITORIA if r in pedidas]
        p_espacio = {"espacio": params["espacio"]} if "espacio" in params else {}
        ejecutar = {
            "auditar_capas": self._hallazgos_capas,
            "entidades_en_capa_0": self._hallazgos_capa0,
            "auditar_bloques": lambda: self._bloques_auditoria()[1],
            "verificar_cuadro_areas": lambda: self._cuadros(p_espacio)[3],
            "duplicados": lambda: self._hallazgos_duplicados(p_espacio),
            "textos_fuera_de_escala": lambda: self._hallazgos_textos(p_espacio),
            "xrefs_no_resueltas": self._hallazgos_xrefs,
            "unidades": self._hallazgos_unidades,
        }
        todos: list[dict[str, Any]] = []
        por_regla: dict[str, dict[str, Any]] = {}
        # §5C.8: resumen.n_cuadros_fuera_de_layout (0 si verificar_cuadro_areas no se ejecuta).
        n_fuera = 0
        if "verificar_cuadro_areas" in reglas:
            n_fuera = sum(1 for c_ in self._cuadros(p_espacio)[1] if not c_["en_layout"])
        for regla in reglas:
            hallazgos = _numerar(ejecutar[regla]())
            por_regla[regla] = {
                "n": len(hallazgos),
                **{s: sum(1 for h in hallazgos if h["severidad"] == s) for s in SEVERIDADES},
                "completo": True,
            }
            todos.extend(hallazgos)
        ordenados = _ordenar(todos)
        page, total, nxt = paginate(ordenados, params)
        resumen = {
            "total_hallazgos": len(ordenados),
            "por_severidad": {s: sum(1 for h in ordenados if h["severidad"] == s) for s in SEVERIDADES},
            "por_regla": por_regla,
            "n_cuadros_fuera_de_layout": n_fuera,
        }
        return self._read(
            {
                "espacio": espacio,
                "resumen": resumen,
                "reglas_omitidas": [],
                "reglas_con_error": [],
                "duracion_ms": 12,
                "hallazgos": page,
                "total": total,
                "cursor_siguiente": nxt,
            }
        )

    # marcar_hallazgos (§5C.9, escritura)
    def _check_capa_mcp(self, params: dict[str, Any]) -> str:
        capa = params.get("capa", CAPA_OBSERVACIONES)
        if not isinstance(capa, str) or not capa.casefold().startswith("mcp-"):
            raise _invalid("capa: solo se admiten capas MCP-*")
        return capa

    def _alloc_handle(self) -> str:
        with self.state.lock:
            handle = f"{self.state.next_handle:X}"
            self.state.next_handle += 1
        return handle

    def _marcar_hallazgos(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        marcas = params.get("marcas")
        if not isinstance(marcas, list) or not 1 <= len(marcas) <= 100:
            raise _invalid("marcas: debe tener entre 1 y 100 elementos")
        capa = self._check_capa_mcp(params)
        altura = params.get("altura_texto", 0.2)
        if isinstance(altura, bool) or not isinstance(altura, (int, float)) or not math.isfinite(altura) or altura <= 0:
            raise _invalid("altura_texto: debe ser un número mayor que 0")
        altura = float(altura)
        espacio, entities = self._space(params)
        # Validar todo antes de crear nada (atomicidad §3.6).
        for i, marca in enumerate(marcas):
            texto = marca.get("texto") if isinstance(marca, dict) else None
            if not isinstance(texto, str) or not 1 <= len(texto) <= 500:
                raise _invalid(f"marcas[{i}].texto: debe tener entre 1 y 500 caracteres")
            _check_bbox(marca)
        # §5C.9 (F9, 0.3.2): cada marca debe intersecar la extensión del espacio destino (entidades fuera de capas
        # MCP-*) ampliada un 10 % del lado mayor; espacio sin extensión → no se bloquea; 'forzar' escribe igualmente.
        advertencias_f9: list[str] = []
        extension = _union2d([e.get("bbox") for e in entities if not str(e.get("capa", "")).casefold().startswith("mcp-")])
        if extension is not None:
            (ex0, ey0), (ex1, ey1) = extension
            m = 0.1 * max(ex1 - ex0, ey1 - ey0)
            fuera = []
            for i, marca in enumerate(marcas):
                (bx0, by0), (bx1, by1) = (p[:2] for p in marca["bbox"])
                if bx0 > ex1 + m or bx1 < ex0 - m or by0 > ey1 + m or by1 < ey0 - m:
                    fuera.append(i)
            if fuera:
                ext_txt = f"[[{_fmt3(ex0)}, {_fmt3(ey0)}], [{_fmt3(ex1)}, {_fmt3(ey1)}]]"
                if params.get("forzar") is not True:
                    lista = ", ".join(
                        f"marcas[{i}] [[{_fmt3(marcas[i]['bbox'][0][0])}, {_fmt3(marcas[i]['bbox'][0][1])}], "
                        f"[{_fmt3(marcas[i]['bbox'][1][0])}, {_fmt3(marcas[i]['bbox'][1][1])}]]"
                        for i in fuera[:5]
                    )
                    if len(fuera) > 5:
                        lista += f" y {len(fuera) - 5} más"
                    sugerencia = (
                        "probablemente las coordenadas son de un layout (espacio papel): indique en 'espacio' el layout correcto"
                        if espacio == "modelo"
                        else "probablemente las coordenadas son del modelo: use espacio:\"modelo\" (o el layout correcto)"
                    )
                    raise _invalid(
                        f"{lista} fuera de la extensión del espacio destino '{espacio}' {ext_txt} (ampliada un 10 %): "
                        f"{sugerencia}. No se escribió nada; pase forzar: true para escribir igualmente."
                    )
                advertencias_f9.append(
                    f"forzar: true: se escribieron marcas fuera de la extensión del espacio destino '{espacio}' "
                    f"{ext_txt} (marcas {', '.join(str(i) for i in fuera)})."
                )
        existente = self._find_layer(capa)
        capa_creada = existente is None
        if existente is None:
            existente = _layer(capa, 1, 35, "mcp")
            self.state.capas_creadas.append(existente)
        capa = existente["nombre"]
        creadas: list[dict[str, Any]] = []
        resultado: list[dict[str, Any]] = []
        for marca in marcas:
            vertices, caja = _revision_cloud(marca["bbox"], altura)
            h_nube, h_texto = self._alloc_handle(), self._alloc_handle()
            (rx0, ry0), (rx1, ry1) = caja
            prefijo = f"[{marca['severidad'].upper()}] " if marca.get("severidad") else ""
            x_texto = rx1 + 0.5 * altura
            creadas.append(
                {
                    "handle": h_nube,
                    "tipo": "LWPOLYLINE",
                    "capa": capa,
                    "bbox": [[rx0, ry0, 0.0], [rx1, ry1, 0.0]],
                    "n_vertices": len(vertices),
                    "cerrada": True,
                    "longitud": 2 * ((rx1 - rx0) + (ry1 - ry0)),
                    "area": (rx1 - rx0) * (ry1 - ry0),
                    "tiene_arcos": True,
                    "vertices": [[x, y, 0.0] for x, y in vertices] if len(vertices) <= 50 else None,
                }
            )
            creadas.append(
                {
                    "handle": h_texto,
                    "tipo": "MTEXT",
                    "capa": capa,
                    "bbox": [[x_texto, ry1 - altura, 0.0], [x_texto + 30 * altura, ry1, 0.0]],
                    "texto": prefijo + marca["texto"],
                    "altura": altura,
                    "posicion": [x_texto, ry1, 0.0],
                    "ancho": 30 * altura,
                    "rotacion": 0.0,
                }
            )
            resultado.append({"nube": h_nube, "texto": h_texto, "bbox_nube": caja, "n_vertices": len(vertices)})
        entities.extend(creadas)
        units = self._units()
        units["advertencias"].extend(advertencias_f9)
        return {
            "pid": self.state.pid,
            "marcas": resultado,
            "handles": [e["handle"] for e in creadas],
            "n_marcas": len(resultado),
            "capa": capa,
            "capa_creada": capa_creada,
            "espacio": espacio,
            **units,
        }

    # limpiar_observaciones (§5C.10, escritura)
    def _limpiar_observaciones(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        if params.get("confirmar") is not True:
            raise _invalid("confirmar: debe ser true")
        capa = self._check_capa_mcp(params)
        existente = self._find_layer(capa)
        if existente is None:
            raise _not_found(f"La capa '{capa}' no existe en el dibujo.", capa=capa)
        por_espacio: dict[str, int] = {}
        n_viewports = 0
        for espacio, entities in self._all_spaces():
            en_capa = [e for e in entities if e["capa"].casefold() == capa.casefold()]
            # §5C.10 (desviación aceptada): los VIEWPORT de la capa se conservan y no cuentan.
            viewports = [e for e in en_capa if e["tipo"] == "VIEWPORT"]
            n_viewports += len(viewports)
            borradas = len(en_capa) - len(viewports)
            if borradas:
                por_espacio[espacio] = borradas
            entities[:] = [e for e in entities if e not in en_capa or e["tipo"] == "VIEWPORT"]
        units = self._units()
        if n_viewports:
            units["advertencias"].append(
                f"Se conservaron {n_viewports} viewport(s) de la capa {existente['nombre']}: "
                "limpiar_observaciones no borra viewports."
            )
        return {
            "pid": self.state.pid,
            "capa": existente["nombre"],
            "n_borradas": sum(por_espacio.values()),
            "por_espacio": por_espacio,
            **units,
        }

    # -- Fase 3 (dibujo primitivo, §5D; protocolo 0.4) ------------------------
    # Flujo de cada escritura, como en el plugin: validación de parámetros → R2 (unidades) → R5/R4 (espacio y
    # ubicación, lectura previa) → R3 (capa) → escritura atómica (nada se toca hasta que todo valida) → R6.

    @staticmethod
    def _invalid_data(detalle: str, **extra: Any) -> _RpcError:
        err = _invalid(detalle)
        err.data.update(extra)
        return err

    @staticmethod
    def _numero(params: dict[str, Any], key: str, default: float | None = None) -> float | None:
        v = params.get(key, default)
        if v is None:
            return None
        if isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v):
            raise _invalid(f"{key}: debe ser un número finito")
        return float(v)

    def _positivo(self, params: dict[str, Any], key: str, default: float | None = None) -> float:
        v = params.get(key, default)
        if isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) or v <= 0:
            raise _invalid(f"{key}: debe ser un número mayor que 0")
        return float(v)

    @staticmethod
    def _cadena(params: dict[str, Any], key: str, a: int, b: int, *, requerido: bool = True) -> str | None:
        v = params.get(key)
        if v is None and not requerido:
            return None
        if not isinstance(v, str) or not a <= len(v) <= b:
            raise _invalid(f"{key}: debe tener entre {a} y {b} caracteres")
        return v

    def _r2(self, params: dict[str, Any]) -> list[str]:
        """R2: INSUNITS = 0 → UNITS_UNKNOWN sin dibujar, salvo aceptar_sin_unidades: true."""
        if self.state.insunits != 0:
            return []
        if params.get("aceptar_sin_unidades") is not True:
            rpc_code, message = ERROR_TABLE["UNITS_UNKNOWN"]
            raise _RpcError(rpc_code, message, {"code": "UNITS_UNKNOWN"})
        return [ADVERTENCIA_ACEPTAR_SIN_UNIDADES]

    def _espacio_destino(self, params: dict[str, Any]) -> tuple[str, list[dict[str, Any]]]:
        """R5: 'modelo' o un layout (NOT_FOUND si no existe); por defecto el espacio activo."""
        espacio = params.get("espacio")
        return self._space({"espacio": espacio if espacio is not None else self.state.espacio_activo})

    def _extension(self, espacio: str, entities: list[dict[str, Any]]) -> list[list[float]] | None:
        """Extensión del espacio (§5C.9): entidades fuera de capas MCP-* y sin viewports, más los límites del layout."""
        cajas: list[Any] = [
            e.get("bbox")
            for e in entities
            if not str(e.get("capa", "")).casefold().startswith("mcp-") and e.get("tipo") != "VIEWPORT"
        ]
        if espacio != "modelo" and espacio in self.state.limites_layout:
            cajas.append(self.state.limites_layout[espacio])
        return _union2d(cajas)

    def _r4(
        self,
        espacio: str,
        entities: list[dict[str, Any]],
        cajas: list[list[list[float]] | None],
        forzar: bool,
        etiqueta: str,
    ) -> list[str]:
        """R4: cada caja debe intersecar la extensión ampliada un 10 %; si no, INVALID_PARAMS salvo forzar."""
        extension = self._extension(espacio, entities)
        if extension is None:
            return []
        (ex0, ey0), (ex1, ey1) = extension
        m = 0.1 * max(ex1 - ex0, ey1 - ey0)
        fuera = [
            i
            for i, c in enumerate(cajas)
            if c is not None and (c[0][0] > ex1 + m or c[1][0] < ex0 - m or c[0][1] > ey1 + m or c[1][1] < ey0 - m)
        ]
        if not fuera:
            return []
        ext_txt = _fmt_box(extension)
        if not forzar:
            lista = ", ".join(f"{etiqueta}[{i}] {_fmt_box(cajas[i])}" for i in fuera[:5])  # type: ignore[arg-type]
            if len(fuera) > 5:
                lista += f" y {len(fuera) - 5} más"
            sugerencia = (
                "probablemente las coordenadas son de un layout (espacio papel): indique en 'espacio' el layout correcto"
                if espacio == "modelo"
                else "probablemente las coordenadas son del modelo: use espacio:\"modelo\" (o el layout correcto)"
            )
            raise self._invalid_data(
                f"{lista} fuera de la extensión del espacio destino '{espacio}' {ext_txt} (ampliada un 10 %): "
                f"{sugerencia}. No se escribió nada; pase forzar: true para escribir igualmente.",
                espacio=espacio,
                extension=extension,
                **{f"{etiqueta}_fuera": fuera},
            )
        return [
            f"forzar: true: se escribió fuera de la extensión del espacio destino '{espacio}' {ext_txt} "
            f"({etiqueta} {', '.join(str(i) for i in fuera)})."
        ]

    def _flag(self, conjunto: set[str], nombre: str) -> bool:
        return nombre.casefold() in {c.casefold() for c in conjunto}

    def _plan_capa(self, capa: str | None, concepto_defecto: str | None) -> tuple[str, str, dict[str, Any] | None]:
        """LayerResolver.Resolve (R3): (nombre buscado, origen, especificación para crearla o None)."""
        prefs: dict[str, str] = LPS.get("capas_preferidas") or {}
        if capa is None:
            if concepto_defecto is not None and concepto_defecto in prefs:
                nombre, origen = prefs[concepto_defecto], "defecto"
            else:
                nombre, origen = "MCP-DIBUJO", "defecto"
        else:
            if not capa.strip() or any(ch in CARACTERES_NO_VALIDOS_CAPA for ch in capa):
                raise _invalid(DETALLE_NOMBRE_CAPA_INVALIDO)
            clave = next((k for k in prefs if k.casefold() == capa.casefold()), None)
            if clave is not None:
                nombre, origen = prefs[clave], "concepto"
            else:
                nombre, origen = capa, "nombre"
        return nombre, origen, self._spec_capa(nombre)

    @staticmethod
    def _spec_capa(nombre: str) -> dict[str, Any] | None:
        for seccion in ("capas_permitidas", "capas_mcp"):
            for clave, valor in (LPS.get(seccion) or {}).items():
                if clave.casefold() == nombre.casefold():
                    return {"nombre": clave, **valor}
        if nombre.casefold().startswith("mcp-") and "|" not in nombre:
            return {"nombre": nombre, **MCP_DEFECTO}
        return None

    @staticmethod
    def _nueva_capa(spec: dict[str, Any]) -> tuple[dict[str, Any], list[str]]:
        nombre = spec["nombre"]
        capa = _layer(nombre, int(spec["color"]), int(spec["grosor"]), "mcp" if nombre.casefold().startswith("mcp-") else "estandar")
        capa["plot"] = bool(spec.get("plot", 1))
        advertencias: list[str] = []
        tipo = str(spec.get("tipo_linea", "Continuous"))
        if tipo.casefold() in TIPOS_LINEA_CARGADOS:
            capa["tipo_linea"] = tipo
        else:
            advertencias.append(
                f"La capa {nombre} se creó con tipo de línea Continuous: '{tipo}' no está cargado en el dibujo."
            )
        return capa, advertencias

    def _capa_escritura(
        self, capa_param: str | None, concepto_defecto: str | None
    ) -> tuple[dict[str, Any], bool, list[str]]:
        """DrawLayerFactory.Ensure (R3) sin escribir: (capa, se_creará, advertencias). La creación se confirma al final."""
        nombre, _origen, spec = self._plan_capa(capa_param, concepto_defecto)
        existente = self._find_layer(nombre)
        if existente is None:
            if "|" in nombre:
                raise _invalid(DETALLE_NOMBRE_CAPA_INVALIDO)
            if spec is None:
                raise _not_found(f"La capa '{nombre}' no existe en el dibujo.", capa=nombre, detalle=DETALLE_CAPA_NO_CREABLE)
            nueva, advertencias = self._nueva_capa(spec)
            return nueva, True, advertencias
        real = existente["nombre"]
        if "|" in real:
            raise self._invalid_data("capa dependiente de xref", capa=real)
        if self._flag(self.state.capas_bloqueadas, real):
            raise self._invalid_data("capa bloqueada", capa=real)
        if self._flag(self.state.capas_congeladas, real):
            raise self._invalid_data("capa congelada", capa=real)
        advertencias = []
        if self._flag(self.state.capas_apagadas, real):
            advertencias.append(f"La capa {real} está apagada: las entidades no serán visibles.")
        return existente, False, advertencias

    def _confirmar_escritura(
        self, entities: list[dict[str, Any]], nuevas: list[dict[str, Any]], capa: dict[str, Any], creada: bool
    ) -> None:
        """Commit de la 'transacción': capa (si hay que crearla) y entidades, todo junto."""
        if creada:
            self.state.capas_creadas.append(capa)
        entities.extend(nuevas)

    def _r6(
        self,
        handles: list[str],
        capa: dict[str, Any],
        creada: bool,
        espacio: str,
        propias: list[str],
        **extra: Any,
    ) -> dict[str, Any]:
        """Respuesta común R6 (DrawWrite.Response) + campos propios."""
        units = self._units()
        units["advertencias"].extend(propias)
        return {
            "pid": self.state.pid,
            "handles": handles,
            "capa": capa["nombre"],
            "capa_creada": creada,
            "espacio": espacio,
            **units,
            **extra,
        }

    def _estilo(self, params: dict[str, Any], disponibles: list[str], clase: str) -> str:
        estilo = self._cadena(params, "estilo", 1, 255, requerido=False)
        if estilo is None:
            return disponibles[0]
        real = next((s for s in disponibles if s.casefold() == estilo.casefold()), None)
        if real is None:
            raise _not_found(f"El estilo de {clase} '{estilo}' no existe en el dibujo.", estilo=estilo)
        return real

    # linea (§5.4, adaptada en 0.4: R2, R3, R4, R5)
    def _linea(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        if "lineas" in params:
            lineas = params["lineas"]
        else:
            lineas = [{"inicio": params["inicio"], "fin": params["fin"]}]
        segmentos = []
        for i, item in enumerate(lineas):
            a, b = _p3(item["inicio"]), _p3(item["fin"])
            if math.dist(a, b) <= 1e-9:
                raise _invalid(f"lineas[{i}]: longitud cero")
            segmentos.append((a, b))
        # exigir_unidades se admite y no tiene efecto (obsoleto desde 0.4: R2 ya rechaza INSUNITS=0).
        propias = self._r2(params)
        espacio, entities = self._espacio_destino(params)
        propias += self._r4(espacio, entities, [_box_of([a, b]) for a, b in segmentos], params.get("forzar") is True, "lineas")
        capa, creada, adv_capa = self._capa_escritura(params.get("capa"), None)
        propias += adv_capa
        nuevas = [_line(self._alloc_handle(), capa["nombre"], a, b) for a, b in segmentos]
        self._confirmar_escritura(entities, nuevas, capa, creada)
        units = self._units()
        units["advertencias"].extend(propias)
        return {
            "pid": self.state.pid,
            "entidades": [
                {"handle": e["handle"], "inicio": e["inicio"], "fin": e["fin"], "longitud": e["longitud"]} for e in nuevas
            ],
            "handles": [e["handle"] for e in nuevas],
            "capa": capa["nombre"],
            "capa_creada": creada,
            "espacio": espacio,
            **units,
        }

    # crear_capa (§5D.1)
    def _crear_capa(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        nombre = self._cadena(params, "nombre", 1, 255)
        assert nombre is not None
        buscado, origen, spec = self._plan_capa(nombre, None)
        existente = self._find_layer(buscado)
        advertencias: list[str] = []
        if existente is None:
            if spec is None or "|" in buscado:
                raise self._invalid_data(DETALLE_CREAR_CAPA_NO_ESTANDAR, capa=buscado)
            existente, advertencias = self._nueva_capa(spec)
            self.state.capas_creadas.append(existente)
            creada = True
        else:
            creada = False
        real = existente["nombre"]
        units = self._units()
        units["advertencias"].extend(advertencias)
        return {
            "pid": self.state.pid,
            "capa": real,
            "creada": creada,
            "color": existente["color"],
            "tipo_linea": existente["tipo_linea"],
            "grosor": existente["grosor"],
            "plot": existente.get("plot", True),
            "bloqueada": self._flag(self.state.capas_bloqueadas, real),
            "congelada": self._flag(self.state.capas_congeladas, real),
            "apagada": self._flag(self.state.capas_apagadas, real),
            "origen": "concepto" if origen == "concepto" else "nombre",
            **units,
        }

    # polilinea (§5D.2)
    def _polilinea(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        vertices = params.get("vertices")
        if not isinstance(vertices, list) or not 2 <= len(vertices) <= 1000:
            raise _invalid("vertices: debe tener entre 2 y 1000 elementos")
        cerrada = params.get("cerrada", False) is True
        bulges = params.get("bulges")
        ancho = self._numero(params, "ancho", 0.0) or 0.0
        if ancho < 0:
            raise _invalid("ancho: debe ser un número mayor o igual que 0")
        propias_z = [ADVERTENCIA_Z_IGNORADA] if any(len(v) > 2 and float(v[2]) != 0.0 for v in vertices) else []
        xy = [(float(v[0]), float(v[1])) for v in vertices]
        error = _validar_polilinea(xy, cerrada, bulges)
        if error:
            raise _invalid(error)
        propias = self._r2(params)
        espacio, entities = self._espacio_destino(params)
        caja = _box_of([list(p) for p in xy])
        propias += propias_z + self._r4(espacio, entities, [caja], params.get("forzar") is True, "vertices")
        capa, creada, adv_capa = self._capa_escritura(params.get("capa"), None)
        propias += adv_capa
        longitud, area = _longitud_area(xy, cerrada, bulges)
        handle = self._alloc_handle()
        entidad = {
            "handle": handle,
            "tipo": "LWPOLYLINE",
            "capa": capa["nombre"],
            "bbox": _box3(caja),
            "n_vertices": len(xy),
            "cerrada": cerrada,
            "longitud": longitud,
            "area": area,
            "tiene_arcos": any(abs(float(b)) > 1e-12 for b in (bulges or [])),
            "vertices": [[x, y, 0.0] for x, y in xy] if len(xy) <= 50 else None,
            "_bulges": list(bulges or [0.0] * len(xy)),
            "_ancho": ancho,
        }
        self._confirmar_escritura(entities, [entidad], capa, creada)
        return self._r6(
            [handle],
            capa,
            creada,
            espacio,
            propias,
            entidad={"handle": handle, "n_vertices": len(xy), "cerrada": cerrada, "longitud": longitud, "area": area, "bbox": caja},
        )

    # circulo y arco (§5D.3)
    def _circulo(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        centro = _p3(params["centro"])
        radio = self._positivo(params, "radio")
        propias = self._r2(params)
        espacio, entities = self._espacio_destino(params)
        caja = [[centro[0] - radio, centro[1] - radio], [centro[0] + radio, centro[1] + radio]]
        propias += self._r4(espacio, entities, [caja], params.get("forzar") is True, "entidades")
        capa, creada, adv_capa = self._capa_escritura(params.get("capa"), None)
        propias += adv_capa
        handle = self._alloc_handle()
        longitud, area = 2 * math.pi * radio, math.pi * radio * radio
        entidad = {
            "handle": handle,
            "tipo": "CIRCLE",
            "capa": capa["nombre"],
            "bbox": _box3(caja),
            "centro": centro,
            "radio": radio,
            "longitud": longitud,
            "area": area,
        }
        self._confirmar_escritura(entities, [entidad], capa, creada)
        return self._r6(
            [handle],
            capa,
            creada,
            espacio,
            propias,
            entidad={"handle": handle, "centro": centro, "radio": radio, "longitud": longitud, "area": area},
        )

    def _arco(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        centro = _p3(params["centro"])
        radio = self._positivo(params, "radio")
        inicio = _norm360(self._numero(params, "angulo_inicio") or 0.0)
        fin = _norm360(self._numero(params, "angulo_fin") or 0.0)
        if abs(inicio - fin) <= 1e-12:
            raise _invalid("angulo_fin: debe ser distinto de angulo_inicio")
        propias = self._r2(params)
        espacio, entities = self._espacio_destino(params)
        caja = [[centro[0] - radio, centro[1] - radio], [centro[0] + radio, centro[1] + radio]]
        propias += self._r4(espacio, entities, [caja], params.get("forzar") is True, "entidades")
        capa, creada, adv_capa = self._capa_escritura(params.get("capa"), None)
        propias += adv_capa
        handle = self._alloc_handle()
        barrido = (fin - inicio) % 360.0
        longitud = radio * math.radians(barrido)
        entidad = {
            "handle": handle,
            "tipo": "ARC",
            "capa": capa["nombre"],
            "bbox": _box3(caja),
            "centro": centro,
            "radio": radio,
            "angulo_inicio": inicio,
            "angulo_fin": fin,
            "longitud": longitud,
        }
        self._confirmar_escritura(entities, [entidad], capa, creada)
        return self._r6(
            [handle],
            capa,
            creada,
            espacio,
            propias,
            entidad={"handle": handle, "centro": centro, "radio": radio, "angulo_inicio": inicio, "angulo_fin": fin, "longitud": longitud},
        )

    # texto y mtext (§5D.4)
    def _texto(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        posicion = _p3(params["posicion"])
        texto = self._cadena(params, "texto", 1, 2000)
        assert texto is not None
        altura = self._positivo(params, "altura")
        rotacion = _norm360(self._numero(params, "rotacion", 0.0) or 0.0)
        justificacion = params.get("justificacion", "izquierda")
        if justificacion not in JUSTIFICACIONES:
            raise _invalid(f"justificacion: debe ser uno de {', '.join(JUSTIFICACIONES)}")
        estilo = self._estilo(params, self.state.estilos_texto, "texto")
        propias_saltos: list[str] = []
        if "\n" in texto or "\r" in texto:
            texto = texto.replace("\r\n", " ").replace("\r", " ").replace("\n", " ")
            propias_saltos.append(ADVERTENCIA_SALTOS_TEXTO)
        propias = self._r2(params)
        espacio, entities = self._espacio_destino(params)
        propias += propias_saltos + self._r4(
            espacio, entities, [[posicion[:2], posicion[:2]]], params.get("forzar") is True, "entidades"
        )
        capa, creada, adv_capa = self._capa_escritura(params.get("capa"), None)
        propias += adv_capa
        handle = self._alloc_handle()
        caja = [[posicion[0], posicion[1]], [posicion[0] + 0.8 * altura * len(texto), posicion[1] + altura]]
        entidad = {
            "handle": handle,
            "tipo": "TEXT",
            "capa": capa["nombre"],
            "bbox": _box3(caja),
            "texto": texto,
            "altura": altura,
            "posicion": posicion,
            "rotacion": rotacion,
            # Privado: TextString guardado (R7: %% neutralizados).
            "_contenido": _escape_dbtext(texto),
        }
        self._confirmar_escritura(entities, [entidad], capa, creada)
        return self._r6(
            [handle],
            capa,
            creada,
            espacio,
            propias,
            entidad={
                "handle": handle,
                "posicion": posicion,
                "altura": altura,
                "rotacion": rotacion,
                "justificacion": justificacion,
                "estilo": estilo,
                "bbox": caja,
            },
        )

    def _mtext(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        posicion = _p3(params["posicion"])
        texto = self._cadena(params, "texto", 1, 2000)
        assert texto is not None
        altura = self._positivo(params, "altura")
        ancho = self._numero(params, "ancho", 0.0) or 0.0
        if ancho < 0:
            raise _invalid("ancho: debe ser un número mayor o igual que 0")
        rotacion = _norm360(self._numero(params, "rotacion", 0.0) or 0.0)
        adjunto = params.get("adjunto", "superior_izquierda")
        if adjunto not in ADJUNTOS:
            raise _invalid(f"adjunto: debe ser uno de {', '.join(ADJUNTOS)}")
        estilo = self._estilo(params, self.state.estilos_texto, "texto")
        propias = self._r2(params)
        espacio, entities = self._espacio_destino(params)
        propias += self._r4(espacio, entities, [[posicion[:2], posicion[:2]]], params.get("forzar") is True, "entidades")
        capa, creada, adv_capa = self._capa_escritura(params.get("capa"), None)
        propias += adv_capa
        handle = self._alloc_handle()
        lineas = texto.splitlines() or [texto]
        w = ancho if ancho > 0 else 0.8 * altura * max(len(l) for l in lineas)
        caja = [[posicion[0], posicion[1] - altura * 1.5 * len(lineas)], [posicion[0] + w, posicion[1]]]
        entidad = {
            "handle": handle,
            "tipo": "MTEXT",
            "capa": capa["nombre"],
            "bbox": _box3(caja),
            "texto": texto,
            "altura": altura,
            "posicion": posicion,
            "ancho": ancho,
            "rotacion": rotacion,
            # Privado: Contents guardado (R7: MTextEscape).
            "_contenido": _escape_mtext(texto),
        }
        self._confirmar_escritura(entities, [entidad], capa, creada)
        return self._r6(
            [handle],
            capa,
            creada,
            espacio,
            propias,
            entidad={
                "handle": handle,
                "posicion": posicion,
                "altura": altura,
                "ancho": ancho,
                "rotacion": rotacion,
                "adjunto": adjunto,
                "estilo": estilo,
                "bbox": caja,
            },
        )

    # insertar_bloque (§5D.5)
    @staticmethod
    def _escala_bloque(valor: Any) -> list[float]:
        def valida(c: Any) -> bool:
            return not isinstance(c, bool) and isinstance(c, (int, float)) and math.isfinite(c) and c != 0

        if valor is None:
            return [1.0, 1.0, 1.0]
        if valida(valor):
            return [float(valor)] * 3
        if isinstance(valor, list) and len(valor) in (2, 3) and all(valida(c) for c in valor):
            return [float(valor[0]), float(valor[1]), float(valor[2] if len(valor) == 3 else valor[0])]
        raise _invalid(DETALLE_ESCALA_BLOQUE)

    def _insertar_bloque(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        nombre = self._cadena(params, "nombre", 1, 255)
        assert nombre is not None
        if any(s in nombre for s in ("\\", "/", ":", "..")) or re.search(r"\.[A-Za-z0-9]{1,4}$", nombre):
            raise _invalid(DETALLE_BLOQUE_CON_RUTA)
        if nombre.startswith("*"):
            raise _invalid("nombre: no se insertan bloques anónimos")
        posicion = _p3(params["posicion"])
        escala = self._escala_bloque(params.get("escala"))
        rotacion = _norm360(self._numero(params, "rotacion", 0.0) or 0.0)
        modo = params.get("escala_unidades", "auto")
        if modo not in ("auto", "ninguna"):
            raise _invalid("escala_unidades: debe ser uno de auto, ninguna")
        atributos_in = params.get("atributos") or {}
        vistos: dict[str, str] = {}
        for tag, valor in atributos_in.items():
            if tag.casefold() in vistos:
                raise _invalid(f"atributos: etiqueta repetida '{tag}'")
            vistos[tag.casefold()] = tag
            if not isinstance(valor, str) or len(valor) > 500:
                raise _invalid(f"atributos.{tag}: debe tener entre 0 y 500 caracteres")
        propiedades_pedidas = self._leer_propiedades_dinamicas(params)
        propias = self._r2(params)
        espacio, entities = self._espacio_destino(params)
        propias += self._r4(espacio, entities, [[posicion[:2], posicion[:2]]], params.get("forzar") is True, "entidades")
        # Resolución de la definición: dibujo → biblioteca (solo con desde_biblioteca).
        definicion = next((d for d in self.state.block_defs if d["nombre"].casefold() == nombre.casefold()), None)
        importar: dict[str, Any] | None = None
        if definicion is not None:
            real = definicion["nombre"]
            unidades_bloque = int(definicion.get("unidades_bloque", 0))
            attdefs = self.state.atributos_bloques.get(real.upper(), [])
            tamano = TAMANO_BLOQUES_DIBUJO.get(real.upper(), [1.0, 1.0])
            dinamico = bool(definicion.get("dinamico"))
            props_base = self.state.propiedades_bloques.get(real.upper(), [])
        elif params.get("desde_biblioteca") is not True:
            raise _not_found(
                f"El bloque '{nombre}' no existe en el dibujo.",
                bloque=nombre,
                detalle="pase desde_biblioteca: true si el bloque está en la biblioteca",
            )
        else:
            importar = next((b for k, b in self.state.biblioteca.items() if k.casefold() == nombre.casefold()), None)
            if importar is None:
                raise _not_found(
                    f"El bloque '{nombre}' no está en el dibujo ni en la biblioteca ({BIBLIOTECA_CARPETA}).",
                    bloque=nombre,
                    archivo=f"{BIBLIOTECA_CARPETA}\\{nombre}.dwg",
                )
            real = importar["nombre"]
            unidades_bloque = int(importar["unidades_bloque"])
            attdefs = list(importar["atributos"])
            tamano = list(importar["tamano"])
            dinamico = bool(importar.get("dinamico"))
            props_base = importar.get("propiedades", [])
        tags = [t for t, _m, _v in attdefs]
        for clave, tag in vistos.items():
            if clave not in {t.casefold() for t in tags}:
                raise self._invalid_data(
                    f"atributos: etiqueta desconocida '{tag}' (etiquetas del bloque: {', '.join(tags)})", etiquetas=tags
                )
        capa, creada, adv_capa = self._capa_escritura(params.get("capa"), None)
        # Propiedades dinámicas (§5G.6): en el plugin un rechazo descarta la transacción tras crear la referencia.
        propiedades = self._aplicar_dinamicas(real, dinamico, props_base, propiedades_pedidas)
        # Unidades (escala_unidades): factor = UnitsScale.Factor(unidades_bloque, INSUNITS del dibujo).
        ins = self.state.insunits
        factor = _factor_unidades(unidades_bloque, ins)
        propias_unidades: list[str] = []
        if factor is None:
            factor_informado = 1.0
            propias_unidades.append(
                f"No se aplicó factor de unidades: el bloque (INSUNITS={unidades_bloque}) o el dibujo (INSUNITS={ins}) "
                "no declaran unidades con equivalencia métrica."
            )
        else:
            factor_informado = factor
        if modo == "auto" and factor is not None and abs(factor - 1.0) > 1e-9:
            aplicada = [s * factor for s in escala]
            propias_unidades.append(
                f"Se aplicó el factor de unidades {_fmt_g10(factor)} (bloque en {UNIDADES[unidades_bloque][0]}, "
                f"dibujo en {UNIDADES[ins][0]}): escala final {', '.join(_fmt_g10(s) for s in aplicada)}. Si la "
                "geometría del bloque ya está en las unidades del dibujo (metadato de unidades incorrecto, ver "
                "auditar_bloques), repita con escala_unidades: \"ninguna\"."
            )
        else:
            aplicada = list(escala)
        # Atributos (orden de la definición): valor dado o por defecto; PLANO_N conserva el formato en línea.
        handle = self._alloc_handle()
        atributos_out: dict[str, str] = {}
        attribs: list[dict[str, Any]] = []
        propias_plano: list[str] = []
        for tag, multilinea, defecto in attdefs:
            dado = vistos.get(tag.casefold())
            if multilinea and PLANO_N_RE.match(defecto):
                if dado is None:
                    contenido = defecto
                else:
                    numero = _plano_n_numero(atributos_in[dado])
                    sustituido = _plano_n_sustituir(defecto, numero)
                    if sustituido is None:
                        contenido = _escape_mtext(atributos_in[dado])
                        propias_plano.append(ADVERTENCIA_PLANO_N)
                    else:
                        contenido = sustituido
                m = PLANO_N_RE.match(contenido)
                visible = (m.group("num").replace("\\\\", "\\") + " de") if m else atributos_in.get(dado or "", "")
            else:
                visible = atributos_in[dado] if dado is not None else defecto
                contenido = _escape_mtext(visible) if multilinea else _escape_dbtext(visible)
            atributos_out[tag] = visible
            attrib = _attrib(self._alloc_handle(), tag, visible, list(posicion))
            attrib["_contenido"] = contenido
            attribs.append(attrib)
        sx, sy = tamano[0] * abs(aplicada[0]), tamano[1] * abs(aplicada[1])
        caja = [[posicion[0], posicion[1]], [posicion[0] + sx, posicion[1] + sy]]
        insert = _insert(handle, capa["nombre"], real, posicion, rotacion, _box3(caja), attribs)
        insert["escala"] = aplicada
        insert["dinamico"] = dinamico
        insert["_propiedades"] = propiedades
        # Commit: definición importada + capas que trae + capa destino + inserción + atributos, juntos (un CTRL+Z).
        capas_importadas: list[str] = []
        if importar is not None:
            capas_importadas = self._importar_definicion(importar)
        definicion_final = next(d for d in self.state.block_defs if d["nombre"] == real)
        definicion_final["n_inserciones"] = int(definicion_final.get("n_inserciones", 0)) + 1
        self._confirmar_escritura(entities, [insert], capa, creada)
        extra: dict[str, Any] = {
            "entidad": {
                "handle": handle,
                "bloque": real,
                "posicion": posicion,
                "escala": aplicada,
                "rotacion": rotacion,
                "n_atributos": len(attribs),
                "bbox": caja,
            },
            "atributos": atributos_out,
            "handles_atributos": [a["handle"] for a in attribs],
            "importado_de_biblioteca": importar is not None,
            "dinamico": dinamico,
            "propiedades_dinamicas": copy.deepcopy(propiedades),
        }
        if importar is not None:
            extra["archivo"] = f"{BIBLIOTECA_CARPETA}\\{real}.dwg"
            extra["capas_importadas"] = capas_importadas
        extra.update(
            {
                "unidades_bloque": unidades_bloque,
                "factor_unidades": factor_informado,
                "escala_aplicada": aplicada,
                "escala_unidades": modo,
                "origen_texto": ORIGEN_TEXTO,
            }
        )
        return self._r6([handle], capa, creada, espacio, propias + adv_capa + propias_unidades + propias_plano, **extra)

    def _importar_definicion(self, importar: dict[str, Any]) -> list[str]:
        """Database.Insert de la biblioteca: definición + capas que no existían. Devuelve esas capas."""
        capas_importadas: list[str] = []
        for nombre_capa in importar["capas"]:
            if self._find_layer(nombre_capa) is None:
                spec = self._spec_capa(nombre_capa) or {"nombre": nombre_capa, **MCP_DEFECTO}
                nueva, _adv = self._nueva_capa(spec)
                self.state.capas_creadas.append(nueva)
                capas_importadas.append(nueva["nombre"])
        real = importar["nombre"]
        attdefs = list(importar["atributos"])
        tags = [t for t, _m, _v in attdefs]
        self.state.block_defs.append(
            {
                "nombre": real,
                "n_inserciones": 0,
                "tags": tags,
                "tiene_atributos": bool(tags),
                "n_entidades": importar["n_entidades"],
                "es_xref": False,
                "anonimo": False,
                "dinamico": bool(importar.get("dinamico")),
                "unidades_bloque": int(importar["unidades_bloque"]),
                "comentario": "",
            }
        )
        self.state.atributos_bloques[real.upper()] = attdefs
        if importar.get("dinamico"):
            self.state.propiedades_bloques[real.upper()] = copy.deepcopy(importar.get("propiedades", []))
        return capas_importadas

    @staticmethod
    def _leer_propiedades_dinamicas(params: dict[str, Any]) -> dict[str, Any] | None:
        """ReadDynamicProperties del plugin: forma de propiedades_dinamicas, al leer los parámetros y antes de mirar el dibujo."""
        if "propiedades_dinamicas" not in params:
            return None
        forma = "propiedades_dinamicas: debe ser un objeto con 1 a 20 propiedades"
        valores = params["propiedades_dinamicas"]
        if not isinstance(valores, dict):
            raise _invalid(forma)
        vistos: set[str] = set()
        for nombre, valor in valores.items():
            if not nombre.strip():
                raise _invalid("propiedades_dinamicas: propiedad con nombre vacío")
            if nombre.casefold() in vistos:
                raise _invalid(f"propiedades_dinamicas: propiedad repetida '{nombre}'")
            vistos.add(nombre.casefold())
            if not isinstance(valor, str) and not _es_numero(valor):
                raise _invalid(f"propiedades_dinamicas.{nombre}: debe ser un número o una cadena")
        if not 1 <= len(valores) <= 20:
            raise _invalid(forma)
        return valores

    def _aplicar_dinamicas(
        self, bloque: str, dinamico: bool, base: list[dict[str, Any]], valores: dict[str, Any] | None
    ) -> list[dict[str, Any]]:
        """DynamicBlockSupport.Apply + Read (§5G.6), sobre la referencia ya creada: en vano solo al dibujar."""
        props = copy.deepcopy(base) if dinamico else []
        if valores is None:
            return props
        if not dinamico:
            raise _invalid(detalle_no_dinamico(bloque))
        nombres = [p["nombre"] for p in props]
        for clave, valor in valores.items():
            prop = next((p for p in props if p["nombre"].casefold() == clave.casefold()), None)
            if prop is None:
                raise self._invalid_data(detalle_propiedad_desconocida(clave, nombres), propiedades=nombres)
            real = prop["nombre"]
            if prop["solo_lectura"]:
                raise self._invalid_data(f"propiedades_dinamicas.{real}: es de solo lectura", propiedad=real)
            # DynamicBlockSupport.ToWritable: decide el tipo del valor actual, no las unidades.
            if isinstance(prop["valor"], str) and not isinstance(valor, str):
                raise self._invalid_data(f"propiedades_dinamicas.{real}: debe ser una cadena", propiedad=real)
            if _es_numero(prop["valor"]) and isinstance(valor, str):
                raise self._invalid_data(f"propiedades_dinamicas.{real}: debe ser un número", propiedad=real)
            permitidos = prop["valores_permitidos"]
            if permitidos is not None and valor not in permitidos:
                raise self._invalid_data(
                    f"propiedades_dinamicas.{real}: el valor {valor} no está entre los admitidos "
                    f"({', '.join(str(v) for v in permitidos)})",
                    propiedad=real,
                )
            if prop["unidades"] != "ninguna" and _es_numero(valor) and valor <= 0:
                # DynamicBlockReferenceProperty.Value lanza; el simulador lo reproduce con distancias no positivas.
                raise self._invalid_data(
                    f"propiedades_dinamicas.{real}: AutoCAD rechazó el valor (eInvalidInput)", propiedad=real
                )
            prop["valor"] = float(valor) if _es_numero(valor) else valor
        return props

    # cota (§5D.6)
    def _cota(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        tipo = params.get("tipo")
        if tipo not in ("lineal", "alineada"):
            raise _invalid("tipo: debe ser uno de lineal, alineada")
        p1, p2, pl = _p3(params["p1"]), _p3(params["p2"]), _p3(params["posicion_linea"])
        if math.dist(p1, p2) <= 1e-9:
            raise _invalid("p2: coincide con p1")
        if "rotacion" in params and tipo == "alineada":
            raise _invalid("rotacion: solo aplica a tipo lineal")
        rot = math.radians(self._numero(params, "rotacion", 0.0) or 0.0)
        estilo = self._estilo(params, self.state.estilos_cota, "cota")
        sustituto = self._cadena(params, "texto_sustituto", 1, 500, requerido=False)
        propias = self._r2(params)
        espacio, entities = self._espacio_destino(params)
        caja = _box_of([p1, p2, pl])
        propias += self._r4(espacio, entities, [caja], params.get("forzar") is True, "entidades")
        capa, creada, adv_capa = self._capa_escritura(params.get("capa"), "cotas")
        propias += adv_capa
        dx, dy = p2[0] - p1[0], p2[1] - p1[1]
        medida = abs(dx * math.cos(rot) + dy * math.sin(rot)) if tipo == "lineal" else math.hypot(dx, dy)
        texto = _escape_dbtext(sustituto) if sustituto is not None else ""
        handle = self._alloc_handle()
        entidad = {
            "handle": handle,
            "tipo": "DIMENSION",
            "capa": capa["nombre"],
            "bbox": _box3(caja),
            "subtipo": "RotatedDimension" if tipo == "lineal" else "AlignedDimension",
            "medida": medida,
            "texto": texto,
            "p1": p1,
            "p2": p2,
            "posicion_linea": pl,
        }
        self._confirmar_escritura(entities, [entidad], capa, creada)
        return self._r6(
            [handle],
            capa,
            creada,
            espacio,
            propias,
            entidad={
                "handle": handle,
                "tipo": tipo,
                "medida": medida,
                "estilo": estilo,
                "texto": texto,
                "p1": p1,
                "p2": p2,
                "posicion_linea": pl,
                "bbox": caja,
            },
        )

    # hatch (§5D.7)
    @staticmethod
    def _contorno_cerrado(e: dict[str, Any]) -> bool:
        tipo = e["tipo"]
        if tipo in ("CIRCLE",):
            return True
        if tipo == "ELLIPSE":
            return bool(e.get("cerrada", True))
        if tipo in ("LWPOLYLINE", "POLYLINE"):
            if e.get("cerrada"):
                return True
            v = e.get("vertices") or []
            return len(v) >= 3 and math.dist(v[0][:2], v[-1][:2]) <= 1e-9
        return False

    def _hatch(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        contorno = params.get("contorno")
        if not isinstance(contorno, dict) or ("handles" in contorno) == ("vertices" in contorno):
            raise _invalid("contorno: indique handles o vertices (solo uno)")
        patron = self._cadena(params, "patron", 1, 255)
        assert patron is not None
        escala = self._positivo(params, "escala", 1.0)
        angulo = _norm360(self._numero(params, "angulo", 0.0) or 0.0)
        xy: list[tuple[float, float]] = []
        if "vertices" in contorno:
            xy = [(float(v[0]), float(v[1])) for v in contorno["vertices"]]
            error = _validar_polilinea(xy, True, None, "contorno.vertices")
            if error:
                raise _invalid(error)
        propias = self._r2(params)
        espacio, entities = self._espacio_destino(params)
        bordes: list[dict[str, Any]] = []
        if "handles" in contorno:
            for i, h in enumerate(contorno["handles"]):
                loc = self._find_entity(h)
                if loc is None:
                    raise _not_found(f"No existe ninguna entidad con handle '{h}'.", handle=h)
                esp, e = loc
                if esp != espacio:
                    raise _invalid(f"contorno.handles[{i}]: la entidad no está en el espacio '{espacio}'")
                if not self._contorno_cerrado(e):
                    raise _invalid(f"contorno.handles[{i}]: la entidad no es un contorno cerrado ({e['tipo']})")
                bordes.append(e)
            caja = _union2d([e.get("bbox") for e in bordes])
            area: float | None = sum(float(e.get("area") or 0.0) for e in bordes) if bordes else None
        else:
            caja = _box_of([list(p) for p in xy])
            area = _longitud_area(xy, True, None)[1]
        propias += self._r4(
            espacio, entities, [caja], params.get("forzar") is True, "vertices" if "vertices" in contorno else "entidades"
        )
        if patron.upper() not in PATRONES_SOMBREADO:
            raise _not_found(f"El patrón de sombreado '{patron}' no existe.", patron=patron)
        capa, creada, adv_capa = self._capa_escritura(params.get("capa"), "achurados")
        propias += adv_capa
        handle = self._alloc_handle()
        asociativo = "handles" in contorno
        entidad = {
            "handle": handle,
            "tipo": "HATCH",
            "capa": capa["nombre"],
            "bbox": _box3(caja) if caja else None,
            "patron": patron.upper(),
            "area": area,
            "asociativo": asociativo,
        }
        self._confirmar_escritura(entities, [entidad], capa, creada)
        return self._r6(
            [handle],
            capa,
            creada,
            espacio,
            propias,
            entidad={
                "handle": handle,
                "patron": patron.upper(),
                "escala": escala,
                "angulo": angulo,
                "area": area,
                "n_bucles": len(bordes) if asociativo else 1,
                "asociativo": asociativo,
                "contorno_handles": [e["handle"] for e in bordes] if asociativo else None,
                "bbox": caja,
            },
        )

    # mover / copiar / rotar / escalar / borrar (§5D.8)
    def _localizar(self, handle: str) -> tuple[str, list[dict[str, Any]], dict[str, Any], bool] | None:
        """(espacio, lista del espacio, entidad, es_atributo) o None."""
        wanted = handle.upper()
        for espacio, entities in self._all_spaces():
            for entity in entities:
                if entity["handle"] == wanted:
                    return espacio, entities, entity, False
                for attrib in entity.get("_atributos", []):
                    if attrib["handle"] == wanted:
                        return espacio, entities, attrib, True
        return None

    def _modificar(self, params: dict[str, Any], metodo: str) -> dict[str, Any]:
        handles = params.get("handles")
        if not isinstance(handles, list) or not 1 <= len(handles) <= 500:
            raise _invalid("handles: debe tener entre 1 y 500 elementos")
        vistos: set[str] = set()
        for i, h in enumerate(handles):
            if not isinstance(h, str) or not re.fullmatch(r"[0-9A-Fa-f]{1,16}", h):
                raise _invalid(f"handles[{i}]: debe ser hexadecimal de 1 a 16 caracteres")
            if h.upper() in vistos:
                raise _invalid(f"handles[{i}]: handle repetido")
            vistos.add(h.upper())
        # Parámetros propios (Parse, antes de R2).
        extra: dict[str, Any] = {}
        fn: Any = None
        giro, factor = 0.0, 1.0
        if metodo in ("mover", "copiar"):
            d = _p3(params["desplazamiento"])
            if metodo == "mover" and all(abs(c) <= 0.0 for c in d):
                raise _invalid("desplazamiento: no puede ser cero")
            extra["desplazamiento"] = d

            def traslacion(p: list[float], d: list[float] = d) -> list[float]:
                q = _p3(p)
                return [q[0] + d[0], q[1] + d[1], q[2] + d[2]]

            fn = traslacion
        elif metodo == "rotar":
            base = _p3(params["base"])
            angulo = self._numero(params, "angulo") or 0.0
            if abs(_norm360(angulo)) <= 1e-12:
                raise _invalid("angulo: no puede ser 0")
            giro = angulo
            c, s = math.cos(math.radians(angulo)), math.sin(math.radians(angulo))
            extra.update({"base": base, "angulo": angulo})

            def rotacion(p: list[float], b: list[float] = base, c: float = c, s: float = s) -> list[float]:
                q = _p3(p)
                x, y = q[0] - b[0], q[1] - b[1]
                return [b[0] + c * x - s * y, b[1] + s * x + c * y, q[2]]

            fn = rotacion
        elif metodo == "escalar":
            base = _p3(params["base"])
            factor = self._positivo(params, "factor")
            if abs(factor - 1.0) <= 1e-12:
                raise _invalid("factor: no puede ser 1")
            extra.update({"base": base, "factor": factor})

            def escalado(p: list[float], b: list[float] = base, f: float = factor) -> list[float]:
                q = _p3(p)
                return [b[0] + f * (q[0] - b[0]), b[1] + f * (q[1] - b[1]), b[2] + f * (q[2] - b[2])]

            fn = escalado
        propias = self._r2(params)
        # 1–5. Existencia, tipo, capa, mismo espacio y protección (compartido con array_*: _proteger_handles).
        encontrados, usuario, n_ocultas = self._proteger_handles(handles, params.get("confirmar") is True)
        espacio0 = encontrados[0][0]
        if n_ocultas:
            propias.append(f"{n_ocultas} entidad(es) en capas congeladas o apagadas: el cambio no será visible.")
        # 6. R4 con las cajas transformadas (salvo borrar).
        lista_espacio = encontrados[0][1]
        if metodo != "borrar":
            cajas = [_transform_box(e.get("bbox"), fn) for _esp, _lst, e, _a in encontrados]
            propias += self._r4(espacio0, lista_espacio, cajas, params.get("forzar") is True, "entidades")
        # Escritura (todo o nada: ya validado).
        nuevos: list[str] = []
        if metodo == "borrar":
            borrar = {e["handle"] for _esp, _lst, e, _a in encontrados}
            lista_espacio[:] = [e for e in lista_espacio if e["handle"] not in borrar]
        elif metodo == "copiar":
            for _esp, _lst, e, _a in encontrados:
                copia = copy.deepcopy(e)
                copia["handle"] = self._alloc_handle()
                for attrib in copia.get("_atributos", []):
                    attrib["handle"] = self._alloc_handle()
                _transformar_entidad(copia, fn)
                lista_espacio.append(copia)
                nuevos.append(copia["handle"])
        else:
            for _esp, _lst, e, _a in encontrados:
                _transformar_entidad(e, fn, giro, factor)
        capas: list[str] = []
        for _esp, _lst, e, _a in encontrados:
            if e["capa"] not in capas:
                capas.append(e["capa"])
        units = self._units()
        units["advertencias"].extend(propias)
        result: dict[str, Any] = {
            "pid": self.state.pid,
            "handles": nuevos if metodo == "copiar" else [e["handle"] for _esp, _lst, e, _a in encontrados],
            "n": len(encontrados),
            "n_usuario": len(usuario),
            "espacio": espacio0,
            "capas": capas,
            **units,
            **extra,
        }
        if metodo == "copiar":
            result["handles_origen"] = [e["handle"] for _esp, _lst, e, _a in encontrados]
        return result

    def _mover(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        return self._modificar(params, "mover")

    def _copiar(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        return self._modificar(params, "copiar")

    def _rotar(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        return self._modificar(params, "rotar")

    def _escalar(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        return self._modificar(params, "escalar")

    def _borrar(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        return self._modificar(params, "borrar")

    # -- Fase 4A (geometría paramétrica, §5E; protocolo 0.5) --------------------
    # Flujo de las generadoras como GeneratorBase: propiedades (additionalProperties: false) → parámetros propios →
    # G2 estricta (INSUNITS = 0 → UNITS_UNKNOWN, sin aceptar_sin_unidades) → resolución de PuntoRef → plan →
    # huérfanas de MCP-PREVIEW → R4 → sin preview: dibujo definitivo; con preview: registro (+ dibujo en MCP-PREVIEW).
    # Simula la FORMA de las respuestas (G6) y los errores del contrato; la geometría es una aproximación.

    def _check_props(self, nombre: str, params: dict[str, Any]) -> None:
        """ParamReader.RequireObject + obligatorios: "<p>: propiedad no permitida" / "<p>: obligatorio"."""
        schema = SCHEMAS_FASE4A[nombre] if nombre in SCHEMAS_FASE4A else SCHEMAS_FASE4B[nombre]
        for clave in params:
            if clave not in schema["properties"]:
                raise _invalid(f"{clave}: propiedad no permitida")
        for clave in schema.get("required", []):
            if clave not in params:
                raise _invalid(f"{clave}: obligatorio")

    @staticmethod
    def _bool(params: dict[str, Any], key: str, default: bool) -> bool:
        v = params.get(key, default)
        if not isinstance(v, bool):
            raise _invalid(f"{key}: debe ser booleano")
        return v

    @staticmethod
    def _entero(params: dict[str, Any], key: str, a: int, b: int, default: int | None = None) -> int:
        v = params.get(key, default)
        if not _es_numero(v) or math.floor(v) != v or not a <= v <= b:
            raise _invalid(f"{key}: debe ser un entero entre {a} y {b}")
        return int(v)

    def _leer_handles(self, params: dict[str, Any], maximo: int) -> list[str]:
        handles = params.get("handles")
        if not isinstance(handles, list) or not 1 <= len(handles) <= maximo:
            raise _invalid(f"handles: debe ser un array de 1 a {maximo} handles")
        vistos: set[str] = set()
        for i, h in enumerate(handles):
            if not isinstance(h, str) or not re.fullmatch(r"[0-9A-Fa-f]{1,16}", h):
                raise _invalid(f"handles[{i}]: formato inválido")
            if h.upper() in vistos:
                raise _invalid(f"handles[{i}]: handle repetido")
            vistos.add(h.upper())
        return [h.upper() for h in handles]

    @staticmethod
    def _leer_handle(params: dict[str, Any], key: str) -> str:
        v = params.get(key)
        if not isinstance(v, str) or not re.fullmatch(r"[0-9A-Fa-f]{1,16}", v):
            raise _invalid(f"{key}: formato inválido")
        return v.upper()

    def _entidad_ref(self, handle: str) -> dict[str, Any]:
        loc = self._find_entity(handle)
        if loc is None:
            raise _not_found(f"No existe ninguna entidad con handle '{handle}'.", handle=handle)
        return loc[1]

    # -- PuntoRef (§5E.1) --

    @staticmethod
    def _vertices_de(e: dict[str, Any]) -> list[list[float]] | None:
        if e["tipo"] == "LINE":
            return [_p3(e["inicio"]), _p3(e["fin"])]
        if e["tipo"] in ("LWPOLYLINE", "POLYLINE") and isinstance(e.get("vertices"), list):
            return [_p3(v) for v in e["vertices"]]
        return None

    @classmethod
    def _curva(cls, e: dict[str, Any]) -> dict[str, list[float]] | None:
        """inicio / fin / medio de una curva del dibujo ficticio, o None si no es curva."""
        tipo = e["tipo"]
        if tipo in ("CIRCLE", "ARC"):
            c, r = _p3(e["centro"]), float(e["radio"])
            a0 = math.radians(e.get("angulo_inicio", 0.0)) if tipo == "ARC" else 0.0
            barrido = math.radians((e["angulo_fin"] - e["angulo_inicio"]) % 360.0) if tipo == "ARC" else 2 * math.pi

            def en(a: float) -> list[float]:
                return [c[0] + r * math.cos(a), c[1] + r * math.sin(a), c[2]]

            return {"inicio": en(a0), "fin": en(a0 + barrido), "medio": en(a0 + barrido / 2)}
        verts = cls._vertices_de(e)
        if verts is None:
            return None
        cerrada = bool(e.get("cerrada")) and tipo != "LINE"
        pts = verts + [verts[0]] if cerrada else verts
        total = sum(math.dist(pts[i][:2], pts[i + 1][:2]) for i in range(len(pts) - 1))
        mitad, acumulado = total / 2, 0.0
        medio = pts[0]
        for i in range(len(pts) - 1):
            largo = math.dist(pts[i][:2], pts[i + 1][:2])
            if acumulado + largo >= mitad - 1e-12 and largo > 0:
                t = (mitad - acumulado) / largo
                medio = [pts[i][k] + t * (pts[i + 1][k] - pts[i][k]) for k in range(3)]
                break
            acumulado += largo
        return {"inicio": verts[0], "fin": verts[0] if cerrada else verts[-1], "medio": medio}

    @classmethod
    def _primitivas(cls, e: dict[str, Any], extender: bool) -> list[tuple[Any, ...]] | None:
        """Curva como primitivas: ("L", a, b, infinita) y ("C", centro, radio). None si no es curva."""
        tipo = e["tipo"]
        if tipo in ("CIRCLE", "ARC"):
            return [("C", _p3(e["centro"])[:2], float(e["radio"]))]
        if tipo == "LINE":
            return [("L", tuple(_p3(e["inicio"])[:2]), tuple(_p3(e["fin"])[:2]), extender)]
        verts = cls._vertices_de(e)
        if verts is None:
            return None
        xy = [tuple(v[:2]) for v in verts]
        n = len(xy)
        m = n if e.get("cerrada") else n - 1
        return [("L", xy[i], xy[(i + 1) % n], False) for i in range(m)]

    @staticmethod
    def _cortes(p: tuple[Any, ...], q: tuple[Any, ...]) -> list[tuple[float, float]]:
        """Intersecciones de dos primitivas (rectas/segmentos y circunferencias)."""
        tol = 1e-9
        if p[0] == "C" and q[0] == "L":
            p, q = q, p
        if p[0] == "L" and q[0] == "L":
            a, b, inf1 = p[1], p[2], p[3]
            c, d, inf2 = q[1], q[2], q[3]
            x = _interseccion_rectas(a, (b[0] - a[0], b[1] - a[1]), c, (d[0] - c[0], d[1] - c[1]))
            if x is None:
                return []
            if (not inf1 and not -tol <= x[2] <= 1 + tol) or (not inf2 and not -tol <= x[3] <= 1 + tol):
                return []
            return [(x[0], x[1])]
        if p[0] == "L" and q[0] == "C":
            a, b, inf = p[1], p[2], p[3]
            (cx, cy), r = q[1], q[2]
            dx, dy = b[0] - a[0], b[1] - a[1]
            fx, fy = a[0] - cx, a[1] - cy
            aa, bb, cc = dx * dx + dy * dy, 2 * (fx * dx + fy * dy), fx * fx + fy * fy - r * r
            disc = bb * bb - 4 * aa * cc
            if disc < -tol:
                return []
            raiz = math.sqrt(max(0.0, disc))
            ts = sorted({(-bb - raiz) / (2 * aa), (-bb + raiz) / (2 * aa)})
            return [(a[0] + t * dx, a[1] + t * dy) for t in ts if inf or -tol <= t <= 1 + tol]
        (x0, y0), r0 = p[1], p[2]
        (x1, y1), r1 = q[1], q[2]
        d = math.dist((x0, y0), (x1, y1))
        if d <= tol or d > r0 + r1 + tol or d < abs(r0 - r1) - tol:
            return []
        a = (r0 * r0 - r1 * r1 + d * d) / (2 * d)
        h = math.sqrt(max(0.0, r0 * r0 - a * a))
        mx, my = x0 + a * (x1 - x0) / d, y0 + a * (y1 - y0) / d
        return [(mx + h * (y1 - y0) / d, my - h * (x1 - x0) / d), (mx - h * (y1 - y0) / d, my + h * (x1 - x0) / d)]

    def _resolver_ref(self, ref: dict[str, Any], path: str, advertencias: list[str]) -> dict[str, Any]:
        """PointRefResolver.Resolve: {ref, punto [x,y,z], ancla {handles, firmas, tipo} | None}."""
        if ref["kind"] == "coord":
            return {"ref": ref, "punto": list(ref["punto"]), "ancla": None}
        if ref["kind"] == "ent":
            e = self._entidad_ref(ref["handle"])
            h, tipo, punto = e["handle"], e["tipo"], ref["punto"]
            if punto in ("inicio", "fin", "medio"):
                curva = self._curva(e)
                if curva is None:
                    raise self._invalid_data(
                        f"{path}: la entidad {h} ({tipo}) no es una curva: no tiene inicio, fin ni medio", handle=h
                    )
                resultado = list(curva[punto])
            elif punto == "centro":
                if tipo in ("CIRCLE", "ARC"):
                    resultado = _p3(e["centro"])
                else:
                    advertencias.append(f"{path}: {h} ({tipo}) no es un círculo ni un arco; se usó el centro de su caja.")
                    caja = e.get("bbox") or [e.get("posicion", [0.0, 0.0, 0.0])] * 2
                    resultado = [(caja[0][k] + caja[1][k]) / 2 if k < len(caja[0]) else 0.0 for k in range(3)]
            else:
                verts = self._vertices_de(e)
                if verts is None:
                    raise self._invalid_data(
                        f"{path}: la entidad {h} ({tipo}) no tiene vértices (use inicio/fin/medio/centro)", handle=h
                    )
                i = ref["indice"] or 0
                if i >= len(verts):
                    n = len(verts)
                    raise self._invalid_data(
                        f"{path}.indice: {i} fuera de rango; la entidad {h} tiene {n} vértice(s) (índices 0 a {n - 1})",
                        handle=h,
                        n_vertices=n,
                    )
                resultado = list(verts[i])
            return {"ref": ref, "punto": resultado, "ancla": {"handles": [h], "firmas": [_firma(e)], "tipo": tipo}}
        h1, h2 = ref["handle"], ref["handle2"]
        e1, e2 = self._entidad_ref(h1), self._entidad_ref(h2)
        prim1, prim2 = self._primitivas(e1, True), self._primitivas(e2, True)
        for e, prim in ((e1, prim1), (e2, prim2)):
            if prim is None:
                raise self._invalid_data(f"{path}: la entidad {e['handle']} ({e['tipo']}) no es una curva", handle=e["handle"])
        puntos: list[tuple[float, float]] = []
        for p in prim1 or []:
            for q in prim2 or []:
                for x in self._cortes(p, q):
                    if all(math.dist(x, y) > 1e-9 for y in puntos):
                        puntos.append(x)
        if not puntos:
            raise _invalid_geometry(f"{path}: las entidades {h1} y {h2} no se intersecan", handle=h1, interseccion_con=h2)
        if len(puntos) > 1:
            cerca = ref["cerca_de"]
            if cerca is None:
                raise self._invalid_data(
                    f"{path}: hay {len(puntos)} intersecciones entre {h1} y {h2}; indique cerca_de",
                    intersecciones=[[x, y, 0.0] for x, y in puntos[:5]],
                    n_intersecciones=len(puntos),
                )
            puntos.sort(key=lambda x: math.dist(x, cerca[:2]))
        x, y = puntos[0]
        ancla = {"handles": [h1, h2], "firmas": [_firma(e1), _firma(e2)], "tipo": f"{e1['tipo']}+{e2['tipo']}"}
        return {"ref": ref, "punto": [x, y, 0.0], "ancla": ancla}

    @staticmethod
    def _punto_json(pt: dict[str, Any]) -> dict[str, Any]:
        """ResolvedPoint.ToJson: {ref, punto, ancla}."""
        return {
            "ref": _ref_json(pt["ref"]),
            "punto": pt["punto"],
            "ancla": "+".join(pt["ancla"]["handles"]) if pt["ancla"] else None,
        }

    # -- Lecturas: punto_de, distancia, proyectar (§5E.2) --

    def _punto_de(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        self._check_props("punto_de", params)
        ref = _parse_punto_ref(params["ref"], "ref")
        propias: list[str] = []
        pt = self._resolver_ref(ref, "ref", propias)
        units = self._units()
        units["advertencias"].extend(propias)
        return {
            "pid": self.state.pid,
            "punto": pt["punto"],
            "ref": _ref_json(ref),
            "ancla": "+".join(pt["ancla"]["handles"]) if pt["ancla"] else None,
            "tipo_entidad": pt["ancla"]["tipo"] if pt["ancla"] else None,
            **units,
        }

    def _distancia(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        self._check_props("distancia", params)
        ra, rb = _parse_punto_ref(params["a"], "a"), _parse_punto_ref(params["b"], "b")
        propias: list[str] = []
        pa, pb = self._resolver_ref(ra, "a", propias), self._resolver_ref(rb, "b", propias)
        a, b = pa["punto"], pb["punto"]
        dx, dy, dz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
        d2 = math.hypot(dx, dy)
        units = self._units()
        units["advertencias"].extend(propias)
        return {
            "pid": self.state.pid,
            "distancia": math.sqrt(dx * dx + dy * dy + dz * dz),
            "distancia_2d": d2,
            "dx": dx,
            "dy": dy,
            "dz": dz,
            "angulo": _norm360(math.degrees(math.atan2(dy, dx))) if d2 > 1e-9 else None,
            "a": a,
            "b": b,
            "puntos_resueltos": [self._punto_json(pa), self._punto_json(pb)],
            **units,
        }

    def _proyectar(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        self._check_props("proyectar", params)
        ref = _parse_punto_ref(params["punto"], "punto")
        handle = self._leer_handle(params, "handle")
        propias: list[str] = []
        pt = self._resolver_ref(ref, "punto", propias)
        e = self._entidad_ref(handle)
        p = pt["punto"]
        tipo = e["tipo"]
        if tipo in ("CIRCLE", "ARC"):
            c, r = _p3(e["centro"]), float(e["radio"])
            a0 = e.get("angulo_inicio", 0.0) if tipo == "ARC" else 0.0
            barrido = (e["angulo_fin"] - e["angulo_inicio"]) % 360.0 if tipo == "ARC" else 360.0
            ang = 0.0 if math.dist(p[:2], c[:2]) <= 1e-12 else (math.degrees(math.atan2(p[1] - c[1], p[0] - c[0])) - a0) % 360.0
            if ang > barrido:
                ang = barrido if (ang - barrido) < (360.0 - ang) else 0.0
            a = math.radians(a0 + ang)
            proy = [c[0] + r * math.cos(a), c[1] + r * math.sin(a), c[2]]
            parametro, desde_inicio, longitud = math.radians(a0 + ang), r * math.radians(ang), r * math.radians(barrido)
        else:
            prims = self._primitivas(e, False)
            if prims is None:
                raise self._invalid_data(f"handle: la entidad {handle} ({tipo}) no es una curva", handle=handle)
            mejor: tuple[float, list[float], float, float] | None = None
            acumulado = 0.0
            for i, (_k, a, b, _inf) in enumerate(prims):
                dxs, dys = b[0] - a[0], b[1] - a[1]
                largo2 = dxs * dxs + dys * dys
                t = 0.0 if largo2 <= 0 else max(0.0, min(1.0, ((p[0] - a[0]) * dxs + (p[1] - a[1]) * dys) / largo2))
                q = [a[0] + t * dxs, a[1] + t * dys, 0.0]
                d = math.dist(p[:2], q[:2])
                largo = math.sqrt(largo2)
                param = t * largo if tipo == "LINE" else i + t
                if mejor is None or d < mejor[0] - 1e-12:
                    mejor = (d, q, param, acumulado + t * largo)
                acumulado += largo
            assert mejor is not None
            _d, proy, parametro, desde_inicio = mejor
            longitud = acumulado
        units = self._units()
        units["advertencias"].extend(propias)
        return {
            "pid": self.state.pid,
            "punto": p,
            "punto_proyectado": proy,
            "distancia": math.dist(p, proy),
            "parametro": parametro,
            "distancia_desde_inicio": desde_inicio,
            "longitud_curva": longitud,
            "handle": handle,
            "tipo_entidad": tipo,
            "puntos_resueltos": [self._punto_json(pt)],
            **units,
        }

    # -- Almacén de previsualizaciones (§5E.5; PreviewRegistry con reloj simulado) --

    def _purgar_previews(self) -> None:
        ahora = self.state.ahora
        for pid_ in [k for k, r in self.state.previews.items() if r["caduca"] <= ahora]:
            del self.state.previews[pid_]

    def _previews_vivas(self, doc: str | None = None) -> list[dict[str, Any]]:
        self._purgar_previews()
        doc = self.state.doc_key if doc is None else doc
        return sorted((r for r in self.state.previews.values() if r["doc"] == doc), key=lambda r: r["n"])

    def _registrar_preview(
        self, herramienta: str, params: dict[str, Any], anclas: list[dict[str, Any]], handles: list[str], espacio: str
    ) -> tuple[dict[str, Any], dict[str, Any] | None]:
        vivas = self._previews_vivas()
        desalojada = None
        if len(vivas) >= PREVIEW_MAX_POR_DOC:
            desalojada = vivas[0]
            del self.state.previews[desalojada["id"]]
        self.state.next_preview += 1
        rec = {
            "id": f"prev-{self.state.next_preview:012x}",
            "n": self.state.next_preview,
            "doc": self.state.doc_key,
            "herramienta": herramienta,
            "params": params,
            "anclas": anclas,
            "handles": list(handles),
            "espacio": espacio,
            "creado": self.state.ahora,
            "caduca": self.state.ahora + PREVIEW_TTL,
        }
        self.state.previews[rec["id"]] = rec
        return rec, desalojada

    def _huerfanas(self) -> int:
        """PreviewStore.OrphanWarning: entidades de MCP-PREVIEW (modelo y layouts) sin previsualización viva.

        Si la capa MCP-PREVIEW no existe → 0 sin recorrer nada (el tope de coste del plugin no se simula aquí).
        """
        if self._find_layer(CAPA_PREVIEW) is None:
            return 0
        vivos = {h for r in self._previews_vivas() for h in r["handles"]}
        return sum(
            1
            for _esp, entities in self._all_spaces()
            for e in entities
            if str(e.get("capa", "")).casefold() == CAPA_PREVIEW.casefold() and e["handle"] not in vivos
        )

    def _quitar_entidad(self, handle: str) -> None:
        for _esp, entities in self._all_spaces():
            entities[:] = [e for e in entities if e["handle"] != handle]

    def _preview_pedida(self, nombre: str, params: dict[str, Any]) -> dict[str, Any]:
        """confirmar / descartar_preview, pasos (1) y (2): NOT_FOUND y otro documento."""
        self._check_props(nombre, params)
        preview_id = params.get("preview_id")
        if not isinstance(preview_id, str):
            raise _invalid("preview_id: debe ser una cadena")
        if len(preview_id) < 1:
            raise _invalid("preview_id: debe tener al menos 1 carácter(es)")
        if len(preview_id) > 64:
            raise _invalid("preview_id: debe tener como máximo 64 caracteres")
        self._purgar_previews()
        rec = self.state.previews.get(preview_id)
        if rec is None:
            raise _not_found(mensaje_preview_no_encontrada(preview_id), preview_id=preview_id)
        if rec["doc"] != self.state.doc_key:
            raise self._invalid_data(detalle_preview_otro_documento(preview_id), preview_id=preview_id)
        return rec

    def _confirmar(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        rec = self._preview_pedida("confirmar", params)
        # (3) Anclas: cualquier cambio (movida, editada, borrada) → INVALID_PARAMS sin escribir nada.
        for ancla in rec["anclas"]:
            for h, firma in zip(ancla["handles"], ancla["firmas"]):
                loc = self._find_entity(h)
                if loc is None or _firma(loc[1]) != firma:
                    raise self._invalid_data(detalle_ancla_cambiada(h), preview_id=rec["id"], handle=h)
        # (4)–(6) Recalcular desde los parámetros guardados y escribir en un paso; el registro se olvida al final.
        resp = self._generar(rec["herramienta"], copy.deepcopy(rec["params"]), confirmando=rec)
        self.state.previews.pop(rec["id"], None)
        return resp

    def _descartar_preview(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        rec = self._preview_pedida("descartar_preview", params)
        borradas = 0
        for h in rec["handles"]:
            loc = self._find_entity(h)
            if loc is not None and str(loc[1].get("capa", "")).casefold() == CAPA_PREVIEW.casefold():
                self._quitar_entidad(h)
                borradas += 1
        # Sin nada que borrar no se abre paso de deshacer (§5E.5); con algo borrado, un paso.
        if borradas:
            self.state.pasos_deshacer += 1
        self.state.previews.pop(rec["id"], None)
        return {
            "pid": self.state.pid,
            "preview_id": rec["id"],
            "descartada": True,
            "n_borradas": borradas,
            "n_no_encontradas": len(rec["handles"]) - borradas,
            "herramienta": rec["herramienta"],
            **self._units(),
        }

    # -- Núcleo común de las generadoras (GeneratorBase) --

    @staticmethod
    def _caja_planificada(p: dict[str, Any]) -> list[list[float]] | None:
        return p.get("caja")

    def _materializar(self, p: dict[str, Any], handle: str, capa: str, previos: list[str]) -> dict[str, Any]:
        """Entidad del dibujo ficticio a partir de una entidad planificada (GeometryDrawer)."""
        tipo = p["tipo"]
        if tipo == "LWPOLYLINE":
            xy = p["vertices"]
            longitud, area = _longitud_area(xy, p["cerrada"], None)
            return {
                "handle": handle,
                "tipo": "LWPOLYLINE",
                "capa": capa,
                "bbox": _box3(_box_of([list(v) for v in xy])),
                "n_vertices": len(xy),
                "cerrada": p["cerrada"],
                "longitud": longitud,
                "area": area,
                "tiene_arcos": False,
                "vertices": [[x, y, 0.0] for x, y in xy],
            }
        if tipo == "LINE":
            e = _line(handle, capa, p["inicio"], p["fin"])
            # §5G.1: una pieza de una cara conserva su aspecto propio, salvo en la previsualización.
            return e if capa.casefold() == CAPA_PREVIEW.casefold() else {**e, **p.get("aspecto", {})}
        if tipo == "CIRCLE":
            c, r = p["centro"], p["radio"]
            return {
                "handle": handle,
                "tipo": "CIRCLE",
                "capa": capa,
                "bbox": _box3([[c[0] - r, c[1] - r], [c[0] + r, c[1] + r]]),
                "centro": c,
                "radio": r,
                "longitud": 2 * math.pi * r,
                "area": math.pi * r * r,
            }
        if tipo == "TEXT":
            return {
                "handle": handle,
                "tipo": "TEXT",
                "capa": capa,
                "bbox": _box3(p["caja"]),
                "texto": p["texto"],
                "altura": p["altura"],
                "posicion": p["posicion"],
                "rotacion": 0.0,
                "_contenido": _escape_dbtext(p["texto"]),
                "_justificacion": "medio_centro",
                "_estilo": p["estilo"],
            }
        if tipo == "MTEXT":
            return {
                "handle": handle,
                "tipo": "MTEXT",
                "capa": capa,
                "bbox": _box3(p["caja"]),
                "texto": p["texto"],
                "altura": p["altura"],
                "posicion": p["posicion"],
                "ancho": p["ancho"],
                "rotacion": 0.0,
                "_contenido": _escape_mtext(p["texto"]),
                "_adjunto": "medio_centro",
                "_estilo": p["estilo"],
            }
        if tipo == "HATCH":
            return {
                "handle": handle,
                "tipo": "HATCH",
                "capa": capa,
                "bbox": _box3(p["caja"]),
                "patron": p["patron"],
                "area": p["area"],
                # §5G.0: en previsualización, un sombreado con bucles ajenos no pone reactores en entidades del usuario.
                "asociativo": capa.casefold() != CAPA_PREVIEW.casefold() or not p.get("bucles_conservados"),
                "_contorno": [previos[i] for i in p["contorno"]] + [h for b in p.get("bucles_conservados", []) for h in b],
                "_escala": p["escala"],
                "_angulo": p["angulo"],
                # §5G.0: un sombreado rehecho conserva su aspecto propio, salvo en la previsualización.
                **({} if capa.casefold() == CAPA_PREVIEW.casefold() else p.get("aspecto", {})),
            }
        if tipo == "INSERT":
            e = _insert(handle, capa, p["nombre"], p["posicion"], p["rotacion"], _box3(p["caja"]), [])
            e["escala"] = p["escala"]
            e["dinamico"] = p["dinamico"]
            e["_propiedades"] = copy.deepcopy(p["propiedades"])
            e["_aplicadas"] = list(p["aplicadas"])
            return e
        if tipo == "DIMENSION":
            return {
                "handle": handle,
                "tipo": "DIMENSION",
                "capa": capa,
                "bbox": _box3(p["caja"]),
                "subtipo": "RotatedDimension",
                "medida": p["medida"],
                "texto": "",
                "p1": p["p1"],
                "p2": p["p2"],
                "posicion_linea": p["linea"],
                "_estilo": p["estilo"],
            }
        # COPY / OFFSET: entidad ya transformada (sin handle).
        e = copy.deepcopy(p["entidad"])
        e["handle"] = handle
        e["capa"] = capa
        for attrib in e.get("_atributos", []):
            attrib["handle"] = self._alloc_handle()
        e["_origen"] = p["origen"]
        return e

    @staticmethod
    def _resumen_4a(e: dict[str, Any]) -> dict[str, Any]:
        """entidades[] de G6: handle, tipo, capa, bbox y medidas según el tipo."""
        d: dict[str, Any] = {"handle": e["handle"], "tipo": e["tipo"], "capa": e["capa"], "bbox": _bbox2d(e.get("bbox"))}
        tipo = e["tipo"]
        if tipo == "LWPOLYLINE":
            d.update(n_vertices=e["n_vertices"], cerrada=e["cerrada"], longitud=e["longitud"], area=e["area"])
        elif tipo == "LINE":
            d["longitud"] = e["longitud"]
        elif tipo in ("CIRCLE", "ARC"):
            d.update(radio=e["radio"], centro=e["centro"])
            if tipo == "ARC":
                d["longitud"] = e.get("longitud")
        elif tipo == "TEXT":
            d.update(
                texto=e["texto"], altura=e["altura"], posicion=e["posicion"],
                justificacion=e.get("_justificacion", "izquierda"), estilo=e.get("_estilo"),
            )
        elif tipo == "MTEXT":
            d.update(texto=e["texto"], altura=e["altura"], posicion=e["posicion"], adjunto=e.get("_adjunto"), estilo=e.get("_estilo"))
        elif tipo == "HATCH":
            d.update(patron=e.get("patron"), area=e.get("area"), asociativo=e.get("asociativo"), contorno_handles=e.get("_contorno"))
        elif tipo == "INSERT":
            d.update(
                bloque=e["nombre"], posicion=e["posicion"], rotacion=e["rotacion"], escala=e["escala"], dinamico=e["dinamico"],
                propiedades_aplicadas=list(e.get("_aplicadas", [])),
            )
        elif tipo == "DIMENSION":
            d.update(medida=e["medida"], estilo=e.get("_estilo"))
        if "_origen" in e:
            d["origen"] = e["_origen"]
            if e.get("longitud") is not None and "longitud" not in d:
                d["longitud"] = e["longitud"]
        return d

    def _dibujar(
        self, plan: list[dict[str, Any]], entities: list[dict[str, Any]], en_preview: bool, propias: list[str]
    ) -> dict[str, Any]:
        """GeometryDrawer.Draw: capas por concepto (R3) o MCP-PREVIEW, validadas antes de escribir nada.

        Fase 4B: BORRAR se omite en la previsualización y se ejecuta en el definitivo (misma transacción); INSERT
        importa antes la definición de la biblioteca (EnsureBlockDefinitions)."""
        borrados = [p for p in plan if p["tipo"] == "BORRAR"]
        plan = [p for p in plan if p["tipo"] != "BORRAR"]
        for p in borrados if not en_preview else []:
            loc = self._find_entity(p["handle"])
            if loc is None or loc[1]["tipo"] != p["tipo_esperado"]:
                raise _invalid(f"borrar: la entidad {p['handle']} ya no es {p['tipo_esperado']}")
        # DrawBlockRef: nombres y valores dinámicos solo se comprueban sobre la referencia creada (§5G.6). Un rechazo aborta
        # toda la transacción: aquí, antes de escribir nada. Con dibujar_preview: false esto no se ejecuta.
        for p in plan:
            if p["tipo"] == "INSERT":
                p["propiedades"] = self._aplicar_dinamicas(p["nombre"], p["dinamico"], p["base_dinamica"], p["valores_dinamicos"])
                p["aplicadas"] = [
                    next(q["nombre"] for q in p["propiedades"] if q["nombre"].casefold() == k.casefold())
                    for k in p["valores_dinamicos"] or {}
                ]
        resueltas: dict[tuple[Any, Any], tuple[dict[str, Any], bool]] = {}
        claves = []
        for p in plan:
            clave = (CAPA_PREVIEW, None) if en_preview else (p.get("capa"), p.get("concepto"))
            if clave not in resueltas:
                capa, creada, adv = self._capa_escritura(*clave)
                propias.extend(adv)
                resueltas[clave] = (capa, creada)
            claves.append(clave)
        capas_creadas: list[str] = []
        for capa, creada in resueltas.values():
            if creada and self._find_layer(capa["nombre"]) is None:
                self.state.capas_creadas.append(capa)
                capas_creadas.append(capa["nombre"])
        capas_importadas: list[str] = []
        for p in plan:
            if p["tipo"] == "INSERT" and p["importar"] is not None and not any(
                d["nombre"].casefold() == p["nombre"].casefold() for d in self.state.block_defs
            ):
                capas_importadas += self._importar_definicion(p["importar"])
        handles_borrados: list[str] = []
        if not en_preview:
            for p in borrados:
                self._quitar_entidad(p["handle"])
                handles_borrados.append(p["handle"])
        handles: list[str] = []
        nuevas: list[dict[str, Any]] = []
        for p, clave in zip(plan, claves):
            capa_real = self._find_layer(resueltas[clave][0]["nombre"])
            assert capa_real is not None
            e = self._materializar(p, self._alloc_handle(), capa_real["nombre"], handles)
            handles.append(e["handle"])
            nuevas.append(e)
        entities.extend(nuevas)
        capas: list[str] = []
        for e in nuevas:
            if e["capa"] not in capas:
                capas.append(e["capa"])
        propiedades = next((e["_propiedades"] for e in nuevas if e["tipo"] == "INSERT" and "_propiedades" in e), [])
        ancho_resuelto = None
        for p in plan:
            if p["tipo"] == "INSERT" and p.get("ancho_al_dibujar") is not None:
                ancho_resuelto = p["ancho_al_dibujar"]["resuelto"]
                propias.extend(p["ancho_al_dibujar"]["avisos"])
        return {
            "handles": handles,
            "handles_borrados": handles_borrados,
            "entidades": [self._resumen_4a(e) for e in nuevas],
            "capas": capas,
            "capas_creadas": capas_creadas,
            "capas_importadas": capas_importadas,
            "propiedades_dinamicas": copy.deepcopy(propiedades),
            "ancho_resuelto": ancho_resuelto,
            "importo": any(p["tipo"] == "INSERT" and p["importar"] is not None for p in plan),
        }

    def _generar(self, nombre: str, params: dict[str, Any], confirmando: dict[str, Any] | None = None) -> dict[str, Any]:
        self._check_props(nombre, params)
        preview = self._bool(params, "preview", False)
        dibujar = self._bool(params, "dibujar_preview", True)
        forzar = self._bool(params, "forzar", False)
        self._cadena(params, "espacio", 1, 255, requerido=False)
        own, refs = getattr(self, f"_parse_{nombre}")(params)
        # G2 estricta: sin aceptar_sin_unidades (el schema no lo admite).
        if self.state.insunits == 0:
            raise _units_unknown()
        espacio, entities = self._espacio_destino(params)
        propias: list[str] = []
        puntos = [self._resolver_ref(r, path, propias) for r, path in refs]
        plan, numeros = getattr(self, f"_plan_{nombre}")(own, puntos, propias)
        numeros = dict(numeros)
        # Huellas de las entidades usadas directamente por handle (GeneratedGeometry.AnclasExtra, §5E.5).
        anclas_extra: list[dict[str, Any]] = numeros.pop("_anclas_extra", [])
        if not plan:
            raise _invalid_geometry("la herramienta no produjo ninguna entidad")
        if confirmando is None:
            n = self._huerfanas()
            if n:
                propias.append(advertencia_huerfanas(n))
        cajas = [self._caja_planificada(p) for p in plan]
        propias += self._r4(espacio, entities, cajas, forzar, "entidades")
        numeros = dict(numeros)
        numeros.setdefault("n_entidades", sum(1 for p in plan if p["tipo"] != "BORRAR"))
        numeros.setdefault("bbox", _union2d([c for c in cajas if c is not None]))
        puntos_json = [self._punto_json(pt) for pt in puntos]
        vacio: dict[str, Any] = {
            "handles": [], "handles_borrados": [], "entidades": [], "capas": [], "capas_creadas": [],
            "capas_importadas": [], "propiedades_dinamicas": [], "ancho_resuelto": None, "importo": False,
        }
        if preview and confirmando is None:
            anclas = [pt["ancla"] for pt in puntos if pt["ancla"]] + anclas_extra
            guardados = {k: copy.deepcopy(v) for k, v in params.items() if k not in ("preview", "dibujar_preview")}
            dibujo = vacio
            if dibujar:
                dibujo = self._dibujar(plan, entities, True, propias)
                self.state.pasos_deshacer += 1
            rec, desalojada = self._registrar_preview(nombre, guardados, anclas, dibujo["handles"], espacio)
            if desalojada is not None:
                propias.append(advertencia_desalojo(desalojada["id"]))
            return self._extras_4b(nombre, self._g6(rec, dibujar, dibujo, espacio, puntos_json, numeros, propias), dibujo)
        # Definitivo (o confirmación): en confirmar, primero se decide qué entidades de preview se borran.
        borrar: list[str] = []
        if confirmando is not None:
            for h in confirmando["handles"]:
                loc = self._find_entity(h)
                if loc is None:
                    continue
                capa_actual = str(loc[1].get("capa", ""))
                if capa_actual.casefold() != CAPA_PREVIEW.casefold():
                    propias.append(advertencia_preview_movida(h, capa_actual))
                    continue
                borrar.append(h)
        dibujo = self._dibujar(plan, entities, False, propias)
        for h in borrar:
            self._quitar_entidad(h)
        self.state.pasos_deshacer += 1
        resp = self._extras_4b(nombre, self._g6(None, False, dibujo, espacio, puntos_json, numeros, propias), dibujo)
        if confirmando is not None:
            resp["preview_id"] = confirmando["id"]
            resp["confirmado"] = True
            resp["n_preview_borradas"] = len(borrar)
            resp["herramienta"] = confirmando["herramienta"]
        return resp

    def _g6(
        self,
        rec: dict[str, Any] | None,
        dibujada: bool,
        dibujo: dict[str, Any],
        espacio: str,
        puntos_json: list[dict[str, Any]],
        numeros: dict[str, Any],
        propias: list[str],
    ) -> dict[str, Any]:
        """GeneratorBase.BuildResponse (G6)."""
        units = self._units()
        units["advertencias"].extend(propias)
        capa = dibujo["capas"][0] if dibujo["capas"] else (CAPA_PREVIEW if rec is not None else "")
        return {
            "pid": self.state.pid,
            "preview": rec is not None,
            "preview_id": rec["id"] if rec is not None else None,
            "preview_dibujada": bool(rec is not None and dibujada),
            "caduca": rec["caduca"].isoformat(timespec="milliseconds") if rec is not None else None,
            "handles": dibujo["handles"],
            "handles_borrados": dibujo["handles_borrados"],
            "entidades": dibujo["entidades"],
            "capa": capa,
            "capas": dibujo["capas"],
            "capas_creadas": dibujo["capas_creadas"],
            "espacio": espacio,
            "puntos_resueltos": puntos_json,
            "numeros": numeros,
            **units,
        }

    @staticmethod
    def _extras_4b(nombre: str, resp: dict[str, Any], dibujo: dict[str, Any]) -> dict[str, Any]:
        """vano (§5G.1): G6 + capas_importadas (solo en la llamada que importó el bloque) + propiedades_dinamicas, en la raíz."""
        if nombre == "vano":
            if dibujo["importo"]:
                resp["capas_importadas"] = dibujo["capas_importadas"]
            resp["propiedades_dinamicas"] = dibujo["propiedades_dinamicas"]
            bloque = resp["numeros"].get("bloque")
            if bloque is not None and bloque["ancho_por"] == "pendiente":
                if dibujo["ancho_resuelto"] is not None:
                    resp["numeros"]["bloque"] = {**bloque, **dibujo["ancho_resuelto"]}
                else:
                    resp["advertencias"].append(advertencia_ancho_pendiente(bloque["nombre"]))
        return resp

    def _muro(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        return self._generar("muro", params)

    def _ejes_rejilla(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        return self._generar("ejes_rejilla", params)

    def _recinto(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        return self._generar("recinto", params)

    def _offset(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        return self._generar("offset", params)

    def _array_rectangular(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        return self._generar("array_rectangular", params)

    def _array_polar(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        return self._generar("array_polar", params)

    @staticmethod
    def _polilinea_plan(xy: list[tuple[float, float]], cerrada: bool, capa: Any, concepto: Any) -> dict[str, Any]:
        return {
            "tipo": "LWPOLYLINE",
            "vertices": list(xy),
            "cerrada": cerrada,
            "capa": capa,
            "concepto": concepto,
            "caja": _box_of([list(v) for v in xy]),
        }

    # -- muro (§5E.3.1) --

    def _parse_muro(self, params: dict[str, Any]) -> tuple[dict[str, Any], list[tuple[dict[str, Any], str]]]:
        eje = params.get("eje")
        refs: list[tuple[dict[str, Any], str]] = []
        eje_handle = None
        if isinstance(eje, str):
            if not re.fullmatch(r"[0-9A-Fa-f]{1,16}", eje):
                raise _invalid("eje: formato de handle inválido")
            eje_handle = eje.upper()
        elif isinstance(eje, list):
            refs = [(r, f"eje[{i}]") for i, r in enumerate(_parse_lista_refs(eje, "eje", 2, 200))]
        else:
            raise _invalid("eje: debe ser un array de 2 a 200 puntos o el handle de una línea/polilínea")
        espesor = self._positivo(params, "espesor")
        justificacion = params.get("justificacion", "centro")
        if justificacion not in ("centro", "izquierda", "derecha"):
            raise _invalid("justificacion: debe ser uno de centro, izquierda, derecha")
        cerrado = self._bool(params, "cerrado", False)
        representacion = params.get("representacion", "contorno")
        if representacion not in ("contorno", "caras"):
            raise _invalid("representacion: debe ser uno de contorno, caras")
        achurado = params.get("achurado")
        if achurado is not None:
            if not isinstance(achurado, dict):
                raise _invalid("achurado: debe ser un objeto")
            for clave in achurado:
                if clave not in ("patron", "escala", "angulo"):
                    raise _invalid(f"achurado.{clave}: propiedad no permitida")
            if representacion != "contorno":
                raise _invalid("achurado: solo con representacion 'contorno'")
            patron = self._cadena(achurado, "patron", 1, 255, requerido=False) or "SOLID"
            achurado = {
                "patron": patron.upper(),
                "escala": self._positivo(achurado, "escala", 1.0),
                "angulo": _norm360(self._numero(achurado, "angulo", 0.0) or 0.0),
            }
        limite = self._numero(params, "limite_inglete", 4.0) or 4.0
        if limite < 1:
            raise _invalid("limite_inglete: debe ser un número mayor o igual que 1")
        own = {
            "eje_handle": eje_handle,
            "espesor": espesor,
            "justificacion": justificacion,
            "cerrado": cerrado,
            "representacion": representacion,
            "achurado": achurado,
            "capa": self._cadena(params, "capa", 1, 255, requerido=False),
            "capa_achurado": self._cadena(params, "capa_achurado", 1, 255, requerido=False),
            "limite": limite,
        }
        return own, refs

    def _plan_muro(
        self, own: dict[str, Any], puntos: list[dict[str, Any]], propias: list[str]
    ) -> tuple[list[dict[str, Any]], dict[str, Any]]:
        cerrado = own["cerrado"]
        if own["eje_handle"] is not None:
            e = self._entidad_ref(own["eje_handle"])
            verts = self._vertices_de(e)
            if verts is None:
                raise self._invalid_data(
                    f"eje: la entidad {e['handle']} ({e['tipo']}) no es una línea ni una polilínea", handle=e["handle"]
                )
            cerrado = cerrado or bool(e.get("cerrada"))
            if e.get("tiene_arcos"):
                propias.append(f"eje: la polilínea {e['handle']} tiene arcos; se usaron las cuerdas.")
            anclas_extra = [_ancla_directa(e)]
            xy, colapsados = _colapsar_duplicados(
                [(float(v[0]), float(v[1])) for v in verts], lazo_cerrado=cerrado
            )
            if colapsados:
                propias.append(advertencia_eje_duplicados_colapsados(e["handle"], colapsados))
        else:
            verts = [pt["punto"] for pt in puntos]
            anclas_extra = []
            xy = [(float(v[0]), float(v[1])) for v in verts]
        if own["achurado"] is not None and _patron_desconocido(own["achurado"]["patron"]):
            raise _not_found(
                f"El patrón de sombreado '{own['achurado']['patron']}' no existe.", patron=own["achurado"]["patron"]
            )
        try:
            w = _contorno_muro(xy, own["espesor"], own["justificacion"], cerrado, own["limite"])
        except _WallError as ex:
            if ex.es_parametro:
                raise _invalid(ex.detalle) from None
            raise _invalid_geometry(ex.detalle) from None
        capa = own["capa"]
        plan: list[dict[str, Any]] = []
        if own["representacion"] == "contorno":
            plan.append(self._polilinea_plan(w["contorno"], True, capa, "muros"))
            if w["interior"] is not None:
                plan.append(self._polilinea_plan(w["interior"], True, capa, "muros"))
            if own["achurado"] is not None:
                plan.append(
                    {
                        "tipo": "HATCH",
                        "contorno": list(range(len(plan))),
                        "patron": own["achurado"]["patron"],
                        "escala": own["achurado"]["escala"],
                        "angulo": own["achurado"]["angulo"],
                        "area": w["area"],
                        "capa": own["capa_achurado"],
                        "concepto": "achurados",
                        "caja": _box_of([list(v) for v in w["contorno"]]),
                    }
                )
        else:
            izq, der = w["izquierda"], w["derecha"]
            plan.append(self._polilinea_plan(izq, cerrado, capa, "muros"))
            plan.append(self._polilinea_plan(der, cerrado, capa, "muros"))
            if not cerrado:
                for a, b in ((izq[0], der[0]), (izq[-1], der[-1])):
                    plan.append(
                        {
                            "tipo": "LINE",
                            "inicio": [a[0], a[1], 0.0],
                            "fin": [b[0], b[1], 0.0],
                            "capa": capa,
                            "concepto": "muros",
                            "caja": _box_of([list(a), list(b)]),
                        }
                    )
        numeros = {
            "longitud_eje": w["longitud_eje"],
            "longitud_contorno": w["longitud_contorno"],
            "area": w["area"],
            "espesor": own["espesor"],
            "n_tramos": w["n_tramos"],
            "_anclas_extra": anclas_extra,
        }
        return plan, numeros

    # -- ejes_rejilla (§5E.3.2) --

    def _parse_ejes_rejilla(self, params: dict[str, Any]) -> tuple[dict[str, Any], list[tuple[dict[str, Any], str]]]:
        origen = _parse_punto_ref(params["origen"], "origen")
        seps: dict[str, list[float]] = {}
        for eje in ("x", "y"):
            clave = f"separaciones_{eje}"
            valor = params.get(clave)
            if not isinstance(valor, list) or len(valor) > 100:
                raise _invalid(f"{clave}: debe tener entre 0 y 100 elementos")
            for i, s in enumerate(valor):
                if not _es_numero(s) or s <= 0:
                    raise _invalid(f"{clave}[{i}]: debe ser un número mayor que 0")
            seps[eje] = [float(s) for s in valor]
        if not seps["x"] and not seps["y"]:
            raise _invalid("separaciones: se necesita al menos un eje en x o en y")
        rotulos: dict[str, list[str]] = {}
        for eje, defecto in (("x", lambda n: [str(i + 1) for i in range(n)]), ("y", _rotulos_letras)):
            clave, n = f"rotulos_{eje}", len(seps[eje]) + 1
            valor = params.get(clave)
            if valor is None:
                rotulos[eje] = defecto(n)
                continue
            if not isinstance(valor, list) or len(valor) != n:
                raise _invalid(f"{clave}: debe tener {n} elementos (separaciones_{eje} + 1)")
            for i, r in enumerate(valor):
                if not isinstance(r, str) or not 1 <= len(r) <= 10:
                    raise _invalid(f"{clave}[{i}]: debe tener entre 1 y 10 caracteres")
            rotulos[eje] = list(valor)
        vistos: set[str] = set()
        for r in rotulos["x"] + rotulos["y"]:
            if r in vistos:
                raise _invalid(f"rotulos: rótulo repetido '{r}'")
            vistos.add(r)
        extension = params.get("extension")
        if not _es_numero(extension) or extension < 0:
            raise _invalid("extension: debe ser un número mayor o igual que 0")
        radio = self._positivo(params, "radio_burbuja")
        altura = self._positivo(params, "altura_texto")
        burbujas = params.get("burbujas", "fin")
        if burbujas not in ("inicio", "fin", "ambos"):
            raise _invalid("burbujas: debe ser uno de inicio, fin, ambos")
        own = {
            "seps": seps,
            "rotulos": rotulos,
            "extension": float(extension),
            "radio": radio,
            "altura": altura,
            "burbujas": burbujas,
            "rotacion": self._numero(params, "rotacion", 0.0) or 0.0,
            "estilo_param": params.get("estilo"),
            "capa": self._cadena(params, "capa", 1, 255, requerido=False),
        }
        return own, [(origen, "origen")]

    def _plan_ejes_rejilla(
        self, own: dict[str, Any], puntos: list[dict[str, Any]], propias: list[str]
    ) -> tuple[list[dict[str, Any]], dict[str, Any]]:
        if own["estilo_param"] is not None:
            estilo = self._estilo({"estilo": own["estilo_param"]}, self.state.estilos_texto, "texto")
        else:
            # AxisLabelStyle.Choose (gate en vivo de Fase 4A, 2026-10-27): sin 'estilo', el de la oficina ("75") si
            # existe en el dibujo; si no, el actual con advertencia.
            real = next((s for s in self.state.estilos_texto if s.casefold() == ESTILO_EJES_OFICINA.casefold()), None)
            if real is not None:
                estilo = real
            else:
                estilo = self.state.estilos_texto[0]
                propias.append(advertencia_estilo_ejes(ESTILO_EJES_OFICINA, estilo))
        o = puntos[0]["punto"]
        xs = [o[0] + sum(own["seps"]["x"][:i]) for i in range(len(own["seps"]["x"]) + 1)]
        ys = [o[1] + sum(own["seps"]["y"][:j]) for j in range(len(own["seps"]["y"]) + 1)]
        ext, r, rot, capa = own["extension"], own["radio"], own["rotacion"], own["capa"]
        lineas, circulos, textos = [], [], []

        def g(x: float, y: float) -> list[float]:
            return _girar([x, y, o[2]], rot, o)

        extremos = {"inicio": [-1], "fin": [1], "ambos": [-1, 1]}[own["burbujas"]]
        for i, x in enumerate(xs):
            lineas.append((g(x, ys[0] - ext), g(x, ys[-1] + ext)))
            for s in extremos:
                c = g(x, ys[-1] + ext + r) if s > 0 else g(x, ys[0] - ext - r)
                circulos.append(c)
                textos.append((c, own["rotulos"]["x"][i]))
        for j, y in enumerate(ys):
            lineas.append((g(xs[0] - ext, y), g(xs[-1] + ext, y)))
            for s in extremos:
                c = g(xs[-1] + ext + r, y) if s > 0 else g(xs[0] - ext - r, y)
                circulos.append(c)
                textos.append((c, own["rotulos"]["y"][j]))
        plan: list[dict[str, Any]] = []
        for a, b in lineas:
            plan.append({"tipo": "LINE", "inicio": a, "fin": b, "capa": capa, "concepto": "ejes", "caja": _box_of([a, b])})
        for c in circulos:
            plan.append(
                {"tipo": "CIRCLE", "centro": c, "radio": r, "capa": capa, "concepto": "ejes",
                 "caja": [[c[0] - r, c[1] - r], [c[0] + r, c[1] + r]]}
            )
        for c, texto in textos:
            plan.append(
                {"tipo": "TEXT", "posicion": c, "texto": texto, "altura": own["altura"], "estilo": estilo, "capa": capa,
                 "concepto": "ejes", "caja": _caja_texto(c, texto, own["altura"])}
            )
        numeros = {
            "n_ejes_x": len(xs),
            "n_ejes_y": len(ys),
            "ancho": sum(own["seps"]["x"]),
            "alto": sum(own["seps"]["y"]),
            "posiciones_x": xs,
            "posiciones_y": ys,
            "n_burbujas": len(circulos),
        }
        return plan, numeros

    # -- recinto (§5E.3.3) --

    def _parse_recinto(self, params: dict[str, Any]) -> tuple[dict[str, Any], list[tuple[dict[str, Any], str]]]:
        contorno = params.get("contorno")
        refs: list[tuple[dict[str, Any], str]] = []
        contorno_handle = None
        if isinstance(contorno, str):
            if not re.fullmatch(r"[0-9A-Fa-f]{1,16}", contorno):
                raise _invalid("contorno: formato de handle inválido")
            contorno_handle = contorno.upper()
        elif isinstance(contorno, list):
            refs = [(r, f"contorno[{i}]") for i, r in enumerate(_parse_lista_refs(contorno, "contorno", 3, 200))]
        else:
            raise _invalid("contorno: debe ser un array de 3 a 200 puntos o el handle de una polilínea cerrada")
        nombre = self._cadena(params, "nombre", 1, 100)
        assert nombre is not None
        saltos = "\n" in nombre or "\r" in nombre
        nombre = nombre.replace("\r\n", " ").replace("\r", " ").replace("\n", " ")
        altura = self._positivo(params, "altura_texto")
        decimales = self._entero(params, "decimales", 0, 3, 2)
        n_contorno = len(refs)
        if "etiqueta_en" in params:
            refs.append((_parse_punto_ref(params["etiqueta_en"], "etiqueta_en"), "etiqueta_en"))
        ancho = self._numero(params, "ancho_texto", 0.0) or 0.0
        if ancho < 0:
            raise _invalid("ancho_texto: debe ser un número mayor o igual que 0")
        own = {
            "contorno_handle": contorno_handle,
            "n_contorno": n_contorno,
            "etiqueta_en": "etiqueta_en" in params,
            "nombre": nombre,
            "saltos": saltos,
            "altura": altura,
            "decimales": decimales,
            "ancho": ancho,
            "estilo_param": params.get("estilo"),
            "capa_contorno": self._cadena(params, "capa_contorno", 1, 255, requerido=False),
            "capa_texto": self._cadena(params, "capa_texto", 1, 255, requerido=False),
        }
        return own, refs

    def _plan_recinto(
        self, own: dict[str, Any], puntos: list[dict[str, Any]], propias: list[str]
    ) -> tuple[list[dict[str, Any]], dict[str, Any]]:
        estilo = self._estilo({"estilo": own["estilo_param"]} if own["estilo_param"] is not None else {}, self.state.estilos_texto, "texto")
        if own["saltos"]:
            propias.append("nombre: los saltos de línea se sustituyeron por espacios.")
        plan: list[dict[str, Any]] = []
        if own["contorno_handle"] is not None:
            e = self._entidad_ref(own["contorno_handle"])
            verts = self._vertices_de(e) if e["tipo"] in ("LWPOLYLINE", "POLYLINE") else None
            cerrada = bool(e.get("cerrada")) or (verts is not None and len(verts) >= 4 and math.dist(verts[0], verts[-1]) <= 1e-9)
            if verts is None or not cerrada:
                raise self._invalid_data(
                    f"contorno: la entidad {e['handle']} no es una polilínea cerrada ({e['tipo']})", handle=e["handle"]
                )
            if math.dist(verts[0], verts[-1]) <= 1e-9:
                verts = verts[:-1]
            if e.get("tiene_arcos"):
                propias.append(
                    f"contorno: la polilínea {e['handle']} tiene arcos; el área y el centroide se calcularon sobre las cuerdas."
                )
            # PolygonMetrics.CollapseConsecutiveDuplicates: duplicados consecutivos colapsados con advertencia.
            xy, colapsados = _colapsar_duplicados([(v[0], v[1]) for v in verts])
            if colapsados:
                propias.append(advertencia_duplicados_colapsados(e["handle"], colapsados))
            anclas_extra = [_ancla_directa(e)]
        else:
            xy = [(pt["punto"][0], pt["punto"][1]) for pt in puntos[: own["n_contorno"]]]
            anclas_extra = []
        error = _validar_polilinea(xy, True, None, "contorno")
        if error:
            raise _invalid(error)
        area = abs(_shoelace(xy))
        if area <= 1e-9:
            raise _invalid_geometry("contorno: el área es 0")
        centroide = _centroide(xy)
        if own["etiqueta_en"]:
            p = puntos[-1]["punto"]
            etiqueta, en_centroide = (p[0], p[1]), False
        else:
            etiqueta, en_centroide = _punto_interior(xy)
        if own["contorno_handle"] is None:
            plan.append(self._polilinea_plan(xy, True, own["capa_contorno"], None))
        texto = f"{own['nombre']}\n{_formatear_area(area, own['decimales'])}"
        posicion = [etiqueta[0], etiqueta[1], 0.0]
        plan.append(
            {
                "tipo": "MTEXT",
                "posicion": posicion,
                "texto": texto,
                "altura": own["altura"],
                "ancho": own["ancho"],
                "estilo": estilo,
                "capa": own["capa_texto"],
                "concepto": "textos",
                "caja": _caja_texto(posicion, texto, own["altura"], own["ancho"]),
            }
        )
        numeros = {
            "area": area,
            "perimetro": _perimetro(xy, True),
            "centroide": [centroide[0], centroide[1], 0.0],
            "punto_etiqueta": posicion,
            "etiqueta_en_centroide": en_centroide,
            "n_vertices": len(xy),
            "_anclas_extra": anclas_extra,
        }
        return plan, numeros

    # -- offset (§5E.3.4) --

    def _parse_offset(self, params: dict[str, Any]) -> tuple[dict[str, Any], list[tuple[dict[str, Any], str]]]:
        handle = self._leer_handle(params, "handle")
        distancia = self._positivo(params, "distancia")
        lado = params.get("lado")
        refs: list[tuple[dict[str, Any], str]] = []
        if isinstance(lado, str):
            if lado not in ("izquierda", "derecha"):
                raise _invalid("lado: debe ser un PuntoRef o uno de izquierda, derecha")
        else:
            refs.append((_parse_punto_ref(lado, "lado"), "lado"))
        own = {
            "handle": handle,
            "distancia": distancia,
            "lado": lado if isinstance(lado, str) else None,
            "capa": self._cadena(params, "capa", 1, 255, requerido=False),
        }
        return own, refs

    def _plan_offset(
        self, own: dict[str, Any], puntos: list[dict[str, Any]], propias: list[str]
    ) -> tuple[list[dict[str, Any]], dict[str, Any]]:
        h, d = own["handle"], own["distancia"]
        e = self._entidad_ref(h)
        tipo = e["tipo"]
        if tipo not in ("LINE", "LWPOLYLINE", "POLYLINE", "CIRCLE", "ARC") or (
            tipo in ("LWPOLYLINE", "POLYLINE") and not isinstance(e.get("vertices"), list)
        ):
            raise self._invalid_data(f"handle: la entidad {h} ({tipo}) no es una curva: no admite offset", handle=h)
        curva = self._curva(e)
        assert curva is not None
        # Punto de lado: el dado, o el punto medio desplazado según la normal izquierda/derecha de la tangente.
        if own["lado"] is None:
            lado_punto = list(puntos[0]["punto"])
        else:
            m = curva["medio"]
            if tipo in ("CIRCLE", "ARC"):
                c = _p3(e["centro"])
                ang = math.atan2(m[1] - c[1], m[0] - c[0])
                tangente = (-math.sin(ang), math.cos(ang))
            else:
                verts = self._vertices_de(e) or []
                pts = verts + [verts[0]] if e.get("cerrada") else verts
                tangente = (pts[1][0] - pts[0][0], pts[1][1] - pts[0][1])
                for i in range(len(pts) - 1):
                    if _dist_segmento(tuple(m[:2]), tuple(pts[i][:2]), tuple(pts[i + 1][:2])) <= 1e-9:
                        tangente = (pts[i + 1][0] - pts[i][0], pts[i + 1][1] - pts[i][1])
                        break
            largo = math.hypot(*tangente)
            normal = (-tangente[1] / largo, tangente[0] / largo)
            signo = 1.0 if own["lado"] == "izquierda" else -1.0
            lado_punto = [m[0] + signo * normal[0] * d, m[1] + signo * normal[1] * d, m[2]]
        candidatos: list[tuple[float, dict[str, Any], Any]] = []
        lp = tuple(lado_punto[:2])
        # O3 (2026-10-27): punto lateral esperado = proyección del punto de lado sobre la original + d hacia el lado.
        lateral: tuple[float, float] | None = None
        if tipo in ("CIRCLE", "ARC"):
            c0 = _p3(e["centro"])
            vx, vy = lp[0] - c0[0], lp[1] - c0[1]
            nv = math.hypot(vx, vy)
            proy = (c0[0] + float(e["radio"]) * vx / nv, c0[1] + float(e["radio"]) * vy / nv) if nv > 1e-12 else None
        else:
            vs = [(v[0], v[1]) for v in (self._vertices_de(e) or [])]
            if e.get("cerrada") and tipo != "LINE" and vs:
                vs = vs + [vs[0]]
            proy = min((_proyectar_segmento(lp, vs[i], vs[i + 1]) for i in range(len(vs) - 1)),
                       key=lambda q: math.dist(q, lp), default=None)
        if proy is not None:
            wx, wy = lp[0] - proy[0], lp[1] - proy[1]
            nw = math.hypot(wx, wy)
            if nw > 1e-9:
                lateral = (proy[0] + d * wx / nw, proy[1] + d * wy / nw)
        if tipo in ("CIRCLE", "ARC"):
            c, r = _p3(e["centro"]), float(e["radio"])
            for r2 in (r + d, r - d):
                if r2 <= 1e-9:
                    continue
                nueva = copy.deepcopy(e)
                nueva.pop("handle", None)
                nueva["radio"] = r2
                nueva["bbox"] = _box3([[c[0] - r2, c[1] - r2], [c[0] + r2, c[1] + r2]])
                nueva["longitud"] = (e["longitud"] / r) * r2 if e.get("longitud") else 2 * math.pi * r2
                if tipo == "CIRCLE":
                    nueva["area"] = math.pi * r2 * r2
                candidatos.append(
                    (abs(math.dist(lp, c[:2]) - r2), nueva, lambda q, c=c, r2=r2: abs(math.dist(q, c[:2]) - r2))
                )
        else:
            verts = self._vertices_de(e) or []
            xy = [(v[0], v[1]) for v in verts]
            cerrada = bool(e.get("cerrada")) and tipo != "LINE"
            for off in (d, -d):
                nuevo = _desplazar_camino(xy, off, cerrada)
                if cerrada:
                    a0, a1 = _shoelace(xy), _shoelace(nuevo)
                    if abs(a1) <= 1e-9 or (a0 > 0) != (a1 > 0) or _primer_cruce(nuevo, True) is not None:
                        continue
                n = len(nuevo)
                m_ = n if cerrada else n - 1
                dist = min(_dist_segmento(lp, nuevo[i], nuevo[(i + 1) % n]) for i in range(m_))
                if tipo == "LINE":
                    nueva = _line("", e["capa"], [nuevo[0][0], nuevo[0][1], 0.0], [nuevo[1][0], nuevo[1][1], 0.0])
                else:
                    longitud, area = _longitud_area(nuevo, cerrada, None)
                    nueva = {
                        "tipo": tipo,
                        "capa": e["capa"],
                        "bbox": _box3(_box_of([list(v) for v in nuevo])),
                        "n_vertices": n,
                        "cerrada": cerrada,
                        "longitud": longitud,
                        "area": area,
                        "tiene_arcos": False,
                        "vertices": [[x, y, 0.0] for x, y in nuevo],
                    }
                nueva.pop("handle", None)
                candidatos.append(
                    (dist, nueva,
                     lambda q, nuevo=nuevo, n=n, m_=m_: min(_dist_segmento(q, nuevo[i], nuevo[(i + 1) % n]) for i in range(m_)))
                )
        if not candidatos:
            raise _invalid_geometry(f"offset: AutoCAD no pudo construir una curva paralela a {h} a distancia {_fmt3(d)}", handle=h)
        candidatos.sort(key=lambda c: c[0])
        resultado = candidatos[0][1]
        # O3: la elegida debe pasar a ≤ d/2 del punto lateral; si no, el lado pedido no es construible (OffsetChoice).
        if lateral is not None and not candidatos[0][2](lateral) <= d / 2:
            motivo = (
                f"la distancia supera o iguala el radio {_fmt_g(float(e['radio']))}" if tipo in ("CIRCLE", "ARC")
                else "AutoCAD no construye la curva paralela de ese lado"
            )
            raise _invalid_geometry(detalle_offset_lado(d, motivo), handle=h)
        if own["capa"] is None:
            self._capa_heredada_offset(h, e["capa"], propias)
        # PlannedOffset.CajaResultado (gate en vivo de Fase 4A, 2026-10-27): la caja de R4 y de numeros.bbox es la de la
        # curva ELEGIDA, no la de la original ampliada la distancia (que abarcaba las dos paralelas candidatas).
        caja_resultado = _bbox2d(resultado.get("bbox"))
        plan = [
            {
                "tipo": "OFFSET",
                "entidad": resultado,
                "origen": h,
                "capa": own["capa"] if own["capa"] is not None else e["capa"],
                "concepto": None,
                "caja": caja_resultado,
            }
        ]
        numeros = {
            "distancia": d,
            "longitud_original": e.get("longitud"),
            "lado_punto": lado_punto,
            "bbox": caja_resultado,
            "_anclas_extra": [_ancla_directa(e)],
        }
        return plan, numeros

    def _capa_heredada_offset(self, h: str, capa: str, propias: list[str]) -> None:
        """OffsetHandler.CheckInheritedLayer: R3 sobre la capa heredada del original (offset sin 'capa')."""
        if "|" in capa:
            raise self._invalid_data(detalle_capa_heredada(h, "dependiente de xref", capa), capa=capa, handle=h)
        if self._flag(self.state.capas_bloqueadas, capa):
            raise self._invalid_data(detalle_capa_heredada(h, "bloqueada", capa), capa=capa, handle=h)
        if self._flag(self.state.capas_congeladas, capa):
            raise self._invalid_data(detalle_capa_heredada(h, "congelada", capa), capa=capa, handle=h)
        if self._flag(self.state.capas_apagadas, capa):
            propias.append(
                f"offset: la curva {h} está en la capa apagada '{capa}' y la paralela hereda esa capa: no será visible "
                "(indique capa para otra capa de destino)."
            )

    # -- array_rectangular / array_polar (§5E.3.5) --

    def _proteger_handles(
        self, handles: list[str], confirmar: bool
    ) -> tuple[list[tuple[str, list[dict[str, Any]], dict[str, Any], bool]], list[tuple[str, str]], int]:
        """HandleGuard + EntityProtection (§5D.8, comprobaciones 1–5), compartido con mover/copiar/rotar/escalar/borrar."""
        # 1. Existencia.
        encontrados = []
        for h in handles:
            loc = self._localizar(h)
            if loc is None:
                raise _not_found(f"No existe ninguna entidad con handle '{h}'.", handle=h)
            encontrados.append(loc)
        # 2–3. Tipo y capa (EntityProtection.Classify).
        n_ocultas = 0
        for i, (espacio, _lst, e, es_atributo) in enumerate(encontrados):
            if es_atributo:
                raise self._invalid_data(f"handles[{i}]: es un atributo; use el handle del INSERT", handle=e["handle"])
            if e["tipo"] == "VIEWPORT" and espacio != "modelo":
                raise self._invalid_data(f"handles[{i}]: es un viewport de papel", handle=e["handle"])
            capa = e["capa"]
            if "|" in capa:
                raise self._invalid_data(
                    f"handles[{i}]: la entidad está en una capa dependiente de xref", handle=e["handle"], capa=capa
                )
            if self._flag(self.state.capas_bloqueadas, capa):
                raise self._invalid_data(f"handles[{i}]: la entidad está en una capa bloqueada", handle=e["handle"], capa=capa)
            if self._flag(self.state.capas_congeladas, capa) or self._flag(self.state.capas_apagadas, capa):
                n_ocultas += 1
        # 4. Mismo espacio.
        espacio0 = encontrados[0][0]
        for espacio, _lst, e, _a in encontrados[1:]:
            if espacio != espacio0:
                raise _invalid(
                    f"handles: las entidades deben estar en el mismo espacio ({encontrados[0][2]['handle']} en "
                    f"{espacio0}, {e['handle']} en {espacio})"
                )
        # 5. Protección de las entidades del usuario.
        usuario = [(e["handle"], e["capa"]) for _esp, _lst, e, _a in encontrados if not e["capa"].casefold().startswith("mcp-")]
        if usuario and not confirmar:
            ejemplos = ", ".join(f"{h} en {c}" for h, c in usuario[:5])
            raise self._invalid_data(
                f"confirmar: {len(usuario)} de las {len(encontrados)} entidades no están en capas MCP-* (p. ej. "
                f"{ejemplos}); pase confirmar: true para modificarlas",
                n_usuario=len(usuario),
                ejemplos=[{"handle": h, "capa": c} for h, c in usuario[:5]],
            )
        return encontrados, usuario, n_ocultas

    def _parse_array_comun(self, params: dict[str, Any]) -> dict[str, Any]:
        return {
            "handles": self._leer_handles(params, 100),
            "confirmar": self._bool(params, "confirmar", False),
            "capa": self._cadena(params, "capa", 1, 255, requerido=False),
        }

    def _parse_array_rectangular(self, params: dict[str, Any]) -> tuple[dict[str, Any], list[tuple[dict[str, Any], str]]]:
        own = self._parse_array_comun(params)
        filas = self._entero(params, "filas", 1, 100)
        columnas = self._entero(params, "columnas", 1, 100)
        for clave in ("sep_filas", "sep_columnas"):
            v = params.get(clave)
            if not _es_numero(v) or v == 0:
                raise _invalid(f"{clave}: no puede ser 0")
        if filas * columnas < 2:
            raise _invalid(DETALLE_ARRAY_POSICIONES)
        if filas * columnas - 1 > MAX_COPIAS_ARRAY:
            raise _invalid(DETALLE_ARRAY_TOPE.format(n=filas * columnas - 1))
        own.update(
            filas=filas,
            columnas=columnas,
            sep_filas=float(params["sep_filas"]),
            sep_columnas=float(params["sep_columnas"]),
            angulo=self._numero(params, "angulo", 0.0) or 0.0,
            con_base="base" in params,
        )
        refs = [(_parse_punto_ref(params["base"], "base"), "base")] if "base" in params else []
        return own, refs

    def _parse_array_polar(self, params: dict[str, Any]) -> tuple[dict[str, Any], list[tuple[dict[str, Any], str]]]:
        own = self._parse_array_comun(params)
        refs = [(_parse_punto_ref(params["centro"], "centro"), "centro")]
        n = self._entero(params, "n", 2, 501)
        total = params.get("angulo_total")
        if not _es_numero(total) or total == 0:
            raise _invalid("angulo_total: no puede ser 0")
        if n - 1 > MAX_COPIAS_ARRAY:
            raise _invalid(DETALLE_ARRAY_TOPE.format(n=n - 1))
        own.update(n=n, angulo_total=float(total), rotar=self._bool(params, "rotar_elementos", True), con_base="base" in params)
        if "base" in params:
            refs.append((_parse_punto_ref(params["base"], "base"), "base"))
        return own, refs

    def _plan_copias(
        self, own: dict[str, Any], posiciones: list[tuple[Any, float, dict[str, Any]]], propias: list[str]
    ) -> tuple[list[dict[str, Any]], dict[str, Any]]:
        encontrados = own["_encontrados"]
        plan: list[dict[str, Any]] = []
        for fn, giro, _info in posiciones:
            for _esp, _lst, e, _a in encontrados:
                copia = copy.deepcopy(e)
                copia.pop("handle", None)
                _transformar_entidad(copia, fn, giro)
                plan.append(
                    {
                        "tipo": "COPY",
                        "entidad": copia,
                        "origen": e["handle"],
                        "capa": own["capa"] if own["capa"] is not None else e["capa"],
                        "concepto": None,
                        "caja": _bbox2d(copia.get("bbox")),
                    }
                )
        capas: list[str] = []
        for _esp, _lst, e, _a in encontrados:
            if e["capa"] not in capas:
                capas.append(e["capa"])
        numeros = {
            "n_copias": len(posiciones),
            "n_posiciones": len(posiciones) + 1,
            "n_entidades": len(plan),
            "transformaciones": [info for _fn, _g, info in posiciones[:20]],
            "n_usuario": own["_n_usuario"],
            "capas": capas,
            "_anclas_extra": [_ancla_directa(e) for _esp, _lst, e, _a in encontrados],
        }
        return plan, numeros

    def _preparar_array(self, own: dict[str, Any], propias: list[str]) -> list[list[float]]:
        encontrados, usuario, n_ocultas = self._proteger_handles(own["handles"], own["confirmar"])
        if n_ocultas:
            propias.append(f"{n_ocultas} entidad(es) en capas congeladas o apagadas: el cambio no será visible.")
        own["_encontrados"], own["_n_usuario"] = encontrados, len(usuario)
        caja = _union2d([e.get("bbox") for _esp, _lst, e, _a in encontrados])
        if caja is None:
            raise _invalid("handles: las entidades no tienen caja calculable; indique base")
        return caja

    def _plan_array_rectangular(
        self, own: dict[str, Any], puntos: list[dict[str, Any]], propias: list[str]
    ) -> tuple[list[dict[str, Any]], dict[str, Any]]:
        caja = self._preparar_array(own, propias)
        base = puntos[0]["punto"] if own["con_base"] else [caja[0][0], caja[0][1], 0.0]
        a = math.radians(own["angulo"])
        posiciones = []
        for fila in range(own["filas"]):
            for col in range(own["columnas"]):
                if fila == 0 and col == 0:
                    continue
                vx, vy = col * own["sep_columnas"], fila * own["sep_filas"]
                dx, dy = vx * math.cos(a) - vy * math.sin(a), vx * math.sin(a) + vy * math.cos(a)

                def fn(p: list[float], dx: float = dx, dy: float = dy) -> list[float]:
                    q = _p3(p)
                    return [q[0] + dx, q[1] + dy, q[2]]

                posiciones.append((fn, 0.0, {"posicion": [fila, col], "desplazamiento": [dx, dy], "rotacion": 0.0}))
        return self._plan_copias(own, posiciones, propias)

    def _plan_array_polar(
        self, own: dict[str, Any], puntos: list[dict[str, Any]], propias: list[str]
    ) -> tuple[list[dict[str, Any]], dict[str, Any]]:
        caja = self._preparar_array(own, propias)
        centro = puntos[0]["punto"]
        base = puntos[1]["punto"] if own["con_base"] else [(caja[0][0] + caja[1][0]) / 2, (caja[0][1] + caja[1][1]) / 2, 0.0]
        n, total = own["n"], own["angulo_total"]
        paso = total / n if abs(abs(total) - 360.0) <= 1e-9 else total / (n - 1)
        posiciones = []
        for k in range(1, n):
            th = k * paso
            destino = _girar(base, th, centro)
            dx, dy = destino[0] - base[0], destino[1] - base[1]
            if own["rotar"]:

                def fn(p: list[float], th: float = th) -> list[float]:
                    return _girar(_p3(p), th, centro)

                giro = th
            else:

                def fn(p: list[float], dx: float = dx, dy: float = dy) -> list[float]:
                    q = _p3(p)
                    return [q[0] + dx, q[1] + dy, q[2]]

                giro = 0.0
            posiciones.append((fn, giro, {"posicion": k, "desplazamiento": [dx, dy], "rotacion": giro}))
        return self._plan_copias(own, posiciones, propias)

    # -- Fase L (§5F, limpieza de planos) ------------------------------------
    # Simula la FORMA de las respuestas y los errores del contrato (L1–L8) sobre el dibujo ficticio; el criterio de
    # dentro/fuera es el de §5F.1 por caja, el de purga es el grafo de referencias de la previsualización y el de
    # unidades reutiliza _bloques_auditoria (como el plugin reutiliza BloquesCheck).

    def _check_props_l(self, nombre: str, params: dict[str, Any]) -> None:
        schema = SCHEMAS_FASEL[nombre]
        for clave in params:
            if clave not in schema["properties"]:
                raise _invalid(f"{clave}: propiedad no permitida")
        for clave in schema.get("required", []):
            if clave not in params:
                raise _invalid(f"{clave}: obligatorio")

    def _modo(self, params: dict[str, Any]) -> str:
        """CleanWrite.ResolveMode (L1): 'preview' | 'write'."""
        confirmar = self._bool(params, "confirmar", False)
        preview = params.get("preview")
        if preview is not None and not isinstance(preview, bool):
            raise _invalid("preview: debe ser booleano")
        if not confirmar:
            if preview is False:
                raise _invalid(DETALLE_PREVIEW_SIN_CONFIRMAR)
            return "preview"
        return "preview" if preview is True else "write"

    def _cabecera_l(self, modo: str, propias: list[str]) -> dict[str, Any]:
        """CleanWrite.Response (L6): pid, preview, escrito, insunits, unidades, advertencias (§2.1/§2.2 + propias)."""
        units = self._units()
        units["advertencias"].extend(propias)
        return {"pid": self.state.pid, "preview": modo == "preview", "escrito": modo == "write", **units}

    def _check_units_l(self, modo: str) -> None:
        """CleanWrite.CheckUnits (L3, solo corregir_unidades_bloques desde 0.8.3): en escritura INSUNITS = 0 → UNITS_UNKNOWN."""
        if modo == "write" and self.state.insunits == 0:
            raise _units_unknown()

    @staticmethod
    def _lista_cadenas(params: dict[str, Any], key: str, a: int, b: int) -> list[str] | None:
        v = params.get(key)
        if v is None:
            return None
        if not isinstance(v, list) or not a <= len(v) <= b:
            raise _invalid(f"{key}: debe tener entre {a} y {b} elementos")
        for i, x in enumerate(v):
            if not isinstance(x, str) or not 1 <= len(x) <= 255:
                raise _invalid(f"{key}[{i}]: debe tener entre 1 y 255 caracteres")
        return v

    def _bloqueo_capa(self, nombre: str) -> str | None:
        if "|" in nombre:
            return "xref"
        if self._flag(self.state.capas_bloqueadas, nombre):
            return "bloqueada"
        return None

    # borrar_fuera_de (§5F.1)
    def _borrar_fuera_de(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        self._check_props_l("borrar_fuera_de", params)
        modo = self._modo(params)
        h = self._leer_handle(params, "contorno")
        criterio = params.get("criterio", "caja_cruza")
        if criterio not in CRITERIOS:
            raise _invalid(DETALLE_CRITERIO)
        muestra = self._entero(params, "muestra", 1, 50, 10)
        excluir = self._lista_cadenas(params, "excluir_capas", 1, 100) or []
        # L3 desde 0.8.3: sin rechazo por INSUNITS = 0 (solo advertencia de _units).
        propias: list[str] = []

        # Contorno (§5F.1).
        loc = self._find_entity(h)
        if loc is None:
            if any(c["handle"] == h for d in self.state.block_defs for c in d.get("_contenido", [])):
                raise _invalid(f"contorno: la entidad {h} está dentro de una definición de bloque")
            raise _not_found(f"No existe ninguna entidad con handle '{h}'.", handle=h)
        espacio_c, contorno = loc
        if params.get("espacio") is not None:
            pedido = self._cadena(params, "espacio", 1, 255)
            espacio_p, _ents = self._space({"espacio": pedido})
            if espacio_p != espacio_c:
                raise _invalid(f"contorno: la entidad {h} no está en el espacio '{pedido}'")
        tipo = contorno["tipo"]
        if tipo == "CIRCLE":
            cx, cy = contorno["centro"][0], contorno["centro"][1]
            poly = poligono_circulo(cx, cy, float(contorno["radio"]))
            n_vertices = LADOS_CIRCULO
            area = math.pi * float(contorno["radio"]) ** 2
        elif tipo == "LWPOLYLINE":
            verts = self._vertices_de(contorno)
            if verts is None:
                b = contorno["bbox"]
                verts = [[b[0][0], b[0][1]], [b[1][0], b[0][1]], [b[1][0], b[1][1]], [b[0][0], b[1][1]]]
            xy = [(float(v[0]), float(v[1])) for v in verts]
            cierre_explicito = len(xy) >= 4 and math.dist(xy[0], xy[-1]) <= 1e-9
            if contorno.get("cerrada") is not True and not cierre_explicito:
                raise _invalid(f"contorno: la polilínea {h} no está cerrada")
            if cierre_explicito:
                xy = xy[:-1]  # primer = último: el cierre no es un vértice duplicado
            curvos = sum(1 for b in contorno.get("_bulges", []) if abs(float(b)) > 1e-12)
            if curvos:
                propias.append(advertencia_tramos_curvos(curvos))
            poly, quitados = _colapsar_duplicados(xy)
            if quitados and len(poly) >= 3:
                propias.append(advertencia_duplicados_colapsados(h, quitados))
            if len(poly) < 3:
                raise _invalid_geometry("contorno: se necesitan al menos 3 vértices", handle=h)
            cruce = _primer_cruce(poly, True)
            if cruce is not None:
                raise _invalid_geometry(f"contorno: la polilínea se autointerseca (tramos {cruce[0]} y {cruce[1]})", handle=h)
            area = abs(_shoelace(poly))
            if area <= 1e-12:
                raise _invalid_geometry("contorno: área 0", handle=h)
            n_vertices = len(poly)
        else:
            raise _invalid(f"contorno: la entidad {h} no es una polilínea cerrada ni un círculo ({tipo})")

        capas_existentes = {c["nombre"].casefold() for c in self._layers()}
        faltan = [c for c in excluir if c.casefold() not in capas_existentes]
        if faltan:
            raise _not_found(f"excluir_capas: capas inexistentes: {', '.join(faltan)}", capas=faltan)
        excluir_cf = {c.casefold() for c in excluir}

        # Inventario (lectura).
        _esp, entities = self._space({"espacio": espacio_c})
        borrar: list[dict[str, Any]] = []
        conservar: list[dict[str, Any]] = []
        sin_caja: list[dict[str, Any]] = []
        excluidas: list[dict[str, Any]] = []
        viewports_conservados = 0
        n_cruzan = 0
        for e in entities:
            if e["handle"] == contorno["handle"]:
                continue
            if e.get("bbox") is None:
                sin_caja.append(e)
                continue
            rel = clasificar_caja(poly, e["bbox"])
            if rel == "cruza":
                n_cruzan += 1
            se_borra = rel == "fuera" or (rel == "cruza" and criterio == "caja_dentro")
            if se_borra and e["tipo"] == "VIEWPORT" and espacio_c != "modelo":
                viewports_conservados += 1
                conservar.append(e)
                excluidas.append(e)
            elif se_borra and e["capa"].casefold() in excluir_cf:
                conservar.append(e)
                excluidas.append(e)
            elif se_borra:
                borrar.append(e)
            else:
                conservar.append(e)

        bloqueadas: dict[str, int] = {}
        congeladas = 0
        for e in borrar:
            if self._bloqueo_capa(e["capa"]) is not None:
                bloqueadas[e["capa"]] = bloqueadas.get(e["capa"], 0) + 1
            elif self._flag(self.state.capas_congeladas, e["capa"]) or self._flag(self.state.capas_apagadas, e["capa"]):
                congeladas += 1
        if bloqueadas:
            orden = dict(sorted(bloqueadas.items(), key=lambda kv: (-kv[1], kv[0].casefold())))
            raise self._invalid_data(detalle_capas_bloqueadas(orden), capas=list(orden))

        viewports = []
        if espacio_c == "modelo":
            for vp in self.state.viewports_layout:
                ventana = vp["ventana"]
                nb = sum(1 for e in borrar if _bbox_hits(e["bbox"], ventana))
                nc = sum(1 for e in conservar if _bbox_hits(e["bbox"], ventana))
                estado = estado_viewport(nb, nc)
                viewports.append(
                    {
                        "layout": vp["layout"],
                        "handle": vp["handle"],
                        "ventana": copy.deepcopy(ventana),
                        "estado": estado,
                        "n_borradas": nb,
                        "n_conservadas": nc,
                    }
                )
                aviso = advertencia_viewport(vp["layout"], vp["handle"], estado, ventana, nb, nc)
                if aviso is not None:
                    propias.append(aviso)
        if sin_caja:
            propias.append(advertencia_sin_caja(_conteo_ordenado([e["tipo"] for e in sin_caja])))
        if viewports_conservados:
            propias.append(advertencia_viewports_conservados(viewports_conservados, espacio_c))
        if congeladas:
            propias.append(advertencia_congeladas_borradas(congeladas))

        n_borradas = None
        escritura_ms = None
        if modo == "write":
            quitar = {e["handle"] for e in borrar}
            entities[:] = [e for e in entities if e["handle"] not in quitar]
            self.state.pasos_deshacer += 1
            n_borradas = len(quitar)
            escritura_ms = 0
        caja = contorno["bbox"]
        return {
            **self._cabecera_l(modo, propias),
            "contorno": {
                "handle": contorno["handle"],
                "tipo": tipo,
                "capa": contorno["capa"],
                "n_vertices": n_vertices,
                "area": area,
                "bbox": [[caja[0][0], caja[0][1]], [caja[1][0], caja[1][1]]],
            },
            "espacio": espacio_c,
            "criterio": criterio,
            "n_total": len(borrar) + len(conservar) + len(sin_caja),
            "n_borrar": len(borrar),
            "n_conservar": len(conservar),
            "n_cruzan": n_cruzan,
            "n_sin_caja": len(sin_caja),
            "n_excluidas": len(excluidas),
            "n_borradas": n_borradas,
            "por_tipo": _conteo_ordenado([e["tipo"] for e in borrar]),
            "por_capa": _conteo_ordenado([e["capa"] for e in borrar]),
            "excluidas_por_capa": _conteo_ordenado([e["capa"] for e in excluidas]),
            "muestra": [e["handle"] for e in borrar[:muestra]],
            "viewports": viewports,
            "duracion_ms": {"lectura": 0, "escritura": escritura_ms},
            "origen_texto": ORIGEN_TEXTO,
        }

    # purgar (§5F.2)
    def _purga_contexto(self) -> dict[str, str]:
        st = self.state
        return {
            "capas": st.capa_actual,
            "tipos_linea": st.tipo_linea_actual,
            "estilos_texto": st.estilos_texto[0] if st.estilos_texto else "Standard",
            "estilos_cota": st.estilos_cota[0] if st.estilos_cota else "Standard",
        }

    def _purga_protegido(self, cat: str, nombre: str, es_xref: bool) -> bool:
        """PurgeRules.IsProtected sin 'excluir' (exclusiones fijas)."""
        cf = nombre.casefold()
        actual = self._purga_contexto().get(cat, "").casefold()
        if cat == "capas":
            return cf in ("0", "defpoints") or cf == actual or cf.startswith("mcp-")
        if cat == "tipos_linea":
            return cf in ("bylayer", "byblock", "continuous") or cf == actual
        if cat in ("estilos_texto", "estilos_cota"):
            return cf == "standard" or cf == actual
        if cat == "bloques":
            return cf == "*model_space" or cf.startswith("*paper_space") or es_xref
        if cat == "regapps":
            return cf == "acad"
        return False

    def _purga_nodos(self) -> dict[tuple[str, str], dict[str, Any]]:
        st = self.state
        nodos: dict[tuple[str, str], dict[str, Any]] = {}
        for d in st.block_defs:
            nodos[("bloques", d["nombre"].casefold())] = {"cat": "bloques", "nombre": d["nombre"], "def": d}
        for c in self._layers():
            nodos[("capas", c["nombre"].casefold())] = {"cat": "capas", "nombre": c["nombre"], "capa": c}
        for t in st.tipos_linea:
            nodos[("tipos_linea", t.casefold())] = {"cat": "tipos_linea", "nombre": t}
        for t in st.estilos_texto:
            nodos[("estilos_texto", t.casefold())] = {"cat": "estilos_texto", "nombre": t}
        for t in st.estilos_cota:
            nodos[("estilos_cota", t.casefold())] = {"cat": "estilos_cota", "nombre": t}
        return nodos

    def _purga_aristas(self) -> list[tuple[tuple[str, str] | None, tuple[str, str]]]:
        """Aristas 'quien referencia → referenciado' (None = raíz: los espacios)."""
        aristas: list[tuple[tuple[str, str] | None, tuple[str, str]]] = []

        def de_entidad(src: tuple[str, str] | None, e: dict[str, Any]) -> None:
            aristas.append((src, ("capas", str(e["capa"]).casefold())))
            if e.get("tipo") == "INSERT" and e.get("nombre"):
                aristas.append((src, ("bloques", str(e["nombre"]).casefold())))
            tl = e.get("_tipo_linea")
            if tl and str(tl).casefold() not in ("bylayer", "byblock"):
                aristas.append((src, ("tipos_linea", str(tl).casefold())))
            if e.get("_estilo"):
                aristas.append((src, ("estilos_texto", str(e["_estilo"]).casefold())))
            if e.get("_estilo_cota"):
                aristas.append((src, ("estilos_cota", str(e["_estilo_cota"]).casefold())))
            for a in e.get("_atributos", []):
                de_entidad(src, a)

        for _esp, entities in self._all_spaces():
            for e in entities:
                de_entidad(None, e)
        for d in self.state.block_defs:
            for e in d.get("_contenido", []):
                de_entidad(("bloques", d["nombre"].casefold()), e)
        for c in self._layers():
            aristas.append((("capas", c["nombre"].casefold()), ("tipos_linea", str(c["tipo_linea"]).casefold())))
        return aristas

    def _purga_punto_fijo(
        self, cats: list[str], excluir_cf: set[str], retener: set[tuple[str, str]]
    ) -> tuple[list[list[tuple[str, str]]], dict[tuple[str, str], dict[str, Any]], set[tuple[str, str]]]:
        """PurgeRules.FixedPoint sobre el grafo: rondas, nodos y referenciados al final. 'retener' = raíces extra."""
        nodos = self._purga_nodos()
        aristas = self._purga_aristas()
        candidatos = {
            k
            for k, n in nodos.items()
            if n["cat"] in cats
            and k not in retener
            and n["nombre"].casefold() not in excluir_cf
            and not self._purga_protegido(n["cat"], n["nombre"], bool(n.get("def", {}).get("es_xref")))
        }
        vivos = set(nodos)
        rondas: list[list[tuple[str, str]]] = []
        while True:
            referenciados = {hacia for desde, hacia in aristas if desde is None or desde in vivos}
            ronda = sorted(
                (k for k in candidatos if k in vivos and k not in referenciados),
                key=lambda k: (CATEGORIAS_PURGA.index(k[0]), k[1]),
            )
            if not ronda:
                break
            rondas.append(ronda)
            vivos -= set(ronda)
        return rondas, nodos, referenciados

    def _purga_regapps(self, excluir_cf: set[str]) -> list[str]:
        usadas = {r.casefold() for r in self.state.regapps_usadas}
        return [
            r
            for r in self.state.regapps
            if r.casefold() != "acad" and r.casefold() not in excluir_cf and r.casefold() not in usadas
        ]

    def _purga_inventario(
        self, cats: list[str], rondas: list[list[tuple[str, str]]], nodos: dict[tuple[str, str], dict[str, Any]],
        regapps: list[str], muestra: int,
    ) -> dict[str, Any]:
        por_cat: dict[str, list[tuple[int, dict[str, Any]]]] = {c: [] for c in cats}
        for i, ronda in enumerate(rondas, start=1):
            for k in ronda:
                por_cat[k[0]].append((i, nodos[k]))
        out: dict[str, Any] = {}
        for cat in cats:
            if cat == "regapps":
                filas = [{"nombre": r, "ronda": 1} for r in sorted(regapps, key=str.casefold)]
                out[cat] = {"n": len(filas), "nombres": filas[:muestra], "nombres_truncados": len(filas) > muestra}
            elif cat == "bloques":
                filas = [
                    {
                        "nombre": n["nombre"],
                        "n_entidades": n["def"]["n_entidades"],
                        "ronda": r,
                        "unidades": n["def"]["unidades_bloque"],
                    }
                    for r, n in por_cat[cat]
                ]
                filas.sort(key=lambda f: (f["ronda"], -f["n_entidades"], f["nombre"].casefold()))
                anonimos = sum(1 for _r, n in por_cat[cat] if n["def"]["anonimo"])
                out[cat] = {
                    "n": len(filas),
                    "n_con_nombre": len(filas) - anonimos,
                    "n_anonimos": anonimos,
                    "n_entidades": sum(f["n_entidades"] for f in filas),
                    "nombres": filas[:muestra],
                    "nombres_truncados": len(filas) > muestra,
                }
            else:
                filas = [{"nombre": n["nombre"], "ronda": r} for r, n in por_cat[cat]]
                filas.sort(key=lambda f: (f["ronda"], f["nombre"].casefold()))
                out[cat] = {"n": len(filas), "nombres": filas[:muestra], "nombres_truncados": len(filas) > muestra}
        return out

    def _purgar(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        self._check_props_l("purgar", params)
        modo = self._modo(params)
        pedidas = params.get("categorias", ["todo"])
        if not isinstance(pedidas, list) or not 1 <= len(pedidas) <= 7:
            raise _invalid("categorias: debe tener entre 1 y 7 elementos")
        for i, c in enumerate(pedidas):
            if c not in CATEGORIAS_PURGA + ["todo"]:
                raise _invalid(detalle_categoria(i))
        cats = list(CATEGORIAS_PURGA) if "todo" in pedidas else [c for c in CATEGORIAS_PURGA if c in pedidas]
        excluir = self._lista_cadenas(params, "excluir", 1, 200) or []
        muestra = self._entero(params, "muestra", 1, 200, 50)
        # L3 desde 0.8.3: sin rechazo por INSUNITS = 0 (solo advertencia de _units).
        propias: list[str] = []
        nodos_todos = self._purga_nodos()
        nombres_todos = {k[1] for k in nodos_todos} | {r.casefold() for r in self.state.regapps}
        for x in excluir:
            if x.casefold() not in nombres_todos:
                propias.append(advertencia_excluir_inexistente(x))
        excluir_cf = {x.casefold() for x in excluir}

        rondas, nodos, referenciados = self._purga_punto_fijo(cats, excluir_cf, set())
        regapps = self._purga_regapps(excluir_cf) if "regapps" in cats else []
        purgables = self._purga_inventario(cats, rondas, nodos, regapps, muestra)

        # protegidos / excluidos: símbolos sin referencia viva al final que no se purgan por las exclusiones.
        ctx = self._purga_contexto()
        purgados_prev = {k for r in rondas for k in r}
        protegidos: dict[str, list[str]] = {c: [] for c in cats}
        excluidos: list[str] = []
        for k, n in sorted(nodos.items(), key=lambda kv: kv[1]["nombre"].casefold()):
            if n["cat"] not in cats or k in purgados_prev or k in referenciados:
                continue
            es_xref = bool(n.get("def", {}).get("es_xref"))
            if self._purga_protegido(n["cat"], n["nombre"], es_xref):
                sufijo = " (actual)" if n["nombre"].casefold() == ctx.get(n["cat"], "").casefold() else ""
                protegidos[n["cat"]].append(n["nombre"] + sufijo)
            elif n["nombre"].casefold() in excluir_cf:
                excluidos.append(n["nombre"])
        if "regapps" in cats:
            usadas = {r.casefold() for r in self.state.regapps_usadas}
            for r in sorted(self.state.regapps, key=str.casefold):
                if r.casefold() in usadas:
                    continue
                if r.casefold() == "acad":
                    protegidos["regapps"].append(r)
                elif r.casefold() in excluir_cf:
                    excluidos.append(r)

        purgados = None
        n_rondas = max(len(rondas), 1 if regapps else 0)
        escritura_ms = None
        if modo == "write":
            # Database.Purge iterado: conserva además las definiciones con nombre de bloques dinámicos (las referencian
            # sus representaciones anónimas aunque no tengan inserciones directas).
            retener = {
                ("bloques", d["nombre"].casefold())
                for d in self.state.block_defs
                if d.get("dinamico") and not d["anonimo"]
            }
            reales, nodos_r, _ref = self._purga_punto_fijo(cats, excluir_cf, retener)
            purgados = self._purga_inventario(cats, reales, nodos_r, regapps, muestra)
            hechos = {k for r in reales for k in r}
            no_purgados = len(purgados_prev - hechos)
            no_previstos = len(hechos - purgados_prev)
            if no_purgados or no_previstos:
                propias.append(advertencia_purga_difiere(no_purgados, no_previstos))
            self._aplicar_purga(hechos, nodos_r, regapps)
            self.state.pasos_deshacer += 1
            n_rondas = max(len(reales), 1 if regapps else 0)
            escritura_ms = 0
        return {
            **self._cabecera_l(modo, propias),
            "categorias": cats,
            "rondas": n_rondas,
            "purgables": purgables,
            "protegidos": protegidos,
            "excluidos": excluidos,
            "purgados": purgados,
            "duracion_ms": {"lectura": 0, "escritura": escritura_ms},
            "origen_texto": ORIGEN_TEXTO,
        }

    def _aplicar_purga(self, hechos: set[tuple[str, str]], nodos: dict[tuple[str, str], dict[str, Any]], regapps: list[str]) -> None:
        st = self.state
        quitar = {cat: {k[1] for k in hechos if k[0] == cat} for cat in CATEGORIAS_PURGA}
        st.block_defs[:] = [d for d in st.block_defs if d["nombre"].casefold() not in quitar["bloques"]]
        st.capas_borradas |= quitar["capas"]
        st.tipos_linea[:] = [t for t in st.tipos_linea if t.casefold() not in quitar["tipos_linea"]]
        st.estilos_texto[:] = [t for t in st.estilos_texto if t.casefold() not in quitar["estilos_texto"]]
        st.estilos_cota[:] = [t for t in st.estilos_cota if t.casefold() not in quitar["estilos_cota"]]
        fuera = {r.casefold() for r in regapps}
        st.regapps[:] = [r for r in st.regapps if r.casefold() not in fuera]

    # normalizar_capas (§5F.3)
    def _normalizar_capas(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        self._check_props_l("normalizar_capas", params)
        modo = self._modo(params)
        mapa = params.get("mapa")
        # Mismos textos que NormalizarCapasHandler.ReadMapa.
        if not isinstance(mapa, dict) or not 1 <= len(mapa) <= 200:
            raise _invalid("mapa: debe ser un objeto con 1 a 200 pares capa_origen → destino")
        for k, v in mapa.items():
            if not 1 <= len(k) <= 255:
                raise _invalid("mapa: cada capa origen debe tener entre 1 y 255 caracteres")
            if not isinstance(v, str):
                raise _invalid(f"mapa['{k}']: debe ser una cadena")
            if not 1 <= len(v) <= 255:
                raise _invalid(f"mapa['{k}']: debe tener entre 1 y 255 caracteres")
        espacios_param = params.get("espacios")
        if espacios_param is not None:
            if not isinstance(espacios_param, list) or not 1 <= len(espacios_param) <= 100:
                raise _invalid(DETALLE_ESPACIOS_FORMA)
            for i, x in enumerate(espacios_param):
                if not isinstance(x, str) or not 1 <= len(x) <= 255:
                    raise _invalid(detalle_espacios_item(i))
            vistos_esp: set[str] = set()
            for x in espacios_param:
                if x.casefold() in vistos_esp:
                    raise _invalid(detalle_espacio_repetido(x))
                vistos_esp.add(x.casefold())
        incluir_viewports = self._bool(params, "incluir_viewports", False)
        incluir_bloques = self._bool(params, "incluir_bloques", False)
        borrar_vacias = self._bool(params, "borrar_capas_origen_vacias", False)
        conservar_aspecto = self._bool(params, "conservar_aspecto", False)
        # L3 desde 0.8.3: sin rechazo por INSUNITS = 0 (solo advertencia de _units).
        st = self.state
        prefs: dict[str, str] = LPS.get("capas_preferidas") or {}

        # Validación del mapa (LayerMapValidator.Validate, orden 1–6).
        pares = list(mapa.items())
        if any(not o.strip() for o, _d in pares):
            raise _invalid("mapa: la capa origen no puede estar vacía")
        faltan = [o for o, _d in pares if self._find_layer(o) is None]
        if faltan:
            detalle = f"mapa: capas de origen inexistentes: {', '.join(faltan)}"
            raise _not_found(detalle, capas=faltan, detalle=detalle)
        # Comprobación 2, en el orden de LayerMapValidator: DEFPOINTS, origen del MCP, "0" con incluir_bloques y
        # repetidos, cada una sobre todo el mapa.
        for o, _d in pares:
            if o.casefold() == "defpoints":
                raise _invalid("mapa: la capa DEFPOINTS no se normaliza")
        for o, _d in pares:
            real = self._find_layer(o)["nombre"]  # type: ignore[index]
            if es_capa_mcp(real):
                raise self._invalid_data(detalle_origen_mcp(o, real), capa=real)
        if incluir_bloques and any(o == "0" for o, _d in pares):
            raise self._invalid_data(
                "mapa: la capa 0 no puede ser origen con incluir_bloques: true (la capa 0 dentro de los bloques es correcta)",
                capa="0",
            )
        vistos: set[str] = set()
        for o, _d in pares:
            if o.casefold() in vistos:
                raise _invalid(f"mapa: origen repetido '{o}'")
            vistos.add(o.casefold())
        entradas: list[dict[str, Any]] = []
        for o, d in pares:
            real = self._find_layer(o)["nombre"]  # type: ignore[index]
            # Nombre no válido como símbolo (LayerMapValidator.IsValidDestinationName): antes del rechazo del MCP y del
            # NOT_FOUND; la barra vertical solo se admite en una capa existente (de xref: comprobación 6).
            if (
                not d.strip()
                or d.strip() != d
                or any(ch in CARACTERES_NO_VALIDOS_CAPA for ch in d)
                or ("|" in d and self._find_layer(d) is None)
            ):
                raise self._invalid_data(DETALLE_NOMBRE_CAPA_INVALIDO, capa=d)
            clave = next((k for k in prefs if k.casefold() == d.casefold()), None)
            nombre = prefs[clave] if clave is not None else d
            existente = self._find_layer(nombre)
            if existente is not None:
                nombre = existente["nombre"]
            # Destino del MCP (exista o no, por nombre o por concepto): antes que el NOT_FOUND (§5F.3, comprobación 3).
            if es_capa_mcp(nombre):
                raise self._invalid_data(detalle_destino_mcp(o, nombre), capa=nombre)
            spec = None
            if existente is None:
                spec = self._spec_capa(nombre)
                if spec is None or "|" in nombre:
                    # Como el plugin (NormalizarCapasHandler.ToToolException): mensaje de capa inexistente y el texto
                    # de LayerMapValidator.MissingDestination en data.detalle.
                    raise _not_found(
                        f"La capa '{nombre}' no existe en el dibujo.", capa=nombre, detalle=detalle_destino_no_creable(o, nombre)
                    )
            entradas.append(
                {
                    "escrito": o,
                    "origen": real,
                    "destino": existente["nombre"] if existente is not None else spec["nombre"],  # type: ignore[index]
                    "destino_concepto": clave,
                    "destino_existe": existente is not None,
                    "spec": spec,
                }
            )
        for e in entradas:
            if e["origen"].casefold() == e["destino"].casefold():
                raise self._invalid_data(f"mapa['{e['escrito']}']: origen y destino son la misma capa", capa=e["origen"])
        origenes_cf = {e["origen"].casefold() for e in entradas}
        for e in entradas:
            if e["destino"].casefold() in origenes_cf:
                raise _invalid(detalle_cadena(e["destino"]))
        for e in entradas:
            # Mismo orden que LayerMapValidator.StateError: bloqueada → congelada → xref.
            for capa in (e["origen"], e["destino"]) if e["destino_existe"] else (e["origen"],):
                if self._flag(st.capas_bloqueadas, capa):
                    raise self._invalid_data("capa bloqueada", capa=capa)
                if self._flag(st.capas_congeladas, capa):
                    raise self._invalid_data("capa congelada", capa=capa)
                if "|" in capa:
                    raise self._invalid_data("capa dependiente de xref", capa=capa)

        # espacios (LayerMapScope.TryResolve), después de validar el mapa: layouts inexistentes → NOT_FOUND con todos.
        espacios: list[str] | None = None
        if espacios_param is not None:
            por_cf = {l.casefold(): l for l in LAYOUTS}
            faltan_esp = [x for x in espacios_param if x.casefold() != "modelo" and x.casefold() not in por_cf]
            if faltan_esp:
                detalle = detalle_espacios_inexistentes(faltan_esp, list(LAYOUTS))
                raise _not_found(detalle, espacios=faltan_esp, layouts=list(LAYOUTS), detalle=detalle)
            espacios = ["modelo" if x.casefold() == "modelo" else por_cf[x.casefold()] for x in espacios_param]
        incluidos_cf = None if espacios is None else {x.casefold() for x in espacios}

        # Inventario: qué entidades se reasignan.
        propias: list[str] = []
        destino_de = {e["origen"].casefold(): e for e in entradas}
        for e in entradas:
            for capa in (e["origen"], e["destino"]) if e["destino_existe"] else (e["origen"],):
                if self._flag(st.capas_apagadas, capa):
                    propias.append(f"La capa {capa} está apagada: las entidades no serán visibles.")
        afectadas: list[tuple[dict[str, Any], str]] = []  # (entidad, origen real)
        por_origen: dict[str, dict[str, Any]] = {
            e["origen"]: {"tipos": [], "espacios": [], "bloques": [], "excl_espacio": [], "excl_vp": [], "excl_papel": []}
            for e in entradas
        }
        excluidas_ids: set[int] = set()
        vp_excluidos: dict[str, int] = {}
        forzadas = 0

        def tocar(ent: dict[str, Any], tipo: str, espacio: str | None, bloque: str | None) -> None:
            nonlocal forzadas
            e = destino_de[ent["capa"].casefold()]
            afectadas.append((ent, e["origen"]))
            acc = por_origen[e["origen"]]
            acc["tipos"].append(tipo)
            if espacio is not None:
                acc["espacios"].append(espacio)
            if bloque is not None:
                acc["bloques"].append(f"bloque:{bloque}")
            color = ent.get("_color")
            tl = ent.get("_tipo_linea")
            if (color is not None and color != 256) or (tl is not None and str(tl).casefold() != "bylayer"):
                forzadas += 1

        def excluir(ent: dict[str, Any], clave: str, espacio: str) -> None:
            por_origen[destino_de[ent["capa"].casefold()]["origen"]][clave].append(espacio)
            excluidas_ids.add(id(ent))

        for espacio, entities in self._all_spaces():
            incluido = incluidos_cf is None or espacio.casefold() in incluidos_cf
            es_layout = espacio != "modelo"
            papel_id = id(_viewport_papel(entities)) if es_layout else None
            for ent in entities:
                es_vp = es_layout and ent["tipo"] == "VIEWPORT"
                papel = es_vp and id(ent) == papel_id
                if ent["capa"].casefold() in destino_de:
                    # LayerMapScope.Classify: espacio excluido → viewport n.º 1 → otro viewport sin incluir_viewports.
                    if not incluido:
                        excluir(ent, "excl_espacio", espacio)
                    elif es_vp and papel:
                        excluir(ent, "excl_papel", espacio)
                    elif es_vp and not incluir_viewports:
                        excluir(ent, "excl_vp", espacio)
                        vp_excluidos[espacio] = vp_excluidos.get(espacio, 0) + 1
                    else:
                        tocar(ent, ent["tipo"], espacio, None)
                for a in ent.get("_atributos", []):
                    if a["capa"].casefold() in destino_de:
                        if incluido:
                            tocar(a, "ATTRIB", espacio, None)
                        else:
                            excluir(a, "excl_espacio", espacio)
        anonimos_dinamicos = 0
        if incluir_bloques:
            for d in st.block_defs:
                contenido = d.get("_contenido", [])
                if d["anonimo"]:
                    if any(c["capa"].casefold() in destino_de for c in contenido):
                        anonimos_dinamicos += 1
                    continue
                if d["es_xref"]:
                    continue
                for c in contenido:
                    if c["capa"].casefold() in destino_de:
                        tocar(c, c["tipo"], None, d["nombre"])
        # Capas origen que quedarían vacías (modelo, layouts —también lo excluido— y TODOS los bloques, anónimos
        # incluidos). Mismo orden de advertencias que el plugin: capas protegidas, viewports, anónimos, forzadas.
        capas_borrar: list[str] = []
        if borrar_vacias:
            reasignadas = {id(ent) for ent, _o in afectadas}
            usadas: set[str] = set()
            for _esp, entities in self._all_spaces():
                for ent in entities:
                    if id(ent) not in reasignadas:
                        usadas.add(ent["capa"].casefold())
                    for a in ent.get("_atributos", []):
                        if id(a) not in reasignadas:
                            usadas.add(a["capa"].casefold())
            for d in st.block_defs:
                for c in d.get("_contenido", []):
                    if id(c) not in reasignadas:
                        usadas.add(c["capa"].casefold())
            for e in entradas:
                o = e["origen"]
                if o.casefold() in usadas:
                    continue
                if o.casefold() in ("0", "defpoints") or o.casefold() == st.capa_actual.casefold():
                    propias.append(advertencia_origen_no_borrable(o))
                    continue
                capas_borrar.append(o)
        aviso_vp = advertencia_viewports_excluidos([(l, vp_excluidos.get(l, 0)) for l in LAYOUTS])
        if aviso_vp:
            propias.append(aviso_vp)
        if anonimos_dinamicos:
            propias.append(advertencia_anonimos_dinamicos(anonimos_dinamicos))
        if forzadas:
            propias.append(advertencia_propiedades_forzadas(forzadas))
        # conservar_aspecto (§5G.5): LayerAppearance.Compute por entidad a reasignar.
        arreglos: list[tuple[dict[str, Any], dict[str, Any]]] = []
        n_pierden_no_plot = 0
        for ent, origen in afectadas:
            entrada = destino_de[origen.casefold()]
            aspecto_destino = self._find_layer(entrada["destino"]) or self._nueva_capa(entrada["spec"])[0]
            arreglo = calcular_aspecto(ent, self._find_layer(origen), aspecto_destino)
            if arreglo["pierde_no_plot"]:
                n_pierden_no_plot += 1
            if arreglo["fijar"]:
                arreglos.append((ent, arreglo["fijar"]))
        if not conservar_aspecto and arreglos:
            propias.append(advertencia_cambiarian_aspecto(len(arreglos)))

        capas_creadas: list[str] = []
        capas_borradas = None
        n_reasignadas = None
        n_aspecto_fijado = None
        escritura_ms = None
        if modo == "write":
            for e in entradas:
                if not e["destino_existe"] and e["destino"] not in capas_creadas:
                    nueva, adv = self._nueva_capa(e["spec"])
                    st.capas_creadas.append(nueva)
                    capas_creadas.append(nueva["nombre"])
                    propias.extend(adv)
            n_aspecto_fijado = 0
            if conservar_aspecto:
                for ent, fijar in arreglos:
                    ent.update(fijar)
                n_aspecto_fijado = len(arreglos)
                if n_aspecto_fijado or n_pierden_no_plot:
                    propias.append(advertencia_aspecto_fijado(n_aspecto_fijado, n_pierden_no_plot))
            for ent, origen in afectadas:
                ent["capa"] = destino_de[origen.casefold()]["destino"]
            st.capas_borradas |= {c.casefold() for c in capas_borrar}
            capas_borradas = list(capas_borrar)
            n_reasignadas = len(afectadas)
            st.pasos_deshacer += 1
            escritura_ms = 0
        n_por_origen = {o: len(v["tipos"]) for o, v in por_origen.items()}
        filas_mapa = [
            {
                "origen": e["origen"],
                "destino": e["destino"],
                "destino_concepto": e["destino_concepto"],
                "destino_existe": e["destino_existe"],
                "destino_creada": e["destino"] in capas_creadas,
                "n": n_por_origen[e["origen"]],
            }
            for e in entradas
        ]
        salida_por_origen: dict[str, Any] = {}
        n_excluidas = 0
        for o, v in por_origen.items():
            fila: dict[str, Any] = {"por_tipo": _conteo_ordenado(v["tipos"]), "por_espacio": _conteo_ordenado(v["espacios"])}
            if incluir_bloques:
                fila["por_espacio_bloques"] = _conteo_ordenado(v["bloques"])
            n_o = len(v["excl_espacio"]) + len(v["excl_vp"]) + len(v["excl_papel"])
            fila["excluidas"] = {
                "n": n_o,
                "por_espacio": _conteo_ordenado(v["excl_espacio"]),
                "viewports": _conteo_ordenado(v["excl_vp"]),
                "viewports_papel": _conteo_ordenado(v["excl_papel"]),
            }
            n_excluidas += n_o
            salida_por_origen[o] = fila
        return {
            **self._cabecera_l(modo, propias),
            "mapa": filas_mapa,
            "n_reasignar": len(afectadas),
            "n_reasignadas": n_reasignadas,
            "por_origen": salida_por_origen,
            "n_excluidas": n_excluidas,
            "espacios": espacios,
            "incluir_viewports": incluir_viewports,
            "n_propiedades_forzadas": forzadas,
            "n_cambian_aspecto": len(arreglos),
            "n_aspecto_fijado": n_aspecto_fijado,
            "incluir_bloques": incluir_bloques,
            "borrar_capas_origen_vacias": borrar_vacias,
            "capas_borrar": capas_borrar,
            "capas_borradas": capas_borradas,
            "capas_creadas": capas_creadas,
            "duracion_ms": {"lectura": 0, "escritura": escritura_ms},
            "origen_texto": ORIGEN_TEXTO,
        }

    # -- Fase 4B (§5G) --------------------------------------------------------

    def _vano(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        return self._generar("vano", params)

    def _union_muros(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        return self._generar("union_muros", params)

    def _cotas_automaticas(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        return self._generar("cotas_automaticas", params)

    def _leer_contorno(self, e: dict[str, Any], campo: str, propias: list[str]) -> list[tuple[float, float]]:
        """Comprobaciones de 'muro' (§5G.1) sobre la entidad ya resuelta por HandleGuard: vértices sin duplicados."""
        h = e["handle"]
        verts = e.get("vertices") if e["tipo"] == "LWPOLYLINE" else None
        cerrada = bool(e.get("cerrada")) or (bool(verts) and len(verts) > 1 and verts[0][:2] == verts[-1][:2])
        if verts is None or not cerrada:
            raise self._invalid_data(f"{campo}: la entidad {h} no es una polilínea cerrada ({e['tipo']})", handle=h)
        if e.get("tiene_arcos"):
            raise self._invalid_data(f"{campo}: la polilínea {h} tiene arcos; solo se admiten contornos rectos", handle=h)
        xy, colapsados = _colapsar_duplicados([(float(v[0]), float(v[1])) for v in verts], lazo_cerrado=True)
        if colapsados:
            propias.append(advertencia_duplicados_colapsados(h, colapsados))
        return xy

    def _comprobar_espacio_muro(self, own: dict[str, Any], espacio: str, h: str) -> None:
        destino, _ents = self._espacio_destino({"espacio": own["espacio"]})
        if destino != espacio:
            raise _invalid(f"espacio: el muro {h} está en '{espacio}'")

    @staticmethod
    def _bucles_hatch(e: dict[str, Any]) -> list[list[str]]:
        """Bucles de un sombreado: _bucles si los tiene; si no, cada entidad de _contorno es un bucle."""
        return [[h.upper() for h in b] for b in (e.get("_bucles") or [[h] for h in e.get("_contorno") or []])]

    def _hatches_a_rehacer(self, handles: list[str], propias: list[str], reparte: Any = None) -> list[dict[str, Any]]:
        """Política rehacer (§5G.0): sustituye los bucles del muro y conserva los demás; lo que no se puede, se avisa."""
        muros = set(handles)
        rehacer: list[dict[str, Any]] = []
        for _esp, entities in self._all_spaces():
            for e in entities:
                if e["tipo"] != "HATCH" or not e.get("asociativo"):
                    continue
                bucles = self._bucles_hatch(e)
                if not any(set(b) & muros for b in bucles):
                    continue
                if all(reparte(b) if reparte is not None and set(b) & muros else not (set(b) & muros) or set(b) <= muros for b in bucles):
                    rehacer.append(e)
                else:
                    propias.append(advertencia_hatch_no_reconstruible(e["handle"]))
        return rehacer

    def _bucles_conservados(self, h: dict[str, Any], muros: set[str]) -> list[list[str]]:
        return [b for b in self._bucles_hatch(h) if not set(b) & muros]

    def _anclas_bucles(self, hatches: list[dict[str, Any]], muros: set[str]) -> list[dict[str, Any]]:
        """Huellas de las entidades de los bucles conservados: si cambian, confirmar no escribe."""
        anclas = []
        for h in hatches:
            for bucle in self._bucles_conservados(h, muros):
                for handle in bucle:
                    loc = self._find_entity(handle)
                    if loc is not None:
                        anclas.append(_ancla_directa(loc[1]))
        return anclas

    @staticmethod
    def _hatch_plan(h: dict[str, Any], indice: int | list[int], xy: list[tuple[float, float]], extra: list[list[str]] | None = None) -> dict[str, Any]:
        return {
            "tipo": "HATCH",
            "contorno": indice if isinstance(indice, list) else [indice],
            "bucles_conservados": extra or [],
            "patron": h.get("patron"),
            "escala": h.get("_escala", 1.0),
            "angulo": h.get("_angulo", 0.0),
            "aspecto": {k: h[k] for k in ("_color", "_transparencia", "_grosor", "_tipo_linea") if k in h},
            "area": abs(_shoelace(xy)),
            "capa": h["capa"],
            "concepto": None,
            "caja": _box_of([list(v) for v in xy]),
        }

    # vano (§5G.1)
    def _parse_vano(self, params: dict[str, Any]) -> tuple[dict[str, Any], list[tuple[dict[str, Any], str]]]:
        muro = self._leer_handle(params, "muro")
        cara_opuesta = self._leer_handle(params, "cara_opuesta") if "cara_opuesta" in params else None
        if cara_opuesta is not None and cara_opuesta.casefold() == muro.casefold():
            raise _invalid("cara_opuesta: debe ser distinta de muro")
        posicion = params.get("posicion")
        refs: list[tuple[dict[str, Any], str]] = []
        distancia: tuple[float, bool] | None = None
        if isinstance(posicion, dict) and "distancia" in posicion:
            for clave in posicion:
                if clave not in ("distancia", "desde"):
                    raise _invalid(f"posicion.{clave}: propiedad no permitida")
            d = posicion["distancia"]
            if not _es_numero(d) or d < 0:
                raise _invalid("posicion.distancia: debe ser un número mayor o igual que 0")
            desde = posicion.get("desde", "inicio")
            if desde not in ("inicio", "fin"):
                raise _invalid("posicion.desde: debe ser inicio o fin")
            distancia = (float(d), desde == "fin")
        else:
            refs = [(_parse_punto_ref(posicion, "posicion"), "posicion")]
        ancho = self._positivo(params, "ancho")
        tipo = params.get("tipo")
        if tipo not in ("puerta", "ventana", "hueco"):
            raise _invalid("tipo: debe ser uno de puerta, ventana, hueco")
        bloque = self._cadena(params, "bloque", 1, 255, requerido=False)
        if tipo == "hueco" and bloque is not None:
            raise _invalid("bloque: no aplica con tipo hueco")
        propiedades = self._leer_propiedades_dinamicas(params)
        if tipo == "hueco" and propiedades is not None:
            raise _invalid("propiedades_dinamicas: no aplica con tipo hueco")
        cara = params.get("cara_apertura", "izquierda")
        if cara not in ("izquierda", "derecha"):
            raise _invalid("cara_apertura: debe ser izquierda o derecha")
        bisagra = params.get("bisagra", "inicio")
        if bisagra not in ("inicio", "fin"):
            raise _invalid("bisagra: debe ser inicio o fin")
        own = {
            "muro": muro,
            "cara_opuesta": cara_opuesta,
            "distancia": distancia,
            "ancho": ancho,
            "tipo": tipo,
            "bloque": bloque,
            "cara": cara,
            "bisagra": bisagra,
            "propiedades": propiedades,
            "confirmar": self._bool(params, "confirmar", False),
            "capa": self._cadena(params, "capa", 1, 255, requerido=False),
            "espacio": params.get("espacio"),
        }
        return own, refs

    def _definicion_vano(self, nombre: str) -> dict[str, Any]:
        """Definición del dibujo o de la biblioteca (desde_biblioteca implícito), sin importarla todavía."""
        d = next((x for x in self.state.block_defs if x["nombre"].casefold() == nombre.casefold()), None)
        if d is not None:
            real = d["nombre"]
            return {
                "nombre": real,
                "importar": None,
                "dinamico": bool(d.get("dinamico")),
                "propiedades": self.state.propiedades_bloques.get(real.upper(), []),
            }
        importar = next((b for k, b in self.state.biblioteca.items() if k.casefold() == nombre.casefold()), None)
        if importar is None:
            raise _not_found(
                f"El bloque '{nombre}' no está en el dibujo ni en la biblioteca ({BIBLIOTECA_CARPETA}).",
                bloque=nombre,
                archivo=f"{BIBLIOTECA_CARPETA}\\{nombre}.dwg",
            )
        return {
            "nombre": importar["nombre"],
            "importar": importar,
            "dinamico": bool(importar.get("dinamico")),
            "propiedades": importar.get("propiedades", []),
        }

    def _plan_bloque_vano(
        self, own: dict[str, Any], hueco: dict[str, Any], propias: list[str]
    ) -> tuple[dict[str, Any], dict[str, Any]]:
        """Paso (4) del plan de §5G.1: catálogo, propiedad de ancho, colocación. (entidad planificada, numeros.bloque)."""
        catalogo: dict[str, dict[str, Any]] = LPS.get("biblioteca_vanos") or {}
        nombre = own["bloque"]
        if nombre is None:
            nombre = next(k for k, v in catalogo.items() if v["tipo"] == own["tipo"])
        clave = next((k for k in catalogo if k.casefold() == nombre.casefold()), None)
        if clave is None:
            raise self._invalid_data(detalle_bloque_no_catalogo(nombre), bloque=nombre)
        spec = {"bloque": clave, **catalogo[clave]}
        definicion = self._definicion_vano(clave)
        real = definicion["nombre"]
        ancho = own["ancho"]
        if abs(float(spec["espesor_nominal"]) - hueco["espesor"]) > 1e-3:
            propias.append(advertencia_espesor_vano(real, float(spec["espesor_nominal"]), hueco["espesor"]))
        props_base = definicion["propiedades"]
        prop_ancho: str | None = None
        candidatas: list[str] = []
        if definicion["dinamico"]:
            nominal = float(spec["ancho_nominal"]) + float(spec.get("ajuste_ancho") or 0.0)
            prop_ancho, candidatas = buscar_propiedad_ancho(props_base, spec.get("propiedad_ancho"), nominal)
        # §5G.1: definición por importar sin nombre configurado: el plugin decide en la referencia creada, al dibujar.
        pendiente = definicion["dinamico"] and definicion["importar"] is not None and spec.get("propiedad_ancho") is None
        avisos: list[str] = []
        if own["propiedades"] is not None and not definicion["dinamico"]:
            raise self._invalid_data(detalle_no_dinamico(real), bloque=real)
        valores = dict(own["propiedades"] or {})
        if prop_ancho is not None:
            repetida = next((k for k in valores if k.casefold() == prop_ancho.casefold()), None)
            if repetida is not None:
                avisos.append(f"propiedades_dinamicas: el ancho lo fija la herramienta; se ignoró el valor de '{prop_ancho}'.")
                del valores[repetida]
            valores[prop_ancho] = ancho + float(spec.get("ajuste_ancho") or 0.0)
        colocacion = colocar_bloque(hueco, spec, own["cara"], own["bisagra"], prop_ancho is not None, ancho)
        if prop_ancho is None and len(candidatas) > 1:
            avisos.append(advertencia_ancho_ambiguo(real, candidatas, colocacion["factor_ancho"]))
        elif prop_ancho is None:
            avisos.append(advertencia_ancho_escalado(real, colocacion["factor_ancho"]))
        if not pendiente:
            propias.extend(avisos)
        resuelto = {
            "ancho_por": "propiedad" if prop_ancho is not None else "escala",
            "propiedad_ancho": prop_ancho,
            "factor_ancho": colocacion["factor_ancho"],
            "escala": colocacion["escala"],
        }
        alcance = ancho if own["tipo"] == "puerta" else 0.0
        plan = {
            "tipo": "INSERT",
            "nombre": real,
            "importar": definicion["importar"],
            "posicion": colocacion["posicion"],
            "rotacion": colocacion["rotacion"],
            "escala": colocacion["escala"],
            "dinamico": definicion["dinamico"],
            "base_dinamica": props_base,
            "valores_dinamicos": valores or None,
            "capa": own["capa"],
            "concepto": spec["capa"],
            "caja": caja_bloque_vano(hueco, own["cara"], alcance),
            "ancho_al_dibujar": {"resuelto": resuelto, "avisos": avisos} if pendiente else None,
        }
        numeros = {
            "nombre": real,
            "catalogo": spec,
            "posicion": colocacion["posicion"],
            "rotacion": colocacion["rotacion"],
            "espejo": colocacion["espejo"],
            **resuelto,
        }
        if pendiente:
            provisional = colocar_bloque(hueco, spec, own["cara"], own["bisagra"], True, ancho)
            numeros.update(ancho_por="pendiente", propiedad_ancho=None, factor_ancho=None, escala=provisional["escala"])
        return plan, numeros

    def _plan_vano(
        self, own: dict[str, Any], puntos: list[dict[str, Any]], propias: list[str]
    ) -> tuple[list[dict[str, Any]], dict[str, Any]]:
        muro = self._find_entity(own["muro"])
        if muro is not None and muro[1]["tipo"] == "LINE":
            return self._plan_muro_lineas(own, muro[1], puntos, propias)
        if own["cara_opuesta"] is not None:
            raise _invalid("cara_opuesta: solo aplica si muro es una LINE")
        encontrados, _usuario, _ocultas = self._proteger_handles([own["muro"]], own["confirmar"])
        espacio, _lst, e, _attr = encontrados[0]
        self._comprobar_espacio_muro(own, espacio, e["handle"])
        xy = self._leer_contorno(e, "muro", propias)
        try:
            if own["distancia"] is not None:
                centro = centro_por_distancia(xy, own["distancia"][0], own["distancia"][1])
            else:
                punto = puntos[0]["punto"]
                centro = (float(punto[0]), float(punto[1]))
            hueco = cortar_muro(xy, centro, own["ancho"])
        except _WallError as ex:
            if ex.es_parametro:
                raise _invalid(ex.detalle) from None
            raise _invalid_geometry(ex.detalle) from None
        plan_bloque = None
        numeros_bloque = None
        if own["tipo"] != "hueco":
            plan_bloque, numeros_bloque = self._plan_bloque_vano(own, hueco, propias)
        elif own["capa"] is not None:
            propias.append("capa: no aplica con tipo hueco; los contornos conservan la capa del muro.")
        hatches = self._hatches_a_rehacer([e["handle"]], propias)
        muros = {e["handle"]}
        self._avisar_columnas(hueco, {h["handle"] for h in hatches}, propias)
        ini, fin = hueco["lado_inicio"], hueco["lado_fin"]
        plan: list[dict[str, Any]] = [{"tipo": "BORRAR", "handle": e["handle"], "tipo_esperado": e["tipo"], "caja": None}]
        plan += [{"tipo": "BORRAR", "handle": h["handle"], "tipo_esperado": "HATCH", "caja": None} for h in hatches]
        plan.append(self._polilinea_plan(ini, True, e["capa"], "muros"))
        plan.append(self._polilinea_plan(fin, True, e["capa"], "muros"))
        for h in hatches:
            conservados = self._bucles_conservados(h, muros)
            if conservados:
                plan.append(self._hatch_plan(h, [0, 1], ini + fin, conservados))
            else:
                plan.append(self._hatch_plan(h, 0, ini))
                plan.append(self._hatch_plan(h, 1, fin))
        if plan_bloque is not None:
            plan.append(plan_bloque)
        contornos = []
        for lado, bucle in (("inicio", ini), ("fin", fin)):
            contornos.append(
                {"lado": lado, "n_vertices": len(bucle), "longitud": _perimetro(bucle, True), "area": abs(_shoelace(bucle))}
            )
        numeros = {
            "hueco": {
                "centro": list(hueco["centro"]),
                "a1": list(hueco["a1"]),
                "a2": list(hueco["a2"]),
                "b1": list(hueco["b1"]),
                "b2": list(hueco["b2"]),
                "ancho": own["ancho"],
                "espesor": hueco["espesor"],
                "direccion": _norm360(math.degrees(math.atan2(hueco["direccion"][1], hueco["direccion"][0]))),
                "distancia_desde_inicio": hueco["distancia_desde_inicio"],
                "longitud_tramo": hueco["longitud_tramo"],
            },
            "contornos": contornos,
            "lineas": [],
            "bloque": numeros_bloque,
            "muro_original": {"handle": e["handle"], "capa": e["capa"], "area": abs(_shoelace(xy)), "n_vertices": len(xy), "cara_opuesta": None},
            "n_hatch_rehechos": len(hatches),
            "_anclas_extra": [_ancla_directa(x) for x in [e, *hatches]] + self._anclas_bucles(hatches, muros),
        }
        return plan, numeros

    def _avisar_columnas(self, hueco: dict[str, Any], excluidos: set[str], propias: list[str]) -> None:
        """WallEntitySupport.WarnHoleOverlaps: columnas y sombreados bajo el hueco, salvo los que se rehacen (cajas en planta)."""
        capa = LPS["capas_preferidas"]["columnas"]
        (dx, dy) = hueco["direccion"]
        quad = [hueco[k] for k in ("a1", "a2", "b2", "b1")]
        tocadas: list[str] = []
        sombreados: list[str] = []
        for _esp, entities in self._all_spaces():
            for e in entities:
                caja = e.get("bbox")
                es_columna = e.get("capa", "").casefold() == capa.casefold()
                if not (es_columna or e["tipo"] == "HATCH") or e["handle"] in excluidos or not caja:
                    continue
                esquinas = [(caja[i][0], caja[j][1]) for i in (0, 1) for j in (0, 1)]
                ejes = [(1.0, 0.0), (0.0, 1.0), (dx, dy), (-dy, dx)]
                if all(
                    max(p[0] * u + p[1] * v for p in quad) >= min(p[0] * u + p[1] * v for p in esquinas)
                    and min(p[0] * u + p[1] * v for p in quad) <= max(p[0] * u + p[1] * v for p in esquinas)
                    for u, v in ejes
                ):
                    (sombreados if e["tipo"] == "HATCH" else tocadas).append(e["handle"])
        if tocadas:
            propias.append(advertencia_columnas(capa, tocadas))
        if sombreados:
            propias.append(advertencia_sombreados_en_hueco(sombreados))

    def _plan_muro_lineas(
        self, own: dict[str, Any], e: dict[str, Any], puntos: list[dict[str, Any]], propias: list[str]
    ) -> tuple[list[dict[str, Any]], dict[str, Any]]:
        """§5G.1: muro de dos caras LINE: cuatro piezas con su aspecto y dos tapas en la capa del muro."""

        def cara(x: dict[str, Any]) -> tuple[Any, Any]:
            return (tuple(float(c) for c in x["inicio"][:2]), tuple(float(c) for c in x["fin"][:2]))

        centro_pedido = None
        if own["distancia"] is None:
            punto = puntos[0]["punto"]
            centro_pedido = (float(punto[0]), float(punto[1]))
        if own["cara_opuesta"] is not None:
            loc = self._find_entity(own["cara_opuesta"])
            if loc is None:
                raise _not_found(f"No existe ninguna entidad con handle '{own['cara_opuesta']}'.", handle=own["cara_opuesta"])
            o = loc[1]
            if o["tipo"] != "LINE":
                raise self._invalid_data(f"cara_opuesta: la entidad {o['handle']} no es una LINE ({o['tipo']})", handle=o["handle"])
        else:
            espacio_muro = self._find_entity(e["handle"])
            assert espacio_muro is not None
            lineas = [
                (x["handle"], cara(x))
                for esp, entities in self._all_spaces() if esp == espacio_muro[0]
                for x in entities if x["tipo"] == "LINE" and x["capa"].casefold() == e["capa"].casefold()
            ]
            elegida, candidatas = buscar_cara_opuesta((e["handle"], cara(e)), lineas, centro_pedido, own["ancho"])
            if elegida is None:
                detalle = detalle_varias_caras(e["capa"], candidatas) if candidatas else detalle_ninguna_cara(e["handle"], e["capa"])
                raise self._invalid_data(detalle, handle=e["handle"], candidatas=[h for h, _s in candidatas])
            hallada = self._find_entity(elegida)
            assert hallada is not None
            o = hallada[1]
        encontrados, _usuario, _ocultas = self._proteger_handles([e["handle"], o["handle"]], own["confirmar"])
        self._comprobar_espacio_muro(own, encontrados[0][0], e["handle"])
        try:
            centro = centro_pedido or centro_caras_por_distancia(cara(e), cara(o), e["handle"], o["handle"], own["distancia"][0], own["distancia"][1])
            corte = cortar_caras(cara(e), cara(o), e["handle"], o["handle"], centro, own["ancho"])
        except _WallError as ex:
            extra = {} if own["cara_opuesta"] is not None else {"cara_opuesta": o["handle"]}
            if ex.es_parametro:
                raise self._invalid_data(ex.detalle, **extra) from None
            raise _invalid_geometry(ex.detalle, **extra) from None
        plan_bloque = None
        numeros_bloque = None
        if own["tipo"] != "hueco":
            plan_bloque, numeros_bloque = self._plan_bloque_vano(own, corte, propias)
        elif own["capa"] is not None:
            propias.append("capa: no aplica con tipo hueco; los contornos conservan la capa del muro.")
        muros = {e["handle"], o["handle"]}
        repartos: dict[tuple[str, ...], tuple[list[str], list[str]]] = {}

        def reparte(bucle: list[str]) -> bool:
            otras = []
            for h in bucle:
                if h not in muros:
                    loc = self._find_entity(h)
                    if loc is None or loc[1]["tipo"] != "LINE":
                        return False
                    otras.append((h, *cara(loc[1])))
            reparto = repartir_bucle(corte, otras) if muros <= set(bucle) else None
            if reparto is not None:
                repartos[tuple(bucle)] = reparto
            return reparto is not None

        hatches = self._hatches_a_rehacer([e["handle"], o["handle"]], propias, reparte)
        self._avisar_columnas(corte, {h["handle"] for h in hatches}, propias)

        def pieza(clave: str, capa: str, origen: dict[str, Any] | None) -> dict[str, Any]:
            a, b = corte["piezas"][clave]
            p = {
                "tipo": "LINE", "inicio": [a[0], a[1], 0.0], "fin": [b[0], b[1], 0.0], "capa": capa, "concepto": "muros",
                "caja": _box_of([list(a), list(b)]),
            }
            if origen is not None:
                p["aspecto"] = {k: origen[k] for k in ("_color", "_transparencia", "_grosor", "_tipo_linea", "_escala_tipo_linea") if k in origen}
            return p

        plan: list[dict[str, Any]] = [
            {"tipo": "BORRAR", "handle": e["handle"], "tipo_esperado": "LINE", "caja": None},
            {"tipo": "BORRAR", "handle": o["handle"], "tipo_esperado": "LINE", "caja": None},
        ]
        plan += [{"tipo": "BORRAR", "handle": h["handle"], "tipo_esperado": "HATCH", "caja": None} for h in hatches]
        n = len(plan)
        plan += [
            pieza("muro_inicio", e["capa"], e), pieza("muro_fin", e["capa"], e),
            pieza("opuesta_inicio", o["capa"], o), pieza("opuesta_fin", o["capa"], o),
            pieza("tapa_inicio", e["capa"], None), pieza("tapa_fin", e["capa"], None),
        ]
        puntos_caja = [list(p) for t in corte["piezas"].values() for p in t]
        for h in hatches:
            extra = self._bucles_conservados(h, muros)
            for b in self._bucles_hatch(h):
                if tuple(b) in repartos:
                    extra += [repartos[tuple(b)][0], repartos[tuple(b)][1]]
            # Los índices del plan no cuentan los BORRAR (_dibujar los separa antes de materializar).
            plan.append(self._hatch_plan(h, [0, 1, 2, 3, 4, 5], [tuple(p) for p in puntos_caja], extra))
        if plan_bloque is not None:
            plan.append(plan_bloque)
        numeros = {
            "hueco": {
                "centro": list(corte["centro"]), "a1": list(corte["a1"]), "a2": list(corte["a2"]),
                "b1": list(corte["b1"]), "b2": list(corte["b2"]), "ancho": own["ancho"], "espesor": corte["espesor"],
                "direccion": _norm360(math.degrees(math.atan2(corte["direccion"][1], corte["direccion"][0]))),
                "distancia_desde_inicio": corte["distancia_desde_inicio"], "longitud_tramo": corte["longitud_tramo"],
            },
            "contornos": [],
            "lineas": [
                {"lado": lado, "origen": origen, "longitud": math.dist(*corte["piezas"][clave])}
                for lado, origen, clave in (
                    ("inicio", e["handle"], "muro_inicio"), ("fin", e["handle"], "muro_fin"),
                    ("inicio", o["handle"], "opuesta_inicio"), ("fin", o["handle"], "opuesta_fin"),
                )
            ],
            "bloque": numeros_bloque,
            "muro_original": {"handle": e["handle"], "capa": e["capa"], "area": None, "n_vertices": 2, "cara_opuesta": o["handle"]},
            "n_hatch_rehechos": len(hatches),
            "n_entidades": len(plan) - n,
            "_anclas_extra": [_ancla_directa(x) for x in [e, o, *hatches]] + self._anclas_bucles(hatches, muros),
        }
        return plan, numeros

    # union_muros (§5G.2)
    def _parse_union_muros(self, params: dict[str, Any]) -> tuple[dict[str, Any], list[tuple[dict[str, Any], str]]]:
        a = self._leer_handle(params, "muro_a")
        b = self._leer_handle(params, "muro_b")
        if a == b:
            raise _invalid("muro_b: debe ser distinto de muro_a")
        tipo = params.get("tipo", "auto")
        if tipo not in ("L", "T", "cruce", "auto"):
            raise _invalid("tipo: debe ser L, T, cruce o auto")
        own = {
            "a": a,
            "b": b,
            "tipo": tipo,
            "confirmar": self._bool(params, "confirmar", False),
            "capa": self._cadena(params, "capa", 1, 255, requerido=False),
            "espacio": params.get("espacio"),
        }
        return own, []

    def _plan_union_muros(
        self, own: dict[str, Any], _puntos: list[dict[str, Any]], propias: list[str]
    ) -> tuple[list[dict[str, Any]], dict[str, Any]]:
        encontrados, _usuario, _ocultas = self._proteger_handles([own["a"], own["b"]], own["confirmar"])
        ea, eb = encontrados[0][2], encontrados[1][2]
        self._comprobar_espacio_muro(own, encontrados[0][0], ea["handle"])
        xa = self._leer_contorno(ea, "muro_a", propias)
        xb = self._leer_contorno(eb, "muro_b", propias)
        for campo, xy in (("muro_a", xa), ("muro_b", xb)):
            if not es_cuadrilatero_convexo(xy):
                raise _invalid_geometry(detalle_convexo(campo))
        try:
            u = unir_cuadrilateros(xa, xb, own["tipo"])
        except _WallError as ex:
            raise _invalid_geometry(ex.detalle) from None
        capa = own["capa"]
        if capa is None:
            capa = ea["capa"]
            if ea["capa"].casefold() != eb["capa"].casefold():
                propias.append(advertencia_capas_muros(ea["capa"], eb["capa"]))
        hatches = self._hatches_a_rehacer([ea["handle"], eb["handle"]], propias)
        plan: list[dict[str, Any]] = [
            {"tipo": "BORRAR", "handle": ea["handle"], "tipo_esperado": ea["tipo"], "caja": None},
            {"tipo": "BORRAR", "handle": eb["handle"], "tipo_esperado": eb["tipo"], "caja": None},
        ]
        plan += [{"tipo": "BORRAR", "handle": h["handle"], "tipo_esperado": "HATCH", "caja": None} for h in hatches]
        plan.append(self._polilinea_plan(u["contorno"], True, capa, "muros"))
        muros = {ea["handle"], eb["handle"]}
        simples = [h for h in hatches if not self._bucles_conservados(h, muros)]
        if simples:
            # Patrón del sombreado de muro_a; si solo muro_b tenía, el suyo.
            elegido = next((h for h in simples if ea["handle"] in {x.upper() for x in h.get("_contorno", [])}), simples[0])
            plan.append(self._hatch_plan(elegido, 0, u["contorno"]))
        for h in hatches:
            if h not in simples:
                plan.append(self._hatch_plan(h, 0, u["contorno"], self._bucles_conservados(h, muros)))
        numeros = {
            "tipo_detectado": u["tipo_detectado"],
            "area_a": u["area_a"],
            "area_b": u["area_b"],
            "area_solape": u["area_solape"],
            "area_union": u["area_union"],
            "n_vertices": u["n_vertices"],
            "longitud_contorno": _perimetro(u["contorno"], True),
            "_anclas_extra": [_ancla_directa(x) for x in [ea, eb, *hatches]] + self._anclas_bucles(hatches, muros),
        }
        return plan, numeros

    # cotas_automaticas (§5G.3)
    def _parse_cotas_automaticas(self, params: dict[str, Any]) -> tuple[dict[str, Any], list[tuple[dict[str, Any], str]]]:
        handles = self._leer_handles(params, 100)
        direccion = params.get("direccion", "auto")
        if not (direccion in ("x", "y", "auto") or _es_numero(direccion)):
            raise _invalid("direccion: debe ser x, y, auto o un número en grados")
        lado = params.get("lado", "izquierda")
        refs: list[tuple[dict[str, Any], str]] = []
        if isinstance(lado, str):
            if lado not in ("izquierda", "derecha"):
                raise _invalid("lado: debe ser izquierda o derecha")
        else:
            refs = [(_parse_punto_ref(lado, "lado"), "lado")]
        own = {
            "handles": handles,
            "direccion": direccion,
            "lado": lado if isinstance(lado, str) else None,
            "separacion": self._positivo(params, "separacion"),
            "estilo_dado": "estilo" in params,
            "params_estilo": {k: params[k] for k in ("estilo",) if k in params},
            "cadena": self._bool(params, "cadena", True),
            "tolerancia": self._positivo(params, "tolerancia", 0.001),
            "capa": self._cadena(params, "capa", 1, 255, requerido=False),
            "espacio": params.get("espacio"),
        }
        return own, refs

    def _puntos_acotables(self, i: int, e: dict[str, Any]) -> list[tuple[float, float]]:
        tipo = e["tipo"]
        if tipo == "LINE":
            crudos = [e["inicio"], e["fin"]]
        elif tipo in ("LWPOLYLINE", "POLYLINE") and isinstance(e.get("vertices"), list):
            crudos = e["vertices"]
        elif tipo in ("CIRCLE", "ARC"):
            crudos = [e["centro"]]
        elif tipo in ("INSERT", "TEXT", "MTEXT"):
            crudos = [e["posicion"]]
        else:
            raise self._invalid_data(f"handles[{i}]: la entidad {e['handle']} ({tipo}) no aporta puntos acotables", handle=e["handle"])
        return [(float(p[0]), float(p[1])) for p in crudos]

    def _plan_cotas_automaticas(
        self, own: dict[str, Any], puntos: list[dict[str, Any]], propias: list[str]
    ) -> tuple[list[dict[str, Any]], dict[str, Any]]:
        entidades = []
        espacios = []
        for h in own["handles"]:
            loc = self._find_entity(h)
            if loc is None:
                raise _not_found(f"No existe ninguna entidad con handle '{h}'.", handle=h)
            espacios.append(loc[0])
            entidades.append(loc[1])
        for espacio, e in zip(espacios[1:], entidades[1:]):
            if espacio != espacios[0]:
                raise _invalid(
                    f"handles: las entidades deben estar en el mismo espacio ({entidades[0]['handle']} en {espacios[0]}, "
                    f"{e['handle']} en {espacio})"
                )
        destino, _ents = self._espacio_destino({"espacio": own["espacio"]})
        if destino != espacios[0]:
            propias.append(
                f"cotas_automaticas: las entidades están en '{espacios[0]}' y las cotas se crean en '{destino}' (parámetro espacio)."
            )
        estilo = self._estilo(own["params_estilo"], self.state.estilos_cota, "cota")
        if not own["estilo_dado"]:
            propias.append(advertencia_estilo_cota(estilo))
        por_entidad = [self._puntos_acotables(i, e) for i, e in enumerate(entidades)]
        todos = [p for lista in por_entidad for p in lista]
        direccion = own["direccion"]
        grados = {"x": 0.0, "y": 90.0}.get(direccion) if isinstance(direccion, str) else float(direccion)
        if grados is None:
            grados = direccion_dominante(por_entidad[0])
        lado_punto = puntos[0]["punto"] if puntos else None
        c = cadena_de_cotas(todos, grados, own["lado"], lado_punto, own["separacion"], own["cadena"], own["tolerancia"])
        plan = [
            {
                "tipo": "DIMENSION",
                "p1": s["p1"],
                "p2": s["p2"],
                "linea": s["linea"],
                "medida": s["medida"],
                "rotacion": grados,
                "estilo": estilo,
                "capa": own["capa"],
                "concepto": "cotas",
                "caja": _box_of([s["p1"], s["p2"], s["linea"]]),
            }
            for s in c["cotas"]
        ]
        numeros = {
            "medidas": c["medidas"],
            "total": c["total"],
            "n_cotas": len(c["cotas"]),
            "direccion": grados,
            "lado": c["lado"],
            "separacion": own["separacion"],
            "estilo": estilo,
            "dimscale": self.state.dimscale,
            "cannoscale": self.state.cannoscale,
            "n_puntos": len(todos),
            "n_puntos_fundidos": c["n_fundidos"],
            "_anclas_extra": [_ancla_directa(e) for e in entidades],
        }
        return plan, numeros

    # auditar_archivo (§5H.1): forma del contrato sobre datos simulados, no las reglas reales
    def _validar_ruta_auditoria(self, ruta: str) -> str | None:
        """ExportRules.ValidatePath con carpetas_auditoria_permitidas, sin tocar el disco."""
        return self._validar_ruta_export(ruta, self.state.carpetas_auditoria, DETALLE_RUTA_FUERA_AUDITORIA)

    def _buscar_archivo(self, ruta: str) -> dict[str, Any] | None:
        for nombre, datos in self.state.archivos.items():
            if nombre.casefold() == ruta.casefold():
                return datos
            if self.state.archivos_por_nombre and ntpath.basename(nombre).casefold() == ntpath.basename(ruta).casefold():
                return datos
        return None

    def _auditar_archivo(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        for clave in params:
            if clave not in AUDITAR_ARCHIVO_SCHEMA["properties"]:
                raise _invalid(f"{clave}: propiedad no permitida")
        ruta = self._cadena(params, "ruta", 1, 1000)
        assert ruta is not None
        pedidas = params.get("comprobaciones", COMPROBACIONES_FASE5)
        for i, nombre in enumerate(pedidas):
            if nombre not in COMPROBACIONES_FASE5:
                raise _invalid(f"comprobaciones[{i}]: debe ser uno de {', '.join(COMPROBACIONES_FASE5)}")
            if nombre in pedidas[:i]:
                raise _invalid(f"comprobaciones: comprobación repetida '{nombre}'")
        comprobaciones = [c for c in COMPROBACIONES_FASE5 if c in pedidas]
        copiar = params.get("copiar", True)
        if not isinstance(copiar, bool):
            raise _invalid("copiar: debe ser booleano")
        presupuesto = params.get("presupuesto_ms", 8000)
        if isinstance(presupuesto, bool) or not isinstance(presupuesto, int) or not 1000 <= presupuesto <= 30000:
            raise _invalid("presupuesto_ms: debe ser un entero entre 1000 y 30000")
        error_ruta = self._validar_ruta_auditoria(ruta)
        if error_ruta is not None:
            raise _invalid(error_ruta)
        datos = self._buscar_archivo(ruta)
        if datos is None:
            raise _not_found(f"El archivo '{ruta}' no existe.", archivo=ruta)
        if datos.get("danado"):
            raise _RpcError(-32603, "No se pudo leer el archivo DWG.", {"code": "INTERNAL", "detalle": "eBadDwgHeader"})
        return self._respuesta_auditar_archivo(ruta, datos, comprobaciones, copiar, params)

    def _hallazgos_archivo(self, datos: dict[str, Any], insunits: int, reglas: list[str]) -> list[dict[str, Any]]:
        def hallazgo(regla: str, severidad: str, mensaje: str, **extra: Any) -> dict[str, Any]:
            return {"regla": regla, "severidad": severidad, "mensaje": mensaje, "handles": [], "bbox": None, "espacio": None, "datos": extra}

        filas: list[dict[str, Any]] = []
        for nombre, clasificacion, n in datos.get("capas_ajenas", []):
            contenido = f"tiene {n} entidades" if n else "no tiene entidades"
            severidad = "media" if n else "baja"
            filas.append(hallazgo("auditar_capas", severidad, f"La capa '{nombre}' ({clasificacion}) {contenido}.", clasificacion=clasificacion, n_entidades=n))
        n_capa_0 = datos.get("n_capa_0", 0)
        if n_capa_0:
            filas.append(hallazgo("entidades_en_capa_0", "media", f"{n_capa_0} entidades en la capa 0 en el espacio modelo.", n=n_capa_0))
        for bloque, motivo in datos.get("bloques_sospechosos", []):
            filas.append(hallazgo("auditar_bloques", "baja", f"El nombre de bloque '{bloque}' parece arbitrario ({motivo}); 1 inserciones.", bloque=bloque, motivo=motivo, n_inserciones=1))
        for _i in range(datos.get("n_duplicados", 0)):
            filas.append(hallazgo("duplicados", "baja", "2 LINE idénticas.", tipo="LINE", n=2))
        if "mediana" in datos:
            filas.append(hallazgo("textos_fuera_de_escala", "baja", "1 textos fuera de escala.", motivo="mayor", n=1, mediana=datos["mediana"]))
        measurement = datos.get("measurement", 1)
        if insunits != INSUNITS_LPS or datos.get("unidades_inconsistentes"):
            filas.append(hallazgo("unidades", "alta" if insunits == 0 else "media", "Unidades distintas del estándar.", insunits=insunits, measurement=measurement, insunits_lps=INSUNITS_LPS))
        filas = [f for f in filas if f["regla"] in reglas]
        orden_severidad = {"alta": 0, "media": 1, "baja": 2}
        filas.sort(key=lambda f: (orden_severidad[f["severidad"]], reglas.index(f["regla"])))
        contadores: dict[str, int] = {}
        for fila in filas:
            contadores[fila["regla"]] = contadores.get(fila["regla"], 0) + 1
            fila["id"] = f"{fila['regla']}-{contadores[fila['regla']]}"
        return filas

    def _respuesta_auditar_archivo(
        self, ruta: str, datos: dict[str, Any], comprobaciones: list[str], copiar: bool, params: dict[str, Any]
    ) -> dict[str, Any]:
        insunits = datos.get("insunits", INSUNITS_LPS)
        corto, nombre_unidad, _m = UNIDADES.get(insunits, ("desconocida", "desconocida", None))
        reglas = [c for c in comprobaciones if c != "resumen"]
        no_disponible = []
        if copiar and "xrefs_no_resueltas" in reglas:
            reglas.remove("xrefs_no_resueltas")
            no_disponible.append({"comprobacion": "xrefs_no_resueltas", "motivo": MOTIVO_XREFS_NO_DISPONIBLE})
        hallazgos = self._hallazgos_archivo(datos, insunits, reglas)
        por_severidad = {s: sum(1 for h in hallazgos if h["severidad"] == s) for s in ("alta", "media", "baja")}
        por_regla = {
            r: {
                "n": sum(1 for h in hallazgos if h["regla"] == r),
                **{s: sum(1 for h in hallazgos if h["regla"] == r and h["severidad"] == s) for s in ("alta", "media", "baja")},
                "completo": True,
            }
            for r in reglas
        }
        pagina, total, siguiente = paginate(hallazgos, params)
        advertencias: list[str] = []
        if insunits != INSUNITS_LPS:
            advertencias.append(f"El dibujo está en {nombre_unidad} (INSUNITS={insunits}); el estándar LPS es metros (INSUNITS=6).")
        if not self.state.carpetas_auditoria:
            advertencias.append(ADVERTENCIA_SIN_CARPETAS_AUDITORIA)
        resumen_archivo = None
        if "resumen" in comprobaciones:
            resumen_archivo = {
                "measurement": datos.get("measurement", 1),
                "n_capas": datos.get("n_capas", 3),
                "entidades_modelo": {"total": datos.get("n_entidades", 10), "por_tipo": {"LINE": datos.get("n_entidades", 10)}},
                "n_bloques": datos.get("n_bloques", 1),
                "bloques_mas_usados": [{"nombre": n, "n_inserciones": 1} for n in datos.get("bloques_mas_usados", [])],
                "layouts": datos.get("layouts", ["Layout1"]),
                "n_xrefs": 0,
            }
        return {
            "pid": self.state.pid,
            "origen": "plugin",
            "archivo": {
                "ruta": ruta,
                "nombre": ntpath.basename(ruta),
                "tamano_bytes": datos.get("tamano_bytes", 1024),
                "modificado": "2026-10-25T10:12:00-05:00",
                "version_dwg": datos.get("version_dwg", "AC1032"),
                "abierto_en_sesion": False,
                "copia_temporal": copiar,
            },
            "espacio": params.get("espacio", "modelo"),
            "comprobaciones": comprobaciones,
            "no_disponible": no_disponible,
            "resumen": {"total_hallazgos": len(hallazgos), "por_severidad": por_severidad, "por_regla": por_regla, "n_cuadros_fuera_de_layout": 0},
            "resumen_archivo": resumen_archivo,
            "reglas_omitidas": [],
            "reglas_con_error": [],
            "duracion_ms": 12,
            "hallazgos": pagina,
            "total": total,
            "cursor_siguiente": siguiente,
            "origen_texto": ORIGEN_TEXTO,
            "insunits": insunits,
            "unidades": corto,
            "en_metros_declarado": insunits == 6,
            "advertencias": advertencias,
        }

    # insertar_export (§5G.4)
    def _validar_ruta_export(self, ruta: str, carpetas: Any = _SIN_CARPETAS, fuera: str = DETALLE_RUTA_FUERA) -> str | None:
        """ExportRules.ValidatePath, sin tocar el disco."""
        if not re.match(r"^([A-Za-z]:[\\/]|\\\\[^\\/]+[\\/])", ruta):
            return DETALLE_RUTA_ABSOLUTA
        if not ruta.casefold().endswith(".dwg"):
            return DETALLE_RUTA_DWG
        if ".." in ruta or any(ch in ruta for ch in "*?") or any(ord(ch) < 32 for ch in ruta):
            return DETALLE_RUTA_COMODINES
        if carpetas is _SIN_CARPETAS:
            carpetas = self.state.carpetas_export
        if carpetas:
            normal = ruta.replace("/", "\\").casefold()
            if not any(normal.startswith(c.replace("/", "\\").rstrip("\\").casefold() + "\\") for c in carpetas):
                return fuera
        return None

    def _nombre_bloque_export(self, ruta: str) -> str:
        """ExportRules.BlockNameFor: CYPE_<archivo saneado>, con _2, _3… si ya existe."""
        base = ntpath.splitext(ntpath.basename(ruta))[0]
        limpio = re.sub(r'[<>/\\":;?*|,=`]', "_", base).strip()
        nombre = ("CYPE_" + limpio if limpio else "CYPE_EXPORT")[:200]
        existentes = {d["nombre"].casefold() for d in self.state.block_defs}
        candidato, n = nombre, 1
        while candidato.casefold() in existentes:
            n += 1
            candidato = f"{nombre}_{n}"
        return candidato

    def _insertar_export(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        self._check_props("insertar_export", params)
        st = self.state
        ruta = self._cadena(params, "ruta", 1, 1000)
        assert ruta is not None
        ref = _parse_punto_ref(params["posicion"], "posicion")
        rotacion = _norm360(self._numero(params, "rotacion", 0.0) or 0.0)
        escala = self._numero(params, "escala", 1.0)
        if escala == 0:
            raise _invalid("escala: debe ser un número distinto de 0")
        modo = params.get("escala_unidades", "auto")
        if modo not in ("auto", "ninguna"):
            raise _invalid("escala_unidades: debe ser uno de auto, ninguna")
        filtro = params.get("filtrar_capas", "todas")
        if filtro not in ("todas", "cype"):
            raise _invalid("filtrar_capas: debe ser uno de todas, cype")
        if params.get("como", "bloque") == "xref":
            raise _invalid(DETALLE_COMO_XREF)
        if params.get("como", "bloque") != "bloque":
            raise _invalid("como: debe ser uno de bloque, xref")
        nombre_dado = self._cadena(params, "nombre_bloque", 1, 200, requerido=False)
        if nombre_dado is not None and (re.search(r'[<>/\\":;?*|,=`]', nombre_dado) or nombre_dado.startswith("*")):
            raise _invalid("nombre_bloque: nombre de bloque no válido")
        propias = self._r2(params)
        espacio, entities = self._espacio_destino(params)
        punto = self._resolver_ref(ref, "posicion", propias)
        posicion = _p3(punto["punto"])
        error_ruta = self._validar_ruta_export(ruta)
        if error_ruta is not None:
            raise _invalid(error_ruta)
        export = st.exports.get(ruta.casefold())
        if export is None:
            raise _not_found(f"El archivo '{ruta}' no existe.", archivo=ruta)
        if export.get("ilegible"):
            raise _RpcError(-32603, f"Error interno: no se pudo leer '{ruta}' (eBadDwgHeader)", {"code": "INTERNAL", "detalle": "eBadDwgHeader"})
        if not st.carpetas_export:
            propias.append(ADVERTENCIA_SIN_CARPETAS)
        formatos, escalas = deteccion_lamina(export)
        if modo == "auto" and formatos:
            raise self._invalid_data(
                detalle_lamina(formatos, escalas), formatos=formatos, escalas=escalas, escalas_sugeridas=[n * 0.01 for n in escalas]
            )
        if nombre_dado is not None and any(d["nombre"].casefold() == nombre_dado.casefold() for d in st.block_defs):
            raise self._invalid_data(f"nombre_bloque: el bloque '{nombre_dado}' ya existe en el dibujo", bloque=nombre_dado)
        nombre = nombre_dado if nombre_dado is not None else self._nombre_bloque_export(ruta)
        capa, creada, adv_capa = self._capa_escritura(params.get("capa_destino"), None)
        propias += adv_capa
        # Unidades: mm → m = 0,001; export sin unidades → factor 1 con aviso.
        ins_export = int(export["insunits"])
        factor = _factor_unidades(ins_export, st.insunits) if ins_export != 0 else None
        if ins_export == 0 and modo == "auto":
            propias.append(ADVERTENCIA_EXPORT_SIN_UNIDADES)
        elif factor is None and modo == "auto":
            propias.append(
                f"No se aplicó factor de unidades: el export (INSUNITS={ins_export}) o el dibujo (INSUNITS={st.insunits}) "
                "no declaran unidades con equivalencia métrica."
            )
        # §5G.4: con 'ninguna' no se aplica ningún factor y se informa 1.
        factor_informado = 1.0 if factor is None or modo != "auto" else factor
        final = escala * factor_informado
        aplicada = [final, final, final]
        # Capas del export (ExportRules.ClassifyLayers) y filtro en la base lateral.
        patrones = (LPS.get("grupos_externos_permitidos") or {}).get("cype") or []

        def es_cype(c: str) -> bool:
            return any(fnmatch.fnmatchcase(c.casefold(), p.casefold()) for p in patrones)

        capas_archivo: dict[str, int] = export["capas"]
        cype = [c for c in capas_archivo if c != "0" and es_cype(c)]
        otras = [c for c in capas_archivo if c != "0" and not es_cype(c)]
        excluidas = sum(n for c, n in capas_archivo.items() if c in otras) if filtro == "cype" else 0
        entran = cype + ([] if filtro == "cype" else otras)
        if filtro == "todas" and otras:
            propias.append(advertencia_capas_ajenas(otras))
        nuevas = [c for c in entran if self._find_layer(c) is None]
        capas_importadas = {"cype": [c for c in nuevas if c in cype], "otras": [c for c in nuevas if c in otras]}
        tam = export["tamano"]
        # ExportRules.PlacedBox: la caja del contenido con escala y rotación; R4 mira esa caja, no solo el punto.
        giro = math.radians(rotacion)
        colocadas = [
            (posicion[0] + x * final * math.cos(giro) - y * final * math.sin(giro), posicion[1] + x * final * math.sin(giro) + y * final * math.cos(giro))
            for x, y in ((0.0, 0.0), (tam[0], 0.0), (tam[0], tam[1]), (0.0, tam[1]))
        ]
        caja = [[min(p[0] for p in colocadas), min(p[1] for p in colocadas)], [max(p[0] for p in colocadas), max(p[1] for p in colocadas)]]
        propias += self._r4(espacio, entities, [caja], params.get("forzar") is True, "entidades")
        # Commit: definición + capas + capa destino + INSERT en un solo paso de deshacer (§5G.7).
        handle = self._alloc_handle()
        for c in nuevas:
            st.capas_creadas.append(_layer(c, 7, -3, "cype" if c in cype else "fuera_de_estandar"))
        st.block_defs.append(
            {
                "nombre": nombre,
                "n_inserciones": 1,
                "tags": [],
                "tiene_atributos": False,
                "n_entidades": sum(capas_archivo.values()) - excluidas,
                "es_xref": False,
                "anonimo": False,
                "dinamico": False,
                "unidades_bloque": ins_export,
                "comentario": "",
            }
        )
        insert = _insert(handle, capa["nombre"], nombre, posicion, rotacion, _box3(caja), [])
        insert["escala"] = aplicada
        self._confirmar_escritura(entities, [insert], capa, creada)
        st.pasos_deshacer += 1
        return self._r6(
            [handle],
            capa,
            creada,
            espacio,
            propias,
            entidad={"handle": handle, "bloque": nombre, "posicion": posicion, "escala": aplicada, "rotacion": rotacion, "bbox": caja},
            archivo=ruta,
            unidades_export=ins_export,
            factor_unidades=factor_informado,
            escala_aplicada=aplicada,
            escala_unidades=modo,
            n_entidades_export=sum(capas_archivo.values()),
            n_entidades_excluidas=excluidas,
            capas_importadas=capas_importadas,
            capas_export={"cype": cype, "otras": otras},
            bloques_importados=list(export["bloques"]),
            puntos_resueltos=[self._punto_json(punto)],
            origen_texto=ORIGEN_TEXTO,
        )

    # corregir_unidades_bloques (§5F.4)
    def _motivo_no_marcado(self, d: dict[str, Any], hallazgo: dict[str, Any] | None) -> str:
        u = d["unidades_bloque"]
        if u in (0, INSUNITS_DIBUJO):
            return "unidades iguales a las del dibujo"
        if hallazgo is None:
            return "sin inserciones"
        datos = hallazgo["datos"]
        if datos["n_no_compensadas"] == 0 and datos["factor_escala"] is not None:
            return "inserciones compensadas"
        if datos["tamano_real_m"] is None or datos["factor_escala"] is None:
            return "tamaño no medible"
        return "tamaño real no plausible"

    def _corregir_unidades_bloques(self, _id: Any, params: dict[str, Any]) -> dict[str, Any]:
        self._check_props_l("corregir_unidades_bloques", params)
        modo = self._modo(params)
        pedidos = self._lista_cadenas(params, "bloques", 1, 200)
        self._check_units_l(modo)
        st = self.state
        propias: list[str] = []
        ins = st.insunits
        # BloquesCheck.Analyze: mismos hallazgos (e ids) que auditar_bloques.
        _resumen, hallazgos = self._bloques_auditoria()
        _numerar(hallazgos)
        unidades = {
            h["datos"]["bloque"].casefold(): h
            for h in hallazgos
            if "unidades_bloque" in h["datos"] and "metadato_unidades_incorrecto" in h["datos"]
        }
        por_nombre = {d["nombre"].casefold(): d for d in st.block_defs}
        elegidos: list[tuple[dict[str, Any], dict[str, Any]]] = []
        if ins == 0:
            propias.append(ADVERTENCIA_BLOQUES_SIN_UNIDADES)
        elif pedidos is None:
            for h in unidades.values():
                if h["datos"]["metadato_unidades_incorrecto"] is True:
                    elegidos.append((por_nombre[h["datos"]["bloque"].casefold()], h))
        else:
            faltan = [b for b in pedidos if b.casefold() not in por_nombre]
            if faltan:
                raise _not_found(f"bloques: definiciones inexistentes: {', '.join(faltan)}", bloques=faltan)
            for i, b in enumerate(pedidos):
                d = por_nombre[b.casefold()]
                if d["anonimo"] or d["es_xref"] or d["nombre"].startswith("*"):
                    raise _invalid(f"bloques[{i}]: '{b}' es un layout, una xref o un bloque anónimo")
                h = unidades.get(b.casefold())
                if h is None or h["datos"]["metadato_unidades_incorrecto"] is not True:
                    raise _invalid(detalle_no_marcado(i, b, self._motivo_no_marcado(d, h)))
                if all(x[0] is not d for x in elegidos):
                    elegidos.append((d, h))
        elegidos.sort(key=lambda x: x[0]["nombre"].casefold())
        filas = []
        for d, h in elegidos:
            u = d["unidades_bloque"]
            filas.append(
                {
                    "nombre": d["nombre"],
                    "unidades_actuales": u,
                    "unidades_actuales_nombre": UNIDADES.get(u, ("desconocida",))[0],
                    "unidades_propuestas": ins,
                    "n_inserciones": h["datos"]["n_inserciones"],
                    "tamano_real_m": copy.deepcopy(h["datos"]["tamano_real_m"]),
                    "tamano_por_anidada": h["datos"]["tamano_por_anidada"],
                    "hallazgo_id": h["id"],
                }
            )
        corregidos = None
        escritura_ms = None
        if modo == "write":
            for d, _h in elegidos:
                d["unidades_bloque"] = ins
            corregidos = [f["nombre"] for f in filas]
            st.pasos_deshacer += 1
            escritura_ms = 0
        return {
            **self._cabecera_l(modo, propias),
            "origen_lista": "auditar_bloques" if pedidos is None else "parametro",
            "bloques": filas,
            "n": len(filas),
            "corregidos": corregidos,
            "duracion_ms": {"lectura": 0, "escritura": escritura_ms},
            "origen_texto": ORIGEN_TEXTO,
        }
