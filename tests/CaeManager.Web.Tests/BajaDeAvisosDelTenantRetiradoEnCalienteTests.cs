using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.EstadoMaterializado.Frescura;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Al retirarse en caliente la selección de Tenant beneficiario (<see cref="ClienteActivoSeleccionado.Invalidar"/>), lo que
/// el circuito suscribió con el Tenant anterior se da de baja en ese momento, no al cerrarse el circuito: la suscripción a
/// los mensajes en tiempo real de la bandeja de Comunicaciones (<see cref="SuscripcionMensajesDelTenantActual"/>) y la de
/// la caché del estado materializado (<see cref="CacheEstadoCircuito"/>), con las piezas de producción de la selección y
/// del <see cref="TenantActual"/>. El cambio de B a C y las carreras de la caché están en
/// <c>CacheEstadoCircuitoTrasRetirarLaSeleccionTests</c>.
/// </summary>
public class BajaDeAvisosDelTenantRetiradoEnCalienteTests
{
    private static readonly Guid Gestor = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly Guid Origen = Guid.Parse("eeeeeeee-0000-0000-0000-0000000000a0");
    private static readonly Guid Beneficiario = Guid.Parse("eeeeeeee-0000-0000-0000-0000000000b0");
    private static readonly Guid OtroBeneficiario = Guid.Parse("eeeeeeee-0000-0000-0000-0000000000c0");

    [Fact]
    public void La_seleccion_de_produccion_avisa_de_su_retirada()
    {
        typeof(ClienteActivoSeleccionado).Should().Implement<IAvisoSeleccionRetirada>(
            "sin esto la caché y la bandeja no se enteran y siguen suscritas al Tenant retirado");
    }

    [Fact]
    public void El_Tenant_actual_de_produccion_reenvia_el_aviso_de_la_seleccion()
    {
        var circuito = Circuito.Abrir();
        var avisados = new List<Guid>();
        Action<Guid> oyente = avisados.Add;
        var aviso = (IAvisoSeleccionRetirada)circuito.Tenant;

        aviso.TenantSeleccionadoRetirado += oyente;
        circuito.Seleccion.Invalidar();
        aviso.TenantSeleccionadoRetirado -= oyente;

        avisados.Should().Equal([Beneficiario]);
        circuito.Tenant.TenantId.Should().Be(Origen, "cuando llega el aviso, el Tenant actual ya es el de origen");
    }

    [Fact]
    public void Un_oyente_de_SeleccionRetirada_que_lanza_no_impide_las_bajas()
    {
        var circuito = Circuito.Abrir();
        var avisados = new List<Guid>();
        circuito.Seleccion.SeleccionRetirada += () => throw new InvalidOperationException("oyente roto");
        circuito.Seleccion.TenantSeleccionadoRetirado += avisados.Add;

        try
        {
            circuito.Seleccion.Invalidar();
        }
        catch (InvalidOperationException)
        {
            // Lo que haga con eso quien retira la selección no es de este test.
        }
        avisados.Should().Equal([Beneficiario], "las bajas van antes que el aviso antiguo");
    }

    [Fact]
    public void Un_oyente_de_la_retirada_que_lanza_no_impide_que_los_demas_se_den_de_baja()
    {
        var circuito = Circuito.Abrir();
        var avisados = new List<Guid>();
        circuito.Seleccion.TenantSeleccionadoRetirado += _ => throw new InvalidOperationException("oyente roto");
        circuito.Seleccion.TenantSeleccionadoRetirado += avisados.Add;

        circuito.Seleccion.Invoking(s => s.Invalidar()).Should().NotThrow();
        avisados.Should().Equal([Beneficiario], "el aviso dice qué Tenant se retiró");
    }

    // ── Mensajes en tiempo real (bandeja de Comunicaciones) ────────────────

    [Fact]
    public void La_bandeja_de_Comunicaciones_se_suscribe_por_la_suscripcion_que_sigue_la_retirada()
    {
        var fuente = File.ReadAllText(Path.Combine(RaizDelRepositorio(),
            "src", "CaeManager.Web", "Features", "Comunicaciones", "Pages", "Bandeja.razor.cs"));

        fuente.Should().Contain("SuscripcionMensajesDelTenantActual.Abrir(Notificador, TenantActual, ",
            "con el Tenant actual inyectado, que es el que avisa de la retirada");
        fuente.Should().NotContain("Notificador.Suscribir(",
            "una suscripción directa con el Tenant del arranque seguiría recibiendo los mensajes del Tenant retirado");
    }

    [Fact]
    public async Task Con_la_seleccion_vigente_la_bandeja_recibe_los_mensajes_del_Tenant_beneficiario()
    {
        var circuito = Circuito.Abrir();
        var notificador = new NotificadorMensajesTiempoReal(NullLogger<NotificadorMensajesTiempoReal>.Instance);
        var recibidos = new List<MensajeWhatsAppRecibidoEvent>();

        using var suscripcion = SuscripcionMensajesDelTenantActual.Abrir(notificador, circuito.Tenant, Anotar(recibidos));
        await notificador.PublicarAsync(new MensajeWhatsAppRecibidoEvent(Beneficiario, Guid.NewGuid()));
        await notificador.PublicarAsync(new MensajeWhatsAppRecibidoEvent(Origen, Guid.NewGuid()));

        suscripcion.TenantSuscrito.Should().Be(Beneficiario);
        recibidos.Should().ContainSingle().Which.TenantId.Should().Be(Beneficiario);
    }

    [Fact]
    public async Task Tras_retirar_la_seleccion_la_bandeja_no_recibe_los_mensajes_del_Tenant_anterior_y_pasa_al_de_origen()
    {
        var circuito = Circuito.Abrir();
        var notificador = new NotificadorMensajesTiempoReal(NullLogger<NotificadorMensajesTiempoReal>.Instance);
        var recibidos = new List<MensajeWhatsAppRecibidoEvent>();
        using var suscripcion = SuscripcionMensajesDelTenantActual.Abrir(notificador, circuito.Tenant, Anotar(recibidos));

        circuito.Seleccion.Invalidar();
        await notificador.PublicarAsync(new MensajeWhatsAppRecibidoEvent(Beneficiario, Guid.NewGuid()));
        await notificador.PublicarAsync(new MensajeWhatsAppRecibidoEvent(Origen, Guid.NewGuid()));

        suscripcion.TenantSuscrito.Should().Be(Origen);
        recibidos.Should().ContainSingle().Which.TenantId.Should().Be(Origen, "lo de B ya no llega tras la retirada");
    }

    [Fact]
    public async Task Un_mensaje_del_Tenant_anterior_en_vuelo_no_llega_y_nunca_hay_dos_suscripciones()
    {
        var seleccion = new SeleccionQueCambia();
        var notificador = new NotificadorQueGuardaLasSuscripciones();
        var recibidos = new List<MensajeWhatsAppRecibidoEvent>();
        using var suscripcion = SuscripcionMensajesDelTenantActual.Abrir(notificador, seleccion, Anotar(recibidos));
        var callbackDeB = notificador.Callbacks[^1];

        seleccion.CambiarA(OtroBeneficiario);
        // El notificador recorre una instantánea: la callback de B puede llegar aunque ya se haya soltado.
        await callbackDeB(new MensajeWhatsAppRecibidoEvent(Beneficiario, Guid.NewGuid()));
        await notificador.Callbacks[^1](new MensajeWhatsAppRecibidoEvent(OtroBeneficiario, Guid.NewGuid()));

        recibidos.Should().ContainSingle().Which.TenantId.Should().Be(OtroBeneficiario, "al pasar de B a C llegan los de C y no los de B");
        notificador.Activas.Should().Be(1);

        seleccion.CambiarA(OtroBeneficiario);
        notificador.Activas.Should().Be(1, "un aviso de retirada que no cambia el Tenant no duplica la suscripción");
        notificador.Suscripciones.Should().Be(2);

        suscripcion.Dispose();
        notificador.Activas.Should().Be(0);
        seleccion.Oyentes.Should().Be(0, "liberada, deja de escuchar la retirada");
    }

    // ── Caché del estado materializado ─────────────────────────────────────

    [Fact]
    public async Task Tras_retirar_la_seleccion_la_cache_del_circuito_no_recibe_los_avisos_del_Tenant_anterior()
    {
        var circuito = Circuito.Abrir();
        var notificador = new NotificadorEstadoTenant();
        using var cache = new CacheEstadoCircuito(
            new UsuarioDelCircuito(), circuito.Tenant, new VersionDelContexto(circuito.Tenant), notificador);
        var recibidos = new List<AvisoCambioEstado>();
        cache.CambioRecibido += recibidos.Add;

        await cache.ObtenerAsync(Consulta(), CancellationToken.None);
        notificador.Publicar(new AvisoCambioEstado(Beneficiario, 2, [Guid.NewGuid()]));
        recibidos.Should().ContainSingle("control positivo: con la selección vigente el aviso de B llega");

        circuito.Seleccion.Invalidar();

        notificador.SuscripcionesDe(Beneficiario).Should().Be(0, "la baja es en la retirada, no al cerrarse el circuito");
        notificador.Publicar(new AvisoCambioEstado(Beneficiario, 3, [Guid.NewGuid()]));
        recibidos.Should().ContainSingle("tras la retirada, nada de B llega al circuito");

        await cache.ObtenerAsync(Consulta(), CancellationToken.None);
        notificador.SuscripcionesDe(Origen).Should().Be(1);
        notificador.Publicar(new AvisoCambioEstado(Origen, 2, [Guid.NewGuid()]));
        recibidos.Should().HaveCount(2).And.Contain(a => a.TenantId == Origen);
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private static Func<MensajeWhatsAppRecibidoEvent, Task> Anotar(List<MensajeWhatsAppRecibidoEvent> recibidos) =>
        aviso =>
        {
            recibidos.Add(aviso);
            return Task.CompletedTask;
        };

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer el fichero");
        return dir!;
    }

    private static ConsultaEstadoEnCache<Guid> Consulta() => new(
        "centros", f => [f],
        _ => Task.FromResult<IReadOnlyList<Guid>>([Guid.NewGuid()]),
        (_, _) => Task.FromResult<IReadOnlyList<Guid>>([]));

    /// <summary>
    /// Las piezas de producción de un circuito: el principal congelado del Gestor CAE con el claim de su Tenant de origen, la
    /// cookie con el token que emite la propia clase y el <see cref="TenantActual"/>.
    /// </summary>
    private sealed record Circuito(TenantActual Tenant, ClienteActivoSeleccionado Seleccion)
    {
        public static Circuito Abrir()
        {
            var proteccion = new EphemeralDataProtectionProvider();
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Gestor.ToString()),
                new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, Origen.ToString()),
            ], "prueba"));
            var http = new DefaultHttpContext { User = principal };
            http.Request.Headers.Cookie =
                $"{ClienteActivoSeleccionado.NombreCookie}={ClienteActivoSeleccionado.Proteger(proteccion, Gestor, Beneficiario, Guid.NewGuid())}";
            var accesor = new HttpContextAccessor { HttpContext = http };
            var seleccion = new ClienteActivoSeleccionado(accesor, proteccion);
            var circuito = new Circuito(new TenantActual(new PrincipalCongelado(principal), accesor, seleccion), seleccion);
            circuito.Tenant.TenantId.Should().Be(Beneficiario, "control positivo: la cookie protegida selecciona B");
            return circuito;
        }
    }

    private sealed class PrincipalCongelado(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class UsuarioDelCircuito : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Gestor);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>("GestorCae");
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Origen);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>Lee la versión del Tenant que da el contexto en ese momento, como la RLS.</summary>
    private sealed class VersionDelContexto(ITenantActual tenant) : ILectorVersionEstadoTenant
    {
        public Task<VersionEstadoLeida?> LeerAsync(CancellationToken cancellationToken) =>
            Task.FromResult<VersionEstadoLeida?>(new VersionEstadoLeida(tenant.TenantId!.Value, 1));
    }

    /// <summary>Una selección que pasa de B a C; hoy no ocurre dentro de un circuito, pero la suscripción lo aguanta.</summary>
    private sealed class SeleccionQueCambia : IClienteActivoSeleccionado, IAvisoSeleccionRetirada, ITenantActual
    {
        public Guid? TenantIdSeleccionado { get; private set; } = Beneficiario;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
        public Guid? TenantId => TenantIdSeleccionado ?? Origen;
        public event Action<Guid>? TenantSeleccionadoRetirado;
        public int Oyentes => TenantSeleccionadoRetirado?.GetInvocationList().Length ?? 0;

        public void CambiarA(Guid tenant)
        {
            var retirado = TenantIdSeleccionado!.Value;
            TenantIdSeleccionado = tenant;
            TenantSeleccionadoRetirado?.Invoke(retirado);
        }
    }

    /// <summary>Guarda cada callback aunque se suelte, para simular el aviso en vuelo de una instantánea.</summary>
    private sealed class NotificadorQueGuardaLasSuscripciones : INotificadorMensajesTiempoReal
    {
        public List<Func<MensajeWhatsAppRecibidoEvent, Task>> Callbacks { get; } = [];
        public int Suscripciones { get; private set; }
        public int Activas { get; private set; }

        public IDisposable Suscribir(Guid tenantId, Func<MensajeWhatsAppRecibidoEvent, Task> callback)
        {
            Callbacks.Add(callback);
            Suscripciones++;
            Activas++;
            return new Baja(this);
        }

        public Task PublicarAsync(MensajeWhatsAppRecibidoEvent aviso, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        private sealed class Baja(NotificadorQueGuardaLasSuscripciones notificador) : IDisposable
        {
            private bool _hecha;

            public void Dispose()
            {
                if (_hecha) return;
                _hecha = true;
                notificador.Activas--;
            }
        }
    }
}
