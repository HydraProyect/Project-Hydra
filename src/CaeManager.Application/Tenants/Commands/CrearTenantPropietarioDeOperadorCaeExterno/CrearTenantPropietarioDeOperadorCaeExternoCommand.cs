using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Commands.CrearTenantPropietarioDeOperadorCaeExterno;

/// <summary>
/// Alta, por el Actor de Plataforma TALVEG, de un Tenant propietario nuevo
/// que un Operador CAE externo ya existente pasa a operar (decisión del
/// propietario, 2026-09-20: el alta del Operador y de sus Tenants la hace
/// TALVEG, no el propio Operador).
///
/// <b>Gemelo de <see cref="CrearClienteDelegante.CrearClienteDeleganteCommand"/> con una
/// diferencia que es todo el motivo de que exista:</b> aquel cuelga la
/// delegación del Tenant de <i>origen</i> de quien ejecuta — correcto cuando
/// quien crea es un usuario del propio Operador, incorrecto cuando quien crea
/// es TALVEG: la <see cref="DelegacionTenant"/> colgaría del Tenant de plataforma
/// y TALVEG pasaría a figurar como Operador CAE de un cliente que nunca
/// contrató su operación (cierra el UNKNOWN de la auditoría del caso fundador,
/// § 2 B2). Aquí el Operador se nombra <b>explícitamente</b> y se valida.
///
/// <b>Contrato de autorización</b> (los cuatro planos, ADR-011 § 1):
/// <list type="bullet">
/// <item><b>Plataforma</b>: <c>AdminPlataforma</c> con alcance <i>global</i> y
/// vigente (<see cref="IAutorizacionAdminPlataforma.PuedeGlobalmenteAsync"/>). Global y no acotada
/// porque el Tenant propietario todavía no existe: no hay nada a lo que
/// acotar, mismo motivo que <c>CrearClienteDeleganteCommand</c>. No pasa por
/// <see cref="IComandoDeAprovisionamiento"/>: no hay Sesión Privilegiada ni
/// elevación de rol de PostgreSQL, porque no escribe contenido CAE de ningún
/// Tenant ajeno — solo catálogos globales y filas del Tenant que ella misma crea.</item>
/// <item><b>Propiedad</b>: el Tenant nuevo es su propio Tenant propietario; nada se
/// deduce del Operador ni de quién crea (nace <c>ClienteDirecto</c>, sin
/// Empresa ni datos).</item>
/// <item><b>Operación</b>: la delegación nace <c>OperadorExterno</c> y abre la
/// operación delegada Operador → Tenant propietario. <b>No asigna a nadie como
/// Gestor CAE</b> — a diferencia de <c>CrearClienteDeleganteCommand</c>, el ejecutor
/// es un Actor de Plataforma y nunca es Gestor CAE ni Operador CAE (ADR-011 §
/// 1). Quién gestiona lo decide después una Asignación de Cartera del Operador.</item>
/// <item><b>Operador válido</b>: existe, tiene concedida
/// <see cref="Tenant.PuedeActuarComoOperadorCaeExterno"/> y no es el Tenant de
/// plataforma — TALVEG no es Operador CAE por defecto.</item>
/// </list>
///
/// <b>Auditoría</b>: <c>AuditoriaInterceptor</c> registra el alta de
/// <c>Tenant</c>, <c>DelegacionTenant</c> y <c>ParametroSistema</c> con
/// <c>ActorRealUsuarioId</c> del ejecutor (no hay usuario simulado: ni existe
/// impersonación ni el Tenant tiene usuarios).
/// </summary>
public record CrearTenantPropietarioDeOperadorCaeExternoCommand(Guid TenantOperadorId, string NombreTenantPropietario)
    : ICommand<Guid>;

public class CrearTenantPropietarioDeOperadorCaeExternoCommandValidator
    : AbstractValidator<CrearTenantPropietarioDeOperadorCaeExternoCommand>
{
    public CrearTenantPropietarioDeOperadorCaeExternoCommandValidator()
    {
        RuleFor(c => c.TenantOperadorId).NotEmpty().WithMessage("Selecciona el Operador CAE externo.");
        RuleFor(c => c.NombreTenantPropietario)
            .NotEmpty().WithMessage("El nombre del Tenant propietario es obligatorio.")
            .MaximumLength(Tenant.LongitudMaximaNombre);
    }
}

public class CrearTenantPropietarioDeOperadorCaeExternoCommandHandler(
    ITenantRepository tenantRepositorio,
    ITenantsQueryContext tenantsContext,
    IDelegacionTenantRepository vinculosRepositorio,
    IParametroSistemaRepository parametroSistemaRepositorio,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IAsignacionesOperativasWriter asignacionesWriter,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearTenantPropietarioDeOperadorCaeExternoCommand, Result<Guid>>
{
    /// <summary>Mismos valores por defecto que <c>CrearClienteDeleganteCommand</c>; se repiten porque Application no depende de Infrastructure.</summary>
    private const int UmbralAmbarDiasPorDefecto = 30;
    private const int UmbralRojoDiasPorDefecto = 15;

    public async Task<Result<Guid>> Handle(
        CrearTenantPropietarioDeOperadorCaeExternoCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<Guid>(Error.Crear("TenantPropietarioDeOperador.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        // La autoridad va antes que cualquier lectura: quien no puede no debe
        // poder distinguir, por el mensaje de error, qué Ids son Operadores reales.
        if (!await autorizacion.PuedeGlobalmenteAsync(usuarioId.Value, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "TenantPropietarioDeOperador.SinPermiso",
                "Solo la administración de plataforma puede dar de alta un Tenant propietario para un Operador CAE externo."));

        // Tenant es catálogo global (sin filtro de tenant): un Id de Tenant es válido cross-tenant por diseño.
        var operador = await tenantsContext.Tenants
            .Where(t => t.Id == request.TenantOperadorId)
            .Select(t => new { t.PuedeActuarComoOperadorCaeExterno, t.EsPlataforma })
            .SingleOrDefaultAsync(cancellationToken);

        if (operador is null || operador.EsPlataforma || !operador.PuedeActuarComoOperadorCaeExterno)
            return Result.Fallo<Guid>(Error.Crear(
                "TenantPropietarioDeOperador.OperadorNoValido", "No encontramos ese Operador CAE externo."));

        var nombreNormalizado = request.NombreTenantPropietario.Trim();
        if (await tenantRepositorio.ExisteConNombreAsync(nombreNormalizado, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("TenantPropietarioDeOperador.NombreDuplicado", "Ya existe un tenant con este nombre."));

        // ClienteDirecto: cómo el Tenant propietario se ve a sí mismo (una sola empresa gestionada).
        var tenantPropietario = new Tenant(nombreNormalizado, PerfilVocabularioTenant.ClienteDirecto);
        // Un único SaveChanges: Tenant, ParametroSistema, operación raíz, DelegacionTenant y
        // operación delegada se confirman en una sola transacción. Con dos guardados (como
        // CrearClienteDeleganteCommand) un fallo o una cancelación entre ambos dejaría un
        // Tenant propietario aprovisionado sin Operador CAE externo (hallazgo de Codex, alto).
        //
        // Ámbito explícito contra el Id del Tenant nuevo: la fila de ParametroSistema
        // y la operación raíz se sellan con SU TenantId, no con el del ejecutor.
        // DelegacionTenant es catálogo global: el ámbito no le afecta.
        using (AmbitoTenantExplicito.Establecer(tenantPropietario.Id))
        {
            tenantRepositorio.Agregar(tenantPropietario);
            parametroSistemaRepositorio.Agregar(new ParametroSistema(UmbralAmbarDiasPorDefecto, UmbralRojoDiasPorDefecto));
            await asignacionesWriter.AsegurarOperacionRaizAsync(
                tenantPropietario.Id, tenantPropietario.CreadoEnUtc, cancellationToken);

            var vinculo = new DelegacionTenant(request.TenantOperadorId, tenantPropietario.Id);
            vinculosRepositorio.Agregar(vinculo);
            await asignacionesWriter.AbrirOperacionDelegadaAsync(
                tenantPropietario.Id, request.TenantOperadorId, vinculo.CreadoEnUtc, vigenciaHasta: null, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Exito(tenantPropietario.Id);
    }
}
