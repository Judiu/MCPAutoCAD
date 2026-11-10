"""Comprueba standards/LPS_METROS.dwt contra la tabla de protocol.md §5H.4.

Uso: python -I verificar_plantilla_metros.py <plantilla.dwt> [--oda <exe>] [--accoreconsole [<exe>]] [--lps <lps.json>]

Trabaja siempre sobre una copia .dwg en tests/tmp_fase5_plantilla/, que se borra al terminar:
- ODA File Converter -> DXF -> ezdxf: cabecera, 24 capas frente a lps.json, tipos de línea, estilos, COTA75 variable a
  variable (estilo y cabecera), bloque C y 0 entidades en modelo y layouts.
- Con --accoreconsole, AutoCAD real ejecuta el script fijo tools/verificar_plantilla_metros.scr: INSUNITS, MEASUREMENT,
  LTSCALE, TEXTSTYLE, CLAYER, DIMSTYLE, las variables de COTA75 y las 24 capas. Es la única comprobación que detecta
  anulaciones del estilo actual (cabecera distinta del estilo): ODA + ezdxf no las ven. Los números salen con la
  precisión de DIMDEC (0.045 se muestra 0.05), por eso se comparan redondeados a dos decimales.

Los esperados están escritos aquí a propósito y no se importan de crear_plantilla_metros.py: un error en ese script
no debe validarse a sí mismo. Imprime una tabla OK/FALLO; código de salida 0 solo si todas las filas son OK.
"""
import argparse
import ctypes
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import ezdxf

RAIZ = Path(__file__).resolve().parents[1]
CARPETA_TEMPORAL = RAIZ / "tests" / "tmp_fase5_plantilla"
LPS_POR_DEFECTO = RAIZ / "standards" / "lps.json"
SCR = Path(__file__).resolve().with_suffix(".scr")
ODA_POR_DEFECTO = Path(r"C:\Program Files\ODA\ODAFileConverter 27.1.0\ODAFileConverter.exe")
ACCORE_POR_DEFECTO = Path(r"D:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe")
TIMEOUT_S = 120
SIN_DIALOGOS = 0x8003  # SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX
TOLERANCIA = 1e-9

CABECERA = {
    "$INSUNITS": 6, "$MEASUREMENT": 1, "$LUNITS": 2, "$LUPREC": 2, "$AUNITS": 0, "$AUPREC": 0,
    "$LTSCALE": 0.0003, "$PSLTSCALE": 1, "$CELTSCALE": 1.0, "$CLAYER": "0", "$DIMSTYLE": "COTA75",
    "$TEXTSTYLE": "75", "$TEXTSIZE": 0.09, "$LWDISPLAY": 0,
}
TIPOS_LINEA = {
    "Continuous": ("Solid line", 0.0, ()),
    "CENTER": ("Center ____ _ ____ _ ____ _ ____ _ ____ _ ____", 50.8, (31.75, -6.35, 6.35, -6.35)),
    "CONTINUA": ("Linea ininterrumpida", 0.0, ()),
}
ESTILOS_TEXTO = {
    "Standard": {"font": "arial.ttf", "height": 0.0},
    "75": {"font": "micross.ttf", "height": 0.09, "width": 1.0, "oblique": 0.0},
    "cota75": {"font": "simplex.shx", "height": 0.09, "width": 1.0},
}
COTA75 = {
    "dimtxt": 0.09, "dimasz": 0.045, "dimexo": 0.1, "dimexe": 0.1, "dimdli": 0.0, "dimgap": 0.015,
    "dimdec": 2, "dimtdec": 2, "dimzin": 4, "dimtzin": 8, "dimtad": 1, "dimtih": 0, "dimtoh": 0,
    "dimse1": 1, "dimse2": 1, "dimtix": 1, "dimtofl": 1, "dimtmove": 2, "dimatfit": 0, "dimtolj": 0,
    "dimcen": 0.2, "dimclrd": 256, "dimclre": 8, "dimclrt": 256, "dimlwd": -1,
    "dimaltf": 0.03937007874016, "dimaltd": 3, "dimalttd": 3,
    "dimblk": "C", "dimblk1": "", "dimblk2": "", "dimldrblk": "", "dimtxsty": "cota75",
}
COTA75_FUERA_DE_TABLA = {"$DIMDSEP": 46, "$DIMADEC": 0, "$DIMAZIN": 0, "$DIMFXL": 1.0}
# Lo que accoreconsole escribe tras _ST con el formato de DIMDEC 2. DIMSE1/2 no: su valor depende del idioma.
COTA75_EN_AUTOCAD = {
    "DIMTXT": "0.09", "DIMASZ": "0.05", "DIMEXO": "0.10", "DIMEXE": "0.10", "DIMDLI": "0.00", "DIMGAP": "0.02",
    "DIMDEC": "2", "DIMTDEC": "2", "DIMZIN": "4", "DIMTZIN": "8", "DIMTAD": "1", "DIMTMOVE": "2", "DIMATFIT": "0",
    "DIMCEN": "0.20", "DIMCLRE": "8", "DIMLWD": "-1", "DIMBLK": "C", "DIMTXSTY": "cota75", "DIMSCALE": "1.00",
    "DIMDSEP": ".", "DIMADEC": "0", "DIMAZIN": "0",
}
ENTIDADES_BLOQUE_C = {"CIRCLE": 1, "LINE": 3, "LWPOLYLINE": 1}


class Tabla:
    def __init__(self):
        self.filas = []

    def fila(self, comprobacion, ok, esperado, leido):
        self.filas.append((bool(ok), comprobacion, esperado, leido))

    def igual(self, comprobacion, esperado, leido):
        self.fila(comprobacion, iguales(esperado, leido), esperado, leido)

    def imprimir(self):
        ancho = max(len(f[1]) for f in self.filas)
        for ok, comprobacion, esperado, leido in self.filas:
            print(f"{'OK   ' if ok else 'FALLO'} | {comprobacion:<{ancho}} | esperado {esperado!r} | leído {leido!r}")
        fallos = sum(1 for f in self.filas if not f[0])
        print(f"{len(self.filas)} comprobaciones, {fallos} fallo(s)")
        return fallos


def iguales(a, b):
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return abs(a - b) <= TOLERANCIA
    return a == b


def plantilla_a_dxf(plantilla, trabajo, oda):
    entrada, salida = trabajo / "in", trabajo / "out"
    entrada.mkdir()
    salida.mkdir()
    copia = entrada / "LPS_METROS.dwg"
    shutil.copyfile(plantilla, copia)
    subprocess.run(
        [str(oda), str(entrada), str(salida), "ACAD2018", "DXF", "0", "1", "*.DWG"],
        cwd=trabajo, capture_output=True, timeout=TIMEOUT_S, creationflags=subprocess.CREATE_NO_WINDOW,
    )
    dxf = salida / "LPS_METROS.dxf"
    if not dxf.is_file():
        raise SystemExit("ODA no generó el DXF de la plantilla.")
    return copia, dxf


def comprobar_cabecera(doc, t):
    for variable, valor in CABECERA.items():
        t.igual(f"cabecera {variable}", valor, doc.header.get(variable))


def comprobar_capas(doc, capas, t):
    leidas = {c.dxf.name: c for c in doc.layers}
    t.igual("capas: las de lps.json y ninguna más", sorted(capas), sorted(leidas))
    for nombre, p in capas.items():
        esperado = (p["color"], p["tipo_linea"], p["grosor"], p["plot"])
        c = leidas.get(nombre)
        leido = (c.dxf.color, c.dxf.linetype, c.dxf.lineweight, c.dxf.plot) if c else None
        t.igual(f"capa {nombre}", esperado, leido)


def comprobar_tipos_linea(doc, t):
    leidos = {l.dxf.name for l in doc.linetypes} - {"ByBlock", "ByLayer"}
    t.igual("tipos de línea: solo los de §5H.4", sorted(TIPOS_LINEA), sorted(leidos))
    for nombre, (descripcion, total, elementos) in TIPOS_LINEA.items():
        if nombre not in doc.linetypes:
            continue
        lt = doc.linetypes.get(nombre)
        etiquetas = lt.pattern_tags.tags
        longitud = next((v for c, v in etiquetas if c == 40), None)
        leido = (lt.dxf.description, longitud, tuple(v for c, v in etiquetas if c == 49))
        t.igual(f"tipo de línea {nombre}", (descripcion, total, elementos), leido)


def comprobar_estilos_texto(doc, t):
    leidos = {s.dxf.name for s in doc.styles}
    t.igual("estilos de texto: solo los de §5H.4", sorted(ESTILOS_TEXTO), sorted(leidos))
    for nombre, valores in ESTILOS_TEXTO.items():
        if nombre not in doc.styles:
            continue
        estilo = doc.styles.get(nombre)
        leido = {k: estilo.dxf.get(k) for k in valores}
        t.fila(f"estilo de texto {nombre}", all(iguales(valores[k], leido[k]) for k in valores), valores, leido)


def comprobar_cota75(doc, t):
    t.igual("estilos de cota: Standard y COTA75", ["COTA75", "Standard"], sorted(d.dxf.name for d in doc.dimstyles))
    estilo = doc.dimstyles.get("COTA75")
    for nombre, valor in COTA75.items():
        t.igual(f"COTA75 {nombre.upper()}", valor, estilo.dxf.get(nombre))
        t.igual(f"cabecera ${nombre.upper()}", valor, doc.header.get(f"${nombre.upper()}"))
    for variable, valor in COTA75_FUERA_DE_TABLA.items():
        t.igual(f"cabecera {variable} (fuera de la tabla)", valor, doc.header.get(variable))


def comprobar_bloque_c(doc, t):
    t.igual("bloques: solo C y los espacios", ["*Model_Space", "*Paper_Space", "C"], sorted(b.name for b in doc.blocks))
    if "C" not in doc.blocks:
        return
    bloque = doc.blocks.get("C")
    tipos = {}
    for e in bloque:
        tipos[e.dxftype()] = tipos.get(e.dxftype(), 0) + 1
    t.igual("bloque C: entidades", ENTIDADES_BLOQUE_C, tipos)
    t.igual("bloque C: base", (0.0, 0.0, 0.0), tuple(bloque.block.dxf.base_point))
    t.igual("bloque C: capa de sus entidades", ["0"], sorted({e.dxf.layer for e in bloque}))
    t.igual("bloque C: anchura de la polilínea", [0.4], [e.dxf.const_width for e in bloque.query("LWPOLYLINE")])
    t.igual("bloque C: metadato de unidades", 0, bloque.block_record.dxf.units)


def comprobar_entidades(doc, t):
    t.igual("layouts", ["Layout1", "Model"], sorted(doc.layout_names()))
    for nombre in doc.layout_names():
        t.igual(f"entidades en {nombre}", 0, len(doc.layout(nombre)))


def ejecutar_accoreconsole(copia, trabajo, accore):
    """Devuelve el texto de la consola: accoreconsole escribe UTF-16 LE en stdout.

    Sin rastro en el perfil del usuario: /isolate lleva variables y datos de usuario a la carpeta de trabajo, TEMP y
    TMP evitan los archivos accc* que accoreconsole puede dejar vacíos en %TEMP%, y SetErrorMode (lo hereda el proceso
    hijo) evita que un fallo abra un diálogo de Windows en el escritorio.
    """
    if not SCR.is_file():
        raise SystemExit(f"No existe {SCR}")
    perfil, temporal = trabajo / "perfil", trabajo / "temp"
    perfil.mkdir()
    temporal.mkdir()
    entorno = {**os.environ, "TEMP": str(temporal), "TMP": str(temporal)}
    ctypes.windll.kernel32.SetErrorMode(SIN_DIALOGOS)
    r = subprocess.run(
        [str(accore), "/i", str(copia), "/s", str(SCR), "/l", "es-ES", "/isolate", "mcpautocad", str(perfil)],
        cwd=trabajo, env=entorno, capture_output=True, timeout=TIMEOUT_S, creationflags=subprocess.CREATE_NO_WINDOW,
    )
    return r.returncode, r.stdout.decode("utf-16-le", "replace").replace("\0", "")


def valor_prompt(log, comando):
    m = re.search(re.escape(comando) + r".*?<([^>]*)>", log, re.DOTALL)
    return m.group(1).strip().strip('"') if m else None


def capas_de_consola(log):
    """Filas de -LAYER ?: nombre -> (color, tipo de línea, grosor en centésimas de mm o -3 si es el de por defecto)."""
    capas = {}
    for m in re.finditer(r'^"([^"]+)"\s+\S+\s+\S*\s*(\d+)[^"\n]*"([^"]+)"\s+(.*)$', log, re.MULTILINE):
        grosor = re.match(r"([\d.]+) mm", m.group(4).strip())
        capas[m.group(1)] = (int(m.group(2)), m.group(3), round(float(grosor.group(1)) * 100) if grosor else -3)
    return capas


def comprobar_consola(log, codigo, capas, t):
    t.igual("accoreconsole: código de salida", 0, codigo)
    t.fila("accoreconsole: llegó a QUIT", "_.QUIT" in log, "_.QUIT en el log", "sí" if "_.QUIT" in log else "no")
    for comando, esperado in (("_.INSUNITS", "6"), ("_.MEASUREMENT", "1"), ("_.LTSCALE", "0.0003"),
                              ("_.TEXTSTYLE", "75"), ("_.CLAYER", "0")):
        t.igual(f"accoreconsole {comando}", esperado, valor_prompt(log, comando))
    m = re.search(r'DIMSTYLE = "([^"]*)"', log)
    t.igual("accoreconsole DIMSTYLE", "COTA75", m.group(1) if m else None)

    inicio, estado = log.find("_.-DIMSTYLE"), log.find("_ST", log.find("_.-DIMSTYLE"))
    anulaciones = re.findall(r"^\s+(DIM\w+)\s", log[inicio:estado], re.MULTILINE) if 0 <= inicio < estado else None
    t.igual("accoreconsole: anulaciones de COTA75", [], anulaciones)
    variables = dict(re.findall(r"^(DIM\w+) +(\S+)", log[estado:], re.MULTILINE)) if estado >= 0 else {}
    for nombre, valor in COTA75_EN_AUTOCAD.items():
        t.igual(f"accoreconsole {nombre}", valor, variables.get(nombre))

    leidas = capas_de_consola(log)
    t.igual("accoreconsole: capas", sorted(capas, key=str.upper), sorted(leidas, key=str.upper))
    for nombre, p in capas.items():
        t.igual(f"accoreconsole capa {nombre}", (p["color"], p["tipo_linea"], p["grosor"]), leidas.get(nombre))


def main(argv=None):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description="Verifica standards/LPS_METROS.dwt (protocol.md §5H.4).")
    p.add_argument("plantilla", type=Path)
    p.add_argument("--oda", type=Path, default=ODA_POR_DEFECTO)
    p.add_argument("--accoreconsole", type=Path, nargs="?", const=ACCORE_POR_DEFECTO, default=None)
    p.add_argument("--lps", type=Path, default=LPS_POR_DEFECTO)
    a = p.parse_args(argv)
    for requerido in [a.plantilla, a.oda, a.lps] + ([a.accoreconsole] if a.accoreconsole else []):
        if not requerido.is_file():
            raise SystemExit(f"No existe {requerido}")

    capas = json.loads(a.lps.read_text(encoding="utf-8"))["capas_permitidas"]
    t = Tabla()
    CARPETA_TEMPORAL.mkdir(parents=True, exist_ok=True)
    trabajo = Path(tempfile.mkdtemp(prefix="verificar_", dir=CARPETA_TEMPORAL))
    try:
        copia, dxf = plantilla_a_dxf(a.plantilla, trabajo, a.oda)
        doc = ezdxf.readfile(dxf)
        comprobar_cabecera(doc, t)
        comprobar_capas(doc, capas, t)
        comprobar_tipos_linea(doc, t)
        comprobar_estilos_texto(doc, t)
        comprobar_cota75(doc, t)
        comprobar_bloque_c(doc, t)
        comprobar_entidades(doc, t)
        if a.accoreconsole:
            codigo, log = ejecutar_accoreconsole(copia, trabajo, a.accoreconsole)
            comprobar_consola(log, codigo, capas, t)
    finally:
        shutil.rmtree(trabajo, ignore_errors=True)
        try:
            CARPETA_TEMPORAL.rmdir()  # solo si quedó vacía
        except OSError:
            pass
    return 1 if t.imprimir() else 0


if __name__ == "__main__":
    sys.exit(main())
