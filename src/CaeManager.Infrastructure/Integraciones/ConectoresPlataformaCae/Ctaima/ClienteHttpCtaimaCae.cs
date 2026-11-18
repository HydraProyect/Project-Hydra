using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CaeManager.Application.Integraciones.ConectoresPlataformaCae;
using CaeManager.Domain.Integraciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Ctaima;

/// <summary>
/// Cliente HTTP tipado de la API oficial de CTAIMACAE.net (servidor <c>ctaima.azure-api.net</c>).
/// Autentica con dos cabeceras: <see cref="CabeceraClave"/>, que es la credencial de la conexión, y
/// <see cref="CabeceraTenant"/>, que identifica al contratista en CTAIMACAE.net. La clave nunca va en la
/// URL (la API admite también <c>subscription-key</c> por query; aquí no se usa) y nunca se escribe en
/// un log ni en una excepción (OWASP ASVS 4.0.3 V7.1.1).
///
/// <para>
/// Ninguna respuesta del fabricante sale de esta clase como texto: un fallo es un
/// <see cref="MotivoUsoConector"/> y el log solo lleva el código HTTP. Las respuestas 400 y 404 de la
/// API traen textos con CIF, NIF y nombres de sociedades; no se leen.
/// </para>
///
/// <para>
/// No reintenta. El alta no es idempotente del otro lado (la API no admite clave de idempotencia), y
/// un reintento tras un timeout o un 5xx puede crear el Trabajador dos veces. Por eso el registro no
/// lleva el pipeline de resiliencia estándar.
/// </para>
/// </summary>
public sealed class ClienteHttpCtaimaCae(
    HttpClient http, IOptions<CtaimaCaeOptions> opciones, ILogger<ClienteHttpCtaimaCae> logger)
{
    public const string CabeceraClave = "Ocp-Apim-Subscription-Key";
    public const string CabeceraTenant = "tenantId";

    /// <summary>Tope del cuerpo de respuesta que se acepta en memoria.</summary>
    public const long TamanoMaximoRespuesta = 1024 * 1024;

    public const string RutaTrabajadoresInternos = "resource-management/api/internal-employees";

    private const int LongitudMaximaCabecera = 512;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Un 3xx no se sigue: la cabecera de la clave no es <c>Authorization</c>, y .NET la reenviaría al
    /// destino de la redirección.
    /// </summary>
    public static SocketsHttpHandler CrearManejadorSinRedirecciones() => new()
    {
        AllowAutoRedirect = false,
    };

    public Task<ResultadoConector<TrabajadorInternoCtaimaRespuesta>> CrearTrabajadorInternoAsync(
        CredencialApiConector credencial, string tenantIdCtaima, TrabajadorInternoCtaimaPeticion peticion,
        CancellationToken cancellationToken) =>
        EnviarAsync<TrabajadorInternoCtaimaRespuesta>(
            HttpMethod.Post, RutaTrabajadoresInternos, JsonContent.Create(peticion, options: Json),
            credencial, tenantIdCtaima, OperacionConectorPlataformaCae.DarDeAltaTrabajador, cancellationToken);

    /// <summary>
    /// La traducción de un código HTTP a motivo. Pública para que los tests la recorran entera.
    /// 400 y 409 son un rechazo de los datos; 401 y 403, la clave o el <c>tenantId</c> que la API no
    /// acepta; 408 y 5xx, la plataforma que no está.
    /// </summary>
    public static MotivoUsoConector TraducirEstado(HttpStatusCode codigo) => (int)codigo switch
    {
        400 or 409 or 422 => MotivoUsoConector.RechazadoPorPlataforma,
        401 or 403 => MotivoUsoConector.CredencialRechazada,
        404 => MotivoUsoConector.NoEncontradoEnPlataforma,
        429 => MotivoUsoConector.LimiteSuperado,
        408 or >= 500 => MotivoUsoConector.PlataformaNoDisponible,
        _ => MotivoUsoConector.Inesperado,
    };

    private async Task<ResultadoConector<T>> EnviarAsync<T>(
        HttpMethod metodo, string ruta, HttpContent contenido, CredencialApiConector credencial, string tenantIdCtaima,
        OperacionConectorPlataformaCae operacion, CancellationToken cancellationToken)
        where T : class
    {
        using var _ = contenido;

        // Se valida a mano y se añade sin validación del framework: el FormatException de
        // HttpHeaders.Add copia el valor rechazado en su mensaje, y aquí ese valor es la clave.
        if (!EsValorDeCabeceraSeguro(credencial.Revelar()))
            return ResultadoConector<T>.Falla(MotivoUsoConector.CredencialRechazada);

        if (!EsValorDeCabeceraSeguro(tenantIdCtaima))
            return ResultadoConector<T>.Falla(MotivoUsoConector.DatosInsuficientes);

        using var peticion = new HttpRequestMessage(metodo, new Uri(new Uri(opciones.Value.UrlBase), ruta)) { Content = contenido };
        peticion.Headers.TryAddWithoutValidation(CabeceraClave, credencial.Revelar());
        peticion.Headers.TryAddWithoutValidation(CabeceraTenant, tenantIdCtaima);

        HttpResponseMessage respuesta;
        try
        {
            respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelación de quien llama: la puerta la registra como Cancelado.
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("CTAIMACAE.net no respondió a tiempo en {Operacion}.", operacion);
            return ResultadoConector<T>.Falla(MotivoUsoConector.PlataformaNoDisponible);
        }
        catch (HttpRequestException ex)
        {
            // Solo el tipo y el código de error de socket: el mensaje puede llevar el host o el cuerpo.
            logger.LogWarning("CTAIMACAE.net no está disponible en {Operacion} ({TipoError}).", operacion, ex.HttpRequestError);
            return ResultadoConector<T>.Falla(MotivoUsoConector.PlataformaNoDisponible);
        }

        using (respuesta)
        {
            if (respuesta.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
                return await LeerCuerpoAsync<T>(respuesta, operacion, cancellationToken);

            var motivo = TraducirEstado(respuesta.StatusCode);
            logger.LogWarning(
                "CTAIMACAE.net respondió {CodigoHttp} en {Operacion}; se registra como {Motivo}.",
                (int)respuesta.StatusCode, operacion, motivo);
            return ResultadoConector<T>.Falla(motivo);
        }
    }

    private async Task<ResultadoConector<T>> LeerCuerpoAsync<T>(
        HttpResponseMessage respuesta, OperacionConectorPlataformaCae operacion, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var cuerpo = await respuesta.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
            if (cuerpo is not null)
                return ResultadoConector<T>.Exito(cuerpo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning("CTAIMACAE.net devolvió un cuerpo ilegible en {Operacion} ({TipoExcepcion}).", operacion, ex.GetType().Name);
        }

        return ResultadoConector<T>.Falla(MotivoUsoConector.Inesperado);
    }

    private static bool EsValorDeCabeceraSeguro(string? valor) =>
        !string.IsNullOrEmpty(valor)
        && valor.Length <= LongitudMaximaCabecera
        && valor.All(c => c is > ' ' and < (char)0x7F);
}
