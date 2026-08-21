using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Plataforma;

/// <summary>
/// Implementación <b>heredada</b> de <see cref="IAutorizacionAperturaSesion"/>:
/// responde que sí cuando el usuario pertenece al tenant marcado como plataforma.
///
/// Es exactamente la puerta que la vía antigua usaba
/// (<c>AbrirAccesoSoporteCommand</c>), y es un rol monolítico — justo lo que
/// ADR-011 § 4bis.2 quiere sustituir por una matriz por capacidades. Está aquí
/// para que F2b-6 no pierda el control mientras esa migración llega, y para que
/// cuando llegue baste con cambiar esta clase: el comando pregunta "¿puede
/// abrir?", no "¿es de plataforma?", así que la ceremonia no se entera.
///
/// <b>Contra el tenant de ORIGEN, nunca contra <c>ITenantActual</c>.</b> Ese
/// refleja el workspace activo, así que alguien que ya esté operando un tenant
/// ajeno lo tendría fijado a ese tenant y podría usarlo para abrirse acceso a un
/// tercero. El de origen sale del claim de sesión y es lo único que la selección
/// de workspace no puede cambiar.
///
/// <b>Qué NO comprueba</b>, y es deliberado para no mezclar autoridades: no mira
/// la concesión ni su capacidad. Que exista una concesión de <c>SoporteLectura</c>
/// no autoriza a abrir, y no tenerla no es un problema de autorización sino de
/// que no hay nada que abrir — lo dice el dominio, con su propio error.
/// </summary>
public class AutorizacionAperturaSesionPorTenantDePlataforma(
    ITenantsQueryContext tenantsContext,
    ICurrentUserService currentUserService) : IAutorizacionAperturaSesion
{
    public async Task<bool> PuedeAbrirAsync(
        Guid usuarioId, Guid tenantObjetivoId, CancellationToken cancellationToken = default)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (tenantOrigenId is null) return false;

        // Nadie abre una sesión de soporte sobre su propio tenant: ahí ya entra
        // por la vía normal, y permitirlo sería una forma de saltarse su propio
        // rol dentro de su organización.
        if (tenantOrigenId.Value == tenantObjetivoId) return false;

        return await tenantsContext.Tenants
            .AnyAsync(t => t.Id == tenantOrigenId.Value && t.EsPlataforma, cancellationToken);
    }
}
