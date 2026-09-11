using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Comercial.Commands.ActualizarEstadoComercialTenant;
using CaeManager.Application.Comercial.Commands.RegistrarSuscripcionTenant;
using CaeManager.Application.Comercial.Queries.ObtenerEstadoComercialTenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comercial.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Tests;

/// <summary>
/// Estado comercial contra su mockup Gen 2 («Estado Comercial TALVEG.dc.html»).
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué consulta y qué comandos llegan al
/// mediador y con qué parámetros —el doble responde como los handlers: la
/// consulta devuelve vacío sin autorización global; vincular lee de «Stripe»
/// quién paga y el estado inicial; actualizar exige suscripción vinculada—,
/// qué se pinta con lo que vuelve, qué pide confirmación antes de escribir,
/// qué pasa con respuestas fuera de orden (<see cref="TaskCompletionSource{TResult}"/>)
/// y qué pasa al retirar el componente. Y que ningún texto visible confunda
/// Pagador TALVEG con Tenant propietario.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> la autorización real (<c>IAutorizacionAdminPlataforma</c>,
/// en Application), la llamada real a Stripe, el webhook, el gate de escritura
/// (<c>GateComercialTenantBehavior</c>) ni el aspecto (bUnit no evalúa CSS).
/// </para>
/// </summary>
public class EstadoComercialGen2Tests : BunitContext
{
    /// <summary><see cref="Modal"/> importa dialogo-foco.js y <see cref="AtajosListaTeclado"/> atajos-lista.js.</summary>
    public EstadoComercialGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid TenantArbeko = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid TenantBeitia = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");
    private static readonly Guid TenantElorrio = Guid.Parse("c3c3c3c3-0000-0000-0000-000000000003");

    // ---------------------------------------------------------------- dobles

    private sealed class MediadorControlado(Func<object, CancellationToken, Task<object?>> responder) : IMediator
    {
        public List<object> Enviados { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return (TResponse)(await responder(request, cancellationToken))!;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Enviados.Add(request!);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class LoggerQueAnota : ILogger<EstadoComercial>
    {
        public List<string> Errores { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Errores.Add(formatter(state, exception));
        }
    }

    private sealed class TenantPersistido
    {
        public required Guid Id { get; init; }
        public required string Nombre { get; init; }
        public EstadoComercialTenant Estado { get; set; } = EstadoComercialTenant.SinSuscripcion;
        public string? IdPagadorStripe { get; set; }
        public string? IdSuscripcion { get; set; }
        public DateTime? ActualizadoEnUtc { get; set; }
    }

    /// <summary>Una suscripción tal como la ve «Stripe»: quién la paga y en qué estado está (ya mapeado).</summary>
    private sealed record SuscripcionEnStripe(string IdPagador, EstadoComercialTenant? Estado);

    /// <summary>Una petición que el test retiene y resuelve cuando quiere, con la respuesta que había al pedirla.</summary>
    private sealed record Retenida(object Peticion, CancellationToken Token, object? RespuestaAlPedir, TaskCompletionSource<object?> Tarea)
    {
        public bool Cancelada { get; set; }
    }

    /// <summary>
    /// El «servidor» del test. <see cref="Tenants"/> es lo persistido y
    /// <see cref="Stripe"/> lo que devolvería el proveedor de pago. Los
    /// comandos cambian lo persistido igual que sus handlers, y la siguiente
    /// consulta lo devuelve.
    /// </summary>
    private sealed class Escenario
    {
        public List<TenantPersistido> Tenants { get; } =
        [
            new() { Id = TenantArbeko, Nombre = "Grupo Arbeko", Estado = EstadoComercialTenant.Activa, IdPagadorStripe = "cus_A", IdSuscripcion = "sub_arbeko", ActualizadoEnUtc = new DateTime(2026, 9, 5, 7, 14, 0, DateTimeKind.Utc) },
            new() { Id = TenantBeitia, Nombre = "Construcciones Beitia" },
            new() { Id = TenantElorrio, Nombre = "Montajes Elorrio", Estado = EstadoComercialTenant.Suspendida, IdPagadorStripe = "cus_E", IdSuscripcion = "sub_elorrio", ActualizadoEnUtc = new DateTime(2026, 8, 28, 10, 45, 0, DateTimeKind.Utc) },
        ];

        public Dictionary<string, SuscripcionEnStripe> Stripe { get; } = new()
        {
            ["sub_arbeko"] = new("cus_A", EstadoComercialTenant.SoloLectura),
            ["sub_beitia"] = new("cus_pagador_de_beitia", EstadoComercialTenant.Activa),
            ["sub_elorrio"] = new("cus_E", EstadoComercialTenant.Suspendida),
        };

        /// <summary>Como <c>PuedeGlobalmenteAsync</c>: sin ella la consulta devuelve vacío, no un error.</summary>
        public bool AutorizacionGlobal { get; set; } = true;

        public Func<object, Exception?> Fallar { get; set; } = _ => null;

        public Func<object, bool> Retener { get; set; } = _ => false;

        public List<Retenida> Retenidas { get; } = [];

        public Task<object?> Responder(object peticion, CancellationToken token)
        {
            if (Fallar(peticion) is { } excepcion)
                return Task.FromException<object?>(excepcion);

            if (Retener(peticion))
            {
                var retenida = new Retenida(peticion, token, Calcular(peticion), new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously));
                token.Register(() => retenida.Cancelada = true);
                Retenidas.Add(retenida);
                return retenida.Tarea.Task;
            }

            return Task.FromResult(Calcular(peticion));
        }

        private object? Calcular(object peticion) => peticion switch
        {
            ObtenerEstadoComercialTenantsQuery => Consultar(),
            RegistrarSuscripcionTenantCommand c => Registrar(c),
            ActualizarEstadoComercialTenantCommand c => Actualizar(c),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
        };

        private List<EstadoComercialTenantDto> Consultar() => !AutorizacionGlobal
            ? []
            : Tenants.OrderBy(t => t.Nombre, StringComparer.Ordinal)
                .Select(t => new EstadoComercialTenantDto(t.Id, t.Nombre, t.Estado, t.IdPagadorStripe, t.IdSuscripcion, t.ActualizadoEnUtc))
                .ToList();

        private Result Registrar(RegistrarSuscripcionTenantCommand c)
        {
            var tenant = Tenants.SingleOrDefault(t => t.Id == c.TenantId);
            if (tenant is null)
                return Result.Fallo(Error.Crear("Comercial.TenantNoEncontrado", "No encontramos ese tenant."));

            if (!Stripe.TryGetValue(c.StripeSubscriptionId, out var suscripcion))
                return Result.Fallo(Error.Crear("Stripe.NoEncontrada", "Stripe no reconoce esta suscripción. Revisa el identificador."));

            if (suscripcion.Estado is not { } estado)
                return Result.Fallo(Error.Crear("Comercial.EstadoNoReconocido", "Stripe devolvió un estado de suscripción que no reconocemos."));

            tenant.IdPagadorStripe = suscripcion.IdPagador;
            tenant.IdSuscripcion = c.StripeSubscriptionId;
            tenant.Estado = estado;
            tenant.ActualizadoEnUtc = DateTime.UtcNow;
            return Result.Exito();
        }

        private Result Actualizar(ActualizarEstadoComercialTenantCommand c)
        {
            var tenant = Tenants.SingleOrDefault(t => t.Id == c.TenantId);
            if (tenant is null)
                return Result.Fallo(Error.Crear("Comercial.TenantNoEncontrado", "No encontramos ese tenant."));

            if (tenant.IdSuscripcion is null)
                return Result.Fallo(Error.Crear("Comercial.SinSuscripcionVinculada", "Este tenant todavía no tiene ninguna suscripción de Stripe vinculada."));

            if (Stripe[tenant.IdSuscripcion].Estado is not { } estado)
                return Result.Fallo(Error.Crear("Comercial.EstadoNoReconocido", "Stripe devolvió un estado de suscripción que no reconocemos."));

            tenant.Estado = estado;
            tenant.ActualizadoEnUtc = DateTime.UtcNow;
            return Result.Exito();
        }
    }

    private (IRenderedComponent<EstadoComercial> Cut, MediadorControlado Mediador, LoggerQueAnota Logger) Montar(Escenario escenario, bool integrada = false)
    {
        var mediador = new MediadorControlado(escenario.Responder);
        var logger = new LoggerQueAnota();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<ILogger<EstadoComercial>>(logger);

        var cut = Render<EstadoComercial>(p => p.Add(x => x.IntegradaEnConfiguracion, integrada));
        return (cut, mediador, logger);
    }

    private IReadOnlyList<ToastMensaje> Toasts() => Services.GetRequiredService<ToastService>().Mensajes;

    private static IElement Fila(IRenderedComponent<EstadoComercial> cut, Guid tenantId) =>
        cut.FindAll("tbody tr").Single(tr => tr.GetAttribute("data-tenant-id") == tenantId.ToString());

    private static string Celda(IRenderedComponent<EstadoComercial> cut, Guid tenantId, int columna) =>
        Fila(cut, tenantId).QuerySelectorAll("td")[columna].TextContent.Trim();

    private static IElement BotonDeFila(IRenderedComponent<EstadoComercial> cut, Guid tenantId) =>
        Fila(cut, tenantId).QuerySelectorAll("button").Single(b => !b.ClassList.Contains("estado-comercial-id"));

    /// <summary>El diálogo abierto con ese título, o null. Se busca de nuevo en cada llamada.</summary>
    private static IElement? Dialogo(IRenderedComponent<EstadoComercial> cut, string titulo) =>
        cut.FindAll(".modal-contenido").SingleOrDefault(m => m.QuerySelector("h2")?.TextContent.Trim() == titulo);

    private static IElement BotonDe(IElement contenedor, string texto) =>
        contenedor.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == texto);

    private static List<T> Enviados<T>(MediadorControlado mediador) => mediador.Enviados.OfType<T>().ToList();

    private const string TituloFormulario = "Vincular suscripción de Stripe";
    private const string TituloConfirmarVincular = "¿Vincular esta suscripción?";
    private const string TituloConfirmarActualizar = "¿Actualizar el estado comercial desde Stripe?";

    private static async Task EscribirIdAsync(IRenderedComponent<EstadoComercial> cut, string valor)
    {
        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = valor });
        await cut.Find("input").BlurAsync(new FocusEventArgs());
    }

    private static async Task AbrirFormularioYPedirConfirmacionAsync(IRenderedComponent<EstadoComercial> cut, Guid tenantId, string id)
    {
        await BotonDeFila(cut, tenantId).ClickAsync(new MouseEventArgs());
        await EscribirIdAsync(cut, id);
        await BotonDe(Dialogo(cut, TituloFormulario)!, "Vincular").ClickAsync(new MouseEventArgs());
    }

    // ---------------------------------------------------------------- lista

    [Fact]
    public void Cada_tenant_se_pinta_con_su_estado_su_suscripcion_y_la_accion_que_le_corresponde()
    {
        var (cut, mediador, _) = Montar(new Escenario());

        Enviados<ObtenerEstadoComercialTenantsQuery>(mediador).Should().ContainSingle();
        cut.Find("h1").TextContent.Should().Be("Estado comercial de los tenants");
        cut.Find(".cabecera-pagina-kicker").TextContent.Should().Be("Plataforma");

        cut.FindAll("tbody tr").Select(tr => tr.QuerySelector("td")!.TextContent.Trim())
            .Should().Equal("Construcciones Beitia", "Grupo Arbeko", "Montajes Elorrio");

        Celda(cut, TenantArbeko, 1).Should().Be("Activa");
        Celda(cut, TenantArbeko, 2).Should().Be("sub_arbeko");
        BotonDeFila(cut, TenantArbeko).TextContent.Trim().Should().Be("Actualizar desde Stripe");

        Celda(cut, TenantBeitia, 1).Should().Be("Sin suscripción");
        Celda(cut, TenantBeitia, 2).Should().Be("—");
        Celda(cut, TenantBeitia, 3).Should().Be("—");
        BotonDeFila(cut, TenantBeitia).TextContent.Trim().Should().Be("Vincular suscripción");

        Celda(cut, TenantElorrio, 1).Should().Be("Suspendida");
        Celda(cut, TenantElorrio, 3).Should().Be(
            new DateTime(2026, 8, 28, 10, 45, 0, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
    }

    [Fact]
    public void Integrada_en_el_hub_el_titulo_baja_a_h2()
    {
        var (cut, _, _) = Montar(new Escenario(), integrada: true);

        cut.FindAll("h1").Should().BeEmpty();
        cut.Find("h2.titulo-panel-configuracion").TextContent.Should().Be("Estado comercial de los tenants");
    }

    [Fact]
    public async Task Un_fallo_de_carga_se_distingue_de_la_lista_vacia_y_reintentar_vuelve_a_pedirla()
    {
        var escenario = new Escenario();
        var fallos = 0;
        escenario.Fallar = p => p is ObtenerEstadoComercialTenantsQuery && fallos++ == 0 ? new InvalidOperationException("caída") : null;
        var (cut, mediador, logger) = Montar(escenario);

        cut.Markup.Should().Contain("No pudimos cargar el estado comercial");
        cut.Markup.Should().NotContain("No hay ningún tenant todavía");
        cut.FindAll("tbody tr").Should().BeEmpty();
        Toasts().Should().BeEmpty("el fallo se cuenta en la propia pantalla, no en un aviso que desaparece");
        logger.Errores.Should().ContainSingle();

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new MouseEventArgs());

        Enviados<ObtenerEstadoComercialTenantsQuery>(mediador).Should().HaveCount(2);
        cut.Markup.Should().NotContain("No pudimos cargar el estado comercial");
        cut.FindAll("tbody tr").Should().HaveCount(3);
    }

    [Fact]
    public void Sin_autorizacion_global_la_consulta_llega_vacia_y_la_pantalla_dice_por_que_puede_ser()
    {
        var (cut, _, _) = Montar(new Escenario { AutorizacionGlobal = false });

        cut.Find(".estado-vacio h3").TextContent.Should().Be("No hay ningún tenant todavía");
        cut.Find(".estado-vacio p").TextContent.Should().Contain("capacidad de administración de plataforma con alcance global");
        cut.FindAll("table").Should().BeEmpty();
    }

    [Fact]
    public async Task j_y_k_recorren_las_filas_sin_salirse_de_la_lista()
    {
        var (cut, _, _) = Montar(new Escenario());
        var atajos = cut.FindComponent<AtajosListaTeclado>();

        string? Enfocada() => cut.FindAll("tbody tr.fila-enfocada").SingleOrDefault()?.GetAttribute("data-tenant-id");

        Enfocada().Should().BeNull();
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));
        Enfocada().Should().Be(TenantBeitia.ToString());
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));
        Enfocada().Should().Be(TenantElorrio.ToString(), "j en la última fila se queda en ella");
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("k"));
        Enfocada().Should().Be(TenantArbeko.ToString());
    }

    [Fact]
    public async Task Un_clic_en_el_id_de_suscripcion_lo_copia_tal_cual()
    {
        var modulo = JSInterop.SetupModule("./js/clipboard.js");
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();
        var (cut, _, _) = Montar(new Escenario());

        await Fila(cut, TenantElorrio).QuerySelector("button.estado-comercial-id")!.ClickAsync(new MouseEventArgs());

        modulo.VerifyInvoke("copiarAlPortapapeles").Arguments.Should().Equal("sub_elorrio");
        Toasts().Should().ContainSingle(t => t.Tono == TonoToast.Exito && t.Mensaje.Contains("id de suscripción"));
    }

    // ---------------------------------------------------------------- vincular

    [Fact]
    public async Task Un_id_que_no_empieza_por_sub_se_rechaza_en_pantalla_sin_llegar_al_mediador()
    {
        var (cut, mediador, _) = Montar(new Escenario());

        await AbrirFormularioYPedirConfirmacionAsync(cut, TenantBeitia, "cus_12345");

        Dialogo(cut, TituloFormulario)!.QuerySelector("[role=alert]")!.TextContent.Should().Contain("debe empezar por sub_");
        Dialogo(cut, TituloConfirmarVincular).Should().BeNull();
        Enviados<RegistrarSuscripcionTenantCommand>(mediador).Should().BeEmpty();
    }

    [Fact]
    public async Task Vincular_pide_confirmacion_con_su_efecto_real_y_solo_entonces_escribe()
    {
        var escenario = new Escenario();
        var (cut, mediador, _) = Montar(escenario);

        await AbrirFormularioYPedirConfirmacionAsync(cut, TenantBeitia, "  sub_beitia ");

        Dialogo(cut, TituloFormulario).Should().BeNull("la confirmación sustituye al formulario, no se apila encima");
        var confirmacion = Dialogo(cut, TituloConfirmarVincular)!;
        confirmacion.TextContent.Should().Contain("sub_beitia").And.Contain("Construcciones Beitia")
            .And.Contain("«Solo lectura» o «Suspendida»").And.Contain("dejará de poder escribir");
        Enviados<RegistrarSuscripcionTenantCommand>(mediador).Should().BeEmpty("confirmar es un paso distinto de pedir confirmación");

        await BotonDe(confirmacion, "Vincular suscripción").ClickAsync(new MouseEventArgs());

        Enviados<RegistrarSuscripcionTenantCommand>(mediador).Should().ContainSingle()
            .Which.Should().Be(new RegistrarSuscripcionTenantCommand(TenantBeitia, "sub_beitia"));
        Dialogo(cut, TituloConfirmarVincular).Should().BeNull();
        Toasts().Should().ContainSingle(t => t.Tono == TonoToast.Exito && t.Mensaje == "Suscripción vinculada.");
        Enviados<ObtenerEstadoComercialTenantsQuery>(mediador).Should().HaveCount(2, "tras escribir se vuelve a leer lo persistido");
        Celda(cut, TenantBeitia, 1).Should().Be("Activa", "el estado lo decide Stripe, no la pantalla");
        Celda(cut, TenantBeitia, 2).Should().Be("sub_beitia");
        BotonDeFila(cut, TenantBeitia).TextContent.Trim().Should().Be("Actualizar desde Stripe");
    }

    [Fact]
    public async Task Cancelar_la_confirmacion_vuelve_al_formulario_con_lo_escrito_y_sin_escribir()
    {
        var (cut, mediador, _) = Montar(new Escenario());
        await AbrirFormularioYPedirConfirmacionAsync(cut, TenantBeitia, "sub_beitia");

        await BotonDe(Dialogo(cut, TituloConfirmarVincular)!, "Cancelar").ClickAsync(new MouseEventArgs());

        Dialogo(cut, TituloConfirmarVincular).Should().BeNull();
        Dialogo(cut, TituloFormulario).Should().NotBeNull();
        cut.Find("input").GetAttribute("value").Should().Be("sub_beitia");
        Enviados<RegistrarSuscripcionTenantCommand>(mediador).Should().BeEmpty();
    }

    [Fact]
    public async Task Si_el_comando_falla_vuelve_al_formulario_con_su_mensaje_y_no_recarga()
    {
        var (cut, mediador, _) = Montar(new Escenario());
        await AbrirFormularioYPedirConfirmacionAsync(cut, TenantBeitia, "sub_que_no_existe");

        await BotonDe(Dialogo(cut, TituloConfirmarVincular)!, "Vincular suscripción").ClickAsync(new MouseEventArgs());

        Enviados<RegistrarSuscripcionTenantCommand>(mediador).Should().ContainSingle();
        Dialogo(cut, TituloFormulario)!.QuerySelector("[role=alert]")!.TextContent
            .Should().Be("Stripe no reconoce esta suscripción. Revisa el identificador.");
        Toasts().Should().BeEmpty();
        Enviados<ObtenerEstadoComercialTenantsQuery>(mediador).Should().ContainSingle();
        Celda(cut, TenantBeitia, 1).Should().Be("Sin suscripción");
    }

    [Fact]
    public async Task Un_doble_clic_en_confirmar_la_vinculacion_envia_un_solo_comando()
    {
        var escenario = new Escenario();
        var (cut, mediador, _) = Montar(escenario);
        await AbrirFormularioYPedirConfirmacionAsync(cut, TenantBeitia, "sub_beitia");
        escenario.Retener = p => p is RegistrarSuscripcionTenantCommand;

        var primero = BotonDe(Dialogo(cut, TituloConfirmarVincular)!, "Vincular suscripción").ClickAsync(new MouseEventArgs());
        var segundo = BotonDe(Dialogo(cut, TituloConfirmarVincular)!, "Vincular suscripción").ClickAsync(new MouseEventArgs());

        Enviados<RegistrarSuscripcionTenantCommand>(mediador).Should().ContainSingle();

        escenario.Retenidas.Single().Tarea.SetResult(escenario.Retenidas.Single().RespuestaAlPedir);
        await Task.WhenAll(primero, segundo);
        Enviados<RegistrarSuscripcionTenantCommand>(mediador).Should().ContainSingle();
    }

    // ---------------------------------------------------------------- actualizar

    [Fact]
    public async Task Actualizar_desde_Stripe_pide_confirmacion_y_pinta_el_estado_que_devuelve()
    {
        var (cut, mediador, _) = Montar(new Escenario());

        await BotonDeFila(cut, TenantArbeko).ClickAsync(new MouseEventArgs());

        var confirmacion = Dialogo(cut, TituloConfirmarActualizar)!;
        confirmacion.TextContent.Should().Contain("sub_arbeko").And.Contain("Grupo Arbeko").And.Contain("dejará de poder escribir");
        Enviados<ActualizarEstadoComercialTenantCommand>(mediador).Should().BeEmpty();

        await BotonDe(confirmacion, "Actualizar desde Stripe").ClickAsync(new MouseEventArgs());

        Enviados<ActualizarEstadoComercialTenantCommand>(mediador).Should().ContainSingle()
            .Which.TenantId.Should().Be(TenantArbeko);
        Dialogo(cut, TituloConfirmarActualizar).Should().BeNull();
        Toasts().Should().ContainSingle(t => t.Tono == TonoToast.Exito && t.Mensaje == "Estado comercial actualizado.");
        Celda(cut, TenantArbeko, 1).Should().Be("Solo lectura");
    }

    [Fact]
    public async Task Si_la_actualizacion_falla_se_avisa_con_el_mensaje_del_comando_y_no_se_recarga()
    {
        var escenario = new Escenario();
        escenario.Stripe["sub_arbeko"] = new("cus_A", null);
        var (cut, mediador, _) = Montar(escenario);

        await BotonDeFila(cut, TenantArbeko).ClickAsync(new MouseEventArgs());
        await BotonDe(Dialogo(cut, TituloConfirmarActualizar)!, "Actualizar desde Stripe").ClickAsync(new MouseEventArgs());

        // Un solo aviso EN TOTAL, no "uno que case": un fallo que siguiera de
        // largo sumaría el de éxito y ContainSingle(predicado) no lo vería.
        Toasts().Should().ContainSingle()
            .Which.Should().Match<ToastMensaje>(t => t.Tono == TonoToast.Error && t.Mensaje.StartsWith("Stripe devolvió un estado"));
        Enviados<ObtenerEstadoComercialTenantsQuery>(mediador).Should().ContainSingle();
        Celda(cut, TenantArbeko, 1).Should().Be("Activa");
    }

    [Fact]
    public async Task Mientras_una_actualizacion_esta_en_curso_otra_fila_no_cambia_lo_que_se_confirma()
    {
        var escenario = new Escenario();
        var (cut, mediador, _) = Montar(escenario);
        escenario.Retener = p => p is ActualizarEstadoComercialTenantCommand;

        await BotonDeFila(cut, TenantArbeko).ClickAsync(new MouseEventArgs());
        var enCurso = BotonDe(Dialogo(cut, TituloConfirmarActualizar)!, "Actualizar desde Stripe").ClickAsync(new MouseEventArgs());

        await BotonDeFila(cut, TenantElorrio).ClickAsync(new MouseEventArgs());

        Dialogo(cut, TituloConfirmarActualizar)!.TextContent.Should().Contain("Grupo Arbeko").And.NotContain("Montajes Elorrio");

        var retenida = escenario.Retenidas.Single();
        retenida.Tarea.SetResult(retenida.RespuestaAlPedir);
        await enCurso;

        Enviados<ActualizarEstadoComercialTenantCommand>(mediador).Should().ContainSingle()
            .Which.TenantId.Should().Be(TenantArbeko);
        Dialogo(cut, TituloConfirmarActualizar).Should().BeNull("la fila pulsada a destiempo no deja abierta una confirmación ajena");
    }

    // ---------------------------------------------------------------- carreras y retirada

    [Fact]
    public async Task Una_recarga_vieja_que_llega_tarde_no_pisa_la_vigente()
    {
        var escenario = new Escenario();
        var (cut, mediador, _) = Montar(escenario);
        escenario.Retener = p => p is ObtenerEstadoComercialTenantsQuery;

        // Recarga 2: tras vincular Beitia. Se lee ANTES de que Arbeko cambie.
        await AbrirFormularioYPedirConfirmacionAsync(cut, TenantBeitia, "sub_beitia");
        var vincular = BotonDe(Dialogo(cut, TituloConfirmarVincular)!, "Vincular suscripción").ClickAsync(new MouseEventArgs());

        // Recarga 3: tras actualizar Arbeko (Stripe dice Solo lectura).
        await BotonDeFila(cut, TenantArbeko).ClickAsync(new MouseEventArgs());
        var actualizar = BotonDe(Dialogo(cut, TituloConfirmarActualizar)!, "Actualizar desde Stripe").ClickAsync(new MouseEventArgs());

        escenario.Retenidas.Should().HaveCount(2);
        var (vieja, vigente) = (escenario.Retenidas[0], escenario.Retenidas[1]);

        vigente.Tarea.SetResult(vigente.RespuestaAlPedir);
        await actualizar;
        Celda(cut, TenantArbeko, 1).Should().Be("Solo lectura");

        vieja.Tarea.SetResult(vieja.RespuestaAlPedir);
        await vincular;

        Enviados<ObtenerEstadoComercialTenantsQuery>(mediador).Should().HaveCount(3);
        Celda(cut, TenantArbeko, 1).Should().Be("Solo lectura", "la respuesta de la recarga anterior ya no es la vigente");
        Celda(cut, TenantBeitia, 1).Should().Be("Activa");
    }

    [Fact]
    public async Task Retirar_el_componente_cancela_la_carga_en_vuelo_y_su_respuesta_no_toca_nada()
    {
        var escenario = new Escenario { Retener = p => p is ObtenerEstadoComercialTenantsQuery };
        var (cut, mediador, logger) = Montar(escenario);
        var retenida = escenario.Retenidas.Single();
        retenida.Cancelada.Should().BeFalse();

        await DisposeComponentsAsync();

        retenida.Cancelada.Should().BeTrue("DisposeAsync del componente tiene que haberse ejecutado y cancelado su ciclo");

        // Como EF: la consulta cancelada termina en OperationCanceledException.
        retenida.Tarea.SetException(new OperationCanceledException(retenida.Token));

        // Barrera, no espera: SetException encola la continuación de la carga en
        // el dispatcher del renderer, e InvokeAsync se encola DETRÁS de ella, así
        // que al volver la página ya ha manejado la respuesta. Con un
        // Task.Yield() las comprobaciones de abajo se adelantaban a la
        // continuación y daban verde sin haber observado nada (demostrado por
        // mutación: quitar la guarda _desechado del catch pasaba en verde).
        await cut.InvokeAsync(() => { });

        logger.Errores.Should().BeEmpty("una carga cancelada por retirar la pantalla no es un fallo que registrar");
        Toasts().Should().BeEmpty();
        mediador.Enviados.Should().ContainSingle();
    }

    // ---------------------------------------------------------------- vocabulario

    /// <summary>
    /// Plano comercial: el Pagador TALVEG no se deduce del tenant, y en
    /// particular no es por definición el Tenant propietario. Recorre todos
    /// los estados y diálogos de la pantalla y exige que ninguna frase visible
    /// los iguale, que ninguna haga pagar al tenant y que «cliente» no
    /// aparezca sin su apellido semántico.
    /// </summary>
    [Fact]
    public async Task Ningun_texto_visible_confunde_Pagador_TALVEG_con_Tenant_propietario()
    {
        var textos = new List<string>();

        var (cut, _, _) = Montar(new Escenario());
        textos.Add(TextoDe(cut));
        await AbrirFormularioYPedirConfirmacionAsync(cut, TenantBeitia, "sub_x");
        textos.Add(TextoDe(cut));
        await BotonDe(Dialogo(cut, TituloConfirmarVincular)!, "Cancelar").ClickAsync(new MouseEventArgs());
        await BotonDe(Dialogo(cut, TituloFormulario)!, "Cancelar").ClickAsync(new MouseEventArgs());
        await BotonDeFila(cut, TenantArbeko).ClickAsync(new MouseEventArgs());
        textos.Add(TextoDe(cut));
        await BotonDe(Dialogo(cut, TituloConfirmarActualizar)!, "Actualizar desde Stripe").ClickAsync(new MouseEventArgs());
        textos.Add(TextoDe(cut));
        textos.AddRange(Toasts().Select(t => t.Mensaje));

        var vacia = Render<EstadoComercial>();
        textos.Add(TextoDe(vacia));

        var todo = Regex.Replace(string.Join(" ", textos), @"\s+", " ");
        var frases = Regex.Split(todo, @"(?<=[.!?])\s+");

        todo.Should().Contain("Pagador TALVEG", "si el término no aparece, esta comprobación no está mirando la frase que importa");
        frases.Should().Contain(f => f.Contains("Pagador TALVEG") && f.Contains("Tenant propietario"),
            "la entradilla es la frase que separa los dos conceptos; sin ella el resto pasa en vacío");

        frases.Where(f => Regex.IsMatch(f, @"\bpagador\b", RegexOptions.IgnoreCase) && Regex.IsMatch(f, @"\bpropietario\b", RegexOptions.IgnoreCase))
            .Should().OnlyContain(f => f.Contains("no tiene por qué"),
                "una frase que junta Pagador y Tenant propietario solo puede ser para decir que no se deducen uno del otro");

        frases.Should().NotContain(f => Regex.IsMatch(f,
                @"\btenant (paga|pagador)\b|\bpaga el tenant\b|\bpagador del tenant\b|\bpagador\b[^.]*\b(es|coincide con|equivale a) (el|al) tenant propietario\b",
                RegexOptions.IgnoreCase),
            "el tenant no es quien paga por definición");

        Regex.Matches(todo, @"\bcliente\b(?!\s+(comercial TALVEG|empresarial))", RegexOptions.IgnoreCase)
            .Should().BeEmpty("«cliente» a secas no dice de qué plano se habla");
    }

    [Fact]
    public void El_test_de_vocabulario_detecta_una_frase_que_iguala_Pagador_y_Tenant_propietario()
    {
        // Control positivo del instrumento de arriba: las mismas reglas sobre
        // una frase que las incumple tienen que saltar.
        const string mala = "El Pagador TALVEG es el Tenant propietario.";

        Regex.IsMatch(mala, @"\bpagador\b", RegexOptions.IgnoreCase).Should().BeTrue();
        mala.Contains("no tiene por qué").Should().BeFalse();
        Regex.IsMatch(mala, @"\bpagador\b[^.]*\b(es|coincide con|equivale a) (el|al) tenant propietario\b", RegexOptions.IgnoreCase)
            .Should().BeTrue();
    }

    private static string TextoDe(IRenderedComponent<EstadoComercial> cut) =>
        string.Join(" ", cut.Nodes.Select(n => n.TextContent));
}
