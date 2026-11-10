"""Genera la plantilla LPS en metros, standards/LPS_METROS.dwt (protocol.md §5H.4).

Uso: python -I crear_plantilla_metros.py <lps.json> <casa6x12_limpio.dwg> <salida.dwt> [--oda <ODAFileConverter.exe>]

Cadena: ezdxf crea un DXF R2018 con la tabla de §5H.4 -> ODA File Converter DXF->DWG (ACAD2018) -> se renombra a
.dwt (un .dwt es un DWG con otra extensión) y se copia a la salida. Sin ninguna entidad en modelo ni en layouts.

- Capas: las 24 de capas_permitidas de lps.json, con color, tipo de línea, grosor y plot exactos; ninguna más.
- Bloque C (flecha de COTA75): se importa tal cual del 6X12 con ezdxf.addons.Importer, desde un DXF que ODA saca de
  una COPIA temporal del .dwg de referencia; el original solo se lee. Se conserva la geometría, no el metadato de
  unidades del BLOCK_RECORD (4 en el 6X12): el Importer lo deja en 0, y así auditar_bloques no marca
  unidades_distintas en cada dibujo LPS con cotas (protocol.md §5C.3).
- Las variables $DIM* de la cabecera se fijan a los mismos valores que COTA75: si difieren, AutoCAD las trata como
  anulaciones del estilo actual y las cotas nuevas no saldrían con COTA75.
- Variables fuera de la tabla de §5H.4 (COTA75_FUERA_DE_TABLA), medidas con accoreconsole sobre el 6X12:
  DIMDSEP 46 (punto; el valor por defecto de ezdxf es 44, coma, y cambiaría el separador decimal de las cotas),
  DIMADEC 0, DIMAZIN 0, DIMFXL 1 y DIMJOGANG 45° (sin ellas ODA deja en el estilo 2, 3, 2.5 y otro ángulo, distintos
  de la cabecera, y AutoCAD lista cuatro anulaciones). Las anulaciones propias del 6X12 (DIMFXL 0, DIMFXLON, DIMJOGANG
  90, DIMLTYPE Continuous) no se copian: son del dibujo, no del estilo.

Idempotente: cada ejecución sustituye la salida por otra de contenido equivalente (ODA y ezdxf estampan fechas y
GUID propios, así que los bytes pueden cambiar). Los temporales van a tests/tmp_fase5_plantilla/ y se borran siempre.
Comprobación: tools/verificar_plantilla_metros.py.
"""
import argparse
import hashlib
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import ezdxf
from ezdxf.addons import Importer

RAIZ = Path(__file__).resolve().parents[1]
CARPETA_TEMPORAL = RAIZ / "tests" / "tmp_fase5_plantilla"
ODA_POR_DEFECTO = Path(r"C:\Program Files\ODA\ODAFileConverter 27.1.0\ODAFileConverter.exe")
ODA_TIMEOUT_S = 120

CABECERA = {
    "$INSUNITS": 6, "$MEASUREMENT": 1, "$LUNITS": 2, "$LUPREC": 2, "$AUNITS": 0, "$AUPREC": 0,
    "$PSLTSCALE": 1, "$CELTSCALE": 1.0, "$CLAYER": "0", "$DIMSTYLE": "COTA75", "$TEXTSTYLE": "75",
    "$TEXTSIZE": 0.09, "$LWDISPLAY": 0,
}
CENTER_DESCRIPCION = "Center ____ _ ____ _ ____ _ ____ _ ____ _ ____"
CENTER_PATRON = [50.8, 31.75, -6.35, 6.35, -6.35]  # longitud total + A,31.75,-6.35,6.35,-6.35 de acadiso.lin
CONTINUA_DESCRIPCION = "Linea ininterrumpida"

# Datos de cada estilo: fuente, altura, y familia + banderas de los XDATA "ACAD" (los mismos que el 6X12).
ESTILOS_TEXTO = {
    "Standard": ("arial.ttf", 0.0, ("Arial", 34)),
    "75": ("micross.ttf", 0.09, ("Microsoft Sans Serif", 34)),
    "cota75": ("simplex.shx", 0.09, None),
}
COTA75 = {
    "dimtxt": 0.09, "dimasz": 0.045, "dimexo": 0.1, "dimexe": 0.1, "dimdli": 0.0, "dimgap": 0.015,
    "dimdec": 2, "dimtdec": 2, "dimzin": 4, "dimtzin": 8, "dimtad": 1, "dimtih": 0, "dimtoh": 0,
    "dimse1": 1, "dimse2": 1, "dimtix": 1, "dimtofl": 1, "dimtmove": 2, "dimatfit": 0, "dimtolj": 0,
    "dimcen": 0.2, "dimclrd": 256, "dimclre": 8, "dimclrt": 256, "dimlwd": -1,
    "dimaltf": 0.03937007874016, "dimaltd": 3, "dimalttd": 3,
    "dimblk": "C", "dimblk1": "", "dimblk2": "", "dimldrblk": "", "dimtxsty": "cota75",
}
# Fuera de la tabla de §5H.4; ver el docstring. Se fijan en estilo y cabecera para que AutoCAD no liste anulaciones.
COTA75_FUERA_DE_TABLA = {"dimdsep": 46, "dimadec": 0, "dimazin": 0, "dimfxl": 1.0, "dimjogang": math.pi / 4}


def ejecutar_oda(oda, entrada, salida, version_formato, patron):
    """Convierte por carpeta con ODA y devuelve los archivos de salida; ODA sale con 0 aunque no convierta."""
    resultado = subprocess.run(
        [str(oda), str(entrada), str(salida), "ACAD2018", version_formato, "0", "1", patron],
        cwd=salida.parent, capture_output=True, timeout=ODA_TIMEOUT_S,
        creationflags=subprocess.CREATE_NO_WINDOW,
    )
    creados = [p for p in salida.iterdir() if p.suffix.lower() == f".{version_formato.lower()}"]
    if not creados:
        detalle = resultado.stdout.decode("utf-8", "replace") + resultado.stderr.decode("utf-8", "replace")
        errores = "; ".join(p.read_text("utf-8", "replace") for p in salida.glob("*.err"))
        raise SystemExit(f"ODA no generó ningún .{version_formato}: {detalle} {errores}".strip())
    return creados


def dxf_de_referencia(dwg, trabajo, oda):
    """Saca el DXF de una copia del DWG de referencia; ODA nunca ve la carpeta del original."""
    entrada, salida = trabajo / "ref_in", trabajo / "ref_out"
    entrada.mkdir()
    salida.mkdir()
    shutil.copyfile(dwg, entrada / "referencia.dwg")
    return ejecutar_oda(oda, entrada, salida, "DXF", "*.DWG")[0]


def crear_capas(doc, capas):
    for nombre, p in capas.items():
        if p["tipo_linea"] not in doc.linetypes:
            raise SystemExit(f"El tipo de línea '{p['tipo_linea']}' de la capa {nombre} no está en la plantilla.")
        if nombre not in doc.layers:  # sin distinguir mayúsculas: Defpoints de ezdxf es DEFPOINTS de lps.json
            doc.layers.add(nombre)
        capa = doc.layers.get(nombre)
        capa.dxf.name = nombre
        capa.dxf.color = p["color"]
        capa.dxf.linetype = p["tipo_linea"]
        capa.dxf.lineweight = p["grosor"]
        capa.dxf.plot = p["plot"]
        capa.dxf.flags = 0
    sobrantes = [c.dxf.name for c in doc.layers if c.dxf.name not in capas]
    if sobrantes:
        raise SystemExit(f"Capas fuera de capas_permitidas: {sobrantes}")


def crear_estilos_texto(doc):
    for nombre, (fuente, altura, familia) in ESTILOS_TEXTO.items():
        estilo = doc.styles.get(nombre) if nombre in doc.styles else doc.styles.add(nombre, font=fuente)
        estilo.dxf.font = fuente
        estilo.dxf.height = altura
        estilo.dxf.width = 1.0
        estilo.dxf.oblique = 0.0
        estilo.dxf.generation_flags = 0
        if altura:
            estilo.dxf.last_height = altura
        if familia:
            estilo.set_xdata("ACAD", [(1000, familia[0]), (1071, familia[1])])


def crear_cota75(doc, bloque_referencia):
    """El bloque C debe existir antes: DIMBLK se exporta como handle de su BLOCK_RECORD."""
    origen = ezdxf.readfile(bloque_referencia)
    if "C" not in origen.blocks:
        raise SystemExit("El plano de referencia no tiene el bloque C (flecha de COTA75).")
    importador = Importer(origen, doc)
    importador.import_block("C", rename=False)
    importador.finalize()

    estilo = doc.dimstyles.add("COTA75")
    for nombre, valor in {**COTA75, **COTA75_FUERA_DE_TABLA}.items():
        estilo.dxf.set(nombre, valor)
        doc.header[f"${nombre.upper()}"] = valor


def crear_dxf(capas, lps, bloque_referencia, ruta_dxf):
    doc = ezdxf.new("R2018", setup=False)
    doc.linetypes.get("Continuous").dxf.description = "Solid line"
    doc.linetypes.add("CENTER", pattern=CENTER_PATRON, description=CENTER_DESCRIPCION)
    doc.linetypes.add("CONTINUA", pattern=[0.0], description=CONTINUA_DESCRIPCION)
    crear_capas(doc, capas)
    crear_estilos_texto(doc)
    crear_cota75(doc, bloque_referencia)
    for variable, valor in CABECERA.items():
        doc.header[variable] = valor
    doc.header["$LTSCALE"] = lps["ltscale_referencia"]

    entidades = len(doc.modelspace()) + sum(len(doc.layout(n)) for n in doc.layout_names() if n != "Model")
    if entidades:
        raise SystemExit(f"La plantilla debe quedar sin entidades y tiene {entidades}.")
    errores = doc.audit().errors
    if errores:
        raise SystemExit(f"ezdxf.audit encontró errores: {[str(e) for e in errores]}")
    doc.saveas(ruta_dxf)


def dxf_a_dwt(ruta_dxf, trabajo, oda, salida):
    carpeta_salida = trabajo / "dwg_out"
    carpeta_salida.mkdir()
    dwg = ejecutar_oda(oda, ruta_dxf.parent, carpeta_salida, "DWG", "*.DXF")[0]
    salida.parent.mkdir(parents=True, exist_ok=True)
    temporal = salida.with_name(salida.name + ".tmp")
    shutil.copyfile(dwg, temporal)
    os.replace(temporal, salida)


def main(argv=None):
    p = argparse.ArgumentParser(description="Genera standards/LPS_METROS.dwt (protocol.md §5H.4).")
    p.add_argument("lps", type=Path, help="ruta de lps.json")
    p.add_argument("referencia", type=Path, help="casa6x12_limpio.dwg (solo se lee el bloque C de una copia)")
    p.add_argument("salida", type=Path, help="ruta del .dwt")
    p.add_argument("--oda", type=Path, default=ODA_POR_DEFECTO, help="ruta de ODAFileConverter.exe")
    a = p.parse_args(argv)
    for requerido in (a.lps, a.referencia, a.oda):
        if not requerido.is_file():
            raise SystemExit(f"No existe {requerido}")
    if a.salida.suffix.lower() != ".dwt":
        raise SystemExit(f"La salida debe ser un .dwt: {a.salida}")

    lps = json.loads(a.lps.read_text(encoding="utf-8"))
    capas = lps["capas_permitidas"]
    CARPETA_TEMPORAL.mkdir(parents=True, exist_ok=True)
    trabajo = Path(tempfile.mkdtemp(prefix="crear_", dir=CARPETA_TEMPORAL))
    try:
        referencia_dxf = dxf_de_referencia(a.referencia, trabajo, a.oda)
        carpeta_dxf = trabajo / "dxf"
        carpeta_dxf.mkdir()
        ruta_dxf = carpeta_dxf / "LPS_METROS.dxf"
        crear_dxf(capas, lps, referencia_dxf, ruta_dxf)
        dxf_a_dwt(ruta_dxf, trabajo, a.oda, a.salida)
    finally:
        shutil.rmtree(trabajo, ignore_errors=True)
        try:
            CARPETA_TEMPORAL.rmdir()  # solo si quedó vacía
        except OSError:
            pass

    resumen = hashlib.sha256(a.salida.read_bytes()).hexdigest()
    print(f"{a.salida}: {a.salida.stat().st_size} bytes, sha256 {resumen}, {len(capas)} capas")
    return 0


if __name__ == "__main__":
    sys.exit(main())
