using CaeManager.Application.Common;
using System.Security.Cryptography;
using System.Text;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.ApiKeys.Commands.GenerarClaveApi;

/// <summary>
/// Emite una clave de la API pública para el Tenant propietario de una delegación de
/// propósito Soporte.
///
/// <para>
/// <b>La emisión queda fuera de la Sesión Privilegiada</b> hasta que exista una
/// operación privilegiada específica para ella: no se ejecuta como acto de una sesión
/// privilegiada, y el pipeline mantiene su denegación de escritura para ese caso. Por
/// eso no sigue el criterio de <c>AbrirAccesoSoporteCommand</c>. Lo que exige hoy, todo
/// a la vez:
/// </para>
/// <list type="bullet">
/// <item>la cuenta pertenece a la organización de origen de la delegación
/// (<c>TenantConsultoraId</c> = Tenant de origen de la cuenta, nunca el Tenant activo);</item>
/// <item>la delegación es de propósito Soporte y está activa y vigente al emitir
/// (<see cref="CriterioSoporteClaveApi.PuedeEmitir"/>). Al autenticar,
/// <c>ApiKeyAuthenticationHandler</c> exige que siga abierta esa misma ventana
/// (<see cref="CriterioSoporteClaveApi.PuedeAutenticar"/>): la clave vive como mucho lo que ella,
/// aunque su caducidad sea posterior;</item>
/// <item>AdminPlataforma sobre el Tenant propietario objetivo
/// (<see cref="IAutorizacionAdminPlataforma.PuedeSobreTenantAsync"/>), no la mera
/// pertenencia al Tenant de plataforma;</item>
/// <item>segundo factor activo en la cuenta;</item>
/// <item>un motivo no vacío (mismas reglas que el de <c>AbrirAccesoSoporteCommand</c>), que
/// queda en el registro de auditoría del alta de la clave en el Tenant propietario y no
/// se persiste como columna;</item>
/// <item>una caducidad en UTC, futura y como máximo a 365 días.</item>
/// </list>
///
/// La clave completa se devuelve una única vez, en <see cref="Result{T}.Valor"/>;
/// a partir de aquí solo se guarda su hash — no hay manera de recuperarla.
/// </summary>
public record GenerarClaveApiCommand(Guid DelegacionTenantId, string NombreDescriptivo, DateTime? CaducaEnUtc, string Motivo) : ICommand<ClaveApiGeneradaDto>;

public record ClaveApiGeneradaDto(Guid Id, string ClaveEnClaro, string PrefijoVisible);

public class GenerarClaveApiCommandValidator : AbstractValidator<GenerarClaveApiCommand>
{
    public GenerarClaveApiCommandValidator()
    {
        RuleFor(c => c.DelegacionTenantId).NotEmpty();

        RuleFor(c => c.NombreDescriptivo)
            .NotEmpty().WithMessage("Indica para qué se va a usar esta clave.")
            .MaximumLength(200);

        // Mismas reglas que el motivo de AbrirAccesoSoporteCommandValidator: no vacío y 500 como máximo.
        RuleFor(c => c.Motivo)
            .NotEmpty().WithMessage("Indica por qué se emite esta clave.")
            .MaximumLength(500);

        RuleFor(c => c.CaducaEnUtc)
            .Must(fecha => fecha is { Kind: DateTimeKind.Utc } valor
                && valor > DateTime.UtcNow && valor <= DateTime.UtcNow.AddDays(DiasMaximosDeCaducidad))
            .WithMessage($"Indica una caducidad futura que no supere {DiasMaximosDeCaducidad} días (como último día válido, hoy + {DiasMaximosDeCaducidad - 1}).");
    }

    /// <summary>Techo de la caducidad de una clave; <c>ClaveApi</c> aplica el mismo en el dominio.</summary>
    public const int DiasMaximosDeCaducidad = 365;
}

public class GenerarClaveApiCommandHandler(
    IDelegacionTenantRepository delegacionRepositorio,
    IClaveApiRepository claveRepositorio,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GenerarClaveApiCommand, Result<ClaveApiGeneradaDto>>
{
    private const string PrefijoClave = "hydra_";

    public async Task<Result<ClaveApiGeneradaDto>> Handle(GenerarClaveApiCommand request, CancellationToken cancellationToken)
    {
        // El pipeline ya valida el comando; se repite aquí porque la caducidad y el motivo son
        // parte del contrato de emisión y el handler no puede depender de que lo invoquen por MediatR.
        var validacion = new GenerarClaveApiCommandValidator().Validate(request);
        if (!validacion.IsValid)
            return Result.Fallo<ClaveApiGeneradaDto>(
                Error.Crear("ClaveApi.DatosInvalidos", validacion.Errors[0].ErrorMessage));

        var delegacion = await delegacionRepositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);
        if (delegacion is null || delegacion.Proposito is not PropositoDelegacion.Soporte)
            return Result.Fallo<ClaveApiGeneradaDto>(
                Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación de soporte."));

        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (tenantOrigenId is null || delegacion.TenantConsultoraId != tenantOrigenId.Value)
            return Result.Fallo<ClaveApiGeneradaDto>(
                Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación de soporte."));

        // El Tenant propietario de la clave: el lado delegante de la delegación de Soporte.
        var tenantPropietarioId = delegacion.TenantClienteId;

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<ClaveApiGeneradaDto>(
                Error.Crear("ClaveApi.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeSobreTenantAsync(usuarioId.Value, tenantPropietarioId, cancellationToken))
            return Result.Fallo<ClaveApiGeneradaDto>(
                Error.Crear("ClaveApi.NoAutorizado", "No tienes autorización de plataforma sobre esta organización."));

        if (!await currentUserService.TieneDobleFactorActivoAsync())
            return Result.Fallo<ClaveApiGeneradaDto>(
                Error.Crear("ClaveApi.SinDobleFactor", "Activa la autenticación en dos pasos antes de emitir una clave."));

        if (!CriterioSoporteClaveApi.PuedeEmitir(delegacion, tenantOrigenId.Value, tenantPropietarioId, DateTime.UtcNow))
            return Result.Fallo<ClaveApiGeneradaDto>(
                Error.Crear("ClaveApi.DelegacionNoVigente", "La delegación de soporte debe estar activa y vigente."));

        var secreto = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var claveEnClaro = $"{PrefijoClave}{secreto}";
        var prefijoVisible = claveEnClaro[..(PrefijoClave.Length + 6)];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(claveEnClaro))).ToLowerInvariant();

        var clave = new ClaveApi(request.NombreDescriptivo, prefijoVisible, hash, usuarioId.Value,
            request.CaducaEnUtc!.Value, request.Motivo);

        // La clave pertenece al Tenant propietario, igual que RegistroActividadSoporte
        // en AbrirAccesoSoporteCommand: sin este ámbito explícito, el
        // interceptor la sellaría contra el tenant plataforma. El registro de
        // auditoría del alta, con el motivo, queda en ese mismo Tenant.
        using (AmbitoTenantExplicito.Establecer(tenantPropietarioId))
        {
            claveRepositorio.Agregar(clave);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Exito(new ClaveApiGeneradaDto(clave.Id, claveEnClaro, prefijoVisible));
    }
}
