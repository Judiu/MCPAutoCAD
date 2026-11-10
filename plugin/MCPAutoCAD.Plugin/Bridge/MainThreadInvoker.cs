using System;
using System.Threading;
using System.Windows.Forms;

namespace MCPAutoCAD.Plugin.Bridge
{
    /// <summary>
    /// Encola acciones al hilo principal de AutoCAD (PLAN.md §4.1). Se construye en Initialize(), que corre en el
    /// hilo principal. Usa el SynchronizationContext de UI capturado; si no hay uno de UI (null, o el contexto base
    /// que despacharía al ThreadPool), crea un Control oculto y usa BeginInvoke.
    /// </summary>
    internal sealed class MainThreadInvoker : IDisposable
    {
        private readonly SynchronizationContext? _context;
        private readonly Control? _control;

        private MainThreadInvoker(SynchronizationContext? context, Control? control)
        {
            _context = context;
            _control = control;
        }

        /// <summary>Descripción del mecanismo elegido, para el log y MCPESTADO.</summary>
        public string Mechanism => _context != null ? "SynchronizationContext(" + _context.GetType().Name + ")" : "Control.BeginInvoke";

        /// <summary>Crea el invocador. DEBE llamarse en el hilo principal.</summary>
        public static MainThreadInvoker CreateOnMainThread()
        {
            SynchronizationContext? current = SynchronizationContext.Current;
            if (current != null && IsUiContext(current))
            {
                return new MainThreadInvoker(current, null);
            }

            var control = new Control();
            // Fuerza la creación del handle en ESTE hilo (el principal): BeginInvoke despachará aquí.
            IntPtr handle = control.Handle;
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("No se pudo crear el control oculto para despachar al hilo principal.");
            }

            return new MainThreadInvoker(null, control);
        }

        /// <summary>
        /// El SynchronizationContext base (y cualquiera desconocido) despacha al ThreadPool: tocar AutoCAD desde ahí
        /// es fatal. Solo se aceptan los contextos de WinForms y WPF, que despachan al hilo que los creó.
        /// </summary>
        private static bool IsUiContext(SynchronizationContext ctx)
        {
            string name = ctx.GetType().FullName ?? "";
            return name == "System.Windows.Forms.WindowsFormsSynchronizationContext"
                || name == "System.Windows.Threading.DispatcherSynchronizationContext";
        }

        /// <summary>Encola la acción. Puede llamarse desde cualquier hilo. La acción nunca debe lanzar.</summary>
        public void Post(Action action)
        {
            if (_context != null)
            {
                _context.Post(_ => action(), null);
                return;
            }

            Control control = _control ?? throw new ObjectDisposedException(nameof(MainThreadInvoker));
            control.BeginInvoke(action);
        }

        public void Dispose()
        {
            // El Control se destruye en el hilo principal (Terminate); fuera de él solo se descarta la referencia.
            _control?.Dispose();
        }
    }
}
