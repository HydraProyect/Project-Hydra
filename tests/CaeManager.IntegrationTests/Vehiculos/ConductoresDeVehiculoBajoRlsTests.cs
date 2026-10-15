using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Vehiculos.Commands.EditarFichaVehiculo;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculoPorId;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Vehiculos;

/// <summary>
/// La unión <c>VehiculosTrabajadores</c> (conductores y operadores autorizados de la Ficha de Vehículo 360),
/// escrita y leída <b>como <c>cae_app_runtime</c></b> con los interceptores de sellado y de sesión RLS de
/// producción (mismo arnés que <c>DesgloseDocumentalDeVehiculosBajoRlsTests</c>).
///
/// <para>
/// Escenario: el Tenant propietario de la sesión tiene un Vehículo («Camión grúa») y un Trabajador propio;
/// otro Tenant propietario tiene su propio Trabajador. Se prueban tres barreras por separado: el handler
/// (que solo admite Trabajadores del Tenant propietario del Vehículo), la FK compuesta
/// <c>(TenantId, TrabajadorId)</c> de la base (que rechaza la unión aunque el handler no la filtre) y la
/// política <c>aislamiento_tenant</c> (que oculta la fila a otro Tenant aunque se salte el filtro global de EF).
/// La siembra va como propietario de la base, porque no es lo que se mide.
/// </para>
/// </summary>
public class ConductoresDeVehiculoBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private CaeManagerDbContext _runtimeAjeno = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _grua;
    private Guid _trabajadorPropio;
    private Guid _trabajadorAjeno;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantSesion = new Tenant("Tenant propietario de la sesión");
        var tenantAjeno = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantSesion, tenantAjeno);
        await _propietario.SaveChangesAsync();
        _tenantSesion = tenantSesion.Id;
        _tenantAjeno = tenantAjeno.Id;

        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var propia = new Empresa("Montajes Springfield S.L.", "B10380186");
            _propietario.Empresas.Add(propia);
            await _propietario.SaveChangesAsync();
            var grua = Vehiculo.DeEmpresa(propia.Id, "Camión grúa", "Iveco Daily", "1234BCD");
            var raul = Trabajador.DeEmpresa(propia.Id, "Raúl", "Ortega Gil", "77189989B");
            _propietario.Vehiculos.Add(grua);
            _propietario.Trabajadores.Add(raul);
            await _propietario.SaveChangesAsync();
            (_grua, _trabajadorPropio) = (grua.Id, raul.Id);
        }

        using (AmbitoTenantExplicito.Establecer(_tenantAjeno))
        {
            var propiaAjena = new Empresa("Grúas Shelbyville S.L.", "B87654323");
            _propietario.Empresas.Add(propiaAjena);
            await _propietario.SaveChangesAsync();
            var iker = Trabajador.DeEmpresa(propiaAjena.Id, "Iker", "Sanz Mora", "12345678Z");
            _propietario.Trabajadores.Add(iker);
            await _propietario.SaveChangesAsync();
            _trabajadorAjeno = iker.Id;
        }

        _runtime = CrearRuntime(_tenantSesion);
        _runtimeAjeno = CrearRuntime(_tenantAjeno);
    }

    public async Task DisposeAsync()
    {
        await _runtimeAjeno.DisposeAsync();
        await _runtime.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento: el runtime está de verdad bajo RLS y el Trabajador ajeno existe.</summary>
    [Fact]
    public async Task El_runtime_va_bajo_RLS_efectiva_y_el_Trabajador_ajeno_existe()
    {
        (await _runtime.Trabajadores.IgnoreQueryFilters().CountAsync(t => t.Id == _trabajadorAjeno)).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar el Trabajador del Tenant ajeno");
        (await _runtime.Trabajadores.IgnoreQueryFilters().CountAsync(t => t.Id == _trabajadorPropio)).Should().Be(1,
            "control positivo: el Trabajador propio sí se ve");
        (await _propietario.Trabajadores.IgnoreQueryFilters().CountAsync(t => t.Id == _trabajadorAjeno)).Should().Be(1);
    }

    [Fact]
    public async Task El_handler_rechaza_como_conductor_a_un_Trabajador_de_otro_Tenant_sin_tocar_la_ficha()
    {
        var resultado = await Handler().Handle(Comando(await Version(), _trabajadorAjeno), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Vehiculo.TrabajadorNoEncontrado");
        (await _propietario.Set<VehiculoTrabajador>().IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _propietario.Vehiculos.IgnoreQueryFilters().AsNoTracking().SingleAsync(v => v.Id == _grua))
            .Tipo.Should().BeNull("el rechazo llega antes de modificar la ficha");
    }

    [Fact]
    public async Task La_base_rechaza_la_union_con_un_Trabajador_de_otro_Tenant_aunque_el_handler_no_filtre()
    {
        // Sin pasar por el handler: el sellado pone el Tenant de la sesión y la FK compuesta
        // (TenantId, TrabajadorId) no encuentra a ese Trabajador en el Tenant de la sesión.
        _runtime.Set<VehiculoTrabajador>().Add(new VehiculoTrabajador(_grua, _trabajadorAjeno));

        var guardar = () => _runtime.SaveChangesAsync();

        var fallo = await guardar.Should().ThrowAsync<DbUpdateException>();
        fallo.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        (await _propietario.Set<VehiculoTrabajador>().IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task El_conductor_propio_se_guarda_en_el_Tenant_de_la_sesion_y_no_se_ve_desde_otro_Tenant()
    {
        var resultado = await Handler().Handle(Comando(await Version(), _trabajadorPropio), CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue(resultado.Error.Mensaje);

        var fila = await _propietario.Set<VehiculoTrabajador>().IgnoreQueryFilters().AsNoTracking().SingleAsync();
        (fila.TenantId, fila.VehiculoId, fila.TrabajadorId).Should().Be((_tenantSesion, _grua, _trabajadorPropio));
        (await _runtime.Set<VehiculoTrabajador>().IgnoreQueryFilters().CountAsync()).Should().Be(1,
            "control positivo: el Tenant de la sesión ve su unión");
        (await _runtimeAjeno.Set<VehiculoTrabajador>().IgnoreQueryFilters().CountAsync()).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar la unión al otro Tenant");

        // La lectura de la Ficha traduce a SQL la proyección de conductores (navegación a Trabajador).
        var detalle = await new ObtenerVehiculoPorIdQueryHandler(
            _runtime, _runtime, new AlcanceDatosServiceFalso(),
            new CalculoEstadoDocumentalService(_runtime, _runtime, _runtime), PoliticaNotaInternaPruebas.Con("GestorCae"))
            .Handle(new ObtenerVehiculoPorIdQuery(_grua), CancellationToken.None);
        detalle!.Tipo.Should().Be("Camión grúa");
        detalle.Conductores.Should().Equal(new ConductorVehiculoDto(_trabajadorPropio, "Raúl Ortega Gil"));
    }

    private EditarFichaVehiculoCommandHandler Handler() =>
        new(new VehiculoRepository(_runtime), _runtime, new AlcanceDatosServiceFalso(), _runtime);

    private EditarFichaVehiculoCommand Comando(Guid version, params Guid[] trabajadorIds) =>
        new(_grua, version, "Camión grúa", "Propio", null, null, null, null, null, trabajadorIds);

    private async Task<Guid> Version() =>
        (await _propietario.Vehiculos.IgnoreQueryFilters().AsNoTracking().SingleAsync(v => v.Id == _grua)).Version;

    private CaeManagerDbContext CrearRuntime(Guid tenant)
    {
        var tenantDeLaPeticion = new TenantActualDeLaPeticion(tenant);
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: tenant);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

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
