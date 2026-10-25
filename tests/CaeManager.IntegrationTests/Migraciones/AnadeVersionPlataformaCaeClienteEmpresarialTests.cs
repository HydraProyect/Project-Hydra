using CaeManager.Application.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// Solo CI: PostgreSQL real. Las filas anteriores a Version reciben un token no vacío; después el interceptor
/// lo renueva y EF rechaza la segunda escritura simultánea. Mismo arnés de avance y retroceso que AnadeContactoVisitaTests.
/// </summary>
public class AnadeVersionPlataformaCaeClienteEmpresarialTests : IAsyncLifetime
{
    private const string Migracion = "20261025023950_AnadeVersionPlataformaCaeClienteEmpresarial";
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private Guid _tenantId;
    private Guid _plataformaId;
    private Guid _otraPlataformaId;

    public async Task InitializeAsync()
    {
        await using var contexto = NuevoContexto();
        await contexto.Database.MigrateAsync();
        var tenant = new Tenant("Tenant propietario de la plataforma CAE");
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        _tenantId = tenant.Id;

        using var ambito = AmbitoTenantExplicito.Establecer(_tenantId);
        var empresa = new Empresa("Empresa contraparte");
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();
        var plataforma = new PlataformaCaeClienteEmpresarial(empresa.Id, "Nalanda", "https://portal.example", "CLI-01");
        contexto.PlataformasCaeClienteEmpresarial.Add(plataforma);
        await contexto.SaveChangesAsync();
        _plataformaId = plataforma.Id;
        var otraEmpresa = new Empresa("Otra Empresa contraparte");
        contexto.Empresas.Add(otraEmpresa);
        await contexto.SaveChangesAsync();
        var otraPlataforma = new PlataformaCaeClienteEmpresarial(otraEmpresa.Id, "CTAIMA", null, null);
        contexto.PlataformasCaeClienteEmpresarial.Add(otraPlataforma);
        await contexto.SaveChangesAsync();
        _otraPlataformaId = otraPlataforma.Id;

        var migraciones = contexto.Database.GetMigrations().ToList();
        var indice = migraciones.IndexOf(Migracion);
        indice.Should().BeGreaterThan(0);
        await contexto.GetService<IMigrator>().MigrateAsync(migraciones[indice - 1]);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task La_fila_existente_recibe_version_y_dos_ediciones_simultaneas_no_se_pisan()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantId);
        await using (var migrador = NuevoContexto())
            await migrador.GetService<IMigrator>().MigrateAsync();

        await using var primero = NuevoContexto();
        await using var segundo = NuevoContexto();
        var a = await primero.PlataformasCaeClienteEmpresarial.SingleAsync(p => p.Id == _plataformaId);
        var b = await segundo.PlataformasCaeClienteEmpresarial.SingleAsync(p => p.Id == _plataformaId);
        var versionAnterior = a.Version;
        versionAnterior.Should().NotBeEmpty("el formulario exige una versión no vacía también para las filas anteriores");
        b.Version.Should().Be(versionAnterior);
        var otra = await primero.PlataformasCaeClienteEmpresarial.AsNoTracking().SingleAsync(p => p.Id == _otraPlataformaId);
        otra.Version.Should().NotBeEmpty().And.NotBe(versionAnterior, "cada fila existente recibe su propio token");
        var porDefecto = await primero.Database.SqlQueryRaw<string>(
                """
                SELECT COALESCE(column_default, '') AS "Value" FROM information_schema.columns
                WHERE table_name = 'PlataformasCaeClienteEmpresarial' AND column_name = 'Version'
                """)
            .SingleAsync();
        porDefecto.Should().BeEmpty("sin DEFAULT, un INSERT que omita la Version falla en vez de dejarla vacía");
        a.Nombre.Should().Be("Nalanda");
        a.UrlPortal.Should().Be("https://portal.example");
        a.CodigoCliente.Should().Be("CLI-01");
        primero.Model.FindEntityType(typeof(PlataformaCaeClienteEmpresarial))!
            .FindProperty(nameof(PlataformaCaeClienteEmpresarial.Version))!.IsConcurrencyToken.Should().BeTrue();

        a.Actualizar("Dokify", null, "A");
        await primero.SaveChangesAsync();
        a.Version.Should().NotBe(versionAnterior);

        b.Actualizar("Edición obsoleta", null, "B");
        var guardarSegundo = () => segundo.SaveChangesAsync();
        await guardarSegundo.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var lectura = NuevoContexto();
        var guardada = await lectura.PlataformasCaeClienteEmpresarial.SingleAsync(p => p.Id == _plataformaId);
        guardada.Nombre.Should().Be("Dokify");
        guardada.CodigoCliente.Should().Be("A");
        guardada.Version.Should().Be(a.Version);
    }

    [Fact]
    public async Task El_repositorio_relee_la_plataforma_ya_rastreada_y_suelta_la_edicion_que_pierde_la_carrera()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantId);
        await using (var migrador = NuevoContexto())
            await migrador.GetService<IMigrator>().MigrateAsync();

        // El circuito de A guarda la plataforma y la deja rastreada; luego B la edita desde su propio circuito.
        await using var circuitoA = NuevoContexto();
        var repositorioA = new PlataformaCaeClienteEmpresarialRepository(circuitoA);
        var clienteId = (await circuitoA.PlataformasCaeClienteEmpresarial.AsNoTracking().SingleAsync(p => p.Id == _plataformaId))
            .ClienteEmpresarialId;
        var deA = (await repositorioA.ObtenerPlataformaAsync(clienteId))!;
        deA.Actualizar("Dokify", null, "A");
        (await repositorioA.GuardarCambiosAsync()).Should().BeTrue();

        await using (var circuitoB = NuevoContexto())
        {
            var deB = await circuitoB.PlataformasCaeClienteEmpresarial.SingleAsync(p => p.Id == _plataformaId);
            deB.Actualizar("Edición de B", null, "B");
            await circuitoB.SaveChangesAsync();
        }

        var releida = (await repositorioA.ObtenerPlataformaAsync(clienteId))!;
        releida.Nombre.Should().Be("Edición de B", "sin releer, el Command compararía la Version de pantalla con un token viejo");
        var versionDeB = releida.Version;

        // Carrera entre la comprobación de Version y el UPDATE: la edición perdida no queda pendiente en el circuito.
        await using (var circuitoC = NuevoContexto())
        {
            var deC = await circuitoC.PlataformasCaeClienteEmpresarial.SingleAsync(p => p.Id == _plataformaId);
            deC.Actualizar("Edición de C", null, "C");
            await circuitoC.SaveChangesAsync();
        }

        releida.Actualizar("Edición obsoleta de A", null, "A2");
        var guardar = () => repositorioA.GuardarCambiosAsync();
        await guardar.Should().ThrowAsync<DbUpdateConcurrencyException>();
        circuitoA.ChangeTracker.Entries<PlataformaCaeClienteEmpresarial>().Should().BeEmpty();
        (await repositorioA.ObtenerPlataformaAsync(clienteId))!.Version.Should().NotBe(versionDeB);
    }

    private CaeManagerDbContext NuevoContexto()
    {
        var tenant = new TenantPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenant), new ConcurrenciaOptimistaInterceptor())
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class TenantPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
