using CaeManager.Application.Common;
using CaeManager.Domain.Cumplimiento.Subencargados;
using CaeManager.Domain.Plataforma;
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
/// #2010, D-7, <b>como <c>cae_app_runtime</c></b> con los interceptores de sellado y de sesión RLS reales:
/// el catálogo de subencargados lo escribe solo un Actor de Plataforma TALVEG con AdminPlataforma global; el
/// aviso, también, en el Tenant propietario del contexto y a su propio nombre; la objeción, solo el
/// Administrador de ese Tenant; avisos y objeciones no se reescriben ni se borran, y cada Tenant ve solo
/// los suyos (también la exclusión que apaga su IA).
/// </summary>
public class SubencargadosBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _actorGlobal = Guid.NewGuid();
    private readonly Guid _actorAcotado = Guid.NewGuid();
    private readonly Guid _usuarioDelTenant = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _subencargadoId;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantSesion = new Tenant("Tenant propietario de la sesión");
        var tenantAjeno = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantSesion, tenantAjeno);
        var ahora = DateTime.UtcNow;
        _propietario.ConcesionesPrivilegio.AddRange(
            ConcesionPrivilegio.Global(_actorGlobal, ahora.AddMinutes(-10), ahora.AddHours(4)),
            ConcesionPrivilegio.SobreTenants(_actorAcotado, CapacidadPrivilegio.AdminPlataforma, [tenantSesion.Id],
                vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4)));
        var subencargado = Nuevo(_actorGlobal);
        subencargado.MarcarAvisado(ahora);
        _propietario.Subencargados.Add(subencargado);
        await _propietario.SaveChangesAsync();
        _tenantSesion = tenantSesion.Id;
        _tenantAjeno = tenantAjeno.Id;
        _subencargadoId = subencargado.Id;

        // Lo del Tenant ajeno, sembrado como propietario (superusuario: sin RLS).
        using (AmbitoTenantExplicito.Establecer(_tenantAjeno))
        {
            _propietario.AvisosCambioSubencargado.Add(
                new AvisoCambioSubencargado(_subencargadoId, Guid.NewGuid(), Guid.NewGuid(), FaseAvisoCambioSubencargado.Enviado, ahora, _actorGlobal));
            _propietario.ObjecionesSubencargado.Add(new ObjecionSubencargado(subencargado, Guid.NewGuid(), "No queremos IA", ahora));
            await _propietario.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task El_Actor_de_Plataforma_global_da_de_alta_el_subencargado_y_el_aviso_y_cada_Tenant_ve_solo_lo_suyo()
    {
        await using (var runtime = CrearRuntime(_actorGlobal, Roles.Administrador))
        {
            var repositorio = new SubencargadoRepository(runtime);
            repositorio.Agregar(Nuevo(_actorGlobal));
            repositorio.AgregarAviso(new AvisoCambioSubencargado(
                _subencargadoId, Guid.NewGuid(), Guid.NewGuid(), FaseAvisoCambioSubencargado.Enviado, DateTime.UtcNow, _actorGlobal));
            await runtime.SaveChangesAsync();

            (await repositorio.TieneAvisoProbadoAsync(_subencargadoId, enviadoComoTardeEnUtc: null)).Should().BeTrue();
            (await runtime.AvisosCambioSubencargado.IgnoreQueryFilters().CountAsync()).Should().Be(1,
                "sin filtro global de EF, solo RLS oculta el aviso del Tenant ajeno");
            (await runtime.ObjecionesSubencargado.IgnoreQueryFilters().CountAsync()).Should().Be(0,
                "sin filtro global de EF, solo RLS oculta la objeción del Tenant ajeno");
        }

        await using var limpio = CrearPropietarioLimpio();
        (await limpio.AvisosCambioSubencargado.IgnoreQueryFilters().CountAsync()).Should().Be(2, "control del instrumento");
        (await limpio.ObjecionesSubencargado.IgnoreQueryFilters().CountAsync()).Should().Be(1, "control del instrumento");
    }

    [Fact]
    public async Task La_exclusion_por_objecion_solo_apaga_el_conmutador_del_Tenant_que_objeto()
    {
        await using var runtime = CrearRuntime(_usuarioDelTenant, Roles.Administrador);
        var repositorio = new SubencargadoRepository(runtime);

        (await repositorio.ExcluyeConmutadorAsync(_tenantSesion, ConmutadorExclusionSubencargado.Ia)).Should().BeFalse(
            "la objeción del Tenant ajeno no excluye al de la sesión");

        await using var limpio = CrearPropietarioLimpio();
        using var _ = AmbitoTenantExplicito.Establecer(_tenantAjeno);
        (await new SubencargadoRepository(limpio).ExcluyeConmutadorAsync(_tenantAjeno, ConmutadorExclusionSubencargado.Ia)).Should().BeTrue(
            "control del instrumento: la objeción del Tenant ajeno sí lo excluye a él");
    }

    [Theory]
    [InlineData("acotado")]
    [InlineData("sin concesión")]
    public async Task Sin_AdminPlataforma_global_no_se_escribe_el_catalogo_ni_el_aviso(string quien)
    {
        var usuario = quien == "acotado" ? _actorAcotado : _usuarioDelTenant;

        await using (var runtime = CrearRuntime(usuario, Roles.Administrador))
        {
            runtime.Subencargados.Add(Nuevo(usuario));
            await DebeRechazarAsync(runtime, "el alta del catálogo exige AdminPlataforma global");
        }

        await using (var runtime = CrearRuntime(usuario, Roles.Administrador))
        {
            runtime.AvisosCambioSubencargado.Add(new AvisoCambioSubencargado(
                _subencargadoId, Guid.NewGuid(), Guid.NewGuid(), FaseAvisoCambioSubencargado.Enviado, DateTime.UtcNow, usuario));
            await DebeRechazarAsync(runtime, "solo_el_actor_de_plataforma_avisa");
        }
    }

    [Fact]
    public async Task El_aviso_queda_a_nombre_de_quien_firma_el_contexto()
    {
        await using var runtime = CrearRuntime(_actorGlobal, Roles.Administrador);
        runtime.AvisosCambioSubencargado.Add(new AvisoCambioSubencargado(
            _subencargadoId, Guid.NewGuid(), Guid.NewGuid(), FaseAvisoCambioSubencargado.Enviado, DateTime.UtcNow, Guid.NewGuid()));

        await DebeRechazarAsync(runtime, "AvisadoPorUsuarioId tiene que ser la identidad del contexto firmado");
    }

    [Fact]
    public async Task Quien_no_es_Administrador_del_Tenant_propietario_no_objeta()
    {
        // El usuario de la sesión dice ser Administrador, pero en la base de datos no lo es de ese Tenant:
        // la política restrictiva lo comprueba con app_es_administrador_del_tenant, no con lo que diga la sesión.
        var subencargado = await _propietario.Subencargados.AsNoTracking().SingleAsync(s => s.Id == _subencargadoId);
        await using var runtime = CrearRuntime(_usuarioDelTenant, Roles.Administrador);
        runtime.ObjecionesSubencargado.Add(new ObjecionSubencargado(subencargado, _usuarioDelTenant, "Motivo", DateTime.UtcNow));

        await DebeRechazarAsync(runtime, "solo_el_administrador_objeta");
    }

    [Fact]
    public async Task La_politica_no_deja_insertar_un_aviso_en_otro_Tenant_aunque_se_salte_el_sellado()
    {
        await using var runtime = CrearRuntime(_actorGlobal, Roles.Administrador);
        await runtime.Database.OpenConnectionAsync();

        var excepcion = await Record.ExceptionAsync(() => runtime.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AvisosCambioSubencargado"
                ("Id", "TenantId", "SubencargadoId", "EnvioId", "UsuarioDestinatarioId", "Fase", "RegistradoEnUtc", "AvisadoPorUsuarioId")
            VALUES ({Guid.NewGuid()}, {_tenantAjeno}, {_subencargadoId}, {Guid.NewGuid()}, {Guid.NewGuid()}, 'Enviado', now(), {_actorGlobal})
            """));

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("42501",
            "el WITH CHECK de aislamiento_tenant rechaza una fila de otro Tenant");
    }

    [Theory]
    [InlineData("""UPDATE "AvisosCambioSubencargado" SET "Fase" = 'Enviado'""")]
    [InlineData("""UPDATE "AvisosCambioSubencargado" SET "RegistradoEnUtc" = now() - interval '60 days'""")]
    [InlineData("""DELETE FROM "AvisosCambioSubencargado" """)]
    [InlineData("""UPDATE "ObjecionesSubencargado" SET "Efecto" = 'ResolucionSinPenalizacionDisponible'""")]
    [InlineData("""DELETE FROM "ObjecionesSubencargado" """)]
    [InlineData("""DELETE FROM "Subencargados" """)]
    public async Task La_aplicacion_no_reescribe_ni_borra_avisos_objeciones_ni_subencargados(string orden)
    {
        await using var runtime = CrearRuntime(_actorGlobal, Roles.Administrador);

        var excepcion = await Record.ExceptionAsync(() => runtime.Database.ExecuteSqlRawAsync(orden));

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("42501");
    }

    [Theory]
    [InlineData("cae_app_runtime", "Subencargados", "SELECT,INSERT,UPDATE")]
    [InlineData("cae_app_runtime", "AvisosCambioSubencargado", "SELECT,INSERT")]
    [InlineData("cae_app_runtime", "ObjecionesSubencargado", "SELECT,INSERT")]
    [InlineData("cae_app_soporte", "Subencargados", "")]
    [InlineData("cae_app_soporte", "AvisosCambioSubencargado", "")]
    [InlineData("cae_app_soporte", "ObjecionesSubencargado", "")]
    [InlineData("cae_app_aprovisionamiento", "Subencargados", "")]
    [InlineData("cae_app_aprovisionamiento", "AvisosCambioSubencargado", "")]
    [InlineData("cae_app_aprovisionamiento", "ObjecionesSubencargado", "")]
    public async Task Cada_rol_tiene_exactamente_sus_privilegios(string rol, string tabla, string concedidos)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        var esperados = concedidos.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var privilegio in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
        {
            await using var orden = new NpgsqlCommand("SELECT has_table_privilege(@rol, quote_ident(@tabla), @privilegio);", conexion);
            orden.Parameters.AddWithValue("rol", rol);
            orden.Parameters.AddWithValue("tabla", tabla);
            orden.Parameters.AddWithValue("privilegio", privilegio);

            ((bool)(await orden.ExecuteScalarAsync())!).Should().Be(esperados.Contains(privilegio), $"{rol} y {privilegio} sobre {tabla}");
        }
    }

    [Theory]
    [InlineData("Subencargados")]
    [InlineData("AvisosCambioSubencargado")]
    [InlineData("ObjecionesSubencargado")]
    public async Task Las_tres_tablas_tienen_RLS_forzada(string tabla)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var orden = new NpgsqlCommand(
            "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = quote_ident(@tabla)::regclass;", conexion);
        orden.Parameters.AddWithValue("tabla", tabla);

        ((bool)(await orden.ExecuteScalarAsync())!).Should().BeTrue();
    }

    private static async Task DebeRechazarAsync(CaeManagerDbContext runtime, string motivo)
    {
        var excepcion = await Record.ExceptionAsync(() => runtime.SaveChangesAsync());

        excepcion.Should().BeOfType<DbUpdateException>()
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("42501", motivo);
    }

    private static Subencargado Nuevo(Guid actor) => new(
        "Proveedor de IA", "Lectura asistida de documentos", "UE; sin transferencia internacional", true,
        ConmutadorExclusionSubencargado.Ia, actor, DateTime.UtcNow);

    private CaeManagerDbContext CrearPropietarioLimpio()
    {
        var tenant = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion)
            .AddInterceptors(new TenantSelladoInterceptor(tenant))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    private CaeManagerDbContext CrearRuntime(Guid usuarioId, string rol)
    {
        var tenant = new TenantActualDeLaPeticion(_tenantSesion);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenant),
                new ConcurrenciaOptimistaInterceptor(),
                new TenantRlsConnectionInterceptor(tenant, new SinClienteActivo(),
                    new CurrentUserServiceFalso(usuarioId, rol, tenantOrigenId: _tenantSesion), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
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
