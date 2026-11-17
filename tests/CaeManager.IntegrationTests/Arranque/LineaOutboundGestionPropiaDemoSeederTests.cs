using CaeManager.Application.Common;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
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
/// <b>La línea Outbound de gestión propia (P) de la siembra de Development la lleva el Tenant de demostración que gestiona su
/// CAE con su propio personal, y ningún otro; una segunda pasada no escribe nada.</b> Corre sobre PostgreSQL real con el
/// <see cref="CaeManagerDbContext"/> como cae_app_runtime (RLS efectiva), y las siembras que crean Tenants de demostración de
/// las que decide la tabla de la PR (cobertura: Alfa, Beta y su Operador CAE externo; Inbound: el Tenant TCE y tres TEC) corren
/// antes que la línea, en el orden de Program.cs. Cada recuento se lee dentro del ámbito de su Tenant, y se recorren
/// <b>todos</b> los Tenants de la base, así que una fila sembrada en el Tenant equivocado se vería.
/// </summary>
public sealed class LineaOutboundGestionPropiaDemoSeederFixture : IAsyncLifetime
{
    internal ArnesDeArranqueRuntime Arnes { get; private set; } = null!;
    internal IConfiguration Configuracion { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        Configuracion = new ConfigurationBuilder()
            .AddConfiguration(Arnes.Servicios.GetRequiredService<IConfiguration>())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CoberturaPantallasSeeder.ClaveConfiguracion] = "true",
                [InboundDemoSeeder.ClaveConfiguracion] = "true"
            })
            .Build();

        using var ambito = Arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        await CoberturaPantallasSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(),
            Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        await InboundDemoSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IUserStore<ApplicationUser>>(), new AlmacenDeSiembraInboundEnMemoria(), Configuracion,
            EntornoDePrueba.Desarrollo, NullLogger.Instance);
        await SembrarLineasAsync();
    }

    /// <summary>Como en Program.cs: las dos líneas del Producto Contratado, con el contexto de bootstrap y al final.</summary>
    internal async Task SembrarLineasAsync()
    {
        using var ambito = Arnes.Servicios.CreateScope();
        await using var bootstrap = ambito.ServiceProvider.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        await LineaInboundDemoSeeder.SeedAsync(bootstrap, Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
    }

    public Task DisposeAsync() => Arnes.DisposeAsync().AsTask();
}

public class LineaOutboundGestionPropiaDemoSeederTests(LineaOutboundGestionPropiaDemoSeederFixture fixture)
    : IClassFixture<LineaOutboundGestionPropiaDemoSeederFixture>
{
    [Fact]
    public async Task El_Tenant_Beta_tiene_la_linea_P_vigente_con_los_datos_de_demostracion()
    {
        var lineas = await LineasDeAsync(CoberturaPantallasSeeder.NombreTenantBeta);

        var linea = lineas.Should().ContainSingle().Subject;
        linea.Linea.Should().Be(LineaProductoContratado.OutboundGestionPropia);
        linea.Estado.Should().Be(EstadoProductoContratado.Vigente);
        linea.VigenteHastaUtc.Should().BeNull();
        linea.EstaVigenteEn(DateTime.UtcNow).Should().BeTrue("el inicio nunca es futuro");
        linea.PagadorStripeCustomerId.Should().Be(LineaOutboundGestionPropiaDemoSeeder.PagadorDemo);
        linea.Tramo.Should().Be(TramoTrabajadoresActivos.De1A10);
        linea.Modalidad.Should().Be(ModalidadSuscripcionAlta.Anual);
    }

    [Fact]
    public async Task Ningun_otro_Tenant_de_la_base_tiene_la_linea_P()
    {
        var beta = (await IdsAsync(CoberturaPantallasSeeder.NombreTenantBeta)).Single();

        var conLineaP = new List<Guid>();
        foreach (var (id, _) in await TodosLosTenantsAsync())
            if ((await LineasDeAsync(id)).Any(p => p.Linea == LineaProductoContratado.OutboundGestionPropia))
                conLineaP.Add(id);

        conLineaP.Should().BeEquivalentTo(new[] { beta },
            "solo el Tenant de gestión propia la lleva: ni plataforma, ni Operador CAE externo, ni gestión externa, ni Inbound");
    }

    [Fact]
    public async Task Los_Tenants_excluidos_que_la_siembra_si_crea_no_la_tienen_y_el_TCE_conserva_solo_su_linea_Inbound()
    {
        // Control del instrumento: la prueba de arriba recorre todos los Tenants, pero esto demuestra que los excluidos
        // existen en esta base (un nombre que no existiera no probaría nada) y que se leen sus filas.
        foreach (var nombre in new[]
                 {
                     CoberturaPantallasSeeder.NombreTenantOperador, CoberturaPantallasSeeder.NombreTenantAlfa,
                     InboundDemoSeeder.NombreTenantMoncayo, InboundDemoSeeder.NombreTenantRibera, InboundDemoSeeder.NombreTenantSomontano
                 })
        {
            (await IdsAsync(nombre)).Should().ContainSingle($"{nombre} lo siembra esta base");
            (await LineasDeAsync(nombre)).Should().BeEmpty($"{nombre} no gestiona su CAE con su propio personal");
        }

        (await LineasDeAsync(InboundDemoSeeder.NombreTenantTce)).Should().ContainSingle()
            .Which.Linea.Should().Be(LineaProductoContratado.Inbound);
    }

    [Fact]
    public async Task El_Tenant_Beta_no_tiene_delegacion_de_Operador_CAE_externo_ni_Asignacion_de_Operacion_externa_y_Alfa_si()
    {
        // Beta no tiene ninguna delegación ni operación externa (por eso la guarda lo deja pasar) y Alfa, Tenant propietario con
        // gestión externa, tiene las dos: sin ese control, un cero de Beta no distinguiría «no hay nada» de «esta lectura no ve
        // nada». Ojo con lo que prueba: es una copia del predicado de la guarda con cae_app_runtime, no las lecturas del seeder
        // con el rol propietario; demuestra el estado de la base de Development, no que la guarda funcione (eso lo prueban
        // los tests de la clase acotada, de abajo, que ejecutan el seeder).
        var beta = (await IdsAsync(CoberturaPantallasSeeder.NombreTenantBeta)).Single();
        var alfa = (await IdsAsync(CoberturaPantallasSeeder.NombreTenantAlfa)).Single();

        (await TieneGestionExternaAsync(alfa)).Should().Be((true, true),
            "Alfa tiene una delegación activa de su Operador CAE externo y una Asignación de Operación externa, y la lectura las ve");
        (await TieneGestionExternaAsync(beta)).Should().Be((false, false),
            "Beta gestiona su CAE con su propio personal: sin delegación de Operador CAE externo ni Asignación de Operación externa");
    }

    [Fact]
    public async Task Una_segunda_siembra_no_duplica_ninguna_linea()
    {
        await fixture.SembrarLineasAsync();
        await fixture.SembrarLineasAsync();

        (await LineasDeAsync(CoberturaPantallasSeeder.NombreTenantBeta)).Should().ContainSingle();
        (await LineasDeAsync(InboundDemoSeeder.NombreTenantTce)).Should().ContainSingle();
    }

    private async Task<List<ProductoContratado>> LineasDeAsync(string nombreTenant) =>
        await LineasDeAsync((await IdsAsync(nombreTenant)).Single());

    private async Task<List<ProductoContratado>> LineasDeAsync(Guid tenantId)
    {
        using var ambito = fixture.Arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using (AmbitoTenantExplicito.Establecer(tenantId))
            return await contexto.ProductosContratados.ToListAsync();
    }

    /// <summary>Copia del predicado de las dos lecturas de la guarda de gestión propia, con el contexto de tráfico y dentro del ámbito del Tenant.</summary>
    private async Task<(bool Delegacion, bool Operacion)> TieneGestionExternaAsync(Guid tenantId)
    {
        using var ambito = fixture.Arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            var delegacion = await contexto.DelegacionesTenant.AnyAsync(
                d => d.TenantClienteId == tenantId && d.Proposito == PropositoDelegacion.OperadorExterno && d.Activa);
            var operacion = await contexto.AsignacionesOperacion.AnyAsync(
                o => !o.EsRaiz && o.PropietarioTenantId == tenantId && o.OperadorTenantId != tenantId
                     && o.Servicio == ServicioCae.Outbound);
            return (delegacion, operacion);
        }
    }

    private async Task<List<Guid>> IdsAsync(string nombre)
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        return await bootstrap.Tenants.Where(t => t.Nombre == nombre).Select(t => t.Id).ToListAsync();
    }

    private async Task<List<(Guid Id, string Nombre)>> TodosLosTenantsAsync()
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        return (await bootstrap.Tenants.Select(t => new { t.Id, t.Nombre }).ToListAsync()).Select(t => (t.Id, t.Nombre)).ToList();
    }
}

/// <summary>
/// <b>Lo que acota la línea P cuando el Tenant no es suyo</b>, en una base aparte: un Tenant que se llama como el Tenant Beta
/// pero no lleva el marcador de datos de demo no recibe nada (podría ser real), el que sí lo lleva la recibe, uno de la lista
/// que resulta ser Operador CAE externo, tener una delegación activa de Operador CAE externo o tener una Asignación de
/// Operación Outbound externa (aunque no tenga delegación) no la recibe, una delegación de Soporte o una operación interna
/// no la impiden, y fuera de Development no escribe aunque <c>DatosPrueba:Activo</c> esté activo.
/// </summary>
public sealed class LineaOutboundGestionPropiaDemoSeederAcotadaTests : IAsyncLifetime
{
    private ArnesDeArranqueRuntime _arnes = null!;
    private IConfiguration _configuracion = null!;

    public async Task InitializeAsync()
    {
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        _configuracion = _arnes.Servicios.GetRequiredService<IConfiguration>();
    }

    public Task DisposeAsync() => _arnes.DisposeAsync().AsTask();

    private async Task<int> LineasPAsync(Guid tenantId)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using (AmbitoTenantExplicito.Establecer(tenantId))
            return await contexto.ProductosContratados.CountAsync(p => p.Linea == LineaProductoContratado.OutboundGestionPropia);
    }

    [Fact]
    public async Task Un_Tenant_con_el_nombre_de_Beta_pero_sin_marcador_de_demo_no_recibe_la_linea_y_con_marcador_si()
    {
        using var ambito = _arnes.Servicios.CreateScope();
        await using var bootstrap = ambito.ServiceProvider.GetRequiredService<FabricaContextoDeBootstrap>().Crear();

        var ajeno = new Tenant(CoberturaPantallasSeeder.NombreTenantBeta, PerfilVocabularioTenant.ClienteDirecto);
        using (AmbitoTenantExplicito.Establecer(ajeno.Id))
        {
            bootstrap.Tenants.Add(ajeno);
            await bootstrap.SaveChangesAsync();
        }

        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        (await LineasPAsync(ajeno.Id)).Should().Be(0, "sin el marcador de datos de demo, el Tenant podría ser real");

        // El mismo Tenant, ya marcado: la recibe, una sola vez.
        using (AmbitoTenantExplicito.Establecer(ajeno.Id))
        {
            ajeno.MarcarDatosDemoCompletados();
            await bootstrap.SaveChangesAsync();
        }

        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        (await LineasPAsync(ajeno.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Un_Tenant_de_la_lista_habilitado_como_Operador_CAE_externo_no_recibe_la_linea()
    {
        using var ambito = _arnes.Servicios.CreateScope();
        await using var bootstrap = ambito.ServiceProvider.GetRequiredService<FabricaContextoDeBootstrap>().Crear();

        var operador = new Tenant(CoberturaPantallasSeeder.NombreTenantBeta, PerfilVocabularioTenant.Consultora);
        operador.HabilitarComoOperadorCaeExterno();
        operador.MarcarDatosDemoCompletados();
        using (AmbitoTenantExplicito.Establecer(operador.Id))
        {
            bootstrap.Tenants.Add(operador);
            await bootstrap.SaveChangesAsync();
        }

        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);

        (await LineasPAsync(operador.Id)).Should().Be(0, "un Operador CAE externo no gestiona su CAE con su propio personal");
    }

    [Fact]
    public async Task Un_Tenant_de_la_lista_con_delegacion_activa_de_Operador_CAE_externo_no_recibe_la_linea_y_sin_ella_si()
    {
        using var ambito = _arnes.Servicios.CreateScope();
        await using var bootstrap = ambito.ServiceProvider.GetRequiredService<FabricaContextoDeBootstrap>().Crear();

        var propietario = new Tenant(CoberturaPantallasSeeder.NombreTenantBeta, PerfilVocabularioTenant.ClienteDirecto);
        propietario.MarcarDatosDemoCompletados();
        var operador = new Tenant("Operador CAE externo de la prueba de la línea P", PerfilVocabularioTenant.Consultora);
        operador.HabilitarComoOperadorCaeExterno();
        foreach (var tenant in new[] { propietario, operador })
            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                bootstrap.Tenants.Add(tenant);
                await bootstrap.SaveChangesAsync();
            }

        var delegacion = new DelegacionTenant(operador.Id, propietario.Id);
        using (AmbitoTenantExplicito.Establecer(operador.Id))
        {
            bootstrap.DelegacionesTenant.Add(delegacion);
            await bootstrap.SaveChangesAsync();
        }

        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        (await LineasPAsync(propietario.Id)).Should().Be(0, "con un Operador CAE externo activo, el Tenant propietario tiene gestión externa");

        // Control del instrumento: la misma siembra sobre el mismo Tenant, con la delegación revocada, sí escribe; el cero de
        // arriba lo causó la guarda y no otra cosa.
        using (AmbitoTenantExplicito.Establecer(operador.Id))
        {
            delegacion.Desactivar();
            await bootstrap.SaveChangesAsync();
        }

        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        (await LineasPAsync(propietario.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Un_Tenant_de_la_lista_con_una_Asignacion_de_Operacion_externa_y_sin_delegacion_no_recibe_la_linea()
    {
        // Aísla la segunda lectura de la guarda: sin ninguna DelegacionTenant, solo la operación externa puede omitir al Tenant.
        // Una mutación que quitara esa lectura dejaría pasar la línea y haría fallar esta prueba. El filtro
        // OperadorTenantId != tenant.Id no lo fija esta prueba (su operación es externa con cualquier filtro), sino la de la
        // operación interna, de abajo.
        using var ambito = _arnes.Servicios.CreateScope();
        await using var bootstrap = ambito.ServiceProvider.GetRequiredService<FabricaContextoDeBootstrap>().Crear();

        var propietario = new Tenant(CoberturaPantallasSeeder.NombreTenantBeta, PerfilVocabularioTenant.ClienteDirecto);
        propietario.MarcarDatosDemoCompletados();
        var operador = new Tenant("Operador CAE externo de la prueba de la línea P", PerfilVocabularioTenant.Consultora);
        operador.HabilitarComoOperadorCaeExterno();
        foreach (var tenant in new[] { propietario, operador })
            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                bootstrap.Tenants.Add(tenant);
                await bootstrap.SaveChangesAsync();
            }

        var ahora = DateTime.UtcNow;
        using (AmbitoTenantExplicito.Establecer(propietario.Id))
        {
            bootstrap.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
                propietario.Id, operador.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora, null, ahora));
            await bootstrap.SaveChangesAsync();
        }

        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);

        (await LineasPAsync(propietario.Id)).Should().Be(0, "una Asignación de Operación externa es gestión externa, aunque no haya delegación");
    }

    [Fact]
    public async Task Una_Asignacion_de_Operacion_interna_no_cuenta_como_gestion_externa_y_el_Tenant_recibe_la_linea()
    {
        // Fija el filtro OperadorTenantId != tenant.Id de la segunda lectura de la guarda: una operación interna (operador
        // igual al propio Tenant propietario, que no es la raíz) es gestión propia y no debe omitir al Tenant. Si el filtro se
        // quitara, esta operación contaría como externa y la línea no se escribiría. Se acota a una Relación Empresarial porque
        // una interna universal es la raíz, que la guarda ya excluye con !EsRaiz y no distinguiría el filtro.
        using var ambito = _arnes.Servicios.CreateScope();
        await using var bootstrap = ambito.ServiceProvider.GetRequiredService<FabricaContextoDeBootstrap>().Crear();

        var propietario = new Tenant(CoberturaPantallasSeeder.NombreTenantBeta, PerfilVocabularioTenant.ClienteDirecto);
        propietario.MarcarDatosDemoCompletados();
        using (AmbitoTenantExplicito.Establecer(propietario.Id))
        {
            bootstrap.Tenants.Add(propietario);
            await bootstrap.SaveChangesAsync();
        }

        var ahora = DateTime.UtcNow;
        var clienteEmpresarial = Empresa.CrearComoCliente("Cliente de la operación interna", "B10380186", false, null, null);
        using (AmbitoTenantExplicito.Establecer(propietario.Id))
        {
            bootstrap.Empresas.Add(clienteEmpresarial);
            await bootstrap.SaveChangesAsync();

            bootstrap.AsignacionesOperacion.Add(AsignacionOperacion.Interna(
                propietario.Id, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteEmpresarial.Id), ahora, null, ahora));
            await bootstrap.SaveChangesAsync();
        }

        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);

        (await LineasPAsync(propietario.Id)).Should().Be(1,
            "una Asignación de Operación interna (operador = el propio Tenant) no es gestión externa");
    }

    [Fact]
    public async Task Una_delegacion_activa_de_Soporte_no_cuenta_como_Operador_CAE_externo_y_el_Tenant_recibe_la_linea()
    {
        // Fija el filtro de propósito de la primera lectura de la guarda: una DelegacionTenant de Soporte activa y vigente no
        // es un Operador CAE externo (Soporte TALVEG no es Operador CAE) y no debe omitir al Tenant. Si el filtro
        // Proposito == OperadorExterno se quitara, esta delegación contaría y la línea no se escribiría.
        using var ambito = _arnes.Servicios.CreateScope();
        await using var bootstrap = ambito.ServiceProvider.GetRequiredService<FabricaContextoDeBootstrap>().Crear();

        var propietario = new Tenant(CoberturaPantallasSeeder.NombreTenantBeta, PerfilVocabularioTenant.ClienteDirecto);
        propietario.MarcarDatosDemoCompletados();
        var plataforma = new Tenant("Tenant de plataforma de la prueba de la línea P", PerfilVocabularioTenant.Consultora);
        foreach (var tenant in new[] { propietario, plataforma })
            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                bootstrap.Tenants.Add(tenant);
                await bootstrap.SaveChangesAsync();
            }

        var ahora = DateTime.UtcNow;
        var soporte = DelegacionTenant.ParaSoporte(plataforma.Id, propietario.Id);
        soporte.ActivarParaSoporte("Revisión de una pantalla de demostración", ahora.AddHours(2), ahora);
        using (AmbitoTenantExplicito.Establecer(plataforma.Id))
        {
            bootstrap.DelegacionesTenant.Add(soporte);
            await bootstrap.SaveChangesAsync();
        }

        // Control del instrumento: la delegación de Soporte está activa y vigente y la guarda la tendría delante.
        (await bootstrap.DelegacionesTenant.AnyAsync(
            d => d.TenantClienteId == propietario.Id && d.Proposito == PropositoDelegacion.Soporte && d.Activa)).Should().BeTrue();

        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);

        (await LineasPAsync(propietario.Id)).Should().Be(1,
            "una delegación de Soporte no es un Operador CAE externo, aunque esté activa");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Fuera_de_Development_no_escribe_aunque_el_Tenant_este_marcado(string entorno)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        await using var bootstrap = ambito.ServiceProvider.GetRequiredService<FabricaContextoDeBootstrap>().Crear();

        var marcado = new Tenant(CoberturaPantallasSeeder.NombreTenantBeta, PerfilVocabularioTenant.ClienteDirecto);
        marcado.MarcarDatosDemoCompletados();
        using (AmbitoTenantExplicito.Establecer(marcado.Id))
        {
            bootstrap.Tenants.Add(marcado);
            await bootstrap.SaveChangesAsync();
        }

        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, _configuracion, new EntornoDePrueba(entorno), NullLogger.Instance);

        (await LineasPAsync(marcado.Id)).Should().Be(0);
    }
}
