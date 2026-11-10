using System.Text.Json;

namespace MCPAutoCAD.Plugin.Handlers.Draw
{
    /// <summary>
    /// borrar (protocol.md §5D.8). Hilo principal; requiere documento; ESCRITURA (un CTRL+Z las recupera). Borra las
    /// entidades por handle (UpgradeOpen + Erase) tras las comprobaciones de <see cref="HandleGuard"/> (pasos 1–5).
    /// Sin R4 (no hay geometría nueva).
    /// </summary>
    internal sealed class BorrarHandler : IToolHandler
    {
        public string Name => "borrar";

        public string Description =>
            "BORRA entidades existentes (por handle) en un solo paso de deshacer (un CTRL+Z las recupera); todo o nada. " +
            "Si alguna entidad no está en una capa MCP-* exige confirmar: true; rechaza atributos (borre el INSERT), entidades de bloques, " +
            "viewports de papel y capas bloqueadas o de xref.";

        public bool RequiresDocument => true;

        public bool IsWrite => true;

        public bool RunsOnPipeThread => false;

        public JsonElement InputSchema { get; } = ModifySupport.BuildSchema("");

        public JsonElement? OutputSchema { get; } = ModifySupport.BuildOutputSchema("");

        public void ValidateParams(JsonElement parameters) => Parse(parameters);

        private static ModifySupport.Common Parse(JsonElement p)
        {
            ParamReader.RequireObject(p, ModifySupport.CommonParams);
            return ModifySupport.ReadCommon(p);
        }

        public object Execute(JsonElement parameters, ExecutionContext ctx)
        {
            ModifySupport.Common c = Parse(parameters);
            return ModifySupport.Run(ctx, c, ModifySupport.Operation.Erase, null, null, _ => { });
        }
    }
}
