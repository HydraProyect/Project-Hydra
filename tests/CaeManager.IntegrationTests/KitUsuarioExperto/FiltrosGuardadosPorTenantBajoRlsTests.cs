using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.KitUsuarioExperto;

/// <summary>
/// Los filtros guardados pertenecen al Tenant en el que se guardan (decisión D4
/// del 2026-10-08): un Gestor CAE con Asignación de Cartera sobre dos Tenants no
/// ve en uno los filtros que guardó en el otro, ni puede borrarlos desde allí.
///
/// <para>
/// La lectura y la escritura van autenticadas como <c>cae_app_runtime</c>, con
/// el interceptor de sesión RLS: bajo el rol propietario PostgreSQL no aplica la
/// política ni con <c>FORCE</c>, y solo se estaría probando el filtro global de
/// EF. Los dos primeros tests separan a propósito las dos barreras — la política
/// (con <c>IgnoreQueryFilters</c> y SQL directo) y el filtro de EF no se prestan
/// evidencia.
/// </para>
/// </summary>
public class FiltrosGuardadosPorTenantBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestorCae = Guid.NewGuid();
    private ITenantActual _tenantDeLaPeticion = null!;
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private Guid _tenantOrigen;
    private Guid _tenantA;
    private Guid _tenantB;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var origen = new Tenant("Operador CAE externo de prueba");
        var a = new Tenant("Tenant propietario A");
        var b = new Tenant("Tenant propietario B");
        _propietario.Tenants.AddRange(origen, a, b);
        await _propietario.SaveChangesAsync();
        _tenantOrigen = origen.Id;
        _tenantA = a.Id;
        _tenantB = b.Id;

        var tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantOrigen);
        _tenantDeLaPeticion = tenantDeLaPeticion;
        var opcionesRuntime = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(
                    tenantDeLaPeticion, new SinClienteActivo(),
                    new CurrentUserServiceFalso(_gestorCae, tenantOrigenId: _tenantOrigen),
                    BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        _runtime = new CaeManagerDbContext(opcionesRuntime, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>
    /// Control del instrumento y de la política a la vez: con el filtro global
    /// de EF desactivado, lo único que puede ocultar la fila es RLS.
    /// </summary>
    [Fact]
    public async Task La_politica_RLS_oculta_en_un_Tenant_el_filtro_guardado_en_otro_aunque_se_ignore_el_filtro_de_EF()
    {
        var id = await GuardarAsync(_tenantA, "Vencidos");

        using (AmbitoTenantExplicito.Establecer(_tenantB))
            (await _runtime.FiltrosGuardados.IgnoreQueryFilters().CountAsync(f => f.Id == id)).Should().Be(0,
                "app.tenant_id es el Tenant B y la política aislamiento_tenant oculta la fila del Tenant A");

        using (AmbitoTenantExplicito.Establecer(_tenantA))
            (await _runtime.FiltrosGuardados.IgnoreQueryFilters().CountAsync(f => f.Id == id)).Should().Be(1);

        (await _propietario.FiltrosGuardados.IgnoreQueryFilters().SingleAsync(f => f.Id == id)).TenantId.Should().Be(_tenantA,
            "el interceptor sella el Tenant en el que se guardó, no el de origen del usuario");
    }

    [Fact]
    public async Task La_politica_RLS_rechaza_escribir_un_filtro_con_el_Tenant_de_otro()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantA);

        var insertar = () => _runtime.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "FiltrosGuardados" ("Id", "TenantId", "UsuarioId", "Pantalla", "Nombre", "ValoresJson", "CreadoEnUtc")
             VALUES ({Guid.NewGuid()}, {_tenantB}, {_gestorCae}, 'Clientes', 'Colado', {"{}"}, now())
             """);

        (await insertar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(
            PostgresErrorCodes.InsufficientPrivilege, "WITH CHECK de aislamiento_tenant: la fila no es del Tenant de la sesión");
    }

    [Fact]
    public async Task Un_Gestor_CAE_con_dos_Tenants_no_ve_en_uno_los_filtros_del_otro()
    {
        await GuardarAsync(_tenantA, "Vencidos");

        (await ListarAsync(_tenantB)).Should().BeEmpty("el filtro se guardó en el Tenant A");
        (await ListarAsync(_tenantA)).Select(f => f.Nombre).Should().Equal("Vencidos");
    }

    [Fact]
    public async Task El_mismo_nombre_se_puede_repetir_en_otro_Tenant_pero_no_en_el_mismo()
    {
        await GuardarAsync(_tenantA, "Vencidos");

        using (AmbitoTenantExplicito.Establecer(_tenantB))
        {
            var enOtroTenant = await CrearHandlerGuardar().Handle(
                new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, "Vencidos", "{}"), CancellationToken.None);
            enOtroTenant.EsExitoso.Should().BeTrue("la clave incluye el Tenant");
        }

        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            var repetido = await CrearHandlerGuardar().Handle(
                new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, "  Vencidos ", "{}"), CancellationToken.None);
            repetido.EsFallido.Should().BeTrue();
            repetido.Error.Codigo.Should().Be("FiltroGuardado.NombreDuplicado");
        }
    }

    /// <summary>
    /// La comprobación del handler es cortesía; quien garantiza la clave es el
    /// índice único, también frente a dos guardados simultáneos.
    /// </summary>
    [Fact]
    public async Task El_indice_unico_rechaza_el_nombre_repetido_aunque_se_salte_la_comprobacion_del_handler()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantA);
        _runtime.FiltrosGuardados.Add(new FiltroGuardado(_gestorCae, PantallasConFiltrosGuardados.Clientes, "Vencidos", "{}"));
        _runtime.FiltrosGuardados.Add(new FiltroGuardado(_gestorCae, PantallasConFiltrosGuardados.Clientes, "Vencidos", "{}"));

        var guardar = () => _runtime.SaveChangesAsync();

        (await guardar.Should().ThrowAsync<DbUpdateException>()).Which.InnerException
            .Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        _runtime.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Desde_otro_Tenant_no_se_puede_borrar_el_filtro_y_desde_el_suyo_si()
    {
        var id = await GuardarAsync(_tenantA, "Vencidos");

        using (AmbitoTenantExplicito.Establecer(_tenantB))
        {
            var desdeOtro = await CrearHandlerEliminar().Handle(new EliminarFiltroGuardadoCommand(id), CancellationToken.None);
            desdeOtro.EsFallido.Should().BeTrue();
            desdeOtro.Error.Codigo.Should().Be("FiltroGuardado.NoEncontrado");
        }

        (await _propietario.FiltrosGuardados.IgnoreQueryFilters().CountAsync(f => f.Id == id)).Should().Be(1, "sigue existiendo");

        using (AmbitoTenantExplicito.Establecer(_tenantA))
            (await CrearHandlerEliminar().Handle(new EliminarFiltroGuardadoCommand(id), CancellationToken.None)).EsExitoso.Should().BeTrue();

        (await _propietario.FiltrosGuardados.IgnoreQueryFilters().CountAsync(f => f.Id == id)).Should().Be(0);
    }

    private async Task<Guid> GuardarAsync(Guid tenant, string nombre)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenant);
        var resultado = await CrearHandlerGuardar().Handle(
            new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, nombre, "{}"), CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue();
        _runtime.ChangeTracker.Clear();
        return resultado.Valor;
    }

    private async Task<IReadOnlyList<FiltroGuardadoDto>> ListarAsync(Guid tenant)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenant);
        return await new ObtenerFiltrosGuardadosQueryHandler(_runtime, UsuarioActual())
            .Handle(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Clientes), CancellationToken.None);
    }

    private GuardarFiltroCommandHandler CrearHandlerGuardar() =>
        new(UsuarioActual(), _tenantDeLaPeticion, new FiltroGuardadoRepository(_runtime), _runtime);

    private EliminarFiltroGuardadoCommandHandler CrearHandlerEliminar() =>
        new(UsuarioActual(), new FiltroGuardadoRepository(_runtime), _runtime);

    private CurrentUserServiceFalso UsuarioActual() => new(_gestorCae, tenantOrigenId: _tenantOrigen);

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>Como el <c>TenantActual</c> real: primero el ámbito explícito, luego el Tenant de la sesión.</summary>
    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
