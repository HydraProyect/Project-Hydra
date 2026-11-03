using CaeManager.Application.Common;
using CaeManager.Application.ContactosSeguridad;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Seguridad.Incidentes.Aviso.Commands.ConfirmarYEnviarPrimerAviso;

/// <summary>
/// «Confirmar y enviar el primer aviso»: el clic del Actor de Plataforma TALVEG que confirma la violación (desde
/// aquí cuentan las 24 y las 36 horas) y envía el primer aviso a cada Tenant beneficiario afectado. Nunca se
/// envía solo: este comando es la única puerta del primer aviso.
///
/// <para>
/// <b>Destinatarios.</b> Por Tenant, su contacto de seguridad validado o, sin él, sus Administradores
/// (<c>app_destinatarios_aviso_seguridad</c>, incremento 3a); la base vuelve a comprobar cada destinatario al
/// reclamar el envío.
/// </para>
///
/// <para>
/// <b>Reintento.</b> El mismo comando, otra vez: la confirmación ya hecha no cambia la hora, los envíos que
/// salieron no se repiten y los fallidos se reenvían con el texto de la primera vez.
/// </para>
/// </summary>
public record ConfirmarYEnviarPrimerAvisoCommand(Guid IncidenteId, ContenidoAvisoIncidente Contenido)
    : ICommand<ResumenEnvioAviso>;

public class ConfirmarYEnviarPrimerAvisoCommandHandler(
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IAutorizacionAdminPlataforma autorizacion,
    IIncidentesSeguridadPlataforma plataforma,
    IContactosSeguridadPlataforma contactos,
    IEmailService correo,
    IOptions<OpcionesIncidentesSeguridad> opciones,
    ILogger<ConfirmarYEnviarPrimerAvisoCommandHandler> logger)
    : IRequestHandler<ConfirmarYEnviarPrimerAvisoCommand, Result<ResumenEnvioAviso>>
{
    public async Task<Result<ResumenEnvioAviso>> Handle(ConfirmarYEnviarPrimerAvisoCommand request, CancellationToken cancellationToken)
    {
        if (await AutorizacionAvisoIncidenteSeguridad.MotivoParaNoDecidirAsync(
                currentUserService, sesionPrivilegiadaActual, autorizacion, cancellationToken) is { } motivo)
            return Result.Fallo<ResumenEnvioAviso>(motivo);

        if (AutorizacionAvisoIncidenteSeguridad.ValidarContenido(request.Contenido, PlantillaAvisoIncidenteSeguridad.PrimerAviso) is { } invalido)
            return Result.Fallo<ResumenEnvioAviso>(invalido);

        var incidente = await plataforma.ObtenerAsync(request.IncidenteId, cancellationToken);
        if (incidente is null)
            return Result.Fallo<ResumenEnvioAviso>(ErroresIncidenteSeguridad.NoEncontrado);
        if (incidente.Estado == EstadoIncidenteSeguridad.Descartado)
            return Result.Fallo<ResumenEnvioAviso>(ErroresIncidenteSeguridad.EstadoNoValido);

        // Destinatarios antes de confirmar: si no se pueden resolver, el plazo no empieza sin poder avisar.
        var destinatarios = await contactos.ResolverDestinatariosAsync(incidente.TenantsAfectados, cancellationToken);
        if (destinatarios.EsFallido)
            return Result.Fallo<ResumenEnvioAviso>(ErroresIncidenteSeguridad.SoloActorDePlataforma);

        // Longitud del correo ya escapado, también antes de confirmar: un aviso que la base rechazaría por largo
        // no puede arrancar el plazo. La hora provisional solo cambia la fecha, de longitud fija.
        if (await EnvioAvisosIncidenteSeguridad.ValidarLongitudAsync(
                plataforma, incidente, incidente.ConfirmadoEnUtc ?? DateTime.UtcNow,
                PlantillaAvisoIncidenteSeguridad.PrimerAviso, request.Contenido, destinatarios.Valor,
                cancellationToken) is { } largo)
            return Result.Fallo<ResumenEnvioAviso>(largo);

        var confirmacion = await plataforma.DecidirAsync(
            incidente.Id, DecisionIncidenteSeguridad.Confirmar, motivo: null, cancellationToken);
        if (confirmacion.EsFallido)
            return Result.Fallo<ResumenEnvioAviso>(confirmacion.Error);

        // La hora de confirmación es la de la base (la de la primera vez, si es un reintento).
        var confirmado = await plataforma.ObtenerAsync(incidente.Id, cancellationToken);
        if (confirmado?.ConfirmadoEnUtc is not { } confirmadoEnUtc)
            return Result.Fallo<ResumenEnvioAviso>(ErroresIncidenteSeguridad.NoConfirmado);

        logger.LogWarning("Incidente de seguridad {IncidenteId} confirmado; se envía el primer aviso.", incidente.Id);
        return await EnvioAvisosIncidenteSeguridad.EnviarAsync(
            plataforma, contactos, correo, opciones.Value, logger, confirmado, confirmadoEnUtc,
            PlantillaAvisoIncidenteSeguridad.PrimerAviso, request.Contenido, destinatarios.Valor, cancellationToken);
    }
}
