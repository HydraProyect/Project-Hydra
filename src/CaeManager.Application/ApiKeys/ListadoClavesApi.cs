using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi;
using CaeManager.Domain.ApiKeys;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.ApiKeys;

/// <summary>
/// Las claves del Tenant del ámbito, con su estado real. Lo comparten el listado del Actor de
/// Plataforma TALVEG y el del Tenant propietario: una clave cortada se ve igual en los dos.
/// </summary>
internal static class ListadoClavesApi
{
    public static async Task<IReadOnlyList<ClaveApiDto>> LeerAsync(
        IApiKeysQueryContext claveContext, IClaveApiRepository claveRepositorio, Guid tenantPropietarioId, CancellationToken cancellationToken)
    {
        // RevocadaEnUtc y no c.EstaActiva: una propiedad sin mapear obliga a EF a
        // materializar la entidad entera, HashClave incluida, y en Sesión Privilegiada la
        // conexión lleva cae_app_soporte, que solo puede leer las columnas sin el hash
        // (migración SoporteSinLecturaDeCredenciales).
        var filas = await claveContext.ClavesApi
            .OrderByDescending(c => c.CreadoEnUtc)
            .Select(c => new { c.Id, c.NombreDescriptivo, c.PrefijoVisible, c.CreadoEnUtc, c.UltimoUsoUtc, c.RevocadaEnUtc, c.CaducaEnUtc, c.CreadaPorUsuarioId })
            .ToListAsync(cancellationToken);

        var ahora = DateTime.UtcNow;
        var claves = new List<ClaveApiDto>(filas.Count);
        foreach (var c in filas)
        {
            // Mismo criterio que ApiKeyAuthenticationHandler. Revocada o caducada ya no autentica
            // y su estado lo cuenta la propia fila.
            var cortada = c.RevocadaEnUtc is null && ahora < c.CaducaEnUtc
                && !await claveRepositorio.ClaveSigueRespaldadaAsync(tenantPropietarioId, c.CreadaPorUsuarioId, c.CreadoEnUtc, cancellationToken);

            claves.Add(new ClaveApiDto(c.Id, c.NombreDescriptivo, c.PrefijoVisible, c.CreadoEnUtc, c.UltimoUsoUtc, c.RevocadaEnUtc == null, c.CaducaEnUtc, cortada));
        }

        return claves;
    }
}
