using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Complementa <see cref="AlcanceDeSesionPrivilegiadaTests"/>: aquella prueba
/// fija <c>ITenantActual.TenantId</c> al MISMO tenant que
/// <c>SesionPrivilegiadaActiva.TenantObjetivoId</c> en los tres tests —nunca
/// los separa—, así que nunca ejercita la comprobación de que "total" es total
/// SOLO dentro del tenant que la sesión abrió.
///
/// Contrato: la capacidad de una sesión privilegiada (SoporteLectura,
/// BreakGlass) concede acceso total exclusivamente dentro de
/// <c>TenantObjetivoId</c>, nunca en un <see cref="AmbitoTenantExplicito"/>
/// distinto, aunque la misma instancia scoped de
/// <see cref="AlcanceDatosService"/> se reutilice para varios tenants (mismo
/// mecanismo de fan-out que el defecto de rol cerrado en #571).
///
/// Es una prueba de defensa en profundidad: hoy ningún llamador reutiliza esta
/// instancia para un tenant ajeno al objetivo de una sesión privilegiada
/// activa (ver checkpoint/PR para el detalle de alcanzabilidad), pero el
/// servicio no debe depender de que eso siga siendo cierto.
/// </summary>
public class AlcanceDeSesionPrivilegiadaFueraDelTenantObjetivoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantObjetivoDeLaSesion = Guid.NewGuid();
    private readonly Guid _tenantAjeno = Guid.NewGuid();
    private readonly Guid _usuarioPlataforma = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenantObjetivoDeLaSesion);
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    public async Task No_hay_acceso_total_en_un_tenant_distinto_del_objetivo_de_la_sesion(CapacidadPrivilegio capacidad)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantAjeno };
        var alcance = new AlcanceDatosService(
            CrearContexto(_tenantAjeno),
            new CurrentUserServiceFalso(_usuarioPlataforma, rol: null),
            tenantActual,
            new SesionPrivilegiadaFalsa(new SesionPrivilegiadaActiva(
                Guid.NewGuid(), Guid.NewGuid(), _tenantObjetivoDeLaSesion, capacidad, null)));

        (await alcance.TieneAccesoTotalAsync()).Should().BeFalse(
            "la sesión solo abrió el tenant objetivo — un tenant distinto no hereda su capacidad");

        // Vacía, no null: sin rol de negocio (plano 3) y sin acceso total aquí,
        // el reparto por cliente cae al mismo fallo cerrado que un rol
        // desconocido — nunca "sin restricción".
        (await alcance.ObtenerClienteIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>
    /// La forma real del defecto potencial: una única instancia (scoped, como
    /// la que reutiliza un fan-out) visita primero el tenant objetivo de la sesión
    /// —donde "total" es correcto— y después uno ajeno, cambiando solo
    /// <see cref="ITenantActual.TenantId"/> entre las dos llamadas. El caché
    /// por tenant introducido en #571 evita que el segundo SIRVA el valor
    /// cacheado del primero, pero no evita que se vuelva a calcular mal: sin
    /// la comprobación de tenant, la segunda llamada recalcula "total" para el
    /// tenant ajeno a partir de la MISMA sesión.
    /// </summary>
    [Fact]
    public async Task El_acceso_total_del_tenant_objetivo_no_se_extiende_a_un_tenant_visitado_despues()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantObjetivoDeLaSesion };
        var alcance = new AlcanceDatosService(
            CrearContexto(_tenantObjetivoDeLaSesion),
            new CurrentUserServiceFalso(_usuarioPlataforma, rol: null),
            tenantActual,
            new SesionPrivilegiadaFalsa(new SesionPrivilegiadaActiva(
                Guid.NewGuid(), Guid.NewGuid(), _tenantObjetivoDeLaSesion, CapacidadPrivilegio.SoporteLectura, null)));

        (await alcance.TieneAccesoTotalAsync()).Should().BeTrue(
            "en su propio tenant objetivo, SoporteLectura sí ve el tenant entero");

        tenantActual.TenantId = _tenantAjeno;

        (await alcance.TieneAccesoTotalAsync()).Should().BeFalse(
            "la misma instancia, ahora sobre un tenant ajeno, no puede heredar el acceso total del primero");
    }

    private CaeManagerDbContext CrearContexto(Guid tenantDeSellado)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantDeSellado };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>Mismo doble que <see cref="AlcanceDeSesionPrivilegiadaTests"/>: aísla la decisión de alcance dada una sesión ya válida.</summary>
    private sealed class SesionPrivilegiadaFalsa(SesionPrivilegiadaActiva sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(sesion);
    }
}
