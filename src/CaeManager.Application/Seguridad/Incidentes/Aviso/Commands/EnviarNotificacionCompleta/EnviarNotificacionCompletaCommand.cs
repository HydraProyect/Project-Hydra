using CaeManager.Application.Common;
using CaeManager.Application.ContactosSeguridad;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Seguridad.Incidentes.Aviso.Commands.EnviarNotificacionCompleta;

/// <summary>
/// Envía la notificación completa (los cinco apartados del art. 33.3 RGPD) de un incidente ya Confirmado, en
/// las 36 horas siguientes a la confirmación. Mismo clic, misma autoridad, mismos destinatarios e igual de
/// idempotente que <see cref="ConfirmarYEnviarPrimerAviso.ConfirmarYEnviarPrimerAvisoCommand"/>.
/// </summary>
public record EnviarNotificacionCompletaCommand(Guid IncidenteId, ContenidoAvisoIncidente Contenido)
    : ICommand<ResumenEnvioAviso>;

public class EnviarNotificacionCompletaCommandHandler(
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IAutorizacionAdminPlataforma autorizacion,
    IIncidentesSeguridadPlataforma plataforma,
    IContactosSeguridadPlataforma contactos,
    IEmailService correo,
    IOptions<OpcionesIncidentesSeguridad> opciones,
    ILogger<EnviarNotificacionCompletaCommandHandler> logger)
    : IRequestHandler<EnviarNotificacionCompletaCommand, Result<ResumenEnvioAviso>>
{
    public async Task<Result<ResumenEnvioAviso>> Handle(EnviarNotificacionCompletaCommand request, CancellationToken cancellationToken)
    {
        if (await AutorizacionAvisoIncidenteSeguridad.MotivoParaNoDecidirAsync(
                currentUserService, sesionPrivilegiadaActual, autorizacion, cancellationToken) is { } motivo)
            return Result.Fallo<ResumenEnvioAviso>(motivo);

        if (AutorizacionAvisoIncidenteSeguridad.ValidarContenido(request.Contenido, PlantillaAvisoIncidenteSeguridad.NotificacionCompleta) is { } invalido)
            return Result.Fallo<ResumenEnvioAviso>(invalido);

        var incidente = await plataforma.ObtenerAsync(request.IncidenteId, cancellationToken);
        if (incidente is null)
            return Result.Fallo<ResumenEnvioAviso>(ErroresIncidenteSeguridad.NoEncontrado);
        if (incidente.Estado != EstadoIncidenteSeguridad.Confirmado || incidente.ConfirmadoEnUtc is not { } confirmadoEnUtc)
            return Result.Fallo<ResumenEnvioAviso>(ErroresIncidenteSeguridad.NoConfirmado);

        var destinatarios = await contactos.ResolverDestinatariosAsync(incidente.TenantsAfectados, cancellationToken);
        if (destinatarios.EsFallido)
            return Result.Fallo<ResumenEnvioAviso>(ErroresIncidenteSeguridad.SoloActorDePlataforma);

        return await EnvioAvisosIncidenteSeguridad.EnviarAsync(
            plataforma, contactos, correo, opciones.Value, logger, incidente, confirmadoEnUtc,
            PlantillaAvisoIncidenteSeguridad.NotificacionCompleta, request.Contenido, destinatarios.Valor, cancellationToken);
    }
}
