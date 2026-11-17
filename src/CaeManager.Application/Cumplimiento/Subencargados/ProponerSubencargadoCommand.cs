using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento.Subencargados;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Cumplimiento.Subencargados;

/// <summary>
/// Da de alta un subencargado nuevo en estado Propuesto (#2010, D-7). Todavía no avisa a nadie: el aviso lo envía
/// <see cref="AvisarCambioSubencargadoCommand"/>, y la activación exige ese aviso probado y el plazo.
/// </summary>
[AreaDelEncargo("Cumplimiento")]
public record ProponerSubencargadoCommand(
    string Nombre,
    string Finalidad,
    string UbicacionYTransferencia,
    bool AdmiteExclusionPorTenant,
    ConmutadorExclusionSubencargado? Conmutador) : ICommand<Guid>;

public class ProponerSubencargadoCommandValidator : AbstractValidator<ProponerSubencargadoCommand>
{
    public ProponerSubencargadoCommandValidator()
    {
        RuleFor(c => c.Nombre).NotEmpty().MaximumLength(Subencargado.LongitudMaximaNombre);
        RuleFor(c => c.Finalidad).NotEmpty().MaximumLength(Subencargado.LongitudMaximaFinalidad);
        RuleFor(c => c.UbicacionYTransferencia).NotEmpty().MaximumLength(Subencargado.LongitudMaximaUbicacion);
        RuleFor(c => c.Conmutador).IsInEnum();
        RuleFor(c => c.Conmutador)
            .Must((c, conmutador) => c.AdmiteExclusionPorTenant == conmutador.HasValue)
            .WithMessage("Un subencargado que admite exclusión por Tenant propietario nombra su conmutador, y uno que no la admite no lleva ninguno.");
    }
}

public class ProponerSubencargadoCommandHandler(
    ICurrentUserService usuarioActual,
    ISesionPrivilegiadaActual sesionPrivilegiada,
    IAutorizacionAdminPlataforma autorizacion,
    ISubencargadoRepository repositorio,
    IUnitOfWork unitOfWork,
    TimeProvider reloj)
    : IRequestHandler<ProponerSubencargadoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ProponerSubencargadoCommand request, CancellationToken cancellationToken)
    {
        var actor = await AutorizacionSubencargados.ActorDePlataformaAsync(usuarioActual, sesionPrivilegiada, autorizacion, cancellationToken);
        if (actor.EsFallido)
            return actor;

        var subencargado = new Subencargado(
            request.Nombre, request.Finalidad, request.UbicacionYTransferencia, request.AdmiteExclusionPorTenant,
            request.Conmutador, actor.Valor, reloj.GetUtcNow().UtcDateTime);
        repositorio.Agregar(subencargado);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito(subencargado.Id);
    }
}
