using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Centros;

/// <summary>
/// P1-D3 bajo RLS real: dos Documentos del mismo tipo para el mismo
/// Trabajador (el vencido y su renovación) no tumban el cumplimiento del
/// Centro de Trabajo y manda el vigente, leyendo <b>como
/// <c>cae_app_runtime</c></b> con los interceptores de sellado y de sesión
/// RLS de producción (mismo arnés que <c>KpisCentrosBloqueadosBajoRlsTests</c>).
/// Otro Tenant propietario tiene su propio par duplicado, los dos vencidos,
/// como control de que la conexión de lectura está de verdad bajo RLS. La
/// siembra va como propietario de la base, porque no es lo que se mide.
/// </summary>
public class CumplimientoCentroDocumentosDuplicadosBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _centroSesion;
    private Guid _documentoAjeno;

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

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        (_centroSesion, _) = await SembrarAsync(_tenantSesion, VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        (_, _documentoAjeno) = await SembrarAsync(_tenantAjeno, VigenciaDocumento.VenceEl(hoy.AddDays(-3)));

        var tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenantSesion);
        var opcionesRuntime = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        _runtime = new CaeManagerDbContext(opcionesRuntime, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento: la conexión de lectura está de verdad bajo RLS.</summary>
    [Fact]
    public async Task La_lectura_va_bajo_RLS_efectiva()
    {
        (await _runtime.Documentos.IgnoreQueryFilters().CountAsync(d => d.Id == _documentoAjeno)).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar el Documento del Tenant ajeno");
        (await _runtime.Documentos.IgnoreQueryFilters().CountAsync()).Should().Be(2,
            "control positivo: las dos copias del Tenant de la sesión sí se ven");
    }

    [Fact]
    public async Task Con_dos_copias_del_mismo_tipo_el_cumplimiento_no_lanza_y_manda_la_vigente()
    {
        var servicio = new CalculoEstadoCentroService(_runtime, _runtime, _runtime, _runtime, _runtime, _runtime);

        var cumplimiento = await servicio.CalcularCumplimientoAsync([_centroSesion], CancellationToken.None);

        cumplimiento[_centroSesion].Requeridos.Should().Be(1);
        cumplimiento[_centroSesion].AlDia.Should().Be(1, "manda la copia vigente, no la vencida");
    }

    /// <summary>
    /// Un Centro de Trabajo con un Trabajador asignado y dos Documentos del
    /// mismo tipo requerido: uno vencido y otro con la vigencia indicada.
    /// </summary>
    private async Task<(Guid CentroId, Guid DocumentoId)> SembrarAsync(Guid tenantId, VigenciaDocumento vigenciaDeLaRenovacion)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var cliente = Empresa.CrearComoCliente("Cervezas Duff Ibérica", "B12345674", false, null, null);
        var empresa = new Empresa("Montajes Springfield S.L.", "B87654323");
        _propietario.Empresas.AddRange(cliente, empresa);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        var tipo = new TipoDocumento("Formación 60h", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        _propietario.TiposDocumento.Add(tipo);
        await _propietario.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Fábrica de Springfield");
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Homer", "Simpson", "77189989B");
        _propietario.Centros.Add(centro);
        _propietario.Trabajadores.Add(trabajador);
        await _propietario.SaveChangesAsync();

        _propietario.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, hoy));
        var vencido = Documento.DeTrabajador(trabajador.Id, tipo.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-10)));
        var renovacion = Documento.DeTrabajador(trabajador.Id, tipo.Id, hoy.AddDays(-5), vigenciaDeLaRenovacion);
        _propietario.Documentos.AddRange(vencido, renovacion);
        await _propietario.SaveChangesAsync();

        return (centro.Id, renovacion.Id);
    }

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
