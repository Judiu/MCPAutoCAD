using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using MCPAutoCAD.GeometryCore.Audit;

namespace MCPAutoCAD.Plugin.Handlers.Audit
{
    /// <summary>
    /// Una regla de auditoría ejecutable por <c>auditar_dibujo</c> (protocol.md §5C.8). Se descubre por reflexión
    /// (clase concreta con constructor público sin parámetros), igual que <see cref="ToolRegistry"/> descubre los
    /// handlers. Cada handler de lectura de §5C.1–§5C.7 implementa su regla en una clase aparte que cumple esta
    /// interfaz y la reutiliza; las reglas 7 y 8 (xrefs, unidades) solo existen como IAuditCheck.
    /// Solo hilo principal; solo lectura (la transacción la abre quien llama).
    /// </summary>
    internal interface IAuditCheck
    {
        /// <summary>Nombre de la regla (campo <c>regla</c> de §5C.0 y clave de <c>resumen.por_regla</c>).</summary>
        string Regla { get; }

        /// <summary>Posición en el orden fijo de §5C.8 (1 = auditar_capas … 8 = unidades).</summary>
        int Orden { get; }

        /// <summary>
        /// Ejecuta la regla. Debe comprobar <see cref="AuditContext.TimeExceeded"/> en sus bucles y, si se agota,
        /// devolver lo calculado con <see cref="AuditCheckResult.Completo"/> = false. No debe lanzar por datos del
        /// dibujo (lo que no se pueda leer se omite); una excepción se registra y la regla cuenta como fallida.
        /// </summary>
        AuditCheckResult Run(AuditContext ctx);
    }

    /// <summary>Contexto compartido por las reglas dentro de una llamada a auditar_dibujo.</summary>
    internal sealed class AuditContext
    {
        public AuditContext(Database db, Transaction tr, SpaceResolver.ResolvedSpace espacio, DateTime deadlineUtc)
        {
            Db = db ?? throw new ArgumentNullException(nameof(db));
            Tr = tr ?? throw new ArgumentNullException(nameof(tr));
            Espacio = espacio ?? throw new ArgumentNullException(nameof(espacio));
            DeadlineUtc = deadlineUtc;
        }

        public Database Db { get; }

        /// <summary>Transacción de solo lectura (StartOpenCloseTransaction) abierta por quien llama.</summary>
        public Transaction Tr { get; }

        /// <summary>Espacio pedido (parámetro <c>espacio</c> o el actual): ámbito de las reglas 4, 5 y 6.</summary>
        public SpaceResolver.ResolvedSpace Espacio { get; }

        /// <summary>Plazo absoluto (UTC) para toda la llamada (§3.10: inicio + 8 000 ms).</summary>
        public DateTime DeadlineUtc { get; }

        /// <summary>true si ya venció el plazo. Barato: comprobar cada N entidades en los bucles.</summary>
        public bool TimeExceeded => DateTime.UtcNow > DeadlineUtc;
    }

    /// <summary>Resultado de una regla.</summary>
    internal sealed class AuditCheckResult
    {
        /// <summary>Hallazgos (sin id: auditar_dibujo los asigna al final con <see cref="Finding.AssignIds"/>).</summary>
        public List<Finding> Hallazgos { get; } = new List<Finding>();

        /// <summary>Conteos propios de la regla, informativos (se vuelcan en el log, no en la respuesta de §5C.8).</summary>
        public Dictionary<string, object?> Resumen { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

        /// <summary>false si la regla se interrumpió por el plazo (§3.10).</summary>
        public bool Completo { get; set; } = true;

        /// <summary>Advertencias propias de la regla (se añaden a <c>advertencias</c> tras la LPS).</summary>
        public List<string> Advertencias { get; } = new List<string>();
    }
}
