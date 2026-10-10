namespace CaeManager.E2ETests;

/// <summary>
/// El bucle con que <see cref="WebAppFixture"/> espera a que la aplicación
/// arrancada responda 200 en <c>/salud</c>, separado de la fixture para poder
/// probarlo sin proceso, sin red y sin esperar de verdad: la petición, el
/// estado del proceso, el reloj y la pausa entre intentos llegan como
/// delegados (ver <c>SondeoDeArranqueTests</c>).
///
/// <para>
/// Tres respuestas significan «todavía no» y se reintentan hasta agotar el
/// plazo: la conexión rechazada (<see cref="HttpRequestException"/>), un
/// código HTTP que no es de éxito y la petición que agota su propio tiempo
/// (<see cref="OperationCanceledException"/>). Esta última no se capturaba
/// hasta el 2026-10-10: con el servidor aceptando la conexión pero tardando
/// más que el tiempo de la petición en contestar (arranque en frío con la
/// máquina cargada), <c>HttpClient</c> lanza <see cref="TaskCanceledException"/>,
/// la excepción salía de <c>InitializeAsync</c> y todas las clases de la
/// colección caían en 1 ms sin ejecutarse.
/// </para>
/// </summary>
internal static class SondeoDeArranque
{
    /// <param name="pedirSalud">Una petición a <c>/salud</c>, con su propio tiempo máximo.</param>
    /// <param name="codigoDeSalidaSiElProcesoTermino">El código de salida del proceso si ya terminó; <c>null</c> si sigue vivo.</param>
    /// <param name="plazo">Tiempo total para obtener el primer 200.</param>
    /// <param name="ahora">El reloj con que se mide el plazo.</param>
    /// <param name="pausa">La espera entre un intento y el siguiente.</param>
    /// <param name="url">La dirección sondeada, solo para los mensajes.</param>
    internal static async Task EsperarAsync(
        Func<Task<HttpResponseMessage>> pedirSalud,
        Func<int?> codigoDeSalidaSiElProcesoTermino,
        TimeSpan plazo,
        Func<DateTime> ahora,
        Func<Task> pausa,
        string url)
    {
        var limite = ahora() + plazo;
        var intentos = 0;
        var ultimaCausa = "no llegó a hacerse ninguna petición";
        Exception? ultimaExcepcion = null;

        while (ahora() < limite)
        {
            if (codigoDeSalidaSiElProcesoTermino() is { } codigoDeSalida)
                throw new InvalidOperationException(
                    $"El proceso de CaeManager.Web terminó inesperadamente (código {codigoDeSalida}) mientras esperábamos que arrancara.");

            intentos++;

            try
            {
                using var respuesta = await pedirSalud();
                if (respuesta.IsSuccessStatusCode)
                    return;

                // Responde, pero todavía no está lista (migraciones o siembra en curso) — se reintenta.
                ultimaCausa = $"código HTTP {(int)respuesta.StatusCode}";
                ultimaExcepcion = null;
            }
            catch (HttpRequestException ex)
            {
                // Todavía no acepta conexiones — se reintenta.
                ultimaCausa = $"conexión rechazada o fallida ({ex.Message})";
                ultimaExcepcion = ex;
            }
            catch (OperationCanceledException ex)
            {
                // Aceptó la conexión y no contestó a tiempo — se reintenta. En este camino no hay ningún token de
                // cancelación externo (ni la fixture ni xUnit pasan uno), así que toda cancelación es el tiempo máximo
                // de la propia petición. Si algún día se añade un token, esta captura tiene que distinguirlo: tal como
                // está, se tragaría la cancelación pedida y seguiría sondeando hasta el plazo.
                ultimaCausa = $"timeout de la petición ({ex.Message})";
                ultimaExcepcion = ex;
            }

            await pausa();
        }

        throw new TimeoutException(
            $"CaeManager.Web no respondió 200 en {url} en {plazo.TotalSeconds:0} s ({intentos} intentos). Última causa: {ultimaCausa}.",
            ultimaExcepcion);
    }
}
