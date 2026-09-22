using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Piloto Outbound, D-8 (Inc. 3). Dos hechos separados sobre
/// <see cref="AlcanceDatosService"/>:
/// <list type="number">
/// <item>Visibilidad estructural: la Empresa propia del Tenant propietario es parte del contexto
/// de un Gestor CAE con cartera válida en ese Tenant, sin que exista Centro, Relación Empresarial
/// ni Asignación Trabajador→Centro (no se crea ningún objeto artificial para darle visibilidad).</item>
/// <item>Invalidación: una instancia ya usada (Scoped: en Blazor Server dura lo que el circuito)
/// no conserva una visión obsoleta tras <see cref="IInvalidadorAlcance.Invalidar"/>.</item>
/// </list>
/// Negativos de la visibilidad: rol Cliente, cartera vacía, otro Tenant y Empresa que no es propia.
/// </summary>
public class AlcanceEmpresaPropiaPorCarteraTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenant);
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private sealed record Escenario(Guid Cliente, Guid Propia, Guid Otra);

    /// <summary>Tenant con Cliente empresarial y Empresa propia SIN Centro ni Relación Empresarial, más una Empresa no propia.</summary>
    private async Task<Escenario> SembrarEstructuraAsync(Guid tenant, string sufijo, string cifCliente, string cifPropia, string cifOtra)
    {
        await using var contexto = CrearContexto(tenant);
        var cliente = Empresa.CrearComoCliente($"Cliente empresarial {sufijo}", cifCliente, false, null, null);
        var propia = new Empresa($"Empresa propia {sufijo}", cifPropia);
        var otra = Empresa.CrearComoCliente($"Otra empresa {sufijo}", cifOtra, false, null, null);
        contexto.Empresas.AddRange(cliente, propia, otra);
        await contexto.SaveChangesAsync();
        return new Escenario(cliente.Id, propia.Id, otra.Id);
    }

    private async Task<Guid> OtorgarCarteraAsync(Guid tenant, Guid clienteId)
    {
        await using var contexto = CrearContexto(tenant);
        var usuarioId = Guid.NewGuid();
        var ahora = DateTime.UtcNow;
        var raiz = AsignacionOperacion.Raiz(tenant, ServicioCae.Outbound, ahora, ahora);
        contexto.AsignacionesOperacion.Add(raiz);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
            raiz, usuarioId, AmbitoAsignacion.DeRelacionCliente(clienteId), ahora, null, ahora));
        await contexto.SaveChangesAsync();
        return usuarioId;
    }

    private AlcanceDatosService CrearServicio(CaeManagerDbContext contexto, Guid usuarioId, string rol, Guid tenant) =>
        new(contexto, new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: tenant),
            new TenantActualAmbiental { TenantId = tenant }, new SesionPrivilegiadaAusente());

    [Fact]
    public async Task Gestor_con_cartera_ve_la_Empresa_propia_sin_Centro_ni_Relacion()
    {
        var e = await SembrarEstructuraAsync(_tenant, "A", "B10380186", "B10380194", "B10380202");
        var gestor = await OtorgarCarteraAsync(_tenant, e.Cliente);

        await using var contexto = CrearContexto(_tenant);
        var visibles = await CrearServicio(contexto, gestor, "GestorCae", _tenant).ObtenerEmpresaIdsVisiblesAsync();

        visibles.Should().NotBeNull().And.Contain(e.Propia,
            "la Empresa propia es parte estructural del Tenant que el Gestor CAE tiene en cartera");
    }

    [Fact]
    public async Task La_visibilidad_de_la_Empresa_propia_no_convierte_en_visible_otra_empresa_no_propia()
    {
        var e = await SembrarEstructuraAsync(_tenant, "A", "B10380186", "B10380194", "B10380202");
        var gestor = await OtorgarCarteraAsync(_tenant, e.Cliente);

        await using var contexto = CrearContexto(_tenant);
        var visibles = await CrearServicio(contexto, gestor, "GestorCae", _tenant).ObtenerEmpresaIdsVisiblesAsync();

        visibles.Should().NotContain(e.Otra, "solo la Empresa propia entra por su condición de propia");
        visibles.Should().NotContain(e.Cliente, "el Cliente empresarial no es una Empresa visible por sí mismo");
    }

    [Fact]
    public async Task Gestor_sin_cartera_no_ve_la_Empresa_propia()
    {
        var e = await SembrarEstructuraAsync(_tenant, "A", "B10380186", "B10380194", "B10380202");

        await using var contexto = CrearContexto(_tenant);
        var visibles = await CrearServicio(contexto, Guid.NewGuid(), "GestorCae", _tenant).ObtenerEmpresaIdsVisiblesAsync();

        visibles.Should().NotBeNull().And.BeEmpty("sin cartera el alcance es [] (falla cerrado), no null");
        visibles.Should().NotContain(e.Propia);
    }

    [Fact]
    public async Task Cartera_en_un_Tenant_no_da_la_Empresa_propia_de_otro_Tenant()
    {
        var a = await SembrarEstructuraAsync(_tenant, "A", "B10380186", "B10380194", "B10380202");
        var b = await SembrarEstructuraAsync(_otroTenant, "B", "B10380210", "B10380228", "B10380236");
        var gestor = await OtorgarCarteraAsync(_tenant, a.Cliente);

        await using var contextoA = CrearContexto(_tenant);
        var enA = await CrearServicio(contextoA, gestor, "GestorCae", _tenant).ObtenerEmpresaIdsVisiblesAsync();
        enA.Should().Contain(a.Propia).And.NotContain(b.Propia);

        await using var contextoB = CrearContexto(_otroTenant);
        var enB = await CrearServicio(contextoB, gestor, "GestorCae", _otroTenant).ObtenerEmpresaIdsVisiblesAsync();
        enB.Should().NotBeNull().And.BeEmpty("el Gestor no tiene cartera en el otro Tenant");
    }

    [Fact]
    public async Task El_rol_Cliente_no_gana_la_Empresa_propia_por_estructura()
    {
        var e = await SembrarEstructuraAsync(_tenant, "A", "B10380186", "B10380194", "B10380202");
        var usuarioPortal = Guid.NewGuid();
        await using (var contexto = CrearContexto(_tenant))
        {
            contexto.Users.Add(new ApplicationUser
            {
                Id = usuarioPortal,
                UserName = $"portal-{usuarioPortal:N}@ejemplo.test",
                Email = $"portal-{usuarioPortal:N}@ejemplo.test",
                ClienteId = e.Cliente,
                TenantId = _tenant
            });
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto(_tenant);
        var visibles = await CrearServicio(lectura, usuarioPortal, "Cliente", _tenant).ObtenerEmpresaIdsVisiblesAsync();

        visibles.Should().NotContain(e.Propia,
            "el usuario de portal solo ve las empresas que trabajan para su Cliente, no la estructura del Tenant");
    }

    [Fact]
    public async Task Sin_invalidar_la_instancia_conserva_la_vision_y_tras_invalidar_la_actualiza()
    {
        var e = await SembrarEstructuraAsync(_tenant, "A", "B10380186", "B10380194", "B10380202");
        var gestor = await OtorgarCarteraAsync(_tenant, e.Cliente);

        await using var contextoCircuito = CrearContexto(_tenant);
        var circuito = CrearServicio(contextoCircuito, gestor, "GestorCae", _tenant);
        (await circuito.ObtenerTrabajadorIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty();

        Guid trabajadorId;
        await using (var escritura = CrearContexto(_tenant))
        {
            var centro = new Centro(e.Cliente, e.Propia, "Centro A");
            escritura.Centros.Add(centro);
            var trabajador = Trabajador.DeEmpresa(e.Propia, "Nora", "Vidal", "22334455Y");
            escritura.Trabajadores.Add(trabajador);
            await escritura.SaveChangesAsync();
            escritura.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, new DateOnly(2026, 1, 1)));
            await escritura.SaveChangesAsync();
            trabajadorId = trabajador.Id;
        }

        (await circuito.ObtenerTrabajadorIdsVisiblesAsync()).Should().NotContain(trabajadorId,
            "la memoización por instancia es deliberada: no se convierte en un servicio sin caché");

        ((IInvalidadorAlcance)circuito).Invalidar();

        (await circuito.ObtenerTrabajadorIdsVisiblesAsync()).Should().Contain(trabajadorId,
            "tras invalidar, el mismo scope ve el alcance actualizado");
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
