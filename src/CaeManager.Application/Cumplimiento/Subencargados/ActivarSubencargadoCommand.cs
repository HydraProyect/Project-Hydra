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
/// El guardado va con las altas de Tenant propietario cerradas (<see cref="ICerrojoAltasTenantPropietario"/>) y tras
/// releer la lista: una alta confirmada durante la comprobación la hace fallar, para que no quede ningún Tenant sin
/// aviso ni fuera del plazo.
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
    ITransaccionDeComando transaccion,
    ICerrojoAltasTenantPropietario cerrojo,
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

        // Fuera de la transacción: cada Tenant se lee en su ámbito, y dentro de una transacción el contexto RLS es el
        // de su BEGIN y no cambiaría al cambiar de ámbito.
        // shortcut: una consulta por Tenant propietario; vale mientras haya decenas de Tenants. Cuando sean
        // cientos, una función de solo lectura acotada al Actor de Plataforma TALVEG que devuelva los Tenants sin aviso.
        var comprobados = await repositorio.TenantsPropietariosAsync(cancellationToken);
        foreach (var tenantId in comprobados)
        {
            using var _ = AmbitoTenantExplicito.Establecer(tenantId);
            if (!await repositorio.TieneAvisoProbadoAsync(subencargado.Id, enviadoComoTarde, cancellationToken))
                sinAvisoProbado++;
        }

        if (subencargado.MotivoParaNoActivar(ahoraUtc, sinAvisoProbado) is { } motivo)
            return Result.Fallo(ErroresSubencargado.NoActivable(motivo));

        return await transaccion.EjecutarAsync(async ct =>
        {
            // Revisión Codex P2 de la PR #2274: con las altas cerradas, un Tenant propietario confirmado después de la
            // lectura de arriba aparece aquí y la activación se repite; uno que llegue después espera al Activo.
            await cerrojo.CerrarAsync(ct);
            var altasDuranteLaComprobacion = (await repositorio.TenantsPropietariosAsync(ct)).Except(comprobados);
            if (altasDuranteLaComprobacion.Any())
                return Result.Fallo(ErroresSubencargado.AltaDeTenantDuranteLaActivacion);

            // Releído dentro para el reintento: tras un fallo, la transacción limpia el seguimiento y el cuerpo repetido
            // parte del estado guardado, no del objeto que el intento anterior dejó activado. En el primer intento EF
            // devuelve la instancia ya seguida, sin refrescarla: si otro la cambió mientras tanto, lo para el token
            // Version al guardar (excepción de concurrencia, sin guardar nada).
            var aActivar = await repositorio.ObtenerAsync(request.SubencargadoId, ct);
            if (aActivar is null)
                return Result.Fallo(ErroresSubencargado.NoEncontrado);
            if (aActivar.MotivoParaNoActivar(ahoraUtc, sinAvisoProbado) is { } motivoDentro)
                return Result.Fallo(ErroresSubencargado.NoActivable(motivoDentro));

            aActivar.Activar(ahoraUtc, sinAvisoProbado, actor.Valor);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Exito();
        }, cancellationToken);
    }
}
