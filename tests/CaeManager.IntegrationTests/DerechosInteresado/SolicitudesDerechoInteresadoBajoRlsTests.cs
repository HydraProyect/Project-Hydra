using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Domain.DerechosInteresado;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.DerechosInteresado;

/// <summary>
/// Las solicitudes de derechos del interesado y las concesiones de capacidad del Tenant (#2002, decisión D-3 del
/// 2026-11-15) <b>como <c>cae_app_runtime</c></b>, con los interceptores de sellado y de sesión RLS reales: cada
/// Tenant ve y escribe solo lo suyo, la política rechaza una fila de otro Tenant aunque se salte el sellado, la
/// aplicación no borra ni reescribe lo registrado fuera de las columnas de cada paso, y Soporte TALVEG y el
/// aprovisionamiento no tienen ningún privilegio sobre las dos tablas.
/// </summary>
public class SolicitudesDerechoInteresadoBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuarioId = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _solicitudAjena;
    private Guid _concesionAjena;

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
            var solicitud = Solicitud();
            var concesion = ConcesionCapacidadTenant.Conceder(
                Guid.NewGuid(), CapacidadTenant.DerechosDelInteresado, _usuarioId, DateTime.UtcNow);
            _propietario.SolicitudesDerechoInteresado.Add(solicitud);
            _propietario.ConcesionesCapacidadTenant.Add(concesion);
            await _propietario.SaveChangesAsync();
            _solicitudAjena = solicitud.Id;
            _concesionAjena = concesion.Id;
        }

        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task Los_repositorios_escriben_y_leen_solo_en_el_Tenant_de_la_sesion()
    {
        var beneficiario = Guid.NewGuid();
        Guid propia;
        await using (var runtime = CrearRuntime())
        {
            var solicitudes = new SolicitudDerechoInteresadoRepository(runtime);
            var concesiones = new ConcesionCapacidadTenantRepository(runtime);
            var solicitud = Solicitud();
            solicitudes.Agregar(solicitud);
            concesiones.Agregar(ConcesionCapacidadTenant.Conceder(
                beneficiario, CapacidadTenant.DerechosDelInteresado, _usuarioId, DateTime.UtcNow));
            (await concesiones.GuardarConcesionAsync(default)).Should().BeTrue();
            propia = solicitud.Id;
        }

        await using (var runtime = CrearRuntime())
        {
            var solicitudes = new SolicitudDerechoInteresadoRepository(runtime);
            var concesiones = new ConcesionCapacidadTenantRepository(runtime);
            (await solicitudes.ListarAsync(default)).Select(s => s.Id).Should().Equal(propia);
            (await solicitudes.ObtenerPorIdAsync(_solicitudAjena, default)).Should().BeNull();
            (await concesiones.TieneVigenteAsync(beneficiario, CapacidadTenant.DerechosDelInteresado, default)).Should().BeTrue();

            (await runtime.SolicitudesDerechoInteresado.IgnoreQueryFilters().CountAsync(s => s.Id == _solicitudAjena)).Should().Be(0,
                "sin filtro global de EF, solo RLS puede ocultar la solicitud del Tenant ajeno");
            (await runtime.ConcesionesCapacidadTenant.IgnoreQueryFilters().CountAsync(c => c.Id == _concesionAjena)).Should().Be(0,
                "sin filtro global de EF, solo RLS puede ocultar la concesión del Tenant ajeno");
        }

        await using var propietario = CrearPropietarioLimpio();
        (await propietario.SolicitudesDerechoInteresado.IgnoreQueryFilters().CountAsync(s => s.Id == _solicitudAjena)).Should().Be(1,
            "control del instrumento: la solicitud ajena existe");
        (await propietario.ConcesionesCapacidadTenant.IgnoreQueryFilters().CountAsync(c => c.Id == _concesionAjena)).Should().Be(1,
            "control del instrumento: la concesión ajena existe");
    }

    [Fact]
    public async Task Cada_paso_se_guarda_como_runtime_y_la_revocacion_tambien()
    {
        var beneficiario = Guid.NewGuid();
        Guid id;
        await using (var runtime = CrearRuntime())
        {
            var solicitud = Solicitud();
            runtime.SolicitudesDerechoInteresado.Add(solicitud);
            runtime.ConcesionesCapacidadTenant.Add(ConcesionCapacidadTenant.Conceder(
                beneficiario, CapacidadTenant.DerechosDelInteresado, _usuarioId, DateTime.UtcNow));
            await runtime.SaveChangesAsync();
            id = solicitud.Id;
        }

        await using (var runtime = CrearRuntime())
        {
            var solicitud = await new SolicitudDerechoInteresadoRepository(runtime).ObtenerPorIdAsync(id, default);
            solicitud!.VerificarIdentidad(_usuarioId, DateTime.UtcNow);
            solicitud.Prorrogar(solicitud.PlazoMaximoProrrogable, "Complejidad.", _usuarioId, DateTime.UtcNow);
            solicitud.IniciarTramitacion(_usuarioId, DateTime.UtcNow);
            solicitud.Resolver("Hecho.", _usuarioId, DateTime.UtcNow);
            var concesion = await new ConcesionCapacidadTenantRepository(runtime)
                .ObtenerVigenteAsync(beneficiario, CapacidadTenant.DerechosDelInteresado, default);
            concesion!.Revocar(_usuarioId, DateTime.UtcNow);
            await runtime.SaveChangesAsync();
        }

        await using (var runtime = CrearRuntime())
        {
            (await runtime.SolicitudesDerechoInteresado.SingleAsync(s => s.Id == id)).Estado.Should().Be(EstadoSolicitudDerecho.Resuelta);
            (await new ConcesionCapacidadTenantRepository(runtime)
                .TieneVigenteAsync(beneficiario, CapacidadTenant.DerechosDelInteresado, default)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task La_auditoria_de_cada_paso_lleva_su_accion_el_actor_real_y_enmascara_los_datos_del_interesado()
    {
        const string Descripcion = "Pide copia de sus datos.";
        const string MotivoProrroga = "Motivo-de-prorroga-reconocible";
        const string Resultado = "Resultado-reconocible-entregado";
        // Sin clave ajena: el Trabajador no necesita existir para que la solicitud lo señale.
        var trabajadorId = Guid.NewGuid();
        var auditoria = new AuditoriaInterceptor(new ActorAuditoriaFijo(ActorAuditoria.Normal(_usuarioId)));
        Guid id;
        await using (var runtime = CrearRuntime(auditoria))
        {
            var solicitud = Solicitud(trabajadorId);
            runtime.SolicitudesDerechoInteresado.Add(solicitud);
            await runtime.SaveChangesAsync();
            id = solicitud.Id;
        }

        await using (var runtime = CrearRuntime(auditoria))
        {
            var solicitud = await new SolicitudDerechoInteresadoRepository(runtime).ObtenerPorIdAsync(id, default);
            solicitud!.VerificarIdentidad(_usuarioId, DateTime.UtcNow);
            await runtime.SaveChangesAsync();
            solicitud.Prorrogar(solicitud.PlazoMaximoProrrogable, MotivoProrroga, _usuarioId, DateTime.UtcNow);
            await runtime.SaveChangesAsync();
            solicitud.IniciarTramitacion(_usuarioId, DateTime.UtcNow);
            await runtime.SaveChangesAsync();
            solicitud.Resolver(Resultado, _usuarioId, DateTime.UtcNow);
            await runtime.SaveChangesAsync();
        }

        var registros = await _propietario.RegistrosAuditoria.IgnoreQueryFilters()
            .Where(r => r.EntidadId == id)
            .ToListAsync();

        registros.Select(r => r.Accion).Should().BeEquivalentTo(
            ["Creado", RegistroAuditoria.AccionIdentidadVerificada, RegistroAuditoria.AccionPlazoProrrogado,
             RegistroAuditoria.AccionTramitacionIniciada, RegistroAuditoria.AccionSolicitudResuelta],
            "cada paso deja su propia acción");
        registros.Should().AllSatisfy(r => r.ActorRealUsuarioId.Should().Be(_usuarioId));
        var textos = string.Join("\n", registros.Select(r => (r.DatosAntes ?? string.Empty) + (r.DatosDespues ?? string.Empty)));
        textos.Should().Contain("***", "control positivo: los campos sensibles están, enmascarados");
        textos.Should().NotContain("Paula Campos").And.NotContain(Descripcion)
            .And.NotContain(MotivoProrroga).And.NotContain(Resultado)
            .And.NotContain(trabajadorId.ToString(), "RegistrosAuditoria la lee Soporte TALVEG: no dice a quién se refiere");
        var creado = registros.Single(r => r.Accion == "Creado");
        creado.DatosDespues.Should().Contain("\"TrabajadorId\":\"***\"", "control positivo: el campo está, enmascarado")
            .And.Contain($"\"Tipo\":{(int)TipoDerechoInteresado.Acceso}", "el tipo de derecho queda en claro");
    }

    [Fact]
    public async Task La_auditoria_de_la_denegacion_lleva_su_accion_el_actor_real_y_enmascara_el_motivo()
    {
        const string MotivoDenegacion = "Motivo-de-denegacion-reconocible";
        var auditoria = new AuditoriaInterceptor(new ActorAuditoriaFijo(ActorAuditoria.Normal(_usuarioId)));
        Guid id;
        await using (var runtime = CrearRuntime(auditoria))
        {
            var solicitud = Solicitud();
            runtime.SolicitudesDerechoInteresado.Add(solicitud);
            await runtime.SaveChangesAsync();
            id = solicitud.Id;
        }

        await using (var runtime = CrearRuntime(auditoria))
        {
            var solicitud = await new SolicitudDerechoInteresadoRepository(runtime).ObtenerPorIdAsync(id, default);
            solicitud!.Denegar(MotivoDenegacion, _usuarioId, DateTime.UtcNow);
            await runtime.SaveChangesAsync();
        }

        var registros = await _propietario.RegistrosAuditoria.IgnoreQueryFilters()
            .Where(r => r.EntidadId == id)
            .ToListAsync();

        registros.Select(r => r.Accion).Should().BeEquivalentTo(
            ["Creado", RegistroAuditoria.AccionSolicitudDenegada],
            "la denegación deja su propia acción");
        registros.Should().AllSatisfy(r => r.ActorRealUsuarioId.Should().Be(_usuarioId));
        var denegacion = registros.Single(r => r.Accion == RegistroAuditoria.AccionSolicitudDenegada);
        denegacion.DatosDespues.Should().Contain("MotivoDenegacion",
            "control positivo: el motivo está en lo que cambió, enmascarado");
        var textos = string.Join("\n", registros.Select(r => (r.DatosAntes ?? string.Empty) + (r.DatosDespues ?? string.Empty)));
        textos.Should().NotContain(MotivoDenegacion);
    }

    [Fact]
    public async Task La_auditoria_de_la_concesion_y_su_revocacion_lleva_su_accion_el_actor_real_y_los_datos_en_claro()
    {
        var beneficiario = Guid.NewGuid();
        var auditoria = new AuditoriaInterceptor(new ActorAuditoriaFijo(ActorAuditoria.Normal(_usuarioId)));
        Guid id;
        await using (var runtime = CrearRuntime(auditoria))
        {
            var repositorio = new ConcesionCapacidadTenantRepository(runtime);
            var concesion = ConcesionCapacidadTenant.Conceder(
                beneficiario, CapacidadTenant.DerechosDelInteresado, _usuarioId, DateTime.UtcNow);
            repositorio.Agregar(concesion);
            (await repositorio.GuardarConcesionAsync(default)).Should().BeTrue();
            id = concesion.Id;
        }

        await using (var runtime = CrearRuntime(auditoria))
        {
            var concesion = await new ConcesionCapacidadTenantRepository(runtime)
                .ObtenerVigenteAsync(beneficiario, CapacidadTenant.DerechosDelInteresado, default);
            concesion!.Revocar(_usuarioId, DateTime.UtcNow);
            await runtime.SaveChangesAsync();
        }

        var registros = await _propietario.RegistrosAuditoria.IgnoreQueryFilters()
            .Where(r => r.EntidadId == id)
            .ToListAsync();

        registros.Select(r => r.Accion).Should().BeEquivalentTo(
            ["Creado", RegistroAuditoria.AccionCapacidadRevocada], "conceder y revocar dejan cada uno su acción");
        registros.Should().AllSatisfy(r =>
        {
            r.EntidadTipo.Should().Be(nameof(ConcesionCapacidadTenant));
            r.ActorRealUsuarioId.Should().Be(_usuarioId);
            r.TenantId.Should().Be(_tenantSesion);
        });
        // La concesión no lleva datos del interesado: quién la recibe, qué capacidad y quién la concedió o revocó
        // quedan en claro, que es lo que el rastro tiene que poder contar.
        var textos = string.Join("\n", registros.Select(r => (r.DatosAntes ?? string.Empty) + (r.DatosDespues ?? string.Empty)));
        textos.Should().NotContain("***");
        registros.Single(r => r.Accion == "Creado").DatosDespues.Should()
            .Contain(beneficiario.ToString())
            .And.Contain($"\"{nameof(ConcesionCapacidadTenant.Capacidad)}\":{(int)CapacidadTenant.DerechosDelInteresado}");
        registros.Single(r => r.Accion == RegistroAuditoria.AccionCapacidadRevocada).DatosDespues.Should()
            .Contain(nameof(ConcesionCapacidadTenant.RevocadaPorUsuarioId)).And.Contain(_usuarioId.ToString());
    }

    [Fact]
    public async Task No_hay_dos_concesiones_vigentes_de_la_misma_cuenta_y_capacidad()
    {
        var beneficiario = Guid.NewGuid();
        await using var runtime = CrearRuntime();
        var repositorio = new ConcesionCapacidadTenantRepository(runtime);
        repositorio.Agregar(ConcesionCapacidadTenant.Conceder(beneficiario, CapacidadTenant.DerechosDelInteresado, _usuarioId, DateTime.UtcNow));
        (await repositorio.GuardarConcesionAsync(default)).Should().BeTrue();

        repositorio.Agregar(ConcesionCapacidadTenant.Conceder(beneficiario, CapacidadTenant.DerechosDelInteresado, _usuarioId, DateTime.UtcNow));

        (await repositorio.GuardarConcesionAsync(default)).Should().BeFalse("el índice único parcial frena la segunda vigente");
    }

    [Fact]
    public async Task La_politica_no_deja_insertar_en_otro_Tenant_aunque_se_salte_el_sellado()
    {
        await using var runtime = CrearRuntime();
        await runtime.Database.OpenConnectionAsync();
        var hoy = DiaDeNegocio.Hoy();

        var solicitud = await Record.ExceptionAsync(() => runtime.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "SolicitudesDerechoInteresado"
                ("Id", "TenantId", "Tipo", "Canal", "FechaRecepcion", "NombreInteresado", "Estado",
                 "PlazoOrdinario", "PlazoVencimiento", "RegistradaPorUsuarioId", "RegistradaEnUtc", "Version")
            VALUES ({Guid.NewGuid()}, {_tenantAjeno}, 'Acceso', 'Otro', {hoy}, 'Paula', 'Recibida',
                    {hoy.AddMonths(1)}, {hoy.AddMonths(1)}, {_usuarioId}, now(), {Guid.NewGuid()})
            """));
        var concesion = await Record.ExceptionAsync(() => runtime.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ConcesionesCapacidadTenant"
                ("Id", "TenantId", "UsuarioId", "Capacidad", "ConcedidaPorUsuarioId", "ConcedidaEnUtc", "Version")
            VALUES ({Guid.NewGuid()}, {_tenantAjeno}, {Guid.NewGuid()}, 'DerechosDelInteresado', {_usuarioId}, now(), {Guid.NewGuid()})
            """));

        solicitud.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("42501",
            "el WITH CHECK de aislamiento_tenant rechaza una solicitud de otro Tenant");
        concesion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("42501",
            "el WITH CHECK de aislamiento_tenant rechaza una concesión de otro Tenant");
    }

    [Theory]
    [InlineData("cae_app_soporte", "SolicitudesDerechoInteresado")]
    [InlineData("cae_app_soporte", "ConcesionesCapacidadTenant")]
    [InlineData("cae_app_aprovisionamiento", "SolicitudesDerechoInteresado")]
    [InlineData("cae_app_aprovisionamiento", "ConcesionesCapacidadTenant")]
    public async Task Soporte_y_aprovisionamiento_no_tienen_ningun_privilegio(string rol, string tabla)
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
    [InlineData("""UPDATE "SolicitudesDerechoInteresado" SET "NombreInteresado" = 'otro'""")]
    [InlineData("""UPDATE "SolicitudesDerechoInteresado" SET "RegistradaPorUsuarioId" = gen_random_uuid()""")]
    [InlineData("""UPDATE "SolicitudesDerechoInteresado" SET "FechaRecepcion" = DATE '2026-01-01'""")]
    [InlineData("""DELETE FROM "SolicitudesDerechoInteresado" """)]
    [InlineData("""UPDATE "ConcesionesCapacidadTenant" SET "UsuarioId" = gen_random_uuid()""")]
    [InlineData("""UPDATE "ConcesionesCapacidadTenant" SET "ConcedidaPorUsuarioId" = gen_random_uuid()""")]
    [InlineData("""DELETE FROM "ConcesionesCapacidadTenant" """)]
    public async Task La_aplicacion_no_borra_ni_reescribe_lo_registrado(string orden)
    {
        await using var runtime = CrearRuntime();

        var excepcion = await Record.ExceptionAsync(() => runtime.Database.ExecuteSqlRawAsync(orden));

        excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("42501");
    }

    private SolicitudDerechoInteresado Solicitud(Guid? trabajadorId = null) => SolicitudDerechoInteresado.Registrar(
        TipoDerechoInteresado.Acceso, CanalEntradaSolicitudDerecho.CorreoElectronico, DiaDeNegocio.Hoy(),
        "Paula Campos", trabajadorId, "Pide copia de sus datos.", _usuarioId, DiaDeNegocio.Hoy(), DateTime.UtcNow);

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

    private CaeManagerDbContext CrearRuntime() => CrearRuntime(auditoria: null);

    private CaeManagerDbContext CrearRuntime(AuditoriaInterceptor? auditoria)
    {
        IInterceptor[] deAuditoria = auditoria is null ? [] : [auditoria];
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(deAuditoria)
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantDeLaPeticion),
                new ConcurrenciaOptimistaInterceptor(),
                new TenantRlsConnectionInterceptor(_tenantDeLaPeticion, new SinClienteActivo(), Usuario(), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _tenantDeLaPeticion);
    }

    private sealed class ActorAuditoriaFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
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
