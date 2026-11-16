using CaeManager.Application.CapacidadesTenant;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.DerechosInteresado;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.DerechosInteresado.Commands.TramitarSolicitudDerechoInteresado;

// Los cinco pasos de tramitación de una SolicitudDerechoInteresado. Todos exigen la capacidad «Derechos del
// interesado» en el Tenant actual (PasoSolicitudDerechoInteresado) y están fuera del Encargo de administración
// (ActosExcluidosDelEncargo.PeticionesExcluidas). Los invariantes de cada paso viven en el dominio.

/// <summary>Recibida → IdentidadVerificada.</summary>
[AreaDelEncargo("DerechosInteresado")]
public record VerificarIdentidadSolicitudDerechoCommand(Guid SolicitudId) : ICommand;

/// <summary>IdentidadVerificada → EnCurso.</summary>
[AreaDelEncargo("DerechosInteresado")]
public record IniciarTramitacionSolicitudDerechoCommand(Guid SolicitudId) : ICommand;

/// <summary>La única prórroga, con motivo escrito; no cambia el estado.</summary>
[AreaDelEncargo("DerechosInteresado")]
public record ProrrogarSolicitudDerechoCommand(Guid SolicitudId, DateOnly NuevoPlazo, string Motivo) : ICommand;

/// <summary>EnCurso → Resuelta, con el resultado opcional.</summary>
[AreaDelEncargo("DerechosInteresado")]
public record ResolverSolicitudDerechoCommand(Guid SolicitudId, string? Resultado) : ICommand;

/// <summary>Cualquier estado abierto → Denegada, con motivo escrito.</summary>
[AreaDelEncargo("DerechosInteresado")]
public record DenegarSolicitudDerechoCommand(Guid SolicitudId, string Motivo) : ICommand;

public class ProrrogarSolicitudDerechoCommandValidator : AbstractValidator<ProrrogarSolicitudDerechoCommand>
{
    public ProrrogarSolicitudDerechoCommandValidator()
    {
        RuleFor(c => c.SolicitudId).NotEmpty();
        RuleFor(c => c.Motivo)
            .Must(m => !string.IsNullOrWhiteSpace(m)).WithMessage("Prorrogar el plazo exige un motivo escrito.")
            .Must(m => m is null || m.Trim().Length <= SolicitudDerechoInteresado.LongitudMaximaMotivo)
            .WithMessage($"El motivo no puede superar {SolicitudDerechoInteresado.LongitudMaximaMotivo} caracteres.");
    }
}

public class DenegarSolicitudDerechoCommandValidator : AbstractValidator<DenegarSolicitudDerechoCommand>
{
    public DenegarSolicitudDerechoCommandValidator()
    {
        RuleFor(c => c.SolicitudId).NotEmpty();
        RuleFor(c => c.Motivo)
            .Must(m => !string.IsNullOrWhiteSpace(m)).WithMessage("Denegar la solicitud exige un motivo escrito.")
            .Must(m => m is null || m.Trim().Length <= SolicitudDerechoInteresado.LongitudMaximaMotivo)
            .WithMessage($"El motivo no puede superar {SolicitudDerechoInteresado.LongitudMaximaMotivo} caracteres.");
    }
}

public class ResolverSolicitudDerechoCommandValidator : AbstractValidator<ResolverSolicitudDerechoCommand>
{
    public ResolverSolicitudDerechoCommandValidator()
    {
        RuleFor(c => c.SolicitudId).NotEmpty();
        RuleFor(c => c.Resultado)
            .Must(r => r is null || r.Trim().Length <= SolicitudDerechoInteresado.LongitudMaximaMotivo)
            .WithMessage($"El resultado no puede superar {SolicitudDerechoInteresado.LongitudMaximaMotivo} caracteres.");
    }
}

public class TramitarSolicitudDerechoInteresadoCommandHandler(
    IAutoridadCapacidadesTenant autoridad,
    IActorAuditoria actorAuditoria,
    ISolicitudDerechoInteresadoRepository solicitudes,
    IUnitOfWork unitOfWork,
    TimeProvider reloj)
    : IRequestHandler<VerificarIdentidadSolicitudDerechoCommand, Result>,
      IRequestHandler<IniciarTramitacionSolicitudDerechoCommand, Result>,
      IRequestHandler<ProrrogarSolicitudDerechoCommand, Result>,
      IRequestHandler<ResolverSolicitudDerechoCommand, Result>,
      IRequestHandler<DenegarSolicitudDerechoCommand, Result>
{
    public Task<Result> Handle(VerificarIdentidadSolicitudDerechoCommand request, CancellationToken cancellationToken) =>
        Dar(request.SolicitudId, (s, actor, ahora) => s.VerificarIdentidad(actor, ahora), cancellationToken);

    public Task<Result> Handle(IniciarTramitacionSolicitudDerechoCommand request, CancellationToken cancellationToken) =>
        Dar(request.SolicitudId, (s, actor, ahora) => s.IniciarTramitacion(actor, ahora), cancellationToken);

    public Task<Result> Handle(ProrrogarSolicitudDerechoCommand request, CancellationToken cancellationToken) =>
        Dar(request.SolicitudId, (s, actor, ahora) => s.Prorrogar(request.NuevoPlazo, request.Motivo, actor, ahora),
            cancellationToken);

    public Task<Result> Handle(ResolverSolicitudDerechoCommand request, CancellationToken cancellationToken) =>
        Dar(request.SolicitudId, (s, actor, ahora) => s.Resolver(request.Resultado, actor, ahora), cancellationToken);

    public Task<Result> Handle(DenegarSolicitudDerechoCommand request, CancellationToken cancellationToken) =>
        Dar(request.SolicitudId, (s, actor, ahora) => s.Denegar(request.Motivo, actor, ahora), cancellationToken);

    private Task<Result> Dar(
        Guid solicitudId, Action<SolicitudDerechoInteresado, Guid, DateTime> paso, CancellationToken cancellationToken) =>
        PasoSolicitudDerechoInteresado.DarAsync(
            autoridad, actorAuditoria, solicitudes, unitOfWork, reloj, solicitudId, paso, cancellationToken);
}
