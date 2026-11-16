using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento.Subencargados;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Cumplimiento.Subencargados;

/// <summary>
/// Retira un subencargado (#2010, D-7): deja de admitir objeciones, y las exclusiones que dejaron sus objeciones
/// dejan de aplicarse porque ya nadie lo usa. Las filas de aviso y de objeción se conservan.
/// </summary>
[AreaDelEncargo("Cumplimiento")]
public record RetirarSubencargadoCommand(Guid SubencargadoId, string Motivo) : ICommand;

public class RetirarSubencargadoCommandValidator : AbstractValidator<RetirarSubencargadoCommand>
{
    public RetirarSubencargadoCommandValidator()
    {
        RuleFor(c => c.SubencargadoId).NotEmpty();
        RuleFor(c => c.Motivo).NotEmpty().MaximumLength(Subencargado.LongitudMaximaMotivo);
    }
}

public class RetirarSubencargadoCommandHandler(
    ICurrentUserService usuarioActual,
    ISesionPrivilegiadaActual sesionPrivilegiada,
    IAutorizacionAdminPlataforma autorizacion,
    ISubencargadoRepository repositorio,
    IUnitOfWork unitOfWork,
    TimeProvider reloj)
    : IRequestHandler<RetirarSubencargadoCommand, Result>
{
    public async Task<Result> Handle(RetirarSubencargadoCommand request, CancellationToken cancellationToken)
    {
        var actor = await AutorizacionSubencargados.ActorDePlataformaAsync(usuarioActual, sesionPrivilegiada, autorizacion, cancellationToken);
        if (actor.EsFallido)
            return actor;

        var subencargado = await repositorio.ObtenerAsync(request.SubencargadoId, cancellationToken);
        if (subencargado is null)
            return Result.Fallo(ErroresSubencargado.NoEncontrado);
        if (subencargado.Estado == EstadoSubencargado.Retirado)
            return Result.Fallo(ErroresSubencargado.YaRetirado);

        subencargado.Retirar(request.Motivo, reloj.GetUtcNow().UtcDateTime);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
