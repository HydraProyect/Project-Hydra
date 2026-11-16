using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.DerechosInteresado;
using CaeManager.Domain.Trabajadores;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.DerechosInteresado.Commands.RegistrarSolicitudDerechoInteresado;

/// <summary>
/// Registra en el Tenant actual una solicitud de un derecho del interesado (#2002, decisión D-3 del 2026-11-15).
/// La registra cualquier cuenta con escritura en el Tenant (lo impone <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>),
/// también un Gestor CAE de un Operador CAE externo; tramitarla y resolverla no (ver
/// <c>AutoridadCapacidadesTenant</c>). Si señala a un Trabajador, tiene que ser uno de la cartera de quien registra:
/// fuera de ella responde como si no existiera. El registro queda auditado como «Creado» con el actor real.
/// </summary>
[AreaDelEncargo("DerechosInteresado")]
public record RegistrarSolicitudDerechoInteresadoCommand(
    TipoDerechoInteresado Tipo,
    CanalEntradaSolicitudDerecho Canal,
    DateOnly FechaRecepcion,
    string NombreInteresado,
    Guid? TrabajadorId,
    string? Descripcion) : ICommand<Guid>;

public class RegistrarSolicitudDerechoInteresadoCommandValidator : AbstractValidator<RegistrarSolicitudDerechoInteresadoCommand>
{
    public RegistrarSolicitudDerechoInteresadoCommandValidator()
    {
        RuleFor(c => c.Tipo).IsInEnum().WithMessage("Indica qué derecho ejerce el interesado.");
        RuleFor(c => c.Canal).IsInEnum().WithMessage("Indica por dónde llegó la solicitud.");
        RuleFor(c => c.FechaRecepcion)
            .Must(f => f <= DiaDeNegocio.Hoy()).WithMessage("La fecha de recepción no puede ser posterior a hoy.");
        RuleFor(c => c.NombreInteresado)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Indica el nombre del interesado.")
            .Must(n => n is null || n.Trim().Length <= SolicitudDerechoInteresado.LongitudMaximaNombreInteresado)
            .WithMessage($"El nombre no puede superar {SolicitudDerechoInteresado.LongitudMaximaNombreInteresado} caracteres.");
        RuleFor(c => c.Descripcion)
            .Must(d => d is null || d.Trim().Length <= SolicitudDerechoInteresado.LongitudMaximaDescripcion)
            .WithMessage($"La descripción no puede superar {SolicitudDerechoInteresado.LongitudMaximaDescripcion} caracteres.");
        RuleFor(c => c.TrabajadorId)
            .Must(t => t != Guid.Empty).WithMessage("El trabajador indicado no es válido.")
            .When(c => c.TrabajadorId is not null);
    }
}

public class RegistrarSolicitudDerechoInteresadoCommandHandler(
    IActorAuditoria actorAuditoria,
    ITrabajadorRepository trabajadores,
    IAlcanceDatosService alcanceDatos,
    ISolicitudDerechoInteresadoRepository solicitudes,
    IUnitOfWork unitOfWork)
    : IRequestHandler<RegistrarSolicitudDerechoInteresadoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        RegistrarSolicitudDerechoInteresadoCommand request, CancellationToken cancellationToken)
    {
        if ((await actorAuditoria.ObtenerAsync()).ActorRealUsuarioId is not { } actorRealId)
            return Result.Fallo<Guid>(ErroresDerechosInteresado.SinCapacidad);

        if (request.TrabajadorId is { } trabajadorId)
        {
            var trabajador = await trabajadores.ObtenerPorIdAsync(trabajadorId, cancellationToken);
            if (trabajador is null || !await alcanceDatos.TrabajadorVisibleAsync(trabajador.Id, cancellationToken))
                return Result.Fallo<Guid>(ErroresDerechosInteresado.TrabajadorNoEncontrado);
        }

        SolicitudDerechoInteresado solicitud;
        try
        {
            solicitud = SolicitudDerechoInteresado.Registrar(
                request.Tipo, request.Canal, request.FechaRecepcion, request.NombreInteresado,
                request.TrabajadorId, request.Descripcion, actorRealId, DiaDeNegocio.Hoy(), DateTime.UtcNow);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo<Guid>(ErroresDerechosInteresado.DatosNoValidos(ex.Message));
        }

        solicitudes.Agregar(solicitud);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito(solicitud.Id);
    }
}
