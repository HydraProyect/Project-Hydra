using System.Security.Cryptography;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.ApiKeys.Commands.GenerarClaveApi;

/// <summary>
/// Emite una clave de la API pública (P3-29) para el tenant cliente de una
/// delegación de soporte — mismo criterio de autorización que
/// <c>AbrirAccesoSoporteCommand</c>: solo el Administrador del tenant
/// plataforma, sobre una delegación de propósito Soporte que lo autorice
/// sobre ese cliente. Ningún tenant se autogestiona esto en v1 (decisión de
/// alcance de P3-29).
///
/// La clave completa se devuelve una única vez, en <see cref="Result{T}.Valor"/>;
/// a partir de aquí solo se guarda su hash — no hay manera de recuperarla.
/// </summary>
public record GenerarClaveApiCommand(Guid DelegacionTenantId, string NombreDescriptivo) : IRequest<Result<ClaveApiGeneradaDto>>;

public record ClaveApiGeneradaDto(Guid Id, string ClaveEnClaro, string PrefijoVisible);

public class GenerarClaveApiCommandValidator : AbstractValidator<GenerarClaveApiCommand>
{
    public GenerarClaveApiCommandValidator()
    {
        RuleFor(c => c.DelegacionTenantId).NotEmpty();

        RuleFor(c => c.NombreDescriptivo)
            .NotEmpty().WithMessage("Indica para qué se va a usar esta clave.")
            .MaximumLength(200);
    }
}

public class GenerarClaveApiCommandHandler(
    IDelegacionTenantRepository delegacionRepositorio,
    IClaveApiRepository claveRepositorio,
    ITenantsQueryContext dbContext,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GenerarClaveApiCommand, Result<ClaveApiGeneradaDto>>
{
    private const string PrefijoClave = "hydra_";

    public async Task<Result<ClaveApiGeneradaDto>> Handle(GenerarClaveApiCommand request, CancellationToken cancellationToken)
    {
        var delegacion = await delegacionRepositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);
        if (delegacion is null || delegacion.Proposito is not PropositoDelegacion.Soporte)
            return Result.Fallo<ClaveApiGeneradaDto>(
                Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación de soporte."));

        // Mismo criterio que AbrirAccesoSoporteCommand: se comprueba contra el
        // tenant de ORIGEN, nunca contra ITenantActual (que reflejaría un
        // Delegated Workspace ya activo).
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        var esPlataforma = tenantOrigenId is not null && await dbContext.Tenants
            .AnyAsync(t => t.Id == tenantOrigenId.Value && t.EsPlataforma, cancellationToken);

        if (!esPlataforma || delegacion.TenantConsultoraId != tenantOrigenId!.Value)
            return Result.Fallo<ClaveApiGeneradaDto>(
                Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación de soporte."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<ClaveApiGeneradaDto>(
                Error.Crear("ClaveApi.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        var secreto = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var claveEnClaro = $"{PrefijoClave}{secreto}";
        var prefijoVisible = claveEnClaro[..(PrefijoClave.Length + 6)];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(claveEnClaro))).ToLowerInvariant();

        var clave = new ClaveApi(request.NombreDescriptivo, prefijoVisible, hash, usuarioId.Value);

        // La clave pertenece al tenant CLIENTE, igual que RegistroActividadSoporte
        // en AbrirAccesoSoporteCommand: sin este ámbito explícito, el
        // interceptor la sellaría contra el tenant plataforma.
        using (AmbitoTenantExplicito.Establecer(delegacion.TenantClienteId))
        {
            claveRepositorio.Agregar(clave);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Exito(new ClaveApiGeneradaDto(clave.Id, claveEnClaro, prefijoVisible));
    }
}
