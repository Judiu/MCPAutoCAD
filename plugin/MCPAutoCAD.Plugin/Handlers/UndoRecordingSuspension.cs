using System;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.Plugin.Logging;

namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// Suspende la grabación de deshacer de una base de datos mientras existe (protocol.md §3.9 / §5B.7, F7 de 0.3.2).
    /// API verificada por reflexión en acdbmgd.dll (AutoCAD 2026):
    /// <c>public void Database.DisableUndoRecording(bool disable)</c> y <c>public bool Database.UndoRecording { get; }</c>
    /// (true = la grabación está activa). Se lee el estado previo y, al terminar, se restaura EXACTAMENTE ese estado:
    /// si la grabación ya estaba desactivada (por otro código), no se toca ni al entrar ni al salir.
    /// No usa UNDO ni ningún comando. Uso: <c>using (new UndoRecordingSuspension(db, "motivo")) { ... }</c>; llamar con el
    /// documento bloqueado (DocumentLock). Gate en vivo de Fase 3: NO basta por sí sola para que un trazado no deje paso
    /// en la pila de deshacer de comandos; vista_png la usa dentro de <c>UndoGroup.RunNeutral</c> (contexto de comando).
    /// </summary>
    internal sealed class UndoRecordingSuspension : IDisposable
    {
        private readonly Database _db;
        private readonly string _what;
        private readonly bool _disabledHere;
        private bool _disposed;

        public UndoRecordingSuspension(Database db, string what)
        {
            _db = db;
            _what = what;
            bool wasRecording = db.UndoRecording;
            if (wasRecording)
            {
                db.DisableUndoRecording(true);
                _disabledHere = true;
            }

            PluginLog.Info(what + ": grabación de deshacer " + (wasRecording ? "suspendida" : "ya estaba desactivada (no se toca)") + ".");
        }

        /// <summary>
        /// true si al terminar <c>DisableUndoRecording(false)</c> lanzó (protocol.md §5D.9): la grabación de deshacer
        /// quedó desactivada y el llamador debe advertirlo al usuario. Solo es válido después de <see cref="Dispose"/>.
        /// </summary>
        public bool ReactivationFailed { get; private set; }

        /// <summary>Detalle del fallo de reactivación ('&lt;Tipo&gt;: &lt;mensaje&gt;') o null si no falló.</summary>
        public string? FailureDetail { get; private set; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_disabledHere)
            {
                return;
            }

            try
            {
                _db.DisableUndoRecording(false);
            }
            catch (Exception ex)
            {
                ReactivationFailed = true;
                FailureDetail = ex.GetType().Name + ": " + ex.Message;
                PluginLog.Warn(_what + ": no se pudo reactivar la grabación de deshacer (" + ex.GetType().Name + ": " + ex.Message + ").");
            }
        }
    }
}
