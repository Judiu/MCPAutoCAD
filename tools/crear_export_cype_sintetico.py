"""Genera tests/fixtures/sintetico_cype.dxf: un export de CYPE SINTÉTICO para probar insertar_export (protocol.md §5G.4).

Uso: python -I tools/crear_export_cype_sintetico.py [<salida.dxf>]

PENDIENTE DE VERIFICAR CON UN EXPORT REAL DE CYPE (ninguno en fixtures): las capas y la geometría imitan lo que
describe PLAN.md, no un archivo real.

Reproducible: dos ejecuciones dan un DXF idéntico byte a byte (metadatos fijos de ezdxf). El DWG se obtiene del DXF con
ODA File Converter, desde una carpeta temporal con solo este DXF:
  ODAFileConverter.exe "<dir_in>" "<dir_out>" ACAD2018 DWG 0 1 "*.DXF"
y se copia a tests/fixtures/sintetico_cype.dwg.

Contenido (INSUNITS 4, mm; sin bloques), en el espacio modelo:
  - CIM_ZAPATAS (color 5): zapata de 1500 x 1500, LWPOLYLINE cerrada con esquina en (0, 0).
  - ARM_PILARES (color 1): pilar de 300 x 300 centrado en la zapata.
  - ALZ_PILARES (color 2): dos líneas de alzado de 3000.
  - ARM_VIGAS (color 3): tres LINE de armadura.
  - TEXTO_CYPE (color 7): TEXT 'P1' de altura 150; capa ajena al grupo cype de lps.json.
  - Capa 0 definida y sin entidades. 8 entidades en total.

Qué esperar al insertarlo con insertar_export en un plano en m (INSUNITS 6):
  - unidades_export 4, factor_unidades 0,001: la zapata mide 1,5 m en el plano.
  - n_entidades_export 8 y, con filtrar_capas 'todas', n_entidades_excluidas 0.
  - capas_importadas.cype = 4 capas (ALZ_PILARES, ARM_PILARES, ARM_VIGAS, CIM_ZAPATAS) y otras = ['TEXTO_CYPE'], con la
    advertencia de capas fuera del grupo CYPE.
  - Con filtrar_capas 'cype': n_entidades_excluidas 1 (el TEXT) y capas_importadas.otras = [].
"""
import sys
from pathlib import Path

import ezdxf
from ezdxf.lldxf.const import DXFValueError

SALIDA_POR_DEFECTO = Path(__file__).resolve().parents[1] / "tests" / "fixtures" / "sintetico_cype.dxf"

INSUNITS_MM = 4
CAPAS = {"ARM_PILARES": 1, "ARM_VIGAS": 3, "ALZ_PILARES": 2, "CIM_ZAPATAS": 5, "TEXTO_CYPE": 7}


def rectangulo(x, y, ancho, alto):
    return [(x, y), (x + ancho, y), (x + ancho, y + alto), (x, y + alto)]


def crear_documento():
    doc = ezdxf.new("R2018", setup=False)
    doc.header["$INSUNITS"] = INSUNITS_MM
    try:
        doc.layers.remove("Defpoints")  # la añade ezdxf; el fixture solo lleva las capas del contrato
    except DXFValueError:
        pass

    for nombre, color in CAPAS.items():
        doc.layers.add(nombre, color=color)

    msp = doc.modelspace()
    msp.add_lwpolyline(rectangulo(0, 0, 1500, 1500), close=True, dxfattribs={"layer": "CIM_ZAPATAS"})
    msp.add_lwpolyline(rectangulo(600, 600, 300, 300), close=True, dxfattribs={"layer": "ARM_PILARES"})

    for x in (2000, 2300):
        msp.add_line((x, 0), (x, 3000), dxfattribs={"layer": "ALZ_PILARES"})

    for x in (2050, 2150, 2250):
        msp.add_line((x, 50), (x, 2950), dxfattribs={"layer": "ARM_VIGAS"})

    msp.add_text("P1", height=150, dxfattribs={"layer": "TEXTO_CYPE", "insert": (600, 1700)})

    # El orden de la sección CLASSES depende de la semilla de hash de Python (ezdxf recorre un conjunto) y python -I
    # ignora PYTHONHASHSEED: se añaden aquí y se ordenan, y saveas ya no añade ninguna; así el DXF es idéntico byte a byte.
    doc.classes.add_required_classes(doc.dxfversion)
    doc.classes.classes = dict(sorted(doc.classes.classes.items()))
    return doc


def main():
    salida = Path(sys.argv[1]) if len(sys.argv) > 1 else SALIDA_POR_DEFECTO
    # Sin esto ezdxf escribe la fecha actual y GUID aleatorios en la cabecera y en sus metadatos.
    ezdxf.options.write_fixed_meta_data_for_testing = True
    crear_documento().saveas(salida)
    print(salida)


if __name__ == "__main__":
    main()
