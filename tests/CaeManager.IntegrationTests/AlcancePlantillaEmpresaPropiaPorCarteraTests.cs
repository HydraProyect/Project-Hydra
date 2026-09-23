using CaeManager.Application.Plataforma;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
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
/// Decisión del propietario 2026-09-22 (opción A): un Gestor o Coordinador CAE con Asignación de
/// Cartera sobre un Tenant ve a toda la plantilla de la Empresa propia de ese Tenant desde su alta,
/// sin Asignación a ningún Centro, para preparar el expediente antes de la primera. Consulta la ve
/// por su alcance total dentro del Tenant, DNI incluido.
///
/// Negativos: el rol Cliente (portal), los Trabajadores de una Subcontrata o de otra Empresa
/// contraparte sin Asignación, sin cartera, y otro Tenant. Cada negativo lleva su control positivo
/// (el mismo instrumento SÍ ve el Trabajador cuando la regla lo permite), para que un vacío no se
/// lea como ausencia.
///
/// Mide <see cref="AlcanceDatosService"/> contra PostgreSQL real, con el filtro global de Tenant del
/// <see cref="CaeManagerDbContext"/>; la RLS no cambia en este incremento y no se prueba aquí.
/// </summary>
public class AlcancePlantillaEmpresaPropiaPorCarteraTests : IAsyncLifetime
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

    private sealed record Escenario(Guid Cliente, Guid Propia, Guid Contraparte, Guid Subcontrata);

    /// <summary>
    /// Cliente empresarial, Empresa propia, otra Empresa contraparte (no propia) y una Subcontrata,
    /// sin ningún Centro, Relación Empresarial ni Asignación.
    /// </summary>
    private async Task<Escenario> SembrarEstructuraAsync(Guid tenant, string sufijo, string cifCliente, string cifPropia, string cifContraparte)
    {
        await using var contexto = CrearContexto(tenant);
        var cliente = Empresa.CrearComoCliente($"Cliente empresarial {sufijo}", cifCliente, false, null, null);
        var propia = new Empresa($"Empresa propia {sufijo}", cifPropia);
        var contraparte = Empresa.CrearComoCliente($"Empresa contraparte {sufijo}", cifContraparte, false, null, null);
        var subcontrata = Empresa.CrearComoSubcontrata($"Subcontrata {sufijo}", null, "Estandar");
        contexto.Empresas.AddRange(cliente, propia, contraparte, subcontrata);
        await contexto.SaveChangesAsync();
        return new Escenario(cliente.Id, propia.Id, contraparte.Id, subcontrata.Id);
    }

    private async Task<Guid> SembrarTrabajadorAsync(Guid tenant, Trabajador trabajador)
    {
        await using var contexto = CrearContexto(tenant);
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();
        return trabajador.Id;
    }

    /// <summary>Centro del Cliente empresarial y Asignación activa del Trabajador a él.</summary>
    private async Task AsignarACentroDelClienteAsync(Guid tenant, Escenario e, Guid trabajadorId)
    {
        await using var contexto = CrearContexto(tenant);
        var centro = new Centro(e.Cliente, e.Propia, $"Centro {trabajadorId:N}");
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();
        contexto.Asignaciones.Add(new Asignacion(trabajadorId, centro.Id, new DateOnly(2026, 1, 1)));
        await contexto.SaveChangesAsync();
    }

    private async Task OtorgarCarteraAsync(Guid tenant, Guid usuarioId, Guid clienteId)
    {
        await using var contexto = CrearContexto(tenant);
        var ahora = DateTime.UtcNow;
        var raiz = AsignacionOperacion.Raiz(tenant, ServicioCae.Outbound, ahora, ahora);
        contexto.AsignacionesOperacion.Add(raiz);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
            raiz, usuarioId, AmbitoAsignacion.DeRelacionCliente(clienteId), ahora, null, ahora));
        await contexto.SaveChangesAsync();
    }

    private async Task<Guid> GestorConCarteraAsync(Guid tenant, Guid clienteId)
    {
        var gestor = Guid.NewGuid();
        await OtorgarCarteraAsync(tenant, gestor, clienteId);
        return gestor;
    }

    private async Task AltaUsuarioAsync(Guid tenant, Guid usuarioId, Guid? clienteId = null, Guid? coordinadorUsuarioId = null)
    {
        await using var contexto = CrearContexto(tenant);
        contexto.Users.Add(new ApplicationUser
        {
            Id = usuarioId,
            UserName = $"u-{usuarioId:N}@ejemplo.test",
            Email = $"u-{usuarioId:N}@ejemplo.test",
            ClienteId = clienteId,
            CoordinadorUsuarioId = coordinadorUsuarioId,
            TenantId = tenant
        });
        await contexto.SaveChangesAsync();
    }

    private static AlcanceDatosService CrearServicio(CaeManagerDbContext contexto, Guid usuarioId, string rol, Guid tenant) =>
        new(contexto, new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: tenant),
            new TenantActualAmbiental { TenantId = tenant }, new SesionPrivilegiadaAusente());

    private async Task<IReadOnlyList<Guid>?> TrabajadoresVisiblesAsync(Guid usuarioId, string rol, Guid tenant)
    {
        await using var contexto = CrearContexto(tenant);
        return await CrearServicio(contexto, usuarioId, rol, tenant).ObtenerTrabajadorIdsVisiblesAsync();
    }

    private async Task<TrabajadorDetalleDto?> DetalleAsync(Guid usuarioId, string rol, Guid tenant, Guid trabajadorId)
    {
        await using var contexto = CrearContexto(tenant);
        var handler = new ObtenerTrabajadorPorIdQueryHandler(contexto, contexto, CrearServicio(contexto, usuarioId, rol, tenant));
        return await handler.Handle(new ObtenerTrabajadorPorIdQuery(trabajadorId), CancellationToken.None);
    }

    [Fact]
    public async Task Gestor_con_cartera_ve_al_Trabajador_de_la_Empresa_propia_sin_Asignacion_ni_Centro()
    {
        var e = await SembrarEstructuraAsync(_tenant, "A", "B10380186", "B10380194", "B10380202");
        var trabajador = await SembrarTrabajadorAsync(_tenant, Trabajador.DeEmpresa(e.Propia, "Nora", "Vidal", "12345678Z"));
        var gestor = await GestorConCarteraAsync(_tenant, e.Cliente);

        var visibles = await TrabajadoresVisiblesAsync(gestor, "GestorCae", _tenant);

        visibles.Should().NotBeNull().And.Contain(trabajador,
            "la plantilla de la Empresa propia se ve desde el alta para preparar el expediente antes de la primera Asignación");

        var detalle = await DetalleAsync(gestor, "GestorCae", _tenant, trabajador);
        detalle.Should().NotBeNull();
        detalle!.Dni.Should().Be("12345678Z");
    }

    [Fact]
    public async Task Coordinador_con_Gestores_con_cartera_ve_al_Trabajador_de_la_Empresa_propia_sin_Asignacion()
    {
        var e = await SembrarEstructuraAsync(_tenant, "A", "B10380186", "B10380194", "B10380202");
        var trabajador = await SembrarTrabajadorAsync(_tenant, Trabajador.DeEmpresa(e.Propia, "Nora", "Vidal", "12345678Z"));
        var coordinador = Guid.NewGuid();
        var gestor = Guid.NewGuid();
        await AltaUsuarioAsync(_tenant, coordinador);
        await AltaUsuarioAsync(_tenant, gestor, coordinadorUsuarioId: coordinador);
        await OtorgarCarteraAsync(_tenant, gestor, e.Cliente);

        var visibles = await TrabajadoresVisiblesAsync(coordinador, "CoordinadorCae", _tenant);

        visibles.Should().NotBeNull().And.Contain(trabajador);
    }

    [Fact]
    public async Task Consulta_ve_al_Trabajador_de_la_Empresa_propia_sin_Asignacion_con_su_DNI()
    {
        var e = await SembrarEstructuraAsync(_tenant, "A", "B10380186", "B10380194", "B10380202");
        var trabajador = await SembrarTrabajadorAsync(_tenant, Trabajador.DeEmpresa(e.Propia, "Nora", "Vidal", "12345678Z"));

        var detalle = await DetalleAsync(Guid.NewGuid(), "Consulta", _tenant, trabajador);

        detalle.Should().NotBeNull("Consulta tiene alcance total dentro del Tenant");
        detalle!.Dni.Should().Be("12345678Z", "Desc-9: Consulta ve el DNI");
    }

    [Fact]
    public async Task El_rol_Cliente_no_ve_la_plantilla_propia_sin_Asignacion_y_si_la_ve_con_Asignacion_a_un_Centro_suyo()
    {
        var e = await SembrarEstructuraAsync(_tenant, "A", "B10380186", "B10380194", "B10380202");
        var sinAsignacion = await SembrarTrabajadorAsync(_tenant, Trabajador.DeEmpresa(e.Propia, "Nora", "Vidal", "12345678Z"));
        var conAsignacion = await SembrarTrabajadorAsync(_tenant, Trabajador.DeEmpresa(e.Propia, "Iker", "Sanz", "87654321X"));
        await AsignarACentroDelClienteAsync(_tenant, e, conAsignacion);
        var usuarioPortal = Guid.NewGuid();
        await AltaUsuarioAsync(_tenant, usuarioPortal, clienteId: e.Cliente);

        var visibles = await TrabajadoresVisiblesAsync(usuarioPortal, "Cliente", _tenant);

        visibles.Should().Contain(conAsignacion, "control positivo: el portal ve a quien trabaja en un Centro de su Cliente");
        visibles.Should().NotContain(sinAsignacion,
            "el usuario de portal de un Cliente empresarial no gana la estructura propia del Tenant");
        (await DetalleAsync(usuarioPortal, "Cliente", _tenant, sinAsignacion)).Should().BeNull();
    }

    [Fact]
    public async Task Los_Trabajadores_de_Subcontrata_o_de_otra_Empresa_contraparte_solo_se_ven_con_Asignacion()
    {
        var e = await SembrarEstructuraAsync(_tenant, "A", "B10380186", "B10380194", "B10380202");
        var deSubcontrata = await SembrarTrabajadorAsync(_tenant, Trabajador.DeSubcontrata(e.Subcontrata, "Leo", "Mas", "11111111H"));
        var deContraparte = await SembrarTrabajadorAsync(_tenant, Trabajador.DeEmpresa(e.Contraparte, "Ana", "Gil", "22222222J"));
        var deSubcontrataAsignado = await SembrarTrabajadorAsync(_tenant, Trabajador.DeSubcontrata(e.Subcontrata, "Eva", "Rey", "33333333P"));
        var deContraparteAsignado = await SembrarTrabajadorAsync(_tenant, Trabajador.DeEmpresa(e.Contraparte, "Pau", "Roca", "44444444A"));
        await AsignarACentroDelClienteAsync(_tenant, e, deSubcontrataAsignado);
        await AsignarACentroDelClienteAsync(_tenant, e, deContraparteAsignado);
        var gestor = await GestorConCarteraAsync(_tenant, e.Cliente);

        var visibles = await TrabajadoresVisiblesAsync(gestor, "GestorCae", _tenant);

        visibles.Should().Contain([deSubcontrataAsignado, deContraparteAsignado], "control positivo: con Asignación sí se ven");
        visibles.Should().NotContain(deSubcontrata, "la plantilla de una Subcontrata sigue dependiendo de la Asignación");
        visibles.Should().NotContain(deContraparte, "solo la plantilla de la Empresa propia entra por estructura");
    }

    [Fact]
    public async Task Sin_cartera_no_se_ve_ningun_Trabajador_de_la_Empresa_propia()
    {
        var e = await SembrarEstructuraAsync(_tenant, "A", "B10380186", "B10380194", "B10380202");
        await SembrarTrabajadorAsync(_tenant, Trabajador.DeEmpresa(e.Propia, "Nora", "Vidal", "12345678Z"));
        var coordinadorSinGestores = Guid.NewGuid();
        await AltaUsuarioAsync(_tenant, coordinadorSinGestores);

        (await TrabajadoresVisiblesAsync(Guid.NewGuid(), "GestorCae", _tenant))
            .Should().NotBeNull().And.BeEmpty("sin cartera el alcance es [] (falla cerrado), no null");
        (await TrabajadoresVisiblesAsync(coordinadorSinGestores, "CoordinadorCae", _tenant))
            .Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task La_plantilla_propia_de_otro_Tenant_nunca_se_ve()
    {
        var a = await SembrarEstructuraAsync(_tenant, "A", "B10380186", "B10380194", "B10380202");
        var b = await SembrarEstructuraAsync(_otroTenant, "B", "B10380186", "B10380194", "B10380202");
        var deA = await SembrarTrabajadorAsync(_tenant, Trabajador.DeEmpresa(a.Propia, "Nora", "Vidal", "12345678Z"));
        var deB = await SembrarTrabajadorAsync(_otroTenant, Trabajador.DeEmpresa(b.Propia, "Iker", "Sanz", "87654321X"));
        // El mismo Gestor CAE con cartera en los dos Tenants: lo único que separa es el Tenant actual.
        var gestor = Guid.NewGuid();
        await OtorgarCarteraAsync(_tenant, gestor, a.Cliente);
        await OtorgarCarteraAsync(_otroTenant, gestor, b.Cliente);

        (await TrabajadoresVisiblesAsync(gestor, "GestorCae", _tenant)).Should().Contain(deA).And.NotContain(deB);
        (await TrabajadoresVisiblesAsync(gestor, "GestorCae", _otroTenant)).Should().Contain(deB).And.NotContain(deA);
        (await DetalleAsync(gestor, "GestorCae", _tenant, deB)).Should().BeNull();
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
