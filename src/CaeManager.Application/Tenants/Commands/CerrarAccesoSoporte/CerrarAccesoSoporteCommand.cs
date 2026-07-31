using CaeManager.Application.Common;
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
public record CerrarAccesoSoporteCommand(Guid DelegacionTenantId) : IRequest<Result>;

public class CerrarAccesoSoporteCommandValidator : AbstractValidator<CerrarAccesoSoporteCommand>
{
    public CerrarAccesoSoporteCommandValidator()
    {
        RuleFor(c => c.DelegacionTenantId).NotEmpty();
    }
}

public class CerrarAccesoSoporteCommandHandler(
    IDelegacionTenantRepository repositorio,
    IApplicationDbContext dbContext,
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

        using (AmbitoTenantExplicito.Establecer(delegacion.TenantClienteId))
        {
            registroRepositorio.Agregar(new RegistroActividadSoporte(
                usuarioId.Value, delegacion.Id, TipoActividadSoporte.AccesoRevocado, "Cerrado manualmente."));

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Exito();
    }
}
