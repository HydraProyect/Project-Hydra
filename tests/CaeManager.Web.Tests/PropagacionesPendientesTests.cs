using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.GestionarPropagacion;
using CaeManager.Application.Documentos.Queries.ObtenerPropagacionesPendientes;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Bandeja.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class PropagacionesPendientesTests : BunitContext
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly PropagacionPendienteDto _correo = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "RNT · septiembre", "Centro Norte", TipoCanalGestion.Email, null, true, new DateOnly(2026, 10, 19));

    private sealed class Mediador(Guid tenant) : IMediator
    {
        public bool ConBuzon { get; set; } = true;
        public bool Autorizado { get; set; } = true;
        public List<GestionarPropagacionCommand> Envios { get; } = [];
        public List<Guid?> TenantsDeEscritura { get; } = [];
        public TaskCompletionSource Enviado { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object? respuesta = request switch
            {
                ObtenerClientesAutorizadosQuery => Autorizado ? new List<ClienteAutorizadoDto> { new(tenant, "Empresa", false) } : new List<ClienteAutorizadoDto>(),
                ComprobarBuzonAvisoPropagacionQuery => ConBuzon ? Guid.NewGuid() : null,
                GestionarPropagacionCommand comando => Enviar(comando),
                DeshacerPropagacionCommand => Result.Exito(),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)respuesta!);
        }
        private Result<Guid> Enviar(GestionarPropagacionCommand comando)
        {
            Envios.Add(comando);
            TenantsDeEscritura.Add(AmbitoTenantExplicito.TenantIdActual);
            Enviado.TrySetResult();
            return Result.Exito(Guid.NewGuid());
        }
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private (IRenderedComponent<PropagacionesPendientes> Cut, Mediador Mediador, ToastService Toasts) Pintar(
        bool conBuzon = true, bool autorizado = true, TimeSpan? ventana = null, PropagacionPendienteDto? destino = null)
    {
        var mediator = new Mediador(_tenant) { ConBuzon = conBuzon, Autorizado = autorizado };
        var toast = new ToastService(TimeSpan.FromSeconds(5), ventana ?? ToastService.DuracionAutoDescarteConAccion);
        Services.AddSingleton<IMediator>(mediator);
        Services.AddSingleton(toast);
        Services.AddScoped<PuertaAccesoDatos>();
        var cut = Render<PropagacionesPendientes>(p => p.Add(c => c.Filas,
            new List<PropagacionesPendientes.Fila> { new(_tenant, "Refrielectric", destino ?? _correo) }));
        return (cut, mediator, toast);
    }

    [Fact]
    public void Sin_buzon_pide_conectarlo_y_conserva_la_propagacion_pendiente()
    {
        var (cut, mediator, toasts) = Pintar(conBuzon: false);
        cut.Find(".propagacion-acciones button").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Conecta el buzón"));
        cut.Find(".propagaciones h2").TextContent.Should().Contain("· 1");
        mediator.Envios.Should().BeEmpty();
        toasts.Mensajes.Should().BeEmpty();
        cut.FindAll("input[name=tenantId]").Should().OnlyContain(e => e.GetAttribute("value") == _tenant.ToString());
    }

    [Fact]
    public async Task Deshacer_en_la_ventana_cancela_y_permite_volver_a_enviar()
    {
        var (cut, mediator, toasts) = Pintar(ventana: TimeSpan.FromMilliseconds(250));
        cut.Find(".propagacion-acciones button").Click();
        cut.WaitForAssertion(() => toasts.Mensajes.Should().ContainSingle());
        mediator.Envios.Should().BeEmpty();
        await cut.InvokeAsync(() => toasts.EjecutarAccionAsync(toasts.Mensajes.Single().Id));
        await Task.Delay(350);
        mediator.Envios.Should().BeEmpty();
        cut.Find(".propagacion-acciones button").TextContent.Should().Be("Enviar aviso");
        cut.Find(".propagacion-acciones button").Click();
        await mediator.Enviado.Task.WaitAsync(TimeSpan.FromSeconds(4));
        cut.WaitForAssertion(() => cut.FindAll(".propagacion").Should().BeEmpty());
        mediator.Envios.Should().ContainSingle().Which.EnviarAviso.Should().BeTrue();
        mediator.TenantsDeEscritura.Should().Equal(_tenant);
        cut.FindAll("button").Should().NotContain(b => b.TextContent == "Deshacer");
    }

    [Fact]
    public async Task Cerrar_la_pantalla_durante_la_ventana_no_envia()
    {
        var (cut, mediator, toasts) = Pintar(ventana: TimeSpan.FromMilliseconds(250));
        cut.Find(".propagacion-acciones button").Click();
        cut.WaitForAssertion(() => toasts.Mensajes.Should().ContainSingle());
        cut.Dispose();
        await Task.Delay(350);
        mediator.Envios.Should().BeEmpty();
        toasts.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_cartera_revocada_durante_la_ventana_impide_el_envio()
    {
        var (cut, mediator, toasts) = Pintar(ventana: TimeSpan.FromMilliseconds(150));
        cut.Find(".propagacion-acciones button").Click();
        cut.WaitForAssertion(() => toasts.Mensajes.Should().ContainSingle());
        mediator.Autorizado = false;
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ya no tienes acceso"));
        await Task.Delay(100);
        mediator.Envios.Should().BeEmpty();
    }

    [Fact]
    public void Un_Tenant_propietario_ajeno_no_alcanza_ninguna_accion()
    {
        var (cut, mediator, toasts) = Pintar(autorizado: false);
        cut.Find(".propagacion-acciones button").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ya no tienes acceso"));
        mediator.Envios.Should().BeEmpty();
        toasts.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public void Abrir_el_portal_no_marca_hecho_y_la_entrega_manual_se_puede_deshacer()
    {
        var plataforma = _correo with { Via = TipoCanalGestion.Plataforma, UrlPortal = "portal.example.test" };
        var (cut, mediator, _) = Pintar(destino: plataforma);
        var enlace = cut.Find(".propagacion-acciones a");
        enlace.GetAttribute("href").Should().Be("https://portal.example.test/");
        enlace.GetAttribute("rel").Should().Contain("noopener");
        enlace.Click();
        mediator.Envios.Should().BeEmpty();
        cut.FindAll(".propagacion-acciones button").Single(b => b.TextContent == "Marcar hecho a mano").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Hecho por ti"));
        mediator.Envios.Should().ContainSingle().Which.EnviarAviso.Should().BeFalse();
        cut.Find(".propagacion-acciones button").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Hecho por ti"));
    }

    [Fact]
    public void El_vacio_no_anuncia_todo_actualizado_si_la_cartera_esta_incompleta()
    {
        var (cut, _, _) = Pintar();
        cut.Render(p => p.Add(c => c.Filas, []).Add(c => c.Incompleta, true));
        cut.Markup.Should().Contain("No se pudieron consultar todas las Empresas");
        cut.Markup.Should().NotContain("Todo actualizado");
    }
}
