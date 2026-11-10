namespace MCPAutoCAD.GeometryCore
{
    /// <summary>
    /// Reglas puras sobre la variable UNDOCTL (protocol.md §3.5 / §3.9) para decidir cuándo el plugin puede ejecutar
    /// "_.U" sin riesgo de deshacer un paso del usuario. Bits de UNDOCTL: 1 = deshacer activo, 2 = solo un paso
    /// ("UNDO Control One"), 4 = agrupación automática, 8 = hay un grupo BEGIN abierto (por el usuario u otro código).
    /// </summary>
    public static class UndoCtlRules
    {
        /// <summary>Bit 1: deshacer activo.</summary>
        public const int Enabled = 1;

        /// <summary>Bit 2: deshacer limitado a un paso.</summary>
        public const int OnlyOne = 2;

        /// <summary>Bit 8: hay un grupo UNDO BEGIN abierto.</summary>
        public const int GroupOpen = 8;

        /// <summary>
        /// true si el deshacer es completo (bit 1 activo y bit 2 apagado): única situación en la que "_.U" tras un grupo
        /// BE/E propio deshace exactamente ese grupo. Con "solo uno" U podría deshacer un paso del usuario.
        /// </summary>
        public static bool IsFullUndo(int undoctl) => (undoctl & Enabled) != 0 && (undoctl & OnlyOne) == 0;

        /// <summary>
        /// true si una LECTURA puede envolverse en un grupo neutro "_.UNDO _BE" → operación → "_.UNDO _E" → "_.U":
        /// deshacer completo y SIN un grupo BEGIN ya abierto (con el bit 8, nuestro "_E" cerraría el grupo del usuario y
        /// "_.U" lo desharía entero).
        /// </summary>
        public static bool CanRunNeutral(int undoctl) => IsFullUndo(undoctl) && (undoctl & GroupOpen) == 0;

        /// <summary>
        /// true si, al NO poder aplicar el grupo neutro, hay que advertir al cliente: solo cuando el deshacer está activo
        /// (con el bit 1 apagado no existe historial que la operación pueda ensuciar).
        /// </summary>
        public static bool NeutralSkipNeedsWarning(int undoctl) => !CanRunNeutral(undoctl) && (undoctl & Enabled) != 0;

        /// <summary>Motivo legible (para el log) por el que no se aplica el grupo neutro; null si se puede aplicar.</summary>
        public static string? NeutralSkipReason(int undoctl)
        {
            if ((undoctl & Enabled) == 0)
            {
                return "deshacer desactivado";
            }

            if ((undoctl & OnlyOne) != 0)
            {
                return "deshacer limitado a un paso";
            }

            if ((undoctl & GroupOpen) != 0)
            {
                return "hay un grupo UNDO BEGIN abierto";
            }

            return null;
        }

        /// <summary>Advertencia exacta (protocol.md §5B.7) cuando vista_png no pudo aislar el trazado en un grupo neutro.</summary>
        public static string NeutralSkippedWarning(int undoctl) =>
            "No se aisló el trazado en un grupo de deshacer neutro (UNDOCTL=" + undoctl.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "): el trazado podría haber dejado un paso en el historial de deshacer.";

        /// <summary>Advertencia exacta (protocol.md §5B.7) cuando el cierre del grupo neutro (_E o _U) falló.</summary>
        public static string NeutralCloseFailedWarning(string detalle) =>
            "No se pudo cerrar o deshacer el grupo de deshacer neutro del trazado (" + detalle
            + "): puede quedar un paso de deshacer vacío en el historial.";
    }
}
