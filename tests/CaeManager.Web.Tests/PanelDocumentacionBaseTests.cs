using Bunit;
using CaeManager.Application.Documentos.DocumentacionBase;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentacionBaseTrabajadores;
using CaeManager.Web.Features.Trabajadores.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class PanelDocumentacionBaseTests : BunitContext
{
    private static readonly Guid TrabajadorId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class MediatorDocBase(Func<ObtenerDocumentacionBaseTrabajadoresQuery, IReadOnlyDictionary<Guid, DocumentacionBaseTrabajadorDto>> responde) : IMediator
    {
        public int Consultas { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Consultas++;
            if (request is not ObtenerDocumentacionBaseTrabajadoresQuery q)
                throw new NotSupportedException(request.GetType().Name);
            return Task.FromResult((TResponse)(object)responde(q));
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    public PanelDocumentacionBaseTests()
    {
        Services.AddLocalization();
        // Con Datos ya cargados el panel no consulta; el mediador solo debe existir para la inyección.
        Services.AddSingleton<IMediator>(new MediatorDocBase(_ => throw new InvalidOperationException("no debería consultar")));
    }

    private static DocumentacionBaseTrabajadorDto Dto(params EstadoIndicadorBase[] estados) =>
        new(Enum.GetValues<TipoDocumentoBase>().Zip(estados, (t, e) => new IndicadorDocumentacionBase(t, e, null, null)).ToList());

    [Fact]
    public void Con_los_cuatro_correctos_dice_al_dia_y_lista_los_cuatro_documentos()
    {
        var cut = Render<PanelDocumentacionBase>(p => p.Add(c => c.Datos, Dto(
            EstadoIndicadorBase.Vigente, EstadoIndicadorBase.ProximoAVencer, EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente)));

        cut.Markup.Should().Contain("Al día en lo básico");
        cut.FindAll(".panel-doc-base-fila").Should().HaveCount(4);
        cut.Markup.Should().Contain("Aptitud médica").And.Contain("Formación Art. 19")
            .And.Contain("Información Art. 18").And.Contain("Entrega de EPI");
        cut.FindAll(".panel-doc-base-fila").Select(f => f.TextContent).Should().ContainSingle(f => f.Contains("Por vencer"))
            .Which.Should().Contain("Formación Art. 19");
        cut.Markup.Should().NotContain("Próximo a vencer");
    }

    [Fact]
    public void Un_indicador_vencido_o_ausente_no_es_al_dia_y_se_nombra()
    {
        var cut = Render<PanelDocumentacionBase>(p => p.Add(c => c.Datos, Dto(
            EstadoIndicadorBase.Vencido, EstadoIndicadorBase.Falta, EstadoIndicadorBase.SinConfirmar, EstadoIndicadorBase.Vigente)));

        cut.Markup.Should().Contain("Falta algo en lo básico").And.NotContain("Al día en lo básico");
        var filas = cut.FindAll(".panel-doc-base-fila").Select(f => f.TextContent).ToList();
        filas[0].Should().Contain("Vencido");
        filas[1].Should().Contain("Pendiente").And.NotContain("Falta");
        filas[2].Should().Contain("Vigencia sin confirmar");
    }

    [Fact]
    public void Una_vigencia_sin_confirmar_cuenta_como_al_dia_y_se_avisa()
    {
        var cut = Render<PanelDocumentacionBase>(p => p.Add(c => c.Datos, Dto(
            EstadoIndicadorBase.Vigente, EstadoIndicadorBase.SinConfirmar, EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente)));

        cut.Markup.Should().Contain("Al día en lo básico").And.NotContain("Falta algo en lo básico");
        cut.FindAll(".panel-doc-base-fila")[1].TextContent.Should().Contain("Vigencia sin confirmar");
        cut.Find(".panel-doc-base-aviso").TextContent.Should().Contain("vigencia sin confirmar");
    }

    [Fact]
    public void Sin_vigencias_sin_confirmar_no_hay_aviso()
    {
        var cut = Render<PanelDocumentacionBase>(p => p.Add(c => c.Datos, Dto(
            EstadoIndicadorBase.Vigente, EstadoIndicadorBase.ProximoAVencer, EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente)));

        cut.FindAll(".panel-doc-base-aviso").Should().BeEmpty();
        var compacto = Render<PanelDocumentacionBase>(p => p
            .Add(c => c.Compacto, true)
            .Add(c => c.Datos, Dto(EstadoIndicadorBase.Vigente, EstadoIndicadorBase.ProximoAVencer, EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente)));
        compacto.FindAll(".panel-doc-base-compacto-aviso").Should().BeEmpty();
        compacto.Find(".panel-doc-base-compacto").GetAttribute("aria-label").Should().Be("Documentación base: al día");
    }

    [Fact]
    public void Compacto_con_vigencia_sin_confirmar_dice_al_dia_y_lo_avisa_en_el_resumen()
    {
        var cut = Render<PanelDocumentacionBase>(p => p
            .Add(c => c.Compacto, true)
            .Add(c => c.Datos, Dto(EstadoIndicadorBase.SinConfirmar, EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente)));

        cut.Markup.Should().Contain("Al día en lo básico");
        var resumen = cut.Find(".panel-doc-base-compacto");
        resumen.GetAttribute("aria-label").Should().Be("Documentación base: al día, con vigencia sin confirmar");
        resumen.GetAttribute("title").Should().Contain("vigencia sin confirmar");
        cut.Find(".panel-doc-base-compacto-aviso").TextContent.Should().Be("sin confirmar");
        cut.FindAll(".panel-doc-base-punto-aviso").Should().HaveCount(1);
    }

    [Fact]
    public void Informacion_Art_18_vigente_se_lee_presente_y_los_demas_vigente()
    {
        var cut = Render<PanelDocumentacionBase>(p => p.Add(c => c.Datos, Dto(
            EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente)));

        var filas = cut.FindAll(".panel-doc-base-fila").Select(f => f.TextContent).ToList();
        filas[2].Should().Contain("Presente").And.NotContain("Vigente");
        filas[0].Should().Contain("Vigente");
    }

    [Fact]
    public void La_nota_no_afirma_obligatoriedad_ni_aptitud_para_un_Centro()
    {
        var cut = Render<PanelDocumentacionBase>(p => p.Add(c => c.Datos, Dto(
            EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente)));

        cut.Find(".panel-doc-base-nota").TextContent
            .Should().Contain("No indica aptitud para un Centro").And.Contain("ni que sean obligatorios para su oficio");
    }

    [Fact]
    public void Compacto_pinta_cuatro_puntos_y_el_resumen_sin_lista()
    {
        var cut = Render<PanelDocumentacionBase>(p => p
            .Add(c => c.Compacto, true)
            .Add(c => c.Datos, Dto(EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vencido, EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Falta)));

        cut.FindAll(".panel-doc-base-punto").Should().HaveCount(4);
        cut.FindAll(".panel-doc-base-punto-mal").Should().HaveCount(2);
        cut.FindAll(".panel-doc-base-lista").Should().BeEmpty();
        cut.Markup.Should().Contain("Falta algo en lo básico");
    }

    [Fact]
    public void Con_TrabajadorId_se_carga_una_sola_vez_y_con_el_Id_pedido()
    {
        var pedido = new List<Guid>();
        var mediador = new MediatorDocBase(q =>
        {
            pedido.AddRange(q.TrabajadorIds);
            return new Dictionary<Guid, DocumentacionBaseTrabajadorDto>
            {
                [TrabajadorId] = Dto(EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente, EstadoIndicadorBase.Vigente)
            };
        });
        Services.AddSingleton<IMediator>(mediador);

        var cut = Render<PanelDocumentacionBase>(p => p.Add(c => c.TrabajadorId, TrabajadorId));
        cut.Render();

        pedido.Should().Equal(TrabajadorId);
        mediador.Consultas.Should().Be(1);
        cut.Markup.Should().Contain("Al día en lo básico");
    }

    [Fact]
    public void Si_el_Trabajador_no_es_visible_no_se_pinta_nada()
    {
        Services.AddSingleton<IMediator>(new MediatorDocBase(_ => new Dictionary<Guid, DocumentacionBaseTrabajadorDto>()));
        Render<PanelDocumentacionBase>(p => p.Add(c => c.TrabajadorId, TrabajadorId)).Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Si_la_carga_falla_el_panel_no_rompe_la_pantalla()
    {
        Services.AddSingleton<IMediator>(new MediatorDocBase(_ => throw new InvalidOperationException("boom")));
        var cut = Render<PanelDocumentacionBase>(p => p.Add(c => c.TrabajadorId, TrabajadorId));
        cut.Markup.Trim().Should().BeEmpty();
    }
}
