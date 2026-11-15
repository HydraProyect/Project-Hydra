using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// Issue #2124, <b>como <c>cae_app_runtime</c></b>: las lecturas de la recuperación del hueco de correo, con el Tenant
/// propietario en <see cref="AmbitoTenantExplicito"/> como en <c>RecuperacionHuecoCorreoHostedService</c>, solo ven las
/// conexiones y los eventos de ese Tenant; y la concurrencia optimista de la conexión impide guardar una limpieza de la
/// marca sobre una fila que otra operación anuló después de leerla. La siembra va como propietario de la base.
/// </summary>
public class RecuperacionHuecoCorreoBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly EphemeralDataProtectionProvider _proteccion = new();
    private readonly DateTime _marca = DateTime.UtcNow.AddHours(-3);

    private CaeManagerDbContext _propietario = null!;
    private Guid _tenantPropio;
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

        var tenantPropio = new Tenant("Tenant propietario del ciclo");
        var tenantAjeno = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantPropio, tenantAjeno);
        await _propietario.SaveChangesAsync();
        _tenantPropio = tenantPropio.Id;
        _tenantAjeno = tenantAjeno.Id;

        _buzonPropio = await SembrarBuzonConMarcaAsync(_tenantPropio, "cae@propio.test");
        _buzonAjeno = await SembrarBuzonConMarcaAsync(_tenantAjeno, "cae@ajeno.test");
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task El_ciclo_de_un_Tenant_solo_ve_sus_conexiones_con_hueco()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantPropio);
        await using var runtime = CrearRuntime();
        var repositorio = new ConexionIntegracionRepository(runtime);

        (await repositorio.ObtenerIdsConHuecoPendienteAsync()).Should().Equal(_buzonPropio);
        (await repositorio.ObtenerActualPorIdAsync(_buzonAjeno)).Should().BeNull("la conexión de otro Tenant no se lee por Id");
    }

    [Fact]
    public async Task Los_eventos_de_recuperacion_de_otro_Tenant_no_cuentan_y_solo_cuentan_los_de_recuperacion_sin_ingerir()
    {
        await SembrarEventoAsync(_tenantAjeno, EventoWebhook.CrearRecuperacion(_buzonAjeno, "{\"value\":[]}", _marca));
        await SembrarEventoAsync(_tenantPropio, new EventoWebhook(_buzonPropio, "{\"value\":[]}"));

        using var ambito = AmbitoTenantExplicito.Establecer(_tenantPropio);
        await using (var runtime = CrearRuntime())
        {
            var eventos = new EventoWebhookRepository(runtime);
            (await eventos.ExistenRecuperacionesSinIngerirAsync(_buzonPropio)).Should().BeFalse("una notificación normal no es de recuperación");
            (await eventos.ExistenRecuperacionesSinIngerirAsync(_buzonAjeno)).Should().BeFalse("el evento de otro Tenant no se ve");
        }

        var recuperacion = EventoWebhook.CrearRecuperacion(_buzonPropio, "{\"value\":[]}", _marca);
        await SembrarEventoAsync(_tenantPropio, recuperacion);

        await using (var runtime = CrearRuntime())
            (await new EventoWebhookRepository(runtime).ExistenRecuperacionesSinIngerirAsync(_buzonPropio)).Should().BeTrue();

        await using (var runtime = CrearRuntime())
        {
            var evento = await runtime.EventosWebhook.SingleAsync(e => e.Id == recuperacion.Id);
            evento.CubreDesdeUtc.Should().BeCloseTo(_marca, TimeSpan.FromMilliseconds(1));
            evento.MarcarEnProceso();
            evento.MarcarProcesado();
            await runtime.SaveChangesAsync();
        }

        await using (var runtime = CrearRuntime())
            (await new EventoWebhookRepository(runtime).ExistenRecuperacionesSinIngerirAsync(_buzonPropio)).Should().BeFalse();
    }

    /// <summary>
    /// Ronda 5 de la revisión: lo que la recuperación salta al encolar. Solo los payloads de recuperación Pendientes o
    /// Procesando de la conexión, y solo los Ids ingeridos en conversaciones de la conexión, ambos dentro del Tenant.
    /// </summary>
    [Fact]
    public async Task Lo_que_la_recuperacion_salta_al_encolar_solo_sale_de_la_conexion_y_del_Tenant()
    {
        var pendiente = EventoWebhook.CrearRecuperacion(_buzonPropio, "{\"pendiente\":1}", _marca);
        var procesando = EventoWebhook.CrearRecuperacion(_buzonPropio, "{\"procesando\":1}", _marca);
        procesando.MarcarEnProceso();
        var procesado = EventoWebhook.CrearRecuperacion(_buzonPropio, "{\"procesado\":1}", _marca);
        procesado.MarcarEnProceso();
        procesado.MarcarProcesado();
        await SembrarEventoAsync(_tenantPropio, pendiente);
        await SembrarEventoAsync(_tenantPropio, procesando);
        await SembrarEventoAsync(_tenantPropio, procesado);
        await SembrarEventoAsync(_tenantPropio, new EventoWebhook(_buzonPropio, "{\"normal\":1}"));
        await SembrarEventoAsync(_tenantAjeno, EventoWebhook.CrearRecuperacion(_buzonAjeno, "{\"ajeno\":1}", _marca));
        await SembrarMensajeAsync(_tenantPropio, _buzonPropio, "ingerido-propio");
        await SembrarMensajeAsync(_tenantAjeno, _buzonAjeno, "ingerido-ajeno");

        using var ambito = AmbitoTenantExplicito.Establecer(_tenantPropio);
        await using var runtime = CrearRuntime();

        (await new EventoWebhookRepository(runtime).ObtenerPayloadsDeRecuperacionesEnCursoAsync(_buzonPropio))
            .Should().BeEquivalentTo(["{\"pendiente\":1}", "{\"procesando\":1}"]);
        (await new EventoWebhookRepository(runtime).ObtenerPayloadsDeRecuperacionesEnCursoAsync(_buzonAjeno))
            .Should().BeEmpty("los eventos de otro Tenant no se ven");

        var conversaciones = new ConversacionRepository(runtime);
        (await conversaciones.ObtenerMensajesExternosExistentesAsync(_buzonPropio, ["ingerido-propio", "ingerido-ajeno", "nuevo"]))
            .Should().BeEquivalentTo(["ingerido-propio"]);
        (await conversaciones.ObtenerMensajesExternosExistentesAsync(_buzonAjeno, ["ingerido-ajeno"]))
            .Should().BeEmpty("los mensajes de otro Tenant no se ven");
    }

    [Fact]
    public async Task Una_perdida_guardada_despues_de_leer_impide_guardar_la_limpieza()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantPropio);
        await using (var runtime = CrearRuntime())
        {
            var conexion = await runtime.ConexionesIntegracion.SingleAsync(c => c.Id == _buzonPropio);
            conexion.IniciarRecuperacionHueco(DateTime.UtcNow);
            conexion.AvanzarRecuperacionHueco(null, hayMas: false);
            await runtime.SaveChangesAsync();
        }

        await using var limpieza = CrearRuntime();
        var leida = await new ConexionIntegracionRepository(limpieza).ObtenerActualPorIdAsync(_buzonPropio);
        leida!.RecuperacionHuecoListadoCompleto.Should().BeTrue("control: el estado de la recuperación persiste");

        // Otra operación registra una pérdida que no mueve la marca: solo anula la recuperación, y eso cambia la versión.
        await using (var otra = CrearRuntime())
        {
            var conexion = await otra.ConexionesIntegracion.SingleAsync(c => c.Id == _buzonPropio);
            conexion.RegistrarNotificacionDescartada(DateTime.UtcNow.AddHours(2)).Should().BeTrue();
            await otra.SaveChangesAsync();
        }

        leida.CompletarRecuperacionHueco().Should().BeTrue();
        var guardar = () => limpieza.SaveChangesAsync();

        await guardar.Should().ThrowAsync<DbUpdateConcurrencyException>();
        var enBase = await _propietario.ConexionesIntegracion.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == _buzonPropio);
        enBase.HuecoPendienteDesdeUtc.Should().NotBeNull("la marca se queda");
        enBase.RecuperacionHuecoEnCurso.Should().BeFalse();
    }

    private async Task<Guid> SembrarBuzonConMarcaAsync(Guid tenantId, string email)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var buzon = new ConexionIntegracion(email, "Buzón CAE");
        buzon.FijarIdentidadBuzon("inbox-" + email);
        buzon.MarcarIdentidadVerificada(DateTime.UtcNow);
        buzon.RegistrarNotificacionDescartada(_marca + ConexionIntegracion.RetrasoMaximoNotificacion);
        _propietario.ConexionesIntegracion.Add(buzon);
        await _propietario.SaveChangesAsync();
        return buzon.Id;
    }

    private async Task SembrarEventoAsync(Guid tenantId, EventoWebhook evento)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        _propietario.EventosWebhook.Add(evento);
        await _propietario.SaveChangesAsync();
    }

    private async Task SembrarMensajeAsync(Guid tenantId, Guid conexionId, string mensajeExternoId)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var conversacion = new Conversacion("Duda sobre CAE");
        conversacion.AsociarConexion(conexionId, "hilo-" + mensajeExternoId);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "<p>ya estaba</p>",
            mensajeExternoId: mensajeExternoId);
        _propietario.Conversaciones.Add(conversacion);
        await _propietario.SaveChangesAsync();
    }

    private CaeManagerDbContext CrearRuntime()
    {
        var tenant = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new AuditoriaInterceptor(new ActorFijo(ActorAuditoria.SinResolver)),
                new TenantSelladoInterceptor(tenant),
                new TenantRlsConnectionInterceptor(tenant, new SinClienteActivo(), new CurrentUserServiceFalso(), BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor())
            .Options;
        return new CaeManagerDbContext(opciones, _proteccion, tenant);
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
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
}
