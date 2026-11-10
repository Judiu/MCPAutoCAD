"""Arma el paquete del puente AutoCAD para LPS Escritorio.

Salida: dist\\puente-<version>\\
    MCPAutoCAD.bundle\\            plugin listo para copiar a %APPDATA%\\Autodesk\\ApplicationPlugins\\
    servidor\\mcp-autocad\\        servidor MCP (PyInstaller onedir, mcp-autocad.exe)
    manifest.json                 versiones + SHA-256 y tamaño de cada archivo
    CONTRATO_INSTALACION.md       copia de packaging\\CONTRATO_INSTALACION.md

Uso:  python packaging\\empaquetar.py [--sin-servidor]
No instala nada, no toca %APPDATA% ni el registro. El venv de construcción vive en packaging\\.venv
(se crea si falta) para que PyInstaller no arrastre paquetes del Python global.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
PKG = REPO / "packaging"
VENV = PKG / ".venv"
PY = VENV / "Scripts" / "python.exe"
PROY_PLUGIN = REPO / "plugin" / "MCPAutoCAD.Plugin" / "MCPAutoCAD.Plugin.csproj"
EXCLUIR_PLUGIN = {".pdb"}
# Versión del paquete: independiente de la del servidor y la del plugin.
PAQUETE_VERSION = "0.9.8"
# Manifest de herramientas que escribe el plugin al cargar en AutoCAD. Va junto al exe del servidor para que un PC
# donde el plugin aún no cargó publique igualmente todas las herramientas (servidor 0.8.3, default_manifest_path).
MANIFEST_PLUGIN = Path(os.path.expandvars(r"%LOCALAPPDATA%\MCPAutoCAD\manifest.json"))
# Una compilación del plugin por versión de AutoCAD: misma fuente, distinto acmgd/acdbmgd/accoremgd.
RELEASES = [  # (serie, producto, carpeta de AutoCAD)
    ("R25.1", "AutoCAD 2026", r"D:\Program Files\Autodesk\AutoCAD 2026"),
    ("R26.0", "AutoCAD 2027", r"C:\Program Files\Autodesk\AutoCAD 2027"),
]


def sha256(p: Path) -> str:
    h = hashlib.sha256()
    with p.open("rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def version_plugin() -> str:
    csproj = (REPO / "plugin" / "MCPAutoCAD.Plugin" / "MCPAutoCAD.Plugin.csproj").read_text(encoding="utf-8")
    return re.search(r"<Version>([^<]+)</Version>", csproj).group(1)


def version_servidor() -> str:
    return re.search(r'^version\s*=\s*"([^"]+)"', (REPO / "server" / "pyproject.toml").read_text(encoding="utf-8"), re.M).group(1)


def compilar_plugin(acad: str, salida: Path) -> None:
    for n in ("acmgd.dll", "acdbmgd.dll", "accoremgd.dll"):
        if not (Path(acad) / n).exists():
            sys.exit(f"Falta {n} en {acad}")
    r = subprocess.run(["dotnet", "build", str(PROY_PLUGIN), "-c", "Release", "--no-incremental", "-nologo",
                        "-p:AcadDir=" + acad + "\\", "-o", str(salida)], capture_output=True, text=True)
    if r.returncode:
        sys.exit(f"dotnet build contra {acad} falló:\n" + r.stdout[-3000:])


COMPONENTE = """
  <Components Description="Plugin .NET del puente MCP para {prod}">
    <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="{serie}" SeriesMax="{serie}" />
    <ComponentEntry AppName="MCPAutoCAD" Version="{v}" ModuleName="./Contents/{serie}/MCPAutoCAD.Plugin.dll"
        AppDescription="Servidor del named pipe mcp-autocad-usuario-PID ({prod})." LoadOnAutoCADStartup="True">
      <Commands GroupName="MCPAUTOCAD">
        <Command Local="MCPESTADO" Global="MCPESTADO" />
      </Commands>
    </ComponentEntry>
  </Components>
"""

PAQUETE_XML = """<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="AutoCAD" ProductType="Application" Name="MCPAutoCAD"
    Description="Puente MCP de LPS para AutoCAD: named pipe local restringido al usuario actual."
    AppVersion="{v}" FriendlyVersion="{v}" Author="LPS"
    ProductCode="{{B3C0F6C2-5E7A-4C5B-9C1D-0A1F0C0D0E01}}" UpgradeCode="{{B3C0F6C2-5E7A-4C5B-9C1D-0A1F0C0D0E02}}">
  <CompanyDetails Name="LPS" />
  <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="{smin}" SeriesMax="{smax}" />
{comps}
</ApplicationPackage>
"""


def armar_bundle(destino: Path, vp: str) -> None:
    """Contents\\<serie>\\ lleva la DLL de esa versión y su copia del estándar: el plugin busca lps.json,
    biblioteca\\ y plantillas\\ junto a su DLL. Contents\\lps.json y Contents\\plantillas\\ quedan además en la
    raíz: son las rutas fijas del contrato para MCP_AUTOCAD_LPS y QnewTemplate, comunes a todas las versiones."""
    contents = destino / "Contents"
    contents.mkdir(parents=True)
    comps = "".join(COMPONENTE.format(prod=prod, serie=serie, v=vp) for serie, prod, _ in RELEASES)
    (destino / "PackageContents.xml").write_text(
        PAQUETE_XML.format(v=vp, smin=RELEASES[0][0], smax=RELEASES[-1][0], comps=comps), encoding="utf-8")
    tmp = PKG / "build_plugin"
    for serie, _, acad in RELEASES:
        shutil.rmtree(tmp, ignore_errors=True)
        compilar_plugin(acad, tmp)
        for p in tmp.rglob("*"):
            if p.is_file() and p.suffix.lower() not in EXCLUIR_PLUGIN:
                d = contents / serie / p.relative_to(tmp)
                d.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(p, d)
        copiar_estandar(contents / serie)
    shutil.rmtree(tmp, ignore_errors=True)
    std = REPO / "standards"
    shutil.copy2(std / "lps.json", contents / "lps.json")
    (contents / "plantillas").mkdir(exist_ok=True)
    shutil.copy2(std / "LPS_METROS.dwt", contents / "plantillas" / "LPS_METROS.dwt")


def copiar_estandar(contents: Path) -> None:
    # Igual que install-plugin.ps1: el estándar, la biblioteca y la plantilla salen de standards\ (sobrescriben el build).
    std = REPO / "standards"
    shutil.copy2(std / "lps.json", contents / "lps.json")
    (contents / "biblioteca").mkdir(exist_ok=True)
    for n in ("LPS_ROTULO.dwg", "Puerta Dinamica LM.dwg", "PuertaBaño.dwg", "VENTANA01.dwg", "TANQUE 1000 LTS.dwg"):
        if (std / n).exists():
            shutil.copy2(std / n, contents / "biblioteca" / n)
        else:
            print("AVISO: falta", std / n, "(no se versiona: ver standards/README.md; pregunte al usuario antes de crearlo)")
    (contents / "plantillas").mkdir(exist_ok=True)
    shutil.copy2(std / "LPS_METROS.dwt", contents / "plantillas" / "LPS_METROS.dwt")


def armar_servidor(destino: Path) -> None:
    if not PY.exists():
        subprocess.run([sys.executable, "-m", "venv", str(VENV)], check=True)
        subprocess.run([str(PY), "-m", "pip", "install", "-q", f"{REPO / 'server'}[oda]", "pyinstaller"], check=True)
    else:  # el servidor puede haber cambiado: reinstalarlo sin dependencias nuevas
        subprocess.run([str(PY), "-m", "pip", "install", "-q", "--no-deps", "--force-reinstall", str(REPO / "server")], check=True)
    trabajo = PKG / "build"
    shutil.rmtree(trabajo, ignore_errors=True)
    args = [str(PY), "-m", "PyInstaller", "--noconfirm", "--onedir", "--console", "--name", "mcp-autocad",
            "--distpath", str(destino), "--workpath", str(trabajo / "work"), "--specpath", str(trabajo),
            "--collect-submodules", "mcp_autocad", "--collect-submodules", "mcp.server", "--collect-submodules", "mcp.shared",
            "--collect-submodules", "mcp_types", "--collect-submodules", "ezdxf",
            "--exclude-module", "mcp.cli", "--exclude-module", "tkinter", "--exclude-module", "matplotlib",
            "--exclude-module", "PySide6", "--exclude-module", "PyQt5", "--exclude-module", "PyQt6",
            "--hidden-import", "win32pipe", "--hidden-import", "win32file", "--hidden-import", "pywintypes",
            str(PKG / "entrada_servidor.py")]
    r = subprocess.run(args, capture_output=True, text=True)
    if r.returncode:
        sys.exit("PyInstaller falló:\n" + r.stderr[-3000:])
    shutil.rmtree(trabajo, ignore_errors=True)


def copiar_manifest_plugin(destino: Path, vp: str) -> None:
    """Exige que el manifest lo haya escrito el plugin de esta misma versión (cargado al menos una vez en AutoCAD)."""
    if not MANIFEST_PLUGIN.exists():
        sys.exit(f"Falta {MANIFEST_PLUGIN}: abra AutoCAD con el plugin {vp} cargado una vez y repita.")
    m = json.loads(MANIFEST_PLUGIN.read_text(encoding="utf-8"))
    if "--prueba" in sys.argv and m.get("plugin_version") != vp:
        # Paquete de prueba: el plugin nuevo aún no cargó en AutoCAD. Se usa el manifest anterior; en un PC donde el
        # plugin ya cargó, el suyo (LOCALAPPDATA) tiene prioridad. No se publica: el definitivo exige el manifest nuevo.
        shutil.copy2(MANIFEST_PLUGIN, destino)
        print(f"AVISO --prueba: manifest del plugin {m.get('plugin_version')} (no {vp}); no publicar este paquete")
        return
    if m.get("plugin_version") != vp or not m.get("tools"):
        sys.exit(f"{MANIFEST_PLUGIN} es del plugin {m.get('plugin_version')!r}, no del {vp}: cargue el plugin nuevo en AutoCAD y repita.")
    shutil.copy2(MANIFEST_PLUGIN, destino)
    print(f"manifest del plugin {vp}: {len(m['tools'])} herramientas")


def listar(raiz: Path, sub: str) -> list[dict]:
    base = raiz / sub
    return [{"ruta": str(p.relative_to(base)).replace("\\", "/"), "bytes": p.stat().st_size, "sha256": sha256(p)}
            for p in sorted(base.rglob("*")) if p.is_file()]


def main() -> None:
    con_servidor = "--sin-servidor" not in sys.argv
    vp, vs = version_plugin(), version_servidor()
    version = PAQUETE_VERSION
    out = REPO / "dist" / (f"puente-{version}-prueba" if "--prueba" in sys.argv else f"puente-{version}")
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    armar_bundle(out / "MCPAutoCAD.bundle", vp)
    if con_servidor:
        armar_servidor(out / "servidor")
        copiar_manifest_plugin(out / "servidor" / "mcp-autocad" / "manifest_plugin.json", vp)
    shutil.copy2(PKG / "CONTRATO_INSTALACION.md", out / "CONTRATO_INSTALACION.md")
    manifest = {
        "esquema": 1,
        "paquete": "puente-autocad-lps",
        "version": version,
        "generado": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "plugin": {"version": vp, "autocad": "AutoCAD completo, no LT",
                   "releases": [{"serie": s, "producto": p, "anio": int(p.split()[-1]), "dll": f"Contents/{s}/MCPAutoCAD.Plugin.dll"}
                                for s, p, _ in RELEASES],
                   "carpeta": "MCPAutoCAD.bundle",
                   "destino": "%APPDATA%\\Autodesk\\ApplicationPlugins\\MCPAutoCAD.bundle",
                   "lps_json": "Contents/lps.json",
                   "plantilla_qnew": "Contents/plantillas/LPS_METROS.dwt",
                   "archivos": listar(out, "MCPAutoCAD.bundle")},
        "servidor": None if not con_servidor else {
            "version": vs, "carpeta": "servidor/mcp-autocad", "exe": "servidor/mcp-autocad/mcp-autocad.exe",
            "transporte": "stdio", "nombre_mcp": "autocad", "archivos": listar(out, "servidor/mcp-autocad")},
    }
    (out / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=1), encoding="utf-8")
    # copia del manifiesto dentro del bundle: así la app sabe qué versión está instalada en %APPDATA%
    (out / "MCPAutoCAD.bundle" / "manifest.json").write_text(
        json.dumps({k: manifest[k] for k in ("esquema", "paquete", "version", "generado")} | {"plugin": manifest["plugin"]},
                   ensure_ascii=False, indent=1), encoding="utf-8")
    tam = sum(p.stat().st_size for p in out.rglob("*") if p.is_file())
    print(f"Paquete {out}  plugin {vp}  servidor {vs if con_servidor else '-'}  {tam / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
