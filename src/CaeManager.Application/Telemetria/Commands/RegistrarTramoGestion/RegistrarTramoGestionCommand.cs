using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Configuracion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Telemetria;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Telemetria.Commands.RegistrarTramoGestion;

/// <summary>
/// Cierra un tramo de medición y lo persiste. Lo invoca el medidor de la
/// Bandeja, nunca el usuario directamente.
///
/// Devuelve <see cref="Result.Exito()"/> sin escribir nada si la medición está
/// apagada para el tenant: el llamante no tiene que saber si el toggle está
/// puesto, y así apagarla en caliente deja de registrar sin romper ninguna
/// pantalla abierta.
/// </summary>
public record RegistrarTramoGestionCommand(
    Guid ConversacionId,
    DateTime InicioUtc,
    DateTime FinUtc,
    int SegundosActivos,
    MotivoCierreSesionGestion Motivo) : ICommand;

public class RegistrarTramoGestionCommandValidator : AbstractValidator<RegistrarTramoGestionCommand>
{
    public RegistrarTramoGestionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty();
        RuleFor(c => c.SegundosActivos).GreaterThanOrEqualTo(0);
        RuleFor(c => c.FinUtc).GreaterThanOrEqualTo(c => c.InicioUtc);
    }
}

public class RegistrarTramoGestionCommandHandler(
    IRegistroTiempoGestionRepository repositorio,
    IComunicacionesQueryContext comunicacionesContext,
    IConfiguracionQueryContext configuracionContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<RegistrarTramoGestionCommand, Result>
{
    /// <summary>
    /// Tope duro por tramo. El medidor solo escucha visibilidad y foco de ventana —
    /// no ratón ni teclado, a propósito —, así que una pestaña enfocada y abandonada
    /// acumularía indefinidamente. Media hora es más de lo que dura cualquier gestión
    /// real de un hilo y deja la métrica inflable solo dentro de ese margen.
    /// </summary>
    public const int SegundosMaximosPorTramo = 30 * 60;

    public async Task<Result> Handle(RegistrarTramoGestionCommand request, CancellationToken cancellationToken)
    {
        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        if (!parametros.MedicionTiempoActiva) return Result.Exito();

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("TiempoGestion.SinUsuario", "No pudimos identificar de quién es este tramo."));

        // Verificación de Id ajeno: el filtro de tenant ya descarta una conversación de
        // otro tenant, pero no basta — un hilo de un buzón personal ajeno también tiene
        // ClienteId null (mismo hallazgo que PR #190, AlcanceDeConversacionesYBuzonesTests
        // lo mecaniza). ConversacionVisibleAsync cubre las dos cosas: cartera del Cliente
        // Y, si el hilo está atado a un buzón personal, que sea el propio dueño.
        var conversacion = await comunicacionesContext.Conversaciones
            .Where(c => c.Id == request.ConversacionId)
            .Select(c => new { c.Id, c.ClienteId, c.ConexionIntegracionId })
            .FirstOrDefaultAsync(cancellationToken);

        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(conversacion.ClienteId, conversacion.ConexionIntegracionId, cancellationToken))
            return Result.Fallo(Error.Crear("TiempoGestion.ConversacionNoEncontrada", "No encontramos esta conversación."));

        var segundos = Math.Min(request.SegundosActivos, SegundosMaximosPorTramo);

        // Un tramo de cero segundos no aporta nada y ensucia las medias: abrir un hilo
        // y cerrarlo al instante no es tiempo de gestión.
        if (segundos == 0) return Result.Exito();

        try
        {
            repositorio.Agregar(new RegistroTiempoGestion(
                conversacion.Id, usuarioId.Value, conversacion.ClienteId,
                request.InicioUtc, request.FinUtc, segundos, request.Motivo));
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("TiempoGestion.Invalido", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
