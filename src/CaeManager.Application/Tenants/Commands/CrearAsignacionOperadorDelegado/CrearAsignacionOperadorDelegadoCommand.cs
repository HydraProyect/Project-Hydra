using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Commands.CrearAsignacionOperadorDelegado;

/// <summary>
/// Autoriza a un usuario del Operador CAE externo (Operador Delegado) a operar sobre
/// un Tenant beneficiario delegado ya creado, con un rol guardado en la fila de la
/// delegación (ADR-004 § 5.3). Ese rol solo rige por la vía heredada (acceso de
/// soporte): por la vía de Asignación de Operación el rol efectivo es el perfil de
/// origen del usuario, el mismo en todos los Tenants que su organización opera
/// (decisión Chris 2026-10-20).
///
/// <para>
/// <b>Sin consumidor en la interfaz, y a propósito (FS-20, 2026-10-08).</b> Exige a la vez
/// que quien actúa administre el Tenant propietario y que la persona elegida sea visible
/// en el Tenant activo y pertenezca al Operador CAE externo: para la primera persona de
/// una delegación eso no se cumple, porque el Tenant propietario no ve las cuentas del
/// Operador CAE externo. La vía que usa el producto es la Asignación de Cartera que decide
/// el Operador CAE externo (<c>ICatalogoIncorporacionCartera.IncorporarAsync</c>), que
/// escribe esta misma fila. Conectarlo a una pantalla pediría antes decidir si el Tenant
/// propietario puede buscar personal de otro Tenant.
/// </para>
/// </summary>
public record CrearAsignacionOperadorDelegadoCommand(Guid DelegacionTenantId, Guid UsuarioId, string Rol) : ICommand<Guid>;

public class CrearAsignacionOperadorDelegadoCommandValidator : AbstractValidator<CrearAsignacionOperadorDelegadoCommand>
{
    // Códigos de Roles.cs en CaeManager.Infrastructure.Identity, repetidos
    // aquí a propósito — Application no puede referenciar Infrastructure.Identity
    // sin invertir la dependencia entre capas (mismo criterio que
    // AutorizacionEscrituraBehavior). Administrador y DireccionCae quedan
    // fuera de la fila delegada: la vía heredada (acceso de soporte) nunca da
    // un rol de Propiedad. Por la vía de Asignación de Operación sí puede
    // tenerlo, pero no lo guarda esta fila: es el perfil de origen (decisión
    // Chris 2026-10-20).
    private static readonly string[] RolesValidosParaOperadorDelegado =
        ["CoordinadorCae", "GestorCae", "Consulta"];

    public CrearAsignacionOperadorDelegadoCommandValidator()
    {
        RuleFor(c => c.DelegacionTenantId).NotEmpty().WithMessage("Selecciona la delegación.");
        RuleFor(c => c.UsuarioId).NotEmpty().WithMessage("Selecciona el Operador Delegado.");
        RuleFor(c => c.Rol)
            .NotEmpty().WithMessage("Selecciona un rol.")
            .Must(rol => RolesValidosParaOperadorDelegado.Contains(rol))
            .WithMessage("Ese rol no está disponible para un Operador Delegado.");
    }
}

public class CrearAsignacionOperadorDelegadoCommandHandler(
    IAsignacionOperadorDelegadoRepository repositorio,
    IDelegacionTenantRepository delegacionRepositorio,
    IDirectorioUsuariosService directorioUsuarios,
    IAsignacionesOperativasWriter asignacionesWriter,
    IAutorizacionDelegacionTenant autorizacion, ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork, ITenantsQueryContext tenantsContext, ITenantActual tenantActual)
    : IRequestHandler<CrearAsignacionOperadorDelegadoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearAsignacionOperadorDelegadoCommand request, CancellationToken cancellationToken)
    {
        var delegacion = await delegacionRepositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);
        if (delegacion is null)
            return Result.Fallo<Guid>(Error.Crear("AsignacionOperadorDelegado.DelegacionNoEncontrada", "No encontramos esa delegación."));

        // La misma autoridad que crear la delegación, y por el mismo motivo:
        // por la vía heredada (acceso de soporte) esta operación decide QUÉ
        // PERSONA concreta va a poder entrar en los datos del Cliente Delegante,
        // y con qué rol. Por la vía de Asignación de Operación ya no lo decide
        // (decisión Chris 2026-10-20): entra el perfil de organización del
        // Operador CAE externo, y el Gestor CAE por su cartera. Que la delegación ya
        // exista autoriza a la Consultora como organización, no a cualquiera a
        // repartir accesos dentro de ella.
        //
        // Se comprueba contra delegacion.TenantClienteId —el dueño de los
        // datos— y no contra TenantConsultoraId: la autoridad es de quien
        // concede, no de quien recibe (ADR-004 § 12.2).
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<Guid>(Error.Crear(
                "AsignacionOperadorDelegado.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        var tenantPropietarioId = delegacion.TenantClienteId;
        if (!await autorizacion.PuedeGestionarDelegacionesAsync(
                usuarioId.Value, tenantPropietarioId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "AsignacionOperadorDelegado.NoAutorizado",
                "Solo un administrador de la organización que delega puede autorizar a un operador sobre sus datos."));

        // Cuarto sitio del mismo hallazgo de Codex sobre el incremento 1b (ver
        // ReactivarDelegacionTenantCommand y AutorizarOperadorCaeExternoQueries):
        // PuedeGestionarDelegacionesAsync no excluye por sí sola el Tenant de
        // plataforma como TenantClienteId — si esta delegación fuera una fila
        // heredada con TALVEG como Cliente Delegante, su Administrador inicial
        // pasaría la autoridad de forma reflexiva. TALVEG nunca es Tenant
        // propietario de un Operador CAE externo (ADR-011 § 1). Se corta
        // DESPUÉS de confirmar la autoridad, con el mismo mensaje que la
        // denegación de arriba: no revela si la delegación existe.
        var esPlataforma = await tenantsContext.Tenants
            .Where(t => t.Id == delegacion.TenantClienteId)
            .Select(t => t.EsPlataforma)
            .SingleOrDefaultAsync(cancellationToken);
        if (esPlataforma)
            return Result.Fallo<Guid>(Error.Crear(
                "AsignacionOperadorDelegado.NoAutorizado",
                "Solo un administrador de la organización que delega puede autorizar a un operador sobre sus datos."));

        // La fila solo se escribe con un extremo de la delegación como Tenant activo
        // (extremos_de_la_delegacion). Esta guarda solo restringe: la autoridad ya se
        // decidió arriba contra el Tenant propietario.
        var tenantActivo = tenantActual.TenantId;
        if (tenantActivo != tenantPropietarioId && tenantActivo != delegacion.TenantConsultoraId)
            return Result.Fallo<Guid>(Error.Crear(
                "AsignacionOperadorDelegado.NoAutorizado",
                "Para autorizar a esta persona, vuelve a tu organización."));

        // La de un Operador CAE externo abre además una cartera, que es dato del Tenant
        // propietario: posicion_en_la_asignacion solo la deja escribir con él activo y,
        // con el Operador CAE externo activo y el origen en el Tenant propietario, ni
        // siquiera deja localizar la Asignación de Operación. Se rechaza aquí en vez de
        // acabar en una excepción de AbrirCarteraOperadorAsync.
        var deOperadorExterno = delegacion.Proposito == PropositoDelegacion.OperadorExterno;
        if (deOperadorExterno && tenantActivo != tenantPropietarioId)
            return Result.Fallo<Guid>(Error.Crear(
                "AsignacionOperadorDelegado.RequiereTenantPropietario",
                "Para autorizar a esta persona, vuelve a tu organización."));

        if (!delegacion.Activa)
            return Result.Fallo<Guid>(Error.Crear("AsignacionOperadorDelegado.DelegacionInactiva", "Esta delegación está desactivada."));

        // Verificación de Ids ajenos — ver P0-1 de Project-Hydra-Negocio/MATURITY_REVIEW.md.
        // AsignacionOperadorDelegado.UsuarioId apunta a AspNetUsers, que
        // Application no puede referenciar directamente (ver IDirectorioUsuariosService).
        if (!await directorioUsuarios.EsVisibleEnTenantActualAsync(request.UsuarioId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("AsignacionOperadorDelegado.UsuarioNoEncontrado", "No encontramos ese usuario."));

        // El invariante de la cadena de autorización: quien opera pertenece al
        // tenant operador. La comprobación de visibilidad de arriba NO basta —
        // da por buenos también a los usuarios del tenant propietario, y
        // concederle a uno de ellos una cartera externa le daría, dentro de su
        // PROPIO workspace, el alcance de esa cartera.
        var tenantDelUsuario = await directorioUsuarios.ObtenerTenantDeUsuarioAsync(request.UsuarioId, cancellationToken);
        if (tenantDelUsuario != delegacion.TenantConsultoraId)
            return Result.Fallo<Guid>(Error.Crear(
                "AsignacionOperadorDelegado.UsuarioDeOtroTenant",
                "Ese usuario no pertenece a la organización que opera esta delegación."));

        if (await repositorio.ExisteAsync(request.DelegacionTenantId, request.UsuarioId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "AsignacionOperadorDelegado.YaAsignado", "Este usuario ya está autorizado en esta delegación."));

        var asignacion = new AsignacionOperadorDelegado(request.DelegacionTenantId, request.UsuarioId, request.Rol);
        repositorio.Agregar(asignacion);

        if (deOperadorExterno)
            await asignacionesWriter.AbrirCarteraOperadorAsync(
                delegacion.TenantClienteId, delegacion.TenantConsultoraId,
                request.UsuarioId, request.Rol, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(asignacion.Id);
    }
}
