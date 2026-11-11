using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using MCPAutoCAD.GeometryCore;
using MCPAutoCAD.Plugin.Logging;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MCPAutoCAD.Plugin.Bridge
{
    /// <summary>
    /// Garantía de "una llamada de escritura = un paso de deshacer" (protocol.md §3.5).
    ///
    /// EL GATE 6 DE LA FASE 0 DECIDE <see cref="UseCommandContextUndo"/>:
    ///   - false (por defecto): se confía en que DocumentLock + UNA sola Transaction en contexto de aplicación
    ///     genere un único paso de deshacer.
    ///   - true: la escritura se ejecuta en contexto de comando (ExecuteInCommandContextAsync, invocado DESDE el
    ///     hilo principal) entre "_.UNDO" "_BE" y "_.UNDO" "_E".
    /// Si en el gate 6 (crear 3 líneas en una llamada → un CTRL+Z las quita todas) falla con false, basta cambiar
    /// la constante a true y recompilar. Ambas rutas se compilan siempre.
    /// </summary>
    internal static class UndoGroup
    {
        /// <summary>Mecanismo de agrupación de deshacer. Ver el comentario de la clase (gate 6).</summary>
        // Gate 6 (2026-10-25): con false, CTRL+Z no revirtió la escritura en contexto de aplicación → true.
        public static readonly bool UseCommandContextUndo = true;

        /// <summary>
        /// Ejecuta <paramref name="write"/> como un único paso de deshacer sobre <paramref name="doc"/>.
        /// Llamar SOLO desde el hilo principal. <paramref name="write"/> debe abrir una sola Transaction de escritura;
        /// excepción (fase M2): renombrar/borrar/copiar_layout validan en una Transaction que cierran antes de llamar a
        /// LayoutManager (que abre el Layout por su cuenta); el grupo _.UNDO _BE/_E sigue envolviendo todo en un CTRL+Z.
        /// Devuelve el resultado directamente (ruta de aplicación) o un Task&lt;object&gt; (ruta de comando).
        /// </summary>
        public static object Run(Document doc, Func<Document, object> write) => Run(doc, write, null);

        /// <summary>
        /// Como <see cref="Run(Document, Func{Document, object})"/>; <paramref name="noEjecutada"/> se llama en el hilo principal
        /// si el documento activo cambió dentro del contexto de comando y <paramref name="write"/> no llegó a ejecutarse (para
        /// liberar recursos del llamador, p. ej. la base lateral de importar_geometria).
        /// </summary>
        public static object Run(Document doc, Func<Document, object> write, Action? noEjecutada)
        {
            if (UseCommandContextUndo)
            {
                return RunInCommandContext(doc, write, noEjecutada);
            }

            using (doc.LockDocument())
            {
                return write(doc);
            }
        }

        private static void InvocarNoEjecutada(Action? noEjecutada)
        {
            try
            {
                noEjecutada?.Invoke();
            }
            catch (Exception ex)
            {
                PluginLog.Warn("UndoGroup: limpieza tras una escritura no ejecutada falló (" + ex.Message + ").");
            }
        }

        private static Task<object> RunInCommandContext(Document doc, Func<Document, object> write, Action? noEjecutada)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            DocumentCollection docs = AcApp.DocumentManager;
            if (docs.MdiActiveDocument != doc)
            {
                // ExecuteInCommandContextAsync opera sobre el documento activo.
                tcs.SetException(new InvalidOperationException("El documento activo cambió antes de escribir."));
                return tcs.Task;
            }

            Task launched = LaunchInCommandContext(
                docs,
                _ =>
                {
                    try
                    {
                        if (docs.MdiActiveDocument != doc)
                        {
                            // Fallo 36: el usuario cambió de documento entre la comprobación y la ejecución; no se escribe nada.
                            InvocarNoEjecutada(noEjecutada);

                            tcs.TrySetException(new ToolException(
                                ErrorCodes.WrongDoc,
                                "El documento activo cambió antes de escribir: no se escribió nada. Vuelva a activar el documento y repita."));
                            return Task.CompletedTask;
                        }

                        Editor ed = doc.Editor;
                        // Cadenas FIJAS (protocol.md §3.8): nunca se concatenan datos del cliente.
                        try
                        {
                            ed.Command("_.UNDO", "_BE");
                        }
                        catch
                        {
                            // Re-auditoría: la escritura no llega a ejecutarse; el llamador libera sus recursos.
                            InvocarNoEjecutada(noEjecutada);
                            throw;
                        }

                        bool written = false;
                        try
                        {
                            object result = write(doc);
                            written = true;
                            tcs.TrySetResult(result);
                        }
                        finally
                        {
                            ed.Command("_.UNDO", "_E");
                            if (!written)
                            {
                                UndoFailedGroup(ed);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }

                    return Task.CompletedTask;
                });

            launched.ContinueWith(
                t =>
                {
                    if (t.Exception != null)
                    {
                        tcs.TrySetException(t.Exception.InnerExceptions);
                    }
                    else
                    {
                        tcs.TrySetException(new InvalidOperationException("El contexto de comando terminó sin ejecutar la escritura."));
                    }
                },
                TaskContinuationOptions.ExecuteSynchronously);

            return tcs.Task;
        }

        /// <summary>
        /// Si la escritura lanzó, el grupo BE/E ya cerrado queda en la pila de deshacer (vacío, o con lo que no revierte
        /// el Abort de la Transaction, como Database.Insert). Gate de Fase 3: el usuario gastaba un CTRL+Z en él. Se
        /// deshace con "_.U" (cadena fija) para que una llamada fallida no deje rastro (R1). Solo con UNDOCTL completo
        /// (bit 1 activo y sin el bit 2 "solo uno"): con deshacer limitado, U podría deshacer un paso del usuario.
        /// </summary>
        private static void UndoFailedGroup(Editor ed)
        {
            try
            {
                int undoctl = ReadUndoCtl();
                if (!UndoCtlRules.IsFullUndo(undoctl))
                {
                    PluginLog.Warn("UndoGroup: escritura fallida; UNDOCTL=" + undoctl + ", no se deshace el grupo.");
                    return;
                }

                ed.Command("_.U");
                PluginLog.Info("UndoGroup: escritura fallida; grupo de deshacer revertido con U.");
            }
            catch (Exception ex)
            {
                PluginLog.Error("UndoGroup: no se pudo revertir el grupo de una escritura fallida", ex);
            }
        }

        /// <summary>Texto fijo de plugin.log que el gate busca para confirmar que se usó el grupo neutro (§5B.7).</summary>
        public const string NeutralLogTag = "grupo neutro BE/E/U";

        /// <summary>
        /// Grupo NEUTRO para LECTURAS que AutoCAD registra en el historial de deshacer aunque no escriban nada nuestro
        /// (protocol.md §3.9 / §5B.7; gate en vivo de Fase 3: un trazado de vista_png dejaba un "_u" suelto pese a
        /// Database.DisableUndoRecording). Desde el hilo principal lanza ExecuteInCommandContextAsync y allí ejecuta
        /// "_.UNDO _BE" → <paramref name="operation"/> → "_.UNDO _E" → "_.U" SIEMPRE (éxito o error): efecto neto CERO
        /// pasos de deshacer; U además devuelve al valor previo lo que AutoCAD registró dentro del grupo (sysvars como
        /// PLQUIET/STYLESHEET). Mismo mecanismo verificado en vivo para las escrituras fallidas (<see cref="UndoFailedGroup"/>).
        /// <para>
        /// Guarda UNDOCTL (<see cref="UndoCtlRules.CanRunNeutral"/>): solo con deshacer completo y sin un grupo BEGIN
        /// abierto. Si no, la operación se ejecuta igualmente en contexto de comando pero SIN BE/E/U (nunca se ejecuta U
        /// a ciegas) y, si el deshacer está activo, se añade <see cref="UndoCtlRules.NeutralSkippedWarning"/> a
        /// <paramref name="advertencias"/>. Si "_E" o "_U" fallan, <see cref="UndoCtlRules.NeutralCloseFailedWarning"/>.
        /// </para>
        /// <para>
        /// Diagnóstico: registra en plugin.log las sysvars de <paramref name="watchedSysVars"/> antes de BE y después de U
        /// (WARN si alguna difiere). El resultado/excepción de <paramref name="operation"/> se entrega DESPUÉS de U, así
        /// que las advertencias añadidas aquí llegan a la respuesta. Las cadenas de comando son FIJAS (§3.8).
        /// </para>
        /// </summary>
        public static Task<object> RunNeutral(
            Document doc, string what, Func<Document, object> operation, List<string> advertencias, string[] watchedSysVars)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            DocumentCollection docs = AcApp.DocumentManager;
            if (docs.MdiActiveDocument != doc)
            {
                tcs.SetException(new InvalidOperationException("El documento activo cambió antes de " + what + "."));
                return tcs.Task;
            }

            Task launched = LaunchInCommandContext(
                docs,
                _ =>
                {
                    try
                    {
                        tcs.TrySetResult(RunNeutralBody(doc, what, operation, advertencias, watchedSysVars));
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }

                    return Task.CompletedTask;
                });

            launched.ContinueWith(
                t =>
                {
                    if (t.Exception != null)
                    {
                        tcs.TrySetException(t.Exception.InnerExceptions);
                    }
                    else
                    {
                        tcs.TrySetException(new InvalidOperationException("El contexto de comando terminó sin ejecutar " + what + "."));
                    }
                },
                TaskContinuationOptions.ExecuteSynchronously);

            return tcs.Task;
        }

        /// <summary>Cuerpo de <see cref="RunNeutral"/>; corre DENTRO del contexto de comando.</summary>
        private static object RunNeutralBody(
            Document doc, string what, Func<Document, object> operation, List<string> advertencias, string[] watchedSysVars)
        {
            Editor ed = doc.Editor;
            int undoctl = ReadUndoCtl();
            string before = SnapshotSysVars(watchedSysVars);
            string undoctlText = undoctl.ToString(CultureInfo.InvariantCulture);

            string? skip = UndoCtlRules.NeutralSkipReason(undoctl);
            if (skip != null)
            {
                PluginLog.Warn(what + ": SIN " + NeutralLogTag + " (" + skip + ", UNDOCTL=" + undoctlText + "); no se ejecuta U.");
                if (UndoCtlRules.NeutralSkipNeedsWarning(undoctl))
                {
                    advertencias.Add(UndoCtlRules.NeutralSkippedWarning(undoctl));
                }

                return operation(doc);
            }

            PluginLog.Info(what + ": " + NeutralLogTag + " (UNDOCTL=" + undoctlText + "); antes: " + before + ".");

            // Cadenas FIJAS (protocol.md §3.8): nunca se concatenan datos del cliente.
            ed.Command("_.UNDO", "_BE");

            object? result = null;
            ExceptionDispatchInfo? failure = null;
            try
            {
                result = operation(doc);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }

            bool closed = false;
            bool undone = false;
            try
            {
                ed.Command("_.UNDO", "_E");
                closed = true;
                // SIEMPRE, con éxito o error: el grupo propio (vacío o con lo que registró AutoCAD) se deshace entero.
                ed.Command("_.U");
                undone = true;
            }
            catch (Exception ex)
            {
                // Si "_E" falló NO se ejecuta U: con el grupo aún abierto, U podría deshacer un paso del usuario.
                PluginLog.Error(what + ": no se pudo " + (closed ? "deshacer (U)" : "cerrar (_E)") + " el grupo neutro", ex);
                advertencias.Add(UndoCtlRules.NeutralCloseFailedWarning(ex.GetType().Name + ": " + ex.Message));
            }

            string after = SnapshotSysVars(watchedSysVars);
            string resumen = what + ": " + NeutralLogTag + " terminado (operacion=" + (failure == null ? "ok" : "error")
                + ", E=" + (closed ? "ok" : "fallo") + ", U=" + (undone ? "ok" : "no") + "); después: " + after + ".";
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                PluginLog.Info(resumen);
            }
            else
            {
                PluginLog.Warn(resumen + " Difiere de antes: " + before + ".");
            }

            failure?.Throw();
            return result!;
        }

        /// <summary>Lee UNDOCTL (entero).</summary>
        private static int ReadUndoCtl() => Convert.ToInt32(AcApp.GetSystemVariable("UNDOCTL"), CultureInfo.InvariantCulture);

        /// <summary>"NOMBRE=valor; ..." de las sysvars pedidas (solo lectura; una ilegible se marca, no lanza).</summary>
        private static string SnapshotSysVars(string[] names)
        {
            var sb = new StringBuilder();
            foreach (string name in names)
            {
                if (sb.Length > 0)
                {
                    sb.Append("; ");
                }

                sb.Append(name).Append('=');
                try
                {
                    object? value = AcApp.GetSystemVariable(name);
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null");
                }
                catch (Exception ex)
                {
                    sb.Append("(ilegible: ").Append(ex.GetType().Name).Append(')');
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// ExecuteInCommandContextAsync devuelve un awaitable propio (DocumentCollection.ExecutionResult), no un Task:
        /// se envuelve para poder observar fallos con ContinueWith.
        /// </summary>
        private static async Task LaunchInCommandContext(DocumentCollection docs, Func<object, Task> callback)
        {
            await docs.ExecuteInCommandContextAsync(callback, null);
        }
    }
}
