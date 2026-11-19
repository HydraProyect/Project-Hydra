using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Retencion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.TiposDocumento.Commands.FijarPlazoRetencionTipoDocumento;

/// <summary>
/// Fija o quita (<paramref name="Anios"/> nulo: hereda el del Tenant propietario) el plazo de conservación propio de
/// un tipo de documento del Tenant propietario actual (decisión del 2026-11-18). Solo afecta a las propuestas de purga
/// futuras. Quién puede: <see cref="AutoridadPlazoRetencionDocumentos"/>.
/// </summary>
[AreaDelEncargo("TiposDocumento")]
public record FijarPlazoRetencionTipoDocumentoCommand(Guid TipoDocumentoId, int? Anios) : ICommand;

public class FijarPlazoRetencionTipoDocumentoCommandValidator : AbstractValidator<FijarPlazoRetencionTipoDocumentoCommand>
{
    public FijarPlazoRetencionTipoDocumentoCommandValidator()
    {
        RuleFor(c => c.TipoDocumentoId).NotEmpty();
        RuleFor(c => c.Anios)
            .InclusiveBetween(PlazoRetencionDocumentos.Minimo, PlazoRetencionDocumentos.Maximo)
            .When(c => c.Anios is not null)
            .WithMessage($"El plazo de conservación va de {PlazoRetencionDocumentos.Minimo} a {PlazoRetencionDocumentos.Maximo} años.");
    }
}

public class FijarPlazoRetencionTipoDocumentoCommandHandler(
    ISesionPrivilegiadaActual sesionPrivilegiada,
    ITenantActual tenantActual,
    ICurrentUserService currentUserService,
    IAdministradorDelTenantPropietario administradorDelTenant,
    ITenantsQueryContext tenants,
    ITipoDocumentoRepository repositorio,
    IUnitOfWork unitOfWork)
    : IRequestHandler<FijarPlazoRetencionTipoDocumentoCommand, Result>
{
    public static readonly Error NoEncontrado = Error.Crear("TipoDocumento.NoEncontrado", "No encontramos ese tipo de documento.");

    public async Task<Result> Handle(FijarPlazoRetencionTipoDocumentoCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadPlazoRetencionDocumentos.PuedeFijarAsync(
                sesionPrivilegiada, tenantActual, currentUserService, administradorDelTenant, tenants, cancellationToken))
            return Result.Fallo(AutoridadPlazoRetencionDocumentos.SoloAdministradorDelTenant);

        // El filtro de Tenant ya oculta los tipos de otro Tenant propietario; se repite a mano, y con la misma
        // respuesta que un tipo inexistente, para no confirmar que existe.
        var tipo = await repositorio.ObtenerPorIdAsync(request.TipoDocumentoId, cancellationToken);
        if (tipo is null || tipo.TenantId != tenantActual.TenantId)
            return Result.Fallo(NoEncontrado);

        try
        {
            tipo.FijarPlazoRetencionDocumentos(request.Anios);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("PlazoRetencion.Invalido", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
