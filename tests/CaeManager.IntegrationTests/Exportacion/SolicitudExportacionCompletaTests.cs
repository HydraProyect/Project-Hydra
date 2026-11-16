using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Exportacion;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Exportacion;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Exportacion;
using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Exportacion;

/// <summary>
/// La exportación completa en autoservicio contra PostgreSQL (#2008, D-8): la RLS de la tabla nueva bajo
/// <c>cae_app_runtime</c>, el candado por Tenant que deja una sola solicitud de dos concurrentes, el índice único
/// parcial de respaldo y el servicio de fondo que genera el zip bajo la RLS del Tenant propietario y borra la
/// contraseña en el mismo guardado que la marca lista.
/// </summary>
public class SolicitudExportacionCompletaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly string _carpetaZips = Path.Combine(Path.GetTempPath(), "exportaciones-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _administradorId = Guid.NewGuid();
    private Guid _tenantA;
    private Guid _tenantB;

    public async Task InitializeAsync()
    {
        await using var propietario = CrearPropietario(null);
        await propietario.Database.MigrateAsync();
        var tenantA = new Tenant("Tenant propietario que exporta");
        var tenantB = new Tenant("Tenant propietario ajeno");
        propietario.Tenants.AddRange(tenantA, tenantB);
        await propietario.SaveChangesAsync();
        (_tenantA, _tenantB) = (tenantA.Id, tenantB.Id);
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_carpetaZips))
            Directory.Delete(_carpetaZips, recursive: true);
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    // ---- RLS ----

    [Fact]
    public async Task Bajo_runtime_un_Tenant_no_ve_las_solicitudes_de_otro_ni_saltando_el_filtro()
    {
        Guid solicitudId;
        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            await using var runtimeA = CrearRuntime();
            var solicitud = new SolicitudExportacionCompleta(_administradorId, "protegida", DateTime.UtcNow);
            runtimeA.SolicitudesExportacionCompleta.Add(solicitud);
            await runtimeA.SaveChangesAsync();
            solicitudId = solicitud.Id;
            (await runtimeA.SolicitudesExportacionCompleta.CountAsync()).Should().Be(1);
        }

        using (AmbitoTenantExplicito.Establecer(_tenantB))
        {
            await using var runtimeB = CrearRuntime();
            (await runtimeB.SolicitudesExportacionCompleta.IgnoreQueryFilters().AnyAsync(s => s.Id == solicitudId))
                .Should().BeFalse("la política aislamiento_tenant oculta la fila de otro Tenant");
            (await runtimeB.Database.ExecuteSqlAsync(
                    $"UPDATE \"SolicitudesExportacionCompleta\" SET \"MotivoFallo\" = 'ajeno' WHERE \"Id\" = {solicitudId}"))
                .Should().Be(0, "tampoco la puede modificar");
        }
    }

    // ---- Una cada 24 horas por Tenant ----

    [Fact]
    public async Task Dos_solicitudes_concurrentes_del_mismo_Tenant_dejan_una_sola()
    {
        await using var testigo = CrearPropietario(_tenantA);
        await using var circuitoUno = CrearPropietario(_tenantA);
        await using var circuitoDos = CrearPropietario(_tenantA);

        var transaccionTestigo = await TomarCandadoAsync(testigo, _tenantA);
        var uno = Solicitud(circuitoUno, _tenantA).Handle(new SolicitarExportacionCompletaCommand(), CancellationToken.None);
        var dos = Solicitud(circuitoDos, _tenantA).Handle(new SolicitarExportacionCompletaCommand(), CancellationToken.None);

        (await EsperanElCandadoAsync(_tenantA, 2, uno, dos))
            .Should().BeTrue("las dos solicitudes esperan el candado del Tenant antes de contar las de las últimas 24 horas");
        await transaccionTestigo.CommitAsync();

        var resultados = await Task.WhenAll(uno, dos);

        resultados.Count(r => r.EsExitoso).Should().Be(1);
        resultados.Single(r => r.EsFallido).Error.Codigo
            .Should().Be(ErroresSolicitudExportacionCompleta.UnaCadaVeinticuatroHoras.Codigo);
        await using var lectura = CrearPropietario(_tenantA);
        (await lectura.SolicitudesExportacionCompleta.IgnoreQueryFilters().CountAsync(s => s.TenantId == _tenantA)).Should().Be(1);
        (await lectura.RegistrosAuditoria.IgnoreQueryFilters()
                .CountAsync(r => r.EntidadTipo == AuditoriaExportacionCompleta.EntidadTipo && r.TenantId == _tenantA))
            .Should().Be(1, "solo la solicitud registrada deja su fila de auditoría");
    }

    [Fact]
    public async Task Las_solicitudes_de_dos_Tenants_no_se_bloquean()
    {
        await using var testigo = CrearPropietario(_tenantA);
        await using var circuitoB = CrearPropietario(_tenantB);

        var transaccionTestigo = await TomarCandadoAsync(testigo, _tenantA);
        var deB = Solicitud(circuitoB, _tenantB).Handle(new SolicitarExportacionCompletaCommand(), CancellationToken.None);

        (await Task.WhenAny(deB, Task.Delay(TimeSpan.FromSeconds(30)))).Should().BeSameAs(deB, "el candado de A no detiene a B");
        (await deB).EsExitoso.Should().BeTrue();
        await transaccionTestigo.RollbackAsync();
    }

    [Fact]
    public async Task El_indice_unico_parcial_impide_dos_solicitudes_vivas_del_mismo_Tenant_sin_el_candado()
    {
        await using var contexto = CrearPropietario(_tenantA);
        contexto.SolicitudesExportacionCompleta.Add(new SolicitudExportacionCompleta(_administradorId, "p1", DateTime.UtcNow));
        await contexto.SaveChangesAsync();

        contexto.SolicitudesExportacionCompleta.Add(new SolicitudExportacionCompleta(_administradorId, "p2", DateTime.UtcNow));
        var guardar = () => contexto.SaveChangesAsync();

        (await guardar.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<Npgsql.PostgresException>()
            .Which.SqlState.Should().Be("23505");
    }

    // ---- Servicio de fondo bajo la RLS del Tenant propietario ----

    [Fact]
    public async Task El_servicio_de_fondo_genera_el_zip_bajo_runtime_borra_la_contrasena_y_audita_cada_fase()
    {
        var protector = new ProtectorContrasenaExportacion(new EphemeralDataProtectionProvider());
        const string contrasena = "contrasena-de-prueba-del-zip";
        Guid solicitudId;
        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            await using var runtimeA = CrearRuntime();
            var solicitud = new SolicitudExportacionCompleta(_administradorId, protector.Proteger(contrasena), DateTime.UtcNow);
            runtimeA.SolicitudesExportacionCompleta.Add(solicitud);
            await runtimeA.SaveChangesAsync();
            solicitudId = solicitud.Id;
        }

        var (servicio, almacen) = CrearServicio(protector);
        await servicio.ProcesarTenantAsync(_tenantA, CancellationToken.None);

        SolicitudExportacionCompleta lista;
        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            await using var runtimeA = CrearRuntime();
            lista = await runtimeA.SolicitudesExportacionCompleta.AsNoTracking().SingleAsync(s => s.Id == solicitudId);
            var fases = await runtimeA.RegistrosAuditoria.AsNoTracking()
                .Where(r => r.EntidadTipo == AuditoriaExportacionCompleta.EntidadTipo && r.EntidadId == solicitudId)
                .Select(r => r.DatosDespues!).ToListAsync();
            fases.Should().ContainSingle(f => f.Contains(AuditoriaExportacionCompleta.FaseLista));
            fases.Should().OnlyContain(f => !f.Contains(contrasena));
        }

        lista.Estado.Should().Be(EstadoSolicitudExportacionCompleta.Lista);
        lista.ContrasenaProtegida.Should().BeNull("la contraseña se borra en el mismo guardado que marca la solicitud lista");
        lista.CaducaEnUtc.Should().Be(lista.DisponibleEnUtc + SolicitudExportacionCompleta.Vigencia);
        lista.TamanoBytes.Should().BeGreaterThan(0);

        await using var flujo = await almacen.AbrirAsync(_tenantA, lista.UbicacionFichero!);
        using var zip = new ZipFile(flujo, leaveOpen: false) { Password = contrasena };
        var todo = new StringBuilder();
        foreach (var entrada in zip.Cast<ZipEntry>())
        {
            entrada.IsCrypted.Should().BeTrue();
            using var lector = new StreamReader(zip.GetInputStream(entrada));
            todo.Append(await lector.ReadToEndAsync());
        }
        todo.ToString().Should().Contain("Tenant propietario que exporta")
            .And.NotContain("Tenant propietario ajeno")
            .And.NotContain("ContrasenaProtegida");

        // Otro Tenant no abre la ubicación de A.
        var ajena = () => almacen.AbrirAsync(_tenantB, lista.UbicacionFichero!);
        await ajena.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task El_barrido_borra_el_fichero_caducado_y_la_marca_caducada()
    {
        var protector = new ProtectorContrasenaExportacion(new EphemeralDataProtectionProvider());
        var reloj = new RelojAjustable(DateTimeOffset.UtcNow);
        Guid solicitudId;
        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            await using var runtimeA = CrearRuntime();
            var solicitud = new SolicitudExportacionCompleta(_administradorId, protector.Proteger("contrasena-de-prueba-del-barrido"), reloj.GetUtcNow().UtcDateTime);
            runtimeA.SolicitudesExportacionCompleta.Add(solicitud);
            await runtimeA.SaveChangesAsync();
            solicitudId = solicitud.Id;
        }

        var (servicio, almacen) = CrearServicio(protector, reloj);
        await servicio.ProcesarTenantAsync(_tenantA, CancellationToken.None);
        var ubicacion = AlmacenExportacionesCompletasEnDisco.UbicacionPara(_tenantA, solicitudId);
        (await almacen.AbrirAsync(_tenantA, ubicacion)).Dispose();

        reloj.Avanzar(SolicitudExportacionCompleta.Vigencia + TimeSpan.FromMinutes(1));
        await servicio.ProcesarTenantAsync(_tenantA, CancellationToken.None);

        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            await using var runtimeA = CrearRuntime();
            var caducada = await runtimeA.SolicitudesExportacionCompleta.AsNoTracking().SingleAsync(s => s.Id == solicitudId);
            caducada.Estado.Should().Be(EstadoSolicitudExportacionCompleta.Caducada);
            caducada.UbicacionFichero.Should().BeNull();
            (await runtimeA.RegistrosAuditoria.AsNoTracking()
                    .CountAsync(r => r.EntidadId == solicitudId && r.DatosDespues!.Contains(AuditoriaExportacionCompleta.FaseCaducada)))
                .Should().Be(1);
        }
        var abrir = () => almacen.AbrirAsync(_tenantA, ubicacion);
        await abrir.Should().ThrowAsync<FileNotFoundException>("el barrido borró el fichero");
    }

    private (GeneradorExportacionCompletaHostedService, AlmacenExportacionesCompletasEnDisco) CrearServicio(
        IProtectorContrasenaExportacion protector, TimeProvider? reloj = null)
    {
        var almacen = new AlmacenExportacionesCompletasEnDisco(
            Options.Create(new OpcionesAlmacenExportacionesCompletas { Ruta = _carpetaZips }),
            Options.Create(new DiskFileStorageServiceOptions()),
            new EntornoDePrueba(_carpetaZips));
        var servicios = new ServiceCollection()
            .AddScoped(_ => CrearRuntime())
            .AddScoped<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>())
            .AddSingleton(almacen)
            .AddSingleton<IFileStorageService, AlmacenSinDocumentos>()
            .AddSingleton(protector)
            .BuildServiceProvider();
        return (new GeneradorExportacionCompletaHostedService(
            servicios.GetRequiredService<IServiceScopeFactory>(), new SinEleccion(), reloj ?? TimeProvider.System,
            NullLogger<GeneradorExportacionCompletaHostedService>.Instance), almacen);
    }

    private static SolicitarExportacionCompletaCommandHandler Solicitud(CaeManagerDbContext contexto, Guid tenantId)
    {
        var usuarioId = Guid.NewGuid();
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        return new SolicitarExportacionCompletaCommandHandler(
            new CurrentUserServiceFalso(usuarioId, "Administrador", tenantId), tenant,
            new ActorFijo(ActorAuditoria.Normal(usuarioId)), new SinSesionPrivilegiada(), new AdministradorEnBase(),
            contexto, new CandadoExportacionCompleta(contexto, new TransaccionDeComando(contexto), tenant),
            new SolicitudExportacionCompletaRepository(contexto),
            new ProtectorContrasenaExportacion(new EphemeralDataProtectionProvider()), contexto, TimeProvider.System);
    }

    private static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> TomarCandadoAsync(
        CaeManagerDbContext contexto, Guid tenantId)
    {
        var transaccion = await contexto.Database.BeginTransactionAsync();
        var clave = CandadoExportacionCompleta.Espacio + tenantId;
        await contexto.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({clave}, 0))");
        return transaccion;
    }

    private async Task<bool> EsperanElCandadoAsync(Guid tenantId, int esperados, params Task[] operaciones)
    {
        var clave = CandadoExportacionCompleta.Espacio + tenantId;
        await using var lectura = CrearPropietario(tenantId);
        for (var intento = 0; intento < 300 && !operaciones.Any(o => o.IsCompleted); intento++)
        {
            var esperando = await lectura.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM pg_locks l
                JOIN pg_database d ON d.oid = l.database
                WHERE l.locktype = 'advisory' AND NOT l.granted AND d.datname = current_database()
                  AND ((l.classid::bigint << 32) | l.objid::bigint) = hashtextextended({clave}, 0)
                """).SingleAsync();
            if (esperando >= esperados) return true;
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>Como propietario de la base: siembra y candado, que no son lo que se mide.</summary>
    private CaeManagerDbContext CrearPropietario(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>Como el servicio de fondo: <c>cae_app_runtime</c>, sin usuario; el Tenant sale del ámbito explícito.</summary>
    private CaeManagerDbContext CrearRuntime()
    {
        var tenant = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenant),
                new TenantRlsConnectionInterceptor(tenant, new SinClienteActivo(), new CurrentUserServiceFalso(), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class AdministradorEnBase : IAdministradorDelTenantPropietario
    {
        public Task<bool> EsAdministradorEnBaseAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class SinEleccion : IEleccionLiderService
    {
        public Task<bool> IntentarEjecutarComoLiderAsync(string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken) =>
            throw new NotSupportedException("La prueba llama a ProcesarTenantAsync directamente.");
    }

    private sealed class AlmacenSinDocumentos : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new FileNotFoundException(identificador);
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EntornoDePrueba(string ruta) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "CaeManager.IntegrationTests";
        public string ContentRootPath { get; set; } = ruta;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RelojAjustable(DateTimeOffset inicio) : TimeProvider
    {
        private DateTimeOffset _ahora = inicio;
        public void Avanzar(TimeSpan intervalo) => _ahora += intervalo;
        public override DateTimeOffset GetUtcNow() => _ahora;
    }
}
