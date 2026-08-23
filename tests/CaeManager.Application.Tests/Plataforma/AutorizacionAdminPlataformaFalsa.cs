using CaeManager.Application.Plataforma;

namespace CaeManager.Application.Tests.Plataforma;

/// <summary>
/// Doble de <see cref="IAutorizacionAdminPlataforma"/>.
///
/// <para>
/// <b>Modela el alcance, no un booleano.</b> Un doble de "sí o no" colapsaría
/// justo la distinción que A3 introduce: una concesión acotada a un tenant y una
/// global responden igual sobre ese tenant y distinto sobre todo lo demás. Con un
/// booleano, un comando que pidiera la autoridad equivocada seguiría en verde.
/// </para>
/// </summary>
public class AutorizacionAdminPlataformaFalsa : IAutorizacionAdminPlataforma
{
    private readonly bool _global;
    private readonly HashSet<Guid> _tenants;

    private AutorizacionAdminPlataformaFalsa(bool global, IEnumerable<Guid>? tenants = null)
    {
        _global = global;
        _tenants = tenants is null ? [] : [.. tenants];
    }

    /// <summary>Concesión global: cubre cualquier tenant y también lo transversal.</summary>
    public static AutorizacionAdminPlataformaFalsa Global() => new(global: true);

    /// <summary>Acotada: cubre esos tenants y <b>nunca</b> lo global.</summary>
    public static AutorizacionAdminPlataformaFalsa AcotadaA(params Guid[] tenants) => new(global: false, tenants);

    /// <summary>Sin ninguna concesión de AdminPlataforma.</summary>
    public static AutorizacionAdminPlataformaFalsa SinNada() => new(global: false);

    public Guid? UltimoTenantConsultado { get; private set; }
    public bool SeConsultoLoGlobal { get; private set; }

    public Task<bool> PuedeSobreTenantAsync(
        Guid usuarioId, Guid tenantObjetivoId, CancellationToken cancellationToken = default)
    {
        UltimoTenantConsultado = tenantObjetivoId;
        return Task.FromResult(_global || _tenants.Contains(tenantObjetivoId));
    }

    public Task<bool> PuedeGlobalmenteAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        SeConsultoLoGlobal = true;
        return Task.FromResult(_global);
    }
}
