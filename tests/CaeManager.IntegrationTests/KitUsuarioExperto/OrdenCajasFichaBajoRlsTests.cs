using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Commands.GuardarOrdenCajasFicha;
using CaeManager.Application.Configuracion.Commands.RestablecerOrdenCajasFicha;
using CaeManager.Application.Configuracion.Queries;
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
/// El orden de cajas de la pestaña «Ficha» de las fichas 360 es de un usuario y
/// se guarda en el Tenant en el que trabaja (decisión del 2026-10-09). Dos
/// fronteras, cada una con su guardián:
///
/// <para>
/// <b>Tenant</b>: la política <c>aislamiento_tenant</c>. Se prueba autenticado
/// como <c>cae_app_runtime</c> con el interceptor de sesión RLS, y con
/// <c>IgnoreQueryFilters</c> o SQL directo para que el filtro global de EF no le
/// preste evidencia.
/// </para>
///
/// <para>
/// <b>Usuario</b>: no la guarda RLS (la política es solo por Tenant, como en
/// <c>FiltrosGuardados</c>) sino los handlers, que toman el usuario de
/// <see cref="ICurrentUserService"/>. Aquí se prueban contra el repositorio y
/// la consulta reales, con dos usuarios en el mismo Tenant.
/// </para>
/// </summary>
public class OrdenCajasFichaBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestorCae = Guid.NewGuid();
    private readonly Guid _otroGestorCae = Guid.NewGuid();
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
    public async Task La_politica_RLS_oculta_en_un_Tenant_el_orden_guardado_en_otro_aunque_se_ignore_el_filtro_de_EF()
    {
        await GuardarAsync(_tenantA, _gestorCae, "notas", "contacto");

        using (AmbitoTenantExplicito.Establecer(_tenantB))
            (await _runtime.OrdenesCajasFicha.IgnoreQueryFilters().CountAsync()).Should().Be(0,
                "app.tenant_id es el Tenant B y la política aislamiento_tenant oculta la fila del Tenant A");

        using (AmbitoTenantExplicito.Establecer(_tenantA))
            (await _runtime.OrdenesCajasFicha.IgnoreQueryFilters().CountAsync()).Should().Be(1);

        (await _propietario.OrdenesCajasFicha.IgnoreQueryFilters().SingleAsync()).TenantId.Should().Be(_tenantA,
            "el interceptor sella el Tenant en el que se guardó, no el de origen del usuario");
    }

    [Fact]
    public async Task La_politica_RLS_rechaza_escribir_un_orden_con_el_Tenant_de_otro()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantA);

        var insertar = () => _runtime.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "OrdenesCajasFicha" ("Id", "TenantId", "UsuarioId", "TipoFicha", "Claves", "ActualizadoEnUtc")
             VALUES ({Guid.NewGuid()}, {_tenantB}, {_gestorCae}, 'Empresa', ARRAY['colado'], now())
             """);

        (await insertar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(
            PostgresErrorCodes.InsufficientPrivilege, "WITH CHECK de aislamiento_tenant: la fila no es del Tenant de la sesión");
    }

    [Fact]
    public async Task El_orden_se_guarda_y_se_lee_en_el_mismo_orden()
    {
        await GuardarAsync(_tenantA, _gestorCae, "notas", "contacto", "datos-fiscales");

        (await LeerAsync(_tenantA, _gestorCae)).Should().Equal("notas", "contacto", "datos-fiscales");
    }

    [Fact]
    public async Task Guardar_otra_vez_sustituye_el_orden_en_la_misma_fila()
    {
        await GuardarAsync(_tenantA, _gestorCae, "notas", "contacto");
        await GuardarAsync(_tenantA, _gestorCae, "contacto", "notas");

        (await LeerAsync(_tenantA, _gestorCae)).Should().Equal("contacto", "notas");
        (await _propietario.OrdenesCajasFicha.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Un_usuario_no_lee_el_orden_de_otro_usuario_del_mismo_Tenant()
    {
        await GuardarAsync(_tenantA, _otroGestorCae, "contacto", "notas");

        (await LeerAsync(_tenantA, _gestorCae)).Should().BeEmpty("en el Tenant A solo ha guardado orden el otro usuario");
        (await LeerAsync(_tenantA, _otroGestorCae)).Should().Equal("contacto", "notas");
    }

    [Fact]
    public async Task Guardar_y_restablecer_no_tocan_el_orden_de_otro_usuario_del_mismo_Tenant()
    {
        await GuardarAsync(_tenantA, _otroGestorCae, "contacto", "notas");

        await GuardarAsync(_tenantA, _gestorCae, "notas", "contacto");
        (await LeerAsync(_tenantA, _otroGestorCae)).Should().Equal(["contacto", "notas"], "guardar el propio no pisa el ajeno");

        await RestablecerAsync(_tenantA, _gestorCae);
        (await LeerAsync(_tenantA, _gestorCae)).Should().BeEmpty("restablecer borra el propio");
        (await LeerAsync(_tenantA, _otroGestorCae)).Should().Equal(["contacto", "notas"], "y deja el ajeno");
    }

    [Fact]
    public async Task Un_Gestor_CAE_con_dos_Tenants_tiene_un_orden_en_cada_uno()
    {
        await GuardarAsync(_tenantA, _gestorCae, "notas", "contacto");

        (await LeerAsync(_tenantB, _gestorCae)).Should().BeEmpty("el orden se guardó en el Tenant A");

        await GuardarAsync(_tenantB, _gestorCae, "contacto", "notas");
        await RestablecerAsync(_tenantB, _gestorCae);

        (await LeerAsync(_tenantA, _gestorCae)).Should().Equal(["notas", "contacto"], "restablecer en el Tenant B no alcanza al A");
    }

    /// <summary>
    /// El filtro por tipo de ficha vive en el repositorio y en la consulta
    /// reales: con un repositorio falso no se prueba.
    /// </summary>
    [Fact]
    public async Task Cada_tipo_de_ficha_tiene_su_orden_y_restablecer_uno_no_toca_el_otro()
    {
        await GuardarAsync(_tenantA, _gestorCae, TiposDeFicha360.Empresa, ["notas", "contacto"]);
        await GuardarAsync(_tenantA, _gestorCae, TiposDeFicha360.Centro, ["accesos", "contacto"]);

        (await LeerAsync(_tenantA, _gestorCae, TiposDeFicha360.Empresa)).Should().Equal(
            ["notas", "contacto"], "guardar el de Centro no pisa el de Empresa");
        (await LeerAsync(_tenantA, _gestorCae, TiposDeFicha360.Centro)).Should().Equal("accesos", "contacto");

        await RestablecerAsync(_tenantA, _gestorCae, TiposDeFicha360.Centro);

        (await LeerAsync(_tenantA, _gestorCae, TiposDeFicha360.Centro)).Should().BeEmpty();
        (await LeerAsync(_tenantA, _gestorCae, TiposDeFicha360.Empresa)).Should().Equal(
            ["notas", "contacto"], "restablecer el de Centro no borra el de Empresa");
    }

    /// <summary>
    /// El «buscar y, si no está, crear» del handler no protege de dos guardados
    /// simultáneos; quien garantiza una sola fila es el índice único.
    /// </summary>
    [Fact]
    public async Task El_indice_unico_rechaza_una_segunda_fila_para_el_mismo_Tenant_usuario_y_tipo_de_ficha()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantA);
        _runtime.OrdenesCajasFicha.Add(new OrdenCajasFicha(_gestorCae, TiposDeFicha360.Empresa, ["notas"], DateTime.UtcNow));
        _runtime.OrdenesCajasFicha.Add(new OrdenCajasFicha(_gestorCae, TiposDeFicha360.Empresa, ["contacto"], DateTime.UtcNow));

        var guardar = () => _runtime.SaveChangesAsync();

        (await guardar.Should().ThrowAsync<DbUpdateException>()).Which.InnerException
            .Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        _runtime.ChangeTracker.Clear();
    }

    private Task GuardarAsync(Guid tenant, Guid usuario, params string[] claves) =>
        GuardarAsync(tenant, usuario, TiposDeFicha360.Empresa, claves);

    private async Task GuardarAsync(Guid tenant, Guid usuario, string tipoFicha, string[] claves)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenant);
        var handler = new GuardarOrdenCajasFichaCommandHandler(
            UsuarioActual(usuario), _tenantDeLaPeticion, new OrdenCajasFichaRepository(_runtime), _runtime, TimeProvider.System);
        var resultado = await handler.Handle(new GuardarOrdenCajasFichaCommand(tipoFicha, claves), CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue();
        _runtime.ChangeTracker.Clear();
    }

    private async Task RestablecerAsync(Guid tenant, Guid usuario, string tipoFicha = TiposDeFicha360.Empresa)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenant);
        var handler = new RestablecerOrdenCajasFichaCommandHandler(UsuarioActual(usuario), new OrdenCajasFichaRepository(_runtime), _runtime);
        var resultado = await handler.Handle(new RestablecerOrdenCajasFichaCommand(tipoFicha), CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue();
        _runtime.ChangeTracker.Clear();
    }

    private async Task<IReadOnlyList<string>> LeerAsync(Guid tenant, Guid usuario, string tipoFicha = TiposDeFicha360.Empresa)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenant);
        return await new ObtenerOrdenCajasFichaQueryHandler(_runtime, UsuarioActual(usuario))
            .Handle(new ObtenerOrdenCajasFichaQuery(tipoFicha), CancellationToken.None);
    }

    private CurrentUserServiceFalso UsuarioActual(Guid usuario) => new(usuario, tenantOrigenId: _tenantOrigen);

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
