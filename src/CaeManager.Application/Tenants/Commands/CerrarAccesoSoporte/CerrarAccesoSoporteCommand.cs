using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Commands.CerrarAccesoSoporte;

/// <summary>
/// Cierra la ventana antes de que caduque sola — lo normal al terminar de
/// atender la incidencia. El acceso se corta en la siguiente petición del
/// operador, no en la siguiente sesión (ver
/// <c>RevalidacionClienteActivoMiddleware</c>).
/// </summary>
public record CerrarAccesoSoporteCommand(Guid DelegacionTenantId) : ICommand;

public class CerrarAccesoSoporteCommandValidator : AbstractValidator<CerrarAccesoSoporteCommand>
{
    public CerrarAccesoSoporteCommandValidator()
    {
        RuleFor(c => c.DelegacionTenantId).NotEmpty();
    }
}

public class CerrarAccesoSoporteCommandHandler(
    IDelegacionTenantRepository repositorio,
    IAsignacionOperadorDelegadoRepository asignacionRepositorio,
    ITenantsQueryContext dbContext,
    IRegistroActividadSoporteRepository registroRepositorio,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CerrarAccesoSoporteCommand, Result>
{
    public async Task<Result> Handle(CerrarAccesoSoporteCommand request, CancellationToken cancellationToken)
    {
        var delegacion = await repositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);
        if (delegacion is null || delegacion.Proposito is not PropositoDelegacion.Soporte)
            return Result.Fallo(Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación de soporte."));

        // Mismo criterio de autorización que abrir — ver ese handler.
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        var esPlataforma = tenantOrigenId is not null && await dbContext.Tenants
            .AnyAsync(t => t.Id == tenantOrigenId.Value && t.EsPlataforma, cancellationToken);

        if (!esPlataforma || delegacion.TenantConsultoraId != tenantOrigenId!.Value)
            return Result.Fallo(Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación de soporte."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Soporte.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        delegacion.Desactivar();

        // Se retira también la asignación del operador. Desactivar la
        // delegación ya corta el acceso, pero dejar la autorización personal
        // viva haría que reabrir la ventana concediera de nuevo el rol
        // anterior sin que nadie lo eligiera.
        var asignacion = await asignacionRepositorio
            .ObtenerPorDelegacionYUsuarioAsync(delegacion.Id, usuarioId.Value, cancellationToken);

        if (asignacion is not null)
            asignacionRepositorio.Eliminar(asignacion);

        using (AmbitoTenantExplicito.Establecer(delegacion.TenantClienteId))
        {
            registroRepositorio.Agregar(RegistroActividadSoporte.PorDelegacion(
                usuarioId.Value, delegacion.Id, TipoActividadSoporte.AccesoRevocado, "Cerrado manualmente."));

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Exito();
    }
}
