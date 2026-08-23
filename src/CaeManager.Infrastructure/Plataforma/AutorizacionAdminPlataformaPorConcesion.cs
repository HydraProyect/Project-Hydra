using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Plataforma;

/// <inheritdoc cref="IAutorizacionAdminPlataforma" />
/// <remarks>
/// <para>
/// El alcance y la vigencia los decide el dominio con
/// <see cref="ConcesionPrivilegio.CubreEn"/>, que junta los tres estados que
/// ADR-011 § 8.1 prohíbe colapsar: existe, es válida <i>ahora</i>, y cubre
/// <i>esto</i>. Reimplementar aquí ese predicado crearía una segunda definición
/// de "concesión vigente" que acabaría divergiendo.
/// </para>
///
/// <para>
/// <b>Por qué se materializan las concesiones del usuario</b> en vez de traducir
/// <c>CubreEn</c> a SQL: es lógica de dominio y el conjunto es diminuto —las
/// concesiones de una persona se cuentan con los dedos—. El filtro por usuario sí
/// va en la consulta, que es lo que este acceso necesita para estar acotado a la
/// posición del llamante.
/// </para>
/// </remarks>
public class AutorizacionAdminPlataformaPorConcesion(
    IPlataformaQueryContext plataformaContext) : IAutorizacionAdminPlataforma
{
    public async Task<bool> PuedeSobreTenantAsync(
        Guid usuarioId, Guid tenantObjetivoId, CancellationToken cancellationToken = default)
    {
        if (tenantObjetivoId == Guid.Empty) return false;

        var ahora = DateTime.UtcNow;
        var concesiones = await AdminPlataformaDelUsuarioAsync(usuarioId, cancellationToken);

        return concesiones.Any(c => c.CubreEn(tenantObjetivoId, ahora));
    }

    public async Task<bool> PuedeGlobalmenteAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var ahora = DateTime.UtcNow;
        var concesiones = await AdminPlataformaDelUsuarioAsync(usuarioId, cancellationToken);

        // EsAlcanceGlobal explícito: una concesión acotada NUNCA satisface esto,
        // por muchos tenants que enumere. Es la mitad del contrato que impide que
        // "AdminPlataforma sobre un cliente" se convierta en autoridad universal.
        //
        // Guid.Empty como objetivo es deliberado: con EsAlcanceGlobal true,
        // CubreEn ni mira la lista de tenants, así que aquí solo aporta la
        // comprobación de estado y ventana.
        return concesiones.Any(c => c.EsAlcanceGlobal && c.CubreEn(Guid.Empty, ahora));
    }

    private async Task<List<ConcesionPrivilegio>> AdminPlataformaDelUsuarioAsync(
        Guid usuarioId, CancellationToken cancellationToken)
    {
        if (usuarioId == Guid.Empty) return [];

        return await plataformaContext.ConcesionesPrivilegio
            .Include(c => c.TenantsAlcanzados)
            .Where(c => c.UsuarioPlataformaId == usuarioId
                        && c.Capacidad == CapacidadPrivilegio.AdminPlataforma)
            .ToListAsync(cancellationToken);
    }
}
