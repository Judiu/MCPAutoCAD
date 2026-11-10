"""Manifest empaquetado junto al exe: se usa solo si el plugin nunca escribió el suyo (PC recién instalado)."""
import json
import sys

from mcp_autocad import server as srv


def _preparar(tmp_path, monkeypatch, con_propio):
    exe_dir = tmp_path / "exe"
    exe_dir.mkdir()
    (exe_dir / "manifest_plugin.json").write_text(json.dumps({"tools": []}), encoding="utf-8")
    local = tmp_path / "local"
    if con_propio:
        (local / "MCPAutoCAD").mkdir(parents=True)
        (local / "MCPAutoCAD" / "manifest.json").write_text("{}", encoding="utf-8")
    monkeypatch.delenv(srv.ENV_MANIFEST, raising=False)
    monkeypatch.setenv("LOCALAPPDATA", str(local))
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    monkeypatch.setattr(sys, "executable", str(exe_dir / "mcp-autocad.exe"))
    return exe_dir, local


def test_sin_manifest_propio_usa_el_empaquetado(tmp_path, monkeypatch):
    exe_dir, _ = _preparar(tmp_path, monkeypatch, con_propio=False)
    assert srv.default_manifest_path() == (exe_dir / "manifest_plugin.json").resolve()


def test_el_propio_tiene_prioridad(tmp_path, monkeypatch):
    _, local = _preparar(tmp_path, monkeypatch, con_propio=True)
    assert srv.default_manifest_path() == local / "MCPAutoCAD" / "manifest.json"


def test_fuera_del_paquete_no_hay_empaquetado(tmp_path, monkeypatch):
    monkeypatch.delattr(sys, "frozen", raising=False)
    assert srv.paquete_manifest_path() is None
