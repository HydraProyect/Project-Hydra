using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// Valora Prevención pasó a ser Avanta Prevención (Grupo Avanta, decisión del propietario del 2026-10-02).
/// El catálogo de plataformas CAE es global y se siembra por <c>HasData</c>: el cambio llega a las bases
/// que ya existen (staging y producción) solo por la migración <c>AvantaPrevencionRenombraValora</c>.
///
/// <para>
/// Lo que se prueba es el contrato del cambio sobre una base que <b>ya tiene</b> el catálogo con la
/// marca antigua: el slug <c>valora</c> y el Id no se tocan, solo cambia el nombre visible, el dominio
/// antiguo sigue resolviendo, el nuevo se añade, y un ajuste hecho sobre el proveedor (aquí, desactivarlo)
/// no se pisa.
/// </para>
/// </summary>
public class AvantaPrevencionRenombraValoraTests : IAsyncLifetime
{
    private const string MigracionDelCambio = "20261002152423_AvantaPrevencionRenombraValora";
    private static readonly Guid IdValora = new("6000000b-0000-0000-0000-000000000001");

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    /// <summary>La migración inmediatamente anterior a la del cambio: así la constante no caduca cuando entre otra posterior.</summary>
    private string MigracionAnterior
    {
        get
        {
            using var contexto = NuevoContexto();
            var migraciones = contexto.Database.GetMigrations().ToList();
            var indice = migraciones.IndexOf(MigracionDelCambio);
            indice.Should().BeGreaterThan(0, "la migración del cambio existe en el ensamblado y no es la línea base");
            return migraciones[indice - 1];
        }
    }

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadena);

        // Se deshace hasta la migración anterior a la del cambio: la base queda como estaba en staging y
        // producción antes de desplegarlo.
        await using var contexto = NuevoContexto();
        await contexto.GetService<IMigrator>().MigrateAsync(MigracionAnterior);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task La_base_previa_tiene_Valora_y_solo_su_dominio_antiguo()
    {
        await using var contexto = NuevoContexto();
        var valora = await contexto.ProveedoresPlataformaCae.SingleAsync(p => p.Id == IdValora);
        valora.Nombre.Should().Be("Valora", "control positivo: sin esto, los tests de abajo no probarían ningún cambio");
        (await DominiosAsync(contexto)).Should().BeEquivalentTo(["valoraprevencion.es"]);
    }

    [Fact]
    public async Task La_migracion_renombra_conserva_slug_e_Id_y_anade_el_dominio_nuevo()
    {
        await using (var contexto = NuevoContexto())
            await contexto.GetService<IMigrator>().MigrateAsync();

        await using var lectura = NuevoContexto();
        var valora = await lectura.ProveedoresPlataformaCae.SingleAsync(p => p.Id == IdValora);

        valora.Nombre.Should().Be("Avanta Prevención");
        valora.Codigo.Should().Be("valora", "el slug es el identificador estable: nunca cambia con el nombre");
        valora.Activo.Should().BeTrue();
        (await DominiosAsync(lectura)).Should().BeEquivalentTo(["valoraprevencion.es", "avantaprevencion.com"]);
        (await lectura.ProveedoresPlataformaCae.CountAsync()).Should().Be(23, "no se crea ni se borra ningún proveedor");
        (await lectura.ProveedoresPlataformaCae.CountAsync(p => p.Nombre == "Valora"))
            .Should().Be(0, "ninguna fila conserva el nombre antiguo");
    }

    [Fact]
    public async Task La_migracion_no_pisa_un_ajuste_hecho_sobre_el_proveedor()
    {
        // El único ajuste que admite el catálogo es activar/desactivar un proveedor
        // (CambiarActivoProveedorPlataformaCommand). La migración solo escribe la columna Nombre.
        await using (var escritura = NuevoContexto())
        {
            var valora = await escritura.ProveedoresPlataformaCae.SingleAsync(p => p.Id == IdValora);
            valora.Desactivar();
            await escritura.SaveChangesAsync();
        }

        await using (var contexto = NuevoContexto())
            await contexto.GetService<IMigrator>().MigrateAsync();

        await using var lectura = NuevoContexto();
        var tras = await lectura.ProveedoresPlataformaCae.SingleAsync(p => p.Id == IdValora);
        tras.Nombre.Should().Be("Avanta Prevención");
        tras.Activo.Should().BeFalse("la migración no debe reactivar un proveedor que alguien desactivó");
    }

    [Fact]
    public async Task La_migracion_se_deshace_sin_dejar_el_dominio_nuevo()
    {
        await using (var contexto = NuevoContexto())
        {
            var migrador = contexto.GetService<IMigrator>();
            await migrador.MigrateAsync();
            await migrador.MigrateAsync(MigracionAnterior);
        }

        await using var lectura = NuevoContexto();
        (await lectura.ProveedoresPlataformaCae.SingleAsync(p => p.Id == IdValora)).Nombre.Should().Be("Valora");
        (await DominiosAsync(lectura)).Should().BeEquivalentTo(["valoraprevencion.es"]);
    }

    [Fact]
    public void La_migracion_del_cambio_existe_en_el_ensamblado()
    {
        using var contexto = NuevoContexto();
        contexto.Database.GetMigrations().Should().Contain(MigracionDelCambio);
    }

    private static Task<List<string>> DominiosAsync(CaeManagerDbContext contexto) =>
        contexto.DominiosProveedorPlataformaCae
            .Where(d => d.ProveedorPlataformaCaeId == IdValora)
            .Select(d => d.Dominio)
            .ToListAsync();

    private CaeManagerDbContext NuevoContexto()
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());
    }
}
