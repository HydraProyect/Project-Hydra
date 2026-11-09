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
/// No reintenta la suscripción de Graph por sí solo — solo limpia el
/// estado/<c>UltimoError</c>; el siguiente ciclo de
/// <c>RenovacionSuscripcionWebhookHostedService</c> se encarga de renovarla
/// si la ventana lo permite. Si la suscripción ya expiró del todo en Graph,
/// reactivar aquí no basta: hace falta reconectar el buzón desde cero.
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
    ICerrojoVerificacionBuzon cerrojo)
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

        // Resuelto ANTES de mutar la entidad (hallazgo del punto 6, revisión
        // Codex sobre PR #820): Rehabilitar() no se deshace solo con
        // devolver un fallo — el DbContext es scoped por circuito Blazor, así
        // que una entidad modificada y trackeada sin guardar podría
        // persistirse igualmente en una operación posterior ajena a este
        // Command.
        if (conexion.Proveedor == ProveedorIntegracion.Microsoft365)
        {
            var (comprobacion, motivoDefinitivo) = await ComprobarIdentidadBuzonAsync(conexion, cancellationToken);
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
        }

        var tenantId = Guid.Empty;
        if (necesitaReclamarBuzon)
        {
            if (tenantActual.TenantId is not { } resuelto)
                return Result.Fallo(Error.Crear("Integraciones.Microsoft365.TenantNoResuelto", "No se pudo determinar la organización actual."));
            tenantId = resuelto;
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

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Exito();
        }

        reclamacionRepositorio.Reclamar(new ReclamacionBuzonIntegracion(conexion.BuzonEmail, conexion.IdentidadBuzon, tenantId, conexion.Id, DateTime.UtcNow));
        var buzonLibre = await reclamacionRepositorio.GuardarCambiosSiBuzonLibreAsync(cancellationToken);
        if (!buzonLibre)
        {
            return Result.Fallo(Error.Crear(
                "Integraciones.Microsoft365.BuzonYaConectado",
                "Este buzón fue conectado por otra organización mientras estaba desconectado."));
        }

        return Result.Exito();
    }

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
    private async Task<(Result Resultado, string? MotivoDefinitivo)> ComprobarIdentidadBuzonAsync(
        ConexionIntegracion conexion, CancellationToken cancellationToken)
    {
        if (conexion.IdentidadBuzon is not { } guardada)
            return (Result.Fallo(Error.Crear(
                CodigoReconexionNecesaria,
                "Esta conexión no tiene la comprobación de buzón: desconéctala y vuelve a conectarla desde Conexiones.")), null);

        var token = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);
        if (token.EsFallido)
            return (Result.Fallo(Error.Crear(CodigoSinComprobacion, "No pudimos comprobar el buzón en Microsoft. Inténtalo de nuevo.")), null);

        var identidad = await graphClient.ObtenerIdentidadBuzonAsync(token.Valor, BuzonGraph.De(conexion), cancellationToken);
        if (identidad.EsFallido)
        {
            if (identidad.Error.Codigo != CodigosErrorMicrosoft365.SinAccesoBuzon)
                return (Result.Fallo(Error.Crear(CodigoSinComprobacion, "No pudimos comprobar el buzón en Microsoft. Inténtalo de nuevo.")), null);

            // Como en el ciclo: un motivo de identidad más concreto que ya tuviera la conexión se conserva.
            var motivo = CompletarIdentidadBuzonService.EsMotivoDeIdentidad(conexion.UltimoError)
                ? conexion.UltimoError
                : CompletarIdentidadBuzonService.MensajeSinAccesoBuzon;
            return (Result.Fallo(Error.Crear(CodigoSinAccesoBuzon, CompletarIdentidadBuzonService.MensajeSinAccesoBuzon)), motivo);
        }

        return string.Equals(identidad.Valor, guardada, StringComparison.Ordinal)
            ? (Result.Exito(), null)
            : (Result.Fallo(Error.Crear(
                CodigoReconexionNecesaria,
                "La dirección de esta conexión corresponde ahora a otro buzón: desconéctala y vuelve a conectarla desde Conexiones.")),
                CompletarIdentidadBuzonService.MensajeBuzonCambiado);
    }

    public const string CodigoReconexionNecesaria = "Integraciones.Microsoft365.ReconexionNecesaria";
    public const string CodigoSinAccesoBuzon = "Integraciones.Microsoft365.ReactivarSinAccesoBuzon";
    public const string CodigoSinComprobacion = "Integraciones.Microsoft365.BuzonSinComprobar";
    public const string CodigoVerificacionEnCurso = "Integraciones.Microsoft365.VerificacionEnCurso";
}
