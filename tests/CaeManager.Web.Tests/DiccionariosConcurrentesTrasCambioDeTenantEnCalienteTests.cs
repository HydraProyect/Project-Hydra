using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.EstadoMaterializado.Frescura;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
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
/// Refuerzo del aislamiento multi-Tenant (2026-10-25): los diccionarios concurrentes que comparten los
/// circuitos (singletons del proceso) no cruzan datos cuando un circuito cambia de Tenant en caliente.
///
/// <para>
/// El cambio en caliente es el único posible dentro de un circuito: la retirada de la selección
/// (<see cref="ClienteActivoSeleccionado.Invalidar"/>), que devuelve el <see cref="TenantActual"/> real del
/// Tenant beneficiario B al Tenant de origen O del Gestor CAE. Cada pieza recibe el Tenant como lo recibe
/// en producción, leído del <see cref="TenantActual"/> en el momento de suscribirse o de pedir.
/// La parte con base de datos (consultas y entidades rastreadas del DbContext del circuito) está en
/// <c>CambioDeTenantEnCalienteDelCircuitoBajoRuntimeTests</c>, en la suite de integración.
/// </para>
/// </summary>
public class DiccionariosConcurrentesTrasCambioDeTenantEnCalienteTests
{
    private static readonly Guid Gestor = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid Origen = Guid.Parse("dddddddd-0000-0000-0000-0000000000a0");
    private static readonly Guid Beneficiario = Guid.Parse("dddddddd-0000-0000-0000-0000000000b0");
    private static readonly Guid OtroBeneficiario = Guid.Parse("dddddddd-0000-0000-0000-0000000000c0");

    [Fact]
    public void Retirar_la_seleccion_cambia_el_Tenant_del_mismo_circuito_al_de_origen()
    {
        var circuito = Circuito.Abrir();

        circuito.Tenant.TenantId.Should().Be(Beneficiario, "control positivo: la cookie protegida selecciona B");
        circuito.Seleccion.Invalidar();
        circuito.Tenant.TenantId.Should().Be(Origen);
    }

    [Fact]
    public void El_notificador_del_estado_materializado_solo_entrega_a_las_suscripciones_de_su_Tenant()
    {
        var circuito = Circuito.Abrir();
        var notificador = new NotificadorEstadoTenant();
        var recibidosEnB = new List<AvisoCambioEstado>();
        var recibidosEnO = new List<AvisoCambioEstado>();

        using var suscripcionB = notificador.Suscribir(circuito.Tenant.TenantId!.Value, recibidosEnB.Add);
        circuito.Seleccion.Invalidar();
        using var suscripcionO = notificador.Suscribir(circuito.Tenant.TenantId!.Value, recibidosEnO.Add);

        notificador.Publicar(new AvisoCambioEstado(Beneficiario, 1, [Guid.NewGuid()]));
        notificador.Publicar(new AvisoCambioEstado(Origen, 2, [Guid.NewGuid()]));
        notificador.Publicar(new AvisoCambioEstado(OtroBeneficiario, 3, [Guid.NewGuid()]));

        recibidosEnB.Should().ContainSingle().Which.TenantId.Should().Be(Beneficiario);
        recibidosEnO.Should().ContainSingle().Which.TenantId.Should().Be(Origen, "lo de B no llega a la suscripción hecha ya en O");

        suscripcionB.Dispose();
        notificador.SuscripcionesDe(Beneficiario).Should().Be(0, "al liberar la del Tenant anterior no queda nada escuchando B");
        notificador.SuscripcionesDe(Origen).Should().Be(1);
    }

    [Fact]
    public async Task El_notificador_de_mensajes_en_tiempo_real_solo_entrega_a_las_suscripciones_de_su_Tenant()
    {
        var circuito = Circuito.Abrir();
        var notificador = new NotificadorMensajesTiempoReal(NullLogger<NotificadorMensajesTiempoReal>.Instance);
        var recibidosEnB = new List<MensajeWhatsAppRecibidoEvent>();
        var recibidosEnO = new List<MensajeWhatsAppRecibidoEvent>();

        using var suscripcionB = notificador.Suscribir(circuito.Tenant.TenantId!.Value, Anotar(recibidosEnB));
        circuito.Seleccion.Invalidar();
        using var suscripcionO = notificador.Suscribir(circuito.Tenant.TenantId!.Value, Anotar(recibidosEnO));

        await notificador.PublicarAsync(new MensajeWhatsAppRecibidoEvent(Beneficiario, Guid.NewGuid()));
        await notificador.PublicarAsync(new MensajeWhatsAppRecibidoEvent(Origen, Guid.NewGuid()));
        await notificador.PublicarAsync(new MensajeWhatsAppRecibidoEvent(OtroBeneficiario, Guid.NewGuid()));

        recibidosEnB.Should().ContainSingle().Which.TenantId.Should().Be(Beneficiario);
        recibidosEnO.Should().ContainSingle().Which.TenantId.Should().Be(Origen, "lo de B no llega a la suscripción hecha ya en O");

        suscripcionB.Dispose();
        await notificador.PublicarAsync(new MensajeWhatsAppRecibidoEvent(Beneficiario, Guid.NewGuid()));
        recibidosEnB.Should().ContainSingle("liberada la suscripción del Tenant anterior, B ya no entrega nada");
    }

    [Fact]
    public async Task El_contador_del_selector_no_sirve_lo_calculado_con_el_Tenant_anterior_ni_un_Tenant_retirado()
    {
        var circuito = Circuito.Abrir();
        var contador = new ContadorPendientesSelectorTenant(TimeProvider.System);
        var calculos = 0;
        // Un cálculo que devolviera también B después de retirarlo (el peor caso): el contador lo recorta.
        Task<MiTrabajoAgregadoDto> Calcular(CancellationToken _)
        {
            calculos++;
            return Task.FromResult(new MiTrabajoAgregadoDto([Cola(Origen, 5, origen: true), Cola(Beneficiario, 3), Cola(OtroBeneficiario, 1)]));
        }

        var conB = await contador.ObtenerAsync(
            Gestor, circuito.Tenant.TenantId!.Value, [Autorizado(Origen, origen: true), Autorizado(Beneficiario), Autorizado(OtroBeneficiario)],
            Calcular, CancellationToken.None);
        conB.Should().Equal(new Dictionary<Guid, int> { [Beneficiario] = 3, [OtroBeneficiario] = 1 }, "control positivo");

        // Se retira la selección porque la Asignación de Cartera sobre B dejó de valer: B sale del conjunto autorizado.
        circuito.Seleccion.Invalidar();
        var conO = await contador.ObtenerAsync(
            Gestor, circuito.Tenant.TenantId!.Value, [Autorizado(Origen, origen: true), Autorizado(OtroBeneficiario)],
            Calcular, CancellationToken.None);

        conO.Should().Equal(new Dictionary<Guid, int> { [OtroBeneficiario] = 1 }, "B ya no está autorizado y no aparece ni cuenta");
        calculos.Should().Be(2, "el Tenant activo es otro: no se reutiliza la entrada calculada en B");
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private static Func<MensajeWhatsAppRecibidoEvent, Task> Anotar(List<MensajeWhatsAppRecibidoEvent> recibidos) =>
        aviso =>
        {
            recibidos.Add(aviso);
            return Task.CompletedTask;
        };

    private static ClienteAutorizadoDto Autorizado(Guid tenant, bool origen = false) =>
        origen ? new(tenant, "Origen", true, true) : new(tenant, "E" + tenant.ToString("N")[^2..], false, true, EsCarteraGestorCae: true);

    private static MiTrabajoTenantDto Cola(Guid tenant, int total, bool origen = false) => new(
        tenant, "x", origen, new BandejaAgrupadaDto([], []), [], [],
        new ResumenMiTrabajoTenantDto(tenant, "x", origen, total, 0, total, 0, 0), AlcanceCero: false);

    /// <summary>
    /// Las piezas de producción de un circuito: el principal congelado del Gestor CAE con el claim de su
    /// Tenant de origen, la cookie con el token que emite la propia clase y el <see cref="TenantActual"/>.
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
            return new Circuito(new TenantActual(new PrincipalCongelado(principal), accesor, seleccion), seleccion);
        }
    }

    private sealed class PrincipalCongelado(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }
}
