namespace CaeManager.Web.Features.Comunicaciones;

/// <summary>
/// Regla de revisión de la respuesta sugerida, la de la maqueta del 2026-11-01 (Main.dc.html, <c>UMBRAL_REVISION</c>): una
/// sugerencia insertada en el redactor sigue «sin revisar» y bloquea Enviar y Ctrl+Intro hasta que el Gestor CAE pulsa
/// «Lo he revisado» o cambia más de <see cref="UmbralEdicion"/> caracteres del texto insertado. La garantía de que no se
/// envía una sugerencia sin revisar es la del servidor (<c>BloqueoRespuestaSugerida</c>); esto solo evita ofrecer el botón.
/// </summary>
public static class RevisionRespuestaSugerida
{
    /// <summary>Distancia de edición por encima de la cual el texto cuenta como revisado: «una letra suelta no basta».</summary>
    public const int UmbralEdicion = 15;

    /// <summary>El redactor se ha apartado del texto insertado más que <see cref="UmbralEdicion"/>.</summary>
    public static bool EditadaLoSuficiente(string? textoActual, string? textoInsertado) =>
        DistanciaEdicion(textoActual, textoInsertado) > UmbralEdicion;

    /// <summary>
    /// Distancia de Levenshtein acotada: devuelve como máximo <c>UmbralEdicion + 1</c>. Solo calcula la banda de
    /// <see cref="UmbralEdicion"/> celdas a cada lado de la diagonal, así que el coste es lineal en la longitud y un texto
    /// de cientos de KiB no bloquea el pintado del redactor.
    /// </summary>
    public static int DistanciaEdicion(string? origen, string? destino)
    {
        var a = origen ?? string.Empty;
        var b = destino ?? string.Empty;
        if (string.Equals(a, b, StringComparison.Ordinal)) return 0;

        const int tope = UmbralEdicion + 1;
        if (Math.Abs(a.Length - b.Length) > UmbralEdicion) return tope;

        var previa = new int[b.Length + 1];
        var actual = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previa[j] = Math.Min(j, tope);

        for (var i = 1; i <= a.Length; i++)
        {
            var desde = Math.Max(1, i - UmbralEdicion);
            var hasta = Math.Min(b.Length, i + UmbralEdicion);

            // Celda a la izquierda de la banda: el borde de la tabla (columna 0) o fuera de la banda, que ya vale el tope.
            actual[desde - 1] = desde == 1 ? Math.Min(i, tope) : tope;

            for (var j = desde; j <= hasta; j++)
            {
                var coste = a[i - 1] == b[j - 1] ? 0 : 1;
                var valor = Math.Min(Math.Min(previa[j - 1] + coste, previa[j] + 1), actual[j - 1] + 1);
                actual[j] = Math.Min(valor, tope);
            }

            // Celda a la derecha de la banda: la fila siguiente la lee como «fuera de la banda».
            if (hasta + 1 <= b.Length) actual[hasta + 1] = tope;

            (previa, actual) = (actual, previa);
        }

        return Math.Min(previa[b.Length], tope);
    }
}
