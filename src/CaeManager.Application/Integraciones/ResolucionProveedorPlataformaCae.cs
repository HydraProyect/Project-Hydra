using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Candidato encontrado al resolver una URL/dominio contra el catálogo de
/// <c>ProveedorPlataformaCae</c> — 0, 1 o N resultados (Sabentis/Quirón
/// Prevención comparten dominio a propósito, ver
/// <c>DominioProveedorPlataformaCae</c>).
/// </summary>
public record ProveedorPlataformaCaeCandidatoDto(Guid Id, string Nombre, bool Activo);

/// <summary>
/// Resuelve a qué <c>ProveedorPlataformaCae</c> pertenece una URL de acceso
/// (p. ej. la de un <c>CanalGestionDocumental</c>) — Parte 2 § (a),
/// PLAN-EJECUCION-UX.md: "la resolución matchea por dominio y sufijo
/// (subdominios incluidos); multi-match ⇒ elegir entre candidatos; sin match
/// ⇒ selección manual / alta".
/// </summary>
public interface IResolucionProveedorPlataformaCaeService
{
    Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorUrlAsync(string url, CancellationToken cancellationToken = default);
}

public class ResolucionProveedorPlataformaCaeService(IProveedoresPlataformaCaeQueryContext queryContext)
    : IResolucionProveedorPlataformaCaeService
{
    public async Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorUrlAsync(
        string url, CancellationToken cancellationToken = default)
    {
        var host = ExtraerHost(url);
        if (host is null)
            return [];

        var dominios = await queryContext.DominiosProveedorPlataformaCae
            .Select(d => new ParDominioProveedor(d.ProveedorPlataformaCaeId, d.Dominio))
            .ToListAsync(cancellationToken);

        var proveedores = await queryContext.ProveedoresPlataformaCae
            .Select(p => new ProveedorPlataformaCaeCandidatoDto(p.Id, p.Nombre, p.Activo))
            .ToListAsync(cancellationToken);

        return Resolver(host, dominios, proveedores);
    }

    /// <summary>Par (proveedor, dominio) plano — la forma mínima que <see cref="Resolver"/> necesita, sin depender de las entidades de dominio ni de EF Core.</summary>
    public record ParDominioProveedor(Guid ProveedorPlataformaCaeId, string Dominio);

    /// <summary>
    /// Extraído como método puro y estático (mismo patrón que
    /// <c>ObtenerBandejaGestorQueryHandler.Fusionar</c>) para poder probar el
    /// matching sin levantar EF Core.
    /// </summary>
    public static IReadOnlyList<ProveedorPlataformaCaeCandidatoDto> Resolver(
        string host, IReadOnlyList<ParDominioProveedor> dominios, IReadOnlyList<ProveedorPlataformaCaeCandidatoDto> proveedores)
    {
        var proveedorIdsCoincidentes = dominios
            .Where(d => CoincideHost(host, d.Dominio))
            .Select(d => d.ProveedorPlataformaCaeId)
            .ToHashSet();

        return proveedores.Where(p => proveedorIdsCoincidentes.Contains(p.Id)).ToList();
    }

    /// <summary>
    /// Coincidencia exacta o por sufijo de subdominio: "cliente123.ergasia.es"
    /// coincide con el dominio sembrado "ergasia.es" (match por sufijo,
    /// Ergasia/Valora/PlayCAE lo necesitan — subdominios propios de cada
    /// cliente), pero "notergasia.es" no (el punto delimitador evita falsos
    /// positivos por coincidencia de sufijo de cadena sin ser subdominio real).
    /// </summary>
    public static bool CoincideHost(string host, string dominio) =>
        host.Equals(dominio, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + dominio, StringComparison.OrdinalIgnoreCase);

    /// <summary>Acepta tanto una URL completa como un host suelto — un canal puede tener guardada cualquiera de las dos formas.</summary>
    public static string? ExtraerHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var candidato = url.Trim();

        if (Uri.TryCreate(candidato, UriKind.Absolute, out var uriAbsoluta) && !string.IsNullOrEmpty(uriAbsoluta.Host))
            return uriAbsoluta.Host;

        // Sin esquema ("app.twind.io/login") — Uri.TryCreate absoluto falla; se
        // le añade uno neutro solo para reutilizar el parser de host de .NET
        // en vez de reinventar el recorte de ruta/query a mano.
        if (Uri.TryCreate("https://" + candidato, UriKind.Absolute, out var uriConEsquema) && !string.IsNullOrEmpty(uriConEsquema.Host))
            return uriConEsquema.Host;

        return null;
    }
}
