using System.Security.Cryptography;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Logo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Tenants.Commands.GuardarLogoTenant;

/// <summary>
/// Sube o sustituye el logo del Tenant actual (contrato del selector de Tenant, lote 1). El Tenant
/// objetivo es siempre el actual, nunca un parámetro: para el Administrador es su Tenant de origen
/// (si tiene otro seleccionado, el autorizador lo deniega) y para Soporte TALVEG el objetivo de su
/// Sesión Privilegiada. Marcado <see cref="IComandoDeAprovisionamiento"/> para que Soporte TALVEG con
/// la capacidad Aprovisionamiento escriba con <c>cae_app_aprovisionamiento</c> (decisión 4), que solo
/// puede actualizar las tres columnas del logo de <c>Tenants</c>.
/// </summary>
public record GuardarLogoTenantCommand(byte[] ImagenOriginal) : ICommand, IComandoDeAprovisionamiento;

public class GuardarLogoTenantCommandValidator : AbstractValidator<GuardarLogoTenantCommand>
{
    public const int TamanoMaximoBytes = 5 * 1024 * 1024;

    public GuardarLogoTenantCommandValidator()
    {
        RuleFor(c => c.ImagenOriginal).NotEmpty();
        RuleFor(c => c.ImagenOriginal.Length).LessThanOrEqualTo(TamanoMaximoBytes)
            .WithMessage("Máximo 5 MB.");
    }
}

public class GuardarLogoTenantCommandHandler(
    ITenantActual tenantActual,
    IAutorizacionLogoTenant autorizacion,
    IConversorLogoTenantService conversor,
    ITenantRepository tenantRepository,
    IFileStorageService almacenamiento,
    IUnitOfWork unitOfWork,
    ILogger<GuardarLogoTenantCommandHandler> logger)
    : IRequestHandler<GuardarLogoTenantCommand, Result>
{
    public async Task<Result> Handle(GuardarLogoTenantCommand request, CancellationToken cancellationToken)
    {
        // Autorización antes de gastar CPU en decodificar nada.
        if (tenantActual.TenantId is not { } tenantId
            || !await autorizacion.PuedeEscribirAsync(tenantId, cancellationToken))
            return Result.Fallo(ErroresLogoTenant.NoAutorizado);

        byte[] png;
        try
        {
            png = conversor.ConvertirAPng(request.ImagenOriginal);
        }
        catch (LogoTenantNoAdmitidoException ex)
        {
            return Result.Fallo(Error.Crear("LogoTenant.ImagenNoAdmitida", ex.Message));
        }

        var tenant = await tenantRepository.ObtenerPorIdAsync(tenantId, cancellationToken);
        if (tenant is null)
            return Result.Fallo(ErroresLogoTenant.NoAutorizado);

        var claveAnterior = tenant.LogoArchivoClave;

        // Orden normativo (revisión Codex C9): blob nuevo → SaveChanges con LogoVersion como token de
        // concurrencia → borrado del anterior en mejor esfuerzo. Un fallo entre el blob y el guardado
        // deja, como mucho, un blob huérfano del propio Tenant que ninguna columna referencia y que por
        // tanto nunca se sirve.
        string claveNueva;
        using (var flujo = new MemoryStream(png))
            claveNueva = await almacenamiento.GuardarAsync(flujo, "logo.png", cancellationToken);

        // La versión incluye la clave del blob nuevo (única por subida): así cambia en CADA sustitución
        // aunque el contenido sea idéntico y el token de concurrencia detecta a la subida perdedora
        // (revisión Codex ronda 1).
        var version = VersionDe(png, claveNueva);
        var versionAnterior = tenant.LogoVersion;
        var actualizadoAnterior = tenant.LogoActualizadoEnUtc;

        try
        {
            tenant.EstablecerLogo(claveNueva, version, DateTime.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Conflicto de concurrencia (otra subida ganó) o cualquier otro fallo: el blob recién
            // escrito no lo referencia nadie. ConcurrenciaBehavior traduce el conflicto a resultado.
            // La entidad sigue rastreada: sin restaurar, otro guardado del mismo contexto persistiría una
            // referencia al blob que se acaba de borrar (revisión Codex ronda 1).
            tenant.RestaurarLogo(claveAnterior, versionAnterior, actualizadoAnterior);
            await BorrarEnMejorEsfuerzoAsync(claveNueva, tenantId);
            throw;
        }

        if (claveAnterior is not null && claveAnterior != claveNueva)
            await BorrarEnMejorEsfuerzoAsync(claveAnterior, tenantId);

        return Result.Exito();
    }

    /// <summary>
    /// Hash corto del PNG servido y de la clave de su blob: rompe la caché de la URL y es el token de
    /// concurrencia. Cambia en cada sustitución. No es autoridad de nada (C10), así que 64 bits de
    /// SHA-256 bastan.
    /// </summary>
    internal static string VersionDe(byte[] png, string claveBlob) =>
        Convert.ToHexStringLower(SHA256.HashData([.. png, .. System.Text.Encoding.UTF8.GetBytes(claveBlob)]))[..Tenant.LongitudLogoVersion];

    private async Task BorrarEnMejorEsfuerzoAsync(string clave, Guid tenantId)
    {
        try
        {
            await almacenamiento.EliminarAsync(clave, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo borrar el blob {Archivo} del logo del Tenant {TenantId}.", clave, tenantId);
        }
    }
}
