using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants.Queries.ObtenerLogoTenant;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Web.Features.Tenants;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// <c>GET /tenants/{id}/logo</c> contra PostgreSQL con el rol de runtime y el almacenamiento real
/// (contrato del selector de Tenant, § 4.1.5; invariantes I11, I12, I13 e I17). Escenario: un Gestor
/// CAE de un Operador CAE externo con Asignación de Cartera vigente sobre el Tenant beneficiario A (con
/// logo) y sobre otro sin logo; otro Gestor CAE del mismo Operador sin cartera; y un Tenant B ajeno con
/// logo. El blob de cada logo se escribe cifrado en la carpeta de su Tenant propietario, así que solo
/// se lee dentro de su ámbito: el endpoint lo abre con <c>AmbitoTenantExplicito</c> tras autorizar.
/// </summary>
public class LogoTenantEndpointBajoRlsTests : IAsyncLifetime, IDisposable
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly string _rutaArchivos = Path.Combine(Path.GetTempPath(), $"caemanager-logo-{Guid.NewGuid():N}");
    private readonly IDataProtectionProvider _protector = new EphemeralDataProtectionProvider();
    private readonly Guid _gestor = Guid.NewGuid();
    private readonly Guid _gestorSinCartera = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;

    private Guid _origen;
    private Guid _a;
    private Guid _sinLogo;
    private Guid _b;
    private byte[] _pngDeA = null!;
    private string _versionDeA = null!;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var origen = new Tenant("Operador CAE externo de prueba");
        var a = new Tenant("Tenant beneficiario A");
        var sinLogo = new Tenant("Tenant beneficiario sin logo");
        var b = new Tenant("Tenant ajeno B");
        _propietario.Tenants.AddRange(origen, a, sinLogo, b);
        await _propietario.SaveChangesAsync();
        (_origen, _a, _sinLogo, _b) = (origen.Id, a.Id, sinLogo.Id, b.Id);

        var ayer = DateTime.UtcNow.AddDays(-1);
        foreach (var beneficiario in new[] { _a, _sinLogo })
        {
            using var ambito = AmbitoTenantExplicito.Establecer(beneficiario);
            var operacion = AsignacionOperacion.Externa(
                beneficiario, _origen, ServicioCae.Outbound, AmbitoAsignacion.Universal, ayer, null, DateTime.UtcNow);
            _propietario.AsignacionesOperacion.Add(operacion);
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, _gestor, Roles.GestorCae, AmbitoAsignacion.Universal, ayer, null, DateTime.UtcNow));
            await _propietario.SaveChangesAsync();
        }

        _pngDeA = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        _versionDeA = "aaaaaaaaaaaaaaaa";
        await FijarLogoAsync(a, _pngDeA, _versionDeA);
        await FijarLogoAsync(b, [0x89, 0x50, 0x4E, 0x47, 9, 9, 9], "bbbbbbbbbbbbbbbb");
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rutaArchivos)) Directory.Delete(_rutaArchivos, recursive: true);
    }

    [Fact]
    public async Task El_Gestor_CAE_con_cartera_recibe_el_logo_sin_cache()
    {
        var respuesta = await ServirAsync(_gestor, _a);

        respuesta.Estado.Should().Be(StatusCodes.Status200OK);
        respuesta.Cuerpo.Should().Equal(_pngDeA);
        respuesta.Cabeceras["Content-Type"].Should().Be("image/png");
        respuesta.Cabeceras["Cache-Control"].Should().Contain("no-store");
        respuesta.Cabeceras["X-Content-Type-Options"].Should().Be("nosniff");
        respuesta.Cabeceras["Content-Disposition"].Should().Be("inline");
    }

    [Fact]
    public async Task Los_cuatro_casos_negativos_dan_el_mismo_404()
    {
        var sinCartera = await ServirAsync(_gestorSinCartera, _a);
        var inexistente = await ServirAsync(_gestor, Guid.NewGuid());
        var ajenoConLogo = await ServirAsync(_gestor, _b);
        var autorizadoSinLogo = await ServirAsync(_gestor, _sinLogo);

        foreach (var respuesta in new[] { sinCartera, inexistente, ajenoConLogo, autorizadoSinLogo })
        {
            respuesta.Estado.Should().Be(StatusCodes.Status404NotFound);
            respuesta.Cuerpo.Should().BeEmpty();
            respuesta.Cabeceras.Should().Equal(inexistente.Cabeceras,
                "un Tenant sin cartera, inexistente, ajeno o sin logo no se distinguen por las cabeceras");
        }
        sinCartera.Cabeceras["Cache-Control"].Should().Contain("no-store");
    }

    [Fact]
    public async Task La_version_de_la_URL_no_autoriza_ni_filtra()
    {
        var conVersionCorrectaSinCartera = await ServirAsync(_gestorSinCartera, _a, version: _versionDeA);
        var conVersionAntiguaConCartera = await ServirAsync(_gestor, _a, version: "0000000000000000");

        conVersionCorrectaSinCartera.Estado.Should().Be(StatusCodes.Status404NotFound);
        conVersionAntiguaConCartera.Estado.Should().Be(StatusCodes.Status200OK);
        conVersionAntiguaConCartera.Cuerpo.Should().Equal(_pngDeA, "se sirve siempre el logo vigente");
    }

    [Fact]
    public async Task Un_blob_huerfano_nunca_se_sirve()
    {
        // Subida interrumpida entre el blob y SaveChanges: el blob existe en la carpeta de A, pero la
        // columna sigue apuntando al anterior. Se sirve el de la columna.
        using (AmbitoTenantExplicito.Establecer(_a))
        using (var flujo = new MemoryStream([0x89, 0x50, 0x4E, 0x47, 6, 6, 6]))
            await Almacenamiento().GuardarAsync(flujo, "logo.png");

        (await ServirAsync(_gestor, _a)).Cuerpo.Should().Equal(_pngDeA);
    }

    [Fact]
    public async Task Si_el_blob_de_la_columna_falta_da_el_mismo_404()
    {
        var clave = (await _propietario.Tenants.AsNoTracking().SingleAsync(t => t.Id == _a)).LogoArchivoClave!;
        using (AmbitoTenantExplicito.Establecer(_a))
            await Almacenamiento().EliminarAsync(clave);

        var respuesta = await ServirAsync(_gestor, _a);
        var inexistente = await ServirAsync(_gestor, Guid.NewGuid());

        respuesta.Estado.Should().Be(StatusCodes.Status404NotFound);
        respuesta.Cabeceras.Should().Equal(inexistente.Cabeceras);
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private sealed record Respuesta(int Estado, byte[] Cuerpo, Dictionary<string, string> Cabeceras);

    private async Task<Respuesta> ServirAsync(Guid usuario, Guid tenantId, string? version = null)
    {
        await using var runtime = CrearRuntime(usuario);
        var handler = new ObtenerLogoTenantQueryHandler(
            new SesionPrivilegiadaAusente(), new CurrentUserServiceFalso(usuario, tenantOrigenId: _origen), runtime, runtime);

        var servicios = new ServiceCollection();
        servicios.AddLogging();
        var contexto = new DefaultHttpContext { RequestServices = servicios.BuildServiceProvider() };
        contexto.Response.Body = new MemoryStream();
        if (version is not null)
            contexto.Request.QueryString = new QueryString($"?v={version}");

        var resultado = await LogoTenantEndpoints.ServirAsync(
            tenantId, contexto, new MediatorDelLogo(handler), Almacenamiento(), CancellationToken.None);
        await resultado.ExecuteAsync(contexto);

        return new Respuesta(
            contexto.Response.StatusCode,
            ((MemoryStream)contexto.Response.Body).ToArray(),
            contexto.Response.Headers
                .Where(c => c.Key != "Content-Length")
                .ToDictionary(c => c.Key, c => c.Value.ToString()));
    }

    /// <summary>El Tenant de la petición es el de origen del Gestor CAE, como en producción.</summary>
    private DiskFileStorageService Almacenamiento() =>
        new(
            Options.Create(new DiskFileStorageServiceOptions { Ruta = _rutaArchivos }),
            new EntornoDePrueba(),
            new TenantActualDeLaPeticion(_origen),
            _protector,
            new AlertaOperativaNula(),
            NullLogger<DiskFileStorageService>.Instance);

    private async Task FijarLogoAsync(Tenant tenant, byte[] png, string version)
    {
        string clave;
        using (AmbitoTenantExplicito.Establecer(tenant.Id))
        using (var flujo = new MemoryStream(png))
            clave = await Almacenamiento().GuardarAsync(flujo, "logo.png");

        tenant.EstablecerLogo(clave, version, DateTime.UtcNow);
        await _propietario.SaveChangesAsync();
    }

    private CaeManagerDbContext CrearRuntime(Guid usuario)
    {
        var tenantDeLaPeticion = new TenantActualDeLaPeticion(_origen);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(
                    tenantDeLaPeticion, new SinClienteActivo(), new CurrentUserServiceFalso(usuario, tenantOrigenId: _origen),
                    BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
    }

    private sealed class MediatorDelLogo(ObtenerLogoTenantQueryHandler handler) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerLogoTenantQuery query
                ? (Task<TResponse>)(object)handler.Handle(query, cancellationToken)
                : throw new NotSupportedException($"El doble no cubre {request.GetType().Name}.");

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
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

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class AlertaOperativaNula : IAlertaOperativa
    {
        public void Emitir(string mensaje, NivelAlertaOperativa nivel) { }
        public void CapturarExcepcion(Exception excepcion) { }
        public void DejarMigaDePan(string mensaje) { }
        public IDisposable IniciarAmbitoDeCaptura() => new AmbitoVacio();

        private sealed class AmbitoVacio : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class EntornoDePrueba : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "CaeManager.IntegrationTests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
