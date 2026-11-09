using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Integraciones.Commands.ReactivarConexion;

/// <summary>
/// Vuelve una conexión de <see cref="EstadoConexionIntegracion.ConError"/> (o
/// <see cref="EstadoConexionIntegracion.Deshabilitada"/>) a
/// <see cref="EstadoConexionIntegracion.Habilitada"/> — necesario desde que
/// <c>RenovacionSuscripcionWebhookHostedService</c> puede marcar
/// <c>ConError</c> de verdad (salud de plataforma, A-07): sin esta acción esa
/// conexión no tenía ninguna vía de vuelta salvo SQL directo.
///
/// En Microsoft 365, antes de rehabilitar renueva la suscripción de Graph o la
/// crea de nuevo si ya no existe (<see cref="SuscripcionAlRehabilitarService"/>):
/// una conexión nunca vuelve a Habilitada sin suscripción vigente.
/// </summary>
public record ReactivarConexionCommand(Guid ConexionId) : ICommand;

public class ReactivarConexionCommandValidator : AbstractValidator<ReactivarConexionCommand>
{
    public ReactivarConexionCommandValidator()
    {
        RuleFor(c => c.ConexionId).NotEmpty();
    }
}

public class ReactivarConexionCommandHandler(
    IConexionIntegracionRepository conexionRepositorio,
    IReclamacionBuzonIntegracionRepository reclamacionRepositorio,
    IAlcanceDatosService alcanceDatos,
    ITenantActual tenantActual,
    IUnitOfWork unitOfWork,
    AccesoGraphService accesoGraph,
    IMicrosoft365GraphClient graphClient,
    IEventoWebhookRepository eventoRepositorio,
    ICerrojoVerificacionBuzon cerrojo,
    SuscripcionAlRehabilitarService suscripciones,
    IDescarteCambiosPendientes descarteCambios)
    : IRequestHandler<ReactivarConexionCommand, Result>
{
    public async Task<Result> Handle(ReactivarConexionCommand request, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(request.ConexionId, cancellationToken);
        if (conexion is null || !await alcanceDatos.ClienteOpcionalVisibleAsync(conexion.ClienteId, cancellationToken))
            return Result.Fallo(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        // Un buzón personal de OTRO gestor no se resuelve por cartera de
        // Cliente (tiene ClienteId null, igual que el genérico del tenant) —
        // mismo hallazgo y mismo fix que EnviarMensajeNuevoCommandHandler.
        if (!await alcanceDatos.ConexionIntegracionVisibleAsync(conexion.Id, cancellationToken))
            return Result.Fallo(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        if (conexion.Proveedor != ProveedorIntegracion.Microsoft365)
            return await ReactivarAsync(conexion, cancellationToken);

        // Reactivar verifica la identidad del buzón: nunca a la vez que el ciclo diario o el reintento de la misma
        // conexión, en ninguna réplica. Si otra verificación está en curso, no se espera: se pide volver a intentarlo.
        Result? resultado = null;
        if (!await cerrojo.IntentarEjecutarAsync(conexion.Id, async ct => resultado = await ReactivarAsync(conexion, ct), cancellationToken))
            return Result.Fallo(Error.Crear(
                CodigoVerificacionEnCurso, "Ya estamos comprobando este buzón en Microsoft. Inténtalo de nuevo en unos segundos."));

        return resultado!;
    }

    private async Task<Result> ReactivarAsync(ConexionIntegracion conexion, CancellationToken cancellationToken)
    {
        // Solo Deshabilitada libera la reclamación (ver DesconectarBuzonCommandHandler)
        // — ConError nunca la libera, así que reactivar desde ConError no
        // necesita reclamar de nuevo.
        var necesitaReclamarBuzon = conexion.Estado == EstadoConexionIntegracion.Deshabilitada;

        // El Tenant de la reclamación se resuelve antes de tocar Graph ni rastrear nada: sin él no se crea ni se renueva
        // una suscripción que luego habría que borrar, y no queda ninguna fila de SuscripcionWebhook modificada en el
        // DbContext (que vive lo que el circuito) a la espera de que la guarde otra operación.
        var tenantId = Guid.Empty;
        if (necesitaReclamarBuzon)
        {
            if (tenantActual.TenantId is not { } resuelto)
                return Result.Fallo(Error.Crear("Integraciones.Microsoft365.TenantNoResuelto", "No se pudo determinar la organización actual."));

            tenantId = resuelto;
        }

        // Resuelto ANTES de mutar la entidad (hallazgo del punto 6, revisión
        // Codex sobre PR #820): Rehabilitar() no se deshace solo con
        // devolver un fallo — el DbContext es scoped por circuito Blazor, así
        // que una entidad modificada y trackeada sin guardar podría
        // persistirse igualmente en una operación posterior ajena a este
        // Command.
        string? accessToken = null;
        SuscripcionAlRehabilitarService.SuscripcionVigente? vigente = null;
        if (conexion.Proveedor == ProveedorIntegracion.Microsoft365)
        {
            var (comprobacion, motivoDefinitivo, token) = await ComprobarIdentidadBuzonAsync(conexion, cancellationToken);
            accessToken = token;
            if (comprobacion.EsFallido)
            {
                // Resultado definitivo, igual que en el ciclo de verificación (la dirección responde con otro buzón, o
                // Microsoft dice 403/404): la conexión deja de estar operativa ya, porque su verificación anterior no la
                // sostiene, con el mismo motivo que pone el ciclo y la marca del hueco (disparador c). Solo esta
                // mutación se guarda: es lo único que se ha tocado. Desde Deshabilitada no: sigue desconectada, sin
                // reclamación ni suscripción (MarcarConError tampoco la movería).
                if (motivoDefinitivo is { } motivo && conexion.Estado != EstadoConexionIntegracion.Deshabilitada)
                {
                    conexion.MarcarConError(motivo);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                }

                return Result.Fallo(comprobacion.Error);
            }

            // Rehabilitar exige una suscripción vigente (SuscripcionAlRehabilitarService): sin ella la conexión
            // quedaría Habilitada y verificada pero sin notificaciones. Un fallo transitorio no muta nada y pide volver
            // a intentarlo; uno definitivo deja ConError (nunca a una Deshabilitada, que sigue desconectada).
            var suscripcion = await suscripciones.AsegurarAsync(conexion, accessToken!, cancellationToken);
            if (suscripcion.EsFallido)
            {
                if (CodigosErrorMicrosoft365.EsTransitorio(suscripcion.Error))
                    return Result.Fallo(Error.Crear(CodigoSinComprobacion, "No pudimos activar las notificaciones de este buzón. Inténtalo de nuevo."));

                if (conexion.Estado != EstadoConexionIntegracion.Deshabilitada)
                {
                    conexion.MarcarConError(suscripcion.Error.Mensaje);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                }

                return Result.Fallo(Error.Crear(CodigoSuscripcionImposible, suscripcion.Error.Mensaje));
            }

            vigente = suscripcion.Valor;
        }

        var veniaSinVerificar = !conexion.OperativaVerificada(DateTime.UtcNow);

        // Una verificación caducada no marca hueco: mientras estuvo sin verificar, su correo esperó en la cola.
        conexion.Rehabilitar();
        if (conexion.Proveedor == ProveedorIntegracion.Microsoft365)
            conexion.MarcarIdentidadVerificada(DateTime.UtcNow);

        if (!necesitaReclamarBuzon)
        {
            // Desde ConError, o sin verificar: sus eventos retenidos vuelven a la cola. Aquí no se lee Graph: lo que
            // la ingesta descartó mientras tanto queda declarado en HuecoPendienteDesdeUtc, que esta rehabilitación no
            // limpia, y Conexiones lo sigue avisando. Desde Deshabilitada no: lo que llegó con la conexión desconectada
            // a propósito no se ingiere.
            if (conexion.Proveedor == ProveedorIntegracion.Microsoft365 && veniaSinVerificar)
            {
                foreach (var retenido in await eventoRepositorio.ObtenerRetenidosAsync(conexion.Id, cancellationToken))
                    retenido.Liberar();
            }

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                // La rehabilitación no se guardó: lo rastreado (Habilitada, la fila de la suscripción) se suelta para que
                // no lo guarde otra operación del circuito, y la suscripción creada en esta llamada no queda huérfana en
                // Graph. Una solo renovada sigue siendo la de esta conexión en ConError, que la renovación diaria mantiene.
                descarteCambios.DescartarCambiosPendientes();
                await CompensarAsync(tambienRenovada: false);
                throw;
            }

            return Result.Exito();
        }

        // Orden: primero la suscripción y después la reclamación, que se guarda en el mismo guardado que la
        // rehabilitación y la fila de la suscripción. Reclamar antes evitaría crear y borrar una suscripción cuando la
        // guarda transitoria está en pausa, pero dejaría guardada la reclamación de una conexión aún Deshabilitada si
        // después la suscripción fallase, y su compensación (liberarla) sería otro guardado que puede fallar: una
        // reclamación huérfana bloquea el buzón para todos los Tenants. Una suscripción huérfana, en cambio, se borra
        // aquí sin bloquear y, si ese borrado falla, expira sola en unos días. Saber si la guarda está en pausa antes de
        // reclamar exigiría leer reclamaciones de otros Tenants: la guarda se decidió sin ninguna lectura entre Tenants.
        // Un compartido vuelve a entrar en la guarda transitoria (ReclamacionBuzonIntegracion.CuentaParaGuardaTransitoria).
        reclamacionRepositorio.Reclamar(new ReclamacionBuzonIntegracion(
            conexion.BuzonEmail, conexion.IdentidadBuzon, tenantId, conexion.Id, DateTime.UtcNow,
            esBuzonCompartido: conexion.EsBuzonCompartido));
        ResultadoReclamacionBuzon resultadoReclamacion;
        try
        {
            resultadoReclamacion = await reclamacionRepositorio.GuardarCambiosSiBuzonLibreAsync(cancellationToken);
        }
        catch
        {
            descarteCambios.DescartarCambiosPendientes();
            await CompensarAsync(tambienRenovada: true);
            throw;
        }

        switch (resultadoReclamacion)
        {
            case ResultadoReclamacionBuzon.BuzonYaReclamado:
                await CompensarAsync(tambienRenovada: true);
                return Result.Fallo(Error.Crear(
                    "Integraciones.Microsoft365.BuzonYaConectado",
                    "Este buzón fue conectado por otra organización mientras estaba desconectado."));
            case ResultadoReclamacionBuzon.AltaCompartidaEnPausa:
                await CompensarAsync(tambienRenovada: true);
                return Result.Fallo(Error.Crear(
                    ConectarBuzonMicrosoft365.ConectarBuzonMicrosoft365CommandHandler.CodigoAltaCompartidaEnPausa,
                    ConectarBuzonMicrosoft365.ConectarBuzonMicrosoft365CommandHandler.MensajeAltaCompartidaEnPausa));
        }

        return Result.Exito();

        // La rehabilitación no se guardó (la reclamación falló y el guardado se descartó con ChangeTracker.Clear, o el
        // guardado lanzó): la suscripción recién creada en Graph quedaría huérfana. Si la conexión sigue Deshabilitada
        // (camino de la reclamación), también se borra la que solo se renovó: una Deshabilitada no tiene suscripción viva.
        Task CompensarAsync(bool tambienRenovada) => vigente is not null && accessToken is not null
            ? suscripciones.CompensarAsync(accessToken, vigente, tambienRenovada, CancellationToken.None)
            : Task.CompletedTask;
    }

    public const string CodigoSuscripcionImposible = "Integraciones.Microsoft365.SuscripcionImposible";

    /// <summary>
    /// Reactivar no es un atajo para saltarse la guarda del buzón: la conexión debe tener identidad (una anterior a
    /// ese campo, o una cuyo relleno chocó con la reclamación de otro Tenant, no la tiene y se vuelve a conectar,
    /// donde el índice único decide) y el buzón que hoy responde a su dirección debe seguir siendo el mismo. Si no
    /// se puede comprobar, no se reactiva.
    /// <para>
    /// La clasificación es la del ciclo de verificación (<see cref="CompletarIdentidadBuzonService"/>): solo la identidad
    /// distinta y el 403/404 (<see cref="CodigosErrorMicrosoft365.SinAccesoBuzon"/>) son definitivos y devuelven su motivo
    /// de ConError. Un fallo del token o cualquier otro de la lectura (429, 5xx, 401, tiempo agotado, red) es transitorio:
    /// solo se pide volver a intentarlo.
    /// </para>
    /// </summary>
    /// <returns>El resultado y, si el fallo es definitivo, el motivo de ConError que pone el ciclo.</returns>
    private async Task<(Result Resultado, string? MotivoDefinitivo, string? AccessToken)> ComprobarIdentidadBuzonAsync(
        ConexionIntegracion conexion, CancellationToken cancellationToken)
    {
        if (conexion.IdentidadBuzon is not { } guardada)
            return (Result.Fallo(Error.Crear(
                CodigoReconexionNecesaria,
                "Esta conexión no tiene la comprobación de buzón: desconéctala y vuelve a conectarla desde Conexiones.")), null, null);

        var token = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);
        if (token.EsFallido)
            return (Result.Fallo(Error.Crear(CodigoSinComprobacion, "No pudimos comprobar el buzón en Microsoft. Inténtalo de nuevo.")), null, null);

        var identidad = await graphClient.ObtenerIdentidadBuzonAsync(token.Valor, BuzonGraph.De(conexion), cancellationToken);
        if (identidad.EsFallido)
        {
            if (identidad.Error.Codigo != CodigosErrorMicrosoft365.SinAccesoBuzon)
                return (Result.Fallo(Error.Crear(CodigoSinComprobacion, "No pudimos comprobar el buzón en Microsoft. Inténtalo de nuevo.")), null, token.Valor);

            // Como en el ciclo: un motivo de identidad más concreto que ya tuviera la conexión se conserva.
            var motivo = CompletarIdentidadBuzonService.EsMotivoDeIdentidad(conexion.UltimoError)
                ? conexion.UltimoError
                : CompletarIdentidadBuzonService.MensajeSinAccesoBuzon;
            return (Result.Fallo(Error.Crear(CodigoSinAccesoBuzon, CompletarIdentidadBuzonService.MensajeSinAccesoBuzon)), motivo, token.Valor);
        }

        return string.Equals(identidad.Valor, guardada, StringComparison.Ordinal)
            ? (Result.Exito(), null, token.Valor)
            : (Result.Fallo(Error.Crear(
                CodigoReconexionNecesaria,
                "La dirección de esta conexión corresponde ahora a otro buzón: desconéctala y vuelve a conectarla desde Conexiones.")),
                CompletarIdentidadBuzonService.MensajeBuzonCambiado, token.Valor);
    }

    public const string CodigoReconexionNecesaria = "Integraciones.Microsoft365.ReconexionNecesaria";
    public const string CodigoSinAccesoBuzon = "Integraciones.Microsoft365.ReactivarSinAccesoBuzon";
    public const string CodigoSinComprobacion = "Integraciones.Microsoft365.BuzonSinComprobar";
    public const string CodigoVerificacionEnCurso = "Integraciones.Microsoft365.VerificacionEnCurso";
}
