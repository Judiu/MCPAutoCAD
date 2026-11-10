;; extraer_bloques_6x12.lsp - rutinas de la extraccion de la biblioteca de bloques del 6X12 (protocol.md 5G.0 y 5G.6).
;; Lo usa tools\extraer_bloques_6x12.ps1, que es quien documenta el proceso: no se ejecuta suelto.
;; El .ps1 copia este texto DENTRO del .scr (sin lineas de comentario ni en blanco) en vez de hacer (load ...):
;; asi no hace falta SECURELOAD 0, variable que AutoCAD guarda en el registro del perfil del usuario.
;; Archivo solo ASCII: los nombres con enye llegan como argumentos desde el .scr, escrito en Windows-1252.
;; Sin comentarios al final de una linea de codigo. En AutoLISP 't' es simbolo protegido: no usarlo como variable.
(defun mcp-extraer (nombre ruta)
  (if (tblsearch "BLOCK" nombre)
    (progn
      (command "_.-WBLOCK" ruta nombre)
      (princ (strcat "\nMCP-EXTRAIDO: " nombre)))
    (princ (strcat "\nMCP-ERROR: no existe el bloque " nombre)))
  (princ))
(defun mcp-sanear (desplazamiento)
  (setvar "INSUNITS" 6)
  (setvar "CLAYER" "0")
;; El -WBLOCK solo trae el estilo de cota actual (COTA75): no existe Standard. Se limpia el estilo actual de lo que
;; retiene el bloque C y cota75, se guarda como Standard (queda actual) y COTA75 pasa a ser purgable.
  (setvar "TEXTSTYLE" "Standard")
  (setvar "DIMTXSTY" "Standard")
  (setvar "DIMBLK" ".")
  (setvar "DIMBLK1" ".")
  (setvar "DIMBLK2" ".")
  (setvar "DIMLDRBLK" ".")
  (command "_.-DIMSTYLE" "_S" "Standard")
  (if desplazamiento
    (command "_.MOVE" "_ALL" "" "_D" desplazamiento))
  (repeat 3
    (command "_.-PURGE" "_A" "*" "_N"))
  (if (tblsearch "BLOCK" "C")
    (command "_.-PURGE" "_B" "C" "_N"))
  (command "_.AUDIT" "_Y")
  (princ "\nMCP-SANEADO")
  (princ))
