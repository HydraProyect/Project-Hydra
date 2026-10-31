using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Configuracion.Commands.ActualizarSeguridadAcceso;

/// <summary>
/// Plazo de inactividad y exigencia de segundo factor del Tenant actual (decisión del 2026-10-28;
/// ASVS 4.0.3 V3.3.2, V2.8). Va aparte del resto de <c>/configuracion</c> porque solo lo puede
/// cambiar el Administrador del Tenant (<see cref="AutoridadSeguridadAcceso"/>). Las sesiones lo
/// reciben en la siguiente revalidación de su sello, en un minuto como mucho.
/// </summary>
public record ActualizarSeguridadAccesoCommand(int MinutosInactividadSesion, bool ExigeSegundoFactorTodosLosPerfiles) : ICommand;

public class ActualizarSeguridadAccesoCommandValidator : AbstractValidator<ActualizarSeguridadAccesoCommand>
{
    public ActualizarSeguridadAccesoCommandValidator()
    {
        RuleFor(c => c.MinutosInactividadSesion)
            .InclusiveBetween(ParametroSistema.MinimoMinutosInactividadSesion, ParametroSistema.MaximoMinutosInactividadSesion)
            .WithMessage($"Introduce un valor entre {ParametroSistema.MinimoMinutosInactividadSesion} y {ParametroSistema.MaximoMinutosInactividadSesion} minutos.");
    }
}

public class ActualizarSeguridadAccesoCommandHandler(
    ITenantActual tenantActual,
    ICurrentUserService currentUserService,
    IAdministradorDelTenantPropietario administradorDelTenant,
    ITenantsQueryContext tenants,
    IParametroSistemaRepository repositorio,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ActualizarSeguridadAccesoCommand, Result>
{
    public async Task<Result> Handle(ActualizarSeguridadAccesoCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSeguridadAcceso.PuedeEditarAsync(
                tenantActual, currentUserService, administradorDelTenant, tenants, cancellationToken))
            return Result.Fallo(AutoridadSeguridadAcceso.SoloAdministradorDelTenant);

        var parametros = await repositorio.ObtenerAsync(cancellationToken);

        try
        {
            parametros.ActualizarSeguridadAcceso(request.MinutosInactividadSesion, request.ExigeSegundoFactorTodosLosPerfiles);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("ParametroSistema.Invalido", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
