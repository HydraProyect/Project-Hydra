using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.ApiKeys.Commands.RevocarClaveApiDeMiOrganizacion;

/// <summary>
/// El Tenant propietario revoca una de sus claves de la API pública (decisión del 2026-10-28; OWASP
/// ASVS 4.0.3 V4.1.1 y V4.1.3). Autoridad en <see cref="AutoridadClavesApiDelTenant"/>. La clave se
/// busca en el Tenant actual, así que la de otro Tenant no existe aquí. Va exenta del gate
/// comercial: un impago no puede impedir cortar una credencial filtrada.
/// </summary>
public record RevocarClaveApiDeMiOrganizacionCommand(Guid ClaveApiId) : ICommand, IComandoQueReduceExposicion;

public class RevocarClaveApiDeMiOrganizacionCommandValidator : AbstractValidator<RevocarClaveApiDeMiOrganizacionCommand>
{
    public RevocarClaveApiDeMiOrganizacionCommandValidator()
    {
        RuleFor(c => c.ClaveApiId).NotEmpty();
    }
}

public class RevocarClaveApiDeMiOrganizacionCommandHandler(
    IClaveApiRepository claveRepositorio,
    ITenantActual tenantActual,
    ICurrentUserService currentUserService,
    IAdministradorDelTenantPropietario administradorDelTenant,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IUnitOfWork unitOfWork)
    : IRequestHandler<RevocarClaveApiDeMiOrganizacionCommand, Result>
{
    public async Task<Result> Handle(RevocarClaveApiDeMiOrganizacionCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadClavesApiDelTenant.PuedeGestionarAsync(tenantActual, currentUserService, administradorDelTenant, sesionPrivilegiadaActual, cancellationToken))
            return Result.Fallo(AutoridadClavesApiDelTenant.SoloAdministradorDelTenant);

        var clave = await claveRepositorio.ObtenerPorIdAsync(request.ClaveApiId, cancellationToken);
        if (clave is null)
            return Result.Fallo(Error.Crear("ClaveApi.NoEncontrada", "No encontramos esa clave."));

        clave.Revocar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
