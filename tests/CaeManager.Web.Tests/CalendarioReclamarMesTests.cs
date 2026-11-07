using Bunit;
using CaeManager.Application.Calendario.Queries;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Application.Visitas.Queries.ObtenerVisitasParaCalendario;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Bandeja.Components;
using CaeManager.Web.Features.Calendario.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Detalle del día» de la vista Mes con «Reclamar» y «Reclamar los N» (maqueta «Calendario TALVEG», decisión de 2026-11-07).
/// El drawer de lote es de un único titular: con una Empresa el pie es «Reclamar los N»; con varias, un botón por Empresa con
/// su recuento. Qué es reclamable lo decide la consulta del Mes (misma regla que la Semana por Empresa), no esta pantalla.
/// </summary>
public class CalendarioReclamarMesTests : BunitContext
{
    private static readonly DateOnly Hoy = new(2026, 3, 15);
    private static readonly DateOnly Dia10 = new(2026, 3, 10);

    private static readonly Guid EbroId = Guid.NewGuid();
    private static readonly Guid DueroId = Guid.NewGuid();

    private readonly List<object> _recibidas = [];

    public CalendarioReclamarMesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
    }

    private static VencimientoCalendarioDto Venc(Guid empresaId, string empresa, string trabajador, bool reclamable = true) =>
        new(Guid.NewGuid(), Dia10, trabajador, "Formación PRL", EstadoDocumento.Proximo, empresaId, empresa, reclamable);

    private IRenderedComponent<Calendario> Renderizar(string rol, params VencimientoCalendarioDto[] vencimientos)
    {
        AddAuthorization().SetAuthorized("marta").SetRoles(rol);
        Services.AddScoped<IMediator>(_ => new Mediador(_recibidas, vencimientos));
        var cut = Render<Calendario>(p => p.Add(c => c.Hoy, Hoy));
        cut.FindAll("button.calendario-celda")
            .Single(b => b.GetAttribute("aria-label")!.StartsWith("10 de ", StringComparison.Ordinal)).Click();
        return cut;
    }

    private static IReadOnlyList<string> BotonesDelPie(IRenderedComponent<Calendario> cut) =>
        [.. cut.FindAll(".modal-pie button").Select(b => b.TextContent.Trim())];

    private static void ComprobarDrawer(IRenderedComponent<Calendario> cut, Guid empresaId, params Guid[] documentos)
    {
        var drawer = cut.FindComponent<DrawerReclamacionLote>().Instance;
        drawer.TitularIdInicial.Should().Be(empresaId);
        drawer.DocumentoIdsIniciales.Should().BeEquivalentTo(documentos);
        cut.FindAll("[role=dialog] .detalle-dia-fila").Should().BeEmpty("el detalle del día se cierra al abrir el cajón");
    }

    [Fact]
    public void Con_una_Empresa_cada_fila_ofrece_Reclamar_y_el_pie_es_Reclamar_los_N()
    {
        var ana = Venc(EbroId, "Montajes Ebro", "Ana Gil");
        var luis = Venc(EbroId, "Montajes Ebro", "Luis Pardo");
        var cut = Renderizar(Roles.GestorCae, ana, luis);

        cut.FindAll("[role=dialog] .detalle-dia-reclamar").Should().HaveCount(2);
        BotonesDelPie(cut).Should().Equal("Reclamar los 2");
        cut.Find("[role=dialog]").TextContent.Should().NotContain("Montajes Ebro",
            "con una sola Empresa el día no repite su nombre en cada fila");

        cut.Find(".modal-pie button").Click();

        ComprobarDrawer(cut, EbroId, ana.DocumentoId, luis.DocumentoId);
    }

    [Fact]
    public void Una_Empresa_visible_y_un_vencimiento_sin_Empresa_no_cuentan_como_varias_Empresas()
    {
        var ana = Venc(EbroId, "Montajes Ebro", "Ana Gil");
        var sinEmpresa = new VencimientoCalendarioDto(Guid.NewGuid(), Dia10, "Eva Ruiz", "Formación PRL", EstadoDocumento.Proximo);
        var cut = Renderizar(Roles.GestorCae, ana, sinEmpresa);

        BotonesDelPie(cut).Should().Equal("Reclamar el documento");
        cut.Find("[role=dialog]").TextContent.Should().NotContain("Montajes Ebro",
            "un vencimiento sin Empresa visible no convierte el día en uno de varias Empresas");
    }

    [Fact]
    public void Con_dos_Empresas_el_pie_trae_un_boton_por_Empresa_con_su_recuento()
    {
        var ana = Venc(EbroId, "Montajes Ebro", "Ana Gil");
        var luis = Venc(EbroId, "Montajes Ebro", "Luis Pardo");
        var eva = Venc(DueroId, "Estructuras Duero", "Eva Ruiz");
        var cut = Renderizar(Roles.CoordinadorCae, ana, luis, eva);

        BotonesDelPie(cut).Should().Equal("Reclamar el documento · Estructuras Duero", "Reclamar los 2 · Montajes Ebro");
        cut.FindAll("[role=dialog] .detalle-dia-secundaria").Select(e => e.TextContent)
            .Should().Contain("Formación PRL · Estructuras Duero", "con varias Empresas cada fila dice de cuál es");

        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Contains("Montajes Ebro")).Click();

        ComprobarDrawer(cut, EbroId, ana.DocumentoId, luis.DocumentoId);
    }

    [Fact]
    public void Reclamar_en_una_fila_abre_el_cajon_solo_con_ese_documento_de_su_Empresa()
    {
        var ana = Venc(EbroId, "Montajes Ebro", "Ana Gil");
        var eva = Venc(DueroId, "Estructuras Duero", "Eva Ruiz");
        var cut = Renderizar(Roles.DireccionCae, ana, eva);

        cut.FindAll("[role=dialog] .detalle-dia-fila")
            .Single(f => f.TextContent.Contains("Eva Ruiz")).QuerySelector(".detalle-dia-reclamar")!.Click();

        ComprobarDrawer(cut, DueroId, eva.DocumentoId);
    }

    [Fact]
    public void Lo_no_reclamable_no_lleva_Reclamar_ni_cuenta_en_el_pie()
    {
        var reclamable = Venc(EbroId, "Montajes Ebro", "Ana Gil");
        var ajeno = Venc(DueroId, "Estructuras Duero", "Eva Ruiz", reclamable: false);
        var cut = Renderizar(Roles.GestorCae, reclamable, ajeno);

        cut.FindAll("[role=dialog] .detalle-dia-fila").Single(f => f.TextContent.Contains("Eva Ruiz"))
            .QuerySelector(".detalle-dia-reclamar").Should().BeNull();
        BotonesDelPie(cut).Should().Equal("Reclamar el documento");

        cut.Find(".modal-pie button").Click();

        ComprobarDrawer(cut, EbroId, reclamable.DocumentoId);
    }

    [Fact]
    public void Un_dia_sin_nada_reclamable_no_tiene_pie()
    {
        var cut = Renderizar(Roles.GestorCae, Venc(EbroId, "Montajes Ebro", "Ana Gil", reclamable: false));

        cut.FindAll("[role=dialog] .detalle-dia-reclamar").Should().BeEmpty();
        cut.FindAll(".modal-pie").Should().BeEmpty();
    }

    [Fact]
    public void Consulta_ve_el_dia_sin_Reclamar_ni_pie_aunque_la_consulta_diga_reclamable()
    {
        var cut = Renderizar(Roles.Consulta, Venc(EbroId, "Montajes Ebro", "Ana Gil"));

        cut.FindAll("[role=dialog] .detalle-dia-reclamar").Should().BeEmpty();
        cut.FindAll(".modal-pie").Should().BeEmpty();
    }

    private sealed class Mediador(List<object> recibidas, IReadOnlyList<VencimientoCalendarioDto> vencimientos) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            recibidas.Add(request);
            object respuesta = request switch
            {
                ObtenerVencimientosMesQuery => vencimientos,
                ObtenerVisitasParaCalendarioQuery => Array.Empty<VisitaCalendarioDto>(),
                ObtenerLoteReclamacionPorFiltroQuery => Array.Empty<LoteReclamacionAgrupadoDto>(),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }
}
