using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.ApiKeys.Commands.RevocarClaveApi;

/// <summary>
/// Revoca una clave de la API pública. Mismo criterio de autorización que
/// <see cref="GenerarClaveApi.GenerarClaveApiCommand"/> — ver ahí el porqué.
/// </summary>
public record RevocarClaveApiCommand(Guid DelegacionTenantId, Guid ClaveApiId) : ICommand;

public class RevocarClaveApiCommandValidator : AbstractValidator<RevocarClaveApiCommand>
{
    public RevocarClaveApiCommandValidator()
    {
        RuleFor(c => c.DelegacionTenantId).NotEmpty();
        RuleFor(c => c.ClaveApiId).NotEmpty();
    }
}

public class RevocarClaveApiCommandHandler(
    IDelegacionTenantRepository delegacionRepositorio,
    IClaveApiRepository claveRepositorio,
    ITenantsQueryContext dbContext,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<RevocarClaveApiCommand, Result>
{
    public async Task<Result> Handle(RevocarClaveApiCommand request, CancellationToken cancellationToken)
    {
        var delegacion = await delegacionRepositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);
        if (delegacion is null || delegacion.Proposito is not PropositoDelegacion.Soporte)
            return Result.Fallo(Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación de soporte."));

        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        var esPlataforma = tenantOrigenId is not null && await dbContext.Tenants
            .AnyAsync(t => t.Id == tenantOrigenId.Value && t.EsPlataforma, cancellationToken);

        if (!esPlataforma || delegacion.TenantConsultoraId != tenantOrigenId!.Value)
            return Result.Fallo(Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación de soporte."));

        // La clave pertenece al tenant CLIENTE: sin este ámbito explícito el
        // filtro global la buscaría contra el tenant plataforma y no la
        // encontraría nunca, aunque exista.
        using (AmbitoTenantExplicito.Establecer(delegacion.TenantClienteId))
        {
            var clave = await claveRepositorio.ObtenerPorIdAsync(request.ClaveApiId, cancellationToken);
            if (clave is null)
                return Result.Fallo(Error.Crear("ClaveApi.NoEncontrada", "No encontramos esa clave."));

            clave.Revocar();
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Exito();
    }
}
