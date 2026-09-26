using System.Data.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.DependencyInjection;
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
/// P1-D1: los KPI de Inicio (<see cref="ObtenerKpisDashboardQuery"/>) y el
/// estado de Centro que los alimenta (<see cref="ICalculoEstadoCentroService"/>)
/// ya no traen de PostgreSQL una fila por Documento de Trabajador. Los KPI
/// agregan la vigencia por par (EstadoVigencia, FechaVencimiento); el estado de
/// Centro deja en la base los Documentos con vencimiento más allá del umbral
/// ámbar, que siempre son Vigente y nunca causa.
///
/// <para>
/// Qué se mide, con un interceptor en la conexión de lectura: órdenes SQL
/// ejecutadas y filas leídas por las órdenes que leen la vigencia de
/// Documentos. Añadir Trabajadores asignados con un Documento Vigente del mismo
/// vencimiento no cambia ni lo uno ni lo otro, y el resultado visible sigue
/// siendo el que da clasificar Documento a Documento con
/// <see cref="CalculadoraEstadoDocumento"/> (referencia calculada aquí, fuera
/// del handler). La lectura va autenticando como <c>cae_app_runtime</c> con los
/// interceptores de sellado y de sesión RLS de producción (mismo arnés que
/// <c>KpisCentrosBloqueadosBajoRlsTests</c>); la siembra va como propietario de
/// la base, porque no es lo que se mide.
/// </para>
/// </summary>
public class ConsultasKpiAcotadasBajoRlsTests : IAsyncLifetime
{
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly MedidorDeLecturas _medidor = new();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private ServiceProvider _servicios = null!;
    private Guid _tenant;
    private Guid _centro;
    private Guid _empresa;
    private Guid _tipoVigente;
    private int _trabajadoresSembrados;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenant = new Tenant("Tenant beneficiario con histórico documental");
        _propietario.Tenants.Add(tenant);
        await _propietario.SaveChangesAsync();
        _tenant = tenant.Id;

        await SembrarTenantAsync();

        var tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenant);
        var usuario = new CurrentUserServiceFalso(_usuario, tenantOrigenId: _tenant);
        var opcionesRuntime = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                _medidor)
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

    [Fact]
    public async Task Los_KPI_de_Inicio_dan_lo_mismo_que_clasificar_Documento_a_Documento()
    {
        await SembrarTrabajadoresConDocumentoVigenteAsync(cuantos: 4);
        var referencia = await ClasificarDocumentoADocumentoAsync();

        var kpis = await _servicios.GetRequiredService<IMediator>().Send(new ObtenerKpisDashboardQuery());

        kpis.DocumentosVencidos.Should().Be(referencia.GetValueOrDefault(EstadoDocumento.Vencido)).And.Be(1);
        kpis.DocumentosUrgentes.Should().Be(referencia.GetValueOrDefault(EstadoDocumento.Urgente)).And.Be(1);
        kpis.DocumentosProximos.Should().Be(referencia.GetValueOrDefault(EstadoDocumento.Proximo)).And.Be(1);
        kpis.DocumentosVigentes.Should().Be(referencia.GetValueOrDefault(EstadoDocumento.Vigente)).And.Be(5);

        // Denominador: todo menos «no caduca»; el SinConfirmar resta.
        var totalConVigencia = referencia.Where(p => p.Key != EstadoDocumento.SinCaducidad).Sum(p => p.Value);
        totalConVigencia.Should().Be(9, "control de la siembra: 1+1+1+5 con fecha, 1 sin confirmar, el «no caduca» fuera");
        kpis.TasaCumplimientoDocumental.Should().Be(referencia.GetValueOrDefault(EstadoDocumento.Vigente) * 100 / totalConVigencia);
        kpis.SinDatos.Should().BeFalse();
    }

    [Fact]
    public async Task El_estado_del_Centro_tiene_las_mismas_causas_que_clasificar_Documento_a_Documento()
    {
        await SembrarTrabajadoresConDocumentoVigenteAsync(cuantos: 4);

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        // Mismo Tenant que la petición: el ámbito solo sirve a la siembra y a esta referencia.
        using var ambito = AmbitoTenantExplicito.Establecer(_tenant);
        var esperadas = await (
            from documento in _propietario.Documentos
            where documento.TrabajadorId != null && documento.FechaVencimiento != null
            join trabajador in _propietario.Trabajadores on documento.TrabajadorId equals trabajador.Id
            join tipo in _propietario.TiposDocumento on documento.TipoDocumentoId equals tipo.Id
            select new { TipoNombre = tipo.Nombre, TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos, documento.FechaVencimiento })
            .ToListAsync();
        var causasDeReferencia = esperadas
            .Select(d => (Descripcion: $"{d.TipoNombre} — {d.TrabajadorNombre}",
                Estado: CalculadoraEstadoDocumento.Calcular(VigenciaDocumento.VenceEl(d.FechaVencimiento!.Value), hoy, UmbralAmbarDias, UmbralRojoDias)))
            .Where(c => c.Estado != EstadoDocumento.Vigente)
            .ToList();
        causasDeReferencia.Should().HaveCount(3, "control de la siembra: vencido, urgente y próximo");

        using var alcance = _servicios.CreateScope();
        var resultado = await alcance.ServiceProvider.GetRequiredService<ICalculoEstadoCentroService>()
            .CalcularAsync([_centro], CancellationToken.None);

        resultado[_centro].Causas
            .Where(c => c.Ambito == AmbitoCausa.Trabajador && c.Estado != EstadoDocumento.Faltante)
            .Select(c => (c.Descripcion, Estado: c.Estado!.Value))
            .Should().BeEquivalentTo(causasDeReferencia);
    }

    /// <summary>
    /// Coste: más Documentos Vigentes del mismo vencimiento (cada uno de un
    /// Trabajador asignado nuevo) no cambian ni el número de órdenes SQL de los
    /// KPI de Inicio ni las filas que leen las órdenes de vigencia de
    /// Documentos. Control positivo: los KPI sí cuentan los Vigentes nuevos.
    /// </summary>
    [Fact]
    public async Task Los_KPI_de_Inicio_no_leen_mas_filas_de_vigencia_ni_mas_ordenes_al_crecer_los_Documentos()
    {
        var mediador = _servicios.GetRequiredService<IMediator>();

        async Task<(int Ordenes, int FilasVigencia, int Vigentes)> MedirAsync()
        {
            _medidor.Reiniciar();
            var kpis = await mediador.Send(new ObtenerKpisDashboardQuery());
            return (_medidor.Ordenes, _medidor.FilasLeidasDeVigenciaDeDocumentos, kpis.DocumentosVigentes);
        }

        var antes = await MedirAsync();
        await SembrarTrabajadoresConDocumentoVigenteAsync(cuantos: 12);
        var despues = await MedirAsync();

        antes.Ordenes.Should().BeGreaterThan(0, "control: el medidor observa las órdenes de la lectura");
        antes.FilasVigencia.Should().BeGreaterThan(0, "control: el medidor reconoce las órdenes que leen la vigencia de Documentos");
        despues.Vigentes.Should().Be(antes.Vigentes + 12, "control: los Documentos nuevos existen, se ven bajo RLS y se cuentan");
        despues.Ordenes.Should().Be(antes.Ordenes, "sin N+1: las mismas órdenes con 1 que con 13 Trabajadores asignados");
        despues.FilasVigencia.Should().Be(antes.FilasVigencia,
            "la vigencia se agrega en PostgreSQL y los Vigentes más allá del umbral ámbar no salen de la base");
    }

    /// <summary>
    /// Un Centro de Trabajo de un Cliente, un Trabajador asignado con un
    /// Documento en cada estado (vencido, urgente, próximo, vigente, sin
    /// confirmar y «no caduca»), cada uno de un tipo distinto.
    /// </summary>
    private async Task SembrarTenantAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenant);

        var cliente = Empresa.CrearComoCliente("Cervezas Duff Ibérica", "B12345674", false, null, null);
        var empresa = new Empresa("Montajes Springfield S.L.", "B87654323");
        _propietario.Empresas.AddRange(cliente, empresa);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: UmbralAmbarDias, umbralRojoDias: UmbralRojoDias));
        var tipos = Enumerable.Range(1, 6)
            .Select(i => new TipoDocumento($"Tipo {i}", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.No))
            .ToList();
        _propietario.TiposDocumento.AddRange(tipos);
        await _propietario.SaveChangesAsync();
        _empresa = empresa.Id;
        _tipoVigente = tipos[3].Id;

        var centro = new Centro(cliente.Id, empresa.Id, "Fábrica de Springfield");
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Homer", "Simpson", "77189989B");
        _propietario.Centros.Add(centro);
        _propietario.Trabajadores.Add(trabajador);
        await _propietario.SaveChangesAsync();
        _centro = centro.Id;

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        _propietario.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, hoy));
        var vigencias = new[]
        {
            VigenciaDocumento.VenceEl(hoy.AddDays(-3)),
            VigenciaDocumento.VenceEl(hoy.AddDays(5)),
            VigenciaDocumento.VenceEl(hoy.AddDays(20)),
            VigenciaDocumento.VenceEl(hoy.AddYears(1)),
            VigenciaDocumento.SinConfirmar,
            VigenciaDocumento.NoCaduca,
        };
        for (var i = 0; i < vigencias.Length; i++)
            _propietario.Documentos.Add(Documento.DeTrabajador(trabajador.Id, tipos[i].Id, hoy.AddYears(-2), vigencias[i]));
        await _propietario.SaveChangesAsync();
    }

    /// <summary>Trabajadores nuevos asignados al Centro, cada uno con un Documento Vigente que vence el mismo día que el Vigente inicial.</summary>
    private async Task SembrarTrabajadoresConDocumentoVigenteAsync(int cuantos)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenant);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        for (var i = 0; i < cuantos; i++)
        {
            var trabajador = Trabajador.DeEmpresa(_empresa, $"Trabajador {i}", "De prueba", DniValido(10000000 + _trabajadoresSembrados++));
            _propietario.Trabajadores.Add(trabajador);
            await _propietario.SaveChangesAsync();

            _propietario.Asignaciones.Add(new Asignacion(trabajador.Id, _centro, hoy));
            _propietario.Documentos.Add(Documento.DeTrabajador(trabajador.Id, _tipoVigente, hoy.AddYears(-2), VigenciaDocumento.VenceEl(hoy.AddYears(1))));
            await _propietario.SaveChangesAsync();
        }
    }

    private static string DniValido(int numero) => $"{numero:D8}{"TRWAGMYFPDXBNJZSQVHLCKE"[numero % 23]}";

    /// <summary>Referencia independiente del handler: cada Documento de Trabajador del Tenant clasificado uno a uno.</summary>
    private async Task<Dictionary<EstadoDocumento, int>> ClasificarDocumentoADocumentoAsync()
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        using var ambito = AmbitoTenantExplicito.Establecer(_tenant);
        var documentos = await _propietario.Documentos
            .Where(d => d.TrabajadorId != null)
            .Select(d => new { d.EstadoVigencia, d.FechaVencimiento })
            .ToListAsync();
        return documentos
            .GroupBy(d => CalculadoraEstadoDocumento.Calcular(d.EstadoVigencia, d.FechaVencimiento, hoy, UmbralAmbarDias, UmbralRojoDias))
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Cuenta las órdenes SQL de la conexión de lectura y, de las que leen la
    /// vigencia de Documentos (texto con la tabla <c>Documentos</c> y la columna
    /// <c>FechaVencimiento</c>), las filas que EF llegó a leer.
    /// </summary>
    private sealed class MedidorDeLecturas : DbCommandInterceptor
    {
        private int _ordenes;
        private int _filasVigencia;

        public int Ordenes => Volatile.Read(ref _ordenes);
        public int FilasLeidasDeVigenciaDeDocumentos => Volatile.Read(ref _filasVigencia);

        public void Reiniciar()
        {
            Interlocked.Exchange(ref _ordenes, 0);
            Interlocked.Exchange(ref _filasVigencia, 0);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _ordenes);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _ordenes);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            if (command.CommandText.Contains("FROM \"Documentos\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"FechaVencimiento\"", StringComparison.Ordinal))
                Interlocked.Add(ref _filasVigencia, eventData.ReadCount);
            return base.DataReaderDisposing(command, eventData, result);
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
