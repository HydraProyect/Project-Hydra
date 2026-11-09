using System.Text.RegularExpressions;
using Bunit;
using CaeManager.Application.Documentos.DocumentacionBase;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Trabajadores.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pulido de Empresa 360 y Trabajador 360 para la demo (2026-11-10). Lo que bUnit no puede medir (maquetación a 375 px) se
/// fija como invariante de la hoja de estilos; lo demás, como comportamiento del componente.
/// </summary>
public class EmpresaTrabajador360PulidoDemoTests : BunitContext
{
    private sealed class MediadorQueNoSeUsa : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    public EmpresaTrabajador360PulidoDemoTests()
    {
        Services.AddLocalization();
        Services.AddSingleton<IMediator>(new MediadorQueNoSeUsa());
    }

    private static readonly Guid Doc = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly DateOnly Fecha = new(2027, 3, 15);

    private IRenderedComponent<PanelDocumentacionBase> Panel(EstadoIndicadorBase estado, DateOnly? fecha) => Render<PanelDocumentacionBase>(p => p
        .Add(c => c.Datos, new DocumentacionBaseTrabajadorDto([new(TipoDocumentoBase.AptitudMedica, estado, Doc, fecha)]))
        .Add(c => c.OnCorregirDocumento, EventCallback.Factory.Create<Guid>(this, _ => { })));

    [Theory]
    [InlineData(EstadoIndicadorBase.Vigente)]
    [InlineData(EstadoIndicadorBase.ProximoAVencer)]
    [InlineData(EstadoIndicadorBase.Vencido)]
    public void La_fecha_de_vencimiento_sale_una_sola_vez_por_fila_en_su_columna(EstadoIndicadorBase estado)
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var cut = Panel(estado, Fecha);

        var fila = cut.Find(".panel-doc-base-fila");
        var fecha = fila.QuerySelector(".panel-doc-base-fecha")!.TextContent.Trim();
        fecha.Should().NotBeEmpty("la columna «Vence» lleva la fecha");
        Regex.Matches(fila.TextContent, Regex.Escape(fecha)).Should().HaveCount(1, "el detalle de estado ya no repite la fecha");
    }

    [Fact]
    public void El_detalle_de_estado_ya_no_lleva_fecha_y_el_resto_de_la_fila_sigue_ahi()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var cut = Panel(EstadoIndicadorBase.Vigente, Fecha);

        var fila = cut.Find(".panel-doc-base-fila");
        fila.TextContent.Should().Contain("Al día");
        fila.QuerySelector(".panel-doc-base-fecha")!.TextContent.Trim().Should().NotBeEmpty("la fecha sigue en su columna");
        fila.QuerySelectorAll("button").Should().BeEmpty("lo vigente no pide trabajo");
    }

    [Fact]
    public void Un_indicador_que_falta_lleva_su_Gestionar_como_boton_secundario_de_fila()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var cut = Render<PanelDocumentacionBase>(p => p
            .Add(c => c.Datos, new DocumentacionBaseTrabajadorDto([new(TipoDocumentoBase.AptitudMedica, EstadoIndicadorBase.Falta, null, null)]))
            .Add(c => c.OnSubirFaltante, EventCallback.Factory.Create(this, () => { })));

        var boton = cut.Find(".panel-doc-base-fila button");
        boton.TextContent.Trim().Should().Be("Gestionar");
        boton.ClassList.Should().Contain("boton-secundario").And.Contain("accion-gestionar-documento");
        boton.HasAttribute("data-accion-fila").Should().BeTrue("queda marcado como acción de la fila, como el resto de «Gestionar»");
    }

    // ── Invariantes de hoja de estilos (maquetación a 375 px; bUnit no la calcula) ──

    private static string Css(params string[] ruta) => File.ReadAllText(Path.Combine([RaizDelRepositorio(), "src", "CaeManager.Web", "Components", "DesignSystem", .. ruta]));

    [Fact]
    public void El_boton_de_pestana_es_el_bloque_contenedor_de_su_glosa_absoluta()
    {
        var css = Css("Pestanas.razor.css");

        Regex.IsMatch(css, @"\.pestanas-boton\s*\{[^}]*position:\s*relative;").Should().BeTrue(
            "sin él la glosa de 1 px, position: absolute, se coloca respecto a la página y alarga el scroll horizontal");
        Regex.IsMatch(css, @"\.pestanas-contador-glosa\s*\{[^}]*position:\s*absolute;").Should().BeTrue();
    }

    [Fact]
    public void Cuerpo_con_lateral_a_una_columna_usa_minmax_cero_para_no_crecer_con_su_contenido()
    {
        var css = Css("CuerpoConLateral.razor.css");
        var movil = css[css.IndexOf("@media (max-width: 900px)", StringComparison.Ordinal)..];

        Regex.IsMatch(movil, @"\.cuerpo-con-lateral\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\);").Should().BeTrue();
        Regex.IsMatch(movil, @"grid-template-columns:\s*1fr;").Should().BeFalse("«1fr» es minmax(auto, 1fr) y crece hasta el contenido más ancho");
    }

    [Fact]
    public void El_titulo_de_la_cabecera_se_corta_en_dos_lineas_y_las_acciones_saltan_de_fila_antes_de_apretarlo()
    {
        var css = Css("CabeceraIdentidad.razor.css");

        Regex.IsMatch(css, @"\.cabecera-identidad-titulo\s*\{[^}]*-webkit-line-clamp:\s*2;[^}]*overflow:\s*hidden;").Should().BeTrue();
        Regex.IsMatch(css, @"\.cabecera-identidad\s*\{[^}]*flex-wrap:\s*wrap;").Should().BeTrue();
        Regex.IsMatch(css, @"\.cabecera-identidad-texto\s*\{[^}]*min-width:\s*min\(100%,\s*22rem\);").Should().BeTrue();
    }

    [Fact]
    public void El_h1_de_la_cabecera_lleva_el_nombre_completo_en_su_title()
    {
        var cut = Render<CaeManager.Web.Components.DesignSystem.CabeceraIdentidad>(p => p.Add(c => c.Titulo, "Razón social larguísima de la Empresa contratista, S.L."));

        cut.Find("h1.cabecera-identidad-titulo").GetAttribute("title").Should().Be("Razón social larguísima de la Empresa contratista, S.L.");
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("No se encontró CaeManager.slnx subiendo desde el directorio de pruebas.");
    }
}
