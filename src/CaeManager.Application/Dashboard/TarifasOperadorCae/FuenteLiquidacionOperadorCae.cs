using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Operaciones;

namespace CaeManager.Application.Dashboard.TarifasOperadorCae;

/// <summary>
/// Implementación en Outbound del puerto <see cref="IFuenteLiquidacionOperadorCae"/> del Núcleo: reutiliza las mismas
/// piezas que la pantalla «Facturación» (<see cref="AutorizacionTarifasOperadorCae"/>, <see cref="AutorizacionCuadroDireccion"/>,
/// <see cref="MesCuadroDireccion"/> y <see cref="ObtenerEmpresasGestionadasDelMesQueryHandler.LeerAsync"/>), para que la
/// liquidación que marca Facturación sea exactamente la que la pantalla valora. La fase 0 del análisis de módulos moverá
/// estas piezas a Facturación; entonces este adaptador desaparece.
/// </summary>
public sealed class FuenteLiquidacionOperadorCae(ICurrentUserService usuario, IOperacionesQueryContext operaciones)
    : IFuenteLiquidacionOperadorCae
{
    public async Task<Guid?> EmisorQueLeeAsync()
    {
        var origen = await usuario.ObtenerTenantOrigenIdAsync();
        if (origen is null)
            return null;

        // El rol que cuenta es el del Tenant de origen del Operador, no el del Tenant que la sesión tenga activo.
        using (AmbitoTenantExplicito.Establecer(origen.Value))
            return await AutorizacionCuadroDireccion.EntraPorRolAsync(usuario) ? origen : null;
    }

    public Task<Guid?> EmisorQueEmiteAsync() => AutorizacionTarifasOperadorCae.OperadorQueGestionaAsync(usuario);

    public DateTime? FinDelMesUtc(int anyo, int mes) =>
        MesCuadroDireccion.EsValido(anyo, mes) ? new MesCuadroDireccion(anyo, mes).FinUtc : null;

    public async Task<IReadOnlySet<Guid>> TenantsBeneficiariosDelMesAsync(int anyo, int mes, CancellationToken cancellationToken)
    {
        if (await EmisorQueLeeAsync() is not { } emisor)
            return new HashSet<Guid>();

        using (AmbitoTenantExplicito.Establecer(emisor))
        {
            var gestionadas = await ObtenerEmpresasGestionadasDelMesQueryHandler.LeerAsync(usuario, operaciones, anyo, mes, cancellationToken);
            return gestionadas.Tramos.Keys.Where(t => t != emisor).ToHashSet();
        }
    }
}
