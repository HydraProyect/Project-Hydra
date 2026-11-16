using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento.Subencargados;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Cumplimiento.Subencargados;

/// <summary>
/// Activa un subencargado avisado (#2010, D-7). Falla, y el error dice por qué, si no han pasado
/// <see cref="Subencargado.PlazoMinimoAviso"/> desde el aviso o si algún Tenant propietario no tiene un aviso
/// <see cref="FaseAvisoCambioSubencargado.Enviado"/> de hace al menos ese plazo: un envío Preparado o NoConfirmado no
/// cuenta. Cada Tenant se comprueba en su propio ámbito, con su filtro y su RLS: ninguna lectura cruza Tenants.
/// </summary>
[AreaDelEncargo("Cumplimiento")]
public record ActivarSubencargadoCommand(Guid SubencargadoId) : ICommand;

public class ActivarSubencargadoCommandValidator : AbstractValidator<ActivarSubencargadoCommand>
{
    public ActivarSubencargadoCommandValidator() => RuleFor(c => c.SubencargadoId).NotEmpty();
}

public class ActivarSubencargadoCommandHandler(
    ICurrentUserService usuarioActual,
    ISesionPrivilegiadaActual sesionPrivilegiada,
    IAutorizacionAdminPlataforma autorizacion,
    ISubencargadoRepository repositorio,
    IUnitOfWork unitOfWork,
    TimeProvider reloj)
    : IRequestHandler<ActivarSubencargadoCommand, Result>
{
    public async Task<Result> Handle(ActivarSubencargadoCommand request, CancellationToken cancellationToken)
    {
        var actor = await AutorizacionSubencargados.ActorDePlataformaAsync(usuarioActual, sesionPrivilegiada, autorizacion, cancellationToken);
        if (actor.EsFallido)
            return actor;

        var subencargado = await repositorio.ObtenerAsync(request.SubencargadoId, cancellationToken);
        if (subencargado is null)
            return Result.Fallo(ErroresSubencargado.NoEncontrado);

        var ahoraUtc = reloj.GetUtcNow().UtcDateTime;
        var enviadoComoTarde = ahoraUtc - Subencargado.PlazoMinimoAviso;
        var sinAvisoProbado = 0;

        // shortcut: una consulta por Tenant propietario; vale mientras haya decenas de Tenants. Cuando sean
        // cientos, una función de solo lectura acotada al Actor de Plataforma TALVEG que devuelva los Tenants sin aviso.
        foreach (var tenantId in await repositorio.TenantsPropietariosAsync(cancellationToken))
        {
            using var _ = AmbitoTenantExplicito.Establecer(tenantId);
            if (!await repositorio.TieneAvisoProbadoAsync(subencargado.Id, enviadoComoTarde, cancellationToken))
                sinAvisoProbado++;
        }

        if (subencargado.MotivoParaNoActivar(ahoraUtc, sinAvisoProbado) is { } motivo)
            return Result.Fallo(ErroresSubencargado.NoActivable(motivo));

        subencargado.Activar(ahoraUtc, sinAvisoProbado, actor.Valor);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
