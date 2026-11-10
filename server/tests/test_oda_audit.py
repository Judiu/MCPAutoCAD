"""Respaldo sin AutoCAD de auditar_archivo (protocol.md §5H.3).

Dos bloques:
- Sin ODA: los puertos en Python reproducen los casos de las pruebas C# de GeometryCore
  (LayerClassifierTests, LayerClassifierRealDataTests, HeightOutliersTests,
  DuplicateHasherTests, BlockNameHeuristicsTests), más la conversión con un ejecutable falso
  y las reglas sobre dibujos sintéticos de ezdxf.
- Con ODA real (se salta si no está instalado): cifras de §5H.0.5 y §5H.7 sobre COPIAS de los
  tres fixtures. Los originales de tests/fixtures son de solo lectura.
"""

from __future__ import annotations

import hashlib
import io
import json
import math
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from pathlib import Path

import ezdxf
import pytest

from mcp_autocad.errors import McpAutocadError, PluginError
from mcp_autocad import lote as lote_mod
from mcp_autocad.oda import audit, convert, lps_rules
from mcp_autocad.oda.audit import ADVERTENCIA_ODA, NO_DISPONIBLE_ODA, auditar_archivo_oda
from mcp_autocad.oda.convert import OdaCanceladoError, OdaConversionError, OdaNoDisponibleError, OdaTimeoutError, dwg_a_dxf
from mcp_autocad.oda.lps_rules import LpsRules, classify, suggest, suggest_for_pattern, wildcard_match

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests" / "fixtures"
STANDARDS = ROOT / "standards"
ODA_EXE = Path(convert.ODA_EXE_DEFAULT)

LPS = json.loads((STANDARDS / "lps.json").read_text(encoding="utf-8"))
LPS_V1 = json.loads((STANDARDS / "historico" / "lps_v1_6x12_sucio.json").read_text(encoding="utf-8"))
RULES = LpsRules.from_json(LPS)
RULES_V1 = LpsRules.from_json(LPS_V1)

needs_oda = pytest.mark.skipif(not ODA_EXE.is_file(), reason=f"ODA File Converter no está en {ODA_EXE}")

# Copiadas de GeometryCore.Tests/Audit/LayerClassifierRealDataTests.cs (mismos datos, mismas cifras).
COPIA_REAL = [
    ('0', 7, 'Continuous', -3),
    ('0.4', 4, 'Continuous', 40),
    ('01-NIVELES', 82, 'Continuous', -3),
    ('1. Muros', 160, 'Continuous', 35),
    ('10. LINEA PROYECCIÓN', 251, 'Continuous', -3),
    ('12. ESTRUCTURA', 72, 'Continuous', 20),
    ('13. TECHO ESTRUCTURA', 1, 'Continuous', 20),
    ('14. TECHO LÁMINA', 7, 'Continuous', 20),
    ('15. EJES', 2, 'Punto&Rayas', 35),
    ('17. SUPERBOARD', 251, 'Continuous', -3),
    ('2. PISO', 251, 'Continuous', 9),
    ('2D Drafting - General_Pen_No__1', 7, 'Continuous', 15),
    ('2D Drafting - General_Pen_No__2', 8, 'Continuous', 15),
    ('2D Drafting - General_Pen_No__62', 253, 'Continuous', 15),
    ('2D Drafting - General_Pen_No__82', 253, 'Continuous', 15),
    ('3. MOBILIARIO', 104, 'Continuous', 0),
    ('3D EJES ESTRUCTURALES_Pen_No__19', 255, 'Continuous', 15),
    ('3D EJES ESTRUCTURALES_Pen_No__7', 7, 'Continuous', 15),
    ('3D LINEAS DE PLANTA_Pen_No__7', 7, 'Continuous', 15),
    ('4. VENTANAS', 44, 'Continuous', 0),
    ('5. PUERTAS', 2, 'Continuous', 0),
    ('5_P_VIVIENDA', 7, 'Continuous', 20),
    ('6. ACHURADOS', 252, 'Continuous', 0),
    ('6. ESTRUCTURA', 5, 'Continuous', 70),
    ('7. LINEA CORTE', 7, 'Continuous', 0),
    ('8. TEXTO', 53, 'Continuous', -3),
    ('9. Cotas', 2, 'Continuous', -3),
    ('_Above_ Riser Lines', 7, 'Trazo pequeño', 18),
    ('A-DETL-GENF', 92, 'Continuous', 18),
    ('A-DH TEXTOS', 250, 'Continuous', 13),
    ('A-DOOR-____-OTLN', 7, 'Continuous', 18),
    ('A-FLOR', 192, 'Continuous', 25),
    ('A-GENM', 13, 'Continuous', 18),
    ('A-GLAZ-____-OTLN', 7, 'Continuous', 18),
    ('A-GLAZ-____-OTLN-1', 55, 'Continuous', 18),
    ('A-GLAZ-CWMG', 51, 'Continuous', 18),
    ('A-MURO-ACHU', 252, 'Continuous', 30),
    ('A-ROOF', 132, 'Continuous', 18),
    ('A-WALL', 113, 'Continuous', 18),
    ('AC', 7, 'Continuous', -3),
    ('ACHU', 8, 'Continuous', 20),
    ('ACHURADO', 253, 'Continuous', 0),
    ('Achurados', 151, 'Continuous', 9),
    ('ACOTACIONES', 2, 'Continuous', 20),
    ('ADICIONES', 8, 'Continuous', 5),
    ('AGUAS N', 95, 'Continuous', 20),
    ('AMOBLA', 9, 'Continuous', -3),
    ('Antepecho', 201, 'Continuous', 20),
    ('ATRÁS', 251, 'Continuous', 9),
    ('BARRIOS', 7, 'Continuous', 5),
    ('BARRIOS_Pen_No__7', 7, 'Continuous', 15),
    ('Capa1', 30, 'Continuous', -3),
    ('Capa2', 150, 'Continuous', -3),
    ('CICLOP', 6, 'Continuous', -3),
    ('CODIGOMANZANA', 7, 'Continuous', 15),
    ('CODPREDIO', 8, 'Continuous', 0),
    ('CODPREDIO_Pen_No__8', 14, 'Continuous', 15),
    ('columnas', 1, 'Continuous', -3),
    ('CONCRETO', 254, 'Continuous', -3),
    ('CORTE', 220, 'Continuous', 80),
    ('COTA', 4, 'Continuous', -3),
    ('COTACOL', 161, 'Continuous', -3),
    ('Cotas', 8, 'Continuous', 13),
    ('COTAS-VIGAS', 7, 'Continuous', -3),
    ('CT', 20, 'Continuous', -3),
    ('DEFPOINTS', 7, 'Continuous', -3),
    ('DELGADA', 1, 'Continuous', 9),
    ('DEMOLICIÓN', 1, 'Continuous', 0),
    ('DESAGUES', 181, 'Continuous', 25),
    ('DETALLES', 7, 'Continuous', -3),
    ('Dibujo 2D - General_Pen_No__102', 254, 'Continuous', 15),
    ('Dibujo 2D - General_Pen_No__142', 8, 'Continuous', 15),
    ('DINTEL', 1, 'Continuous', -3),
    ('DISENO', 152, 'Continuous', -3),
    ('E-ELEC-EQPM-OTLN', 7, 'Continuous', 18),
    ('EDIFICACION', 9, 'Continuous', 0),
    ('EDIFICACION_Pen_No__9', 135, 'Continuous', 15),
    ('EJES', 1, 'CENTER', 5),
    ('Electrical Equipment', 7, 'Continuous', 18),
    ('Elevation Swing', 7, 'Dash 1_16_', 18),
    ('ESC', 6, 'Continuous', -3),
    ('ESCALERA', 6, 'Continuous', 5),
    ('ESCALERAS', 232, 'Continuous', 15),
    ('ESTRUCTURA', 250, 'Continuous', 60),
    ('Filos', 3, 'Continuous', 15),
    ('Furniture', 7, 'Continuous', 18),
    ('G-IMPT', 8, 'Continuous', 18),
    ('GRUESA', 2, 'Continuous', -3),
    ('GUIA P ILUMINACION', 2, 'Continuous', 0),
    ('hatch 1', 253, 'Continuous', 18),
    ('Hidden P-V', 252, 'HIDDEN', 0),
    ('HIDRAULICO sifones sanitarios', 5, 'Continuous', 5),
    ('HIERRO', 1, 'Continuous', -3),
    ('I-FURN', 30, 'Continuous', 18),
    ('I-FURN-____-OTLN', 7, 'Continuous', 18),
    ('LADRILLO', 30, 'Continuous', -3),
    ('Layer5', 6, 'Continuous', -3),
    ('Layer6', 2, 'Continuous', -3),
    ('Layer7', 3, 'Continuous', -3),
    ('LCOR', 52, 'Continuous', -3),
    ('LINEA DE EVACUACION', 80, 'JIS_02_0.7', 30),
    ('LINEAS SUAVES', 250, 'Continuous', 5),
    ('LOGO1', 7, 'Continuous', -3),
    ('m fijos', 2, 'Continuous', 0),
    ('MAD', 55, 'Continuous', 0),
    ('MAMPOSTERIA', 30, 'Continuous', -3),
    ('MEDIO', 181, 'Continuous', 15),
    ('Mobiliario', 251, 'Continuous', 0),
    ('MU', 6, 'Continuous', -3),
    ('MUEB', 43, 'Continuous', -3),
    ('MUEBLE', 33, 'Continuous', 13),
    ('Mueble Electro', 122, 'Continuous', 5),
    ('muebles', 253, 'Continuous', 5),
    ('muebles fijos', 2, 'Continuous', -3),
    ('MUEBLES2', 251, 'Continuous', 5),
    ('MUR BAAAJO', 241, 'Continuous', 20),
    ('MURALZADA', 250, 'Continuous', 20),
    ('MURO', 44, 'Continuous', 60),
    ('MUROESTRUCTURAL', 20, 'Continuous', 5),
    ('MUROS', 155, 'Continuous', 50),
    ('New_3D MUEBLES_Pen_No__4', 104, 'Continuous', 13),
    ('New_3D MUEBLES_Pen_No__86', 173, 'Continuous', 13),
    ('niveles', 8, 'Continuous', -3),
    ('NOMENCLATURA', 7, 'Continuous', 20),
    ('P-SANR-FIXT-OTLN', 7, 'Continuous', 18),
    ('PDF2_Text', 7, 'Continuous', -3),
    ('piso', 11, 'Continuous', -3),
    ('PREDIOURBANO', 8, 'Continuous', 9),
    ('PREDIOURBANO_Pen_No__8', 14, 'Continuous', 15),
    ('Proyeccion', 220, 'CONTINUA', 5),
    ('Proyeccion 1', 161, 'Continuous', -3),
    ('Puerta -Ventana', 6, 'Continuous', 0),
    ('PUERTAS', 9, 'Continuous', -3),
    ('PUERTAS VENTANAS', 251, 'Continuous', 0),
    ('punteadas', 150, 'ACAD_ISO02W100', 15),
    ('Q-SPCQ-____-OTLN', 7, 'Continuous', 18),
    ('RE', 3, 'Continuous', -3),
    ('REFUERZO', 3, 'Continuous', 30),
    ('Rotulo', 150, 'Continuous', 80),
    ('S-BEAM', 12, 'Continuous', 18),
    ('S-COLS', 52, 'Continuous', 18),
    ('Sanitarios', 6, 'Continuous', 5),
    ('Specialty Equipment', 7, 'Continuous', 18),
    ('T3', 20, 'Continuous', -3),
    ('TEJA', 250, 'Continuous', 20),
    ('TEJALI2', 250, 'Continuous', 13),
    ('TEXT', 250, 'Continuous', 20),
    ('TEXTO', 253, 'Continuous', 5),
    ('TEXTO 0.4', 4, 'Continuous', 5),
    ('TEXTOS', 152, 'Continuous', 15),
    ('TEXTOS 2', 6, 'Continuous', -3),
    ('TITULO-TEXTOS', 3, 'Continuous', -3),
    ('VEN1ALZADA', 250, 'Continuous', 13),
    ('VEN3ALZADA', 250, 'Continuous', 5),
    ('VENT', 7, 'Continuous', -3),
    ('VENTANA PUERTA', 250, 'Continuous', 13),
    ('ventanas', 6, 'Continuous', 9),
    ('VIGA', 2, 'Continuous', -3),
    ('Vigas', 2, 'Continuous', 30),
    ('Walls', 7, 'Continuous', 18),
    ('Zapatas', 30, 'Continuous', 50),
]

SEIS_X12_NORMALIZADO = [
    ('0', 7, 'Continuous', -3),
    ('6. ESTRUCTURA', 5, 'Continuous', 70),
    ('AC', 7, 'Continuous', -3),
    ('Achurados', 151, 'Continuous', 9),
    ('Antepecho', 201, 'Continuous', 20),
    ('columnas', 1, 'Continuous', -3),
    ('Cotas', 8, 'Continuous', 13),
    ('DEFPOINTS', 7, 'Continuous', -3),
    ('EJES', 1, 'CENTER', 5),
    ('Filos', 3, 'Continuous', 15),
    ('I-FURN-____-OTLN', 7, 'Continuous', 18),
    ('m fijos', 2, 'Continuous', 0),
    ('muebles fijos', 2, 'Continuous', -3),
    ('MUR BAAAJO', 241, 'Continuous', 20),
    ('MUROS', 155, 'Continuous', 50),
    ('Proyeccion', 220, 'CONTINUA', 5),
    ('PUERTAS', 9, 'Continuous', -3),
    ('Rotulo', 150, 'Continuous', 80),
    ('Sanitarios', 6, 'Continuous', 5),
    ('TEXTOS', 152, 'Continuous', 15),
    ('ventanas', 6, 'Continuous', 9),
]


def c(nombre: str, color: int, tipo_linea: str, grosor: int) -> lps_rules.Classification:
    return classify(nombre, color, tipo_linea, grosor, RULES)


def v1(nombre: str, color: int, tipo_linea: str, grosor: int) -> lps_rules.Classification:
    return classify(nombre, color, tipo_linea, grosor, RULES_V1)


def counts(capas: list[tuple[str, int, str, int]]) -> dict[str, int]:
    result = dict.fromkeys(lps_rules.CLASSIFICATIONS, 0)
    for capa in capas:
        result[c(*capa).clasificacion] += 1
    return result


# ------------------------------------------------------------------ lps_rules: LpsRules


def test_se_usa_el_estandar_real_del_repositorio():
    assert RULES.disponible
    assert len(RULES.capas_permitidas) == 24
    assert len(RULES.mapa_normalizacion) == 11
    assert "ARM_*" in RULES.patrones_externos
    assert RULES.capas_preferidas["muros"] == "MUROS"
    assert RULES.mapa_normalizacion["s-cols"] == "6. ESTRUCTURA"
    assert RULES.mapa_normalizacion["TEXT"] == "EJES"
    assert RULES.mapa_normalizacion["3D LINEAS DE PLANTA_Pen_No__7"] == "Rotulo"


def test_estandar_v1_historico_se_conserva():
    assert len(RULES_V1.capas_permitidas) == 161
    assert len(RULES_V1.mapa_normalizacion) == 0


def test_reglas_toleran_secciones_mal_formadas():
    r = LpsRules.from_json({
        "capas_permitidas": {"MUROS": {"color": "rojo"}, "EJES": {"color": 1, "tipo_linea": "CENTER", "grosor": 5}, "X": 3},
        "capas_mcp": [],
        "capas_preferidas": {"muros": 5, "ejes": "EJES"},
        "grupos_externos_permitidos": {"cype": "ARM_*", "otro": ["Z_*", 4]},
    })
    assert r.disponible
    assert len(r.capas_permitidas) == 1
    assert r.capas_permitidas["ejes"].plot is True
    assert len(r.capas_mcp) == 0
    assert r.capas_preferidas == {"ejes": "EJES"}
    assert r.patrones_externos == ["Z_*"]


@pytest.mark.parametrize("root", [None, [1, 2], "x", 3])
def test_reglas_raiz_no_objeto_es_vacia(root):
    assert not LpsRules.from_json(root).disponible


def test_grosor_entero_escrito_como_real_y_plot_numerico():
    r = LpsRules.from_json({"capas_permitidas": {"A": {"color": 7.0, "tipo_linea": "X", "grosor": -3, "plot": 0}}})
    assert r.capas_permitidas["a"] == lps_rules.LayerSpec(7, "X", -3, False)


def test_mapa_normalizacion_tolerante_y_con_conceptos():
    r = LpsRules.from_json({
        "capas_preferidas": {"muros": "MUROS"},
        "capas_permitidas": {
            "MUROS": {"color": 155, "tipo_linea": "Continuous", "grosor": 50},
            "AC": {"color": 7, "tipo_linea": "Continuous", "grosor": -3},
        },
        "capas_mcp": {"MCP-DIBUJO": {"color": 3, "tipo_linea": "Continuous", "grosor": -3}},
        "mapa_normalizacion": {"MURO VIEJO": "Muros", "X": 5, "Y": "", "ac": "MUROS", "MCP-DIBUJO": "MUROS", "Z": "OTRA"},
    })
    assert len(r.mapa_normalizacion) == 2
    assert r.mapa_normalizacion["muro viejo"] == "MUROS"
    assert r.mapa_normalizacion["Z"] == "OTRA"
    assert classify("AC", 7, "Continuous", -3, r).sugerencia is None
    assert classify("Z", 7, "Continuous", -3, r).sugerencia == "OTRA"


def test_mapa_normalizacion_ausente_no_cambia_nada():
    r = LpsRules.from_json({"capas_preferidas": {"textos": "TEXTOS"}})
    assert len(r.mapa_normalizacion) == 0
    assert classify("TEXT", 7, "Continuous", -3, r).sugerencia == "TEXTOS"


@pytest.mark.parametrize(
    ("name", "pattern", "expected"),
    [
        ("ARM_1", "ARM_*", True),
        ("arm_", "ARM_*", True),
        ("ARM", "ARM_*", False),
        ("XARM_1", "ARM_*", False),
        ("A_B_C", "A*C", True),
        ("ABC", "*", True),
        ("ABD", "A*C", False),
    ],
)
def test_comodin(name, pattern, expected):
    assert wildcard_match(name, pattern) is expected


def test_ocho_clasificaciones_en_orden():
    assert lps_rules.CLASSIFICATIONS == (
        "mcp", "estandar", "estandar_con_diferencias", "cype",
        "ajena_revit_aia", "ajena_exportacion", "generica", "fuera_de_estandar",
    )  # fmt: skip


# ------------------------------------------------------------------ lps_rules: classify


def test_muros_igual_al_estandar():
    r = c("MUROS", 155, "Continuous", 50)
    assert (r.clasificacion, r.diferencias, r.sugerencia) == ("estandar", [], None)


def test_muros_color_distinto():
    r = c("MUROS", 46, "Continuous", 50)
    assert r.clasificacion == "estandar_con_diferencias"
    assert r.diferencias == ["color: 46 ≠ 155"]
    assert r.sugerencia is None


def test_muros_todas_las_diferencias_en_orden_y_formato():
    r = c("muros", 46, "HIDDEN", -3)
    assert r.clasificacion == "estandar_con_diferencias"
    assert r.diferencias == ["color: 46 ≠ 155", "tipo_linea: HIDDEN ≠ Continuous", "grosor: -3 ≠ 50"]


def test_tipo_linea_sin_mayusculas():
    assert c("MUROS", 155, "CONTINUOUS", 50).clasificacion == "estandar"


@pytest.mark.parametrize(
    ("nombre", "color", "grosor"),
    [("Layer5", 6, -3), ("A-WALL", 113, 18), ("Dibujo 2D - General_Pen_No__142", 8, 15)],
)
def test_orden_con_el_estandar_v1_gana_capas_permitidas(nombre, color, grosor):
    assert v1(nombre, color, "Continuous", grosor).clasificacion == "estandar"


def test_permitidas_con_nombre_de_exportacion_gana_el_estandar():
    assert v1("PDF2_Text", 7, "Continuous", -3).clasificacion != "ajena_exportacion"
    assert v1("Capa2", 7, "Continuous", -3).clasificacion != "generica"


@pytest.mark.parametrize(
    ("nombre", "esperada"),
    [
        ("Layer5", "generica"),
        ("Layer9", "generica"),
        ("Capa2", "generica"),
        ("CAPA27", "generica"),
        ("PDF2_Text", "ajena_exportacion"),
        ("PDF_0", "ajena_exportacion"),
        ("PDF3_Lineas", "ajena_exportacion"),
        ("Plano_Pluma_N3", "ajena_exportacion"),
        ("2D Drafting - General", "ajena_exportacion"),
        ("3D Solidos", "ajena_exportacion"),
        ("Dibujo 2D - General_Pen_No__99", "ajena_exportacion"),
        ("A-WALL-FULL", "ajena_revit_aia"),
        ("ARM_PORTICO", "cype"),
        ("vig_cotas", "cype"),
        ("XYZ-RARO", "fuera_de_estandar"),
    ],
)
def test_clasificacion_por_nombre(nombre, esperada):
    assert c(nombre, 7, "Continuous", -3).clasificacion == esperada


def test_patron_cype_anclado():
    assert c("XARM_1", 3, "Continuous", -3).clasificacion != "cype"


@pytest.mark.parametrize(("color", "tipo", "grosor"), [(1, "Continuous", 35), (5, "HIDDEN", 0)])
def test_mcp_con_cualquier_propiedad(color, tipo, grosor):
    r = c("mcp-observaciones", color, tipo, grosor)
    assert (r.clasificacion, r.sugerencia, r.patron_ajeno, r.sugerencia_patron) == ("mcp", None, None, None)


def test_cotas_en_mayusculas_y_capa_0():
    assert c("COTAS", 8, "Continuous", 13).clasificacion == "estandar"
    assert c("0", 7, "Continuous", -3).clasificacion == "estandar"
    assert c("0", 1, "Continuous", -3).clasificacion == "estandar_con_diferencias"


@pytest.mark.parametrize(
    ("nombre", "esperada"),
    [
        ("muro bajo", "MUROS"),
        ("muros_viejos_2", "MUROS"),
        ("ACOTADO-X", "Cotas"),
        ("textitos", "TEXTOS"),
        ("EJE-A", "EJES"),
        ("Puerta_Ventana", "PUERTAS"),
        ("VENTANAL", "ventanas"),
        ("relleno_hatch", "Achurados"),
        ("columna-x", "columnas"),
        ("VIGAS-AEREAS", "VIGA"),
        ("nivel_piso", "niveles"),
        ("escaleras_2", "ESCALERA"),
        ("Sanitario_bano", "Sanitarios"),
        ("Mobiliário", "muebles fijos"),
        ("MUEBLES-COCINA", "muebles fijos"),
        ("TÉXTO_NOTAS", "TEXTOS"),
        ("cota_muro", "MUROS"),
    ],
)
def test_sugerencia_por_palabra_clave(nombre, esperada):
    r = c(nombre, 7, "Continuous", -3)
    assert lps_rules.is_foreign(r.clasificacion)
    assert r.sugerencia == esperada


def test_sin_palabra_clave():
    assert c("XYZ-RARO", 7, "Continuous", -3).sugerencia is None


def test_sin_estandar_clasifica_solo_lo_que_no_depende_del_archivo():
    empty = LpsRules.from_json(None)
    assert not empty.disponible
    casos = [
        ("MUROS", 155, 50, "fuera_de_estandar"),
        ("ARM_PORTICO", 3, -3, "fuera_de_estandar"),
        ("MCP-OBSERVACIONES", 1, 35, "mcp"),
        ("A-WALL", 113, 18, "ajena_revit_aia"),
        ("Layer5", 6, -3, "generica"),
        ("PDF_0", 7, -3, "ajena_exportacion"),
    ]
    for nombre, color, grosor, esperada in casos:
        assert classify(nombre, color, "Continuous", grosor, empty).clasificacion == esperada
    assert classify("muro bajo", 7, "Continuous", -3, empty).sugerencia is None
    assert classify("MCP-X", 1, "Continuous", 0, None).clasificacion == "mcp"


@pytest.mark.parametrize(
    ("nombre", "color", "grosor", "patron", "sugerencia_patron"),
    [
        ("Q-SPCQ-____-OTLN", 7, 18, "revit_aia", None),
        ("P-SANR-FIXT-OTLN", 7, 18, "revit_aia", "Sanitarios"),
        ("S-COLS", 52, 18, "revit_aia", "columnas"),
        ("A-WALL", 113, 18, "revit_aia", "MUROS"),
        ("3D EJES ESTRUCTURALES_Pen_No__7", 7, 15, "exportacion", "EJES"),
        ("Layer5", 6, -3, "generica", None),
    ],
)
def test_patron_ajeno_en_capas_del_estandar_v1(nombre, color, grosor, patron, sugerencia_patron):
    r = v1(nombre, color, "Continuous", grosor)
    assert r.clasificacion == "estandar"
    assert r.patron_ajeno == patron
    assert r.sugerencia_patron == sugerencia_patron
    assert r.sugerencia is None


@pytest.mark.parametrize(
    ("nombre", "color", "grosor", "clasif", "sugerencia", "sugerencia_patron"),
    [
        ("Q-SPCQ-____-OTLN", 7, 18, "ajena_revit_aia", "muebles fijos", "muebles fijos"),
        ("P-SANR-FIXT-OTLN", 7, 18, "ajena_revit_aia", "muebles fijos", "muebles fijos"),
        ("S-COLS", 52, 18, "ajena_revit_aia", "6. ESTRUCTURA", "6. ESTRUCTURA"),
        ("A-WALL", 113, 18, "ajena_revit_aia", None, "MUROS"),
        ("3D EJES ESTRUCTURALES_Pen_No__7", 7, 15, "ajena_exportacion", "TEXTOS", "TEXTOS"),
        ("Layer5", 6, -3, "generica", None, None),
        ("Dibujo 2D - General_Pen_No__142", 8, 15, "ajena_exportacion", "Rotulo", "Rotulo"),
    ],
)
def test_patron_ajeno_en_la_v2(nombre, color, grosor, clasif, sugerencia, sugerencia_patron):
    r = c(nombre, color, "Continuous", grosor)
    assert (r.clasificacion, r.sugerencia, r.sugerencia_patron) == (clasif, sugerencia, sugerencia_patron)


def test_mapa_normalizacion_gana_a_la_heuristica():
    assert suggest("TEXT", RULES) == "TEXTOS"
    assert suggest_for_pattern("S-COLS", RULES) == "columnas"
    assert suggest_for_pattern("P-SANR-FIXT-OTLN", RULES) == "Sanitarios"
    assert c("TEXT", 250, "Continuous", 20).sugerencia == "EJES"
    assert c("text", 250, "Continuous", 20).sugerencia == "EJES"
    assert c("TEXT", 250, "Continuous", 20).sugerencia_patron is None
    assert c("S-COLS", 52, "Continuous", 18).sugerencia_patron == "6. ESTRUCTURA"


@pytest.mark.parametrize(
    ("nombre", "esperada"),
    [
        ("A-DOOR-____-OTLN", "PUERTAS"),
        ("I-FURN-____-OTLN", "muebles fijos"),
        ("A-GLAZ", "ventanas"),
        ("A-DIMS", "Cotas"),
        ("A-ANNO-DIMS", "TEXTOS"),
        ("S-GRID", "EJES"),
        ("S-BEAM", "VIGA"),
        ("A-FLOR-STRS", "ESCALERA"),
        ("A-PATT", "Achurados"),
        ("A-FLOR-LEVL", "niveles"),
        ("P-PLMB-FIXT", "Sanitarios"),
        ("x_wall_y", "MUROS"),
        ("A-SITE", None),
        ("", None),
        ("MURO-DOOR", "MUROS"),
    ],
)
def test_suggest_for_pattern_tabla_aia(nombre, esperada):
    assert suggest_for_pattern(nombre, RULES) == esperada


def test_suggest_for_pattern_sin_reglas():
    assert suggest_for_pattern("A-WALL", LpsRules()) is None


def test_patron_ajeno_a_wall_full():
    r = c("A-WALL-FULL", 113, "Continuous", 18)
    assert (r.patron_ajeno, r.sugerencia_patron, r.sugerencia) == ("revit_aia", "MUROS", None)


def test_quitar_acentos_y_mayusculas_invariantes():
    assert lps_rules.remove_diacritics("ÁÑÜ") == "ANU"
    assert lps_rules.upper_invariant("straße") == "STRAßE"  # sin cambiar la longitud, como .NET


# ------------------------------------------------------------------ lps_rules: capas reales


def test_copia_real_por_clasificacion():
    assert len(COPIA_REAL) == 161
    assert len({lps_rules.upper_invariant(capa[0]) for capa in COPIA_REAL}) == 161
    assert counts(COPIA_REAL) == {
        "mcp": 0, "estandar": 24, "estandar_con_diferencias": 0, "cype": 0,
        "ajena_revit_aia": 18, "ajena_exportacion": 16, "generica": 5, "fuera_de_estandar": 98,
    }  # fmt: skip


def test_copia_real_las_estandar_son_exactamente_las_permitidas():
    estandar = {lps_rules.upper_invariant(capa[0]) for capa in COPIA_REAL if c(*capa).clasificacion == "estandar"}
    assert estandar == set(RULES.capas_permitidas)


@pytest.mark.parametrize(
    ("nombre", "clasif", "sugerencia", "patron", "sugerencia_patron"),
    [
        ("MUROS", "estandar", None, None, None),
        ("AC", "estandar", None, None, None),
        ("S-COLS", "ajena_revit_aia", "6. ESTRUCTURA", "revit_aia", "6. ESTRUCTURA"),
        ("Q-SPCQ-____-OTLN", "ajena_revit_aia", "muebles fijos", "revit_aia", "muebles fijos"),
        ("P-SANR-FIXT-OTLN", "ajena_revit_aia", "muebles fijos", "revit_aia", "muebles fijos"),
        ("A-DOOR-____-OTLN", "ajena_revit_aia", "PUERTAS", "revit_aia", "PUERTAS"),
        ("I-FURN-____-OTLN", "ajena_revit_aia", None, "revit_aia", "muebles fijos"),
        ("A-WALL", "ajena_revit_aia", None, "revit_aia", "MUROS"),
        ("S-BEAM", "ajena_revit_aia", None, "revit_aia", "VIGA"),
        ("3D EJES ESTRUCTURALES_Pen_No__7", "ajena_exportacion", "TEXTOS", "exportacion", "TEXTOS"),
        ("3D EJES ESTRUCTURALES_Pen_No__19", "ajena_exportacion", "EJES", "exportacion", "EJES"),
        ("Dibujo 2D - General_Pen_No__142", "ajena_exportacion", "Rotulo", "exportacion", "Rotulo"),
        ("2D Drafting - General_Pen_No__1", "ajena_exportacion", None, "exportacion", None),
        ("PDF2_Text", "ajena_exportacion", "TEXTOS", "exportacion", "TEXTOS"),
        ("Layer5", "generica", None, "generica", None),
        ("Capa2", "generica", None, "generica", None),
        ("TEXT", "fuera_de_estandar", "EJES", None, None),
        ("ACHU", "fuera_de_estandar", "6. ESTRUCTURA", None, None),
        ("Furniture", "fuera_de_estandar", "muebles fijos", None, None),
        ("MUEBLE", "fuera_de_estandar", "muebles fijos", None, None),
        ("1. Muros", "fuera_de_estandar", "MUROS", None, None),
        ("ESC", "fuera_de_estandar", None, None, None),
    ],
)
def test_copia_real_capas_concretas(nombre, clasif, sugerencia, patron, sugerencia_patron):
    capa = next(item for item in COPIA_REAL if item[0] == nombre)
    r = c(*capa)
    assert r.diferencias == []
    assert (r.clasificacion, r.sugerencia, r.patron_ajeno, r.sugerencia_patron) == (clasif, sugerencia, patron, sugerencia_patron)


def test_6x12_normalizado_veinte_estandar_y_solo_i_furn_ajena():
    assert len(SEIS_X12_NORMALIZADO) == 21
    resultado = counts(SEIS_X12_NORMALIZADO)
    assert (resultado["estandar"], resultado["ajena_revit_aia"], sum(resultado.values())) == (20, 1, 21)
    r = c("I-FURN-____-OTLN", 7, "Continuous", 18)
    assert (r.clasificacion, r.sugerencia, r.patron_ajeno, r.sugerencia_patron) == (
        "ajena_revit_aia", None, "revit_aia", "muebles fijos",
    )  # fmt: skip


# ------------------------------------------------------------------ BlockNameHeuristics


@pytest.mark.parametrize(
    ("nombre", "motivo"),
    [
        ("jgytfyuh", "sin_vocales"),
        ("BCDFG", "sin_vocales"),
        ("xyzzy", "sin_vocales"),
        ("HHHHH", "sin_vocales"),
        ("HHHH", "letra_repetida"),
        ("AAAAA", "letra_repetida"),
        ("AAAA", "letra_repetida"),
        ("asdf", "secuencia_teclado"),
        ("jkl", "secuencia_teclado"),
        ("qwer1", "secuencia_teclado"),
        ("ZXC", "secuencia_teclado"),
        ("erere", "par_repetido"),
        ("ABAB", "par_repetido"),
    ],
)
def test_nombres_sospechosos(nombre, motivo):
    assert audit.block_name_suspicion(nombre) == motivo


@pytest.mark.parametrize(
    "nombre",
    [
        "DP", "PUERTA", "LPS_ROTULO", "A$C1A2B3C4D", "WC_1", "MESA-COMEDOR", "SILLA", "TV", "VENTANA_2H", "*U12",
        "a$c7F3D2E1B", "", "BCD_FGH", "BCD1FGH", "ZXC1", "ABC", "AAA1",
    ],
)  # fmt: skip
def test_nombres_no_sospechosos(nombre):
    assert audit.block_name_suspicion(nombre) is None


# ------------------------------------------------------------------ HeightOutliers


def heights(*values: float) -> list[tuple[str, float]]:
    return [(f"H{index}", value) for index, value in enumerate(values)]


def test_mediana_impar_y_par():
    assert audit.analyze_heights(heights(0.3, 0.2, 0.1)).mediana == pytest.approx(0.2)
    r = audit.analyze_heights(heights(0.1, 0.4, 0.2, 0.3))
    assert r.mediana == pytest.approx(0.25)
    assert r.n == 4


def test_tres_motivos_con_orden_del_contrato():
    r = audit.analyze_heights(heights(0.2, 1.5, 0.2, 0.01, 0.2, 0.0, 0.2, 2.5, 0.2, 0.03, 0.2, 0.2, 0.2))
    assert r.mediana == pytest.approx(0.2)
    assert [o.handle for o in r.outliers] == ["H7", "H1", "H3", "H9", "H5"]
    assert [o.motivo for o in r.outliers] == ["mayor", "mayor", "menor", "menor", "no_positiva"]
    assert r.outliers[0].ratio == pytest.approx(12.5)
    assert r.outliers[2].ratio == pytest.approx(0.05)


def test_limites_no_son_outliers():
    assert audit.analyze_heights(heights(1, 1, 1, 0.2, 5)).outliers == []


def test_negativa_es_no_positiva():
    r = audit.analyze_heights(heights(1, 1, -0.5))
    assert [o.motivo for o in r.outliers] == ["no_positiva"]


def test_menos_de_dos_sin_outliers():
    assert audit.analyze_heights(heights(0)).outliers == []
    assert audit.analyze_heights(heights(7)).n == 1
    vacio = audit.analyze_heights(heights())
    assert vacio.mediana is None
    assert vacio.outliers == []


def test_mediana_cero_ratio_nulo():
    r = audit.analyze_heights(heights(0, 0, 0, 1))
    assert r.mediana == 0.0
    assert all(o.ratio is None for o in r.outliers)
    assert sum(o.motivo == "no_positiva" for o in r.outliers) == 3


def test_empates_por_orden_de_base_de_datos():
    assert [o.handle for o in audit.analyze_heights(heights(1, 1, 1, 9, 9)).outliers] == ["H3", "H4"]


def test_alturas_no_finitas_se_ignoran():
    r = audit.analyze_heights(heights(1, 1, math.nan, math.inf))
    assert (r.n, r.outliers) == (2, [])


# ------------------------------------------------------------------ DuplicateHasher

TOL = 1e-6


def p3(x: float, y: float, z: float = 0.0) -> tuple[float, float, float]:
    return (x, y, z)


def test_linea_ab_igual_ba():
    assert audit.line_key(p3(0, 0), p3(5, 3), TOL) == audit.line_key(p3(5, 3), p3(0, 0), TOL)


def test_linea_distinta_y_menor_que_tolerancia():
    base = audit.line_key(p3(0, 0), p3(5, 3), TOL)
    assert base != audit.line_key(p3(0, 0), p3(5, 3.001), TOL)
    assert base == audit.line_key(p3(0, 0), p3(5, 3 + 1e-9), TOL)


def test_misma_geometria_con_tolerancias_distintas():
    a, b = (p3(0, 0), p3(1.0004, 2)), (p3(0, 0), p3(1.0001, 2))
    assert audit.line_key(*a, 1e-3) == audit.line_key(*b, 1e-3)
    assert audit.line_key(*a, 1e-6) != audit.line_key(*b, 1e-6)


def test_polilinea_abierta_directa_igual_a_invertida():
    direct = [(0, 0, 0.5), (10, 0, 0), (10, 5, -0.25), (0, 5, 0)]
    reverse = [(0, 5, 0.25), (10, 5, 0), (10, 0, -0.5), (0, 0, 0)]
    assert audit.polyline_key(False, 0, direct, TOL) == audit.polyline_key(False, 0, reverse, TOL)


def test_polilinea_abierta_el_bulge_del_ultimo_vertice_no_cuenta():
    a = audit.polyline_key(False, 0, [(0, 0, 0), (1, 0, 0.7)], TOL)
    assert a == audit.polyline_key(False, 0, [(0, 0, 0), (1, 0, 0)], TOL)


def test_polilinea_cerrada_directa_igual_a_invertida():
    direct = [(0, 0, 0.1), (4, 0, 0.2), (4, 3, 0.3)]
    reverse = [(4, 3, -0.2), (4, 0, -0.1), (0, 0, -0.3)]
    assert audit.polyline_key(True, 2.0, direct, TOL) == audit.polyline_key(True, 2.0, reverse, TOL)


def test_polilinea_cerrada_bulge_de_cierre_distinto():
    a = audit.polyline_key(True, 0, [(0, 0, 0), (4, 0, 0), (4, 3, 0)], TOL)
    assert a != audit.polyline_key(True, 0, [(0, 0, 0), (4, 0, 0), (4, 3, 0.5)], TOL)


def test_polilinea_cerrada_abierta_y_elevacion():
    vertices = [(0, 0, 0), (4, 0, 0), (4, 3, 0)]
    assert audit.polyline_key(True, 0, vertices, TOL) != audit.polyline_key(False, 0, vertices, TOL)
    assert audit.polyline_key(False, 0, vertices, TOL) != audit.polyline_key(False, 1, vertices, TOL)


def test_textos_exactos_y_tipo():
    key = audit.text_key
    assert key(False, "COCINA", p3(1, 2), 0.2, TOL) != key(False, "COCINA ", p3(1, 2), 0.2, TOL)
    assert key(False, "cocina", p3(1, 2), 0.2, TOL) != key(False, "COCINA", p3(1, 2), 0.2, TOL)
    assert key(False, "A", p3(1, 2), 0.2, TOL) != key(True, "A", p3(1, 2), 0.2, TOL)
    assert key(False, "A|0,0,0", p3(0, 0), 1, TOL) != key(False, "A", p3(0, 0), 1, TOL)


def test_circulo_y_arco():
    assert audit.circle_key(p3(1, 1), 2, p3(0, 0, 1), TOL) == audit.circle_key(p3(1, 1), 2, p3(0, 0, 1), TOL)
    assert audit.circle_key(p3(1, 1), 2, p3(0, 0, 1), TOL) != audit.circle_key(p3(1, 1), 2, p3(0, 0, -1), TOL)
    assert audit.arc_key(p3(0, 0), 1, -math.pi / 2, math.pi, TOL) == audit.arc_key(p3(0, 0), 1, 3 * math.pi / 2, math.pi, TOL)


def test_tipos_distintos_nunca_colisionan():
    assert audit.circle_key(p3(0, 0), 1, p3(0, 0, 1), TOL) != audit.arc_key(p3(0, 0), 1, 0, 0, TOL)


def test_formato_entero_invariante():
    key = audit.line_key(p3(-1.5, 2.25), p3(3.125, 1e5), 1e-3)
    assert "-1500,2250,0" in key
    assert " " not in key


@pytest.mark.parametrize("tolerancia", [0.0, -1e-6, 1e-13, 1.5, math.nan])
def test_tolerancia_invalida_lanza(tolerancia):
    with pytest.raises(ValueError, match="tolerancia"):
        audit.line_key(p3(0, 0), p3(1, 1), tolerancia)


def test_agrupacion_con_diccionario_es_lineal():
    groups: dict[str, int] = {}
    for index in range(20000):
        k = index % 10000
        key = audit.line_key(p3(k, 0), p3(k, 1)) if index < 10000 else audit.line_key(p3(k, 1), p3(k, 0))
        groups[key] = groups.get(key, 0) + 1
    assert len(groups) == 10000
    assert set(groups.values()) == {2}


def test_redondeo_al_par_como_math_round():
    # Math.Round(2.5) = 2 y Math.Round(3.5) = 4: dos valores en celdas distintas de un redondeo "normal".
    assert audit.line_key(p3(2.5, 0), p3(9, 9), 1.0) == audit.line_key(p3(2, 0), p3(9, 9), 1.0)
    assert audit.line_key(p3(3.5, 0), p3(9, 9), 1.0) == audit.line_key(p3(4, 0), p3(9, 9), 1.0)


# ------------------------------------------------------------------ comprobaciones, paginación, textos


def test_normalize_checks_defecto_y_orden_fijo():
    assert audit.normalize_checks(None) == [
        "auditar_capas", "entidades_en_capa_0", "auditar_bloques", "verificar_cuadro_areas",
        "duplicados", "textos_fuera_de_escala", "xrefs_no_resueltas", "unidades", "resumen",
    ]  # fmt: skip
    assert audit.normalize_checks(["resumen", "unidades"]) == ["unidades", "resumen"]


def test_normalize_checks_textos_del_contrato():
    with pytest.raises(ValueError) as info:
        audit.normalize_checks(["unidades", "otra"])
    assert str(info.value) == (
        "comprobaciones[1]: debe ser uno de auditar_capas, entidades_en_capa_0, auditar_bloques, "
        "verificar_cuadro_areas, duplicados, textos_fuera_de_escala, xrefs_no_resueltas, unidades, resumen"
    )
    with pytest.raises(ValueError) as info:
        audit.normalize_checks(["unidades", "unidades"])
    assert str(info.value) == "comprobaciones: comprobación repetida 'unidades'"
    with pytest.raises(ValueError):
        audit.normalize_checks([])


@pytest.mark.parametrize("cursor", ["", "abc", "-1", "1234567890", "1.5"])
def test_cursor_invalido(cursor):
    with pytest.raises(ValueError, match="cursor: valor inválido"):
        audit.parse_cursor(cursor)


def test_paginacion_como_paging_slice():
    assert audit.parse_cursor(None) == 0
    assert audit.parse_cursor("12") == 12
    assert audit.slice_page(0, 0, 200) == (0, None)
    assert audit.slice_page(5, 0, 2) == (2, "2")
    assert audit.slice_page(5, 4, 2) == (1, None)
    with pytest.raises(ValueError, match="cursor: fuera de rango"):
        audit.slice_page(5, 5, 2)


def test_textos_literales_del_contrato():
    assert ADVERTENCIA_ODA == (
        "Resultado del respaldo ODA + ezdxf, sin AutoCAD: no se comprobaron cuadro de áreas, "
        "bloques dinámicos ni tamaños reales de inserciones anidadas."
    )
    assert NO_DISPONIBLE_ODA == [
        "propiedades de bloques dinámicos",
        "tamaño real de inserciones anidadas (tamano_por_anidada)",
        "en_layout por viewports",
        "entidades proxy y objetos de terceros",
    ]
    assert audit.MOTIVO_CUADRO_AREAS == "el reconocimiento del cuadro de áreas (AreaTable, viewports) solo está en el plugin"


@pytest.mark.parametrize(
    ("insunits", "measurement", "esperadas"),
    [
        (6, 1, []),
        (0, 1, [audit.WARNING_UNITS_UNKNOWN]),
        (6, 0, ["MEASUREMENT=0 (imperial) con INSUNITS métrico: unidades inconsistentes."]),
        (1, 1, ["MEASUREMENT=1 (métrico) con INSUNITS imperial: unidades inconsistentes."]),
        (1, 0, []),
        (10, 1, []),
    ],
)
def test_advertencias_de_unidades(insunits, measurement, esperadas):
    assert audit.units_warnings(insunits, measurement) == esperadas


def test_advertencia_lps():
    assert audit.lps_units_warning(6, 6, "m") is None
    assert audit.lps_units_warning(1, 6, "m") == "El dibujo está en pulgadas (INSUNITS=1); el estándar LPS es metros (INSUNITS=6)."
    assert audit.lps_units_warning(0, 6, "m") == "El dibujo no declara unidades (INSUNITS=0); el estándar LPS es metros (INSUNITS=6)."
    assert audit.lps_units_warning(6, 99, "xx") == "El dibujo está en metros (INSUNITS=6); el estándar LPS es xx (INSUNITS=99)."


def test_unidades_tabla_y_factor():
    assert [audit.unit_name(n) for n in (0, 6, 21, 99)] == ["desconocida", "m", "us_ft", "desconocida"]
    assert audit.scale_factor(4, 6) == pytest.approx(0.001)
    assert audit.scale_factor(18, 6) is None
    assert audit.is_compensated(0.00101, 0.001)
    assert audit.is_compensated(-0.001, 0.001)
    assert not audit.is_compensated(1.0, 0.001)


@pytest.mark.parametrize(("valor", "texto"), [(0.2, "0.2"), (1000.0, "1000"), (0.001, "0.001"), (0.0, "0"), (0.045, "0.045")])
def test_formato_de_numeros(valor, texto):
    assert audit.format_number(valor) == texto


def test_orden_e_ids_de_hallazgos_y_recorte_de_handles():
    findings = [
        audit.Finding("unidades", "alta", "u"),
        audit.Finding("auditar_capas", "baja", "c1"),
        audit.Finding("auditar_capas", "media", "c2"),
        audit.Finding("duplicados", "alta", "d", handles=[str(n) for n in range(60)]),
    ]
    audit.sort_findings(findings)
    assert [f.id for f in findings] == ["duplicados-1", "unidades-1", "auditar_capas-1", "auditar_capas-2"]
    dictionary = findings[0].to_dict()
    assert list(dictionary) == ["id", "regla", "severidad", "mensaje", "handles", "bbox", "espacio", "datos"]
    assert len(dictionary["handles"]) == 50
    assert dictionary["datos"] == {"handles_truncados": True, "n_handles": 60}


def test_importar_el_paquete_no_exige_ezdxf():
    code = "import sys, mcp_autocad.oda; sys.exit('ezdxf' in sys.modules)"
    env = {**os.environ, "PYTHONPATH": str(ROOT / "server")}
    assert subprocess.run([sys.executable, "-c", code], env=env, timeout=60, check=False).returncode == 0


# ------------------------------------------------------------------ convert con ejecutable falso

FAKE_DXF = """  0
SECTION
  2
ENTITIES
  0
ENDSEC
  0
EOF
"""


def fake_oda(tmp_path: Path, body: str) -> Path:
    """Conversor falso: un .bat que lanza un script. Registra argumentos, cwd y carpeta de entrada."""
    folder = tmp_path / "falso"
    folder.mkdir()
    script = folder / "oda_falso.py"
    script.write_text(
        "import json, os, sys\n"
        "from pathlib import Path\n"
        "entrada, salida = Path(sys.argv[1]), Path(sys.argv[2])\n"
        f"registro = Path({str(folder / 'registro.json')!r})\n"
        "registro.write_text(json.dumps({'argv': sys.argv[1:], 'cwd': os.getcwd(),\n"
        "    'entrada': sorted(p.name for p in entrada.iterdir())}))\n" + body,
        encoding="utf-8",
    )
    bat = folder / "ODAFileConverter.bat"
    bat.write_text(f'@"{sys.executable}" -I "{script}" %*\n', encoding="ascii")
    return bat


WRITES_DXF = f"(salida / 'entrada.dxf').write_text({FAKE_DXF!r})\n"


@pytest.fixture
def dwg(tmp_path: Path) -> Path:
    folder = tmp_path / "origen"
    folder.mkdir()
    path = folder / "PLANO año (1).DWG"
    path.write_bytes(b"AC1027" + b"\0" * 100)
    return path


def test_convert_devuelve_el_dxf_y_usa_argumentos_fijos(tmp_path, dwg):
    exe = fake_oda(tmp_path, WRITES_DXF)
    work = tmp_path / "trabajo"
    work.mkdir()
    dxf = dwg_a_dxf(dwg, work, oda_exe=exe, timeout_s=30)
    assert dxf == work / "oda_out" / "entrada.dxf"
    record = json.loads((exe.parent / "registro.json").read_text())
    assert record["argv"] == [str(work / "oda_in"), str(work / "oda_out"), "ACAD2018", "DXF", "0", "1", "*.DWG"]
    assert Path(record["cwd"]) == work
    assert record["entrada"] == ["entrada.dwg"]  # solo la copia
    assert sorted(item.name for item in work.iterdir()) == ["oda_out"]  # copia y registro ya borrados
    assert dwg.read_bytes().startswith(b"AC1027")


def test_convert_limpia_restos_de_un_trabajo_interrumpido(tmp_path, dwg):
    exe = fake_oda(tmp_path, WRITES_DXF)
    work = tmp_path / "trabajo"
    (work / "oda_in").mkdir(parents=True)
    (work / "oda_in" / "ajeno.dwg").write_bytes(b"x")
    dwg_a_dxf(dwg, work, oda_exe=exe, timeout_s=30)
    assert json.loads((exe.parent / "registro.json").read_text())["entrada"] == ["entrada.dwg"]


def test_convert_sin_ejecutable_texto_literal(tmp_path, dwg):
    with pytest.raises(OdaNoDisponibleError) as info:
        dwg_a_dxf(dwg, tmp_path / "t", oda_exe=Path(r"C:\no\existe.exe"), timeout_s=5)
    assert str(info.value) == (
        r"[ODA_NO_DISPONIBLE] No se encontró ODA File Converter en 'C:\no\existe.exe'; instálelo o defina MCP_AUTOCAD_ODA."
    )
    assert info.value.code == "ODA_NO_DISPONIBLE"
    assert isinstance(info.value, McpAutocadError)
    assert not (tmp_path / "t").exists()


def test_convert_timeout_mata_el_proceso_y_texto_literal(tmp_path, dwg):
    folder = tmp_path / "falso"
    folder.mkdir()
    exe = folder / "lento.bat"
    exe.write_text("@ping -n 4 127.0.0.1 >nul 2>&1\n", encoding="ascii")
    start = time.perf_counter()
    with pytest.raises(OdaTimeoutError) as info:
        dwg_a_dxf(dwg, tmp_path / "trabajo", oda_exe=exe, timeout_s=0.3)
    assert time.perf_counter() - start < 3
    assert str(info.value) == f"[ODA_TIMEOUT] ODA File Converter superó 0.3 s con '{dwg.name}'."
    assert info.value.code == "ODA_TIMEOUT"
    assert not (tmp_path / "trabajo" / "oda_in").exists()
    assert not (tmp_path / "trabajo" / "oda_out").exists()


@pytest.fixture
def slow_converter(monkeypatch) -> list:
    """Un solo proceso real que no termina, como ODA: un .bat dejaría un nieto con el registro abierto."""
    started: list[subprocess.Popen] = []
    real_popen = subprocess.Popen

    def popen(_args, **kwargs):
        started.append(real_popen([sys.executable, "-I", "-c", "import time; time.sleep(60)"], **kwargs))
        return started[-1]

    monkeypatch.setattr(convert.subprocess, "Popen", popen)
    return started


def test_convert_cancelar_mata_el_conversor_sin_esperar_el_plazo(tmp_path, dwg, slow_converter):
    exe = Path(sys.executable)
    cancelar = threading.Event()
    threading.Timer(0.3, cancelar.set).start()
    start = time.perf_counter()
    with pytest.raises(OdaCanceladoError) as info:
        dwg_a_dxf(dwg, tmp_path / "trabajo", oda_exe=exe, timeout_s=60, cancelar=cancelar)
    assert time.perf_counter() - start < 5
    assert str(info.value) == f"[CANCELADO] Auditoría cancelada con '{dwg.name}'."
    assert info.value.code == "CANCELADO"
    assert slow_converter[0].poll() is not None  # el proceso ya no existe
    assert list((tmp_path / "trabajo").iterdir()) == []


def test_convert_ya_cancelado_no_lanza_el_conversor(tmp_path, dwg):
    exe = fake_oda(tmp_path, WRITES_DXF)
    cancelar = threading.Event()
    cancelar.set()
    with pytest.raises(OdaCanceladoError):
        dwg_a_dxf(dwg, tmp_path / "trabajo", oda_exe=exe, timeout_s=30, cancelar=cancelar)
    assert not (exe.parent / "registro.json").exists()


def test_convert_termina_sin_tocar_el_plazo_aunque_haya_evento(tmp_path, dwg):
    exe = fake_oda(tmp_path, WRITES_DXF)
    work = tmp_path / "trabajo"
    assert dwg_a_dxf(dwg, work, oda_exe=exe, timeout_s=30, cancelar=threading.Event()) == work / "oda_out" / "entrada.dxf"


def test_convert_sin_dxf_cita_el_informe_de_oda_sin_la_ruta_temporal(tmp_path, dwg):
    # Como el ODA real: código 0, ningún DXF y el motivo en <nombre>.dxf.err.
    exe = fake_oda(
        tmp_path,
        "ruta = (entrada / 'entrada.dwg').as_posix()\n"
        "(salida / 'entrada.dxf.err').write_text(f'OdError thrown during readFile of drawing \"{ruta}\"')\n",
    )
    work = tmp_path / "trabajo"
    work.mkdir()
    with pytest.raises(OdaConversionError) as info:
        dwg_a_dxf(dwg, work, oda_exe=exe, timeout_s=30)
    assert str(info.value) == "[INTERNAL] No se pudo leer el archivo DWG."
    assert info.value.code == "INTERNAL"
    assert info.value.data["detalle"] == f'OdError thrown during readFile of drawing "{dwg.name}"'
    assert list(work.iterdir()) == []


def test_convert_sin_dxf_ni_informe_cita_el_codigo_de_salida(tmp_path, dwg):
    exe = fake_oda(tmp_path, "sys.exit(3)\n")
    with pytest.raises(OdaConversionError) as info:
        dwg_a_dxf(dwg, tmp_path / "trabajo", oda_exe=exe, timeout_s=30)
    assert info.value.detalle == "ODA File Converter no generó el DXF (código de salida 3)."


def test_convert_archivo_que_no_se_puede_copiar(tmp_path):
    exe = fake_oda(tmp_path, WRITES_DXF)
    with pytest.raises(OdaConversionError, match="No se pudo leer") as info:
        dwg_a_dxf(tmp_path / "no_existe.dwg", tmp_path / "trabajo", oda_exe=exe, timeout_s=30)
    assert "No se pudo copiar" in info.value.detalle


def test_rutas_y_plazo_por_defecto(monkeypatch):
    monkeypatch.delenv("MCP_AUTOCAD_ODA", raising=False)
    monkeypatch.delenv("MCP_AUTOCAD_ODA_TIMEOUT", raising=False)
    assert convert.oda_exe_por_defecto() == Path(r"C:\Program Files\ODA\ODAFileConverter 27.1.0\ODAFileConverter.exe")
    assert convert.timeout_por_defecto() == 300.0  # 0.8.2: antes 120
    monkeypatch.setenv("MCP_AUTOCAD_ODA", r"D:\oda\conv.exe")
    monkeypatch.setenv("MCP_AUTOCAD_ODA_TIMEOUT", "45.5")
    assert convert.oda_exe_por_defecto() == Path(r"D:\oda\conv.exe")
    assert convert.timeout_por_defecto() == 45.5


@pytest.mark.parametrize("valor", ["abc", "0", "-5", "inf", "nan"])
def test_plazo_invalido_usa_el_defecto(monkeypatch, valor):
    monkeypatch.setenv("MCP_AUTOCAD_ODA_TIMEOUT", valor)
    assert convert.timeout_por_defecto() == 300.0


# ------------------------------------------------------------------ reglas sobre dibujos sintéticos


def new_doc(insunits: int = 6, measurement: int = 1):
    doc = ezdxf.new("R2018")
    doc.header["$INSUNITS"] = insunits
    doc.header["$MEASUREMENT"] = measurement
    return doc


def audit_doc(doc, tmp_path: Path, *, checks=None, espacio="modelo", lps=LPS, limit=200, start=0, original=None):
    """Audita un dibujo en memoria con el mismo código que usa auditar_archivo_oda tras leer el DXF."""
    ruta = tmp_path / "plano.dwg"
    ruta.write_bytes(b"AC1027")
    drawing = audit._build_drawing(doc, espacio, lps, original or tmp_path)
    response = audit._audit_drawing(drawing, ruta, audit.normalize_checks(checks), limit, start, time.perf_counter())
    json.dumps(response)  # siempre serializable
    return response


def rule_findings(response: dict, regla: str) -> list[dict]:
    return [finding for finding in response["hallazgos"] if finding["regla"] == regla]


def test_forma_de_la_respuesta(tmp_path):
    r = audit_doc(new_doc(), tmp_path)
    assert list(r) == [
        "pid", "origen", "archivo", "espacio", "comprobaciones", "no_disponible", "resumen", "resumen_reglas",
        "resumen_archivo", "reglas_omitidas", "reglas_con_error", "duracion_ms", "hallazgos", "total",
        "cursor_siguiente", "origen_texto", "insunits", "unidades", "en_metros_declarado", "advertencias",
    ]  # fmt: skip
    assert (r["pid"], r["origen"], r["espacio"]) == (0, "oda", "modelo")
    assert r["origen_texto"] == "contenido_del_plano"
    assert r["archivo"]["copia_temporal"] is True
    assert r["archivo"]["abierto_en_sesion"] is False
    assert r["archivo"]["version_dwg"] == "AC1027"
    assert re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d", r["archivo"]["modificado"])
    assert r["reglas_omitidas"] == []
    assert r["resumen"]["n_cuadros_fuera_de_layout"] == 0


def test_no_disponible_y_por_regla(tmp_path):
    r = audit_doc(new_doc(), tmp_path)
    names = [row["comprobacion"] for row in r["no_disponible"]]
    assert names == ["verificar_cuadro_areas", *NO_DISPONIBLE_ODA]
    assert r["no_disponible"][0]["motivo"] == audit.MOTIVO_CUADRO_AREAS
    assert all(row["motivo"] for row in r["no_disponible"])
    assert "verificar_cuadro_areas" not in r["resumen"]["por_regla"]
    assert "verificar_cuadro_areas" in r["comprobaciones"]


def test_sin_pedir_cuadro_de_areas_solo_la_lista_fija(tmp_path):
    r = audit_doc(new_doc(), tmp_path, checks=["unidades"])
    assert [row["comprobacion"] for row in r["no_disponible"]] == NO_DISPONIBLE_ODA
    assert list(r["resumen"]["por_regla"]) == ["unidades"]
    assert r["resumen_archivo"] is None


def test_advertencia_oda_siempre_y_despues_de_las_de_unidades(tmp_path):
    r = audit_doc(new_doc(insunits=4), tmp_path, checks=["resumen"])
    assert r["advertencias"] == [
        "El dibujo está en milímetros (INSUNITS=4); el estándar LPS es metros (INSUNITS=6).",
        ADVERTENCIA_ODA,
    ]
    assert r["en_metros_declarado"] is False


def test_insunits_cero_alta_y_advertencias(tmp_path):
    r = audit_doc(new_doc(insunits=0), tmp_path, checks=["unidades", "auditar_bloques"])
    assert r["unidades"] == "desconocida"
    [finding] = rule_findings(r, "unidades")
    assert finding["severidad"] == "alta"
    assert finding["mensaje"] == "El dibujo no declara unidades (INSUNITS=0). El estándar LPS es metros (INSUNITS=6)."
    assert finding["datos"] == {"insunits": 0, "measurement": 1, "insunits_lps": 6}
    assert r["advertencias"][:2] == [
        audit.WARNING_UNITS_UNKNOWN,
        "El dibujo no declara unidades (INSUNITS=0); el estándar LPS es metros (INSUNITS=6).",
    ]
    assert "INSUNITS=0: no se comparan las unidades de los bloques." in r["advertencias"]


def test_unidades_inconsistentes_como_arq_cliente_b(tmp_path):
    r = audit_doc(new_doc(insunits=6, measurement=0), tmp_path, checks=["unidades"])
    [finding] = rule_findings(r, "unidades")
    assert finding["severidad"] == "media"
    assert finding["mensaje"] == "MEASUREMENT=0 (imperial) con INSUNITS métrico: unidades inconsistentes."


def test_unidades_en_metros_sin_hallazgo_y_sin_estandar(tmp_path):
    assert rule_findings(audit_doc(new_doc(), tmp_path, checks=["unidades"]), "unidades") == []
    r = audit_doc(new_doc(insunits=4), tmp_path, checks=["unidades"], lps=None)
    assert rule_findings(r, "unidades") == []
    assert r["resumen_reglas"]["unidades"]["insunits_lps"] is None


def test_sin_lps_avisa_y_no_compara(tmp_path):
    doc = new_doc()
    doc.layers.add("MUROS", color=155, lineweight=50)
    r = audit_doc(doc, tmp_path, checks=["auditar_capas"], lps=None)
    assert audit.WARNING_NO_STANDARD in r["advertencias"]
    assert r["resumen_reglas"]["auditar_capas"]["por_clasificacion"]["estandar"] == 0


def test_capas_mensajes_y_severidades(tmp_path):
    doc = new_doc()
    doc.layers.add("MUROS", color=46, lineweight=50)
    doc.layers.add("EJES", color=2, linetype="Continuous", lineweight=9)
    doc.layers.add("muro viejo")
    doc.layers.add("A-WALL")
    doc.layers.add("XREF|MUROS")
    msp = doc.modelspace()
    msp.add_line((0, 0), (1, 0), dxfattribs={"layer": "muro viejo"})
    layout = doc.layouts.get("Layout1")
    layout.add_line((0, 0), (1, 0), dxfattribs={"layer": "muro viejo"})
    block = doc.blocks.new("B")
    block.add_line((0, 0), (1, 0), dxfattribs={"layer": "A-WALL"})  # dentro de bloque: no cuenta

    r = audit_doc(doc, tmp_path, checks=["auditar_capas"])
    by_message = {finding["mensaje"]: finding for finding in rule_findings(r, "auditar_capas")}
    assert by_message["La capa 'muro viejo' (fuera_de_estandar) tiene 2 entidades; capa preferida: MUROS."]["severidad"] == "media"
    assert by_message["La capa 'A-WALL' (ajena_revit_aia) no tiene entidades."]["severidad"] == "baja"
    assert by_message["La capa 'MUROS' tiene color 46; el estándar LPS define 155."]["datos"]["diferencias"] == ["color: 46 ≠ 155"]
    assert (
        "La capa 'EJES' tiene color 2, tipo de línea Continuous y grosor 9; "
        "el estándar LPS define color 1, tipo de línea CENTER y grosor 5." in by_message
    )
    summary = r["resumen_reglas"]["auditar_capas"]
    assert summary["n_capas_xref"] == 1
    assert sum(summary["por_clasificacion"].values()) == summary["n_capas"]
    assert not any("XREF" in message for message in by_message)


def test_capa_0_un_hallazgo_por_espacio(tmp_path):
    doc = new_doc()
    msp = doc.modelspace()
    for index in range(55):
        msp.add_line((index, 0), (index, 1))
    msp.add_circle((0, 0), 1)
    msp.add_text("x", dxfattribs={"height": 1})
    msp.add_arc((0, 0), 1, 0, 90)
    msp.add_point((1, 1))
    msp.add_lwpolyline([(0, 0), (1, 1)])
    doc.layouts.get("Layout1").add_line((0, 0), (1, 0))
    block = doc.blocks.new("B")
    block.add_line((0, 0), (1, 0))  # capa 0 dentro de bloque es correcta

    r = audit_doc(doc, tmp_path, checks=["entidades_en_capa_0"])
    model, paper = rule_findings(r, "entidades_en_capa_0")
    assert model["mensaje"] == (
        "60 entidades en la capa 0 en el espacio modelo (LINE 55, ARC 1, CIRCLE 1, LWPOLYLINE 1, POINT 1, …)."
    )
    assert model["espacio"] == "modelo"
    assert len(model["handles"]) == 50
    assert model["datos"]["handles_truncados"] is True
    assert model["datos"]["n_handles"] == 60
    assert model["bbox"] == [[0.0, 0.0], [49.0, 1.0]]
    assert paper["mensaje"] == "1 entidad en la capa 0 en el espacio Layout1 (LINE 1)."
    assert r["resumen_reglas"]["entidades_en_capa_0"]["total"] == 61


def test_bloques_sin_uso_y_sospechosos(tmp_path):
    doc = new_doc()
    doc.blocks.new("jgytfyuh")
    block = doc.blocks.new("MESA")
    block.add_line((0, 0), (1, 0))
    used = doc.blocks.new("erere")
    used.add_line((0, 0), (1, 0))
    doc.modelspace().add_blockref("erere", (0, 0))
    doc.blocks.new("A$C123")

    r = audit_doc(doc, tmp_path, checks=["auditar_bloques"])
    messages = {finding["mensaje"]: finding for finding in rule_findings(r, "auditar_bloques")}
    assert messages["El nombre de bloque 'jgytfyuh' parece arbitrario (sin_vocales); 0 inserciones."]["severidad"] == "media"
    assert messages["El nombre de bloque 'erere' parece arbitrario (par_repetido); 1 inserción."]["severidad"] == "baja"
    unused = messages["El bloque 'MESA' no tiene inserciones."]
    assert unused["severidad"] == "baja"
    assert unused["datos"] == {"bloque": "MESA", "n_entidades": 1, "unidades_bloque": 0}
    assert "El bloque 'A$C123' no tiene inserciones." in messages
    assert r["resumen_reglas"]["auditar_bloques"]["n_definiciones"] == 4


def test_bloque_dinamico_cuenta_las_inserciones_de_sus_anonimos(tmp_path):
    doc = new_doc()
    source = doc.blocks.new("PUERTA")
    anonymous = doc.blocks.new_anonymous_block("U")
    anonymous.block_record.set_xdata("AcDbBlockRepBTag", [(1070, 1), (1005, source.block_record.dxf.handle)])
    doc.modelspace().add_blockref(anonymous.name, (0, 0))

    r = audit_doc(doc, tmp_path, checks=["auditar_bloques", "resumen"])
    assert rule_findings(r, "auditar_bloques") == []
    assert r["resumen_archivo"]["bloques_mas_usados"] == [{"nombre": "PUERTA", "n_inserciones": 1}]
    assert r["resumen_archivo"]["n_bloques"] == 1


def test_unidades_distintas_no_compensadas_media_y_no_medida(tmp_path):
    doc = new_doc()
    block = doc.blocks.new("lav")
    block.block_record.dxf.units = 4
    msp = doc.modelspace()
    for index in range(4):
        msp.add_blockref("lav", (index, 0))

    r = audit_doc(doc, tmp_path, checks=["auditar_bloques"])
    [finding] = rule_findings(r, "auditar_bloques")
    assert finding["severidad"] == "media"
    assert finding["mensaje"] == (
        "El bloque 'lav' declara unidades mm y 4 de sus inserciones evaluadas no están compensadas (escala 1), "
        "pero su tamaño real es no medible: revise el metadato de unidades del bloque."
    )
    datos = finding["datos"]
    assert datos["medido"] is False
    assert datos["tamano_real_m"] is None
    assert datos["metadato_unidades_incorrecto"] is False
    assert datos["factor_escala"] == pytest.approx(0.001)
    assert (datos["n_no_compensadas"], datos["n_compensadas"], datos["escalas_no_compensadas"]) == (4, 0, [1.0])
    assert finding["bbox"] == [[0.0, 0.0], [3.0, 0.0]]


def test_unidades_distintas_anidadas(tmp_path):
    doc = new_doc()
    inner = doc.blocks.new("C")
    inner.block_record.dxf.units = 4
    outer = doc.blocks.new("D")
    outer.add_blockref("C", (0, 0), dxfattribs={"xscale": 0.045})
    doc.modelspace().add_blockref("D", (0, 0))
    r = audit_doc(doc, tmp_path, checks=["auditar_bloques"])
    [finding] = rule_findings(r, "auditar_bloques")
    assert "(escala 0.045), pero su tamaño real es no medible (inserciones anidadas):" in finding["mensaje"]
    assert finding["bbox"] is None


def test_unidades_distintas_compensadas_mensaje_de_hecho_y_media(tmp_path):
    doc = new_doc()
    block = doc.blocks.new("DP")
    block.block_record.dxf.units = 4
    doc.modelspace().add_blockref("DP", (1, 1), dxfattribs={"xscale": -0.001})
    r = audit_doc(doc, tmp_path, checks=["auditar_bloques"])
    [finding] = rule_findings(r, "auditar_bloques")
    assert finding["severidad"] == "media"
    assert finding["mensaje"] == (
        "El bloque 'DP' está definido en milímetros (INSUNITS=4) y el dibujo en metros (INSUNITS=6); su única inserción "
        "está escalada a 0.001: compensado; riesgo solo al redefinir el bloque o insertarlo sin escala automática."
    )
    assert finding["datos"]["n_compensadas"] == 1


def test_unidades_sin_factor_metrico(tmp_path):
    doc = new_doc()
    block = doc.blocks.new("LEJOS")
    block.block_record.dxf.units = 18
    doc.modelspace().add_blockref("LEJOS", (0, 0))
    r = audit_doc(doc, tmp_path, checks=["auditar_bloques"])
    [finding] = rule_findings(r, "auditar_bloques")
    assert finding["mensaje"].endswith("1 inserción, sin factor de escala métrico.")
    assert finding["datos"]["factor_escala"] is None


def test_mas_de_500_inserciones_muestra_parcial(tmp_path):
    doc = new_doc()
    block = doc.blocks.new("C")
    block.block_record.dxf.units = 4
    msp = doc.modelspace()
    for index in range(501):
        msp.add_blockref("C", (index, 0))
    [finding] = rule_findings(audit_doc(doc, tmp_path, checks=["auditar_bloques"]), "auditar_bloques")
    datos = finding["datos"]
    assert (datos["n_inserciones"], datos["inserciones_evaluadas"], datos["muestra_parcial"]) == (501, 500, True)
    assert datos["n_handles"] == 500


def test_duplicados_grupos_y_tipos_no_comparados(tmp_path):
    doc = new_doc()
    doc.layers.add("A")
    doc.layers.add("B")
    msp = doc.modelspace()
    msp.add_line((0, 0), (5, 0), dxfattribs={"layer": "A"})
    msp.add_line((5, 0), (0, 0), dxfattribs={"layer": "B"})
    for _ in range(5):
        msp.add_circle((1, 1), 2)
    msp.add_arc((0, 0), 1, 90, 180)
    msp.add_arc((0, 0), 1, 90, 180)
    msp.add_lwpolyline([(0, 0), (1, 0), (1, 1)], close=True)
    msp.add_lwpolyline([(1, 1), (1, 0), (0, 0)], close=True)
    msp.add_text("HOLA", dxfattribs={"height": 0.2, "insert": (1, 1)})
    msp.add_text("HOLA", dxfattribs={"height": 0.2, "insert": (1, 1)})
    msp.add_mtext("HOLA", dxfattribs={"char_height": 0.2, "insert": (1, 1)})
    msp.add_mtext("HOLA", dxfattribs={"char_height": 0.2, "insert": (1, 1)})
    msp.add_text("OTRO", dxfattribs={"height": 0.2, "insert": (1, 1)})
    msp.add_point((0, 0))
    msp.add_point((0, 0))

    r = audit_doc(doc, tmp_path, checks=["duplicados"])
    findings = rule_findings(r, "duplicados")
    assert findings[0]["mensaje"] == "5 CIRCLE idénticas (tolerancia 1e-06) en la capa 0."
    assert findings[0]["severidad"] == "media"
    assert findings[0]["bbox"] == [[-1.0, -1.0], [3.0, 3.0]]
    two_layers = next(finding for finding in findings if finding["datos"]["tipo"] == "LINE")
    assert two_layers["mensaje"] == "2 LINE idénticas (tolerancia 1e-06) en las capas A, B."
    assert two_layers["severidad"] == "baja"
    assert two_layers["espacio"] == "modelo"
    assert two_layers["datos"]["tipos_no_comparados"] == ["POINT"]
    assert {finding["datos"]["tipo"] for finding in findings} == {"CIRCLE", "LINE", "ARC", "LWPOLYLINE", "TEXT", "MTEXT"}
    assert r["resumen_reglas"]["duplicados"] == {
        "n_grupos": 6,
        "n_entidades_sobrantes": 9,
        "tipos_no_comparados": ["POINT"],
    }


def test_duplicados_y_textos_respetan_el_espacio(tmp_path):
    doc = new_doc()
    layout = doc.layouts.get("Layout1")
    layout.add_line((0, 0), (1, 0))
    layout.add_line((0, 0), (1, 0))
    doc.modelspace().add_text("x", dxfattribs={"height": 1})
    r = audit_doc(doc, tmp_path, checks=["duplicados", "textos_fuera_de_escala"], espacio="layout1")
    assert r["espacio"] == "Layout1"
    assert len(rule_findings(r, "duplicados")) == 1
    assert r["resumen_reglas"]["textos_fuera_de_escala"] == {"n_textos": 0, "mediana": None}


def test_espacio_inexistente(tmp_path):
    with pytest.raises(PluginError) as info:
        audit_doc(new_doc(), tmp_path, espacio="NoExiste")
    assert str(info.value) == (
        "[NOT_FOUND] El layout 'NoExiste' no existe en el dibujo. Disponibles: modelo, Layout1"
        " (nombre de la pestaña, no el título del plano; listar_layouts da sus viewports)."
    )
    assert info.value.data == {"espacio": "NoExiste", "disponibles": ["modelo", "Layout1"]}


def test_textos_outliers_con_atributos(tmp_path):
    doc = new_doc()
    block = doc.blocks.new("B")
    block.add_attdef("TAG", dxfattribs={"height": 9.0})
    msp = doc.modelspace()
    for _ in range(4):
        msp.add_text("t", dxfattribs={"height": 0.2})
    msp.add_mtext("m", dxfattribs={"char_height": 0.2})
    msp.add_text("mini", dxfattribs={"height": 0.01})
    insert = msp.add_blockref("B", (0, 0))
    insert.add_attrib("TAG", "v", dxfattribs={"height": 9.0})

    r = audit_doc(doc, tmp_path, checks=["textos_fuera_de_escala"])
    assert r["resumen_reglas"]["textos_fuera_de_escala"] == {"n_textos": 7, "mediana": pytest.approx(0.2)}
    menor, mayor = rule_findings(r, "textos_fuera_de_escala")
    assert menor["mensaje"] == "1 texto con altura menor que 0.2 × la mediana (0.2) en el espacio modelo."
    assert mayor["mensaje"] == "1 texto con altura mayor que 5 × la mediana (0.2) en el espacio modelo."
    assert mayor["severidad"] == "baja"
    assert mayor["datos"]["motivo"] == "mayor"
    assert mayor["handles"] == [insert.attribs[0].dxf.handle]


def test_xrefs_relativa_a_la_carpeta_original_y_absoluta(tmp_path):
    original = tmp_path / "expediente"
    original.mkdir()
    (original / "base.dwg").write_bytes(b"x")
    doc = new_doc()
    doc.add_xref_def("base.dwg", "BASE")
    doc.add_xref_def(str(original / "falta.dwg"), "FALTA")
    doc.add_xref_def(str(original / "base.dwg"), "ABSOLUTA")
    insert = doc.modelspace().add_blockref("FALTA", (1, 2))

    r = audit_doc(doc, tmp_path, checks=["xrefs_no_resueltas", "resumen"], original=original)
    [finding] = rule_findings(r, "xrefs_no_resueltas")
    assert finding["severidad"] == "alta"
    assert finding["mensaje"] == f"La xref 'FALTA' tiene estado no_encontrada (ruta guardada {original / 'falta.dwg'})."
    assert finding["handles"] == [insert.dxf.handle]
    assert finding["espacio"] == "modelo"
    assert finding["datos"]["estado"] == "no_encontrada"
    assert finding["datos"]["ruta_resuelta"] is None
    assert r["resumen_reglas"]["xrefs_no_resueltas"]["n_xrefs"] == 3
    assert r["resumen_archivo"]["n_xrefs"] == 3
    assert r["resumen_archivo"]["n_bloques"] == 0


def test_xref_relativa_no_se_resuelve_contra_la_copia(tmp_path):
    """La carpeta temporal no cuenta: una relativa se busca en la carpeta original."""
    doc = new_doc()
    doc.add_xref_def("base.dwg", "BASE")
    (tmp_path / "base.dwg").write_bytes(b"x")
    original = tmp_path / "otra"
    original.mkdir()
    r = audit_doc(doc, tmp_path, checks=["xrefs_no_resueltas"], original=original)
    assert len(rule_findings(r, "xrefs_no_resueltas")) == 1


UNC_ORIGINAL = Path(r"\\nas\planos\EXP 26")


@pytest.mark.parametrize(
    "saved",
    [
        r"\\atacante\recurso\x.dwg",
        "//atacante/recurso/x.dwg",
        r"\\ATACANTE\recurso",
        r"\\?\UNC\atacante\recurso\x.dwg",
        r"\\?\C:\Windows\win.ini",
        r"\\.\pipe\mcp-autocad-1",
        r"\??\C:\x.dwg",
        r"Z:\otra\unidad.dwg",
        "Z:relativa.dwg",
    ],
)
def test_xref_con_destino_ajeno_no_se_consulta(saved):
    assert audit._xref_target(saved, Path(r"C:\exp\26"), ()) is None


@pytest.mark.parametrize(
    "saved, esperado",
    [
        ("base.dwg", r"C:\exp\26\base.dwg"),
        (r"..\comun\base.dwg", r"C:\exp\comun\base.dwg"),
        ("../comun/base.dwg", r"C:\exp\comun\base.dwg"),
        (r"\raiz\base.dwg", r"C:\raiz\base.dwg"),
        (r"c:\OTRA\base.dwg", r"c:\OTRA\base.dwg"),  # misma unidad, sin distinguir mayúsculas
    ],
)
def test_xref_en_la_unidad_del_plano_se_consulta(saved, esperado):
    assert audit._xref_target(saved, Path(r"C:\exp\26"), ()) == esperado


def test_xref_con_original_en_red_solo_consulta_ese_recurso():
    assert audit._xref_target(r"\\NAS\Planos\otro\x.dwg", UNC_ORIGINAL, ()) == r"\\NAS\Planos\otro\x.dwg"
    assert audit._xref_target("x.dwg", UNC_ORIGINAL, ()) == r"\\nas\planos\EXP 26\x.dwg"
    assert audit._xref_target(r"\\nas\otro\x.dwg", UNC_ORIGINAL, ()) is None
    assert audit._xref_target(r"\\atacante\planos\x.dwg", UNC_ORIGINAL, ()) is None


def test_xref_dentro_de_carpetas_permitidas_se_consulta():
    permitted = [r"O:\LPS\Expedientes", r"\\nas\comun"]
    assert audit._xref_target(r"O:\LPS\Expedientes\b.dwg", Path(r"C:\exp"), permitted) == r"O:\LPS\Expedientes\b.dwg"
    assert audit._xref_target(r"\\NAS\comun\b.dwg", Path(r"C:\exp"), permitted) == r"\\NAS\comun\b.dwg"
    assert audit._xref_target(r"O:\LPS\Expedientes2\b.dwg", Path(r"C:\exp"), permitted) is None  # no vale el prefijo
    assert audit._xref_target(r"O:\LPS\Expedientes\..\b.dwg", Path(r"C:\exp"), permitted) is None
    # Windows no deja subir por encima del recurso: \\nas\comun\.. sigue siendo \\nas\comun.
    assert audit._xref_target(r"\\nas\comun\..\otro\b.dwg", Path(r"C:\exp"), permitted) == r"\\nas\comun\otro\b.dwg"


def test_carpetas_permitidas_mal_formadas_no_amplian_nada():
    assert audit._allowed_folders({"carpetas_auditoria_permitidas": "O:\\"}) == ()
    assert audit._allowed_folders({"carpetas_auditoria_permitidas": ["", 3, " O:\\x "]}) == ("O:\\x",)
    assert audit._allowed_folders(None) == ()


def test_xref_unc_da_hallazgo_no_comprobada_sin_tocar_la_ruta(tmp_path, monkeypatch):
    doc = new_doc()
    saved = r"\\atacante\recurso\x.dwg"
    doc.add_xref_def(saved, "AJENA")
    insert = doc.modelspace().add_blockref("AJENA", (0, 0))
    consulted: list[str] = []
    real_exists = os.path.exists

    def exists(path):
        consulted.append(str(path))
        return real_exists(path)

    monkeypatch.setattr(audit.os.path, "exists", exists)
    r = audit_doc(doc, tmp_path, checks=["xrefs_no_resueltas"])
    assert not [path for path in consulted if "atacante" in path]
    [finding] = rule_findings(r, "xrefs_no_resueltas")
    assert finding["severidad"] == "media"
    assert finding["mensaje"] == (
        f"La xref 'AJENA' tiene estado no_comprobada (ruta guardada {saved}): {audit.MOTIVO_XREF_NO_COMPROBADA}."
    )
    assert finding["handles"] == [insert.dxf.handle]
    assert finding["datos"] == {
        "nombre": "AJENA",
        "estado": "no_comprobada",
        "ruta_guardada": saved,
        "ruta_resuelta": None,
        "motivo": audit.MOTIVO_XREF_NO_COMPROBADA,
    }
    assert r["resumen_reglas"]["xrefs_no_resueltas"] == {"n_xrefs": 1, "n_hallazgos": 1, "n_no_comprobadas": 1}


def test_xref_en_carpeta_permitida_de_lps_resuelve(tmp_path):
    otra = tmp_path / "comun"
    otra.mkdir()
    (otra / "base.dwg").write_bytes(b"x")
    doc = new_doc()
    doc.add_xref_def(str(otra / "base.dwg"), "BASE")
    lps = {**LPS, "carpetas_auditoria_permitidas": [str(otra)]}
    assert rule_findings(audit_doc(doc, tmp_path, checks=["xrefs_no_resueltas"], lps=lps), "xrefs_no_resueltas") == []


def test_una_regla_que_falla_no_tumba_la_llamada(tmp_path, monkeypatch):
    def broken(_drawing):
        raise KeyError("x")

    monkeypatch.setitem(audit._RULES, "duplicados", broken)
    r = audit_doc(new_doc(), tmp_path, checks=["duplicados", "unidades"])
    assert r["reglas_con_error"] == [{"regla": "duplicados", "excepcion": "KeyError"}]
    assert "La regla duplicados falló (KeyError); se continuó con las demás." in r["advertencias"]
    assert r["resumen"]["por_regla"]["duplicados"]["completo"] is False
    assert r["resumen"]["por_regla"]["unidades"]["completo"] is True


def test_paginacion_de_hallazgos(tmp_path):
    doc = new_doc()
    for index in range(5):
        doc.layers.add(f"RARA{index}")
    first = audit_doc(doc, tmp_path, checks=["auditar_capas"], limit=2)
    assert (first["total"], len(first["hallazgos"]), first["cursor_siguiente"]) == (5, 2, "2")
    last = audit_doc(doc, tmp_path, checks=["auditar_capas"], limit=2, start=4)
    assert (len(last["hallazgos"]), last["cursor_siguiente"]) == (1, None)
    assert last["hallazgos"][0]["id"] == "auditar_capas-5"
    with pytest.raises(PluginError) as info:
        audit_doc(doc, tmp_path, checks=["auditar_capas"], start=5)
    assert info.value.code == "INVALID_PARAMS"
    assert info.value.data["detalle"] == "cursor: fuera de rango"


def test_ezdxf_inventa_defpoints_y_se_descarta(tmp_path):
    """AutoCAD cuenta 6 capas en sintetico_cype.dwg; ezdxf añade Defpoints al cargar y daría 7."""
    source = new_doc()
    source.layers.add("OTRA")
    source.layers.remove("Defpoints")
    path = tmp_path / "sin_defpoints.dxf"
    source.saveas(path)
    doc = ezdxf.readfile(path)
    assert "Defpoints" in doc.layers  # la creó ezdxf
    assert [layer.nombre for layer in audit._read_layers(doc)] == ["0", "OTRA"]
    assert audit_doc(doc, tmp_path, checks=["resumen"])["resumen_archivo"]["n_capas"] == 2


def test_defpoints_real_se_conserva(tmp_path):
    source = new_doc()
    path = tmp_path / "con_defpoints.dxf"
    source.saveas(path)
    assert [layer.nombre for layer in audit._read_layers(ezdxf.readfile(path))] == ["0", "Defpoints"]


def test_entidad_de_clase_desconocida_cuenta_en_su_capa(tmp_path):
    doc = new_doc()
    doc.layers.add("RARA")
    msp = doc.modelspace()
    path = tmp_path / "base.dxf"
    doc.saveas(path)
    text = path.read_text(encoding="utf-8")
    end = text.rfind("ENDSEC", 0, text.find("OBJECTS"))
    cut = text.rfind("  0\n", 0, end)
    custom = f"  0\nFOO_ENTITY\n  5\nFFF1\n330\n{msp.block_record_handle}\n100\nAcDbEntity\n  8\nRARA\n100\nFooClass\n"
    path.write_text(text[:cut] + custom + text[cut:], encoding="utf-8")

    r = audit_doc(ezdxf.readfile(path), tmp_path, checks=["auditar_capas", "resumen"])
    assert r["reglas_con_error"] == []
    assert r["resumen_archivo"]["entidades_modelo"]["por_tipo"] == {"FOO_ENTITY": 1}
    [finding] = rule_findings(r, "auditar_capas")
    assert finding["datos"]["n_entidades"] == 1


# ------------------------------------------------------------------ auditar_archivo_oda con ODA falso


def oda_args(work: Path, exe: Path, **overrides) -> dict:
    args = {
        "comprobaciones": None,
        "espacio": "modelo",
        "lps": LPS,
        "carpeta_trabajo": work,
        "oda_exe": exe,
        "timeout_s": 30,
        "carpeta_original": None,
    }
    return {**args, **overrides}


def fake_oda_with_doc(tmp_path: Path, doc) -> Path:
    sample = tmp_path / "muestra.dxf"
    doc.saveas(sample)
    return fake_oda(tmp_path, f"import shutil\nshutil.copy({str(sample)!r}, salida / 'entrada.dxf')\n")


def test_extremo_a_extremo_borra_lo_temporal_y_no_toca_el_origen(tmp_path, dwg):
    exe = fake_oda_with_doc(tmp_path, new_doc())
    work = tmp_path / "tmp" / "id"
    before = sorted((item.name, item.stat().st_size, item.stat().st_mtime_ns) for item in dwg.parent.iterdir())
    r = auditar_archivo_oda(dwg, **oda_args(work, exe))
    assert r["origen"] == "oda"
    assert r["archivo"]["nombre"] == dwg.name
    assert r["archivo"]["version_dwg"] == "AC1027"  # del DWG, no el AC1032 del DXF convertido
    assert not work.exists()  # creada aquí, borrada aquí
    assert sorted((item.name, item.stat().st_size, item.stat().st_mtime_ns) for item in dwg.parent.iterdir()) == before


def test_carpeta_de_trabajo_ajena_conserva_lo_que_no_es_suyo(tmp_path, dwg):
    """El lote guarda en la carpeta de trabajo la copia que audita: no se puede borrar."""
    exe = fake_oda_with_doc(tmp_path, new_doc())
    work = tmp_path / "lote"
    work.mkdir()
    copy = work / "PLANO.dwg"
    shutil.copy(dwg, copy)
    auditar_archivo_oda(copy, **oda_args(work, exe, carpeta_original=dwg.parent))
    assert [item.name for item in work.iterdir()] == ["PLANO.dwg"]


def test_medir_recibe_el_documento_y_se_fusiona_en_la_respuesta(tmp_path, dwg):
    """Sin ODA a propósito: es el punto de extensión del lote y debe vigilarse en cualquier equipo."""
    doc = new_doc()
    doc.modelspace().add_line((0, 0), (1, 0), dxfattribs={"layer": "0"})
    exe = fake_oda_with_doc(tmp_path, doc)
    received: list = []

    def medir(documento, cancelar) -> dict:
        received.append((documento, cancelar))
        return {"_entidades": len(documento.modelspace())}

    r = auditar_archivo_oda(dwg, medir=medir, **oda_args(tmp_path / "trabajo", exe))
    assert len(received) == 1 and received[0][1] is None  # sin cancelar del lote: None
    assert r["_entidades"] == 1
    assert r["origen"] == "oda"


def test_medir_solo_anade_sus_campos(tmp_path, dwg):
    exe = fake_oda_with_doc(tmp_path, new_doc())
    base = auditar_archivo_oda(dwg, **oda_args(tmp_path / "a", exe))
    measured = auditar_archivo_oda(dwg, medir=lambda _doc, _cancelar: {"_x": 1}, **oda_args(tmp_path / "b", exe))
    assert set(measured) - set(base) == {"_x"}
    for key in set(base) - {"duracion_ms"}:
        assert measured[key] == base[key], key


def test_medir_en_la_forma_del_lote_conserva_la_copia(tmp_path, dwg):
    """El lote pasa como carpeta de trabajo la de su copia, y esa copia la borra él."""
    exe = fake_oda_with_doc(tmp_path, new_doc())
    work = tmp_path / "lote"
    work.mkdir()
    copy = work / "PLANO.dwg"
    shutil.copy(dwg, copy)
    r = auditar_archivo_oda(copy, medir=lambda _doc, _cancelar: {"_x": 1}, **oda_args(work, exe, carpeta_original=dwg.parent))
    assert r["_x"] == 1
    assert [item.name for item in work.iterdir()] == ["PLANO.dwg"]


def test_medir_que_falla_se_propaga_y_limpia(tmp_path, dwg):
    exe = fake_oda_with_doc(tmp_path, new_doc())
    work = tmp_path / "trabajo"

    def medir(_doc, _cancelar):
        raise RuntimeError("fallo del medidor")

    with pytest.raises(RuntimeError, match="fallo del medidor"):
        auditar_archivo_oda(dwg, medir=medir, **oda_args(work, exe))
    assert not work.exists()


def test_medir_no_se_llama_si_la_lectura_falla(tmp_path, dwg):
    exe = fake_oda(tmp_path, "(salida / 'entrada.dxf').write_text('esto no es un dxf')\n")
    calls: list = []
    with pytest.raises(OdaConversionError):
        auditar_archivo_oda(dwg, medir=calls.append, **oda_args(tmp_path / "trabajo", exe))
    assert calls == []


def test_dxf_ilegible_es_no_se_pudo_leer_y_limpia(tmp_path, dwg):
    exe = fake_oda(tmp_path, "(salida / 'entrada.dxf').write_text('esto no es un dxf')\n")
    work = tmp_path / "trabajo"
    with pytest.raises(OdaConversionError) as info:
        auditar_archivo_oda(dwg, **oda_args(work, exe))
    assert str(info.value) == "[INTERNAL] No se pudo leer el archivo DWG."
    assert info.value.detalle
    assert not work.exists()


def test_espacio_inexistente_tambien_limpia(tmp_path, dwg):
    exe = fake_oda_with_doc(tmp_path, new_doc())
    work = tmp_path / "trabajo"
    with pytest.raises(PluginError, match="NOT_FOUND"):
        auditar_archivo_oda(dwg, **oda_args(work, exe, espacio="X"))
    assert not work.exists()


def test_archivo_inexistente_not_found(tmp_path):
    exe = fake_oda(tmp_path, WRITES_DXF)
    missing = tmp_path / "no.dwg"
    with pytest.raises(PluginError) as info:
        auditar_archivo_oda(missing, **oda_args(tmp_path / "t", exe))
    assert str(info.value) == f"[NOT_FOUND] El archivo '{missing}' no existe."
    assert info.value.data == {"archivo": str(missing)}
    assert not (tmp_path / "t").exists()


def test_sin_oda_no_crea_nada(tmp_path, dwg):
    with pytest.raises(OdaNoDisponibleError):
        auditar_archivo_oda(dwg, **oda_args(tmp_path / "t", tmp_path / "no.exe"))
    assert not (tmp_path / "t").exists()


@pytest.mark.parametrize(
    ("overrides", "detalle"),
    [
        ({"comprobaciones": ["x"]}, None),
        ({"comprobaciones": ["unidades", "unidades"]}, "comprobaciones: comprobación repetida 'unidades'"),
        ({"cursor": "abc"}, "cursor: valor inválido"),
        ({"limit": 0}, "limit: debe ser un entero entre 1 y 1000"),
        ({"limit": 1001}, "limit: debe ser un entero entre 1 y 1000"),
    ],
)
def test_parametros_invalidos(tmp_path, dwg, overrides, detalle):
    exe = fake_oda(tmp_path, WRITES_DXF)
    with pytest.raises(PluginError) as info:
        auditar_archivo_oda(dwg, **oda_args(tmp_path / "t", exe, **overrides))
    assert info.value.code == "INVALID_PARAMS"
    assert info.value.plugin_message == f"Parámetros inválidos: {info.value.data['detalle']}"
    if detalle:
        assert info.value.data["detalle"] == detalle


def test_sin_espacio_en_disco_no_copia_nada(tmp_path, dwg, monkeypatch):
    exe = fake_oda(tmp_path, WRITES_DXF)
    work = tmp_path / "trabajo"
    usage = shutil.disk_usage(tmp_path)._replace(free=50 * 1024 * 1024)
    monkeypatch.setattr(audit.shutil, "disk_usage", lambda _path: usage)
    with pytest.raises(PluginError) as info:
        auditar_archivo_oda(dwg, **oda_args(work, exe))
    assert str(info.value) == "[INTERNAL] No hay espacio en disco para la copia temporal."
    assert info.value.data["detalle"] == f"libres 50 MB, necesarios 200 MB en {work.anchor}"
    assert not (exe.parent / "registro.json").exists()


MIB = 1024 * 1024


def _dwg_de_1_mib(folder: Path) -> Path:
    path = folder / "grande.dwg"
    path.write_bytes(b"AC1027" + b"\0" * (MIB - 6))
    return path


def _libres(monkeypatch, tmp_path: Path, bytes_libres: int) -> None:
    usage = shutil.disk_usage(tmp_path)._replace(free=bytes_libres)
    monkeypatch.setattr(audit.shutil, "disk_usage", lambda _path: usage)


def test_espacio_directo_exige_el_margen_del_lote(tmp_path, monkeypatch):
    """Un DXF mide unas 7 veces su DWG: con 2 × + 100 MB, un DWG de 227 MB podía llenar el disco a mitad de conversión."""
    ruta = _dwg_de_1_mib(tmp_path)
    work = tmp_path / "trabajo"
    work.mkdir()
    _libres(monkeypatch, tmp_path, 210 * MIB - 1)
    with pytest.raises(PluginError) as info:
        audit._check_free_space(ruta, work)
    assert info.value.data["detalle"] == f"libres 209 MB, necesarios 210 MB en {work.anchor}"
    _libres(monkeypatch, tmp_path, 210 * MIB)
    audit._check_free_space(ruta, work)


def test_espacio_no_cuenta_dos_veces_la_copia_del_lote(tmp_path, monkeypatch):
    """En el límite exacto del lote: su copia ya ocupa disco, y un rechazo aquí dejaría la fila en error definitivo."""
    work = tmp_path / "tmp_lote"
    work.mkdir()
    ruta = _dwg_de_1_mib(work)
    assert lote_mod.ESPACIO_FACTOR_ODA * MIB + lote_mod.ESPACIO_RESERVA == 210 * MIB
    _libres(monkeypatch, tmp_path, 209 * MIB)  # 210 antes de copiar, menos la copia de 1 MiB
    audit._check_free_space(ruta, work)
    _libres(monkeypatch, tmp_path, 209 * MIB - 1)
    with pytest.raises(PluginError) as info:
        audit._check_free_space(ruta, work)
    assert info.value.data["detalle"] == f"libres 208 MB, necesarios 209 MB en {work.anchor}"


# ------------------------------------------------------------------ plazo y cancelación de la lectura del DXF


def write_dxf(path: Path, n_lines: int = 3) -> Path:
    doc = new_doc()
    space = doc.modelspace()
    for index in range(n_lines):
        space.add_line((index, 0), (index, 1))
    doc.saveas(path)
    return path


def future(seconds: float = 30.0) -> float:
    return time.monotonic() + seconds


def test_lectura_vigilada_equivale_a_readfile(tmp_path):
    dxf = write_dxf(tmp_path / "a.dxf", 20)
    doc = audit._read_dxf(dxf, "a.dwg", future(), 30, None)
    assert doc.filename == str(dxf)
    assert len(doc.modelspace()) == len(ezdxf.readfile(dxf).modelspace()) == 20
    dxf.unlink()  # archivo ya cerrado


def test_flujo_vigilado_se_detiene_con_la_senal():
    stop = threading.Event()
    stream = audit._GuardedStream(io.StringIO("linea\n" * (audit._LINES_PER_CHECK * 2)), stop)
    for _ in range(audit._LINES_PER_CHECK - 1):
        stream.readline()
    stop.set()
    with pytest.raises(audit._ReadStopped):
        stream.readline()


def test_flujo_vigilado_suelta_el_archivo_en_la_marca_eof(tmp_path):
    path = tmp_path / "x.txt"
    path.write_text("  0\nEOF\nresto\n", encoding="utf-8")
    stream = audit._GuardedStream(open(path, encoding="utf-8"), threading.Event())  # noqa: SIM115
    assert [stream.readline(), stream.readline()] == ["  0\n", "EOF\n"]
    path.unlink()  # en Windows falla si sigue abierto
    assert stream.readline() == ""


def test_flujo_vigilado_no_confunde_un_texto_eof_con_la_marca():
    stream = audit._GuardedStream(io.StringIO("  1\nEOF\n  0\nEOF\n"), threading.Event())
    assert [stream.readline() for _ in range(5)] == ["  1\n", "EOF\n", "  0\n", "EOF\n", ""]


def blocked_load(stop_seen: threading.Event):
    def load(_dxf, stop):
        stop.wait(10)
        stop_seen.set()
        raise audit._ReadStopped

    return load


def test_lectura_que_agota_el_plazo(tmp_path, monkeypatch):
    stop_seen = threading.Event()
    monkeypatch.setattr(audit, "_load_dxf", blocked_load(stop_seen))
    start = time.perf_counter()
    with pytest.raises(OdaTimeoutError) as info:
        audit._read_dxf(tmp_path / "a.dxf", "PLANO.dwg", future(0.2), 7, None)
    assert time.perf_counter() - start < 3
    assert stop_seen.wait(1)  # el hilo recibió la señal
    assert str(info.value) == "[ODA_TIMEOUT] La lectura del DXF con ezdxf superó 7 s con 'PLANO.dwg'."
    assert info.value.code == "ODA_TIMEOUT"


def test_lectura_cancelada(tmp_path, monkeypatch):
    stop_seen = threading.Event()
    monkeypatch.setattr(audit, "_load_dxf", blocked_load(stop_seen))
    cancelar = threading.Event()
    threading.Timer(0.2, cancelar.set).start()
    with pytest.raises(OdaCanceladoError):
        audit._read_dxf(tmp_path / "a.dxf", "PLANO.dwg", future(), 30, cancelar)
    assert stop_seen.wait(1)


def test_lectura_real_cancelada_deja_borrar_el_dxf(tmp_path):
    dxf = write_dxf(tmp_path / "grande.dxf", 40000)
    cancelar = threading.Event()
    cancelar.set()
    with pytest.raises(OdaCanceladoError):
        audit._read_dxf(dxf, "grande.dwg", future(), 30, cancelar)
    dxf.unlink()


def test_lectura_de_algo_que_no_es_dxf(tmp_path):
    dxf = tmp_path / "roto.dxf"
    dxf.write_text("esto no es un dxf", encoding="utf-8")
    with pytest.raises(OdaConversionError) as info:
        audit._read_dxf(dxf, "roto.dwg", future(), 30, None)
    assert info.value.data["detalle"] == "OSError: 'roto.dxf' no es un archivo DXF."


def test_auditoria_cancelada_con_el_conversor_en_marcha_no_deja_nada(tmp_path, dwg, slow_converter):
    exe = Path(sys.executable)
    work = tmp_path / "trabajo"
    cancelar = threading.Event()
    threading.Timer(0.3, cancelar.set).start()
    with pytest.raises(OdaCanceladoError):
        auditar_archivo_oda(dwg, cancelar=cancelar, **oda_args(work, exe))
    assert slow_converter[0].poll() is not None
    assert not work.exists()


def test_auditoria_cancelada_entre_reglas(tmp_path):
    ruta = tmp_path / "plano.dwg"
    ruta.write_bytes(b"AC1027")
    drawing = audit._build_drawing(new_doc(), "modelo", LPS, tmp_path)
    cancelar = threading.Event()
    cancelar.set()
    with pytest.raises(OdaCanceladoError):
        audit._audit_drawing(drawing, ruta, audit.normalize_checks(None), 200, 0, time.perf_counter(), cancelar)


def test_el_plazo_de_la_lectura_descuenta_la_conversion(tmp_path, dwg, monkeypatch):
    exe = fake_oda(tmp_path, WRITES_DXF)
    work = tmp_path / "trabajo"
    received: list[float] = []

    def read(_dxf, _nombre, deadline, timeout_s, _cancelar):
        received.append(deadline - time.monotonic())
        raise OdaTimeoutError(timeout_s, "x", audit.ETAPA_LECTURA)

    monkeypatch.setattr(audit, "_read_dxf", read)
    with pytest.raises(OdaTimeoutError):
        auditar_archivo_oda(dwg, **{**oda_args(work, exe), "timeout_s": 40})
    assert 0 < received[0] < 40
    assert not work.exists()


# ------------------------------------------------------------------ una sola lectura de ezdxf por proceso


@pytest.fixture
def abandoned_read(tmp_path, monkeypatch):
    """Deja una lectura abandonada: como la segunda etapa de ezdxf, ignora la parada hasta ``release``."""
    release = threading.Event()

    def load(_dxf, _stop):
        release.wait(30)
        raise audit._ReadStopped

    monkeypatch.setattr(audit, "_load_dxf", load)
    monkeypatch.setattr(audit, "_STOP_WAIT_S", 0.01)
    with pytest.raises(OdaTimeoutError):
        audit._read_dxf(tmp_path / "abandonada.dxf", "abandonada.dwg", future(0.1), 9, None)
    assert audit._read_slot.locked()  # el hilo sigue vivo tras el error que recibió quien llamó
    yield release
    release.set()
    assert audit._read_slot.acquire(timeout=5)
    audit._read_slot.release()


@pytest.fixture
def conversions(monkeypatch):
    """Sustituye al conversor y registra si se llegó a lanzar."""
    started = threading.Event()

    def convert(*_args, **_kwargs):
        started.set()
        raise OdaConversionError("fin de la prueba")

    monkeypatch.setattr(audit, "dwg_a_dxf", convert)
    return started


def test_no_se_convierte_hasta_que_acaba_la_lectura_abandonada(tmp_path, dwg, abandoned_read, conversions):
    errors: list[Exception] = []

    def run() -> None:
        try:
            auditar_archivo_oda(dwg, **oda_args(tmp_path / "trabajo", Path(sys.executable)))
        except OdaConversionError as exc:
            errors.append(exc)

    thread = threading.Thread(target=run)
    thread.start()
    assert not conversions.wait(0.4)
    abandoned_read.set()
    assert conversions.wait(5)
    thread.join(5)
    assert len(errors) == 1


def test_cancelar_atiende_la_espera_de_la_lectura_abandonada(tmp_path, dwg, abandoned_read, conversions):
    cancelar = threading.Event()
    threading.Timer(0.2, cancelar.set).start()
    work = tmp_path / "trabajo"
    with pytest.raises(OdaCanceladoError):
        auditar_archivo_oda(dwg, cancelar=cancelar, **oda_args(work, Path(sys.executable)))
    assert not conversions.is_set()
    assert not work.exists()


def test_la_espera_de_la_lectura_abandonada_tiene_plazo_y_texto_propio(tmp_path, dwg, abandoned_read, conversions):
    work = tmp_path / "trabajo"
    with pytest.raises(OdaTimeoutError) as info:
        auditar_archivo_oda(dwg, **{**oda_args(work, Path(sys.executable)), "timeout_s": 0.2})
    assert str(info.value) == f"[ODA_TIMEOUT] La espera de la lectura anterior del DXF superó 0.2 s con '{dwg.name}'."
    assert info.value.code == "ODA_TIMEOUT"
    assert not conversions.is_set()
    assert not work.exists()


def test_la_espera_no_descuenta_el_plazo_del_archivo(tmp_path, dwg, monkeypatch):
    received: list[float] = []

    def read(_dxf, _nombre, deadline, timeout_s, _cancelar):
        received.append(deadline - time.monotonic())
        raise OdaTimeoutError(timeout_s, "x", audit.ETAPA_LECTURA)

    monkeypatch.setattr(audit, "dwg_a_dxf", lambda *_args, **_kwargs: tmp_path / "x.dxf")
    monkeypatch.setattr(audit, "_read_dxf", read)
    assert audit._read_slot.acquire(timeout=5)
    threading.Timer(0.6, audit._read_slot.release).start()
    with pytest.raises(OdaTimeoutError):
        auditar_archivo_oda(dwg, **{**oda_args(tmp_path / "trabajo", Path(sys.executable)), "timeout_s": 1.0})
    assert received[0] > 0.7  # sin descontar los 0,6 s de espera


def test_dos_lecturas_a_la_vez_no_se_solapan(tmp_path, monkeypatch):
    active: list[int] = [0, 0]  # en curso, máximo visto
    guard = threading.Lock()

    def load(_dxf, _stop):
        with guard:
            active[0] += 1
            active[1] = max(active)
        time.sleep(0.15)
        with guard:
            active[0] -= 1
        return "doc"

    monkeypatch.setattr(audit, "_load_dxf", load)
    threads = [
        threading.Thread(target=audit._read_dxf, args=(tmp_path / "a.dxf", "a.dwg", future(), 30, None)) for _ in range(3)
    ]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join(5)
    assert active == [0, 1]


def test_el_cupo_de_lectura_se_libera_con_exito_y_con_error(tmp_path):
    audit._read_dxf(write_dxf(tmp_path / "bueno.dxf"), "bueno.dwg", future(), 30, None)
    assert not audit._read_slot.locked()
    malo = tmp_path / "malo.dxf"
    malo.write_text("esto no es un dxf", encoding="utf-8")
    with pytest.raises(OdaConversionError):
        audit._read_dxf(malo, "malo.dwg", future(), 30, None)
    assert not audit._read_slot.locked()


# ------------------------------------------------------------------ ODA real sobre copias de los fixtures

FIXTURE_NAMES = ("casa6x12_limpio", "copia_real", "sintetico_cype")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def layer_classes(response: dict) -> dict[str, str]:
    """Nombre → clasificación de las capas con hallazgo (el nombre solo viene en el mensaje)."""
    found = {}
    for finding in rule_findings(response, "auditar_capas"):
        name = re.match(r"La capa '(.+)' \(\w+\)", finding["mensaje"])
        found[name.group(1)] = finding["datos"]["clasificacion"]
    return found


@pytest.fixture(scope="module")
def real(tmp_path_factory):
    """Una auditoría por fixture, sobre una copia en una carpeta temporal (15 s en total).

    Las copias se borran al terminar: pytest conserva sus carpetas temporales entre ejecuciones
    y un plano de cliente no se puede quedar allí.
    """
    base = tmp_path_factory.mktemp("oda_real")
    hashes = {name: sha256(FIXTURES / f"{name}.dwg") for name in FIXTURE_NAMES}
    responses = {}
    folders = {}
    for name in FIXTURE_NAMES:
        folder = base / name
        work = base / f"trabajo_{name}"
        folder.mkdir()
        work.mkdir()
        copy = folder / f"{name}.dwg"
        shutil.copyfile(FIXTURES / f"{name}.dwg", copy)
        responses[name] = auditar_archivo_oda(
            copy,
            comprobaciones=None,
            espacio="modelo",
            lps=LPS,
            carpeta_trabajo=work,
            oda_exe=ODA_EXE,
            timeout_s=120,
            carpeta_original=None,
            limit=1000,
        )
        folders[name] = (folder, work)
    yield {"hashes": hashes, "responses": responses, "folders": folders}
    shutil.rmtree(base, ignore_errors=True)


@needs_oda
def test_real_cancelar_mata_al_conversor_de_verdad(tmp_path):
    """Con el ODA real: un solo proceso, sin ventanas ni restos tras matarlo a mitad de conversión."""
    copy = tmp_path / "copia_real.dwg"
    shutil.copyfile(FIXTURES / "copia_real.dwg", copy)
    work = tmp_path / "trabajo"
    cancelar = threading.Event()
    threading.Timer(1.0, cancelar.set).start()
    start = time.perf_counter()
    with pytest.raises(OdaCanceladoError):
        auditar_archivo_oda(copy, cancelar=cancelar, **oda_args(work, ODA_EXE))
    assert time.perf_counter() - start < 8
    assert not work.exists()


@needs_oda
def test_real_medir_con_los_auxiliares_del_lote(tmp_path):
    """El uso real: los tres campos auxiliares del lote salen de una sola conversión."""
    from mcp_autocad.lote import medir_auxiliares

    copy = tmp_path / "sintetico_cype.dwg"
    shutil.copyfile(FIXTURES / "sintetico_cype.dwg", copy)
    args = {"limit": 1000, "timeout_s": 120}
    base = auditar_archivo_oda(copy, **{**oda_args(tmp_path / "a", ODA_EXE), **args})
    r = auditar_archivo_oda(copy, medir=medir_auxiliares, **{**oda_args(tmp_path / "b", ODA_EXE), **args})
    assert set(r) - set(base) == {"_cype", "_lamina", "_bloques", "_unidad"}
    for key in set(base) - {"duracion_ms"}:
        assert r[key] == base[key], key
    assert [item.name for item in tmp_path.iterdir()] == ["sintetico_cype.dwg"]


@needs_oda
@pytest.mark.parametrize("name", FIXTURE_NAMES)
def test_real_carpetas_limpias_y_fixtures_intactos(real, name):
    folder, work = real["folders"][name]
    assert list(work.iterdir()) == []
    assert [item.name for item in folder.iterdir()] == [f"{name}.dwg"]  # sin .dwl, .bak ni restos
    assert sha256(FIXTURES / f"{name}.dwg") == real["hashes"][name]
    assert sha256(folder / f"{name}.dwg") == real["hashes"][name]


@needs_oda
@pytest.mark.parametrize("name", FIXTURE_NAMES)
def test_real_forma_comun(real, name):
    r = real["responses"][name]
    json.dumps(r)
    assert (r["pid"], r["origen"], r["reglas_con_error"], r["reglas_omitidas"]) == (0, "oda", [], [])
    assert ADVERTENCIA_ODA in r["advertencias"]
    assert r["no_disponible"][0]["comprobacion"] == "verificar_cuadro_areas"
    assert "verificar_cuadro_areas" not in r["resumen"]["por_regla"]
    assert r["duracion_ms"] > 0


@needs_oda
def test_real_casa6x12(real):
    r = real["responses"]["casa6x12_limpio"]
    assert (r["insunits"], r["unidades"], r["en_metros_declarado"]) == (6, "m", True)
    assert r["archivo"]["version_dwg"] == "AC1032"
    summary = r["resumen_archivo"]
    assert summary["measurement"] == 1
    assert summary["n_capas"] == 33
    assert summary["entidades_modelo"]["total"] == 4694
    assert summary["n_bloques"] == 10
    assert summary["layouts"] == ["Layout1", "Layout2"]
    assert summary["n_xrefs"] == 0
    assert r["resumen_reglas"]["textos_fuera_de_escala"]["mediana"] == pytest.approx(0.15)
    capa0 = next(f for f in rule_findings(r, "entidades_en_capa_0") if f["espacio"] == "modelo")
    assert capa0["datos"]["n"] == 38
    assert rule_findings(r, "unidades") == []
    assert r["resumen_reglas"]["auditar_capas"]["por_clasificacion"] == {
        "mcp": 0, "estandar": 20, "estandar_con_diferencias": 0, "cype": 0,
        "ajena_revit_aia": 5, "ajena_exportacion": 4, "generica": 0, "fuera_de_estandar": 4,
    }  # fmt: skip


@needs_oda
def test_real_casa6x12_las_13_capas_fuera_del_estandar(real):
    # Medido el 2026-10-31 con ODA 27.1 + ezdxf 1.4.4: 20 estandar + I-FURN + estas 12.
    # §5H.3 dice que las 12 quedaron vacías tras normalizar_capas, pero este fixture es el plano limpio
    # ANTES de normalizar: las 12 tienen entidades (Q-SPCQ-____-OTLN, 996) y de las 13 solo I-FURN está vacía.
    assert layer_classes(real["responses"]["casa6x12_limpio"]) == {
        "I-FURN-____-OTLN": "ajena_revit_aia",
        "A-DOOR-____-OTLN": "ajena_revit_aia",
        "P-SANR-FIXT-OTLN": "ajena_revit_aia",
        "Q-SPCQ-____-OTLN": "ajena_revit_aia",
        "S-COLS": "ajena_revit_aia",
        "3D EJES ESTRUCTURALES_Pen_No__7": "ajena_exportacion",
        "3D LINEAS DE PLANTA_Pen_No__7": "ajena_exportacion",
        "Dibujo 2D - General_Pen_No__102": "ajena_exportacion",
        "Dibujo 2D - General_Pen_No__142": "ajena_exportacion",
        "ACHU": "fuera_de_estandar",
        "ESC": "fuera_de_estandar",
        "Furniture": "fuera_de_estandar",
        "TEXT": "fuera_de_estandar",
    }


@needs_oda
def test_real_copia_real(real):
    r = real["responses"]["copia_real"]
    # $ACADVER del DXF sería AC1032: ODA convierte a ACAD2018. El archivo es AC1018.
    assert r["archivo"]["version_dwg"] == "AC1018"
    summary = r["resumen_archivo"]
    assert summary["n_capas"] == 161
    assert summary["entidades_modelo"]["total"] == 36955
    assert summary["entidades_modelo"]["por_tipo"]["LINE"] == 19577
    assert summary["n_bloques"] == 101
    assert summary["layouts"] == ["Layout1", "Layout2"]
    capas = r["resumen_reglas"]["auditar_capas"]
    assert capas["por_clasificacion"] == {
        "mcp": 0, "estandar": 24, "estandar_con_diferencias": 0, "cype": 0,
        "ajena_revit_aia": 18, "ajena_exportacion": 16, "generica": 5, "fuera_de_estandar": 98,
    }  # fmt: skip
    assert capas["n_vacias"] == 40  # el mismo valor que el ejemplo real de §5C.1
    capa0 = next(f for f in rule_findings(r, "entidades_en_capa_0") if f["espacio"] == "modelo")
    assert capa0["datos"]["n"] == 1151
    textos = r["resumen_reglas"]["textos_fuera_de_escala"]
    assert textos["mediana"] == pytest.approx(0.13)
    assert textos["n_textos"] == 2723


@needs_oda
def test_real_copia_real_bloques_y_duplicados(real):
    r = real["responses"]["copia_real"]
    by_block = {f["datos"]["bloque"]: f for f in rule_findings(r, "auditar_bloques") if "n_compensadas" in f["datos"]}
    # §5C.3: línea base real; los tamaños no se miden, así que todos salen media y medido = false.
    for name, unidades in (("lav", 4), ("EE", 4), ("TANQUE 1000 LTS", 4), ("PuertaBaño", 1), ("C", 4)):
        assert by_block[name]["datos"]["unidades_bloque"] == unidades
        assert by_block[name]["severidad"] == "media"
        assert by_block[name]["datos"]["medido"] is False
    # §5B.4 cuenta también las inserciones dentro de otros bloques: lav = 4 en *Model_Space (el 4 de la
    # línea base de §5C.3) + 4 en 'VS 2 2025_6X12  ARQ CLIENTE_E' + 'cocina 1' + *U342. Sobre C (5464) no hay duda.
    assert by_block["lav"]["datos"]["n_inserciones"] == 10
    assert by_block["C"]["datos"]["n_inserciones"] == 5464
    assert by_block["C"]["datos"]["muestra_parcial"] is True
    assert "(inserciones anidadas)" in by_block["C"]["mensaje"]
    duplicados = rule_findings(r, "duplicados")
    assert duplicados
    assert "INSERT" in duplicados[0]["datos"]["tipos_no_comparados"]


@needs_oda
def test_real_sintetico_cype(real):
    r = real["responses"]["sintetico_cype"]
    assert (r["insunits"], r["unidades"], r["en_metros_declarado"]) == (4, "mm", False)
    summary = r["resumen_archivo"]
    # §5H.3 dice 7 capas, medido con ezdxf, que inventa Defpoints al cargar. La tabla de capas del
    # DXF tiene 6 y accoreconsole, sobre una copia, cuenta 6 (2026-10-31): 0, ARM_PILARES, ARM_VIGAS,
    # ALZ_PILARES, CIM_ZAPATAS y TEXTO_CYPE. Se exige lo que dirá el plugin, no el artefacto.
    assert summary["n_capas"] == 6
    assert summary["entidades_modelo"]["total"] == 8
    assert r["resumen_reglas"]["entidades_en_capa_0"]["total"] == 0
    assert r["resumen_reglas"]["auditar_capas"]["por_clasificacion"]["cype"] == 4
    assert layer_classes(r) == {"TEXTO_CYPE": "fuera_de_estandar"}
    [unidades] = rule_findings(r, "unidades")
    assert unidades["mensaje"] == "El dibujo está en milímetros (INSUNITS=4); el estándar LPS es metros (INSUNITS=6)."


@needs_oda
def test_real_nombre_con_acentos_y_parentesis(tmp_path):
    folder = tmp_path / "EXP 26-XXXX ñ"
    folder.mkdir()
    copy = folder / "ARQ MONTAÑA (1).dwg"
    shutil.copyfile(FIXTURES / "sintetico_cype.dwg", copy)
    r = auditar_archivo_oda(copy, **oda_args(tmp_path / "trabajo", ODA_EXE, comprobaciones=["resumen"]))
    assert r["archivo"]["nombre"] == "ARQ MONTAÑA (1).dwg"
    assert r["resumen_archivo"]["entidades_modelo"]["total"] == 8


@needs_oda
def test_real_dwg_truncado_cita_a_oda_y_limpia(tmp_path):
    data = (FIXTURES / "sintetico_cype.dwg").read_bytes()
    damaged = tmp_path / "danado.dwg"
    damaged.write_bytes(data[: len(data) // 2])
    work = tmp_path / "trabajo"
    with pytest.raises(OdaConversionError) as info:
        auditar_archivo_oda(damaged, **oda_args(work, ODA_EXE))
    assert str(info.value) == "[INTERNAL] No se pudo leer el archivo DWG."
    assert "danado.dwg" in info.value.detalle
    assert "oda_in" not in info.value.detalle
    assert not work.exists()


# ---------------------------------------------------------------------------
# Bloques sin medir: "alta 0" con ODA no significa "sin hallazgos graves" (§5H.3)
# ---------------------------------------------------------------------------


def doc_con_bloques_en_mm(*nombres: str):
    doc = new_doc()
    for nombre in nombres:
        doc.blocks.new(nombre).block_record.dxf.units = 4
        doc.modelspace().add_blockref(nombre, (0, 0))
    return doc


def test_bloques_sin_medir_literal_exacto(tmp_path):
    r = audit_doc(doc_con_bloques_en_mm("lav", "CARRO"), tmp_path, checks=["auditar_bloques"])
    aviso = (
        "2 bloque(s) con unidades distintas quedan en media porque sin AutoCAD no se mide su tamaño real; alguno podría "
        "ser alta: reaudite con el plugin para separarlos."
    )
    assert audit.advertencia_bloques_sin_medir(2) == aviso
    assert r["advertencias"].count(aviso) == 1
    assert r["resumen"]["por_severidad"] == {"alta": 0, "media": 2, "baja": 0}  # la severidad no cambia
    assert r["resumen_reglas"]["auditar_bloques"]["n_sin_medir"] == 2


def test_bloques_sin_medir_el_literal_no_lleva_texto_del_plano(tmp_path):
    r = audit_doc(doc_con_bloques_en_mm("NOMBRE_DEL_PLANO"), tmp_path, checks=["auditar_bloques"])
    assert audit.advertencia_bloques_sin_medir(1) in r["advertencias"]
    assert not any("NOMBRE_DEL_PLANO" in aviso for aviso in r["advertencias"])


def test_bloques_sin_medir_cuenta_todo_aunque_la_pagina_no_los_traiga(tmp_path):
    doc = doc_con_bloques_en_mm("A1", "B1", "C1")
    primera = audit_doc(doc, tmp_path, checks=["auditar_bloques"], limit=1)
    segunda = audit_doc(doc, tmp_path, checks=["auditar_bloques"], limit=1, start=2)
    assert len(primera["hallazgos"]) == 1 and len(segunda["hallazgos"]) == 1
    assert audit.advertencia_bloques_sin_medir(3) in primera["advertencias"]
    assert audit.advertencia_bloques_sin_medir(3) in segunda["advertencias"]


def test_sin_bloques_sin_medir_no_hay_advertencia(tmp_path):
    doc = new_doc()
    doc.blocks.new("MESA")  # hallazgo sin_uso: de auditar_bloques, pero sin datos.medido
    doc.blocks.new("OTRO").block_record.dxf.units = 6  # misma unidad que el dibujo
    doc.modelspace().add_blockref("OTRO", (0, 0))
    r = audit_doc(doc, tmp_path, checks=["auditar_bloques"])
    assert rule_findings(r, "auditar_bloques")
    assert r["resumen_reglas"]["auditar_bloques"]["n_sin_medir"] == 0
    assert not any("sin AutoCAD no se mide" in aviso for aviso in r["advertencias"])


def test_bloques_sin_medir_sin_pedir_la_regla_no_hay_advertencia(tmp_path):
    r = audit_doc(doc_con_bloques_en_mm("lav"), tmp_path, checks=["unidades"])
    assert not any("sin AutoCAD no se mide" in aviso for aviso in r["advertencias"])


def _bloque_en_mm(doc, nombre: str, *escalas: float) -> None:
    doc.blocks.new(nombre).block_record.dxf.units = 4
    for escala in escalas:
        doc.modelspace().add_blockref(nombre, (0, 0), dxfattribs={"xscale": escala})


def test_bloques_solo_compensados_no_avisan_y_siguen_en_media(tmp_path):
    doc = new_doc()
    _bloque_en_mm(doc, "DP", 0.001)
    _bloque_en_mm(doc, "DQ", -0.001, 0.001)
    r = audit_doc(doc, tmp_path, checks=["auditar_bloques"])
    assert r["resumen"]["por_severidad"] == {"alta": 0, "media": 2, "baja": 0}  # la severidad de ODA no cambia
    assert all(f["datos"]["medido"] is False for f in rule_findings(r, "auditar_bloques"))
    assert r["resumen_reglas"]["auditar_bloques"]["n_sin_medir"] == 0
    assert r["resumen_reglas"]["auditar_bloques"]["unidades_distintas"] == 2
    assert not any("sin AutoCAD no se mide" in aviso for aviso in r["advertencias"])


def test_bloques_mezcla_cuenta_solo_los_no_compensados(tmp_path):
    doc = new_doc()
    _bloque_en_mm(doc, "COMPENSADO", 0.001)
    _bloque_en_mm(doc, "SIN_COMPENSAR", 1)
    _bloque_en_mm(doc, "PARCIAL", 0.001, 1)  # una inserción sin compensar basta: el plugin la mediría
    r = audit_doc(doc, tmp_path, checks=["auditar_bloques"])
    assert r["resumen"]["por_severidad"]["media"] == 3
    assert r["resumen_reglas"]["auditar_bloques"]["n_sin_medir"] == 2
    assert audit.advertencia_bloques_sin_medir(2) in r["advertencias"]


def test_bloque_sin_factor_metrico_no_cuenta(tmp_path):
    """Sin factor, el plugin tampoco puede subirlo a alta: sale media sin evaluar la compensación."""
    doc = new_doc()
    doc.blocks.new("LEJOS").block_record.dxf.units = 18
    doc.modelspace().add_blockref("LEJOS", (0, 0))
    r = audit_doc(doc, tmp_path, checks=["auditar_bloques"])
    assert r["resumen_reglas"]["auditar_bloques"]["n_sin_medir"] == 0
    assert not any("sin AutoCAD no se mide" in aviso for aviso in r["advertencias"])


# ------------------------------------------------------------------ evidencia de la unidad real (servidor 0.8.1)


def _doc_evidencia() -> "ezdxf.document.Drawing":
    """Modelo en metros: rejilla de 20 × 15 con segmentos de 1 m, 6 cotas de 3 m y ruido que no debe contar."""
    doc = new_doc(insunits=4)  # declara mm; la geometría está en metros
    modelo = doc.modelspace()
    for i in range(21):
        modelo.add_line((i, 0), (i, 1))  # 21 segmentos de 1 m
    modelo.add_lwpolyline([(0, 0), (20, 0), (20, 15), (0, 15)], close=True)  # 20, 15, 20, 15
    for _ in range(6):
        modelo.new_entity("DIMENSION", dxfattribs={"dimtype": 32, "actual_measurement": -3.0})  # bit 32: lineal
    modelo.new_entity("DIMENSION", dxfattribs={"dimtype": 2, "actual_measurement": 90.0})  # angular: no cuenta
    modelo.new_entity("DIMENSION", dxfattribs={"dimtype": 6, "actual_measurement": 1e6})  # coordenada: no cuenta
    modelo.new_entity("DIMENSION", dxfattribs={"dimtype": 0, "actual_measurement": 0.0})  # 0: no cuenta
    modelo.new_entity("DIMENSION", dxfattribs={"dimtype": 0})  # sin medida: no cuenta
    modelo.add_line((5, 5), (5, 5))  # segmento nulo: puntos sí, largo no
    bloque = doc.blocks.new("GRANDE")
    bloque.add_line((0, 0), (1e6, 1e6))  # dentro de un bloque: no se explota
    modelo.add_blockref("GRANDE", (0, 0))
    doc.header["$DIMLFAC"] = 100.0
    return doc


def test_evidencia_unidad_sintetica():
    ev = audit.evidencia_unidad(_doc_evidencia())
    assert ev["n_cotas"] == 6 and ev["cota_mediana"] == 3.0
    assert ev["n_puntos"] == 21 * 2 + 4 + 2
    assert ev["extension"] == pytest.approx(20.0)  # p5–p95 de X: 0 → 20 (Y: 0 → 15)
    assert ev["largo_mediano"] == 1.0  # 21 de 1 m frente a 15, 15, 20, 20; el nulo no cuenta
    assert ev["dimlfac"] == 100.0 and ev["truncada"] is False
    assert ev["n_entidades"] == len(_doc_evidencia().modelspace())
    assert lote_mod.unidad_probable(ev) == {"unidad": "m", "motivo": "única unidad compatible (cotas, extensión)"}


def test_evidencia_unidad_percentiles_ignoran_puntos_lejanos():
    doc = new_doc()
    modelo = doc.modelspace()
    for i in range(100):
        modelo.add_line((i * 0.1, 0), (i * 0.1, 10))
    modelo.add_line((1e6, 1e6), (1e6 + 1, 1e6))  # 2 de 202 puntos: fuera de p5–p95
    ev = audit.evidencia_unidad(doc)
    assert ev["n_puntos"] == 202 and ev["extension"] == pytest.approx(10.0)


def test_evidencia_unidad_vacia():
    ev = audit.evidencia_unidad(new_doc())
    assert ev == {
        "n_cotas": 0, "cota_mediana": None, "n_puntos": 0, "extension": None, "coord_mediana": None, "largo_mediano": None,
        "dimlfac": 1.0, "n_entidades": 0, "truncada": False,
    }  # fmt: skip
    assert lote_mod.unidad_probable(ev) == {"unidad": "indeterminada", "motivo": "sin evidencia suficiente"}


def test_evidencia_unidad_tope_de_entidades(monkeypatch):
    doc = new_doc()
    for i in range(30):
        doc.modelspace().add_line((i, 0), (i, 1))
    monkeypatch.setattr(audit, "MAX_ENTIDADES_EVIDENCIA", 10)
    ev = audit.evidencia_unidad(doc)
    assert ev["n_entidades"] == 10 and ev["truncada"] is True and ev["n_puntos"] == 20


def test_evidencia_unidad_tope_de_valores(monkeypatch):
    doc = new_doc()
    for i in range(30):
        doc.modelspace().add_line((i, 0), (i, 1))
    monkeypatch.setattr(audit, "MAX_VALORES_EVIDENCIA", 5)
    ev = audit.evidencia_unidad(doc)
    assert ev["n_puntos"] == 60 and ev["n_entidades"] == 30 and ev["truncada"] is True


def test_evidencia_unidad_atiende_la_cancelacion():
    doc = new_doc()
    for i in range(audit._ENTIDADES_POR_COMPROBACION + 1):
        doc.modelspace().add_line((i, 0), (i, 1))
    cancelar = threading.Event()
    cancelar.set()
    with pytest.raises(OdaCanceladoError):
        audit.evidencia_unidad(doc, cancelar)
    assert audit.evidencia_unidad(doc, threading.Event())["n_entidades"] == audit._ENTIDADES_POR_COMPROBACION + 1


def test_medir_auxiliares_incluye_la_evidencia_de_unidad():
    aux = lote_mod.medir_auxiliares(_doc_evidencia())
    assert set(aux) == {"_cype", "_lamina", "_bloques", "_unidad"}
    assert aux["_unidad"] == audit.evidencia_unidad(_doc_evidencia())


def test_medir_no_empieza_si_ya_se_cancelo(tmp_path, dwg, monkeypatch):
    """La medición auxiliar no la cubre timeout_s: si la cancelación llegó durante las reglas, no se mide."""
    exe = fake_oda_with_doc(tmp_path, new_doc())
    cancelar = threading.Event()
    calls: list = []
    real_audit = audit._audit_drawing

    def audit_y_cancelar(*args):
        response = real_audit(*args[:-1], None)  # las reglas corren enteras
        cancelar.set()  # y la cancelación llega justo después de la última
        return response

    monkeypatch.setattr(audit, "_audit_drawing", audit_y_cancelar)
    work = tmp_path / "trabajo"
    with pytest.raises(OdaCanceladoError):
        auditar_archivo_oda(dwg, medir=calls.append, cancelar=cancelar, **oda_args(work, exe))
    assert calls == [] and not work.exists()


@needs_oda
@pytest.mark.parametrize(
    ("name", "cotas", "ext_min", "ext_max"),
    [
        ("casa6x12_limpio", 0.35, 30, 35),
        # Metros con una extensión p5–p95 de ~5594 m: con el tope antiguo de 3000 m salía indeterminada; con 20 000, m.
        ("copia_real", 0.454757, 5500, 5700),
    ],
)
def test_real_evidencia_unidad(tmp_path, name, cotas, ext_min, ext_max):
    """Medido sobre una copia en tmp: los dos planos declaran INSUNITS 6 y están en metros."""
    copy = tmp_path / f"{name}.dwg"
    shutil.copyfile(FIXTURES / f"{name}.dwg", copy)
    r = auditar_archivo_oda(
        copy, medir=lote_mod.medir_auxiliares, **{**oda_args(tmp_path / "w", ODA_EXE), "comprobaciones": ["resumen"], "timeout_s": 120}
    )
    ev = r["_unidad"]
    assert ev["n_cotas"] > 600 and ev["cota_mediana"] == pytest.approx(cotas) and ext_min < ev["extension"] < ext_max
    assert r["insunits"] == 6
    assert lote_mod.unidad_probable(ev, r["insunits"]) == {"unidad": "m", "motivo": "única unidad compatible (cotas, extensión)"}
    assert [item.name for item in tmp_path.iterdir()] == [f"{name}.dwg"]


def test_evidencia_unidad_nunca_devuelve_infinito():
    """Dos cotas de 1e308 dan una mediana infinita y un ancho de -1e308 a 1e308 también: salen None, JSON válido."""
    doc = new_doc()
    modelo = doc.modelspace()
    for _ in range(2):
        modelo.new_entity("DIMENSION", dxfattribs={"dimtype": 0, "actual_measurement": 1e308})
    for i in range(20):
        modelo.add_line((-1e308, i), (1e308, i))
    ev = audit.evidencia_unidad(doc)
    assert ev["n_cotas"] == 2 and ev["cota_mediana"] is None
    assert ev["n_puntos"] == 40 and ev["extension"] is None
    assert ev["largo_mediano"] is None  # hypot infinito: no es segmento válido
    json.dumps(ev, allow_nan=False)  # no lanza
    assert lote_mod.unidad_probable(ev)["motivo"] == "sin evidencia suficiente"


def test_evidencia_unidad_cuenta_arc_dimension():
    doc = new_doc()
    modelo = doc.modelspace()
    for _ in range(3):
        modelo.new_entity("ARC_DIMENSION", dxfattribs={"actual_measurement": 2.0})
    modelo.new_entity("ARC_DIMENSION", dxfattribs={"actual_measurement": 0.0})  # 0: no cuenta
    modelo.new_entity("ARC_DIMENSION", dxfattribs={})  # sin medida: no cuenta
    for _ in range(2):
        modelo.new_entity("DIMENSION", dxfattribs={"dimtype": 0, "actual_measurement": 4.0})
    ev = audit.evidencia_unidad(doc)
    assert ev["n_cotas"] == 5 and ev["cota_mediana"] == 2.0


def test_medir_recibe_el_cancelar_del_lote(tmp_path, dwg):
    exe = fake_oda_with_doc(tmp_path, new_doc())
    evento = threading.Event()
    recibidos: list = []
    auditar_archivo_oda(dwg, medir=lambda _doc, cancelar: recibidos.append(cancelar) or {}, cancelar=evento, **oda_args(tmp_path / "w", exe))
    assert recibidos == [evento]


def test_cancelacion_dentro_de_medir_sale_con_el_nombre_del_archivo(tmp_path, dwg):
    exe = fake_oda_with_doc(tmp_path, new_doc())

    def medir(_doc, _cancelar):
        raise OdaCanceladoError("medición auxiliar")

    work = tmp_path / "w"
    with pytest.raises(OdaCanceladoError) as info:
        auditar_archivo_oda(dwg, medir=medir, **oda_args(work, exe))
    assert info.value.archivo == dwg.name and not work.exists()
