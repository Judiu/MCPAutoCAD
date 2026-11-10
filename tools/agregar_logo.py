"""Añade el logo LPS vectorial (desde SVG) a la casilla del logo del rótulo generado por crear_rotulo.py.

Uso: python agregar_logo.py <rotulo.dxf> <logo.svg> <salida.dxf>

- El plano de referencia trae el logo como OLE2FRAME (objeto OLE incrustado), que crear_rotulo.py descarta porque
  no se puede copiar de forma fiable. Aquí se reconstruye desde el SVG oficial como geometría AutoCAD:
  rellenos -> HATCH SOLID (un bucle por subtrazado; los huecos de las letras por paridad), trazos sin relleno ->
  LWPOLYLINE con ancho constante = stroke-width. Curvas aproximadas por segmentos.
- Colores: rojo LPS en color verdadero; negro -> ACI 7 (negro al plotear). Capa 0 dentro del bloque.
- Orientación como en el rótulo original: girado 90° antihorario (ícono abajo, texto hacia arriba), ajustado al
  ancho de la casilla con margen y centrado.
- Requiere svgelements (no forma parte de las dependencias del MCP; solo de esta herramienta).
"""
import math
import sys

import ezdxf
from ezdxf import colors
from svgelements import SVG, Color, Path, Shape

# Casilla del logo en el rótulo (coordenadas del bloque, m): entre el recuadro PROYECTISTA (arriba en y=0.3529)
# y el recuadro PROYECTO (abajo en y=0.5676), interior x 0.002–0.0888. Ver crear_rotulo.py.
CASILLA = (0.002, 0.3529, 0.0888, 0.5676)
MARGEN = 0.08          # fracción del lado que se deja libre
PASO_CURVA = 0.75      # longitud máxima de segmento al aproximar curvas (unidades SVG)


def puntos(subpath):
    """Puntos de un subtrazado aproximando cada segmento curvo; devuelve (pts, cerrado)."""
    pts = []
    cerrado = False
    for seg in subpath:
        name = type(seg).__name__
        if name == "Move":
            pts.append((seg.end.x, seg.end.y))
            continue
        if name == "Close":
            cerrado = True
            continue
        if name == "Line":
            pts.append((seg.end.x, seg.end.y))
            continue
        n = max(2, math.ceil(seg.length() / PASO_CURVA))
        for i in range(1, n + 1):
            p = seg.point(i / n)
            pts.append((p.x, p.y))
    # quita duplicados consecutivos y el cierre repetido
    limpio = []
    for p in pts:
        if not limpio or math.dist(p, limpio[-1]) > 1e-9:
            limpio.append(p)
    if len(limpio) > 2 and math.dist(limpio[0], limpio[-1]) < 1e-9:
        limpio.pop()
        cerrado = True
    return limpio, cerrado


def leer_svg(ruta):
    """Lista de elementos: (tipo 'relleno'|'trazo', [(pts, cerrado)...], color, ancho_svg)."""
    svg = SVG.parse(ruta, reify=True)
    salida = []
    for el in svg.elements():
        if not isinstance(el, Shape):
            continue
        # Path(el) conserva la transformación aparte (p. ej. rect girados); reify() la aplica a los segmentos.
        path = Path(el)
        path.reify()
        subs = [puntos(s) for s in path.as_subpaths()]
        subs = [(p, c) for p, c in subs if len(p) >= 2]
        if not subs:
            continue
        fill = el.fill if el.fill is not None and el.fill.value is not None else None
        stroke = el.stroke if el.stroke is not None and el.stroke.value is not None else None
        if fill is not None and Color(fill).alpha > 0:
            salida.append(("relleno", [(p, True) for p, c in subs if len(p) >= 3], Color(fill), 0.0))
        if stroke is not None and Color(stroke).alpha > 0:
            salida.append(("trazo", subs, Color(stroke), float(el.stroke_width or 1.0)))
    return svg, salida


def main(rotulo, logo, destino):
    svg, elementos = leer_svg(logo)
    alto_svg = svg.viewbox.height if svg.viewbox is not None else svg.height

    # SVG (y hacia abajo) -> y hacia arriba -> giro 90° antihorario: (x, y) -> (-(H - y), x)
    def girar(p):
        x, y = p
        return (-(alto_svg - y), x)

    todos = [girar(q) for _, subs, _, _ in elementos for pts, _ in subs for q in pts]
    xmin = min(p[0] for p in todos)
    xmax = max(p[0] for p in todos)
    ymin = min(p[1] for p in todos)
    ymax = max(p[1] for p in todos)
    cx0, cy0, cx1, cy1 = CASILLA
    ancho, alto = (cx1 - cx0) * (1 - MARGEN), (cy1 - cy0) * (1 - MARGEN)
    s = min(ancho / (xmax - xmin), alto / (ymax - ymin))
    ox = (cx0 + cx1) / 2 - s * (xmin + xmax) / 2
    oy = (cy0 + cy1) / 2 - s * (ymin + ymax) / 2

    def a_bloque(p):
        u, v = girar(p)
        return (ox + s * u, oy + s * v)

    doc = ezdxf.readfile(rotulo)
    msp = doc.modelspace()
    n_hatch = n_pl = 0
    for tipo, subs, color, ancho_svg in elementos:
        attribs = {"layer": "0", "lineweight": 0}
        if (color.red, color.green, color.blue) == (0, 0, 0):
            attribs["color"] = 7
        else:
            attribs["true_color"] = colors.rgb2int((color.red, color.green, color.blue))
        if tipo == "relleno":
            h = msp.add_hatch(dxfattribs=attribs)
            if "true_color" in attribs:
                h.set_solid_fill(rgb=(color.red, color.green, color.blue))
            else:
                h.set_solid_fill(color=7)
            for pts, _ in subs:
                h.paths.add_polyline_path([a_bloque(q) for q in pts], is_closed=True)
            n_hatch += 1
        else:
            for pts, cerrado in subs:
                pl = msp.add_lwpolyline([a_bloque(q) for q in pts], close=cerrado, dxfattribs=attribs)
                pl.dxf.const_width = ancho_svg * s
                n_pl += 1
    doc.saveas(destino)
    print(f"escala={s:.6g} m/unidad_svg hatch={n_hatch} polilineas={n_pl} "
          f"logo=({ox + s * xmin:.4f},{oy + s * ymin:.4f})-({ox + s * xmax:.4f},{oy + s * ymax:.4f})")


if __name__ == "__main__":
    main(*sys.argv[1:])
