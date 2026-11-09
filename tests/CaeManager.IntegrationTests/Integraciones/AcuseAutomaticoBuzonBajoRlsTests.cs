using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.FijarAcuseAutomaticoBuzon;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// El acuse automático <b>como <c>cae_app_runtime</c></b>: dos Tenants propietarios con un buzón cada uno. Cambiar la
/// configuración va con la sesión del Administrador; el envío, como en <c>IngestaWebhookHostedService</c>, sin usuario, con
/// el Tenant del buzón en <see cref="AmbitoTenantExplicito"/> y el actor de sistema. Graph es un doble: esta prueba no envía
/// ningún correo. La siembra va como propietario de la base, porque no es lo que se mide.
/// </summary>
public class AcuseAutomaticoBuzonBajoRlsTests : IAsyncLifetime
{
    private const string Remitente = "persona@ejemplo.test";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly EphemeralDataProtectionProvider _proteccion = new();
    private readonly Guid _administrador = Guid.NewGuid();
    private readonly GraphFalso _graph = new();

    private CaeManagerDbContext _propietario = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _buzonPropio;
    private Guid _buzonAjeno;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, _proteccion, tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantSesion = new Tenant("Tenant propietario de la sesión");
        var tenantAjeno = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantSesion, tenantAjeno);
        await _propietario.SaveChangesAsync();
        _tenantSesion = tenantSesion.Id;
        _tenantAjeno = tenantAjeno.Id;
        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);

        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var buzon = new ConexionIntegracion("cae@ejemplo.test", "Buzón CAE");
            // La ingesta y el acuse solo operan con la identidad del buzón verificada.
            buzon.FijarIdentidadBuzon("inbox-propio");
            buzon.MarcarIdentidadVerificada(DateTime.UtcNow);
            _propietario.ConexionesIntegracion.Add(buzon);
            _propietario.CredencialesIntegracion.Add(new CredencialIntegracion(buzon.Id, "refresh"));
            await _propietario.SaveChangesAsync();
            _buzonPropio = buzon.Id;
        }

        using (AmbitoTenantExplicito.Establecer(tenantAjeno.Id))
        {
            var buzon = new ConexionIntegracion("cae@ajeno.test", "Buzón ajeno");
            // La ingesta y el acuse solo operan con la identidad del buzón verificada.
            buzon.FijarIdentidadBuzon("inbox-ajeno");
            buzon.MarcarIdentidadVerificada(DateTime.UtcNow);
            _propietario.ConexionesIntegracion.Add(buzon);
            await _propietario.SaveChangesAsync();
            _buzonAjeno = buzon.Id;
        }
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task Activar_el_acuse_persiste_y_deja_la_fila_de_auditoria_del_Administrador()
    {
        var actor = ActorAdministrador();
        await using var runtime = CrearRuntime(_tenantDeLaPeticion, actor, ActorAuditoria.Normal(_administrador));

        var resultado = await Handler(runtime).Handle(new FijarAcuseAutomaticoBuzonCommand(_buzonPropio, true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : null);
        (await LeerAcuseActivoAsync(_buzonPropio)).Should().BeTrue();
        var fila = await _propietario.RegistrosAuditoria.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.EntidadTipo == nameof(ConexionIntegracion) && r.EntidadId == _buzonPropio && r.Accion == "Modificado");
        fila.UsuarioId.Should().Be(_administrador);
        fila.DatosDespues.Should().Contain(nameof(ConexionIntegracion.AcuseAutomaticoActivo));
    }

    [Fact]
    public async Task Activar_por_Id_el_acuse_de_un_buzon_de_otro_Tenant_responde_no_encontrada_y_no_lo_cambia()
    {
        await using var runtime = CrearRuntime(_tenantDeLaPeticion, ActorAdministrador(), ActorAuditoria.Normal(_administrador));

        var resultado = await Handler(runtime).Handle(new FijarAcuseAutomaticoBuzonCommand(_buzonAjeno, true), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        (await LeerAcuseActivoAsync(_buzonAjeno)).Should().BeFalse();
    }

    [Fact]
    public async Task El_acuse_sale_sin_usuario_en_el_Tenant_del_buzon_queda_registrado_como_Sistema_y_corta_el_siguiente()
    {
        var primera = await SembrarConversacionAsync("hilo-1", Remitente);
        var segunda = await SembrarConversacionAsync("hilo-2", "PERSONA@ejemplo.test");

        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantSesion);
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();
        await EnviarAcuseAsync(primera, Remitente);
        await EnviarAcuseAsync(segunda, "PERSONA@ejemplo.test");

        _graph.Respuestas.Should().ContainSingle().Which.Should().Be("msg-hilo-1");
        var acuse = await _propietario.Mensajes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.ConversacionId == primera && m.Direccion == DireccionMensaje.Saliente);
        acuse.EsAutomatico.Should().BeTrue();
        var fila = await _propietario.RegistrosAuditoria.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.EntidadTipo == nameof(Mensaje) && r.Accion == RegistroAuditoria.AccionExportacion);
        fila.TenantId.Should().Be(_tenantSesion);
        fila.TipoActor.Should().Be(TipoActorAuditoria.Sistema);
        fila.DatosDespues.Should().Contain("AcuseAutomatico").And.NotContain(Remitente);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-25, true)]
    public async Task El_tope_por_remitente_caduca_y_no_cuenta_otro_remitente(int horasDesdeLaReserva, bool esperado)
    {
        var ahora = DateTime.UtcNow;
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantSesion);
        await using var runtime = CrearRuntime(new TenantActualPorAmbito(), new CurrentUserServiceFalso(), ActorAuditoria.SinResolver);
        var repositorio = new ConversacionRepository(runtime);

        (await ReservarAsync(repositorio, Remitente, ahora.AddHours(horasDesdeLaReserva))).Should().BeTrue();

        (await ReservarAsync(repositorio, "PERSONA@ejemplo.test", ahora)).Should().Be(esperado);
        (await ReservarAsync(repositorio, "otra@ejemplo.test", ahora)).Should().BeTrue();
    }

    [Fact]
    public async Task De_varias_reservas_simultaneas_del_mismo_remitente_solo_una_gana()
    {
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantSesion);
        var ahora = DateTime.UtcNow;

        var resultados = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var runtime = CrearRuntime(new TenantActualPorAmbito(), new CurrentUserServiceFalso(), ActorAuditoria.SinResolver);
            return await ReservarAsync(new ConversacionRepository(runtime), Remitente, ahora);
        }));

        resultados.Count(r => r).Should().Be(1);
    }

    [Fact]
    public async Task La_reserva_tiene_RLS_forzada_guarda_una_huella_y_el_runtime_no_la_borra()
    {
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            await using var runtime = CrearRuntime(new TenantActualPorAmbito(), new CurrentUserServiceFalso(), ActorAuditoria.SinResolver);
            (await ReservarAsync(new ConversacionRepository(runtime), Remitente, DateTime.UtcNow)).Should().BeTrue();
        }

        await using var propietario = new NpgsqlConnection(_cadenaConexion);
        await propietario.OpenAsync();
        (await EscalarAsync(propietario, """
            SELECT c.relrowsecurity AND c.relforcerowsecurity
                   AND EXISTS (SELECT 1 FROM pg_policies p
                               WHERE p.tablename = c.relname AND p.policyname = 'aislamiento_tenant' AND p.with_check IS NOT NULL)
            FROM pg_class c WHERE c.oid = 'public."ReservasAcuseBuzon"'::regclass;
            """)).Should().Be(true);
        (await EscalarAsync(propietario, """SELECT count(*) FROM "ReservasAcuseBuzon" WHERE "HuellaRemitente" LIKE '%@%';""")).Should().Be(0L);

        foreach (var (rol, sentencia) in new[]
        {
            ("cae_app_runtime", """DELETE FROM "ReservasAcuseBuzon";"""),
            ("cae_app_soporte", """SELECT count(*) FROM "ReservasAcuseBuzon";"""),
            ("cae_app_aprovisionamiento", """SELECT count(*) FROM "ReservasAcuseBuzon";"""),
        })
        {
            await EscalarAsync(propietario, $"SET ROLE {rol};");
            var excepcion = await Record.ExceptionAsync(() => EscalarAsync(propietario, sentencia));
            await EscalarAsync(propietario, "RESET ROLE;");

            excepcion.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, rol);
        }
    }

    [Fact]
    public async Task Un_Tenant_no_reserva_en_nombre_de_otro_ni_sobre_el_buzon_de_otro()
    {
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantSesion);
        await using var runtime = CrearRuntime(new TenantActualPorAmbito(), new CurrentUserServiceFalso(), ActorAuditoria.SinResolver);
        var repositorio = new ConversacionRepository(runtime);
        var intervalo = AcuseAutomaticoBuzonService.IntervaloMinimoPorRemitente;

        var otroTenant = await Record.ExceptionAsync(() =>
            repositorio.ReservarAcuseAsync(_tenantAjeno, _buzonAjeno, Remitente, DateTime.UtcNow, intervalo));
        var buzonAjeno = await Record.ExceptionAsync(() =>
            repositorio.ReservarAcuseAsync(_tenantSesion, _buzonAjeno, Remitente, DateTime.UtcNow, intervalo));

        otroTenant.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "WITH CHECK de la política");
        buzonAjeno.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation, "la clave compuesta con el Tenant");
    }

    private Task<bool> ReservarAsync(ConversacionRepository repositorio, string remitente, DateTime ahoraUtc) =>
        repositorio.ReservarAcuseAsync(
            _tenantSesion, _buzonPropio, remitente, ahoraUtc, AcuseAutomaticoBuzonService.IntervaloMinimoPorRemitente);

    private static async Task<object?> EscalarAsync(NpgsqlConnection conexion, string sentencia)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sentencia;
        return await comando.ExecuteScalarAsync();
    }

    private async Task<Guid> SembrarConversacionAsync(string hilo, string remitente)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantSesion);
        var conversacion = new Conversacion("Asunto");
        conversacion.AsociarConexion(_buzonPropio, hilo);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, remitente, "Texto");
        _propietario.Conversaciones.Add(conversacion);
        await _propietario.SaveChangesAsync();
        return conversacion.Id;
    }

    /// <summary>Un ámbito de contexto por acuse, como el ámbito por evento del servicio hospedado.</summary>
    private async Task EnviarAcuseAsync(Guid conversacionId, string destinatario)
    {
        var tenant = new TenantActualPorAmbito();
        var usuario = new CurrentUserServiceFalso();
        await using var runtime = CrearRuntime(tenant, usuario, ActorAuditoria.SinResolver);
        var repositorio = new ConversacionRepository(runtime);
        var conexion = await runtime.ConexionesIntegracion.SingleAsync(c => c.Id == _buzonPropio);
        var conversacion = await repositorio.ObtenerPorIdAsync(conversacionId);
        var pendiente = new AcusePendiente(conexion, conversacion!, $"msg-{conversacion!.HiloExternoId}", destinatario);
        var registro = new RegistroExportacionService(
            new ActorFijo(ActorAuditoria.SinResolver),
            new RegistroAccesoDatoSensibleRepository(runtime, NullLogger<RegistroAccesoDatoSensibleRepository>.Instance));
        var servicio = new AcuseAutomaticoBuzonService(
            repositorio, _graph, new AccesoGraphService(new CredencialIntegracionRepository(runtime), _graph), registro, runtime,
            new DescarteSinDescartar(), NullLogger<AcuseAutomaticoBuzonService>.Instance);

        await servicio.EnviarAsync([pendiente], CancellationToken.None);
    }

    private CurrentUserServiceFalso ActorAdministrador() =>
        new(_administrador, Roles.Administrador, tenantOrigenId: _tenantSesion);

    private FijarAcuseAutomaticoBuzonCommandHandler Handler(CaeManagerDbContext runtime)
    {
        var actor = ActorAdministrador();
        return new FijarAcuseAutomaticoBuzonCommandHandler(
            new ConexionIntegracionRepository(runtime),
            new AlcanceDatosService(runtime, actor, _tenantDeLaPeticion, new SesionPrivilegiadaAusente()),
            actor, runtime);
    }

    private Task<bool> LeerAcuseActivoAsync(Guid conexionId) =>
        _propietario.ConexionesIntegracion.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == conexionId).Select(c => c.AcuseAutomaticoActivo).SingleAsync();

    private CaeManagerDbContext CrearRuntime(ITenantActual tenant, ICurrentUserService usuario, ActorAuditoria actor)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new AuditoriaInterceptor(new ActorFijo(actor)),
                new TenantSelladoInterceptor(tenant),
                new TenantRlsConnectionInterceptor(tenant, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor())
            .Options;
        return new CaeManagerDbContext(opciones, _proteccion, tenant);
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class DescarteSinDescartar : IDescarteCambiosPendientes
    {
        public void DescartarCambiosPendientes() =>
            throw new InvalidOperationException("Un acuse de esta prueba no debe fallar.");
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

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    /// <summary>Solo responde a lo que usa el acuse; cualquier otra llamada a Graph es un fallo de la prueba.</summary>
    private sealed class GraphFalso : IMicrosoft365GraphClient
    {
        public List<string> Respuestas { get; } = [];

        public Task<Result<TokenAplicacionGraphDto>> ObtenerTokenAplicacionAsync(string directorioId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<TokensGraphDto>> RefrescarTokensAsync(string refreshToken, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Exito(new TokensGraphDto("access", "refresh", DateTime.UtcNow.AddHours(1))));

        public Task<Result> EnviarRespuestaAsync(
            string accessToken, BuzonGraph buzon, string mensajeExternoIdOrigen, string cuerpoHtml,
            IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken)
        {
            Respuestas.Add(mensajeExternoIdOrigen);
            return Task.FromResult(Result.Exito());
        }

        public string ConstruirUrlAutorizacion(string redirectUri, string state) => throw new NotSupportedException();

        public Task<Result<TokensGraphDto>> IntercambiarCodigoPorTokensAsync(string code, string redirectUri, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<string>> ObtenerBuzonEmailAsync(string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<string>> ObtenerIdentidadBuzonAsync(string accessToken, BuzonGraph buzon, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<MensajeEnviadoGraphDto>> EnviarNuevoMensajeAsync(
            string accessToken, BuzonGraph buzon, IReadOnlyList<string> destinatarios, string asunto, string cuerpoHtml,
            IReadOnlyList<AdjuntoParaEnviarDto>? adjuntos, CancellationToken cancellationToken, IReadOnlyList<string>? copia = null) =>
            throw new NotSupportedException();

        public Task<Result<MensajeGraphDto>> ObtenerMensajeAsync(
            string accessToken, BuzonGraph buzon, string mensajeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<byte[]>> ObtenerContenidoAdjuntoAsync(
            string accessToken, BuzonGraph buzon, string mensajeId, string adjuntoExternoId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<IReadOnlyList<CarpetaGraphDto>>> ListarCarpetasAsync(string accessToken, BuzonGraph buzon, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<IReadOnlyList<MensajeResumenGraphDto>>> ListarMensajesAsync(
            string accessToken, BuzonGraph buzon, string carpetaExternoId, int top, int skip, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<SuscripcionGraphDto>> CrearSuscripcionAsync(
            string accessToken, BuzonGraph buzon, string notificationUrl, string clientState, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<SuscripcionGraphDto>> RenovarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result> EliminarSuscripcionAsync(string accessToken, string graphSubscriptionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IReadOnlyList<string> ExtraerMensajeIdsDeNotificacion(string payloadJson) => throw new NotSupportedException();
        public string ConstruirNotificacionDeRecuperacion(IReadOnlyList<string> mensajeIds) => throw new NotSupportedException();

        public string? ExtraerClientStateDeNotificacion(string payloadJson) => throw new NotSupportedException();

        public string? ExtraerSubscriptionIdDeNotificacion(string payloadJson) => throw new NotSupportedException();
    }
}
