using CaeManager.Application.Common;
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

        if (await repositorio.ExisteAsync(request.DelegacionTenantId, request.UsuarioId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "AsignacionOperadorDelegado.YaAsignado", "Este usuario ya está autorizado en esta delegación."));

        var asignacion = new AsignacionOperadorDelegado(request.DelegacionTenantId, request.UsuarioId, request.Rol);
        repositorio.Agregar(asignacion);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(asignacion.Id);
    }
}
