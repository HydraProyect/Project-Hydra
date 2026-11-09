using CaeManager.Application.Common;
using CaeManager.Domain.Common;
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
/// <see cref="EstadoEventoWebhook.Retenido"/> vuelven a la cola. El hueco que dejó la caída queda declarado en
/// <see cref="ConexionIntegracion.HuecoPendienteDesdeUtc"/>, que Conexiones avisa; aquí no se lee ni se limpia.</item>
/// <item>Habilitada con la verificación ya caducada: se fija la marca del hueco en el momento en que caducó
/// (<see cref="ConexionIntegracion.RegistrarCaducidadVerificacion"/>), antes de intentar nada, y otra vez después de
/// leer la identidad en Graph y antes de marcarla verificada: la ventana puede caducar durante la llamada.</item>
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
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios,
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
    /// Fallo al pedir el token o al renovar la suscripción de una conexión. Un fallo transitorio
    /// (<see cref="CodigosErrorMicrosoft365.EsTransitorio"/>: 408, 429, 5xx, red, tiempo agotado, cuerpo ilegible) no
    /// cambia nada: se reintenta en el siguiente ciclo. Uno definitivo la pasa a ConError, sin sobrescribir el motivo de
    /// identidad (buzón cambiado, duplicado o sin acceso) que ya tenga: ese es más concreto, y es el que hay que
    /// resolver. La marca del hueco se fija en la expiración de la suscripción si ya pasó, porque desde entonces no
    /// llegan notificaciones, y si no, ahora.
    /// </summary>
    /// <returns>true si la conexión pasó a ConError.</returns>
    public static bool MarcarConErrorDeRenovacion(
        ConexionIntegracion conexion, Error error, DateTime expiracionSuscripcionUtc, DateTime ahoraUtc)
    {
        if (CodigosErrorMicrosoft365.EsTransitorio(error))
            return false;

        if (conexion.Estado == EstadoConexionIntegracion.ConError && EsMotivoDeIdentidad(conexion.UltimoError))
            return false;

        conexion.MarcarConError(error.Mensaje, expiracionSuscripcionUtc < ahoraUtc ? expiracionSuscripcionUtc : ahoraUtc);
        return true;
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

                // Un guardado fallido deja sus cambios rastreados: sin esto, el guardado de la conexión siguiente
                // reintentaría la mutación ajena (o volvería a fallar por ella).
                descarteCambios.DescartarCambiosPendientes();
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

        // La caducidad de la verificación no tiene transición propia: se detecta aquí, en cada ciclo, y la marca toma
        // la fecha exacta de caducidad, no la de este ciclo. Se guarda ya, para que un fallo transitorio posterior no la
        // pierda.
        if (conexion.RegistrarCaducidadVerificacion(ahoraUtc))
            await unitOfWork.SaveChangesAsync(cancellationToken);

        var token = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);
        if (token.EsFallido)
        {
            // Transitorio: no cambia nada, la verificación anterior solo envejece.
            logger.LogWarning(
                "No se pudo obtener el token de la conexión {ConexionId} para comprobar su buzón: {Error}", conexionId, token.Error.Mensaje);
            return false;
        }

        var identidad = await graphClient.ObtenerIdentidadBuzonAsync(token.Valor, BuzonGraph.De(conexion), cancellationToken);

        // La ventana pudo caducar durante la llamada: se registra antes de volver a marcar la conexión verificada, que
        // ya no lo detectaría. Se guarda con el resto de cambios de esta comprobación.
        conexion.RegistrarCaducidadVerificacion(DateTime.UtcNow);

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

            await MarcarVerificadaAsync(conexion, ahoraUtc, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }

        conexion.FijarIdentidadBuzon(identidad.Valor);
        var reclamacion = await reclamacionRepositorio.ObtenerPorConexionAsync(conexionId, cancellationToken);
        reclamacion?.FijarIdentidadBuzon(identidad.Valor);
        conexion.MarcarIdentidadVerificada(ahoraUtc);

        if (await reclamacionRepositorio.GuardarCambiosSiBuzonLibreAsync(cancellationToken))
        {
            // Ya confirmada la identidad: la rehabilitación y la cola van en un segundo guardado, para no tocar nada de
            // un buzón que acaba de chocar con otro.
            await MarcarVerificadaAsync(conexion, ahoraUtc, cancellationToken);
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
    /// Verificada en este ciclo: se rehabilita si estaba en ConError y sus eventos retenidos vuelven a la cola. La marca
    /// del hueco, si la hay, se conserva: el correo de ese intervalo pudo no entrar y Conexiones lo sigue avisando.
    /// </summary>
    private async Task MarcarVerificadaAsync(ConexionIntegracion conexion, DateTime ahoraUtc, CancellationToken cancellationToken)
    {
        conexion.MarcarIdentidadVerificada(ahoraUtc);

        if (conexion.Estado == EstadoConexionIntegracion.ConError)
        {
            logger.LogInformation("La conexión {ConexionId} vuelve a estar verificada: se rehabilita.", conexion.Id);
            conexion.Rehabilitar();
        }

        foreach (var retenido in await eventoRepositorio.ObtenerRetenidosAsync(conexion.Id, cancellationToken))
            retenido.Liberar();
    }
}
