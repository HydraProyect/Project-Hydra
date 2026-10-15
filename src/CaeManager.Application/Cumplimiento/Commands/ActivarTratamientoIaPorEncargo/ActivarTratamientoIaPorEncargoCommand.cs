using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Cumplimiento.Commands.ActivarTratamientoIaPorEncargo;

/// <summary>
/// Activa el tratamiento de datos personales con IA (Nivel 0) en el Tenant
/// activo, por el Administrador del Operador CAE externo que lo administra por un
/// Encargo de administración pleno (decisión de Chris, 2026-10-15; opción 3 del
/// 2026-09-24). Resuelve el limbo de un Tenant propietario sin Administrador
/// propio: nadie podía activar el asistente.
///
/// <para>
/// <b>Tenant a Tenant, nunca la cartera:</b> no recibe Tenant; actúa sobre el
/// Tenant activo y solo si en él la persona administra por un encargo pleno. El
/// clic es técnico: la autoridad es la cláusula del contrato con la empresa
/// gestionada en que se apoya el encargo. La fila guarda el Actor real, el
/// Tenant propietario (su <c>TenantId</c>), las versiones de los textos
/// aceptados, el encargo y su cláusula.
/// </para>
///
/// <para>
/// Quién: rol efectivo Administrador elevado por ESE encargo pleno, sin
/// simulación y fuera de una Sesión Privilegiada. Una Dirección CAE elevada no
/// activa, y el Administrador propio del Tenant propietario o la plataforma
/// siguen por <c>RegistrarInstruccionTratamientoIaTenantPropietarioCommand</c>.
/// Cláusula pendiente de revisión legal: sin ella, no se usa con datos reales.
/// </para>
/// </summary>
public record ActivarTratamientoIaPorEncargoCommand(
    string VersionDpaAceptada,
    string VersionAnexoSubencargadosAceptada) : ICommand<Guid>;

public class ActivarTratamientoIaPorEncargoCommandValidator : AbstractValidator<ActivarTratamientoIaPorEncargoCommand>
{
    public ActivarTratamientoIaPorEncargoCommandValidator()
    {
        RuleFor(c => c.VersionDpaAceptada)
            .NotEmpty().WithMessage("Indica qué versión del DPA se acepta.")
            .MaximumLength(InstruccionTratamientoIaTenantPropietario.LongitudMaximaVersion);

        RuleFor(c => c.VersionAnexoSubencargadosAceptada)
            .NotEmpty().WithMessage("Indica qué versión del Anexo II de subencargados se acepta.")
            .MaximumLength(InstruccionTratamientoIaTenantPropietario.LongitudMaximaVersion);
    }
}

public class ActivarTratamientoIaPorEncargoCommandHandler(
    ITenantActual tenantActual,
    ICurrentUserService currentUserService,
    IActorAuditoria actorAuditoria,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IEncargoDeAdministracionActual encargoActual,
    IEncargosAdministracionQueryContext encargos,
    IInstruccionTratamientoIaTenantPropietarioRepository repositorio,
    IUnitOfWork unitOfWork,
    TimeProvider reloj)
    : IRequestHandler<ActivarTratamientoIaPorEncargoCommand, Result<Guid>>
{
    public static readonly Error NoAutorizado = Error.Crear(
        "InstruccionTratamientoIa.SinEncargoPleno",
        "Solo puede activar la IA de esta organización su Administrador o el Administrador del Operador CAE " +
        "externo que la administra por encargo, si no tiene Administrador propio.");

    public static readonly Error YaVigente = Error.Crear(
        "InstruccionTratamientoIa.YaVigente", "La IA ya está activada en esta organización.");

    public async Task<Result<Guid>> Handle(ActivarTratamientoIaPorEncargoCommand request, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } propietarioTenantId)
            return Result.Fallo<Guid>(NoAutorizado);

        var actor = await actorAuditoria.ObtenerAsync();
        if (actor.ActorRealUsuarioId is not { } actorReal || actor.UsuarioSimuladoId is not null)
            return Result.Fallo<Guid>(NoAutorizado);

        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not null
            || await currentUserService.ObtenerUsuarioActualIdAsync() != actorReal)
            return Result.Fallo<Guid>(NoAutorizado);

        if (await encargoActual.EncargoPlenoAsync() is not { } encargoId
            || await currentUserService.ObtenerRolEfectivoAsync() != "Administrador")
            return Result.Fallo<Guid>(NoAutorizado);

        // El encargo que eleva tiene que ser de ESTE Tenant propietario y seguir vigente: se relee
        // la fila, no se da por buena la señal.
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var clausula = await encargos.EncargosAdministracion
            .Where(e => e.Id == encargoId
                        && e.PropietarioTenantId == propietarioTenantId
                        && e.RetiradoEnUtc == null
                        && e.VigenciaDesde <= ahora
                        && (e.VigenciaHasta == null || ahora < e.VigenciaHasta))
            .Select(e => e.ClausulaContrato)
            .SingleOrDefaultAsync(cancellationToken);
        if (clausula is null)
            return Result.Fallo<Guid>(NoAutorizado);

        if (await repositorio.ObtenerVigenteAsync(propietarioTenantId, cancellationToken) is not null)
            return Result.Fallo<Guid>(YaVigente);

        var instruccion = InstruccionTratamientoIaTenantPropietario.PorEncargoDeAdministracion(
            request.VersionDpaAceptada, request.VersionAnexoSubencargadosAceptada, ahora,
            actorReal, encargoId, clausula);

        repositorio.Agregar(instruccion);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(instruccion.Id);
    }
}
