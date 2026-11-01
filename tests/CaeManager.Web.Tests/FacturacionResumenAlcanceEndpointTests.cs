using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;
using CaeManager.Web.Features.Facturacion;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

public class FacturacionResumenAlcanceEndpointTests
{
    [Fact]
    public async Task Fuera_del_ambito_efectivo_devuelve_404_sin_consultar_el_resumen()
    {
        var resultado = await DescargarAsync(Guid.NewGuid().ToString(), visible: false, existe: true);

        resultado.Contexto.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        resultado.Mediador.Consultas.Should().BeEmpty();
        resultado.Contexto.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
        resultado.Alcance.Consultas.Should().Be(1);
    }

    [Fact]
    public async Task Un_identificador_ajeno_al_Tenant_propietario_devuelve_404_si_la_consulta_no_encuentra_la_Empresa()
    {
        var resultado = await DescargarAsync(Guid.NewGuid().ToString(), visible: true, existe: false);

        resultado.Contexto.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        resultado.Mediador.Consultas.Should().ContainSingle();
    }

    [Fact]
    public async Task Un_nombre_con_traversal_no_se_acepta_como_identificador_ni_llega_a_la_consulta()
    {
        var resultado = await DescargarAsync("..%2Fsecreto.pdf", visible: true, existe: true);

        resultado.Contexto.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        resultado.Alcance.Consultas.Should().Be(0);
        resultado.Mediador.Consultas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Cliente_empresarial_visible_exporta_el_resumen_del_periodo_solicitado()
    {
        var id = Guid.NewGuid();
        var resultado = await DescargarAsync(id.ToString(), visible: true, existe: true);

        resultado.Contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        resultado.Contexto.Response.ContentType.Should().Contain("spreadsheetml");
        resultado.Mediador.Consultas.Should().ContainSingle().Which.Should()
            .Be(new ObtenerResumenFacturacionQuery(id, 2026, 10));
        resultado.Contexto.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
        resultado.Alcance.Consultas.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Con_cartera_restringida_solo_exporta_el_Cliente_empresarial_de_la_cartera(bool enCartera)
    {
        var enLaCartera = Guid.NewGuid();
        var pedido = enCartera ? enLaCartera : Guid.NewGuid();
        var resultado = await DescargarAsync(pedido.ToString(), visible: false, existe: true, cartera: [enLaCartera]);

        resultado.Contexto.Response.StatusCode.Should().Be(enCartera ? StatusCodes.Status200OK : StatusCodes.Status404NotFound);
        resultado.Mediador.Consultas.Should().HaveCount(enCartera ? 1 : 0);
    }

    private static async Task<(HttpContext Contexto, AlcanceProxy Alcance, MediadorProxy Mediador)> DescargarAsync(
        string identificador, bool visible, bool existe, IReadOnlyList<Guid>? cartera = null)
    {
        var alcance = DispatchProxy.Create<IAlcanceDatosService, AlcanceProxy>();
        var dobleAlcance = (AlcanceProxy)(object)alcance;
        dobleAlcance.Visible = visible;
        dobleAlcance.Cartera = cartera;
        var mediador = DispatchProxy.Create<IMediator, MediadorProxy>();
        var dobleMediador = (MediadorProxy)(object)mediador;
        dobleMediador.Resumen = existe ? new ResumenFacturacionDto("Cliente empresarial visible", [], []) : null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IRegistroExportacionService>(new RegistroInerte());
        builder.Services.AddLocalization();
        builder.Services.AddSingleton(alcance);
        builder.Services.AddSingleton(mediador);
        await using var app = builder.Build();
        app.MapFacturacionEndpoints();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/facturacion/resumen.xlsx");
        using var scope = app.Services.CreateScope();
        var contexto = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        contexto.Request.QueryString = new QueryString($"?clienteId={identificador}&anyo=2026&mes=10");
        contexto.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(contexto);
        return (contexto, dobleAlcance, dobleMediador);
    }

    public class AlcanceProxy : DispatchProxy
    {
        public bool Visible { get; set; }
        public IReadOnlyList<Guid>? Cartera { get; set; }
        public int Consultas { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(IAlcanceDatosService.ObtenerClienteIdsVisiblesAsync))
                throw new NotSupportedException(targetMethod.Name);
            Consultas++;
            return Task.FromResult(Cartera ?? (Visible ? null : (IReadOnlyList<Guid>?)[]));
        }
    }

    public class MediadorProxy : DispatchProxy
    {
        public ResumenFacturacionDto? Resumen { get; set; }
        public List<ObtenerResumenFacturacionQuery> Consultas { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args?[0] is not ObtenerResumenFacturacionQuery consulta)
                throw new NotSupportedException(targetMethod?.Name);
            Consultas.Add(consulta);
            return Task.FromResult(Resumen);
        }
    }

    private sealed class RegistroInerte : IRegistroExportacionService
    {
        public Task RegistrarAsync(
            string entidadTipo, int filas, IReadOnlyDictionary<string, string> criterios, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
