namespace MCPAutoCAD.Plugin.Handlers
{
    /// <summary>
    /// Marca un handler de solo lectura que puede responder CON un comando activo (en vez de BUSY), en modo degradado: debe
    /// comprobar <c>ctx.CmdActive != 0 || ctx.Busy.CommandActive</c> y no tocar la base de datos del dibujo mientras tanto.
    /// Solo lo implementan estado (y por tanto documentos/instancias). Ningún otro handler debe implementarlo.
    /// </summary>
    internal interface IDegradableDuringCommand
    {
    }
}
