using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Retencion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Configuracion.Commands.FijarPlazoRetencionDocumentos;

/// <summary>
/// Plazo de conservación de los Documentos del Tenant propietario actual, de 1 a 5 años (decisión del 2026-11-18).
/// Solo afecta a las propuestas de purga futuras: una solicitud abierta conserva su lista. Quién puede:
/// <see cref="AutoridadPlazoRetencionDocumentos"/>.
/// </summary>
[AreaDelEncargo("Configuracion")]
public record FijarPlazoRetencionDocumentosCommand(int Anios) : ICommand;

public class FijarPlazoRetencionDocumentosCommandValidator : AbstractValidator<FijarPlazoRetencionDocumentosCommand>
{
    public FijarPlazoRetencionDocumentosCommandValidator()
    {
        RuleFor(c => c.Anios)
            .InclusiveBetween(PlazoRetencionDocumentos.Minimo, PlazoRetencionDocumentos.Maximo)
            .WithMessage($"El plazo de conservación va de {PlazoRetencionDocumentos.Minimo} a {PlazoRetencionDocumentos.Maximo} años.");
    }
}

public class FijarPlazoRetencionDocumentosCommandHandler(
    ISesionPrivilegiadaActual sesionPrivilegiada,
    ITenantActual tenantActual,
    ICurrentUserService currentUserService,
    IAdministradorDelTenantPropietario administradorDelTenant,
    ITenantsQueryContext tenants,
    IParametroSistemaRepository repositorio,
    IUnitOfWork unitOfWork)
    : IRequestHandler<FijarPlazoRetencionDocumentosCommand, Result>
{
    public async Task<Result> Handle(FijarPlazoRetencionDocumentosCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadPlazoRetencionDocumentos.PuedeFijarAsync(
                sesionPrivilegiada, tenantActual, currentUserService, administradorDelTenant, tenants, cancellationToken))
            return Result.Fallo(AutoridadPlazoRetencionDocumentos.SoloAdministradorDelTenant);

        var parametros = await repositorio.ObtenerAsync(cancellationToken);
        if (parametros.TenantId != tenantActual.TenantId)
            return Result.Fallo(AutoridadPlazoRetencionDocumentos.SoloAdministradorDelTenant);

        try
        {
            parametros.FijarPlazoRetencionDocumentos(request.Anios);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("PlazoRetencion.Invalido", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
