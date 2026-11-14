using CaeManager.Application.Common;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Cumplimiento;
using CaeManager.Domain.Tenants;
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

namespace CaeManager.IntegrationTests.Cumplimiento;

/// <summary>
/// La evidencia contractual del Tenant propietario (#2009) <b>como <c>cae_app_runtime</c></b>, con los
/// interceptores de sellado y de sesión RLS reales: el repositorio escribe y lee solo en el Tenant de la
/// sesión, lo aceptado no se reescribe ni se borra, y una Solicitud de alta no deja dos aceptaciones del
/// mismo documento.
/// </summary>
public class AceptacionContractualTenantBajoRlsTests : IAsyncLifetime
{
    private static readonly string HashDePrueba = new('c', 64);

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuarioId = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _aceptacionAjena;

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

        using (AmbitoTenantExplicito.Establecer(tenantAjeno.Id))
        {
            var ajena = Aceptacion(Guid.NewGuid(), "Condiciones");
            _propietario.AceptacionesContractualesTenant.Add(ajena);
            await _propietario.SaveChangesAsync();
            _aceptacionAjena = ajena.Id;
        }

        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task El_repositorio_escribe_y_lee_solo_en_el_Tenant_de_la_sesion()
    {
        var solicitud = Guid.NewGuid();
        Guid propia;
        await using (var runtime = CrearRuntime())
        {
            var repositorio = new AceptacionContractualTenantRepository(runtime);
            var aceptacion = Aceptacion(solicitud, "Condiciones");
            var encargo = Aceptacion(solicitud, "Encargo");
            repositorio.Agregar(aceptacion);
            repositorio.Agregar(encargo);
            await runtime.SaveChangesAsync();

            // Un envío que llegó a Enviado y otro que se quedó en Preparado o acabó en NoConfirmado: solo el
            // primero cuenta como acuse.
            var enviado = Guid.NewGuid();
            repositorio.AgregarAcuse(new AcuseAceptacionContractualTenant(aceptacion.Id, _usuarioId, enviado, FaseAcuseAceptacionContractual.Preparado, DateTime.UtcNow));
            repositorio.AgregarAcuse(new AcuseAceptacionContractualTenant(aceptacion.Id, _usuarioId, enviado, FaseAcuseAceptacionContractual.Enviado, DateTime.UtcNow));
            var fallido = Guid.NewGuid();
            repositorio.AgregarAcuse(new AcuseAceptacionContractualTenant(encargo.Id, _usuarioId, fallido, FaseAcuseAceptacionContractual.Preparado, DateTime.UtcNow));
            repositorio.AgregarAcuse(new AcuseAceptacionContractualTenant(encargo.Id, _usuarioId, fallido, FaseAcuseAceptacionContractual.NoConfirmado, DateTime.UtcNow));
            repositorio.AgregarAcuse(new AcuseAceptacionContractualTenant(encargo.Id, _usuarioId, Guid.NewGuid(), FaseAcuseAceptacionContractual.Preparado, DateTime.UtcNow));
            await runtime.SaveChangesAsync();
            propia = aceptacion.Id;
        }

        await using (var runtime = CrearRuntime())
        {
            var repositorio = new AceptacionContractualTenantRepository(runtime);
            (await repositorio.ExistenDeSolicitudAltaAsync(solicitud, _usuarioId)).Should().BeTrue();
            (await repositorio.ExistenDeSolicitudAltaAsync(solicitud, Guid.NewGuid())).Should().BeFalse(
                "lo guardado para una cuenta no da por registrada la de otra: sigue a las comprobaciones de autoridad");
            var lista = await repositorio.ListarAsync();
            lista.Should().HaveCount(2).And.OnlyContain(a => a.TenantId == _tenantSesion);
            (await repositorio.UltimoAcusePorAceptacionAsync()).Keys.Should().Equal(propia);

            (await runtime.AceptacionesContractualesTenant.IgnoreQueryFilters().CountAsync(a => a.Id == _aceptacionAjena)).Should().Be(0,
                "sin filtro global de EF, solo RLS puede ocultar la aceptación del Tenant ajeno");
        }

        await using var propietario = CrearPropietarioLimpio();
        (await propietario.AceptacionesContractualesTenant.IgnoreQueryFilters().CountAsync(a => a.Id == _aceptacionAjena)).Should().Be(1,
            "control del instrumento: la aceptación ajena existe");
    }

    [Fact]
    public async Task Una_Solicitud_de_alta_no_deja_dos_aceptaciones_del_mismo_documento()
    {
        var solicitud = Guid.NewGuid();
        await using var runtime = CrearRuntime();
        runtime.AceptacionesContractualesTenant.Add(Aceptacion(solicitud, "Condiciones"));
        await runtime.SaveChangesAsync();
        runtime.AceptacionesContractualesTenant.Add(Aceptacion(solicitud, "Condiciones"));

        var excepcion = await Record.ExceptionAsync(() => runtime.SaveChangesAsync());

        excepcion.Should().BeOfType<DbUpdateException>()
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task Un_envio_del_acuse_no_deja_dos_filas_de_la_misma_fase()
    {
        await using var runtime = CrearRuntime();
        var aceptacion = Aceptacion(Guid.NewGuid(), "Condiciones");
        runtime.AceptacionesContractualesTenant.Add(aceptacion);
        var envio = Guid.NewGuid();
        runtime.AcusesAceptacionContractualTenant.Add(
            new AcuseAceptacionContractualTenant(aceptacion.Id, _usuarioId, envio, FaseAcuseAceptacionContractual.Enviado, DateTime.UtcNow));
        await runtime.SaveChangesAsync();
        runtime.AcusesAceptacionContractualTenant.Add(
            new AcuseAceptacionContractualTenant(aceptacion.Id, _usuarioId, envio, FaseAcuseAceptacionContractual.Enviado, DateTime.UtcNow));

        var excepcion = await Record.ExceptionAsync(() => runtime.SaveChangesAsync());

        excepcion.Should().BeOfType<DbUpdateException>()
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task El_repositorio_ve_la_transaccion_abierta_y_los_cambios_pendientes()
    {
        // EnvioAcuseAceptacionContractual se niega con cualquiera de los dos: su guardado previo al correo
        // tiene que ser definitivo y solo suyo.
        await using var runtime = CrearRuntime();
        var repositorio = new AceptacionContractualTenantRepository(runtime);
        repositorio.HayTransaccionAbierta.Should().BeFalse();
        repositorio.HayCambiosPendientes.Should().BeFalse();

        await using (await runtime.Database.BeginTransactionAsync())
            repositorio.HayTransaccionAbierta.Should().BeTrue();

        repositorio.Agregar(Aceptacion(Guid.NewGuid(), "Condiciones"));
        repositorio.HayCambiosPendientes.Should().BeTrue();
    }

    [Fact]
    public async Task Las_aceptaciones_anadidas_y_sin_guardar_cuentan_para_la_idempotencia()
    {
        // El Command del alta puede repetir su delegado con el mismo contexto tras un fallo de guardado.
        var solicitud = Guid.NewGuid();
        await using var runtime = CrearRuntime();
        var repositorio = new AceptacionContractualTenantRepository(runtime);
        (await repositorio.ExistenDeSolicitudAltaAsync(solicitud, _usuarioId)).Should().BeFalse();

        repositorio.Agregar(Aceptacion(solicitud, "Condiciones"));

        (await repositorio.ExistenDeSolicitudAltaAsync(solicitud, _usuarioId)).Should().BeTrue();
        (await repositorio.ExistenDeSolicitudAltaAsync(solicitud, Guid.NewGuid())).Should().BeFalse(
            "una fila pendiente aún no lleva Tenant: la de otra cuenta no da por registrada esta");
    }

    [Fact]
    public async Task La_politica_no_deja_insertar_en_otro_Tenant_aunque_se_salte_el_sellado()
    {
        await using var runtime = CrearRuntime();
        await runtime.Database.OpenConnectionAsync();

        var excepcion = await Record.ExceptionAsync(() => runtime.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AceptacionesContractualesTenant"
                ("Id", "TenantId", "UsuarioId", "SolicitudAltaId", "Documento", "Version", "HashSha256",
                 "DeclaraFacultades", "Ip", "AceptadaEnUtc", "ImportePermanencia", "RegistradaEnUtc")
            VALUES ({Guid.NewGuid()}, {_tenantAjeno}, {_usuarioId}, {Guid.NewGuid()}, 'Condiciones', '1.0', {HashDePrueba},
                    true, '203.0.113.7', now(), NULL, now())
            """));

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("42501",
            "el WITH CHECK de aislamiento_tenant rechaza una fila de otro Tenant");
    }

    [Theory]
    [InlineData("cae_app_soporte", "AceptacionesContractualesTenant")]
    [InlineData("cae_app_soporte", "AcusesAceptacionContractualTenant")]
    [InlineData("cae_app_aprovisionamiento", "AceptacionesContractualesTenant")]
    [InlineData("cae_app_aprovisionamiento", "AcusesAceptacionContractualTenant")]
    public async Task Soporte_y_aprovisionamiento_no_tienen_ningun_privilegio_sobre_la_evidencia(string rol, string tabla)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        foreach (var privilegio in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
        {
            await using var orden = new NpgsqlCommand("SELECT has_table_privilege(@rol, quote_ident(@tabla), @privilegio);", conexion);
            orden.Parameters.AddWithValue("rol", rol);
            orden.Parameters.AddWithValue("tabla", tabla);
            orden.Parameters.AddWithValue("privilegio", privilegio);

            ((bool)(await orden.ExecuteScalarAsync())!).Should().BeFalse($"{rol} no debe tener {privilegio} sobre {tabla}");
        }
    }

    [Theory]
    [InlineData("""UPDATE "AceptacionesContractualesTenant" SET "Version" = 'otra'""")]
    [InlineData("""UPDATE "AceptacionesContractualesTenant" SET "HashSha256" = repeat('d', 64)""")]
    [InlineData("""DELETE FROM "AceptacionesContractualesTenant" """)]
    [InlineData("""UPDATE "AcusesAceptacionContractualTenant" SET "Fase" = 'Enviado'""")]
    [InlineData("""UPDATE "AcusesAceptacionContractualTenant" SET "RegistradoEnUtc" = now()""")]
    [InlineData("""DELETE FROM "AcusesAceptacionContractualTenant" """)]
    public async Task La_aplicacion_no_puede_reescribir_ni_borrar_lo_aceptado(string orden)
    {
        await using var runtime = CrearRuntime();

        var excepcion = await Record.ExceptionAsync(() => runtime.Database.ExecuteSqlRawAsync(orden));

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("42501");
    }

    private AceptacionContractualTenant Aceptacion(Guid solicitudAltaId, string documento) => new(
        _usuarioId, solicitudAltaId,
        new AceptacionDeclarada(documento, "1.0", HashDePrueba, true, "203.0.113.7", DateTime.UtcNow.AddMinutes(-1), 177.00m),
        DateTime.UtcNow);

    private CurrentUserServiceFalso Usuario() => new(_usuarioId, Roles.Administrador, tenantOrigenId: _tenantSesion);

    private CaeManagerDbContext CrearPropietarioLimpio()
    {
        var tenant = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion)
            .AddInterceptors(new TenantSelladoInterceptor(tenant))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    private CaeManagerDbContext CrearRuntime()
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantDeLaPeticion),
                new ConcurrenciaOptimistaInterceptor(),
                new TenantRlsConnectionInterceptor(_tenantDeLaPeticion, new SinClienteActivo(), Usuario(), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _tenantDeLaPeticion);
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
