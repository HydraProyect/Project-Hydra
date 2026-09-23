using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;

/// <summary>
/// Crea un Delegated Workspace: autoriza a la Consultora
/// <paramref name="TenantConsultoraId"/> a operar sobre el Cliente Delegante
/// <paramref name="TenantClienteId"/> (ADR-004 § 5.3). No concede acceso a
/// ningún usuario por sí sola — eso es <c>CrearAsignacionOperadorDelegadoCommand</c>.
///
/// <para>
/// <b>Incremento 1b del aprovisionamiento</b> (decisión del propietario, 2026-09-22,
/// opción 1′): es el comando con el que el Administrador de un Tenant propietario YA
/// existente autoriza a un Operador CAE externo desde <c>/delegaciones</c>. El Actor de
/// Plataforma TALVEG solo preselecciona el Operador con un enlace que no escribe nada;
/// el clic del Administrador es la instrucción documentada (RGPD art. 28) y queda en
/// la auditoría genérica con su propio Actor real. TALVEG no tiene camino para
/// ejecutarlo: la autorización exige pertenecer al Tenant propietario.
/// </para>
///
/// <para>
/// La parte que recibe el acceso tiene que ser un Operador CAE externo
/// (<see cref="OperadorCaeExternoElegible"/>), no cualquier Tenant existente: la pantalla
/// del 1b es la primera vía de la interfaz que deja elegirlo. Y un Tenant propietario
/// no puede tener dos Operadores CAE externos con la operación completa a la vez
/// (índice <c>IX_AsignacionesOperacion_DelegacionTotalVigente</c>); aquí se rechaza con
/// un mensaje en vez de dejar que la base lo corte con una excepción. Si hay que
/// sustituir al anterior, primero se revoca su vínculo.
/// </para>
/// </summary>
public record CrearDelegacionTenantCommand(Guid TenantConsultoraId, Guid TenantClienteId) : ICommand<Guid>;

public class CrearDelegacionTenantCommandValidator : AbstractValidator<CrearDelegacionTenantCommand>
{
    public CrearDelegacionTenantCommandValidator()
    {
        RuleFor(c => c.TenantConsultoraId).NotEmpty().WithMessage("Selecciona la Consultora.");
        RuleFor(c => c.TenantClienteId).NotEmpty().WithMessage("Selecciona el Cliente Delegante.");
        RuleFor(c => c)
            .Must(c => c.TenantConsultoraId != c.TenantClienteId)
            .WithMessage("Un tenant no puede delegarse acceso a sí mismo.");
    }
}

public class CrearDelegacionTenantCommandHandler(
    IDelegacionTenantRepository repositorio, ITenantsQueryContext tenantsContext,
    IAsignacionesOperativasWriter asignacionesWriter,
    IAutorizacionDelegacionTenant autorizacion, ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearDelegacionTenantCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearDelegacionTenantCommand request, CancellationToken cancellationToken)
    {
        // La autoridad va primero, antes incluso de comprobar que los tenants
        // existan: quien no puede gestionar estas delegaciones tampoco debería
        // poder averiguar, por la diferencia entre dos mensajes de error, qué
        // identificadores de tenant corresponden a organizaciones reales.
        //
        // Y la autoridad es del CLIENTE DELEGANTE, no de la Consultora ni de la
        // plataforma (ADR-004 § 12.2): quien concede acceso a unos datos es su
        // dueño. Que Hydra no pueda iniciar una delegación por su cuenta es § 11.1.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<Guid>(Error.Crear(
                "DelegacionTenant.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeGestionarDelegacionesAsync(
                usuarioId.Value, request.TenantClienteId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "DelegacionTenant.NoAutorizado",
                "Solo un administrador del Cliente Delegante puede autorizar el acceso a sus datos."));

        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        // Tenant es catálogo global (Entity, no EntidadConTenant): la consulta
        // no lleva filtro de tenant a propósito, un Id de Tenant es válido
        // cross-tenant por diseño (ADR-004).
        //
        // Y no basta con que exista: tiene que ser un Operador CAE externo. Un Tenant
        // propietario cualquiera, o el Tenant de plataforma, no reciben operación
        // delegada por esta vía (TALVEG no es Operador CAE por defecto, ADR-011 § 1).
        if (!await tenantsContext.Tenants
                .Where(t => t.Id == request.TenantConsultoraId)
                .AnyAsync(OperadorCaeExternoElegible.Predicado, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "DelegacionTenant.ConsultoraNoEncontrada", "No encontramos ese Operador CAE externo."));

        // El Cliente Delegante tampoco puede ser el Tenant de plataforma: TALVEG nunca
        // es Tenant propietario que delega operación a un Operador CAE externo (ADR-011
        // § 1). Defensa en profundidad — la consulta que resuelve el candidato
        // (AutorizarOperadorCaeExternoQueries) ya lo excluye, pero este comando es quien
        // escribe de verdad y no debe depender solo de que la pantalla se comporte
        // (hallazgo de Codex, alto, sobre el incremento 1b). Mismo mensaje que "no
        // existe": no hace falta que quien pregunta sepa que acertó el Id de plataforma.
        if (!await tenantsContext.Tenants
                .Where(t => t.Id == request.TenantClienteId && !t.EsPlataforma)
                .AnyAsync(cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("DelegacionTenant.ClienteNoEncontrado", "No encontramos ese Cliente Delegante."));

        if (await repositorio.ExisteActivaAsync(request.TenantConsultoraId, request.TenantClienteId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "DelegacionTenant.YaActiva", "Ya existe una delegación activa entre esta Consultora y este Cliente."));

        // Otro Operador CAE externo con la operación completa: la base ya lo
        // prohíbe (una sola delegación total vigente por Tenant propietario y
        // servicio). Se comprueba sobre el vínculo del propio Tenant propietario,
        // que quien llega aquí ya administra: no revela nada de terceros.
        if (await tenantsContext.DelegacionesTenant.AnyAsync(
                d => d.TenantClienteId == request.TenantClienteId
                     && d.TenantConsultoraId != request.TenantConsultoraId
                     && d.Activa
                     && d.Proposito == PropositoDelegacion.OperadorExterno,
                cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "DelegacionTenant.OtroOperadorVigente",
                "Tu organización ya tiene otro Operador CAE externo activo. Revoca su acceso antes de autorizar uno nuevo."));

        var delegacion = new DelegacionTenant(request.TenantConsultoraId, request.TenantClienteId);
        repositorio.Agregar(delegacion);

        // Doble escritura. El propietario de los datos es el Cliente Delegante
        // y el operador es la Consultora — el orden importa y es justo el que
        // encarna "delegación de acceso, no de propiedad".
        await asignacionesWriter.AbrirOperacionDelegadaAsync(
            request.TenantClienteId, request.TenantConsultoraId, delegacion.CreadoEnUtc, vigenciaHasta: null, cancellationToken);

        // La comprobación de OtroOperadorVigente de arriba no cierra la ventana
        // entre dos autorizaciones concurrentes sobre el mismo Tenant
        // propietario: el índice único IX_AsignacionesOperacion_
        // DelegacionTotalVigente sigue siendo la barrera real, y una carrera
        // perdedora sale como DbUpdateException sin traducir (hallazgo E de la
        // revisión puente del incremento 1b, Baja). NO se traduce aquí a
        // propósito: Application no puede depender de Npgsql.PostgresException
        // para distinguir esa carrera de otro DbUpdateException real —el que
        // lanza RLS cuando el workspace activo no es el Tenant propietario— sin
        // cruzar la frontera de capas (FronterasDeCapaTests). Un catch (DbUpdateException)
        // sin esa distinción tapaba ese segundo caso: Desde_el_workspace_de_otro_tenant_RLS_no_deja_escribir_la_operacion
        // dejó de ver la excepción que RLS lanza aposta. Traducirlo bien exige
        // mover la comprobación de ConstraintName a Infrastructure (mismo
        // patrón que OperacionImportacionRepository/ExpiracionAsignacionesHostedService) —
        // incremento aparte.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(delegacion.Id);
    }
}
