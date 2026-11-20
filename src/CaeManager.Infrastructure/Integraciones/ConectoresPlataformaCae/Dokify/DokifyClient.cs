using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CaeManager.Application.Integraciones.ConectoresPlataformaCae;
using CaeManager.Domain.Integraciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Dokify;

/// <summary>
/// Cliente HTTP tipado, de solo lectura, de la API pública de dokify (<c>https://app.dokify.net/app/</c>). Autentica
/// con el token OAuth2 de la Conexión en la cabecera <c>Authorization</c>: nunca en la URL y nunca en un log ni en una
/// excepción (OWASP ASVS 4.0.3 V7.1.1). Solo hace <c>GET</c>; la escritura (altas, subida de documentos, invitaciones,
/// checkins, anulaciones) no está.
///
/// <para>
/// <c>internal</c> a propósito: fuera de Infrastructure nadie puede resolverlo ni llamarlo saltándose la puerta
/// (<c>OperacionesConectorPlataformaCae</c>), que decide Tenant, interruptor, Add-on y credencial y deja el uso
/// registrado.
/// </para>
///
/// <para>
/// Ninguna respuesta de dokify sale de aquí como texto: un fallo es un <see cref="MotivoUsoConector"/> y el log solo lleva
/// el código HTTP y el nombre de la operación. El CIF o NIF por el que se pregunta va en la ruta (la API no ofrece otra
/// forma), así que el registro del <c>HttpClient</c> está apagado entero (su traza de petición escribiría la ruta) y cada
/// petición lleva la marca <see cref="PeticionConIdentificadoresEnLaRuta"/> y se envía dentro de su ámbito, por los
/// que OpenTelemetry y Sentry no la exportan. No reintenta: son lecturas, pero no constan los límites de uso de la API.
/// </para>
/// </summary>
internal sealed partial class DokifyClient(
    HttpClient http, IOptions<DokifyOptions> opciones, ILogger<DokifyClient> logger)
{
    public const string CabeceraAutorizacion = "Authorization";

    /// <summary>Tope del cuerpo de respuesta en memoria: un documento viaja en base64 dentro del JSON.</summary>
    public const long TamanoMaximoRespuesta = 20 * 1024 * 1024;

    // shortcut: la wiki no documenta el tamaño máximo de página; se pide de 50 en 50, por debajo del que cualquier API
    // admite. Medirlo contra el sandbox cuando dokify conteste.
    public const int TamanoMaximoPagina = DokifyMapeo.MaximoDeFilasPorPagina;

    private const int LongitudMaximaCabecera = 4096;

    /// <summary>
    /// Un 3xx no se sigue: la API no tiene redirecciones legítimas, y el destino de una recibiría la ruta, con el CIF o el
    /// NIF por el que se pregunta. Sin cookies: el manejador del pool se
    /// comparte entre las conexiones de todos los Tenants, y una cookie de sesión de una cuenta acabaría en la llamada
    /// de otra.
    /// </summary>
    public static SocketsHttpHandler CrearManejadorSinRedirecciones() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    };

    public Task<ResultadoConector<PaginaDokify<EmpresaDokify>>> ListarEmpresasAsync(
        CredencialApiConector credencial, int pagina, int tamano, CancellationToken cancellationToken) =>
        LeerAsync(RutaDePagina("clients/self/companies", pagina, tamano), credencial, "empresas", DokifyMapeo.Empresas, cancellationToken);

    public Task<ResultadoConector<PaginaDokify<TrabajadorDokify>>> ListarTrabajadoresDeEmpresaAsync(
        CredencialApiConector credencial, string cifEmpresa, int pagina, int tamano, CancellationToken cancellationToken) =>
        LeerAsync(
            EsIdentificadorSeguro(cifEmpresa) ? RutaDePagina($"clients/self/companies/{cifEmpresa}/employees", pagina, tamano) : null,
            credencial, "trabajadores", DokifyMapeo.Trabajadores, cancellationToken);

    public Task<ResultadoConector<MaquinaDokify>> ObtenerMaquinaAsync(
        CredencialApiConector credencial, string matricula, CancellationToken cancellationToken) =>
        LeerAsync(RutaDe(ElementoDokify.Maquina, matricula, "info"), credencial, "maquina", DokifyMapeo.Maquina, cancellationToken);

    public Task<ResultadoConector<EstadoGlobalDokify>> ObtenerEstadoAsync(
        CredencialApiConector credencial, ElementoDokify elemento, string vat, CancellationToken cancellationToken) =>
        LeerAsync(RutaDe(elemento, vat, "status"), credencial, "estado", DokifyMapeo.EstadoGlobal, cancellationToken);

    public Task<ResultadoConector<IReadOnlyList<DocumentoDokify>>> ObtenerDocumentosAsync(
        CredencialApiConector credencial, ElementoDokify elemento, string vat, CancellationToken cancellationToken) =>
        LeerAsync(RutaDe(elemento, vat, "docs"), credencial, "documentos", DokifyMapeo.Documentos, cancellationToken);

    public Task<ResultadoConector<IReadOnlyList<AsignacionDokify>>> ObtenerAsignacionesAsync(
        CredencialApiConector credencial, ElementoDokify elemento, string vat, CancellationToken cancellationToken) =>
        LeerAsync(RutaDe(elemento, vat, "assignments"), credencial, "asignaciones", DokifyMapeo.Asignaciones, cancellationToken);

    // shortcut: solo el último fichero del tipo de documento. Elegir uno concreto (attachment) o el de una Empresa de
    // referencia (company) existe en la API y no se usa hasta que haya un consumidor que lo necesite.
    public Task<ResultadoConector<DocumentoDescargadoDokify>> DescargarDocumentoAsync(
        CredencialApiConector credencial, ElementoDokify elemento, string vat, string tipoDocumentoId, CancellationToken cancellationToken) =>
        LeerAsync(
            EsNumeroSeguro(tipoDocumentoId) && RutaDe(elemento, vat, null) is { } ruta ? $"document/{tipoDocumentoId}/{ruta}" : null,
            credencial, "descarga", DokifyMapeo.Descarga, cancellationToken);

    /// <summary>
    /// La traducción de un código HTTP a motivo. 400 y 422 son un rechazo de la petición; 401 y 403, el token que la API no
    /// acepta (o un documento que no se puede ver); 404, un elemento o documento que no existe; 408 y 5xx, la plataforma
    /// que no está.
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

    // Un CIF, NIF o matrícula, y un tipo de documento numérico: lo que cabe en un tramo de ruta sin escapes. Un valor con
    // «/», «..», «?» o «#» saldría del tramo y llamaría a otra ruta con el token.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Identificador();

    [GeneratedRegex(@"^[0-9]{1,18}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Numero();

    private static bool EsIdentificadorSeguro(string? valor) => valor is not null && Identificador().IsMatch(valor);

    private static bool EsNumeroSeguro(string? valor) => valor is not null && Numero().IsMatch(valor);

    private static string? RutaDe(ElementoDokify elemento, string vat, string? operacion)
    {
        if (!EsIdentificadorSeguro(vat))
            return null;

        var tramo = elemento switch
        {
            ElementoDokify.Empresa => "company",
            ElementoDokify.Trabajador => "employee",
            ElementoDokify.Maquina => "machine",
            _ => null,
        };

        return tramo is null ? null : operacion is null ? $"{tramo}/{vat}" : $"{tramo}/{vat}/{operacion}";
    }

    private static string? RutaDePagina(string ruta, int pagina, int tamano) =>
        pagina >= 0 && tamano is > 0 and <= TamanoMaximoPagina
            ? string.Create(CultureInfo.InvariantCulture, $"{ruta}?page={pagina}&size={tamano}")
            : null;

    private async Task<ResultadoConector<T>> LeerAsync<T>(
        string? ruta, CredencialApiConector credencial, string operacion, Func<JsonElement, T?> mapear,
        CancellationToken cancellationToken)
        where T : class
    {
        if (ruta is null)
            return ResultadoConector<T>.Falla(MotivoUsoConector.DatosInsuficientes);

        // Se valida a mano y se añade sin validación del framework: el FormatException de HttpHeaders.Add copia el valor
        // rechazado en su mensaje, y aquí ese valor es el token. Un token que no cabe en una cabecera no se puede usar: es
        // una conexión sin credencial, no un rechazo de la plataforma, que no llegó a ver la llamada.
        var token = credencial.Revelar();
        if (string.IsNullOrEmpty(token) || token.Length > LongitudMaximaCabecera || !token.All(c => c is > ' ' and < (char)0x7F))
            return ResultadoConector<T>.Falla(MotivoUsoConector.SinCredencial);

        var esquema = opciones.Value.EsquemaAutorizacion;
        using var peticion = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(opciones.Value.UrlBase), ruta));
        PeticionConIdentificadoresEnLaRuta.Marcar(peticion);
        peticion.Headers.TryAddWithoutValidation(CabeceraAutorizacion, string.IsNullOrEmpty(esquema) ? token : $"{esquema} {token}");

        HttpResponseMessage respuesta;
        try
        {
            using (PeticionConIdentificadoresEnLaRuta.Enviando())
                respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelación de quien llama: la puerta la registra como Cancelado.
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("dokify no respondió a tiempo en {Operacion}.", operacion);
            return ResultadoConector<T>.Falla(MotivoUsoConector.PlataformaNoDisponible);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            // La respuesta pasó de TamanoMaximoRespuesta (o una cabecera, de su límite): dokify contestó, así que no es
            // una plataforma caída sino una respuesta que no se espera.
            logger.LogWarning("dokify devolvió una respuesta más grande que el tope en {Operacion}.", operacion);
            return ResultadoConector<T>.Falla(MotivoUsoConector.Inesperado);
        }
        catch (HttpRequestException ex)
        {
            // Solo el código de error: el mensaje puede llevar el host o la ruta, que lleva un NIF.
            logger.LogWarning("dokify no está disponible en {Operacion} ({TipoError}).", operacion, ex.HttpRequestError);
            return ResultadoConector<T>.Falla(MotivoUsoConector.PlataformaNoDisponible);
        }

        using (respuesta)
        {
            if (respuesta.StatusCode == HttpStatusCode.OK)
                return await LeerCuerpoAsync(respuesta, operacion, mapear, cancellationToken);

            var motivo = TraducirEstado(respuesta.StatusCode);
            logger.LogWarning(
                "dokify respondió {CodigoHttp} en {Operacion}; se registra como {Motivo}.", (int)respuesta.StatusCode, operacion, motivo);
            return ResultadoConector<T>.Falla(motivo);
        }
    }

    private async Task<ResultadoConector<T>> LeerCuerpoAsync<T>(
        HttpResponseMessage respuesta, string operacion, Func<JsonElement, T?> mapear, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            await using var flujo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
            using var documento = await JsonDocument.ParseAsync(flujo, cancellationToken: cancellationToken);
            if (mapear(documento.RootElement) is { } valor)
                return ResultadoConector<T>.Exito(valor);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning("dokify devolvió un cuerpo ilegible en {Operacion} ({TipoExcepcion}).", operacion, ex.GetType().Name);
            return ResultadoConector<T>.Falla(MotivoUsoConector.Inesperado);
        }

        logger.LogWarning("dokify devolvió un cuerpo que no es el esperado en {Operacion}.", operacion);
        return ResultadoConector<T>.Falla(MotivoUsoConector.Inesperado);
    }
}
