"""Reglas de capas del estándar LPS sin AutoCAD (protocol.md §2.6, §5C.1).

Puerto fiel de GeometryCore/Audit/LpsLayerRules.cs y LayerClassifier.cs: misma
entrada, misma salida. Si cambia la clasificación hay que cambiarla en los dos
sitios; server/tests/test_oda_audit.py reproduce los casos de las pruebas C#.
"""

from __future__ import annotations

import math
import re
import unicodedata
from dataclasses import dataclass, field
from typing import Any

MCP = "mcp"
ESTANDAR = "estandar"
ESTANDAR_CON_DIFERENCIAS = "estandar_con_diferencias"
CYPE = "cype"
AJENA_REVIT_AIA = "ajena_revit_aia"
AJENA_EXPORTACION = "ajena_exportacion"
GENERICA = "generica"
FUERA_DE_ESTANDAR = "fuera_de_estandar"

# Las 8 clasificaciones en orden de evaluación (y de por_clasificacion).
CLASSIFICATIONS = (
    MCP,
    ESTANDAR,
    ESTANDAR_CON_DIFERENCIAS,
    CYPE,
    AJENA_REVIT_AIA,
    AJENA_EXPORTACION,
    GENERICA,
    FUERA_DE_ESTANDAR,
)

PATTERN_REVIT_AIA = "revit_aia"
PATTERN_EXPORT = "exportacion"
PATTERN_GENERIC = "generica"

# Prefijo de las capas MCP; solo se usa si el estándar no está disponible (§2.6).
MCP_PREFIX = "MCP-"

_REVIT_AIA = re.compile(r"^[A-Z]-[A-Z0-9]{3,4}(-|$)")
_GENERIC_LAYER = re.compile(r"^(Layer|Capa)\d+$", re.IGNORECASE)

# Palabras clave → clave de capas_preferidas, en el orden de §5C.1.
_SUGGESTIONS: tuple[tuple[tuple[str, ...], str], ...] = (
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
)

# Códigos AIA/Revit por campo del nombre → clave de capas_preferidas (§5F.5).
_AIA_CODES = {
    "WALL": "muros",
    "COLS": "columnas",
    "COLM": "columnas",
    "DOOR": "puertas",
    "GLAZ": "ventanas",
    "WIND": "ventanas",
    "FURN": "mobiliario_fijo",
    "CASE": "mobiliario_fijo",
    "SANR": "sanitarios",
    "PLMB": "sanitarios",
    "FIXT": "sanitarios",
    "DIMS": "cotas",
    "TEXT": "textos",
    "ANNO": "textos",
    "IDEN": "textos",
    "GRID": "ejes",
    "BEAM": "vigas",
    "STRS": "escaleras",
    "PATT": "achurados",
    "HATCH": "achurados",
    "LEVL": "niveles",
    "ELEV": "niveles",
}

_INT32_MIN = -(2**31)
_INT32_MAX = 2**31 - 1


def upper_invariant(text: str) -> str:
    """Como string.ToUpperInvariant: carácter a carácter. str.upper() cambia longitudes (ß → SS)."""
    if text.isascii():
        return text.upper()
    return "".join(upper if len(upper := char.upper()) == 1 else char for char in text)


def remove_diacritics(text: str | None) -> str:
    """Quita las marcas diacríticas (Á → A, Ñ → N, Ü → U). No toca otros caracteres."""
    if not text:
        return ""
    decomposed = unicodedata.normalize("NFD", text)
    kept = "".join(char for char in decomposed if unicodedata.category(char) != "Mn")
    return unicodedata.normalize("NFC", kept)


class CaseInsensitiveDict(dict):
    """dict con clave sin distinguir mayúsculas (StringComparer.OrdinalIgnoreCase)."""

    def __setitem__(self, key: str, value: Any) -> None:
        super().__setitem__(upper_invariant(key), value)

    def __getitem__(self, key: str) -> Any:
        return super().__getitem__(upper_invariant(key))

    def __contains__(self, key: object) -> bool:
        return isinstance(key, str) and super().__contains__(upper_invariant(key))

    def get(self, key: str, default: Any = None) -> Any:
        return super().get(upper_invariant(key), default)


@dataclass(frozen=True)
class LayerSpec:
    """Propiedades de una capa de capas_permitidas o capas_mcp."""

    color: int
    tipo_linea: str
    grosor: int
    plot: bool = True


@dataclass(frozen=True)
class Classification:
    """Resultado de clasificar una capa (§5C.1)."""

    clasificacion: str
    diferencias: list[str] = field(default_factory=list)
    sugerencia: str | None = None
    patron_ajeno: str | None = None
    sugerencia_patron: str | None = None


def _try_int(value: Any) -> int | None:
    """Número JSON entero en 32 bits, también escrito como 7.0 (TryInt del C#). Los bool no son números."""
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value if _INT32_MIN <= value <= _INT32_MAX else None
    if isinstance(value, float) and math.isfinite(value) and math.floor(value) == value and _INT32_MIN <= value <= _INT32_MAX:
        return int(value)
    return None


def _read_specs(root: dict, section: str) -> CaseInsensitiveDict:
    specs = CaseInsensitiveDict()
    source = root.get(section)
    if not isinstance(source, dict):
        return specs
    for name, value in source.items():
        if not isinstance(value, dict) or name in specs:
            continue
        color = _try_int(value.get("color"))
        grosor = _try_int(value.get("grosor"))
        tipo_linea = value.get("tipo_linea")
        if color is None or grosor is None or not isinstance(tipo_linea, str):
            continue
        plot = value.get("plot", True)
        if plot is False:
            enabled = False
        elif isinstance(plot, (int, float)) and not isinstance(plot, bool):
            enabled = plot != 0
        else:
            enabled = True
        specs[name] = LayerSpec(color, tipo_linea, grosor, enabled)
    return specs


def _read_preferred(root: dict) -> dict[str, str]:
    source = root.get("capas_preferidas")
    if not isinstance(source, dict):
        return {}
    return {name: value for name, value in source.items() if isinstance(value, str) and value}


def _read_patterns(root: dict) -> list[str]:
    groups = root.get("grupos_externos_permitidos")
    if not isinstance(groups, dict):
        return []
    patterns: list[str] = []
    for items in groups.values():
        if isinstance(items, list):
            patterns.extend(item for item in items if isinstance(item, str) and item)
    return patterns


def _read_map(root: dict, permitidas: CaseInsensitiveDict, mcp: CaseInsensitiveDict, preferidas: dict[str, str]) -> CaseInsensitiveDict:
    """mapa_normalizacion: se ignoran valores no cadena o vacíos y los orígenes del estándar;
    un destino que sea un concepto de capas_preferidas se traduce a su capa."""
    mapping = CaseInsensitiveDict()
    source = root.get("mapa_normalizacion")
    if not isinstance(source, dict):
        return mapping
    concepts = {}
    for concept, layer in preferidas.items():
        concepts.setdefault(upper_invariant(concept), layer)
    for name, target in source.items():
        if not isinstance(target, str) or not name.strip() or name in mapping or name in permitidas or name in mcp:
            continue
        if target.strip():
            mapping[name] = concepts.get(upper_invariant(target), target)
    return mapping


@dataclass(frozen=True)
class LpsRules:
    """Reglas de capas de lps.json. Tolerante: lo mal formado se ignora; nunca lanza."""

    disponible: bool = False
    capas_permitidas: CaseInsensitiveDict = field(default_factory=CaseInsensitiveDict)
    capas_mcp: CaseInsensitiveDict = field(default_factory=CaseInsensitiveDict)
    capas_preferidas: dict[str, str] = field(default_factory=dict)
    patrones_externos: list[str] = field(default_factory=list)
    mapa_normalizacion: CaseInsensitiveDict = field(default_factory=CaseInsensitiveDict)

    @classmethod
    def from_json(cls, root: Any) -> LpsRules:
        """Construye las reglas desde la raíz de lps.json; None o no objeto → reglas vacías."""
        if not isinstance(root, dict):
            return cls()
        permitidas = _read_specs(root, "capas_permitidas")
        mcp = _read_specs(root, "capas_mcp")
        preferidas = _read_preferred(root)
        return cls(
            disponible=True,
            capas_permitidas=permitidas,
            capas_mcp=mcp,
            capas_preferidas=preferidas,
            patrones_externos=_read_patterns(root),
            mapa_normalizacion=_read_map(root, permitidas, mcp, preferidas),
        )


def rank(clasificacion: str) -> int:
    """Número de orden 1–8 de la clasificación (0 si no es válida)."""
    return CLASSIFICATIONS.index(clasificacion) + 1 if clasificacion in CLASSIFICATIONS else 0


def is_foreign(clasificacion: str) -> bool:
    """True para las clasificaciones 5–8 (ajenas, genérica, fuera de estándar)."""
    return rank(clasificacion) >= 5


def wildcard_match(name: str | None, pattern: str | None) -> bool:
    """Patrón con '*' (cualquier secuencia), sin mayúsculas y anclado. Sin regex: nombres y
    patrones son texto ajeno y el retroceso voraz acota el coste a O(n·m)."""
    if name is None or pattern is None:
        return False
    text = upper_invariant(name)
    glob = upper_invariant(pattern)
    i = j = mark = 0
    star = -1
    while i < len(text):
        if j < len(glob) and glob[j] != "*" and glob[j] == text[i]:
            i += 1
            j += 1
        elif j < len(glob) and glob[j] == "*":
            star = j
            j += 1
            mark = i
        elif star >= 0:
            j = star + 1
            mark += 1
            i = mark
        else:
            return False
    while j < len(glob) and glob[j] == "*":
        j += 1
    return j == len(glob)


def _foreign_class(nombre: str) -> str:
    """Clasificaciones 5–8, que no dependen de lps.json."""
    upper = upper_invariant(nombre)
    if _REVIT_AIA.match(upper):
        return AJENA_REVIT_AIA
    if (
        "_PEN_NO__" in upper
        or "_PLUMA_N" in upper
        or upper.startswith("PDF_")
        or (len(upper) > 3 and upper.startswith("PDF") and "0" <= upper[3] <= "9")
        or upper.startswith(("DIBUJO 2D - GENERAL", "2D DRAFTING - GENERAL", "3D "))
    ):
        return AJENA_EXPORTACION
    if _GENERIC_LAYER.match(nombre):
        return GENERICA
    return FUERA_DE_ESTANDAR


def foreign_pattern(nombre: str | None) -> str | None:
    """Patrón ajeno del NOMBRE (desde 0.6), con independencia de lps.json."""
    if not nombre:
        return None
    return {
        AJENA_REVIT_AIA: PATTERN_REVIT_AIA,
        AJENA_EXPORTACION: PATTERN_EXPORT,
        GENERICA: PATTERN_GENERIC,
    }.get(_foreign_class(nombre))


def suggest(nombre: str | None, reglas: LpsRules | None) -> str | None:
    """Capa preferida por palabra clave (primera coincidencia de la tabla) o None."""
    if not nombre or reglas is None or not reglas.capas_preferidas:
        return None
    key = remove_diacritics(upper_invariant(nombre))
    for keywords, concept in _SUGGESTIONS:
        if concept in reglas.capas_preferidas and any(keyword in key for keyword in keywords):
            return reglas.capas_preferidas[concept]
    return None


def suggest_for_pattern(nombre: str | None, reglas: LpsRules | None) -> str | None:
    """Sugerencia para un nombre con patrón ajeno: palabra clave y, si no, código AIA por campos."""
    if not nombre or reglas is None:
        return None
    by_keyword = suggest(nombre, reglas)
    if by_keyword is not None:
        return by_keyword
    upper = remove_diacritics(upper_invariant(nombre))
    for raw_field in re.split("[-_]", upper):
        concept = _AIA_CODES.get(raw_field.strip())
        if concept is not None and concept in reglas.capas_preferidas:
            return reglas.capas_preferidas[concept]
    return None


def _differences(color: int, tipo_linea: str, grosor: int, spec: LayerSpec) -> list[str]:
    differences: list[str] = []
    if color != spec.color:
        differences.append(f"color: {color} ≠ {spec.color}")
    if upper_invariant(tipo_linea) != upper_invariant(spec.tipo_linea):
        differences.append(f"tipo_linea: {tipo_linea} ≠ {spec.tipo_linea}")
    if grosor != spec.grosor:
        differences.append(f"grosor: {grosor} ≠ {spec.grosor}")
    return differences


def classify(nombre: str, color: int, tipo_linea: str | None, grosor: int, reglas: LpsRules | None) -> Classification:
    """Clasifica la capa: mcp → estandar → estandar_con_diferencias → cype → ajena_revit_aia →
    ajena_exportacion → generica → fuera_de_estandar (la primera que cumple gana)."""
    if nombre is None:
        raise ValueError("nombre: obligatorio")
    rules = reglas if reglas is not None else LpsRules()
    line_type = tipo_linea or ""

    is_mcp = nombre in rules.capas_mcp if rules.disponible else upper_invariant(nombre).startswith(MCP_PREFIX)
    pattern = foreign_pattern(nombre)

    # Estándar v2 (2026-10-28): el destino de mapa_normalizacion gana sobre la heurística.
    decided = rules.mapa_normalizacion.get(nombre)
    pattern_suggestion = None if pattern is None else (decided if decided is not None else suggest_for_pattern(nombre, rules))

    if is_mcp:
        return Classification(MCP, [], None, pattern, pattern_suggestion)

    spec = rules.capas_permitidas.get(nombre)
    if spec is not None:
        differences = _differences(color, line_type, grosor, spec)
        kind = ESTANDAR_CON_DIFERENCIAS if differences else ESTANDAR
        return Classification(kind, differences, None, pattern, pattern_suggestion)

    if any(wildcard_match(nombre, item) for item in rules.patrones_externos):
        return Classification(CYPE, [], None, pattern, pattern_suggestion)

    suggestion = decided if decided is not None else suggest(nombre, rules)
    return Classification(_foreign_class(nombre), [], suggestion, pattern, pattern_suggestion)
