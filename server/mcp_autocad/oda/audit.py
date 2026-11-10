"""auditar_archivo sin AutoCAD: ODA File Converter → DXF → ezdxf (protocol.md §5H.3).

Puertos en Python de la lógica de GeometryCore/Audit (Finding, BlockNameHeuristics,
HeightOutliers, DuplicateHasher, UnitsScale, UnitsTable, LpsUnits) y de las reglas
del plugin (Handlers/Audit/*Check.cs). Mismos nombres de regla, severidades y
textos que el plugin donde §5H.3 lo indica.

Lo que no se puede saber sin AutoCAD no se inventa: va a ``no_disponible``. INSUNITS
se informa como declarado; un archivo puede declarar mm y estar en metros (§5H.0.6).
Los textos del plano son datos: aquí solo se cuentan, comparan y citan.

Diferencias medidas con la respuesta del plugin, documentadas para quien compare:
- ``version_dwg`` sale de los 6 primeros bytes del DWG (§5B.8): el $ACADVER del DXF es
  siempre AC1032 porque ODA convierte a ACAD2018.
- ezdxf inventa la capa Defpoints al cargar un archivo que no la trae; se descarta
  para contar las capas reales (AutoCAD informa 6 en sintetico_cype.dwg, no 7).
- ``bbox`` es aproximada (puntos de definición; sin GeometricExtents ni fuentes).
"""

from __future__ import annotations

import logging
import math
import ntpath
import os
import re
import shutil
import threading
import time
from collections import Counter, defaultdict
from collections.abc import Callable, Iterable, Sequence
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any

from ..errors import PluginError
from ..lote import ESPACIO_FACTOR_ODA, ESPACIO_RESERVA
from . import lps_rules
from .convert import (
    ADVERTENCIA_ACCORE,
    ADVERTENCIA_SOLO_ACCORE,
    CONVERSOR_ACCORE,
    CONVERSOR_ODA,
    POLL_S,
    OdaCanceladoError,
    OdaConversionError,
    OdaNoDisponibleError,
    OdaTimeoutError,
    dwg_a_dxf,
    remove_work_files,
)
from .lps_rules import LpsRules, upper_invariant

log = logging.getLogger(__name__)

ADVERTENCIA_ODA = (
    "Resultado del respaldo ODA + ezdxf, sin AutoCAD: no se comprobaron cuadro de áreas, "
    "bloques dinámicos ni tamaños reales de inserciones anidadas."
)

NO_DISPONIBLE_ODA = [
    "propiedades de bloques dinámicos",
    "tamaño real de inserciones anidadas (tamano_por_anidada)",
    "en_layout por viewports",
    "entidades proxy y objetos de terceros",
]

ORIGEN_TEXTO = "contenido_del_plano"
RESUMEN = "resumen"
CUADRO_AREAS = "verificar_cuadro_areas"
MOTIVO_CUADRO_AREAS = "el reconocimiento del cuadro de áreas (AreaTable, viewports) solo está en el plugin"
MOTIVO_SOLO_PLUGIN = "requiere la API de AutoCAD: solo está en el plugin"

# Orden de la tabla de §5C.8 (también el de los hallazgos, §5C.0).
RULE_ORDER = (
    "auditar_capas",
    "entidades_en_capa_0",
    "auditar_bloques",
    CUADRO_AREAS,
    "duplicados",
    "textos_fuera_de_escala",
    "xrefs_no_resueltas",
    "unidades",
)

# Las 9 comprobaciones de §5H.1, en su orden.
COMPROBACIONES = (*RULE_ORDER, RESUMEN)

ALTA = "alta"
MEDIA = "media"
BAJA = "baja"
_SEVERITY_ORDER = {ALTA: 0, MEDIA: 1, BAJA: 2}

MODEL_SPACE = "modelo"
MAX_HANDLES = 50
MAX_EVALUATED_INSERTIONS = 500
TOP_BLOCKS = 20
MAX_TYPES_IN_MESSAGE = 5
DEFAULT_LIMIT = 200
MAX_LIMIT = 1000
MAX_CURSOR_DIGITS = 9

XREF_NO_COMPROBADA = "no_comprobada"
MOTIVO_XREF_NO_COMPROBADA = "la ruta está fuera de la unidad del plano y de carpetas_auditoria_permitidas; no se consultó"
LPS_KEY_FOLDERS = "carpetas_auditoria_permitidas"

ETAPA_LECTURA = "La lectura del DXF con ezdxf"
ETAPA_ESPERA = "La espera de la lectura anterior del DXF"
# La lectura mira la señal de parada cada tantas líneas: unos pocos milisegundos de reacción.
_LINES_PER_CHECK = 4096
# Tras pedir parar, lo que se espera a que el hilo suelte el DXF antes de borrarlo.
_STOP_WAIT_S = 2.0
# Una sola lectura de ezdxf por proceso. La toma quien lee y la suelta el hilo lector al acabar, no
# quien lo lanzó: si el plazo o la cancelación lo abandonan en la segunda etapa, sigue ocupada y la
# siguiente auditoría no se solapa con él.
_read_slot = threading.Lock()

WARNING_NO_STANDARD = "Estándar LPS no disponible: no se comparó con capas_permitidas."
WARNING_UNITS_ZERO_BLOCKS = "INSUNITS=0: no se comparan las unidades de los bloques."


def advertencia_bloques_sin_medir(n: int) -> str:
    """Sin nombres de bloque: el literal es fijo y el texto del plano no entra en las advertencias."""
    return (
        f"{n} bloque(s) con unidades distintas quedan en media porque sin AutoCAD no se mide su tamaño real; alguno "
        "podría ser alta: reaudite con el plugin para separarlos."
    )



# Bits de BLOCK (grupo 70).
_BLOCK_ANONYMOUS = 1
_BLOCK_XREF = 4
_BLOCK_DEPENDENT = 16

_DWG_VERSION = re.compile(r"^AC\d{4}$")

# ---------------------------------------------------------------- unidades (§2.1, §2.2, §5.2, §8)

WARNING_UNITS_UNKNOWN = "INSUNITS=0: el dibujo no declara unidades; los valores se devuelven en unidades de dibujo sin interpretar."
WARNING_IMPERIAL_WITH_METRIC = "MEASUREMENT=0 (imperial) con INSUNITS métrico: unidades inconsistentes."
WARNING_METRIC_WITH_IMPERIAL = "MEASUREMENT=1 (métrico) con INSUNITS imperial: unidades inconsistentes."
UNKNOWN_UNIT = "desconocida"

_UNIT_NAMES = (
    UNKNOWN_UNIT, "in", "ft", "mi", "mm", "cm", "m", "km", "µin", "mil", "yd", "Å", "nm", "µm", "dm",
    "dam", "hm", "Gm", "au", "ly", "pc", "us_ft", "us_in", "us_yd", "us_mi",
)  # fmt: skip

_UNIT_NAMES_ES = (
    UNKNOWN_UNIT, "pulgadas", "pies", "millas", "milímetros", "centímetros", "metros", "kilómetros",
    "micropulgadas", "milésimas de pulgada", "yardas", "ångströms", "nanómetros", "micrómetros", "decímetros",
    "decámetros", "hectómetros", "gigámetros", "unidades astronómicas", "años luz", "pársecs",
    "pies US", "pulgadas US", "yardas US", "millas US",
)  # fmt: skip

_METERS_PER_UNIT: tuple[float | None, ...] = (
    None, 0.0254, 0.3048, 1609.344, 0.001, 0.01, 1.0, 1000.0, 2.54e-8, 2.54e-5, 0.9144, 1e-10, 1e-9, 1e-6,
    0.1, 10.0, 100.0, 1e9, None, None, None,
    1200.0 / 3937.0, 100.0 / 3937.0, 3600.0 / 3937.0, 6336000.0 / 3937.0,
)  # fmt: skip

_COMPENSATION_TOLERANCE = 0.01


def unit_name(insunits: int) -> str:
    """Nombre corto de §8; "desconocida" si INSUNITS = 0 o fuera de rango."""
    return _UNIT_NAMES[insunits] if 0 <= insunits < len(_UNIT_NAMES) else UNKNOWN_UNIT


def unit_name_es(insunits: int) -> str:
    """Nombre en español de §8."""
    return _UNIT_NAMES_ES[insunits] if 0 <= insunits < len(_UNIT_NAMES_ES) else UNKNOWN_UNIT


def units_warnings(insunits: int, measurement: int) -> list[str]:
    """Advertencias de §2.1 y §5.2 (UnitsTable.Warnings)."""
    warnings: list[str] = []
    if insunits == 0:
        warnings.append(WARNING_UNITS_UNKNOWN)
    if measurement == 0 and 4 <= insunits <= 7:
        warnings.append(WARNING_IMPERIAL_WITH_METRIC)
    elif measurement == 1 and 1 <= insunits <= 3:
        warnings.append(WARNING_METRIC_WITH_IMPERIAL)
    return warnings


def lps_units_warning(insunits: int, lps_insunits: int, lps_name: str) -> str | None:
    """Advertencia frente al estándar LPS de §2.2 (LpsUnits.Warning), o None si coincide."""
    if insunits == lps_insunits:
        return None
    in_table = 0 <= lps_insunits < len(_UNIT_NAMES)
    name = unit_name_es(lps_insunits) if in_table else (lps_name or UNKNOWN_UNIT)
    standard = f"el estándar LPS es {name} (INSUNITS={lps_insunits})."
    if insunits == 0:
        return f"El dibujo no declara unidades (INSUNITS=0); {standard}"
    return f"El dibujo está en {unit_name_es(insunits)} (INSUNITS={insunits}); {standard}"


def lps_units(lps: Any) -> tuple[int, str] | None:
    """(insunits, nombre) del estándar, o None si lps.json no los trae válidos (LpsStandard.Load)."""
    units = lps.get("unidades") if isinstance(lps, dict) else None
    if not isinstance(units, dict):
        return None
    insunits, name = units.get("insunits"), units.get("nombre")
    if isinstance(insunits, bool) or not isinstance(insunits, int) or not isinstance(name, str):
        return None
    return insunits, name


def scale_factor(unit_from: int, unit_to: int) -> float | None:
    """Metros por unidad de origen ÷ de destino; None sin equivalencia (UnitsScale.Factor)."""
    if not (0 <= unit_from < len(_METERS_PER_UNIT) and 0 <= unit_to < len(_METERS_PER_UNIT)):
        return None
    source, target = _METERS_PER_UNIT[unit_from], _METERS_PER_UNIT[unit_to]
    return None if source is None or target is None else source / target


def is_compensated(scale: float, factor: float) -> bool:
    """La inserción ya lleva aplicado el factor (±1 %; 1e-9 por el redondeo binario)."""
    if not math.isfinite(factor) or factor <= 0 or not math.isfinite(scale):
        return False
    return abs(abs(scale) / factor - 1.0) <= _COMPENSATION_TOLERANCE + 1e-9


# ---------------------------------------------------------------- comprobaciones y paginación


def normalize_checks(requested: Sequence[str] | None) -> list[str]:
    """Comprobaciones en orden fijo (FileAuditRules.NormalizeChecks). None → todas. ValueError con §5H.5."""
    if requested is None:
        return list(COMPROBACIONES)
    if not 1 <= len(requested) <= len(COMPROBACIONES):
        raise ValueError("comprobaciones: debe ser un array de 1 a 9 cadenas")
    seen: set[str] = set()
    for index, name in enumerate(requested):
        if name not in COMPROBACIONES:
            raise ValueError(f"comprobaciones[{index}]: debe ser uno de {', '.join(COMPROBACIONES)}")
        if name in seen:
            raise ValueError(f"comprobaciones: comprobación repetida '{name}'")
        seen.add(name)
    return [name for name in COMPROBACIONES if name in seen]


def parse_cursor(cursor: str | None) -> int:
    """Paging.ParseCursor (§2.4). ValueError con el texto del contrato."""
    if cursor is None:
        return 0
    if not isinstance(cursor, str) or not 1 <= len(cursor) <= MAX_CURSOR_DIGITS or not all("0" <= c <= "9" for c in cursor):
        raise ValueError("cursor: valor inválido")
    return int(cursor)


def slice_page(total: int, start: int, limit: int) -> tuple[int, str | None]:
    """Paging.Slice (§2.4): filas de la página y cursor siguiente."""
    if start > 0 and start >= total:
        raise ValueError("cursor: fuera de rango")
    count = max(0, min(limit, total - start))
    following = start + count
    return count, (str(following) if following < total else None)


def _invalid_params(detail: str) -> PluginError:
    return PluginError("INVALID_PARAMS", f"Parámetros inválidos: {detail}", {"detalle": detail})


# ---------------------------------------------------------------- hallazgos (Finding.cs, §5C.0)


@dataclass
class Finding:
    regla: str
    severidad: str
    mensaje: str
    handles: list[str] = field(default_factory=list)
    bbox: list[list[float]] | None = None
    espacio: str | None = None
    datos: dict[str, Any] = field(default_factory=dict)
    id: str = ""

    def to_dict(self) -> dict[str, Any]:
        """Objeto de §5C.0: handles recortados a 50 con datos.handles_truncados/n_handles."""
        datos = dict(self.datos)
        handles = self.handles
        if len(handles) > MAX_HANDLES:
            handles = handles[:MAX_HANDLES]
            datos["handles_truncados"] = True
            datos["n_handles"] = len(self.handles)
        return {
            "id": self.id,
            "regla": self.regla,
            "severidad": self.severidad,
            "mensaje": self.mensaje,
            "handles": handles,
            "bbox": self.bbox,
            "espacio": self.espacio,
            "datos": datos,
        }


def sort_findings(findings: list[Finding]) -> None:
    """Severidad, regla (§5C.8), llegada; después ids correlativos por regla (§5C.0)."""
    findings.sort(key=lambda f: (_SEVERITY_ORDER[f.severidad], RULE_ORDER.index(f.regla)))
    counters: Counter[str] = Counter()
    for finding in findings:
        counters[finding.regla] += 1
        finding.id = f"{finding.regla}-{counters[finding.regla]}"


def format_number(value: float) -> str:
    """Como double.ToString("0.############") con cultura invariante."""
    text = f"{value:.12f}".rstrip("0").rstrip(".")
    return "0" if text in ("", "-0") else text


# ---------------------------------------------------------------- BlockNameHeuristics (§5C.3 b)

SIN_VOCALES = "sin_vocales"
LETRA_REPETIDA = "letra_repetida"
SECUENCIA_TECLADO = "secuencia_teclado"
PAR_REPETIDO = "par_repetido"

_KEYBOARD_ROWS = ("QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM")


def _is_letter(char: str) -> bool:
    return "A" <= char <= "Z"


def _has_consonant_run(text: str, minimum: int) -> bool:
    run = 0
    for char in text:
        run = run + 1 if _is_letter(char) and char not in "AEIOU" else 0
        if run >= minimum:
            return True
    return False


def _has_repeated_letter(text: str, minimum: int) -> bool:
    run = 0
    previous = ""
    for char in text:
        if _is_letter(char) and char == previous:
            run += 1
        else:
            run = 1 if _is_letter(char) else 0
        previous = char
        if run >= minimum:
            return True
    return False


def _in_keyboard_row(fragment: str) -> bool:
    return any(fragment in row for row in _KEYBOARD_ROWS)


def _has_keyboard_sequence(text: str) -> bool:
    if len(text) == 3 and all(_is_letter(c) for c in text) and _in_keyboard_row(text):
        return True
    return any(
        all(_is_letter(c) for c in text[i : i + 4]) and _in_keyboard_row(text[i : i + 4])
        for i in range(len(text) - 3)
    )


def _has_repeated_pair(text: str) -> bool:
    return any(
        _is_letter(text[i]) and _is_letter(text[i + 1]) and text[i] != text[i + 1]
        and text[i + 2] == text[i] and text[i + 3] == text[i + 1]
        for i in range(len(text) - 3)
    )  # fmt: skip


def block_name_suspicion(nombre: str | None) -> str | None:
    """Motivo de sospecha o None. Vacíos, anónimos y A$C… no se evalúan."""
    if not nombre or nombre.startswith("*") or upper_invariant(nombre).startswith("A$C"):
        return None
    text = upper_invariant(nombre)
    if _has_consonant_run(text, 5):
        return SIN_VOCALES
    if _has_repeated_letter(text, 4):
        return LETRA_REPETIDA
    if _has_keyboard_sequence(text):
        return SECUENCIA_TECLADO
    if _has_repeated_pair(text):
        return PAR_REPETIDO
    return None


# ---------------------------------------------------------------- HeightOutliers (§5C.7)

MAYOR = "mayor"
MENOR = "menor"
NO_POSITIVA = "no_positiva"

_LOW_FACTOR = 0.2
_HIGH_FACTOR = 5.0


@dataclass(frozen=True)
class HeightOutlier:
    index: int
    handle: str
    altura: float
    motivo: str
    ratio: float | None


@dataclass(frozen=True)
class HeightAnalysis:
    mediana: float | None
    n: int
    outliers: list[HeightOutlier]


def median(values: Sequence[float]) -> float | None:
    """Mediana (media de las dos centrales si n es par); None si no hay valores."""
    if not values:
        return None
    ordered = sorted(values)
    n = len(ordered)
    return ordered[n // 2] if n % 2 == 1 else (ordered[n // 2 - 1] + ordered[n // 2]) / 2.0


def analyze_heights(heights: Sequence[tuple[str, float]]) -> HeightAnalysis:
    """Outliers en el orden del contrato: mayores (ratio desc), menores (ratio asc), no positivas."""
    values = [height for _, height in heights if math.isfinite(height)]
    middle = median(values)
    n = len(values)
    if n < 2 or middle is None:
        return HeightAnalysis(middle, n, [])

    larger: list[HeightOutlier] = []
    smaller: list[HeightOutlier] = []
    non_positive: list[HeightOutlier] = []
    for index, (handle, height) in enumerate(heights):
        if not math.isfinite(height):
            continue
        ratio = None if middle == 0 else height / middle
        if height <= 0:
            non_positive.append(HeightOutlier(index, handle, height, NO_POSITIVA, ratio))
        elif middle > 0 and height < _LOW_FACTOR * middle:
            smaller.append(HeightOutlier(index, handle, height, MENOR, ratio))
        elif middle > 0 and height > _HIGH_FACTOR * middle:
            larger.append(HeightOutlier(index, handle, height, MAYOR, ratio))

    larger.sort(key=lambda o: (-(o.ratio or 0), o.index))
    smaller.sort(key=lambda o: (o.ratio or 0, o.index))
    return HeightAnalysis(middle, n, larger + smaller + non_positive)


# ---------------------------------------------------------------- DuplicateHasher (§5C.6)

DEFAULT_TOLERANCE = 1e-6
MIN_TOLERANCE = 1e-12
MAX_TOLERANCE = 1.0
TOLERANCE_TEXT = "1e-06"

# Tipos que el respaldo compara (§5H.3); el resto va a datos.tipos_no_comparados.
COMPARED_TYPES = ("LINE", "LWPOLYLINE", "CIRCLE", "ARC", "TEXT", "MTEXT")

Point = Sequence[float]

_LONG_LIMIT = 9.2e18
_TWO_PI = 2.0 * math.pi


def _check_tolerance(tolerance: float) -> None:
    if not math.isfinite(tolerance) or not MIN_TOLERANCE <= tolerance <= MAX_TOLERANCE:
        raise ValueError("tolerancia: debe ser un número entre 1e-12 y 1")


def _round_long(value: float, tolerance: float) -> int:
    quotient = value / tolerance
    if not math.isfinite(quotient):
        return 2**63 - 1 if quotient > 0 else (-(2**63) if quotient < 0 else 0)
    rounded = round(quotient)  # Empates al par, igual que Math.Round.
    if rounded >= _LONG_LIMIT:
        return 2**63 - 1
    if rounded <= -_LONG_LIMIT:
        return -(2**63)
    return rounded


def _r(value: float, tolerance: float) -> str:
    """Número redondeado a múltiplo de la tolerancia, como entero (exponencial si no cabe en 64 bits)."""
    if not math.isfinite(value):
        return "nan"
    quotient = value / tolerance
    if not math.isfinite(quotient):
        return f"e{quotient!r}"
    rounded = round(quotient)
    return f"e{float(rounded)!r}" if abs(rounded) >= _LONG_LIMIT else str(rounded)


def _p(point: Point, tolerance: float) -> str:
    return f"{_r(point[0], tolerance)},{_r(point[1], tolerance)},{_r(point[2], tolerance)}"


def _s(text: str) -> str:
    """Prefijo de longitud: evita colisiones con separadores dentro del texto del plano."""
    return f"{len(text)}:{text}"


def normalize_angle(radians: float) -> float:
    """Ángulo normalizado a [0, 2π)."""
    if not math.isfinite(radians):
        return radians
    angle = math.fmod(radians, _TWO_PI)
    if angle < 0:
        angle += _TWO_PI
    return 0.0 if angle >= _TWO_PI else angle


def line_key(start: Point, end: Point, tolerance: float = DEFAULT_TOLERANCE) -> str:
    """Extremos ordenados: A→B y B→A son la misma línea."""
    _check_tolerance(tolerance)
    first, second = _p(start, tolerance), _p(end, tolerance)
    swap = tuple(_round_long(start[i], tolerance) for i in range(3)) > tuple(_round_long(end[i], tolerance) for i in range(3))
    return f"LINE|{second}|{first}" if swap else f"LINE|{first}|{second}"


def _join_vertices(sequence: Iterable[tuple[str, str, str]]) -> str:
    return ";".join(",".join(item) for item in sequence)


def polyline_key(
    closed: bool, elevation: float, vertices: Sequence[tuple[float, float, float]], tolerance: float = DEFAULT_TOLERANCE
) -> str:
    """Mínimo entre la secuencia directa y la invertida (x, y, bulge)."""
    _check_tolerance(tolerance)
    n = len(vertices)
    head = f"LWPOLYLINE|{'c' if closed else 'o'}|{_r(elevation, tolerance)}|{n}"
    if n == 0:
        return head

    # El bulge del último vértice solo cuenta si la polilínea es cerrada (tramo de cierre).
    direct = []
    for index, (x, y, bulge) in enumerate(vertices):
        value = 0.0 if index == n - 1 and not closed else bulge
        direct.append((_r(x, tolerance), _r(y, tolerance), _r(value, tolerance)))

    # Invertida: el tramo que sale de cada vértice es el anterior del original, recorrido al revés.
    reverse = []
    for step in range(n):
        index = n - 1 - step
        x, y, _ = vertices[index]
        if step < n - 1:
            value = -vertices[index - 1][2]
        else:
            value = -vertices[n - 1][2] if closed else 0.0
        reverse.append((_r(x, tolerance), _r(y, tolerance), _r(value, tolerance)))

    forward_text, reverse_text = _join_vertices(direct), _join_vertices(reverse)
    return f"{head}|{min(forward_text, reverse_text)}"


def circle_key(center: Point, radius: float, normal: Point, tolerance: float = DEFAULT_TOLERANCE) -> str:
    _check_tolerance(tolerance)
    return f"CIRCLE|{_p(center, tolerance)}|{_r(radius, tolerance)}|{_p(normal, tolerance)}"


def arc_key(center: Point, radius: float, start_angle: float, end_angle: float, tolerance: float = DEFAULT_TOLERANCE) -> str:
    """Ángulos en radianes."""
    _check_tolerance(tolerance)
    start = _r(normalize_angle(start_angle), tolerance)
    end = _r(normalize_angle(end_angle), tolerance)
    return f"ARC|{_p(center, tolerance)}|{_r(radius, tolerance)}|{start},{end}"


def text_key(is_mtext: bool, text: str, position: Point, height: float, tolerance: float = DEFAULT_TOLERANCE) -> str:
    """Tipo + texto exacto + posición + altura. MTEXT compara el contenido con códigos de formato."""
    _check_tolerance(tolerance)
    kind = "MTEXT" if is_mtext else "TEXT"
    return f"{kind}|{_s(text or '')}|{_p(position, tolerance)}|{_r(height, tolerance)}"


# ---------------------------------------------------------------- lectura del dibujo


@dataclass
class Space:
    """Modelo o layout: nombre de salida, handle de su registro de bloque y entidades."""

    name: str
    record_handle: str
    entities: list[Any]


@dataclass
class Layer:
    nombre: str
    color: int
    tipo_linea: str
    grosor: int


@dataclass
class Drawing:
    """Lo que las reglas necesitan del DXF ya leído."""

    doc: Any
    spaces: list[Space]
    target: Space
    rules: LpsRules
    standard_units: tuple[int, str] | None
    insunits: int
    measurement: int
    layers: list[Layer]
    inserts: dict[str, list[tuple[Any, str | None]]]
    derived: dict[str, list[str]]
    carpeta_original: Path
    carpetas_permitidas: tuple[str, ...] = ()

    def insertions(self, name: str) -> list[tuple[Any, str | None]]:
        """Inserciones directas + las de sus anónimos derivados, en orden de handle (§5B.4)."""
        found = list(self.inserts.get(upper_invariant(name), ()))
        for anonymous in self.derived.get(upper_invariant(name), ()):
            found.extend(self.inserts.get(upper_invariant(anonymous), ()))
        found.sort(key=lambda item: int(item[0].dxf.handle, 16))
        return found


@dataclass
class RuleResult:
    hallazgos: list[Finding] = field(default_factory=list)
    advertencias: list[str] = field(default_factory=list)
    resumen: dict[str, Any] = field(default_factory=dict)


def _read_spaces(doc: Any) -> list[Space]:
    """Modelo y después los layouts por TabOrder (nunca definiciones de bloque)."""
    model = doc.modelspace()
    spaces = [Space(MODEL_SPACE, model.block_record_handle, list(model))]
    layouts = [doc.layouts.get(name) for name in doc.layouts.names()]
    papers = [layout for layout in layouts if not layout.is_modelspace]
    papers.sort(key=lambda layout: (layout.dxf.taborder, upper_invariant(layout.name)))
    spaces.extend(Space(layout.name, layout.block_record_handle, list(layout)) for layout in papers)
    return spaces


def _resolve_space(spaces: list[Space], requested: str | None) -> Space:
    """"modelo" o layout sin distinguir mayúsculas (SpaceResolver.Resolve)."""
    wanted = requested or MODEL_SPACE
    for space in spaces:
        if upper_invariant(space.name) == upper_invariant(wanted):
            return space
    disponibles = [space.name for space in spaces]
    raise PluginError(
        "NOT_FOUND",
        f"El layout '{wanted}' no existe en el dibujo. Disponibles: {', '.join(disponibles)}"
        " (nombre de la pestaña, no el título del plano; listar_layouts da sus viewports).",
        {"espacio": wanted, "disponibles": disponibles},
    )


def _hex_value(text: Any) -> int:
    try:
        return int(str(text), 16)
    except ValueError:
        return 0


def _read_layers(doc: Any) -> list[Layer]:
    """Capas del archivo. ezdxf crea Defpoints al cargar si falta (Drawing._create_required_layers):
    esa capa lleva un handle ≥ $HANDSEED, que ninguna del archivo puede tener."""
    seed = _hex_value(doc.header.get("$HANDSEED", "0"))
    layers = []
    for record in doc.layers:
        name = record.dxf.name
        if upper_invariant(name) == "DEFPOINTS" and seed and _hex_value(record.dxf.handle) >= seed:
            continue
        layers.append(Layer(name, abs(record.dxf.color), record.dxf.linetype, record.dxf.lineweight))
    return layers


def _collect_inserts(
    doc: Any, spaces: list[Space]
) -> tuple[dict[str, list[tuple[Any, str | None]]], dict[str, list[str]]]:
    """Una pasada por todos los bloques: INSERT por nombre (con su espacio, None si anidado) y
    anónimos derivados de cada bloque dinámico (AcDbBlockRepBTag → BLOCK_RECORD de origen)."""
    space_names = {space.record_handle: space.name for space in spaces}
    block_names = {block.block_record.dxf.handle: block.name for block in doc.blocks}
    inserts: dict[str, list[tuple[Any, str | None]]] = defaultdict(list)
    derived: dict[str, list[str]] = defaultdict(list)
    for block in doc.blocks:
        record = block.block_record
        space = space_names.get(record.dxf.handle)
        for entity in block:
            if entity.dxftype() == "INSERT":
                inserts[upper_invariant(entity.dxf.name)].append((entity, space))
        if block.name.startswith("*") and record.has_xdata("AcDbBlockRepBTag"):
            for tag in record.get_xdata("AcDbBlockRepBTag"):
                source = block_names.get(tag.value) if tag.code == 1005 else None
                if source is not None:
                    derived[upper_invariant(source)].append(block.name)
    return dict(inserts), dict(derived)


_OCS_TYPES = frozenset({"TEXT", "ATTRIB", "ATTDEF", "INSERT", "SOLID", "TRACE"})
_POINT_ATTRIBUTES = (
    "insert", "start", "end", "center", "defpoint", "defpoint2", "defpoint3", "location",
    "vtx0", "vtx1", "vtx2", "vtx3",
)  # fmt: skip


def _entity_points(entity: Any) -> list[Any]:
    """Puntos de definición en WCS. Aproximación deliberada: ezdxf.bbox mide textos con fuentes
    y construiría una caché en el perfil del usuario."""
    kind = entity.dxftype()
    if kind == "LWPOLYLINE":
        ocs = entity.ocs()
        elevation = entity.dxf.elevation
        return [ocs.to_wcs((x, y, elevation)) for x, y in entity.get_points("xy")]
    if kind == "POLYLINE":
        return list(entity.points())
    if kind in ("CIRCLE", "ARC"):
        center = entity.ocs().to_wcs(entity.dxf.center)
        radius = entity.dxf.radius
        return [(center[0] - radius, center[1] - radius), (center[0] + radius, center[1] + radius)]
    points = [entity.dxf.get(name) for name in _POINT_ATTRIBUTES if entity.dxf.hasattr(name)]
    if kind in _OCS_TYPES:
        ocs = entity.ocs()
        return [ocs.to_wcs(point) for point in points]
    return points


def union_box(entities: Iterable[Any]) -> list[list[float]] | None:
    """Caja 2D [[xmin, ymin], [xmax, ymax]] de hasta 50 entidades, o None."""
    xs: list[float] = []
    ys: list[float] = []
    for entity in list(entities)[:MAX_HANDLES]:
        try:
            points = _entity_points(entity)
        except Exception as exc:  # noqa: BLE001 - la caja es orientativa: una entidad rara no tumba la regla
            log.debug("Sin caja para %s: %s", entity.dxftype(), exc)
            continue
        xs.extend(float(point[0]) for point in points)
        ys.extend(float(point[1]) for point in points)
    if not xs:
        return None
    return [[min(xs), min(ys)], [max(xs), max(ys)]]


def _sorted_counts(counts: Counter[str]) -> dict[str, int]:
    """Por conteo desc y nombre (AuditSupport.SortedCounts)."""
    return dict(sorted(counts.items(), key=lambda item: (-item[1], item[0])))


def _handle(entity: Any) -> str:
    return entity.dxf.handle


def _layer_of(entity: Any) -> str:
    """Capa de la entidad. Las clases desconocidas para ezdxf (DXFTagStorage) la guardan en sus etiquetas."""
    try:
        return entity.dxf.layer
    except AttributeError:
        return entity.graphic_properties().get("layer", "0")


# ---------------------------------------------------------------- reglas


def _plural(n: int, singular: str, plural: str) -> str:
    return f"{n} {singular if n == 1 else plural}"


def _insertions_text(n: int) -> str:
    return _plural(n, "inserción", "inserciones")


def _join_y(parts: list[str]) -> str:
    return parts[0] if len(parts) == 1 else f"{', '.join(parts[:-1])} y {parts[-1]}"


def _differences_message(layer: Layer, rules: LpsRules, differences: list[str]) -> str:
    spec = rules.capas_permitidas.get(layer.nombre)
    if spec is None:
        return f"La capa '{layer.nombre}' difiere del estándar LPS: {'; '.join(differences)}."
    drawing: list[str] = []
    standard: list[str] = []
    if layer.color != spec.color:
        drawing.append(f"color {layer.color}")
        standard.append(f"color {spec.color}")
    if upper_invariant(layer.tipo_linea) != upper_invariant(spec.tipo_linea):
        drawing.append(f"tipo de línea {layer.tipo_linea}")
        standard.append(f"tipo de línea {spec.tipo_linea}")
    if layer.grosor != spec.grosor:
        drawing.append(f"grosor {layer.grosor}")
        standard.append(f"grosor {spec.grosor}")
    if len(drawing) == 1:
        return f"La capa '{layer.nombre}' tiene {drawing[0]}; el estándar LPS define {standard[0].rsplit(' ', 1)[-1]}."
    return f"La capa '{layer.nombre}' tiene {_join_y(drawing)}; el estándar LPS define {_join_y(standard)}."


def _layer_finding(layer: Layer, n: int, result: lps_rules.Classification, rules: LpsRules) -> Finding | None:
    """estandar_con_diferencias → baja; 5–8 con entidades → media, vacías → baja; el resto, ninguno."""
    kind = result.clasificacion
    if kind == lps_rules.ESTANDAR_CON_DIFERENCIAS:
        severity = BAJA
        message = _differences_message(layer, rules, result.diferencias)
    elif lps_rules.is_foreign(kind):
        severity = MEDIA if n > 0 else BAJA
        content = "no tiene entidades" if n == 0 else f"tiene {_plural(n, 'entidad', 'entidades')}"
        ending = f"; capa preferida: {result.sugerencia}." if result.sugerencia is not None else "."
        message = f"La capa '{layer.nombre}' ({kind}) {content}{ending}"
    else:
        return None
    return Finding(
        "auditar_capas",
        severity,
        message,
        datos={
            "clasificacion": kind,
            "n_entidades": n,
            "diferencias": result.diferencias,
            "sugerencia": result.sugerencia,
            "patron_ajeno": result.patron_ajeno,
            "sugerencia_patron": result.sugerencia_patron,
        },
    )


def _rule_layers(drawing: Drawing) -> RuleResult:
    """Regla 1 (§5C.1). n_entidades en todos los espacios, sin bloques ni ATTRIB."""
    result = RuleResult()
    if not drawing.rules.disponible:
        result.advertencias.append(WARNING_NO_STANDARD)

    counts: Counter[str] = Counter()
    for space in drawing.spaces:
        counts.update(upper_invariant(_layer_of(entity)) for entity in space.entities)

    own = [layer for layer in drawing.layers if "|" not in layer.nombre]
    own.sort(key=lambda layer: upper_invariant(layer.nombre))
    by_class = dict.fromkeys(lps_rules.CLASSIFICATIONS, 0)
    empty = 0
    for layer in own:
        n = counts[upper_invariant(layer.nombre)]
        empty += n == 0
        classified = lps_rules.classify(layer.nombre, layer.color, layer.tipo_linea, layer.grosor, drawing.rules)
        by_class[classified.clasificacion] += 1
        finding = _layer_finding(layer, n, classified, drawing.rules)
        if finding is not None:
            result.hallazgos.append(finding)

    result.resumen = {
        "n_capas": len(own),
        "n_vacias": empty,
        "n_capas_xref": len(drawing.layers) - len(own),
        "por_clasificacion": by_class,
    }
    return result


def _rule_layer_zero(drawing: Drawing) -> RuleResult:
    """Regla 2 (§5C.2): un hallazgo media por espacio; sin definiciones de bloque ni ATTRIB."""
    result = RuleResult()
    total = 0
    for space in drawing.spaces:
        on_zero = [entity for entity in space.entities if _layer_of(entity) == "0"]
        n = len(on_zero)
        total += n
        if n == 0:
            continue
        by_type = _sorted_counts(Counter(entity.dxftype() for entity in on_zero))
        parts = [f"{kind} {count}" for kind, count in list(by_type.items())[:MAX_TYPES_IN_MESSAGE]]
        if len(by_type) > MAX_TYPES_IN_MESSAGE:
            parts.append("…")
        first = on_zero[:MAX_HANDLES]
        result.hallazgos.append(
            Finding(
                "entidades_en_capa_0",
                MEDIA,
                f"{_plural(n, 'entidad', 'entidades')} en la capa 0 en el espacio {space.name} ({', '.join(parts)}).",
                handles=[_handle(entity) for entity in first],
                bbox=union_box(first),
                espacio=space.name,
                datos={"n": n, "por_tipo": by_type, "handles_truncados": n > MAX_HANDLES, "n_handles": n},
            )
        )
    result.resumen = {"total": total}
    return result


def _distinct_scales(scales: Iterable[float], maximum: int) -> list[float]:
    distinct: list[float] = []
    for scale in scales:
        rounded = round(scale, 6)
        if rounded not in distinct:
            distinct.append(rounded)
            if len(distinct) >= maximum:
                break
    return distinct


def _block_units_finding(drawing: Drawing, name: str, units: int, inserts: list[tuple[Any, str | None]]) -> Finding:
    """Sección (a) sin medir (§5H.3): sin GeometricExtents no hay tamaño real, así que nunca sale
    "alta" ni "metadato incorrecto": siempre media con datos.medido = false."""
    n = len(inserts)
    insunits = drawing.insunits
    factor = scale_factor(units, insunits)
    prefix = (
        f"El bloque '{name}' está definido en {unit_name_es(units)} (INSUNITS={units}) "
        f"y el dibujo en {unit_name_es(insunits)} (INSUNITS={insunits}); "
    )

    sampled = inserts[:MAX_EVALUATED_INSERTIONS]
    partial = n > MAX_EVALUATED_INSERTIONS
    evaluated = [(entity, space, abs(entity.dxf.xscale)) for entity, space in sampled if math.isfinite(entity.dxf.xscale)]
    compensated = [item for item in evaluated if factor is not None and is_compensated(item[2], factor)]
    uncompensated = [item for item in evaluated if factor is not None and not is_compensated(item[2], factor)]

    if factor is None or not evaluated:
        reason = "sin factor de escala métrico" if factor is None else "no se pudo leer la escala de sus inserciones"
        message = f"{prefix}{_insertions_text(n)}, {reason}."
        shown = [entity for entity, _ in inserts]
    elif not uncompensated:
        if partial:
            who = f"las {len(evaluated)} inserciones evaluadas de sus {n} están escaladas"
        elif n == 1:
            who = "su única inserción está escalada"
        else:
            who = f"sus {_insertions_text(n)} están escaladas"
        message = (
            f"{prefix}{who} a {format_number(factor)}: compensado; "
            "riesgo solo al redefinir el bloque o insertarlo sin escala automática."
        )
        shown = [entity for entity, _, _ in compensated]
    else:
        scales = _distinct_scales((scale for _, _, scale in uncompensated), 3)
        scale_text = ("escala " if len(scales) == 1 else "escalas ") + ", ".join(format_number(s) for s in scales)
        nested = " (inserciones anidadas)" if all(space is None for _, space, _ in uncompensated) else ""
        message = (
            f"El bloque '{name}' declara unidades {unit_name(units)} y {len(uncompensated)} de sus inserciones "
            f"evaluadas no están compensadas ({scale_text}), pero su tamaño real es no medible{nested}: "
            "revise el metadato de unidades del bloque."
        )
        shown = [entity for entity, _, _ in uncompensated]

    in_space = {id(entity) for entity, space in inserts if space is not None}
    return Finding(
        "auditar_bloques",
        MEDIA,
        message,
        handles=[_handle(entity) for entity in shown],
        bbox=union_box(entity for entity in shown if id(entity) in in_space),
        datos={
            "bloque": name,
            "unidades_bloque": units,
            "unidades_bloque_nombre": unit_name(units),
            "insunits_dibujo": insunits,
            "n_inserciones": n,
            "factor_escala": factor,
            "n_compensadas": len(compensated),
            "n_no_compensadas": len(uncompensated),
            "escalas_no_compensadas": _distinct_scales((scale for _, _, scale in uncompensated), 5),
            "inserciones_evaluadas": len(evaluated),
            "muestra_parcial": partial,
            "tamano_ejemplo": None,
            "tamano_real_m": None,
            "metadato_unidades_incorrecto": False,
            "tamano_por_anidada": False,
            "medido": False,
        },
    )


def _rule_blocks(drawing: Drawing) -> RuleResult:
    """Regla 3, parcial (§5H.3): sin_uso, nombres_sospechosos y unidades_distintas por BLOCK_RECORD."""
    result = RuleResult()
    definitions = []
    anonymous = xrefs = 0
    for block in drawing.doc.blocks:
        if block.block_record.is_any_layout:
            continue
        flags = block.block.dxf.flags
        if flags & _BLOCK_XREF:
            xrefs += 1
        elif flags & _BLOCK_DEPENDENT:
            continue
        elif block.name.startswith("*"):
            anonymous += 1
        else:
            definitions.append(block)
    definitions.sort(key=lambda block: upper_invariant(block.name))

    compare_units = drawing.insunits != 0
    if not compare_units:
        result.advertencias.append(WARNING_UNITS_ZERO_BLOCKS)

    units_found: list[Finding] = []
    names_found: list[Finding] = []
    unused_found: list[Finding] = []
    for block in definitions:
        name = block.name
        inserts = drawing.insertions(name)
        n = len(inserts)
        units = block.block_record.dxf.get("units", 0)

        if compare_units and units not in (0, drawing.insunits) and n > 0:
            units_found.append(_block_units_finding(drawing, name, units, inserts))

        reason = block_name_suspicion(name)
        if reason is not None:
            names_found.append(
                Finding(
                    "auditar_bloques",
                    MEDIA if n == 0 else BAJA,
                    f"El nombre de bloque '{name}' parece arbitrario ({reason}); {_insertions_text(n)}.",
                    datos={"bloque": name, "motivo": reason, "n_inserciones": n},
                )
            )

        if n == 0:
            unused_found.append(
                Finding(
                    "auditar_bloques",
                    BAJA,
                    f"El bloque '{name}' no tiene inserciones.",
                    datos={"bloque": name, "n_entidades": len(block), "unidades_bloque": units},
                )
            )

    # Del total, no de la página: auditar_archivo pagina y el lote corta en LIMITE_HALLAZGOS.
    # Solo lo que el plugin podría subir a alta: sin inserciones no compensadas nunca lo haría (§5C.3).
    sin_medir = sum(1 for finding in units_found if finding.datos["medido"] is False and finding.datos["n_no_compensadas"] > 0)
    if sin_medir > 0:
        result.advertencias.append(advertencia_bloques_sin_medir(sin_medir))

    result.hallazgos = units_found + names_found + unused_found
    result.resumen = {
        "n_definiciones": len(definitions),
        "n_anonimos": anonymous,
        "n_xrefs": xrefs,
        "unidades_distintas": len(units_found),
        "n_sin_medir": sin_medir,
        "nombres_sospechosos": len(names_found),
        "sin_uso": len(unused_found),
    }
    return result


def _duplicate_key(entity: Any) -> str:
    kind = entity.dxftype()
    dxf = entity.dxf
    if kind == "LINE":
        return line_key(dxf.start, dxf.end)
    if kind == "LWPOLYLINE":
        vertices = [(x, y, bulge) for x, y, bulge in entity.get_points("xyb")]
        return polyline_key(entity.closed, dxf.elevation, vertices)
    if kind == "CIRCLE":
        return circle_key(entity.ocs().to_wcs(dxf.center), dxf.radius, dxf.extrusion)
    if kind == "ARC":
        center = entity.ocs().to_wcs(dxf.center)
        return arc_key(center, dxf.radius, math.radians(dxf.start_angle), math.radians(dxf.end_angle))
    if kind == "MTEXT":
        return text_key(True, entity.text, dxf.insert, dxf.char_height)  # MTEXT guarda WCS.
    return text_key(False, dxf.text, entity.ocs().to_wcs(dxf.insert), dxf.height)


@dataclass
class _DuplicateGroup:
    tipo: str
    first_index: int
    entities: list[Any] = field(default_factory=list)
    capas: list[str] = field(default_factory=list)


def _rule_duplicates(drawing: Drawing) -> RuleResult:
    """Regla 5, parcial (§5C.6, §5H.3): hash de geometría redondeada, una pasada, nunca O(n²).
    INSERT no se compara: su clave necesita el nombre efectivo de los bloques dinámicos."""
    groups: dict[str, _DuplicateGroup] = {}
    not_compared: set[str] = set()
    for index, entity in enumerate(drawing.target.entities):
        kind = entity.dxftype()
        if kind not in COMPARED_TYPES:
            not_compared.add(kind)
            continue
        key = _duplicate_key(entity)
        group = groups.get(key)
        if group is None:
            group = groups[key] = _DuplicateGroup(kind, index)
        group.entities.append(entity)
        layer = entity.dxf.layer
        if layer not in group.capas:
            group.capas.append(layer)

    found = [group for group in groups.values() if len(group.entities) >= 2]
    found.sort(key=lambda group: (-len(group.entities), group.first_index))
    types = sorted(not_compared)
    result = RuleResult()
    for group in found:
        n = len(group.entities)
        layers = f"en la capa {group.capas[0]}" if len(group.capas) == 1 else f"en las capas {', '.join(group.capas)}"
        result.hallazgos.append(
            Finding(
                "duplicados",
                MEDIA if n >= 5 else BAJA,
                f"{n} {group.tipo} idénticas (tolerancia {TOLERANCE_TEXT}) {layers}.",
                handles=[_handle(entity) for entity in group.entities],
                bbox=union_box(group.entities),
                espacio=drawing.target.name,
                datos={"tipo": group.tipo, "n": n, "capas": group.capas, "tipos_no_comparados": types},
            )
        )
    result.resumen = {
        "n_grupos": len(found),
        "n_entidades_sobrantes": sum(len(group.entities) - 1 for group in found),
        "tipos_no_comparados": types,
    }
    return result


def _text_items(entities: Iterable[Any]) -> list[tuple[Any, float]]:
    """TEXT, MTEXT y ATTRIB de los INSERT, en orden de base de datos (ATTRIB tras su INSERT)."""
    items: list[tuple[Any, float]] = []
    for entity in entities:
        kind = entity.dxftype()
        if kind == "TEXT":
            items.append((entity, entity.dxf.height))
        elif kind == "MTEXT":
            items.append((entity, entity.dxf.char_height))
        elif kind == "INSERT":
            items.extend((attribute, attribute.dxf.height) for attribute in entity.attribs)
    return items


def _rule_texts(drawing: Drawing) -> RuleResult:
    """Regla 6 (§5C.7): como mucho un hallazgo baja por motivo."""
    items = _text_items(drawing.target.entities)
    analysis = analyze_heights([(_handle(entity), height) for entity, height in items])
    result = RuleResult(resumen={"n_textos": analysis.n, "mediana": analysis.mediana})
    middle = "?" if analysis.mediana is None else format_number(analysis.mediana)
    what = {
        MENOR: f"con altura menor que 0.2 × la mediana ({middle})",
        MAYOR: f"con altura mayor que 5 × la mediana ({middle})",
        NO_POSITIVA: "con altura no positiva (≤ 0)",
    }
    for reason, description in what.items():
        outliers = [items[outlier.index][0] for outlier in analysis.outliers if outlier.motivo == reason]
        n = len(outliers)
        if n == 0:
            continue
        first = outliers[:MAX_HANDLES]
        result.hallazgos.append(
            Finding(
                "textos_fuera_de_escala",
                BAJA,
                f"{_plural(n, 'texto', 'textos')} {description} en el espacio {drawing.target.name}.",
                handles=[_handle(entity) for entity in first],
                bbox=union_box(first),
                espacio=drawing.target.name,
                datos={
                    "motivo": reason,
                    "n": n,
                    "mediana": analysis.mediana,
                    "handles_truncados": n > MAX_HANDLES,
                    "n_handles": n,
                },
            )
        )
    return result


def _allowed_folders(lps: Any) -> tuple[str, ...]:
    """carpetas_auditoria_permitidas de lps.json; aquí solo amplían lo que se consulta."""
    folders = lps.get(LPS_KEY_FOLDERS) if isinstance(lps, dict) else None
    if not isinstance(folders, list):
        return ()
    return tuple(folder.strip() for folder in folders if isinstance(folder, str) and folder.strip())


def _inside(target: str, folder: str) -> bool:
    prefix = ntpath.normcase(ntpath.normpath(folder)).rstrip("\\") + "\\"
    return ntpath.normcase(target).startswith(prefix)


def _xref_target(saved: str, carpeta_original: Path, permitted: Sequence[str]) -> str | None:
    r"""Ruta absoluta que se puede consultar, o None si el plano nombra un destino ajeno.

    La ruta guardada es texto del plano, escrito por terceros: consultar \\equipo\recurso abriría
    una conexión SMB a un equipo que decide el plano. Solo se consulta la unidad (o el recurso de
    red) de la carpeta original y las carpetas permitidas; las rutas de dispositivo, nunca.
    """
    text = saved.replace("/", "\\")
    if text.startswith(("\\\\?\\", "\\\\.\\", "\\??\\")):
        return None
    base = ntpath.abspath(str(carpeta_original))
    target = ntpath.normpath(ntpath.join(base, text))
    same_root = ntpath.normcase(ntpath.splitdrive(target)[0]) == ntpath.normcase(ntpath.splitdrive(base)[0])
    if same_root or any(_inside(target, folder) for folder in permitted):
        return target
    return None


def _rule_xrefs(drawing: Drawing) -> RuleResult:
    """Regla 7, parcial (§5H.3): estado ∈ no_encontrada | resuelta | no_comprobada; resuelta no da hallazgo."""
    result = RuleResult()
    n_xrefs = n_unchecked = 0
    for block in drawing.doc.blocks:
        if not block.block.dxf.flags & _BLOCK_XREF:
            continue
        n_xrefs += 1
        saved = block.block.dxf.get("xref_path", "") or ""
        target = _xref_target(saved, drawing.carpeta_original, drawing.carpetas_permitidas) if saved else ""
        if target and os.path.exists(target):
            continue
        name = block.name
        path_text = f"ruta guardada {saved}" if saved else "sin ruta guardada"
        inserts = drawing.insertions(name)
        spaces = {space for _, space in inserts}
        unchecked = target is None
        n_unchecked += unchecked
        state = XREF_NO_COMPROBADA if unchecked else "no_encontrada"
        message = f"La xref '{name}' tiene estado {state} ({path_text})"
        data = {"nombre": name, "estado": state, "ruta_guardada": saved or None, "ruta_resuelta": None}
        if unchecked:
            message += f": {MOTIVO_XREF_NO_COMPROBADA}"
            data["motivo"] = MOTIVO_XREF_NO_COMPROBADA
        result.hallazgos.append(
            Finding(
                "xrefs_no_resueltas",
                MEDIA if unchecked else ALTA,
                f"{message}.",
                handles=[_handle(entity) for entity, _ in inserts],
                bbox=union_box(entity for entity, _ in inserts),
                espacio=spaces.pop() if len(spaces) == 1 else None,
                datos=data,
            )
        )
    result.resumen = {"n_xrefs": n_xrefs, "n_hallazgos": len(result.hallazgos), "n_no_comprobadas": n_unchecked}
    return result


def _rule_units(drawing: Drawing) -> RuleResult:
    """Regla 8 (§5C.8): mismos textos que UnidadesCheck."""
    insunits, measurement = drawing.insunits, drawing.measurement
    standard = drawing.standard_units
    severity = None
    facts: list[str] = []
    if insunits == 0:
        severity = ALTA
        facts.append("El dibujo no declara unidades (INSUNITS=0).")
        if standard is not None:
            facts.append(f"El estándar LPS es {unit_name_es(standard[0])} (INSUNITS={standard[0]}).")
    else:
        warning = None if standard is None else lps_units_warning(insunits, *standard)
        if warning is not None:
            facts.append(warning)
        facts.extend(units_warnings(insunits, measurement))
        if facts:
            severity = MEDIA

    lps_value = None if standard is None else standard[0]
    data = {"insunits": insunits, "measurement": measurement, "insunits_lps": lps_value}
    result = RuleResult(resumen=dict(data))
    if severity is not None:
        result.hallazgos.append(Finding("unidades", severity, " ".join(facts), datos=data))
    return result


_RULES: dict[str, Callable[[Drawing], RuleResult]] = {
    "auditar_capas": _rule_layers,
    "entidades_en_capa_0": _rule_layer_zero,
    "auditar_bloques": _rule_blocks,
    "duplicados": _rule_duplicates,
    "textos_fuera_de_escala": _rule_texts,
    "xrefs_no_resueltas": _rule_xrefs,
    "unidades": _rule_units,
}


# ---------------------------------------------------------------- resumen (§5B.8)


def _file_summary(drawing: Drawing) -> dict[str, Any]:
    """Campos de §5B.8 que no van en ``archivo`` ni en la raíz (§5H.1)."""
    doc = drawing.doc
    model = drawing.spaces[0].entities
    by_type = Counter(entity.dxftype() for entity in model)
    ordered = dict(sorted(by_type.items(), key=lambda item: (-item[1], upper_invariant(item[0]))))

    usage: list[tuple[str, int]] = []
    n_xrefs = 0
    for block in doc.blocks:
        flags = block.block.dxf.flags
        if flags & _BLOCK_XREF:
            n_xrefs += 1
        elif not (block.block_record.is_any_layout or flags & _BLOCK_ANONYMOUS or block.name.startswith("*")):
            usage.append((block.name, len(drawing.insertions(block.name))))
    usage.sort(key=lambda item: (-item[1], upper_invariant(item[0])))

    return {
        "measurement": drawing.measurement,
        "n_capas": len(drawing.layers),
        "entidades_modelo": {"total": len(model), "por_tipo": ordered},
        "n_bloques": len(usage),
        "bloques_mas_usados": [{"nombre": name, "n_inserciones": n} for name, n in usage[:TOP_BLOCKS]],
        "layouts": [space.name for space in drawing.spaces[1:]],
        "n_xrefs": n_xrefs,
    }


def _dwg_version(ruta: Path, doc: Any) -> str:
    """AC10xx de los 6 primeros bytes del DWG (§5B.8). $ACADVER es el del DXF convertido."""
    try:
        with open(ruta, "rb") as file:
            header = file.read(6).decode("ascii", errors="replace")
    except OSError:
        header = ""
    return header if _DWG_VERSION.match(header) else str(doc.header.get("$ACADVER", ""))


def _file_info(ruta: Path, doc: Any) -> dict[str, Any]:
    status = ruta.stat()
    modified = datetime.fromtimestamp(status.st_mtime).astimezone()
    return {
        "ruta": str(ruta),
        "nombre": ruta.name,
        "tamano_bytes": status.st_size,
        "modificado": modified.isoformat(timespec="seconds"),
        "version_dwg": _dwg_version(ruta, doc),
        "abierto_en_sesion": False,
        "copia_temporal": True,
    }


# ---------------------------------------------------------------- orquestación


class _ReadStopped(Exception):
    """Interrumpe ezdxf desde dentro: a un hilo de Python no se le puede matar."""


class _GuardedStream:
    """Flujo de texto para ezdxf que atiende la señal de parada y suelta el archivo al llegar a EOF.

    ezdxf solo necesita ``readline``. El archivo se cierra al leer la marca 0/EOF y no al terminar
    la carga: la segunda etapa trabaja en memoria y, si el plazo se agota entonces, el DXF ya se
    puede borrar.
    """

    def __init__(self, stream: Any, stop: threading.Event) -> None:
        self._stream: Any = stream
        self._stop = stop
        self._lines = 0
        self._previous = ""

    def readline(self) -> str:
        if self._stream is None:
            return ""
        self._lines += 1
        if self._lines % _LINES_PER_CHECK == 0 and self._stop.is_set():
            raise _ReadStopped
        line = self._stream.readline()
        if line[:3] == "EOF" and line.strip() == "EOF" and self._previous.strip() == "0":
            self.close()
        self._previous = line
        return line

    def close(self) -> None:
        if self._stream is not None:
            self._stream.close()
            self._stream = None


def _load_dxf(dxf: Path, stop: threading.Event) -> Any:
    """Equivale a ezdxf.readfile(dxf, errors="replace"), con el flujo vigilado en DXF de texto."""
    import ezdxf  # noqa: PLC0415 - dependencia opcional del servidor (pyproject)
    from ezdxf.filemanagement import dxf_file_info  # noqa: PLC0415
    from ezdxf.lldxf.validator import is_binary_dxf_file, is_dxf_file  # noqa: PLC0415

    name = str(dxf)
    if is_binary_dxf_file(name):
        return ezdxf.readfile(name, errors="replace")  # ODA escribe texto; aquí no hay vigilancia
    if not is_dxf_file(name):
        raise OSError(f"'{Path(dxf).name}' no es un archivo DXF.")
    stream = _GuardedStream(open(name, encoding=dxf_file_info(name).encoding, errors="replace"), stop)  # noqa: SIM115
    try:
        doc = ezdxf.read(stream)
    finally:
        stream.close()
    doc.filename = name
    return doc


def _take_read_slot(nombre: str, limit: float, timeout_s: float, cancelar: threading.Event | None) -> None:
    """Espera a que no haya otra lectura en curso, hasta ``limit`` y atendiendo a la cancelación."""
    while not _read_slot.acquire(timeout=POLL_S):
        if cancelar is not None and cancelar.is_set():
            raise OdaCanceladoError(nombre)
        if time.monotonic() >= limit:
            raise OdaTimeoutError(timeout_s, nombre, ETAPA_ESPERA)


def _read_dxf(dxf: Path, nombre: str, deadline: float, timeout_s: float, cancelar: threading.Event | None) -> Any:
    """Lee el DXF en un hilo, con el plazo que le queda al archivo y atendiendo a la cancelación.

    Un hilo no se puede matar: en la primera etapa de ezdxf (el archivo) se detiene enseguida; en
    la segunda (en memoria) sigue hasta acabar, sin retener el DXF, consumiendo CPU y memoria, y su
    resultado se descarta. Hasta entonces retiene ``_read_slot``.
    """
    try:
        import ezdxf  # noqa: F401, PLC0415 - antes del hilo: una instalación incompleta se informa aquí
    except ImportError as exc:
        raise OdaConversionError(f"ezdxf no está instalado: {exc}") from exc

    stop = threading.Event()
    finished = threading.Event()
    outcome: dict[str, Any] = {}

    def read() -> None:
        try:
            outcome["doc"] = _load_dxf(dxf, stop)
        except Exception as exc:  # noqa: BLE001 - cualquier fallo de lectura es "no se pudo leer" (§5H.3)
            outcome["error"] = exc
        finally:
            _read_slot.release()
            finished.set()

    _take_read_slot(nombre, deadline, timeout_s, cancelar)
    try:
        threading.Thread(target=read, name="oda-lectura-dxf", daemon=True).start()
    except RuntimeError:
        _read_slot.release()
        raise
    while not finished.wait(POLL_S):
        failure: Exception | None = None
        if cancelar is not None and cancelar.is_set():
            failure = OdaCanceladoError(nombre)
        elif time.monotonic() >= deadline:
            failure = OdaTimeoutError(timeout_s, nombre, ETAPA_LECTURA)
        if failure is not None:
            stop.set()
            finished.wait(_STOP_WAIT_S)
            raise failure
    if "error" in outcome:
        error = outcome["error"]
        raise OdaConversionError(f"{type(error).__name__}: {error}") from error
    return outcome["doc"]


def _build_drawing(doc: Any, espacio: str | None, lps: dict | None, carpeta_original: Path) -> Drawing:
    standard = lps_units(lps)
    rules = LpsRules.from_json(lps) if standard is not None else LpsRules()
    spaces = _read_spaces(doc)
    target = _resolve_space(spaces, espacio)
    inserts, derived = _collect_inserts(doc, spaces)
    return Drawing(
        doc=doc,
        spaces=spaces,
        target=target,
        rules=rules,
        standard_units=standard,
        insunits=int(doc.header.get("$INSUNITS", 0)),
        measurement=int(doc.header.get("$MEASUREMENT", 1)),
        layers=_read_layers(doc),
        inserts=inserts,
        derived=derived,
        carpeta_original=carpeta_original,
        carpetas_permitidas=_allowed_folders(lps),
    )


def _not_available(checks: list[str]) -> list[dict[str, str]]:
    rows = []
    if CUADRO_AREAS in checks:
        rows.append({"comprobacion": CUADRO_AREAS, "motivo": MOTIVO_CUADRO_AREAS})
    rows.extend({"comprobacion": name, "motivo": MOTIVO_SOLO_PLUGIN} for name in NO_DISPONIBLE_ODA)
    return rows


def _rule_stats(findings: list[Finding], complete: bool) -> dict[str, Any]:
    by_severity = Counter(finding.severidad for finding in findings)
    return {
        "n": len(findings),
        ALTA: by_severity[ALTA],
        MEDIA: by_severity[MEDIA],
        BAJA: by_severity[BAJA],
        "completo": complete,
    }


def _audit_drawing(
    drawing: Drawing,
    ruta: Path,
    checks: list[str],
    limit: int,
    start: int,
    started: float,
    cancelar: threading.Event | None = None,
) -> dict[str, Any]:
    """Respuesta de §5H.1. Una regla que falla no tumba la llamada (§5C.8)."""
    findings: list[Finding] = []
    per_rule: dict[str, dict[str, Any]] = {}
    rule_summaries: dict[str, dict[str, Any]] = {}
    errors: list[dict[str, str]] = []
    warnings = list(units_warnings(drawing.insunits, drawing.measurement))
    if drawing.standard_units is not None:
        lps_warning = lps_units_warning(drawing.insunits, *drawing.standard_units)
        if lps_warning is not None:
            warnings.append(lps_warning)
    warnings.append(ADVERTENCIA_ODA)

    for rule in RULE_ORDER:
        if rule not in checks or rule == CUADRO_AREAS:
            continue
        if cancelar is not None and cancelar.is_set():
            raise OdaCanceladoError(ruta.name)
        try:
            result = _RULES[rule](drawing)
        except Exception as exc:  # noqa: BLE001 - §5C.8: se registra y se sigue con la siguiente
            log.exception("La regla de auditoría %s falló", rule)
            kind = type(exc).__name__
            errors.append({"regla": rule, "excepcion": kind})
            warnings.append(f"La regla {rule} falló ({kind}); se continuó con las demás.")
            per_rule[rule] = _rule_stats([], False)
            continue
        findings.extend(result.hallazgos)
        warnings.extend(result.advertencias)
        per_rule[rule] = _rule_stats(result.hallazgos, True)
        rule_summaries[rule] = result.resumen

    sort_findings(findings)
    try:
        count, next_cursor = slice_page(len(findings), start, limit)
    except ValueError as exc:
        raise _invalid_params(str(exc)) from exc
    by_severity = Counter(finding.severidad for finding in findings)
    insunits = drawing.insunits
    return {
        "pid": 0,
        "origen": "oda",
        "archivo": _file_info(ruta, drawing.doc),
        "espacio": drawing.target.name,
        "comprobaciones": checks,
        "no_disponible": _not_available(checks),
        "resumen": {
            "total_hallazgos": len(findings),
            "por_severidad": {ALTA: by_severity[ALTA], MEDIA: by_severity[MEDIA], BAJA: by_severity[BAJA]},
            "por_regla": per_rule,
            "n_cuadros_fuera_de_layout": 0,
        },
        "resumen_reglas": rule_summaries,
        "resumen_archivo": _file_summary(drawing) if RESUMEN in checks else None,
        "reglas_omitidas": [],
        "reglas_con_error": errors,
        "duracion_ms": int((time.perf_counter() - started) * 1000),
        "hallazgos": [finding.to_dict() for finding in findings[start : start + count]],
        "total": len(findings),
        "cursor_siguiente": next_cursor,
        "origen_texto": ORIGEN_TEXTO,
        "insunits": insunits,
        "unidades": unit_name(insunits),
        "en_metros_declarado": insunits == 6,
        "advertencias": warnings,
    }


# ---------------------------------------------------------------- evidencia de la unidad real (§5H.2, unidad_probable)

# Tope de entidades del modelo que se miran: acota tiempo y memoria en planos enormes (la muestra queda marcada).
MAX_ENTIDADES_EVIDENCIA = 500_000
# Tope de coordenadas X (y otras tantas Y) y de largos de segmento que se guardan para percentiles y medianas.
MAX_VALORES_EVIDENCIA = 400_000
_ENTIDADES_POR_COMPROBACION = 4096
# DIMENSION.dimtype & 0x0F: 2 angular y 5 angular de 3 puntos miden ángulos; 6 coordenada mide una posición absoluta.
_COTAS_NO_LONGITUD = (2, 5, 6)


def _finito(valor: Any) -> float | None:
    if isinstance(valor, bool) or not isinstance(valor, (int, float)):
        return None
    numero = float(valor)
    return numero if math.isfinite(numero) else None


def _percentil(ordenados: Sequence[float], q: float) -> float:
    """Percentil por rango más cercano sobre una lista ya ordenada (no vacía)."""
    return ordenados[min(len(ordenados) - 1, max(0, int(round(q * (len(ordenados) - 1)))))]


def _redondeo(valor: float | None) -> float | None:
    """Todo valor no finito sale None: la mediana de dos 1e308 o un ancho de -1e308 a 1e308 dan infinito, y JSON no lo admite."""
    return None if valor is None or not math.isfinite(valor) else round(valor, 6)


def evidencia_unidad(doc: Any, cancelar: threading.Event | None = None) -> dict[str, Any]:
    """Evidencia barata de la unidad REAL del modelo, en una sola pasada y sin explotar bloques.

    - ``n_cotas`` / ``cota_mediana``: DIMENSION de longitud (lineal, alineada, diámetro, radio) y ARC_DIMENSION con
      ``actual_measurement`` distinto de 0 y de None, en valor absoluto. Las angulares y de coordenada no cuentan.
    - ``n_puntos`` / ``extension``: extremos de LINE y vértices de LWPOLYLINE; extension = max(ancho, alto) entre
      los percentiles 5 y 95 de X y de Y (unos pocos puntos lejanos no la inflan).
    - ``coord_mediana`` (servidor 0.8.2): max(mediana de |x|, mediana de |y|) de esos mismos puntos: magnitud de las
      coordenadas, para reconocer un plano mezclado (casi todo junto al origen y unos pocos objetos muy lejos), cuya
      extensión p5–p95 la inflan esos objetos y no indica la unidad (lote.unidad_probable).
    - ``largo_mediano``: mediana de los segmentos no nulos de LINE y LWPOLYLINE (cuerda, sin bulge; cerrada con su
      tramo de cierre). En planta, 2D.
    - ``dimlfac``: $DIMLFAC de la cabecera (solo informativo: actual_measurement ya está en unidades de dibujo).
    - ``n_entidades`` revisadas y ``truncada`` si se llegó a un tope (la muestra son las primeras del modelo).

    Todo en unidades de dibujo; no interpreta nada: eso es ``lote.unidad_probable``. ``cancelar`` se mira cada
    pocas miles de entidades (OdaCanceladoError).
    """
    xs: list[float] = []
    ys: list[float] = []
    largos: list[float] = []
    cotas: list[float] = []
    n_puntos = 0
    n_entidades = 0
    truncada = False

    def punto(x: Any, y: Any) -> None:
        nonlocal n_puntos, truncada
        fx, fy = _finito(x), _finito(y)
        if fx is None or fy is None:
            return
        n_puntos += 1
        if len(xs) < MAX_VALORES_EVIDENCIA:
            xs.append(fx)
            ys.append(fy)
        else:
            truncada = True

    def segmento(a: tuple[float, float], b: tuple[float, float]) -> None:
        nonlocal truncada
        largo = math.hypot(b[0] - a[0], b[1] - a[1])
        if largo > 0 and math.isfinite(largo):
            if len(largos) < MAX_VALORES_EVIDENCIA:
                largos.append(largo)
            else:
                truncada = True

    for entidad in doc.modelspace():
        if n_entidades >= MAX_ENTIDADES_EVIDENCIA:
            truncada = True
            break
        n_entidades += 1
        if cancelar is not None and n_entidades % _ENTIDADES_POR_COMPROBACION == 0 and cancelar.is_set():
            raise OdaCanceladoError("evidencia de unidades")
        tipo = entidad.dxftype()
        try:
            if tipo == "LINE":
                inicio, fin = entidad.dxf.start, entidad.dxf.end
                punto(inicio[0], inicio[1])
                punto(fin[0], fin[1])
                if None not in (_finito(inicio[0]), _finito(inicio[1]), _finito(fin[0]), _finito(fin[1])):
                    segmento((float(inicio[0]), float(inicio[1])), (float(fin[0]), float(fin[1])))
            elif tipo == "LWPOLYLINE":
                vertices = [(float(x), float(y)) for x, y in entidad.get_points("xy")]
                for x, y in vertices:
                    punto(x, y)
                validos = [v for v in vertices if math.isfinite(v[0]) and math.isfinite(v[1])]
                for a, b in zip(validos, validos[1:]):
                    segmento(a, b)
                if entidad.closed and len(validos) > 2:
                    segmento(validos[-1], validos[0])
            elif tipo in ("DIMENSION", "ARC_DIMENSION"):
                # ARC_DIMENSION mide la longitud de un arco: siempre cuenta; DIMENSION solo si es de longitud.
                if tipo == "DIMENSION" and (int(entidad.dxf.get("dimtype", 0)) & 0x0F) in _COTAS_NO_LONGITUD:
                    continue
                medida = _finito(entidad.dxf.get("actual_measurement"))
                if medida is not None and medida != 0:
                    cotas.append(abs(medida))
        except (AttributeError, TypeError, ValueError, IndexError) as exc:
            # Una entidad mal formada no invalida la evidencia del resto.
            log.debug("Entidad %s ignorada en la evidencia de unidades: %s", tipo, exc)

    extension: float | None = None
    coord_mediana: float | None = None
    if xs:
        # Antes de ordenar: la magnitud se mide sobre los mismos puntos, sin importar el orden.
        coord_mediana = max(median([abs(x) for x in xs]), median([abs(y) for y in ys]))
        xs.sort()
        ys.sort()
        ancho = _percentil(xs, 0.95) - _percentil(xs, 0.05)
        alto = _percentil(ys, 0.95) - _percentil(ys, 0.05)
        extension = max(ancho, alto)
    return {
        "n_cotas": len(cotas),
        "cota_mediana": _redondeo(median(cotas)),
        "n_puntos": n_puntos,
        "extension": _redondeo(extension),
        "coord_mediana": _redondeo(coord_mediana),
        "largo_mediano": _redondeo(median(largos)),
        "dimlfac": _redondeo(_finito(doc.header.get("$DIMLFAC"))),
        "n_entidades": n_entidades,
        "truncada": truncada,
    }


def _check_free_space(ruta: Path, carpeta_trabajo: Path) -> None:
    """§5H.3: el margen del lote, porque el DXF mide unas 7 veces el DWG; si no, nada se copia.

    La copia del lote ya está en ``carpeta_trabajo`` y es una de esas veces. Sin descontarla, un archivo que pasó la
    comprobación del lote fallaría aquí, y ese INTERNAL deja la fila en ``error``, que reanudar no repite.
    """
    veces = ESPACIO_FACTOR_ODA - 1 if ruta.parent == carpeta_trabajo else ESPACIO_FACTOR_ODA
    needed = veces * ruta.stat().st_size + ESPACIO_RESERVA
    free = shutil.disk_usage(carpeta_trabajo).free
    if free < needed:
        megabyte = 1024 * 1024
        drive = carpeta_trabajo.anchor or str(carpeta_trabajo)
        detail = f"libres {free // megabyte} MB, necesarios {needed // megabyte} MB en {drive}"
        raise PluginError("INTERNAL", "No hay espacio en disco para la copia temporal.", {"detalle": detail})


def auditar_archivo_oda(
    ruta: Path,
    *,
    comprobaciones: list[str] | None,
    espacio: str | None,
    lps: dict | None,
    carpeta_trabajo: Path,
    oda_exe: Path,
    timeout_s: float,
    carpeta_original: Path | None,
    limit: int = DEFAULT_LIMIT,
    cursor: str | None = None,
    cancelar: threading.Event | None = None,
    medir: Callable[[Any, threading.Event | None], dict[str, Any]] | None = None,
    accore_exe: Path | None = None,
) -> dict[str, Any]:
    """Audita un DWG cerrado sin AutoCAD; la respuesta es la de §5H.1 con origen "oda" y pid 0.

    Servidor 0.8.2: ``accore_exe`` (opcional) es el conversor de respaldo cuando ODA no genera el DXF (``dwg_a_dxf``);
    la respuesta lleva ``conversor`` ("oda" | "accoreconsole") y, con accoreconsole, ``ADVERTENCIA_ACCORE``. El plazo
    de la lectura del DXF cuenta desde que arrancó el conversor que lo generó.

    ``carpeta_trabajo`` es temporal y propia del trabajo: se borra lo que aquí se crea
    y nada más, porque el lote guarda en ella la copia que pasa como ``ruta``.
    ``carpeta_original``: contra ella se resuelven las xrefs relativas (None → la de ``ruta``).
    ``limit``/``cursor`` paginan ``hallazgos`` (§2.4). Nunca escribe junto al archivo auditado.
    ``timeout_s`` cubre la conversión y la lectura del DXF. ``cancelar`` (opcional, el del lote): al
    activarse se mata el conversor y se lanza OdaCanceladoError (§5H.2).
    Si queda una lectura anterior en curso (abandonada por plazo o cancelación), antes de convertir
    se espera a que acabe, hasta otros ``timeout_s`` y atendiendo a ``cancelar``: si no acaba, ODA_TIMEOUT.
    ``medir`` (opcional, el del lote): recibe el documento ezdxf ya auditado, único momento en que
    existe, y lo que devuelve se fusiona en la respuesta. Sus claves son propias: las de §5H.1 no se
    repiten. Si lanza, la excepción se propaga tras limpiar. No lo cubren ``timeout_s`` ni ``duracion_ms``; si
    ``cancelar`` ya está activo al llegar a él, no se llama y se lanza OdaCanceladoError. Recibe ``(doc, cancelar)``:
    si lanza OdaCanceladoError a mitad (la cancelación llegó mientras medía), se propaga con el nombre del archivo.
    """
    started = time.perf_counter()
    ruta = Path(ruta)
    carpeta_trabajo = Path(carpeta_trabajo)
    oda_exe = Path(oda_exe)
    try:
        checks = normalize_checks(comprobaciones)
        start = parse_cursor(cursor)
    except ValueError as exc:
        raise _invalid_params(str(exc)) from exc
    if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= MAX_LIMIT:
        raise _invalid_params(f"limit: debe ser un entero entre 1 y {MAX_LIMIT}")
    if not ruta.is_file():
        raise PluginError("NOT_FOUND", f"El archivo '{ruta}' no existe.", {"archivo": str(ruta)})
    # 0.8.2: sin ODA pero con accoreconsole se convierte con accoreconsole; ODA_NO_DISPONIBLE solo si faltan los dos.
    if not oda_exe.is_file() and not (accore_exe is not None and Path(accore_exe).is_file()):
        raise OdaNoDisponibleError(oda_exe, accore_exe)

    created = not carpeta_trabajo.exists()
    try:
        carpeta_trabajo.mkdir(parents=True, exist_ok=True)
    except OSError as exc:
        raise OdaConversionError(f"No se pudo crear la carpeta de trabajo: {type(exc).__name__}: {exc}") from exc

    try:
        _check_free_space(ruta, carpeta_trabajo)
        # Antes de convertir y fuera del plazo del archivo: la espera no es culpa suya.
        _take_read_slot(ruta.name, time.monotonic() + timeout_s, timeout_s, cancelar)
        _read_slot.release()
        deadline = time.monotonic() + timeout_s
        conversion: dict[str, Any] = {}
        dxf = dwg_a_dxf(
            ruta, carpeta_trabajo, oda_exe=oda_exe, timeout_s=timeout_s, cancelar=cancelar, accore_exe=accore_exe, info=conversion
        )
        if isinstance(conversion.get("inicio"), float):
            # Con accoreconsole el plazo empieza con él: el tiempo que ODA tardó en fallar no se le descuenta.
            deadline = max(deadline, conversion["inicio"] + timeout_s)
        doc = _read_dxf(dxf, ruta.name, deadline, timeout_s, cancelar)
        drawing = _build_drawing(doc, espacio, lps, Path(carpeta_original) if carpeta_original else ruta.parent)
        response = _audit_drawing(drawing, ruta, checks, limit, start, started, cancelar)
        response["conversor"] = conversion.get("conversor", CONVERSOR_ODA)
        if response["conversor"] == CONVERSOR_ACCORE:
            response["advertencias"].append(ADVERTENCIA_SOLO_ACCORE if conversion.get("solo_accore") else ADVERTENCIA_ACCORE)
        if medir is not None:
            # La medición auxiliar no la cubre timeout_s: al menos no empieza si ya se pidió cancelar.
            if cancelar is not None and cancelar.is_set():
                raise OdaCanceladoError(ruta.name)
            try:
                response.update(medir(doc, cancelar))
            except OdaCanceladoError as exc:
                # El medidor la detecta a mitad de pasada: se informa con el archivo, como las demás cancelaciones.
                raise OdaCanceladoError(ruta.name) from exc
    finally:
        clean = remove_work_files(carpeta_trabajo)
        if created:
            try:
                carpeta_trabajo.rmdir()
            except OSError:
                log.debug("La carpeta de trabajo %s no quedó vacía", carpeta_trabajo)
    if not clean:
        response["advertencias"].append(f"No se pudo borrar la copia temporal {carpeta_trabajo}.")
    return response
