using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using MCPAutoCAD.Plugin.Logging;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MCPAutoCAD.Plugin.Bridge
{
    /// <summary>
    /// Estado "comando en curso" mantenido por eventos de AutoCAD (PLAN.md §4.2). Los eventos y la actualización
    /// ocurren en el hilo principal; los campos son volatile para que el hilo del pipe pueda leerlos.
    /// Cada documento tiene su pila de comandos (los transparentes, p. ej. 'ZOOM dentro de LINE, se apilan);
    /// el estado expuesto es el del documento activo y se refresca en DocumentActivated.
    /// </summary>
    internal sealed class BusyState
    {
        private readonly Dictionary<Document, Stack<(string Name, int Serial)>> _stacks = new Dictionary<Document, Stack<(string Name, int Serial)>>();

        /// <summary>Fallo 26: se incrementa en cada DocumentCreated/DocumentActivated (solo hilo principal).</summary>
        private int _docSerial;
        private volatile bool _commandActive;
        private volatile string? _currentCommand;
        private bool _subscribed;

        /// <summary>true si el documento activo tiene un comando en curso según los eventos.</summary>
        public bool CommandActive => _commandActive;

        /// <summary>Nombre global del comando en curso en el documento activo, o null.</summary>
        public string? CurrentCommand => _currentCommand;

        /// <summary>Suscribe los eventos. Llamar solo desde el hilo principal.</summary>
        public void Subscribe()
        {
            if (_subscribed)
            {
                return;
            }

            DocumentCollection docs = AcApp.DocumentManager;
            docs.DocumentCreated += OnDocumentCreated;
            docs.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            docs.DocumentActivated += OnDocumentActivated;
            foreach (Document doc in docs)
            {
                Attach(doc);
            }

            _subscribed = true;
            Refresh();
        }

        /// <summary>Desuscribe todos los eventos. Llamar solo desde el hilo principal.</summary>
        public void Unsubscribe()
        {
            if (!_subscribed)
            {
                return;
            }

            _subscribed = false;
            try
            {
                DocumentCollection docs = AcApp.DocumentManager;
                docs.DocumentCreated -= OnDocumentCreated;
                docs.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
                docs.DocumentActivated -= OnDocumentActivated;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Error al desuscribir eventos del DocumentManager", ex);
            }

            foreach (Document doc in new List<Document>(_stacks.Keys))
            {
                Detach(doc);
            }

            _stacks.Clear();
            _commandActive = false;
            _currentCommand = null;
        }

        private void Attach(Document doc)
        {
            if (_stacks.ContainsKey(doc))
            {
                return;
            }

            _stacks[doc] = new Stack<(string Name, int Serial)>();
            doc.CommandWillStart += OnCommandWillStart;
            doc.CommandEnded += OnCommandFinished;
            doc.CommandCancelled += OnCommandFinished;
            doc.CommandFailed += OnCommandFinished;
        }

        private void Detach(Document doc)
        {
            try
            {
                doc.CommandWillStart -= OnCommandWillStart;
                doc.CommandEnded -= OnCommandFinished;
                doc.CommandCancelled -= OnCommandFinished;
                doc.CommandFailed -= OnCommandFinished;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Error al desuscribir eventos de un documento", ex);
            }

            _stacks.Remove(doc);
        }

        private void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e)
        {
            try
            {
                _docSerial++;
                if (e.Document != null)
                {
                    Attach(e.Document);
                }

                Refresh();
            }
            catch (Exception ex)
            {
                PluginLog.Error("Error en DocumentCreated", ex);
            }
        }

        private void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
        {
            try
            {
                if (e.Document != null)
                {
                    Detach(e.Document);
                }

                Refresh();
            }
            catch (Exception ex)
            {
                PluginLog.Error("Error en DocumentToBeDestroyed", ex);
            }
        }

        private void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
        {
            try
            {
                _docSerial++;
                Refresh();
            }
            catch (Exception ex)
            {
                PluginLog.Error("Error en DocumentActivated", ex);
            }
        }

        private void OnCommandWillStart(object? sender, CommandEventArgs e)
        {
            try
            {
                if (sender is Document doc && _stacks.TryGetValue(doc, out Stack<(string Name, int Serial)>? stack))
                {
                    stack.Push((e.GlobalCommandName ?? "", _docSerial));
                }

                Refresh();
            }
            catch (Exception ex)
            {
                PluginLog.Error("Error en CommandWillStart", ex);
            }
        }

        private void OnCommandFinished(object? sender, CommandEventArgs e)
        {
            try
            {
                if (sender is Document doc && _stacks.TryGetValue(doc, out Stack<(string Name, int Serial)>? stack) && stack.Count > 0)
                {
                    stack.Pop();
                }

                Refresh();
            }
            catch (Exception ex)
            {
                PluginLog.Error("Error en fin de comando", ex);
            }
        }

        /// <summary>
        /// Fallo 26 (0.10.0, en vivo): OPEN lanzado desde Dibujo2 abre otro dibujo y su fin (CommandEnded/Cancelled) no llega
        /// a la pila de Dibujo2, que se quedaba con "OPEN" para siempre: todo devolvía BUSY y cancelar_comando no podía
        /// cancelar nada porque AutoCAD no tenía comando. Se descarta la pila del documento activo solo si (1) CMDACTIVE = 0
        /// (AutoCAD no tiene comando en el documento activo) y (2) TODAS sus entradas se apilaron antes de un cambio de
        /// documento (DocumentCreated/Activated posterior). Un comando real (diálogo de OPEN abierto, LINE en curso tras
        /// cambiar de pestaña y volver) mantiene CMDACTIVE ≠ 0 y no se toca. Solo hilo principal. Devuelve lo descartado o null.
        /// </summary>
        public string? ReconcileStale(int cmdActive)
        {
            if (cmdActive != 0 || !_commandActive)
            {
                return null;
            }

            Document? active = AcApp.DocumentManager.MdiActiveDocument;
            if (active == null || !_stacks.TryGetValue(active, out Stack<(string Name, int Serial)>? stack) || stack.Count == 0)
            {
                return null;
            }

            var names = new List<string>();
            foreach ((string name, int serial) in stack)
            {
                if (serial >= _docSerial)
                {
                    return null;
                }

                names.Add(name.Length == 0 ? "(sin nombre)" : name);
            }

            string stale = string.Join(" > ", names);
            stack.Clear();
            Refresh();
            PluginLog.Warn("BusyState: pila residual descartada en '" + (active.Name ?? "") + "' (" + stale
                + ") con CMDACTIVE = 0 tras un cambio de documento (el fin del comando no llegó a este documento).");
            return stale;
        }

        /// <summary>Recalcula el estado expuesto a partir del documento activo.</summary>
        private void Refresh()
        {
            Document? active = AcApp.DocumentManager.MdiActiveDocument;
            if (active != null && _stacks.TryGetValue(active, out Stack<(string Name, int Serial)>? stack) && stack.Count > 0)
            {
                _currentCommand = stack.Peek().Name;
                _commandActive = true;
            }
            else
            {
                _commandActive = false;
                _currentCommand = null;
            }
        }
    }
}
