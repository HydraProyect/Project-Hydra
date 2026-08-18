using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Tenants.Commands.CrearAsignacionOperadorDelegado;

/// <summary>
/// Autoriza a un usuario de la Consultora (Operador Delegado) a operar sobre
/// un Delegated Workspace ya creado, con un rol concreto para ese cliente
/// (ADR-004 § 5.3 — un mismo usuario puede tener roles distintos en
/// delegaciones distintas).
/// </summary>
public record CrearAsignacionOperadorDelegadoCommand(Guid DelegacionTenantId, Guid UsuarioId, string Rol) : ICommand<Guid>;

public class CrearAsignacionOperadorDelegadoCommandValidator : AbstractValidator<CrearAsignacionOperadorDelegadoCommand>
{
    // Códigos de Roles.cs en CaeManager.Infrastructure.Identity, repetidos
    // aquí a propósito — Application no puede referenciar Infrastructure.Identity
    // sin invertir la dependencia entre capas (mismo criterio que
    // AutorizacionEscrituraBehavior). Administrador y DireccionCae quedan
    // fuera a propósito: un Operador Delegado opera dentro del alcance del
    // Delegated Workspace, nunca con privilegios de administración de la
    // plataforma del cliente.
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
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearAsignacionOperadorDelegadoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearAsignacionOperadorDelegadoCommand request, CancellationToken cancellationToken)
    {
        var delegacion = await delegacionRepositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);
        if (delegacion is null)
            return Result.Fallo<Guid>(Error.Crear("AsignacionOperadorDelegado.DelegacionNoEncontrada", "No encontramos esa delegación."));

        if (!delegacion.Activa)
            return Result.Fallo<Guid>(Error.Crear("AsignacionOperadorDelegado.DelegacionInactiva", "Esta delegación está desactivada."));

        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
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

        if (delegacion.Proposito == PropositoDelegacion.Comercial)
            await asignacionesWriter.AbrirCarteraOperadorAsync(
                delegacion.TenantClienteId, delegacion.TenantConsultoraId,
                request.UsuarioId, request.Rol, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(asignacion.Id);
    }
}
