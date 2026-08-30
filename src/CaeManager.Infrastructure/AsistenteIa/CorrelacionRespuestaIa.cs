namespace CaeManager.Infrastructure.AsistenteIa;

/// <summary>
/// Extrae de una respuesta HTTP fallida de un proveedor de IA lo único que se
/// puede registrar sin riesgo: su identificador de correlación.
///
/// Antes, cada uno de los nueve puntos de error de este directorio hacía
/// <c>ReadAsStringAsync()</c> y volcaba el cuerpo entero al log. El cuerpo de
/// error de un proveedor de IA no es un código de estado: puede incluir
/// fragmentos de la solicitud que lo provocó, y esa solicitud lleva el texto
/// del documento — nombres, DNI, datos de salud del apto médico, listados de
/// plantilla. Eso convertía cada fallo del proveedor en una copia de datos
/// personales replicada a los logs, a Sentry y a los backups de ambos, cada
/// uno con su propia retención y su propia lista de personas con acceso, y
/// ninguna de las dos cosas decidida por el tenant dueño de los datos.
///
/// De paso cerraba la puerta al log forging: el cuerpo lo escribe un tercero,
/// y un tercero que controle su contenido controla lo que aparece en tus
/// registros.
///
/// Lo que queda es suficiente para diagnosticar de verdad. Con el código de
/// estado se distingue credencial (401/403) de cuota (429) de caída (5xx), y
/// con el identificador de correlación el proveedor puede buscar la petición
/// concreta en SU lado, que es donde el cuerpo sí puede consultarse sin
/// copiarlo a ninguna parte.
/// </summary>
internal static class CorrelacionRespuestaIa
{
    /// <summary>
    /// Cabeceras de correlación de los proveedores en uso: <c>request-id</c>
    /// (Anthropic y Mistral), <c>x-request-id</c> (variante habitual) y
    /// <c>x-goog-request-id</c> (Google). Se prueban en orden y se devuelve la
    /// primera presente — no hay una sola cabecera estándar para esto.
    /// </summary>
    private static readonly string[] CabecerasDeCorrelacion =
        ["request-id", "x-request-id", "x-goog-request-id", "cf-ray"];

    /// <summary>
    /// Texto listo para interpolar en un log. Nunca devuelve contenido de la
    /// respuesta, solo su identificador de correlación o la constancia de que
    /// el proveedor no envió ninguno.
    /// </summary>
    public static string Describir(HttpResponseMessage respuesta)
    {
        foreach (var cabecera in CabecerasDeCorrelacion)
        {
            if (respuesta.Headers.TryGetValues(cabecera, out var valores) &&
                valores.FirstOrDefault() is { Length: > 0 } valor)
            {
                return $"{cabecera}={valor}";
            }
        }

        return "sin identificador de correlación";
    }
}
