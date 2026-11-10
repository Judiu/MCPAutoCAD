"""Pasa el contenido del rótulo LPS a la capa Rotulo (estándar v2) sin cambiar su aspecto impreso.

Uso: python normalizar_capas_rotulo.py <entrada.dxf> <salida.dxf> [<lps.json>]

Paso de la cadena del rótulo (decisión del usuario, 2026-10-31):
  crear_rotulo.py -> agregar_logo.py -> normalizar_capas_rotulo.py -> ODA (DXF->DWG ACAD2018)
  -> standards/LPS_ROTULO.dwg
La entrada puede ser la salida de agregar_logo.py o el DXF que ODA saca del LPS_ROTULO.dwg vigente (así se
regeneró la versión 2 sin volver a construir el logo). Es idempotente: aplicado dos veces da el mismo resultado.

- Toda entidad del espacio modelo que no esté en la capa 0 ni en Rotulo pasa a Rotulo. Las de la capa 0 (el logo)
  no se tocan: dentro del bloque siguen heredando la capa del INSERT.
- Para no cambiar el aspecto impreso, antes de moverla se fijan en la entidad los valores que hoy hereda de su
  capa de origen: color (ACI y color verdadero) si es ByLayer, tipo de línea si es ByLayer, grosor si es ByLayer y
  transparencia si es ByLayer y la capa no es opaca. Lo que la entidad ya tiene propio (incluido ByBlock) se
  conserva. Los ATTDEF conservan TAG, prompt, valor, estilo, altura, posición y MTEXT embebido (el objeto embebido
  no lleva capa; solo cambia la capa del ATTDEF).
- La capa Rotulo se crea o se ajusta con las propiedades de lps.json (capas_permitidas.Rotulo: color, tipo de
  línea, grosor, plot).
- Se eliminan las capas que quedan sin uso y no son 0 ni Rotulo (las de exportación del plano de referencia y la
  Defpoints que añade ezdxf), para que Database.Insert no traiga capas fuera del estándar.
- No toca unidades (INSUNITS), punto base, estilos ni geometría.
- Limitación: pensado para el rótulo LPS, no para cualquier bloque. Un INSERT anidado cuyo contenido esté en la
  capa 0 heredaría el color de Rotulo, y una entidad ByLayer en una capa con plot=0 perdería el "no plotear"
  (plot no se fija por entidad). El rótulo LPS no tiene ninguno de los dos casos.
"""
import json
import sys
from pathlib import Path

import ezdxf
from ezdxf.lldxf import const

CAPA_DESTINO = "Rotulo"
LPS_POR_DEFECTO = Path(__file__).resolve().parents[1] / "standards" / "lps.json"


def propiedades_rotulo(ruta_lps):
    datos = json.loads(Path(ruta_lps).read_text(encoding="utf-8"))
    nombre = datos.get("rotulo", {}).get("capa", CAPA_DESTINO)
    p = datos["capas_permitidas"][nombre]
    return nombre, p


def preparar_capa(doc, nombre, p):
    if nombre not in doc.layers:
        doc.layers.add(nombre)
    capa = doc.layers.get(nombre)
    if p["tipo_linea"] not in doc.linetypes:
        raise SystemExit(f"El tipo de línea '{p['tipo_linea']}' de la capa {nombre} no existe en el DXF.")
    capa.dxf.color = p["color"]
    capa.dxf.discard("true_color")
    capa.dxf.linetype = p["tipo_linea"]
    capa.dxf.lineweight = p["grosor"]
    capa.dxf.plot = p["plot"]
    capa.dxf.flags = 0  # sin congelar ni bloquear
    return capa


def fijar_por_capa(e, capa):
    """Fija en la entidad las propiedades ByLayer resueltas con su capa de origen; devuelve las fijadas."""
    fijadas = []
    color = e.dxf.get("color", const.BYLAYER)
    if color == const.BYLAYER and not e.dxf.hasattr("true_color"):
        # color de capa negativo = capa apagada; el valor real es el absoluto
        e.dxf.color = abs(capa.dxf.color)
        fijadas.append("color")
        if capa.dxf.hasattr("true_color"):
            e.dxf.true_color = capa.dxf.true_color
            fijadas.append("true_color")
    if e.dxf.get("linetype", "BYLAYER").upper() == "BYLAYER":
        e.dxf.linetype = capa.dxf.linetype
        fijadas.append("linetype")
    if e.dxf.get("lineweight", const.LINEWEIGHT_BYLAYER) == const.LINEWEIGHT_BYLAYER:
        e.dxf.lineweight = capa.dxf.lineweight
        fijadas.append("lineweight")
    if not e.dxf.hasattr("transparency") and capa.transparency > 0:
        e.transparency = capa.transparency
        fijadas.append("transparency")
    return fijadas


def main(entrada, salida, ruta_lps=LPS_POR_DEFECTO):
    nombre, p = propiedades_rotulo(ruta_lps)
    doc = ezdxf.readfile(entrada)
    msp = doc.modelspace()
    origenes = {}
    for e in msp:
        capa_origen = e.dxf.layer
        if capa_origen == "0" or capa_origen == nombre:
            continue
        capa = doc.layers.get(capa_origen)
        fijadas = fijar_por_capa(e, capa)
        e.dxf.layer = nombre
        clave = origenes.setdefault(capa_origen, {"entidades": 0, "fijadas": {}})
        clave["entidades"] += 1
        for f in fijadas:
            clave["fijadas"][f] = clave["fijadas"].get(f, 0) + 1

    preparar_capa(doc, nombre, p)

    usadas = {e.dxf.layer for e in msp}
    for b in doc.blocks:
        usadas.update(e.dxf.layer for e in b)
    borradas = []
    for capa in list(doc.layers):
        n = capa.dxf.name
        if n in ("0", nombre) or n in usadas:
            continue
        doc.layers.remove(n)
        borradas.append(n)

    doc.saveas(salida)
    for capa_origen, d in sorted(origenes.items()):
        print(f"{capa_origen} -> {nombre}: {d['entidades']} entidades; fijadas {d['fijadas']}")
    print(f"capas eliminadas={borradas} capas finales={[c.dxf.name for c in doc.layers]} "
          f"entidades={len(msp)} attdef={len(msp.query('ATTDEF'))}")


if __name__ == "__main__":
    main(*sys.argv[1:])
