using Bunit;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Bandeja.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P9 (2026-09-18, CAPA-USUARIO-AVANZADO-TALVEG.md § 6.1 quinquies): «solo la
/// vigencia es copiable». <see cref="PanelResolverItem"/> es el único sitio de
/// la Bandeja del gestor que envuelve <c>Item.Fecha</c> en
/// <see cref="TextoFechaCopiable"/>, y hasta este cambio lo hacía sin mirar el
/// tipo — comportamiento documentado como hallazgo, no corregido allí a
/// propósito («la implementación la revisa la sesión de código
/// correspondiente»).
///
/// <para>
/// Renderiza el componente directamente en vez de pasar por
/// <c>TipoItemBandejaUiTests</c> (que solo prueba el gate en aislado, sin DOM)
/// ni por la página <c>/bandeja</c> entera: la propiedad que hace falta
/// demostrar es que <TextoFechaCopiable/> aparece o no según el tipo, y eso se
/// ve en el marcado, no en un booleano.
/// </para>
/// </summary>
public class PanelResolverItemTests : BunitContext
{
    public PanelResolverItemTests()
    {
        Services.AddSingleton<ContextWorkspaceService>();
        Services.AddSingleton<ToastService>();
        // TextoFechaCopiable importa clipboard.js en OnAfterRenderAsync — solo
        // se monta para los tipos que siguen siendo copiables; el módulo se
        // deja pasar en vez de mockearlo entero, igual que GrupoColaTests.
        JSInterop.SetupModule("./js/clipboard.js");
    }

    private static ItemBandejaDto Item(TipoItemBandeja tipo, DateOnly fecha) => new(
        Id: "item-1", Tipo: tipo, Titulo: "t", Subtitulo: "s", Fecha: fecha,
        TrabajadorId: null, CentroId: null, DocumentoId: null, TipoDocumentoId: null, RequisitoId: null);

    [Fact]
    public void Vencido_es_copiable_porque_su_fecha_es_vigencia()
    {
        var cut = Render<PanelResolverItem>(p => p.Add(c => c.Item, Item(TipoItemBandeja.Vencido, new DateOnly(2026, 8, 2))));

        var boton = cut.Find(".panel-resolver-item-fecha button.texto-fecha-copiable");
        boton.TextContent.Trim().Should().Be("02/08/2026");
    }

    [Fact]
    public void RevisionIa_es_copiable_por_la_excepcion_acotada_de_P9()
    {
        var cut = Render<PanelResolverItem>(p => p.Add(c => c.Item, Item(TipoItemBandeja.RevisionIa, new DateOnly(2026, 8, 2))));

        cut.Find(".panel-resolver-item-fecha button.texto-fecha-copiable").TextContent.Trim().Should().Be("02/08/2026");
    }

    /// <summary>
    /// El defecto real que documentó el hallazgo: <c>FechaInicio</c> no es
    /// vigencia. Antes de este cambio, esto renderizaba un
    /// <c>button.texto-fecha-copiable</c> igual que Vencido.
    /// </summary>
    [Fact]
    public void VisitaUrgente_no_es_copiable_porque_su_fecha_es_inicio_no_vigencia()
    {
        var cut = Render<PanelResolverItem>(p => p.Add(c => c.Item, Item(TipoItemBandeja.VisitaUrgente, new DateOnly(2026, 8, 2))));

        cut.FindAll("button.texto-fecha-copiable").Should().BeEmpty();
        cut.Find(".panel-resolver-item-fecha span").TextContent.Trim().Should().Be("02/08/2026");
    }

    [Fact]
    public void SugerenciaVisitaUrgente_no_es_copiable_porque_su_fecha_es_inicio_sugerida_no_vigencia()
    {
        var cut = Render<PanelResolverItem>(
            p => p.Add(c => c.Item, Item(TipoItemBandeja.SugerenciaVisitaUrgente, new DateOnly(2026, 8, 2))));

        cut.FindAll("button.texto-fecha-copiable").Should().BeEmpty();
        cut.Find(".panel-resolver-item-fecha span").TextContent.Trim().Should().Be("02/08/2026");
    }
}
