using CaeManager.Application.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// <b>La siembra Inbound de Development deja lo que declara, en el Tenant que lo posee, y una segunda pasada no escribe
/// nada.</b> Corre sobre PostgreSQL real con el <see cref="CaeManagerDbContext"/> como cae_app_runtime (RLS efectiva), igual
/// que el arranque fuera de la base local; cada recuento se lee dentro del ámbito del Tenant propietario de la fila, así que
/// una fila sembrada en el Tenant equivocado no se contaría.
/// </summary>
public sealed class InboundDemoSeederFixture : IAsyncLifetime
{
    internal ArnesDeArranqueRuntime Arnes { get; private set; } = null!;
    internal IConfiguration Configuracion { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        Configuracion = new ConfigurationBuilder()
            .AddConfiguration(Arnes.Servicios.GetRequiredService<IConfiguration>())
            .AddInMemoryCollection(new Dictionary<string, string?> { [InboundDemoSeeder.ClaveConfiguracion] = "true" })
            .Build();

        await SembrarAsync();
    }

    internal async Task SembrarAsync()
    {
        using var ambito = Arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        await InboundDemoSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IUserStore<ApplicationUser>>(), Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
    }

    public Task DisposeAsync() => Arnes.DisposeAsync().AsTask();
}

public class InboundDemoSeederTests(InboundDemoSeederFixture fixture) : IClassFixture<InboundDemoSeederFixture>
{
    [Fact]
    public async Task El_Tenant_TCE_tiene_un_contrato_de_acceso_en_cada_estado()
    {
        var porEstado = await EnTenantAsync(InboundDemoSeeder.NombreTenantTce, async db =>
            (await db.ContratosAcceso.ToListAsync()).GroupBy(c => c.EstadoEfectivo).ToDictionary(g => g.Key, g => g.Count()));

        porEstado.Should().BeEquivalentTo(new Dictionary<EstadoContratoAcceso, int>
        {
            [EstadoContratoAcceso.Activo] = 2,
            [EstadoContratoAcceso.PendienteDeConfirmar] = 1,
            [EstadoContratoAcceso.Suspendido] = 1,
            [EstadoContratoAcceso.Finalizado] = 1,
            [EstadoContratoAcceso.Cancelado] = 1,
            [EstadoContratoAcceso.Rechazado] = 1,
        });
    }

    [Fact]
    public async Task Los_contratos_llevan_condiciones_historial_y_su_Tenant_TEC()
    {
        var tecs = await IdsAsync(InboundDemoSeeder.NombreTenantMoncayo, InboundDemoSeeder.NombreTenantRibera, InboundDemoSeeder.NombreTenantSomontano);

        await EnTenantAsync(InboundDemoSeeder.NombreTenantTce, async db =>
        {
            var contratos = await db.ContratosAcceso.Include(c => c.HistorialVigencia).ToListAsync();
            var activo = contratos.Single(c => c.Notas == InboundDemoSeeder.Contratos.MoncayoActivo);
            activo.Condiciones.Should().NotBe(CondicionesContratoAcceso.SinFijar);
            activo.CupoTrabajadores.Should().Be(8);
            activo.HistorialVigencia.Select(h => h.Origen).Should().BeEquivalentTo([OrigenCambioVigencia.Edicion, OrigenCambioVigencia.Renovacion]);

            contratos.Single(c => c.Notas == InboundDemoSeeder.Contratos.MoncayoSinVincular).TenantContratistaId.Should().BeNull();
            contratos.Where(c => c.Notas != InboundDemoSeeder.Contratos.MoncayoSinVincular)
                .Should().OnlyContain(c => c.TenantContratistaId != null && tecs.Contains(c.TenantContratistaId.Value));
            return 0;
        });
    }

    [Fact]
    public async Task El_Tenant_TCE_tiene_sus_plantillas_de_requisitos_sin_requisito_base()
    {
        var (plantillas, requisitos, conBase) = await EnTenantAsync(InboundDemoSeeder.NombreTenantTce, async db =>
        {
            var todas = await db.PlantillasRequisitos.Include(p => p.Requisitos).ToListAsync();
            return (todas.Count, todas.Sum(p => p.Requisitos.Count), todas.SelectMany(p => p.Requisitos).Count(r => r.RequisitoBaseId != null));
        });

        plantillas.Should().Be(2);
        requisitos.Should().Be(7);
        conBase.Should().Be(0, "el catálogo base de B-13 no tiene hoy requisitos activos que adoptar");
    }

    [Fact]
    public async Task La_Gestora_CAE_del_Tenant_TCE_tiene_la_cartera_del_Tenant_entero()
    {
        var gestoraId = await UsuarioIdAsync(InboundDemoSeeder.EmailGestoraTce);

        var carteras = await EnTenantAsync(InboundDemoSeeder.NombreTenantTce, db =>
            db.AsignacionesCartera.CountAsync(a => a.UsuarioId == gestoraId && a.Estado == EstadoAsignacion.Vigente));

        carteras.Should().Be(1);
    }

    [Theory]
    [InlineData(InboundDemoSeeder.NombreTenantMoncayo, InboundDemoSeeder.EmailUsuarioMoncayo)]
    [InlineData(InboundDemoSeeder.NombreTenantRibera, InboundDemoSeeder.EmailUsuarioRibera)]
    [InlineData(InboundDemoSeeder.NombreTenantSomontano, InboundDemoSeeder.EmailUsuarioSomontano)]
    public async Task Cada_Tenant_TEC_tiene_su_Usuario_unico_con_2FA_y_su_documentacion(string nombreTenant, string email)
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var tenantId = (await IdsAsync(nombreTenant)).Single();
        var usuarios = await bootstrap.Users.IgnoreQueryFilters().Where(u => u.TenantId == tenantId).ToListAsync();
        usuarios.Should().ContainSingle().Which.Should().Match<ApplicationUser>(u => u.Email == email && u.TwoFactorEnabled);

        var (propias, trabajadores, documentos) = await EnTenantAsync(nombreTenant, async db =>
            (await db.Empresas.CountAsync(e => e.EsPropia), await db.Trabajadores.CountAsync(), await db.Documentos.CountAsync()));
        propias.Should().Be(1);
        trabajadores.Should().Be(3);
        documentos.Should().Be(9);
    }

    [Fact]
    public async Task Montajes_Moncayo_autoriza_la_lectura_del_contrato_activo_y_revoco_la_del_finalizado()
    {
        var tceId = (await IdsAsync(InboundDemoSeeder.NombreTenantTce)).Single();
        var contratos = await EnTenantAsync(InboundDemoSeeder.NombreTenantTce, db =>
            db.ContratosAcceso.ToDictionaryAsync(c => c.Notas!, c => c.Id));

        await EnTenantAsync(InboundDemoSeeder.NombreTenantMoncayo, async db =>
        {
            var autorizaciones = await db.AutorizacionesLecturaDocumental.ToListAsync();
            autorizaciones.Should().HaveCount(2).And.OnlyContain(a => a.TenantDestinatarioId == tceId);

            var vigente = autorizaciones.Single(a => a.ContratoAccesoId == contratos[InboundDemoSeeder.Contratos.MoncayoActivo]);
            vigente.EstaRevocada.Should().BeFalse();
            vigente.IncluyeDocumentosEmpresa.Should().BeTrue();
            (await db.AutorizacionesLecturaTrabajadores.CountAsync(t => t.AutorizacionId == vigente.Id)).Should().Be(2);

            autorizaciones.Single(a => a.ContratoAccesoId == contratos[InboundDemoSeeder.Contratos.MoncayoFinalizado])
                .EstaRevocada.Should().BeTrue();
            return 0;
        });

        foreach (var otro in new[] { InboundDemoSeeder.NombreTenantRibera, InboundDemoSeeder.NombreTenantSomontano })
            (await EnTenantAsync(otro, db => db.AutorizacionesLecturaDocumental.CountAsync())).Should().Be(0, $"{otro} no autoriza ninguna lectura");
    }

    [Fact]
    public async Task Ningun_correo_sembrado_es_entregable()
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var tenants = await IdsAsync([.. InboundDemoSeeder.NombresTenants]);
        tenants.Should().HaveCount(4);

        (await bootstrap.Users.IgnoreQueryFilters().Where(u => tenants.Contains(u.TenantId)).Select(u => u.Email!).ToListAsync())
            .Should().HaveCount(5).And.OnlyContain(e => e.EndsWith("@caemanager.local"));
    }

    [Fact]
    public async Task Una_segunda_siembra_no_escribe_nada()
    {
        var antes = await ContarAsync();
        await fixture.SembrarAsync();
        (await ContarAsync()).Should().BeEquivalentTo(antes);
    }

    private async Task<Dictionary<string, int>> ContarAsync()
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var tenants = await IdsAsync([.. InboundDemoSeeder.NombresTenants]);
        return new Dictionary<string, int>
        {
            ["Tenants"] = tenants.Count,
            ["Usuarios"] = await bootstrap.Users.IgnoreQueryFilters().CountAsync(u => tenants.Contains(u.TenantId)),
            ["Empresas"] = await bootstrap.Empresas.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Centros"] = await bootstrap.Centros.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Trabajadores"] = await bootstrap.Trabajadores.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Documentos"] = await bootstrap.Documentos.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["ContratosAcceso"] = await bootstrap.ContratosAcceso.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["HistorialVigencia"] = await bootstrap.HistorialVigenciaContratos.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Plantillas"] = await bootstrap.PlantillasRequisitos.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Autorizaciones"] = await bootstrap.AutorizacionesLecturaDocumental.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["AutorizacionesTrabajador"] = await bootstrap.AutorizacionesLecturaTrabajadores.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Carteras"] = await bootstrap.AsignacionesCartera.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.PropietarioTenantId)),
        };
    }

    private async Task<List<Guid>> IdsAsync(params string[] nombres)
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        return await bootstrap.Tenants.Where(t => nombres.Contains(t.Nombre)).Select(t => t.Id).ToListAsync();
    }

    private async Task<Guid> UsuarioIdAsync(string email)
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        return await bootstrap.Users.IgnoreQueryFilters().Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
    }

    /// <summary>Lee como cae_app_runtime dentro del ámbito del Tenant: lo que no sea de ese Tenant no se ve.</summary>
    private async Task<T> EnTenantAsync<T>(string nombreTenant, Func<CaeManagerDbContext, Task<T>> leer)
    {
        var tenantId = (await IdsAsync(nombreTenant)).Single();
        using var ambito = fixture.Arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using (AmbitoTenantExplicito.Establecer(tenantId))
            return await leer(contexto);
    }
}

/// <summary>La guarda de <see cref="InboundDemoSeeder"/>, sin base de datos: decide antes de leer o escribir nada.</summary>
public class InboundDemoSeederArranqueTests
{
    [Theory]
    [InlineData("Production", true, true, "*solo puede activarse en Development*")]
    [InlineData("Staging", true, true, "*solo puede activarse en Development*")]
    [InlineData("Development", true, true, null)]
    [InlineData("Production", false, true, null)]
    [InlineData("Production", true, false, null)]
    public void La_guarda_rechaza_todo_entorno_que_no_sea_Development_y_es_inerte_sin_las_claves(
        string entorno, bool datosPrueba, bool inbound, string? mensajeEsperado)
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatosPrueba:Activo"] = datosPrueba.ToString(),
            [InboundDemoSeeder.ClaveConfiguracion] = inbound.ToString()
        }).Build();

        var llamada = () => InboundDemoSeeder.RechazarFueraDeDevelopment(configuracion, new EntornoDePrueba(entorno));

        if (mensajeEsperado is null)
            llamada.Should().NotThrow();
        else
            llamada.Should().Throw<InvalidOperationException>().WithMessage(mensajeEsperado);
    }

    [Fact]
    public async Task Fuera_de_Development_la_siembra_lanza_antes_de_escribir_nada()
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatosPrueba:Activo"] = "true",
            [InboundDemoSeeder.ClaveConfiguracion] = "true"
        }).Build();

        var siembra = () => InboundDemoSeeder.SeedAsync(
            dbContext: null!, userManager: null!, userStore: null!, configuracion, new EntornoDePrueba("Staging"), NullLogger.Instance);

        await siembra.Should().ThrowAsync<InvalidOperationException>().WithMessage("*solo puede activarse en Development*");
    }
}
