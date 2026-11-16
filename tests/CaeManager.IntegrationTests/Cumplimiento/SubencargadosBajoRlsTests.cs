using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento.Subencargados;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
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
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Cumplimiento;

/// <summary>
/// #2010, D-7, <b>como <c>cae_app_runtime</c></b> con los interceptores de sellado y de sesión RLS reales:
/// el catálogo de subencargados lo escribe solo un Actor de Plataforma TALVEG con AdminPlataforma global; el
/// aviso, también, en el Tenant propietario del contexto y a su propio nombre; la objeción, solo el
/// Administrador de ese Tenant; avisos y objeciones no se reescriben ni se borran, y cada Tenant ve solo
/// los suyos (también la exclusión que apaga su IA, que solo cuenta con el subencargado activo). Fija además los
/// predicados reales del repositorio (solo un aviso Enviado prueba el aviso, el límite de fecha es inclusivo,
/// Tenants propietarios = todos los que no son de Plataforma, en cualquier estado) y el camino completo Avisar → Activar con los handlers reales.
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
        // Avisado hace más del plazo, para poder activarlo en los tests que miden el efecto de la objeción.
        var subencargado = Nuevo(_actorGlobal);
        subencargado.MarcarAvisado(ahora - Subencargado.PlazoMinimoAviso - TimeSpan.FromDays(1));
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
    public async Task La_exclusion_por_objecion_solo_apaga_el_conmutador_del_Tenant_que_objeto_y_solo_con_el_subencargado_activo()
    {
        await using var limpio = CrearPropietarioLimpio();

        async Task<bool> ExcluyeAlAjenoAsync()
        {
            limpio.ChangeTracker.Clear();
            using var _ = AmbitoTenantExplicito.Establecer(_tenantAjeno);
            return await new SubencargadoRepository(limpio).ExcluyeConmutadorAsync(_tenantAjeno, ConmutadorExclusionSubencargado.Ia);
        }

        (await ExcluyeAlAjenoAsync()).Should().BeFalse("avisado y sin activar, el Tenant que objetó todavía no lo usa: nada que apagar");

        await CambiarEstadoComoPropietarioAsync(s => s.Activar(DateTime.UtcNow, 0, _actorGlobal));
        (await ExcluyeAlAjenoAsync()).Should().BeTrue("activo, el Tenant que objetó deja de usarlo");

        await using (var runtime = CrearRuntime(_usuarioDelTenant, Roles.Administrador))
        {
            (await new SubencargadoRepository(runtime).ExcluyeConmutadorAsync(_tenantSesion, ConmutadorExclusionSubencargado.Ia)).Should().BeFalse(
                "la objeción del Tenant ajeno no excluye al de la sesión");
            (await new SubencargadoRepository(runtime).ExcluyeConmutadorAsync(_tenantAjeno, ConmutadorExclusionSubencargado.Ia)).Should().BeFalse(
                "desde el Tenant de la sesión no se ve la objeción del ajeno aunque se pida por su Id (aquí actúan el filtro de " +
                "Tenant y la RLS; que la RLS sola la oculta lo fija el test de lectura con IgnoreQueryFilters)");
        }

        await CambiarEstadoComoPropietarioAsync(s => s.Retirar("Se sustituye", DateTime.UtcNow));
        (await ExcluyeAlAjenoAsync()).Should().BeFalse("retirado, ya no lo usa nadie");
    }

    [Fact]
    public async Task Solo_un_aviso_Enviado_registrado_como_muy_tarde_en_el_limite_cuenta_como_aviso_probado()
    {
        var limite = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        await using var runtime = CrearRuntime(_actorGlobal, Roles.Administrador);
        var repositorio = new SubencargadoRepository(runtime);

        repositorio.AgregarAviso(new AvisoCambioSubencargado(
            _subencargadoId, Guid.NewGuid(), Guid.NewGuid(), FaseAvisoCambioSubencargado.Preparado, limite.AddDays(-10), _actorGlobal));
        repositorio.AgregarAviso(new AvisoCambioSubencargado(
            _subencargadoId, Guid.NewGuid(), Guid.NewGuid(), FaseAvisoCambioSubencargado.NoConfirmado, limite.AddDays(-10), _actorGlobal));
        await runtime.SaveChangesAsync();
        (await repositorio.TieneAvisoProbadoAsync(_subencargadoId, limite)).Should().BeFalse("Preparado y NoConfirmado no prueban el aviso");
        (await repositorio.TieneAvisoProbadoAsync(_subencargadoId, null)).Should().BeFalse();

        repositorio.AgregarAviso(new AvisoCambioSubencargado(
            _subencargadoId, Guid.NewGuid(), Guid.NewGuid(), FaseAvisoCambioSubencargado.Enviado, limite, _actorGlobal));
        await runtime.SaveChangesAsync();

        (await repositorio.TieneAvisoProbadoAsync(_subencargadoId, limite)).Should().BeTrue("registrado justo en el límite, cuenta");
        (await repositorio.TieneAvisoProbadoAsync(_subencargadoId, limite.AddTicks(-10))).Should().BeFalse(
            "un microsegundo antes del registro, todavía no");
        (await repositorio.TieneAvisoProbadoAsync(Guid.NewGuid(), null)).Should().BeFalse("el aviso es de ese subencargado, no de otro");
    }

    [Fact]
    public async Task Los_Tenants_propietarios_son_todos_los_que_no_son_el_de_Plataforma_suspendidos_incluidos()
    {
        var suspendido = new Tenant("Tenant propietario suspendido");
        suspendido.Suspender();
        var plataforma = new Tenant("Tenant de Plataforma");
        plataforma.MarcarComoPlataforma();
        _propietario.Tenants.AddRange(suspendido, plataforma);
        await _propietario.SaveChangesAsync();

        await using var runtime = CrearRuntime(_actorGlobal, Roles.Administrador);
        var propietarios = await new SubencargadoRepository(runtime).TenantsPropietariosAsync();

        propietarios.Should().Contain([_tenantSesion, _tenantAjeno]);
        propietarios.Should().Contain(suspendido.Id,
            "un Tenant suspendido se puede reactivar y su IA usaría el subencargado sin haber tenido sus 30 días de aviso");
        propietarios.Should().NotContain(plataforma.Id);
        await using var limpio = CrearPropietarioLimpio();
        (await limpio.Tenants.AsNoTracking().CountAsync(t => t.Id == suspendido.Id || t.Id == plataforma.Id))
            .Should().Be(2, "control del instrumento: los dos existen");
    }

    [Fact]
    public async Task Avisar_y_activar_con_los_handlers_reales_bajo_RLS_deja_el_subencargado_activo()
    {
        // Cada Tenant propietario de la base (los del fixture y los que siembren las migraciones) con un
        // Administrador con correo: si alguno se quedara sin aviso, la activación lo detectaría y fallaría.
        var activos = await _propietario.Tenants.AsNoTracking()
            .Where(t => !t.EsPlataforma).Select(t => t.Id).ToListAsync();
        foreach (var tenantId in activos)
            await AdministradorAsync(tenantId);

        var propuesto = Nuevo(_actorGlobal);
        _propietario.Subencargados.Add(propuesto);
        await _propietario.SaveChangesAsync();

        var hace31Dias = DateTime.UtcNow.AddDays(-31);
        var correo = new CorreoEspia();
        await using (var runtime = CrearRuntime(_actorGlobal, Roles.Administrador))
        {
            var avisar = new AvisarCambioSubencargadoCommandHandler(
                ActorGlobal(), new SinSesionPrivilegiada(), new ConcesionGlobalDeclarada(),
                new SubencargadoRepository(runtime), correo, runtime, new RelojFijo(hace31Dias));

            var resultado = await avisar.Handle(new AvisarCambioSubencargadoCommand(propuesto.Id), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : null);
            resultado.Valor.Should().Be(new ResultadoAvisoSubencargado(activos.Count, 0, 0, 0));
        }

        correo.Destinatarios.Should().HaveCountGreaterThanOrEqualTo(activos.Count);
        await using (var limpio = CrearPropietarioLimpio())
        {
            (await limpio.AvisosCambioSubencargado.IgnoreQueryFilters()
                    .Where(a => a.SubencargadoId == propuesto.Id && a.Fase == FaseAvisoCambioSubencargado.Enviado)
                    .Select(a => a.TenantId).Distinct().ToListAsync())
                .Should().BeEquivalentTo(activos, "cada aviso Enviado queda sellado en el Tenant propietario de su destinatario");
        }

        await using (var runtime = CrearRuntime(_actorGlobal, Roles.Administrador))
        {
            var activar = new ActivarSubencargadoCommandHandler(
                ActorGlobal(), new SinSesionPrivilegiada(), new ConcesionGlobalDeclarada(),
                new SubencargadoRepository(runtime), runtime, new RelojFijo(DateTime.UtcNow));

            var resultado = await activar.Handle(new ActivarSubencargadoCommand(propuesto.Id), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : null);
        }

        await using (var limpio = CrearPropietarioLimpio())
            (await limpio.Subencargados.AsNoTracking().SingleAsync(s => s.Id == propuesto.Id)).Estado.Should().Be(EstadoSubencargado.Activo);
    }

    [Theory]
    [InlineData("acotado")]
    [InlineData("sin concesión")]
    public async Task Sin_AdminPlataforma_global_no_se_cambia_el_catalogo(string quien)
    {
        var usuario = quien == "acotado" ? _actorAcotado : _usuarioDelTenant;
        const string orden = """UPDATE "Subencargados" SET "Estado" = 'Activo' WHERE "Id" = {0}""";

        await using (var runtime = CrearRuntime(usuario, Roles.Administrador))
            (await runtime.Database.ExecuteSqlRawAsync(orden, _subencargadoId)).Should().Be(0,
                "la política UPDATE oculta la fila a quien no tiene AdminPlataforma global");

        await using (var limpio = CrearPropietarioLimpio())
            (await limpio.Subencargados.AsNoTracking().SingleAsync(s => s.Id == _subencargadoId)).Estado.Should().Be(EstadoSubencargado.Avisado);

        await using (var runtime = CrearRuntime(_actorGlobal, Roles.Administrador))
            (await runtime.Database.ExecuteSqlRawAsync(orden, _subencargadoId)).Should().Be(1, "control del instrumento: el global sí lo cambia");
    }

    [Fact]
    public async Task El_Administrador_real_del_Tenant_propietario_registra_su_objecion()
    {
        var administrador = await AdministradorAsync(_tenantSesion);
        var subencargado = await _propietario.Subencargados.AsNoTracking().SingleAsync(s => s.Id == _subencargadoId);

        await using (var runtime = CrearRuntime(administrador, Roles.Administrador))
        {
            var repositorio = new SubencargadoRepository(runtime);
            repositorio.AgregarObjecion(new ObjecionSubencargado(subencargado, administrador, "No queremos este proveedor", DateTime.UtcNow));
            await runtime.SaveChangesAsync();

            (await repositorio.ExisteObjecionAsync(_subencargadoId)).Should().BeTrue();
        }

        await using var limpio = CrearPropietarioLimpio();
        (await limpio.ObjecionesSubencargado.IgnoreQueryFilters().CountAsync(o => o.TenantId == _tenantSesion && o.UsuarioId == administrador))
            .Should().Be(1, "la objeción queda sellada en su Tenant propietario y a su nombre");
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
    [InlineData("cae_app_soporte", "Subencargados", "SELECT")]
    [InlineData("cae_app_soporte", "AvisosCambioSubencargado", "")]
    [InlineData("cae_app_soporte", "ObjecionesSubencargado", "SELECT")]
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

    private async Task CambiarEstadoComoPropietarioAsync(Action<Subencargado> cambio)
    {
        await using var limpio = CrearPropietarioLimpio();
        cambio(await limpio.Subencargados.SingleAsync(s => s.Id == _subencargadoId));
        await limpio.SaveChangesAsync();
    }

    /// <summary>Una cuenta con el rol Administrador y correo en ese Tenant, sembrada como propietario.</summary>
    private async Task<Guid> AdministradorAsync(Guid tenantId)
    {
        await using var limpio = CrearPropietarioLimpio();
        var rolId = await limpio.Roles.Where(r => r.Name == Roles.Administrador).Select(r => r.Id).SingleAsync();
        var id = Guid.NewGuid();
        var correo = $"{id:N}@subencargados.local";
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);
        limpio.Users.Add(new ApplicationUser
        {
            Id = id,
            TenantId = tenantId,
            UserName = id.ToString("N"),
            NormalizedUserName = id.ToString("N").ToUpperInvariant(),
            Email = correo,
            NormalizedEmail = correo.ToUpperInvariant(),
            NombreCompleto = "Administrador",
            EmailConfirmed = true,
            PasswordHash = "hash-que-no-sale",
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
        });
        limpio.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = rolId });
        await limpio.SaveChangesAsync();
        return id;
    }

    private CurrentUserServiceFalso ActorGlobal() =>
        new(_actorGlobal, Roles.Administrador, tenantOrigenId: _tenantSesion, tieneDobleFactorActivo: true);

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

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    /// <summary>La barrera de Application se da por superada: lo que se prueba aquí es que la base la repite.</summary>
    private sealed class ConcesionGlobalDeclarada : IAutorizacionAdminPlataforma
    {
        public Task<bool> PuedeSobreTenantAsync(Guid usuarioId, Guid tenantObjetivoId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> PuedeGlobalmenteAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class RelojFijo(DateTime ahoraUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahoraUtc, TimeSpan.Zero);
    }

    /// <summary>Doble del correo: nunca sale nada; solo apunta a quién iría.</summary>
    private sealed class CorreoEspia : IEmailService
    {
        public List<string> Destinatarios { get; } = [];

        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo,
            string? responderA = null, CancellationToken cancellationToken = default)
        {
            Destinatarios.Add(destinatarioEmail);
            return Task.FromResult(Result.Exito());
        }
    }
}
