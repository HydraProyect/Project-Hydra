using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.ApiKeys.Commands.RevocarClaveApi;

/// <summary>
/// Revoca una clave de la API pública (OWASP ASVS 4.0.3 V4.1.1 y V4.1.3). La autoridad es
/// una concesión AdminPlataforma vigente sobre el Tenant propietario de la clave
/// (<see cref="IAutorizacionAdminPlataforma.PuedeSobreTenantAsync"/>), la misma que exige
/// <see cref="GenerarClaveApi.GenerarClaveApiCommand"/>, y nunca la pertenencia a un Tenant
/// marcado como plataforma: una marca de Tenant es del plano de Propiedad, no autoridad del
/// plano Plataforma. La delegación de Soporte acota sobre qué Tenant se actúa y la cuenta
/// tiene que ser de su organización de origen.
///
/// <para>
/// Revocar exige menos que emitir, a propósito: ni delegación vigente ni segundo factor.
/// Revocar reduce exposición en vez de crearla (ver el contrato de
/// <see cref="IAutorizacionAdminPlataforma"/>). El Tenant propietario revoca por su propia vía,
/// <c>RevocarClaveApiDeMiOrganizacionCommand</c>.
/// </para>
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
    IAutorizacionAdminPlataforma autorizacion,
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
        if (tenantOrigenId is null || delegacion.TenantConsultoraId != tenantOrigenId.Value)
            return Result.Fallo(Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación de soporte."));

        // El Tenant propietario de la clave: el lado delegante de la delegación de Soporte.
        var tenantPropietarioId = delegacion.TenantClienteId;

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("ClaveApi.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeSobreTenantAsync(usuarioId.Value, tenantPropietarioId, cancellationToken))
            return Result.Fallo(Error.Crear("ClaveApi.NoAutorizado", "No tienes autorización de plataforma sobre esta organización."));

        // La clave pertenece al Tenant propietario: sin este ámbito explícito el
        // filtro global la buscaría contra el Tenant de origen de la cuenta y no la
        // encontraría nunca, aunque exista.
        using (AmbitoTenantExplicito.Establecer(tenantPropietarioId))
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
