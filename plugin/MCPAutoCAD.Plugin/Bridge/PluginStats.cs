using System;
using System.Globalization;
using System.Threading;

namespace MCPAutoCAD.Plugin.Bridge
{
    /// <summary>Contadores para el comando MCPESTADO. Thread-safe.</summary>
    internal sealed class PluginStats
    {
        private int _activeConnections;
        private long _requestsServed;
        private volatile string _lastRequest = "(ninguna)";

        public int ActiveConnections => Volatile.Read(ref _activeConnections);

        public long RequestsServed => Interlocked.Read(ref _requestsServed);

        public string LastRequest => _lastRequest;

        public void ConnectionOpened() => Interlocked.Increment(ref _activeConnections);

        public void ConnectionClosed() => Interlocked.Decrement(ref _activeConnections);

        public void RecordRequest(string method, string id, string outcome, long elapsedMs)
        {
            Interlocked.Increment(ref _requestsServed);
            _lastRequest = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {method} (id={id}) → {outcome} en {elapsedMs} ms");
        }
    }
}
