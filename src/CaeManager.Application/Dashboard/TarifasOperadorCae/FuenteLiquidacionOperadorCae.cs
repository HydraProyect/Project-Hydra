using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Operaciones;
using MediatR;

namespace CaeManager.Application.Dashboard.TarifasOperadorCae;

/// <summary>
/// Implementación en Outbound del puerto <see cref="IFuenteLiquidacionOperadorCae"/> del Núcleo: reutiliza las mismas
/// piezas que la pantalla «Facturación» (<see cref="AutorizacionTarifasOperadorCae"/>, <see cref="AutorizacionCuadroDireccion"/>,
/// <see cref="MesCuadroDireccion"/>, <see cref="ObtenerEmpresasGestionadasDelMesQueryHandler.LeerAsync"/> y la serie
/// <see cref="ObtenerSerieCuadroDireccionQuery"/>), para que la
/// liquidación que marca Facturación sea exactamente la que la pantalla valora. La fase 0 del análisis de módulos moverá
/// estas piezas a Facturación; entonces este adaptador desaparece.
/// </summary>
public sealed class FuenteLiquidacionOperadorCae(ICurrentUserService usuario, IOperacionesQueryContext operaciones, IMediator mediator)
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
        if (await EmisorQueLeeAsync() is not { } emisor || !MesCuadroDireccion.EsValido(anyo, mes))
            return new HashSet<Guid>();

        using var ambito = AmbitoTenantExplicito.Establecer(emisor);
        var gestionadas = (await ObtenerEmpresasGestionadasDelMesQueryHandler.LeerAsync(usuario, operaciones, anyo, mes, cancellationToken))
            .Tramos.Keys.Where(t => t != emisor).ToHashSet();

        // El mismo conjunto que lista la pantalla y exporta el CSV (FacturacionOperadorModelo.Construir): las Empresas
        // gestionadas en el mes que además salen en la serie del Cuadro de dirección, es decir, Tenants beneficiarios
        // autorizados por la vía Outbound y alcanzados enteros por quien marca, con un punto en ese mes. Sin esta
        // intersección, una Asignación de Operación solo Inbound dejaría marcar una liquidación que nadie ve.
        var serie = await mediator.Send(new ObtenerSerieCuadroDireccionQuery(anyo, mes, 1), cancellationToken);
        return serie.Tenants
            .Where(t => gestionadas.Contains(t.TenantId) && t.Meses.Any(p => p.Anyo == anyo && p.Mes == mes))
            .Select(t => t.TenantId)
            .ToHashSet();
    }
}
