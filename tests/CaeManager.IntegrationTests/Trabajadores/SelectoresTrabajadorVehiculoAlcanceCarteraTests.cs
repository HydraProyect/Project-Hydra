using CaeManager.Application.Plataforma;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculosParaSelector;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Trabajadores;

/// <summary>
/// Alcance de cartera en los selectores de Trabajador y Vehículo. Un selector que cuelga algo
/// de un Trabajador o un Vehículo ya existente (Documento, Gestión, Proyecto, documento
/// generado) solo ofrece los que están dentro del alcance de datos del usuario: un Gestor CAE no
/// ve el nombre, el DNI ni la matrícula de lo que queda fuera de su Asignación de Cartera. El
/// selector de "base general" de Trabajador (Asignación masiva: crear la primera Asignación) sigue
/// ofreciendo todo el Tenant a propósito.
///
/// Composición real: handlers de Application con <see cref="AlcanceDatosService"/> real sobre
/// PostgreSQL, sin fakes de alcance — la cartera sale de una <see cref="AsignacionCartera"/> de
/// verdad.
///
/// Escenario, Tenant propietario A (el B es gemelo):
/// <list type="bullet">
/// <item>Cliente empresarial dentro (en la cartera del Gestor CAE) con un Centro donde trabaja la
/// Empresa propia; un Trabajador de la Empresa propia asignado a ese Centro; un Vehículo de la
/// Empresa propia.</item>
/// <item>Cliente empresarial fuera con un Centro donde trabaja una Subcontrata (no propia, sin
/// Relación Empresarial con el de dentro); un Trabajador asignado y un Vehículo de esa
/// Subcontrata.</item>
/// </list>
/// Lo de fuera nunca es de la Empresa propia: la Empresa propia es estructuralmente visible para
/// cualquier Gestor CAE con cartera en el Tenant (D-8), así que no serviría como negativo.
/// </summary>
public class SelectoresTrabajadorVehiculoAlcanceCarteraTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();

    private Escenario _a = null!;
    private Escenario _b = null!;
    private Guid _gestorA;

    private sealed record Escenario(
        Guid ClienteDentro, Guid TrabajadorDentro, Guid VehiculoDentro,
        Guid TrabajadorFuera, Guid VehiculoFuera);

    public async Task InitializeAsync()
    {
        await using (var contexto = CrearContexto(_tenant))
            await contexto.Database.MigrateAsync();

        _a = await SembrarAsync(_tenant, "A", ["B10380244", "B10380251", "B10380269"]);
        _b = await SembrarAsync(_otroTenant, "B", ["B10380277", "B10380285", "B10380293"]);
        _gestorA = await OtorgarCarteraAsync(_tenant, _a.ClienteDentro);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Trabajadores, alcance Cartera ────────────────────────────────────

    /// <summary>
    /// Control positivo: la misma composición y el mismo Gestor CAE SÍ reciben al Trabajador de
    /// su cartera. Sin él, el negativo podría estar en verde por un selector que no devuelve nada.
    /// </summary>
    [Fact]
    public async Task Gestor_CAE_ve_en_el_selector_de_cartera_al_Trabajador_de_su_cartera()
    {
        var ids = await TrabajadoresAsync(_tenant, _gestorA, Roles.GestorCae, AlcanceSelectorTrabajadores.Cartera);

        ids.Should().Contain(_a.TrabajadorDentro);
    }

    [Fact]
    public async Task Gestor_CAE_no_ve_en_el_selector_de_cartera_al_Trabajador_fuera_de_su_cartera()
    {
        var ids = await TrabajadoresAsync(_tenant, _gestorA, Roles.GestorCae, AlcanceSelectorTrabajadores.Cartera);

        ids.Should().NotContain(_a.TrabajadorFuera,
            "existe en el Tenant, pero no está en la Asignación de Cartera del Gestor CAE: su nombre y DNI no se ofrecen");
    }

    [Fact]
    public async Task Gestor_CAE_sin_cartera_no_ve_ningun_Trabajador_en_el_selector_de_cartera()
    {
        var ids = await TrabajadoresAsync(_tenant, Guid.NewGuid(), Roles.GestorCae, AlcanceSelectorTrabajadores.Cartera);

        ids.Should().BeEmpty("sin cartera el alcance es [] (falla cerrado), no «sin restricción»");
    }

    /// <summary>
    /// El rol Cliente (usuario de portal) ve lo de su propio Cliente empresarial —es el alcance de
    /// lectura que el portal existe para enseñar— y nada del otro.
    /// </summary>
    [Fact]
    public async Task El_rol_Cliente_solo_ve_en_el_selector_de_cartera_los_Trabajadores_de_su_Cliente_empresarial()
    {
        var usuarioPortal = await CrearUsuarioPortalAsync(_tenant, _a.ClienteDentro);

        var ids = await TrabajadoresAsync(_tenant, usuarioPortal, Roles.Cliente, AlcanceSelectorTrabajadores.Cartera);

        ids.Should().Contain(_a.TrabajadorDentro);
        ids.Should().NotContain(_a.TrabajadorFuera);
    }

    [Fact]
    public async Task Administrador_ve_en_el_selector_de_cartera_a_todos_los_Trabajadores_del_Tenant()
    {
        var ids = await TrabajadoresAsync(_tenant, Guid.NewGuid(), Roles.Administrador, AlcanceSelectorTrabajadores.Cartera);

        ids.Should().BeEquivalentTo([_a.TrabajadorDentro, _a.TrabajadorFuera],
            "acceso total: sin restricción de cartera, pero solo el Tenant actual");
    }

    /// <summary>
    /// Otro Tenant, las dos direcciones: con el Tenant A activo, un Trabajador del Tenant B no
    /// existe (aislamiento de Tenant); con el Tenant B activo, el Gestor CAE no tiene cartera allí
    /// (aislamiento de cartera) — ni siquiera para lo que en B estaría "dentro".
    /// </summary>
    [Fact]
    public async Task La_cartera_de_un_Tenant_no_da_alcance_en_el_selector_sobre_otro_Tenant()
    {
        var desdeA = await TrabajadoresAsync(_tenant, _gestorA, Roles.GestorCae, AlcanceSelectorTrabajadores.Cartera);
        desdeA.Should().NotContain([_b.TrabajadorDentro, _b.TrabajadorFuera]);

        var desdeB = await TrabajadoresAsync(_otroTenant, _gestorA, Roles.GestorCae, AlcanceSelectorTrabajadores.Cartera);
        desdeB.Should().BeEmpty();
    }

    // ── Trabajadores, base general (excepción deliberada) ───────────────

    /// <summary>
    /// La excepción documentada: el selector de Asignación masiva ofrece todo el Tenant para poder
    /// crear la primera Asignación de un Trabajador que todavía no está en la cartera. Fija el
    /// contrato en las dos direcciones: si alguien acotara también la base general, la Asignación
    /// masiva dejaría de poder incorporar Trabajadores nuevos.
    /// </summary>
    [Fact]
    public async Task Gestor_CAE_ve_en_el_selector_de_base_general_todo_el_Tenant_y_solo_el_Tenant()
    {
        var ids = await TrabajadoresAsync(_tenant, _gestorA, Roles.GestorCae, AlcanceSelectorTrabajadores.BaseGeneralDelTenant);

        ids.Should().BeEquivalentTo([_a.TrabajadorDentro, _a.TrabajadorFuera]);
    }

    // ── Vehículos ────────────────────────────────────────────────────────

    [Fact]
    public async Task Gestor_CAE_ve_en_el_selector_el_Vehiculo_de_su_cartera_y_no_el_de_fuera()
    {
        var ids = await VehiculosAsync(_tenant, _gestorA, Roles.GestorCae);

        ids.Should().Contain(_a.VehiculoDentro, "control positivo");
        ids.Should().NotContain(_a.VehiculoFuera,
            "existe en el Tenant, pero su Subcontrata no está en la Asignación de Cartera del Gestor CAE");
    }

    [Fact]
    public async Task Gestor_CAE_sin_cartera_no_ve_ningun_Vehiculo_en_el_selector()
    {
        var ids = await VehiculosAsync(_tenant, Guid.NewGuid(), Roles.GestorCae);

        ids.Should().BeEmpty();
    }

    [Fact]
    public async Task El_rol_Cliente_no_ve_en_el_selector_el_Vehiculo_de_otro_Cliente_empresarial()
    {
        var usuarioPortal = await CrearUsuarioPortalAsync(_tenant, _a.ClienteDentro);

        var ids = await VehiculosAsync(_tenant, usuarioPortal, Roles.Cliente);

        ids.Should().Contain(_a.VehiculoDentro);
        ids.Should().NotContain(_a.VehiculoFuera);
    }

    [Fact]
    public async Task Administrador_ve_en_el_selector_todos_los_Vehiculos_del_Tenant()
    {
        var ids = await VehiculosAsync(_tenant, Guid.NewGuid(), Roles.Administrador);

        ids.Should().BeEquivalentTo([_a.VehiculoDentro, _a.VehiculoFuera]);
    }

    [Fact]
    public async Task La_cartera_de_un_Tenant_no_da_alcance_sobre_los_Vehiculos_de_otro_Tenant()
    {
        var desdeA = await VehiculosAsync(_tenant, _gestorA, Roles.GestorCae);
        desdeA.Should().NotContain([_b.VehiculoDentro, _b.VehiculoFuera]);

        var desdeB = await VehiculosAsync(_otroTenant, _gestorA, Roles.GestorCae);
        desdeB.Should().BeEmpty();
    }

    // ── Infraestructura del test ─────────────────────────────────────────

    private async Task<IReadOnlyList<Guid>> TrabajadoresAsync(
        Guid tenant, Guid usuarioId, string rol, AlcanceSelectorTrabajadores alcance)
    {
        await using var contexto = CrearContexto(tenant);
        var handler = new ObtenerTrabajadoresParaSelectorQueryHandler(contexto, CrearAlcance(contexto, tenant, usuarioId, rol));
        var resultado = await handler.Handle(new ObtenerTrabajadoresParaSelectorQuery(alcance), CancellationToken.None);
        return resultado.Select(t => t.Id).ToList();
    }

    private async Task<IReadOnlyList<Guid>> VehiculosAsync(Guid tenant, Guid usuarioId, string rol)
    {
        await using var contexto = CrearContexto(tenant);
        var handler = new ObtenerVehiculosParaSelectorQueryHandler(contexto, CrearAlcance(contexto, tenant, usuarioId, rol));
        var resultado = await handler.Handle(new ObtenerVehiculosParaSelectorQuery(), CancellationToken.None);
        return resultado.Select(v => v.Id).ToList();
    }

    private static AlcanceDatosService CrearAlcance(CaeManagerDbContext contexto, Guid tenant, Guid usuarioId, string rol) =>
        new(contexto, new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: tenant),
            new TenantActualAmbiental { TenantId = tenant }, new SesionPrivilegiadaAusente());

    private async Task<Escenario> SembrarAsync(Guid tenant, string sufijo, string[] cifs)
    {
        await using var contexto = CrearContexto(tenant);

        var clienteDentro = Empresa.CrearComoCliente($"Cliente empresarial dentro {sufijo}", cifs[0], false, null, null);
        var clienteFuera = Empresa.CrearComoCliente($"Cliente empresarial fuera {sufijo}", cifs[1], false, null, null);
        var propia = new Empresa($"Empresa propia {sufijo}", cifs[2]);
        var subcontrataFuera = Empresa.CrearComoSubcontrata($"Subcontrata fuera {sufijo}", null, NivelServicioSubcontrata.Gestionada.ToString());
        contexto.Empresas.AddRange(clienteDentro, clienteFuera, propia, subcontrataFuera);

        var centroDentro = new Centro(clienteDentro.Id, propia.Id, $"Centro dentro {sufijo}");
        var centroFuera = new Centro(clienteFuera.Id, subcontrataFuera.Id, $"Centro fuera {sufijo}");
        contexto.Centros.AddRange(centroDentro, centroFuera);

        var trabajadorDentro = Trabajador.DeEmpresa(propia.Id, "Nora", "Dentro", sufijo == "A" ? "22334455Y" : "44556677L");
        var trabajadorFuera = Trabajador.DeSubcontrata(subcontrataFuera.Id, "Iker", "Fuera", sufijo == "A" ? "33445566R" : "55667788Z");
        contexto.Trabajadores.AddRange(trabajadorDentro, trabajadorFuera);

        var vehiculoDentro = Vehiculo.DeEmpresa(propia.Id, "Furgoneta dentro", "Modelo", sufijo == "A" ? "1111BCD" : "3333BCD");
        var vehiculoFuera = Vehiculo.DeSubcontrata(subcontrataFuera.Id, "Furgoneta fuera", "Modelo", sufijo == "A" ? "2222BCD" : "4444BCD");
        contexto.Vehiculos.AddRange(vehiculoDentro, vehiculoFuera);

        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajadorDentro.Id, centroDentro.Id, new DateOnly(2026, 1, 1)));
        contexto.Asignaciones.Add(new Asignacion(trabajadorFuera.Id, centroFuera.Id, new DateOnly(2026, 1, 1)));
        await contexto.SaveChangesAsync();

        return new Escenario(clienteDentro.Id, trabajadorDentro.Id, vehiculoDentro.Id, trabajadorFuera.Id, vehiculoFuera.Id);
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

    private async Task<Guid> CrearUsuarioPortalAsync(Guid tenant, Guid clienteId)
    {
        var usuarioId = Guid.NewGuid();
        await using var contexto = CrearContexto(tenant);
        contexto.Users.Add(new ApplicationUser
        {
            Id = usuarioId,
            UserName = $"portal-{usuarioId:N}@ejemplo.test",
            Email = $"portal-{usuarioId:N}@ejemplo.test",
            ClienteId = clienteId,
            TenantId = tenant
        });
        await contexto.SaveChangesAsync();
        return usuarioId;
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
