# Bundle MCPAutoCAD

`MCPAutoCAD.bundle/PackageContents.xml` es la fuente del bundle de carga automática de AutoCAD 2026
(atributos fijados en `protocol.md` §9: `SeriesMin/SeriesMax = R25.1`, `LoadOnAutoCADStartup = True`,
`ModuleName = ./Contents/MCPAutoCAD.Plugin.dll`).

Versión del plugin empaquetado: 0.4.0 (protocolo 0.4; `FileVersion` 0.4.0.0).

Este plugin **no se instala solo**. La instalación la hace el usuario con `scripts/install-plugin.ps1`, que:

1. copia este `PackageContents.xml` a
   `%APPDATA%\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\PackageContents.xml`,
2. copia **todo** el contenido de la carpeta de salida Release
   `plugin/MCPAutoCAD.Plugin/bin/Release/net10.0-windows/` a
   `%APPDATA%\Autodesk\ApplicationPlugins\MCPAutoCAD.bundle\Contents\`,
3. copia `standards/lps.json` a `Contents\lps.json` (sobrescribe la copia del build). Si no existe, avisa y
   sigue: el plugin carga igual, sin la advertencia de unidades LPS (protocol.md §2.2), y
4. desde 0.4, copia `standards/LPS_ROTULO.dwg` a `Contents\biblioteca\LPS_ROTULO.dwg` (crea la carpeta;
   sobrescribe la copia del build). Es la **biblioteca de bloques** de `insertar_bloque` con
   `desde_biblioteca: true` (protocol.md §5D.5). Si no existe, avisa y sigue: el plugin carga igual, pero
   `LPS_ROTULO` no se puede importar.

Contenido instalado de `Contents\`: `MCPAutoCAD.Plugin.dll`, `MCPAutoCAD.GeometryCore.dll`, `*.deps.json`,
`*.runtimeconfig.json`, `*.pdb`, `lps.json` y `biblioteca\LPS_ROTULO.dwg`. El resumen del script muestra
`Estándar LPS: <ruta>` y `Biblioteca de bloques: <ruta>` (o `NO INSTALADO` / `NO INSTALADA`).

`ApplicationPlugins` es una ruta de confianza para SECURELOAD; no hace falta tocar `TRUSTEDPATHS`.

La carpeta de salida nunca contiene `acmgd.dll`, `acdbmgd.dll` ni `accoremgd.dll` (referencias con
`Private=false`). `System.IO.Pipes.AccessControl.dll` tampoco aparece: en .NET 10 viene en el runtime
compartido y no se copia.
