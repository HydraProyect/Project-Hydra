using System.Data.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Dashboard;

/// <summary>
/// P2.4 (D-7 del piloto Outbound): los KPI de Inicio
/// (<see cref="ObtenerKpisDashboardQuery"/>) y de Visión de cartera
/// (<see cref="ObtenerKpisGlobalesQuery"/>, fan-out por Tenant) cuentan los
/// Centros de Trabajo bloqueados con <c>ICalculoEstadoCentroService</c> — una
/// acreditación Rechazada aplicable a su Centro lo bloquea aunque el porcentaje
/// documental siga al 100% —, y un Tenant sin Centros ni documentos sale «sin
/// datos», nunca como organización en verde.
///
/// <para>
/// Tres Tenants: el del Operador CAE externo (origen de la sesión, sin datos),
/// un Tenant beneficiario A con una Rechazada aplicable a su Centro de Trabajo
/// y otro B con una Rechazada NO aplicable (tipo no requerido). La lectura va
/// <b>autenticando como <c>cae_app_runtime</c></b>, con los interceptores de
/// sellado y de sesión RLS de producción (mismo arnés que
/// <c>MiTrabajoRechazadaAplicableBajoRlsTests</c>, #821): si el cálculo del
/// Centro corriera fuera del <c>AmbitoTenantExplicito</c> de cada vuelta del
/// fan-out, RLS le ocultaría el Centro de A y el bloqueo saldría a 0; si se
/// mezclaran Tenants, el bloqueo de A aparecería en B. La siembra va como
/// propietario de la base, porque no es lo que se mide.
/// </para>
/// </summary>
public class KpisCentrosBloqueadosBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly ContadorDeComandos _contador = new();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private ServiceProvider _servicios = null!;
    private Guid _tenantOrigen;
    private Guid _tenantA;
    private Guid _tenantB;
    private Guid _centroA;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantOrigen = new Tenant("Operador CAE externo de prueba");
        var tenantA = new Tenant("Tenant beneficiario con bloqueo");
        var tenantB = new Tenant("Tenant beneficiario sin bloqueo");
        _propietario.Tenants.AddRange(tenantOrigen, tenantA, tenantB);
        _tenantOrigen = tenantOrigen.Id;
        _tenantA = tenantA.Id;
        _tenantB = tenantB.Id;
        foreach (var delegante in new[] { _tenantA, _tenantB })
        {
            var delegacion = new DelegacionTenant(_tenantOrigen, delegante);
            _propietario.DelegacionesTenant.Add(delegacion);
            _propietario.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(delegacion.Id, _usuario, "GestorCae"));
        }
        await _propietario.SaveChangesAsync();

        // El Tenant del Operador CAE no tiene ni Centros ni documentos: solo
        // el ParametroSistema que cada vuelta del fan-out lee.
        using (AmbitoTenantExplicito.Establecer(_tenantOrigen))
        {
            _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
            await _propietario.SaveChangesAsync();
        }

        _centroA = await SembrarTenantConRechazadaAsync(_tenantA, requerido: RequisitoDocumental.Si);
        await SembrarTenantConRechazadaAsync(_tenantB, requerido: RequisitoDocumental.No);

        var tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantOrigen);
        var usuario = new CurrentUserServiceFalso(_usuario, tenantOrigenId: _tenantOrigen);
        var opcionesRuntime = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario),
                _contador)
            .Options;
        _runtime = new CaeManagerDbContext(opcionesRuntime, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);

        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<ITenantActual>(tenantDeLaPeticion);
        servicios.AddSingleton<IUnitOfWork>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Tenants.ITenantsQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.DocumentosIa.IDocumentosIaQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Comunicaciones.IComunicacionesQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Integraciones.IProveedoresPlataformaCaeQueryContext>(_runtime);
        servicios.AddSingleton<IAlcanceDatosService>(new AlcanceDatosServiceFalso());
        servicios.AddSingleton<ICurrentUserService>(usuario);
        _servicios = servicios.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        _servicios.Dispose();
        await _runtime.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>
    /// Control del instrumento: la conexión de lectura está de verdad bajo RLS.
    /// Sin filtro global de EF, fuera del ámbito de A no se ve su Centro.
    /// </summary>
    [Fact]
    public async Task La_lectura_va_bajo_RLS_efectiva()
    {
        (await _runtime.Centros.IgnoreQueryFilters().CountAsync(c => c.Id == _centroA)).Should().Be(0,
            "fuera del ámbito de A, app.tenant_id es el Tenant de origen y RLS oculta el Centro");

        using (AmbitoTenantExplicito.Establecer(_tenantA))
            (await _runtime.Centros.IgnoreQueryFilters().CountAsync(c => c.Id == _centroA)).Should().Be(1);
    }

    [Fact]
    public async Task Vision_de_cartera_cuenta_el_bloqueo_solo_en_su_Tenant_y_no_da_en_verde_ni_al_bloqueado_ni_al_vacio()
    {
        var kpis = await _servicios.GetRequiredService<IMediator>().Send(new ObtenerKpisGlobalesQuery());

        var porTenant = kpis.ClientesConMasRiesgo.ToDictionary(c => c.TenantId);
        porTenant.Keys.Should().BeEquivalentTo([_tenantOrigen, _tenantA, _tenantB]);

        var a = porTenant[_tenantA];
        a.CentrosBloqueados.Should().Be(1, "la Rechazada es aplicable a su Centro de Trabajo (D-7)");
        a.TasaCumplimientoDocumental.Should().Be(100, "control: el porcentaje documental no ve el rechazo");
        a.AdmiteVeredictoVerde.Should().BeFalse("nunca «apto» con una Rechazada aplicable");

        var b = porTenant[_tenantB];
        b.CentrosBloqueados.Should().Be(0,
            "su Rechazada no es aplicable (tipo no requerido y sin fila del Centro), y el bloqueo de A no se cuela en B");
        b.SinDatos.Should().BeFalse();
        b.AdmiteVeredictoVerde.Should().BeTrue("control positivo: con datos y sin bloqueo, sí admite verde");

        var origen = porTenant[_tenantOrigen];
        origen.SinDatos.Should().BeTrue("el Tenant del Operador CAE no tiene Centros ni documentos");
        origen.CentrosBloqueados.Should().Be(0);
        origen.AdmiteVeredictoVerde.Should().BeFalse("«sin datos» no es una organización en verde");

        kpis.CentrosBloqueados.Should().Be(1);
        kpis.HayCumplimientoDocumentalQueMedir.Should().BeTrue();
    }

    [Fact]
    public async Task Inicio_cuenta_el_Centro_bloqueado_del_Tenant_activo()
    {
        var mediador = _servicios.GetRequiredService<IMediator>();

        KpisDashboardDto enA, enOrigen;
        using (AmbitoTenantExplicito.Establecer(_tenantA))
            enA = await mediador.Send(new ObtenerKpisDashboardQuery());
        enOrigen = await mediador.Send(new ObtenerKpisDashboardQuery());

        enA.Centros.Should().Be(1);
        enA.CentrosBloqueados.Should().Be(1);
        enA.SinDatos.Should().BeFalse();
        enOrigen.CentrosBloqueados.Should().Be(0, "fuera del ámbito de A, su bloqueo no se ve");
        enOrigen.SinDatos.Should().BeTrue();
    }

    /// <summary>
    /// Coste: el recuento de bloqueos es una llamada por lotes a
    /// <c>ICalculoEstadoCentroService</c>, no una por Centro. Triplicar los
    /// Centros de Trabajo del Tenant no cambia el número de órdenes SQL.
    /// </summary>
    [Fact]
    public async Task El_numero_de_consultas_no_crece_con_el_numero_de_Centros()
    {
        var mediador = _servicios.GetRequiredService<IMediator>();

        async Task<int> ContarAsync()
        {
            using var ambito = AmbitoTenantExplicito.Establecer(_tenantA);
            var antes = _contador.Total;
            var kpis = await mediador.Send(new ObtenerKpisDashboardQuery());
            kpis.Centros.Should().BeGreaterThan(0);
            return _contador.Total - antes;
        }

        var conUno = await ContarAsync();
        await SembrarCentrosBloqueadosExtraAsync(_tenantA, cuantos: 2);
        var conTres = await ContarAsync();

        conUno.Should().BeGreaterThan(0, "control: el contador observa las órdenes de la lectura");
        conTres.Should().Be(conUno, "sin N+1: las mismas consultas para 1 que para 3 Centros de Trabajo");

        using (AmbitoTenantExplicito.Establecer(_tenantA))
            (await mediador.Send(new ObtenerKpisDashboardQuery())).CentrosBloqueados.Should().Be(3,
                "control: los Centros añadidos también están bloqueados y se cuentan");
    }

    /// <summary>
    /// En el Tenant dado: un Centro de Trabajo con un Trabajador asignado y un
    /// Documento vigente en TALVEG cuya acreditación rechaza la plataforma del
    /// Centro. Con <paramref name="requerido"/> = Si el tipo aplica al Centro
    /// (bloquea); con No, ni es requerido ni tiene fila del Centro (no aplica).
    /// </summary>
    private async Task<Guid> SembrarTenantConRechazadaAsync(Guid tenantId, RequisitoDocumental requerido)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);

        var cliente = Empresa.CrearComoCliente("Cervezas Duff Ibérica", "B12345674", false, null, null);
        var empresa = new Empresa("Montajes Springfield S.L.", "B87654323");
        _propietario.Empresas.AddRange(cliente, empresa);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        var tipo = new TipoDocumento("Formación 60h", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: requerido);
        _propietario.TiposDocumento.Add(tipo);
        var proveedor = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma de prueba");
        _propietario.ProveedoresPlataformaCae.Add(proveedor);
        await _propietario.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Fábrica de Springfield");
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Homer", "Simpson", "77189989B");
        _propietario.Centros.Add(centro);
        _propietario.Trabajadores.Add(trabajador);
        await _propietario.SaveChangesAsync();

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        _propietario.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, hoy));
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        _propietario.Documentos.Add(documento);
        var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Acceso de prueba", proveedor.Id, null, null, null);
        _propietario.CanalesGestionDocumental.Add(canal);
        await _propietario.SaveChangesAsync();

        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        acreditacion.Rechazar(CausaRechazoAcreditacion.Otro, "Documento ilegible", DateTime.UtcNow);
        _propietario.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await _propietario.SaveChangesAsync();

        return centro.Id;
    }

    /// <summary>Más Centros de Trabajo en el Tenant, cada uno con su canal y la misma acreditación rechazada del Documento requerido ya sembrado.</summary>
    private async Task SembrarCentrosBloqueadosExtraAsync(Guid tenantId, int cuantos)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);

        var centroBase = await _propietario.Centros.SingleAsync(c => c.Id == _centroA);
        var trabajadorId = await _propietario.Asignaciones.Where(a => a.CentroId == _centroA).Select(a => a.TrabajadorId).SingleAsync();
        var documentoId = await _propietario.Documentos.Where(d => d.TrabajadorId == trabajadorId).Select(d => d.Id).SingleAsync();
        var proveedorId = await _propietario.ProveedoresPlataformaCae.Select(p => p.Id).FirstAsync();
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        for (var i = 0; i < cuantos; i++)
        {
            var centro = new Centro(centroBase.ClienteId, centroBase.EmpresaId, $"Centro extra {i}");
            _propietario.Centros.Add(centro);
            await _propietario.SaveChangesAsync();

            _propietario.Asignaciones.Add(new Asignacion(trabajadorId, centro.Id, hoy));
            var canal = CanalGestionDocumental.DePlataforma(centro.Id, $"Acceso extra {i}", proveedorId, null, null, null);
            _propietario.CanalesGestionDocumental.Add(canal);
            await _propietario.SaveChangesAsync();

            var acreditacion = new AcreditacionDocumentoPlataforma(documentoId, canal.Id);
            acreditacion.Rechazar(CausaRechazoAcreditacion.Otro, "Documento ilegible", DateTime.UtcNow);
            _propietario.AcreditacionesDocumentoPlataforma.Add(acreditacion);
            await _propietario.SaveChangesAsync();
        }
    }

    /// <summary>Cuenta las órdenes SQL que ejecuta EF por la conexión de lectura.</summary>
    private sealed class ContadorDeComandos : DbCommandInterceptor
    {
        private int _total;

        public int Total => Volatile.Read(ref _total);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _total);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _total);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
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
