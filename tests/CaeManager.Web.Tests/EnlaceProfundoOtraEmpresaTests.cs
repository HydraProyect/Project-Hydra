using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Enlace profundo a una ficha que el Tenant activo no devuelve (contrato del selector de Tenant, § 4.5,
/// invariante I15). El componente solo conoce al usuario y la URL: nunca la entidad. Lo que se prueba es que
/// su salida no depende de la pista salvo cuando la pista está en el conjunto autorizado, que el botón es un
/// POST y no un enlace, y que ningún GET cambia de empresa. La consulta por Id que devuelve null para "otro
/// Tenant" y para "no existe" la prueba la capa de integración con RLS.
/// </summary>
public class EnlaceProfundoOtraEmpresaTests : BunitContext
{
    private static readonly Guid Origen = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid Norte = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid Sur = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid Ajena = Guid.Parse("cccccccc-0000-0000-0000-000000000009");

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }

    private sealed class Mediador(Func<IReadOnlyList<ClienteAutorizadoDto>> lista) : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return request is ObtenerClientesAutorizadosQuery
                ? Task.FromResult((T)(object)lista())
                : throw new NotSupportedException(request.GetType().Name);
        }

        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => Task.CompletedTask;
    }

    private IReadOnlyList<ClienteAutorizadoDto> _lista =
    [
        new(Origen, "Operador de prueba", EsOrigen: true),
        new(Norte, "Empresa Norte", EsOrigen: false, EsGestionadoPorOperacion: true),
        new(Sur, "Empresa Sur", EsOrigen: false, EsGestionadoPorOperacion: true),
    ];

    private readonly Mediador _mediador;

    public EnlaceProfundoOtraEmpresaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        _mediador = new Mediador(() => _lista);
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();
    }

    private IRenderedComponent<EnlaceProfundoOtraEmpresa> Renderizar(string url, Guid? activa = null)
    {
        Services.AddScoped<ITenantActual>(_ => new TenantActualFijo(activa ?? Sur));
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return Render<EnlaceProfundoOtraEmpresa>();
    }

    [Fact]
    public void Sin_pista_solo_se_avisa_de_que_puede_ser_de_otra_empresa_de_la_cartera()
    {
        var cut = Renderizar("trabajadores/aaaaaaaa-0000-0000-0000-000000000001");

        cut.Find(".enlace-otra-empresa-aviso").TextContent.Should().Contain("Puede pertenecer a otra empresa de tu cartera");
        cut.FindAll("form").Should().BeEmpty("sin pista autorizada no hay botón");
    }

    [Fact]
    public void Con_una_pista_autorizada_hay_boton_Abrir_en_que_es_un_POST_con_antiforgery_y_vuelve_a_la_misma_ruta_sin_la_pista()
    {
        var cut = Renderizar($"trabajadores/aaaaaaaa-0000-0000-0000-000000000001?tenant={Norte}&q=x", activa: Sur);

        var formulario = cut.Find("form[data-enlace-otra-empresa]");
        formulario.GetAttribute("method").Should().Be("post");
        formulario.GetAttribute("action").Should().Be("/cuenta/cliente-activo");
        formulario.QuerySelector("input[name=tenantId]")!.GetAttribute("value").Should().Be(Norte.ToString());
        formulario.QuerySelector("input[name=returnUrl]")!.GetAttribute("value")
            .Should().Be("/trabajadores/aaaaaaaa-0000-0000-0000-000000000001", "vuelve al mismo enlace, sin la pista ni otros parámetros");
        formulario.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value").Should().Be("token-de-prueba");
        formulario.QuerySelector("button")!.GetAttribute("type").Should().Be("submit");
        formulario.TextContent.Should().Contain("Abrir en Empresa Norte");
        cut.FindAll("a").Should().BeEmpty("nunca un enlace GET que cambie de empresa");
    }

    /// <summary>
    /// Codex, pasada 1: si solo cambia la URL con el estado de error aún montado, Blazor reutiliza la
    /// instancia; pista, destino y ruta de retorno se recalculan y no se vuelve a consultar la lista.
    /// </summary>
    [Fact]
    public void Si_solo_cambia_la_URL_con_la_instancia_montada_el_boton_sigue_a_la_pista_y_a_la_ruta()
    {
        var cut = Renderizar("centros/aaaaaaaa-0000-0000-0000-000000000001", activa: Sur);
        cut.FindAll("form").Should().BeEmpty("control positivo: sin pista no hay botón");
        var navegacion = Services.GetRequiredService<NavigationManager>();

        cut.InvokeAsync(() => navegacion.NavigateTo($"centros/aaaaaaaa-0000-0000-0000-000000000001?tenant={Norte}"));
        cut.WaitForAssertion(() => cut.Find("form input[name=tenantId]").GetAttribute("value").Should().Be(Norte.ToString()));

        cut.InvokeAsync(() => navegacion.NavigateTo($"centros/aaaaaaaa-0000-0000-0000-000000000002?tenant={Origen}"));
        cut.WaitForAssertion(() =>
        {
            cut.Find("form input[name=tenantId]").GetAttribute("value").Should().Be(Origen.ToString());
            cut.Find("form input[name=returnUrl]").GetAttribute("value").Should().Be("/centros/aaaaaaaa-0000-0000-0000-000000000002");
        });

        cut.InvokeAsync(() => navegacion.NavigateTo($"centros/aaaaaaaa-0000-0000-0000-000000000002?tenant={Ajena}"));
        cut.WaitForAssertion(() => cut.FindAll("form").Should().BeEmpty("una pista no autorizada retira el botón"));

        _mediador.Enviadas.OfType<ObtenerClientesAutorizadosQuery>().Should().ContainSingle("la lista se lee una sola vez");
    }

    [Fact]
    public void La_pista_por_si_sola_no_cambia_de_empresa_ni_navega()
    {
        var url = $"centros/aaaaaaaa-0000-0000-0000-000000000001?tenant={Norte}";
        Renderizar(url, activa: Sur);

        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.Uri.Should().EndWith(url, "renderizar con la pista no navega a ninguna parte");
        _mediador.Enviadas.Should().OnlyContain(e => e is ObtenerClientesAutorizadosQuery, "solo lee el conjunto autorizado; no escribe nada");
    }

    [Theory]
    [InlineData("ajena")]
    [InlineData("basura")]
    [InlineData("activa")]
    public void Una_pista_no_autorizada_invalida_o_de_la_empresa_ya_activa_se_ignora_con_la_misma_salida_que_sin_pista(string caso)
    {
        var url = "clientes/aaaaaaaa-0000-0000-0000-000000000001";
        var sinPista = Renderizar(url, activa: Sur).Markup;

        Guid? activa = Sur;
        var pista = caso switch { "ajena" => Ajena.ToString(), "activa" => Sur.ToString(), _ => "no-es-un-guid" };
        using var otro = new BunitContext();
        otro.JSInterop.Mode = JSRuntimeMode.Loose;
        otro.Services.AddLocalization();
        otro.Services.AddScoped<IMediator>(_ => _mediador);
        otro.Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();
        otro.Services.AddScoped<ITenantActual>(_ => new TenantActualFijo(activa));
        otro.Services.GetRequiredService<NavigationManager>().NavigateTo($"{url}?tenant={pista}");
        var conPista = otro.Render<EnlaceProfundoOtraEmpresa>().Markup;

        conPista.Should().Be(sinPista, "la pista que no está en el conjunto autorizado no cambia nada, ni siquiera lo dice");
        conPista.Should().NotContain("Abrir en");
    }

    [Fact]
    public void Un_usuario_sin_selector_no_recibe_ni_aviso_ni_boton_aunque_traiga_pista()
    {
        _lista = [new(Origen, "Operador de prueba", EsOrigen: true)];

        var cut = Renderizar($"trabajadores/aaaaaaaa-0000-0000-0000-000000000001?tenant={Norte}", activa: Origen);

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Si_la_lista_autorizada_falla_no_se_ofrece_nada_y_no_revienta()
    {
        Services.AddScoped<IMediator>(_ => new Mediador(() => throw new InvalidOperationException("sin lista")));
        Services.AddScoped<ITenantActual>(_ => new TenantActualFijo(Sur));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"trabajadores/x?tenant={Norte}");

        var cut = Render<EnlaceProfundoOtraEmpresa>();

        cut.Markup.Trim().Should().BeEmpty();
    }
}

/// <summary>Tenant efectivo fijo para las pruebas de páginas que pintan <see cref="EnlaceProfundoOtraEmpresa"/> en su estado de error.</summary>
internal sealed class TenantActualFijo(Guid? tenantId) : ITenantActual
{
    public Guid? TenantId => tenantId;
}

internal static class EnlaceProfundoRegistro
{
    private sealed class AntiforgeryFijo : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }

    /// <summary>Lo que el estado de error de las fichas 360 necesita del contenedor para pintar <see cref="EnlaceProfundoOtraEmpresa"/>.</summary>
    public static void ConEnlaceProfundoOtraEmpresa(this BunitServiceProvider servicios)
    {
        servicios.AddScoped<ITenantActual>(_ => new TenantActualFijo(null));
        servicios.AddScoped<AntiforgeryStateProvider, AntiforgeryFijo>();
    }
}
