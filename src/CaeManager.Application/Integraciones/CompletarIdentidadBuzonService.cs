using CaeManager.Application.Common;
using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Comprueba en cada ciclo de renovación la identidad estable (<see cref="ConexionIntegracion.IdentidadBuzon"/>)
/// de las conexiones de Microsoft 365 del Tenant actual que no están Deshabilitadas, con el mismo token que las
/// abre. Lo invoca la renovación de suscripciones, que ya recorre cada Tenant, antes de renovar.
/// <list type="bullet">
/// <item>Coincide (o se acaba de completar): fija <see cref="ConexionIntegracion.IdentidadVerificadaUtc"/>. Si la
/// conexión estaba en ConError, se rehabilita aquí mismo, verificada en este ciclo; sus eventos
/// <see cref="EstadoEventoWebhook.Retenido"/> vuelven a la cola, y si no estaba verificada se encola una lectura
/// puntual del hueco (<see cref="RecuperacionCorreoBuzonService"/>).</item>
/// <item>Sin identidad (dada de alta antes de que existiera): se completa en la conexión y en su
/// <see cref="ReclamacionBuzonIntegracion"/>. Si choca con la de otra reclamación, el mismo buzón está conectado
/// en otro Tenant con otra dirección: esta conexión pasa a ConError (<see cref="MensajeBuzonDuplicado"/>), su
/// identidad queda vacía y Reactivar no la levanta: hay que volver a conectarla, y ahí decide el índice único. La
/// del otro Tenant no se toca: queda fuera del alcance de esta sesión por RLS.</item>
/// <item>Con identidad distinta de la que responde hoy a su dirección (el directorio reasignó la dirección a otro
/// buzón): pasa a ConError (<see cref="MensajeBuzonCambiado"/>).</item>
/// <item>Sin acceso al buzón (403/404, <see cref="CodigosErrorMicrosoft365.SinAccesoBuzon"/>): pasa a ConError
/// (<see cref="MensajeSinAccesoBuzon"/>).</item>
/// <item>Cualquier otro fallo (429, 5xx, 401, tiempo agotado, red, token, excepción) es transitorio y no cambia
/// nada: la verificación anterior envejece y, pasada <see cref="ConexionIntegracion.VentanaVerificacionIdentidad"/>,
/// la ingesta deja el correo en espera y no se envía nada desde el buzón; nada se descarta.</item>
/// </list>
/// ConError queda así solo para resultados definitivos, y "sin verificar" es una frescura, no un estado.
/// </summary>
public class CompletarIdentidadBuzonService(
    IIntegracionesQueryContext queryContext,
    IConexionIntegracionRepository conexionRepositorio,
    IReclamacionBuzonIntegracionRepository reclamacionRepositorio,
    IEventoWebhookRepository eventoRepositorio,
    AccesoGraphService accesoGraph,
    IMicrosoft365GraphClient graphClient,
    RecuperacionCorreoBuzonService recuperacion,
    IUnitOfWork unitOfWork,
    ILogger<CompletarIdentidadBuzonService> logger)
{
    public const string MensajeBuzonDuplicado =
        "Otra organización tiene conectado este mismo buzón con otra dirección. Desconéctalo en una de las dos.";

    public const string MensajeBuzonCambiado =
        "La dirección de esta conexión corresponde ahora a otro buzón. Desconéctala y vuelve a conectarla desde Conexiones.";

    public const string MensajeSinAccesoBuzon =
        "No podemos abrir este buzón: comprueba que la dirección sigue existiendo y que la aplicación TALVEG sigue teniendo acceso a él.";

    /// <summary>
    /// Motivos de ConError que pone esta comprobación. La renovación de la suscripción no los sobrescribe con el suyo,
    /// que es menos concreto: el que hay que resolver es este.
    /// </summary>
    public static bool EsMotivoDeIdentidad(string? ultimoError) =>
        ultimoError is MensajeBuzonDuplicado or MensajeBuzonCambiado or MensajeSinAccesoBuzon;

    /// <summary>
    /// ConError por un fallo de la renovación de la suscripción, sin sobrescribir el motivo de identidad (buzón
    /// cambiado, duplicado o sin acceso) que ya tenga la conexión: ese es más concreto, y es el que hay que resolver.
    /// </summary>
    public static void MarcarConErrorDeRenovacion(ConexionIntegracion conexion, string mensaje)
    {
        if (conexion.Estado == EstadoConexionIntegracion.ConError && EsMotivoDeIdentidad(conexion.UltimoError))
            return;

        conexion.MarcarConError(mensaje);
    }

    /// <returns>Las conexiones cuya identidad se leyó y coincide (o se acaba de completar) en este ciclo.</returns>
    public async Task<IReadOnlySet<Guid>> CompletarAsync(CancellationToken cancellationToken)
    {
        var verificadas = new HashSet<Guid>();
        var activas = await queryContext.ConexionesIntegracion
            .Where(c => c.Proveedor == ProveedorIntegracion.Microsoft365 && c.Estado != EstadoConexionIntegracion.Deshabilitada)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        foreach (var conexionId in activas)
        {
            // Una conexión que lanza no deja sin comprobar a las siguientes del Tenant: no sale verificada y se
            // reintenta en el ciclo siguiente; su estado no cambia.
            try
            {
                if (await ComprobarAsync(conexionId, cancellationToken))
                    verificadas.Add(conexionId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "No se pudo comprobar la identidad del buzón de la conexión {ConexionId}.", conexionId);
            }
        }

        return verificadas;
    }

    /// <returns>true si la identidad se leyó y coincide con la guardada, o se acaba de completar.</returns>
    private async Task<bool> ComprobarAsync(Guid conexionId, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
        if (conexion is null)
            return false;

        var ahoraUtc = DateTime.UtcNow;
        // Antes de tocar nada: si ya estaba verificada y Habilitada, la ingesta no ha descartado nada y no hay hueco.
        var veniaSinVerificar = !conexion.OperativaVerificada(ahoraUtc);

        var token = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);
        if (token.EsFallido)
        {
            // Transitorio: no cambia nada, la verificación anterior solo envejece.
            logger.LogWarning(
                "No se pudo obtener el token de la conexión {ConexionId} para comprobar su buzón: {Error}", conexionId, token.Error.Mensaje);
            return false;
        }

        var identidad = await graphClient.ObtenerIdentidadBuzonAsync(token.Valor, BuzonGraph.De(conexion), cancellationToken);
        if (identidad.EsFallido)
        {
            logger.LogWarning(
                "No se pudo leer la identidad del buzón de la conexión {ConexionId}: {Error}", conexionId, identidad.Error.Mensaje);

            // Solo un 403/404 es definitivo. El resto (429, 5xx, 401, tiempo agotado, red) no dice nada del buzón.
            // Un motivo de identidad más concreto que ya tuviera la conexión se conserva.
            if (identidad.Error.Codigo == CodigosErrorMicrosoft365.SinAccesoBuzon && !EsMotivoDeIdentidad(conexion.UltimoError))
                conexion.MarcarConError(MensajeSinAccesoBuzon);

            // También guarda el token refrescado, si lo hubo, para no volver a rotarlo en la renovación.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return false;
        }

        if (conexion.IdentidadBuzon is { } guardada)
        {
            if (!string.Equals(guardada, identidad.Valor, StringComparison.Ordinal))
            {
                logger.LogError(
                    "La dirección de la conexión {ConexionId} responde ahora con otro buzón: pasa a ConError.", conexionId);
                conexion.MarcarConError(MensajeBuzonCambiado);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return false;
            }

            await MarcarVerificadaAsync(conexion, token.Valor, ahoraUtc, veniaSinVerificar, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }

        conexion.FijarIdentidadBuzon(identidad.Valor);
        var reclamacion = await reclamacionRepositorio.ObtenerPorConexionAsync(conexionId, cancellationToken);
        reclamacion?.FijarIdentidadBuzon(identidad.Valor);
        conexion.MarcarIdentidadVerificada(ahoraUtc);

        if (await reclamacionRepositorio.GuardarCambiosSiBuzonLibreAsync(cancellationToken))
        {
            // Ya confirmada la identidad: la rehabilitación, la cola y la recuperación van en un segundo guardado, para
            // no leer Graph por un buzón que acaba de chocar con otro.
            await MarcarVerificadaAsync(conexion, token.Valor, ahoraUtc, veniaSinVerificar, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }

        // El guardado fallido descartó los cambios en memoria: se vuelve a leer la conexión para marcarla.
        logger.LogError(
            "La conexión {ConexionId} tiene el mismo buzón que una reclamación de otro Tenant con otra dirección: pasa a ConError.",
            conexionId);
        var duplicada = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
        if (duplicada is null)
            return false;

        duplicada.MarcarConError(MensajeBuzonDuplicado);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return false;
    }

    /// <summary>
    /// Verificada en este ciclo: se rehabilita si estaba en ConError, sus eventos retenidos vuelven a la cola y, si
    /// venía sin verificar, se encola la lectura puntual del hueco.
    /// </summary>
    private async Task MarcarVerificadaAsync(
        ConexionIntegracion conexion, string accessToken, DateTime ahoraUtc, bool veniaSinVerificar, CancellationToken cancellationToken)
    {
        conexion.MarcarIdentidadVerificada(ahoraUtc);

        if (conexion.Estado == EstadoConexionIntegracion.ConError)
        {
            logger.LogInformation("La conexión {ConexionId} vuelve a estar verificada: se rehabilita.", conexion.Id);
            conexion.Rehabilitar();
        }

        foreach (var retenido in await eventoRepositorio.ObtenerRetenidosAsync(conexion.Id, cancellationToken))
            retenido.Liberar();

        if (veniaSinVerificar)
            await recuperacion.EncolarHuecoAsync(conexion, accessToken, ahoraUtc, cancellationToken);
    }
}
