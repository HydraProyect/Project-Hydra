using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Plataforma;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// La matriz de alcance de <c>AdminPlataforma</c>, contra la implementación real
/// y concesiones reales.
///
/// <para>
/// Es el primer test de A3 y existe antes que la migración de las operaciones,
/// porque la distinción que A1 fijó —dos operaciones acotables y dos
/// intrínsecamente transversales— es justo lo que se diluye al implementar. Si
/// una concesión acotada satisficiera lo global, "AdminPlataforma sobre un
/// cliente" se convertiría en autoridad universal.
/// </para>
///
/// <para>
/// <b>La asimetría es el contrato:</b> global satisface las dos preguntas;
/// acotada satisface solo la del tenant, y nunca la global.
/// </para>
/// </summary>
public class AlcanceDeAdminPlataformaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantX = Guid.NewGuid();
    private readonly Guid _tenantY = Guid.NewGuid();
    private readonly Guid _conAlcanceAcotado = Guid.NewGuid();
    private readonly Guid _conAlcanceGlobal = Guid.NewGuid();
    private readonly Guid _sinNada = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;

        contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.SobreTenants(
            _conAlcanceAcotado, CapacidadPrivilegio.AdminPlataforma, [_tenantX],
            vigenciaDesde: ahora.AddMinutes(-5), vigenciaHasta: null));

        contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
            _conAlcanceGlobal, vigenciaDesde: ahora.AddMinutes(-5), vigenciaHasta: null));

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Una_concesion_acotada_cubre_su_tenant_y_ninguno_mas()
    {
        var autorizacion = CrearAutorizacion(out var contexto);
        await using var _ = contexto;

        (await autorizacion.PuedeSobreTenantAsync(_conAlcanceAcotado, _tenantX)).Should().BeTrue();
        (await autorizacion.PuedeSobreTenantAsync(_conAlcanceAcotado, _tenantY)).Should().BeFalse(
            "una concesión enumera los tenants que cubre; los demás no están cubiertos");
    }

    /// <summary>
    /// La mitad del contrato que impide la escalada: por muchos tenants que
    /// enumere una concesión acotada, nunca satisface lo transversal.
    /// </summary>
    [Fact]
    public async Task Una_concesion_acotada_nunca_satisface_lo_global()
    {
        var autorizacion = CrearAutorizacion(out var contexto);
        await using var _ = contexto;

        (await autorizacion.PuedeGlobalmenteAsync(_conAlcanceAcotado)).Should().BeFalse(
            "nunca se puede derivar PuedeGlobalmente = true a partir de PuedeSobreTenant = true");
    }

    [Fact]
    public async Task Una_concesion_global_satisface_las_dos_preguntas()
    {
        var autorizacion = CrearAutorizacion(out var contexto);
        await using var _ = contexto;

        (await autorizacion.PuedeGlobalmenteAsync(_conAlcanceGlobal)).Should().BeTrue();
        (await autorizacion.PuedeSobreTenantAsync(_conAlcanceGlobal, _tenantX)).Should().BeTrue();
        (await autorizacion.PuedeSobreTenantAsync(_conAlcanceGlobal, _tenantY)).Should().BeTrue(
            "global es global: cubre cualquier tenant, incluidos los que no existían al concederla");
    }

    [Fact]
    public async Task Sin_concesion_no_hay_autoridad_de_ningun_alcance()
    {
        var autorizacion = CrearAutorizacion(out var contexto);
        await using var _ = contexto;

        (await autorizacion.PuedeSobreTenantAsync(_sinNada, _tenantX)).Should().BeFalse();
        (await autorizacion.PuedeGlobalmenteAsync(_sinNada)).Should().BeFalse();
    }

    /// <summary>
    /// Los tres estados de ADR-011 § 8.1 no se colapsan: que la concesión exista
    /// no basta si no está vigente ahora.
    /// </summary>
    [Fact]
    public async Task Una_concesion_revocada_no_autoriza_nada()
    {
        await using (var contexto = CrearContexto())
        {
            var concesion = await contexto.ConcesionesPrivilegio
                .SingleAsync(c => c.UsuarioPlataformaId == _conAlcanceGlobal);

            concesion.Revocar(DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        var autorizacion = CrearAutorizacion(out var contexto2);
        await using var _ = contexto2;

        (await autorizacion.PuedeGlobalmenteAsync(_conAlcanceGlobal)).Should().BeFalse();
        (await autorizacion.PuedeSobreTenantAsync(_conAlcanceGlobal, _tenantX)).Should().BeFalse();
    }

    /// <summary>
    /// Que <c>SoporteLectura</c> no sirve para esto. Son capacidades distintas y
    /// una no se sustituye por la otra por muy amplio que sea su alcance.
    /// </summary>
    [Fact]
    public async Task Una_concesion_de_SoporteLectura_no_autoriza_operaciones_de_administracion()
    {
        var soporte = Guid.NewGuid();

        await using (var contexto = CrearContexto())
        {
            var ahora = DateTime.UtcNow;
            contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.SobreTenants(
                soporte, CapacidadPrivilegio.SoporteLectura, [_tenantX, _tenantY],
                vigenciaDesde: ahora.AddMinutes(-5), vigenciaHasta: null));

            await contexto.SaveChangesAsync();
        }

        var autorizacion = CrearAutorizacion(out var contexto2);
        await using var _ = contexto2;

        (await autorizacion.PuedeSobreTenantAsync(soporte, _tenantX)).Should().BeFalse();
        (await autorizacion.PuedeGlobalmenteAsync(soporte)).Should().BeFalse();
    }

    private AutorizacionAdminPlataformaPorConcesion CrearAutorizacion(out CaeManagerDbContext contexto)
    {
        contexto = CrearContexto();
        return new AutorizacionAdminPlataformaPorConcesion(contexto);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(
            options, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());
    }
}
