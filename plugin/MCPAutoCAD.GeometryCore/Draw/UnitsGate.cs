namespace MCPAutoCAD.GeometryCore.Draw
{
    /// <summary>
    /// Regla R2 de las escrituras (protocol.md §5D.0): con INSUNITS = 0 no se dibuja (UNITS_UNKNOWN) salvo que el
    /// cliente pase <c>aceptar_sin_unidades: true</c>; con otra unidad distinta de la del estándar LPS se escribe y se
    /// advierte (la advertencia LPS de §2.2 la añade el plugin). Puro.
    /// </summary>
    public static class UnitsGate
    {
        /// <summary>Advertencia añadida cuando se escribe con INSUNITS = 0 por aceptar_sin_unidades.</summary>
        public const string AcceptedWithoutUnits =
            "aceptar_sin_unidades: true: se dibujó en un dibujo sin unidades declaradas (INSUNITS=0); las coordenadas se tomaron tal cual.";

        /// <summary>true si la escritura debe rechazarse con UNITS_UNKNOWN.</summary>
        public static bool MustReject(int insunits, bool aceptarSinUnidades) => insunits == 0 && !aceptarSinUnidades;

        /// <summary>true si procede la advertencia <see cref="AcceptedWithoutUnits"/>.</summary>
        public static bool AcceptedUnknown(int insunits, bool aceptarSinUnidades) => insunits == 0 && aceptarSinUnidades;
    }
}
