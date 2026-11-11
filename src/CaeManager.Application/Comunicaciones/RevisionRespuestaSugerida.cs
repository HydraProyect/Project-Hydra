using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Regla de revisión de la respuesta sugerida, la de la maqueta del 2026-11-01 (Main.dc.html, <c>UMBRAL_REVISION</c>): una
/// sugerencia sigue «sin revisar» mientras el texto que se envía no se aparte de ella más de <see cref="UmbralEdicion"/>
/// caracteres, salvo que una persona pulse «Lo he revisado». La aplican con la misma función la interfaz (que no ofrece
/// Enviar) y el servidor (<see cref="BloqueoRespuestaSugerida"/>, que es la garantía), para que copiar la propuesta a mano
/// sin pasar por «Insertar» no eluda el bloqueo.
///
/// <para>
/// Se compara el texto, no el marcado: los dos lados pasan por <see cref="TextoComparable"/>, la normalización de
/// <see cref="RespuestaSugerida.NormalizarTexto"/> (sin etiquetas, entidades decodificadas, espacios colapsados), que es
/// también la del hash. Así una sugerencia de correo guardada como HTML y el mismo texto escrito en el redactor como texto
/// plano miden 0, y cambiar etiquetas no cuenta como edición.
/// </para>
/// </summary>
public static class RevisionRespuestaSugerida
{
    /// <summary>Distancia de edición por encima de la cual el texto cuenta como revisado: «una letra suelta no basta».</summary>
    public const int UmbralEdicion = 15;

    /// <summary>El texto que se compara: el de <see cref="RespuestaSugerida.NormalizarTexto"/>, igual en interfaz y servidor.</summary>
    public static string TextoComparable(string? texto) =>
        string.IsNullOrEmpty(texto) ? string.Empty : RespuestaSugerida.NormalizarTexto(texto);

    /// <summary>
    /// El texto actual se ha apartado de la sugerencia más que <see cref="UmbralEdicion"/>, comparados como texto. Es la
    /// negación exacta de <see cref="EsCasiLaMisma"/> sobre los textos normalizados: la interfaz y el servidor aplican la
    /// misma función y el mismo umbral.
    /// </summary>
    public static bool EditadaLoSuficiente(string? textoActual, string? textoSugerido) =>
        !EsCasiLaMisma(TextoComparable(textoActual), TextoComparable(textoSugerido));

    /// <summary>
    /// La regla única, sobre textos ya normalizados (<see cref="TextoComparable"/>): el texto es la sugerencia con a lo sumo
    /// <see cref="UmbralEdicion"/> caracteres cambiados, y por tanto sigue sin revisar. No hay excepción por longitud
    /// (revisión Codex r2 de la #2163, ALTO): con un margen menor para las sugerencias cortas, «Ok, gracias» con una firma de
    /// siete letras salía sin revisión por el servidor mientras la interfaz la seguía dando por no revisada.
    /// </summary>
    public static bool EsCasiLaMisma(string cuerpoNormalizado, string textoNormalizado) =>
        DistanciaEdicion(cuerpoNormalizado, textoNormalizado) <= UmbralEdicion;

    /// <summary>
    /// Distancia de Levenshtein acotada: devuelve como máximo <c>UmbralEdicion + 1</c>. Solo calcula la banda de
    /// <see cref="UmbralEdicion"/> celdas a cada lado de la diagonal, así que el coste es lineal en la longitud y un texto
    /// de cientos de KiB no bloquea el pintado del redactor ni el envío.
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
