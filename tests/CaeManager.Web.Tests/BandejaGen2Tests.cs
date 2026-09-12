using Bunit;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Bandeja.Components;
using CaeManager.Web.Features.Bandeja.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using static CaeManager.Web.Tests.BandejaDatosDePrueba;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Mi trabajo» (<c>/bandeja</c>) contra el mockup <c>Mi trabajo TALVEG.dc.html</c>.
///
/// <para>
/// Lo que el mockup añade y estos casos observan: el antetítulo «Control», el
/// selector «Ordenar por» con «Fecha límite», el recuento de cada chip con el
/// <c>title</c> que dice de qué son esos números, la línea «N grupos · M tareas
/// visibles», la leyenda de atajos y la banda del grupo que bloquea el acceso a
/// un Centro. Y lo que el mockup NO pinta pero el código hace y no se puede
/// perder: que los recuentos se cuenten sobre el total y no sobre lo filtrado,
/// que el filtro viva en la URL, y que una carga superada no pise la cola ya
/// pintada.
/// </para>
/// </summary>
public class BandejaGen2Tests : BunitContext
{
    /// <summary><c>AtajosListaTeclado</c> importa <c>./js/atajos-lista.js</c> al montarse: sin modo laxo, el interop tumba todos los casos por un motivo ajeno a lo que se mide.</summary>
    public BandejaGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid Refrielectric = Guid.NewGuid();
    private static readonly Guid MontajesEbro = Guid.NewGuid();

    private (IRenderedComponent<Bandeja> Cut, MediatorDeLaBandeja Mediator) Renderizar(
        MediatorDeLaBandeja mediator, string? tipo = null)
    {
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();

        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(tipo is null ? "bandeja" : "bandeja?tipo=" + Uri.EscapeDataString(tipo));

        return (Render<Bandeja>(), mediator);
    }

    private (IRenderedComponent<Bandeja> Cut, MediatorDeLaBandeja Mediator) Renderizar(params ItemBandejaDto[] items) =>
        Renderizar(new MediatorDeLaBandeja(items));

    // --------------------------------------------------------------- cabecera

    [Fact]
    public void La_cabecera_lleva_el_antetitulo_del_grupo_de_menu_y_el_titulo_del_mockup()
    {
        var (cut, _) = Renderizar(Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A."));

        cut.Find(".cabecera-pagina-kicker").TextContent.Trim().Should().Be("Control");
        cut.Find("h1.titulo-pagina").TextContent.Trim().Should().Be("Mi trabajo");
    }

    [Fact]
    public void Reclamar_en_lote_sigue_estando_en_la_cabecera_aunque_el_mockup_no_lo_pinte()
    {
        var (cut, _) = Renderizar(Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A."));

        cut.Find(".acciones-cabecera").TextContent.Should().Contain("Reclamar en lote",
            "el mockup omite comportamiento, no lo retira: el lote es hoy la única puerta de reclamación masiva");
    }

    // ------------------------------------------------------------------ chips

    /// <summary>
    /// El mockup pone en el <c>title</c> del recuento la frase que lo explica.
    /// Sin ella, «6» junto a «Revisión IA» no dice si son seis documentos, seis
    /// personas o seis avisos — y el chip por sí solo tampoco.
    /// </summary>
    [Fact]
    public void El_recuento_de_cada_chip_explica_en_su_title_de_que_son_esos_numeros()
    {
        var (cut, _) = Renderizar(
            Item("r1", TipoItemBandeja.RevisionIa, Refrielectric, "Refrielectric S.A."),
            Item("r2", TipoItemBandeja.RevisionIa, Refrielectric, "Refrielectric S.A."),
            Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A."));

        Cuenta(cut, "Revisión IA").GetAttribute("title")
            .Should().Be("2 lecturas de la IA pendientes de confirmar o corregir");
        Cuenta(cut, "Vencido").GetAttribute("title")
            .Should().Be("1 documento que ya está fuera de vigencia",
                "en singular el texto es otro: «1 documentos» se lee como una plantilla a medio hacer");
        Cuenta(cut, "Detección de personal").GetAttribute("title")
            .Should().Be("0 altas o bajas detectadas sin confirmar");
    }

    [Fact]
    public void El_chip_activo_se_marca_con_clase_y_tambien_para_lectores_de_pantalla()
    {
        var (cut, _) = Renderizar(
            new MediatorDeLaBandeja(Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A.")),
            tipo: nameof(TipoItemBandeja.Vencido));

        var vencido = Chip(cut, "Vencido");
        vencido.GetAttribute("class").Should().Contain("bandeja-chip-activo");
        vencido.GetAttribute("aria-pressed").Should().Be("true");
        Chip(cut, "Todos").GetAttribute("aria-pressed").Should().Be("false");
    }

    [Fact]
    public void Pulsar_un_chip_escribe_el_filtro_en_la_url()
    {
        var (cut, _) = Renderizar(
            Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A."),
            Item("r1", TipoItemBandeja.RevisionIa, Refrielectric, "Refrielectric S.A."));

        Chip(cut, "Revisión IA").Click();

        Services.GetRequiredService<NavigationManager>().Uri
            .Should().Contain("tipo=" + nameof(TipoItemBandeja.RevisionIa));
        cut.Find(".bandeja-resumen-cuenta").TextContent.Trim().Should().Be("1 grupo · 1 tarea visible");
    }

    // ------------------------------------------------------------------ orden

    [Fact]
    public void El_orden_es_un_selector_con_las_dos_opciones_del_mockup()
    {
        var (cut, _) = Renderizar(Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A."));

        var opciones = cut.FindAll("#bandeja-orden option").Select(o => o.TextContent.Trim()).ToList();
        opciones.Should().Equal(["Impacto", "Fecha límite"]);
        cut.Find("label[for='bandeja-orden']").TextContent.Trim().Should().Be("Ordenar por");
    }

    /// <summary>
    /// «Impacto» ordena por severidad y tamaño; «Fecha límite», por el
    /// vencimiento más próximo de cada grupo. Son criterios distintos y tienen
    /// que dar órdenes distintos: si el selector no reordenara nada, el caso
    /// pasaría igual comprobando solo que existe.
    /// </summary>
    [Fact]
    public void Ordenar_por_fecha_limite_pone_delante_el_grupo_que_vence_antes()
    {
        var (cut, _) = Renderizar(
            Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A.", new DateOnly(2026, 12, 1)),
            Item("v2", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A.", new DateOnly(2026, 12, 2)),
            Item("v3", TipoItemBandeja.Vencido, MontajesEbro, "Montajes Ebro", new DateOnly(2026, 1, 15)));

        TitulosDeGrupo(cut).Should().Equal(["Refrielectric S.A.", "Montajes Ebro"],
            "por impacto manda el grupo con más pendientes");

        cut.Find("#bandeja-orden").Change("Fecha");

        TitulosDeGrupo(cut).Should().Equal(["Montajes Ebro", "Refrielectric S.A."],
            "por fecha límite manda el que vence antes, aunque tenga menos pendientes");
    }

    // ------------------------------------------------------- resumen y atajos

    [Fact]
    public void El_resumen_dice_cuantos_grupos_y_cuantas_tareas_se_estan_viendo()
    {
        var (cut, _) = Renderizar(
            Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A."),
            Item("v2", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A."),
            Item("v3", TipoItemBandeja.Vencido, MontajesEbro, "Montajes Ebro"));

        cut.Find(".bandeja-resumen-cuenta").TextContent.Trim().Should().Be("2 grupos · 3 tareas visibles");
    }

    /// <summary>
    /// La leyenda solo puede prometer las teclas que <c>atajos-lista.js</c>
    /// reparte de verdad. Las de la propuesta del mockup —«e» plegar, «r»
    /// resolver, «p» posponer— no están en el CatalogoAtajos ni en ese fichero:
    /// anunciarlas sería una promesa que la pantalla no cumple.
    /// </summary>
    [Fact]
    public void La_leyenda_de_atajos_solo_promete_las_teclas_que_el_codigo_sirve()
    {
        var (cut, _) = Renderizar(Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A."));

        var atajos = cut.Find(".bandeja-atajos");
        atajos.QuerySelectorAll("kbd").Select(k => k.TextContent.Trim()).Should().Equal(["j", "k", "Enter"]);
        atajos.TextContent.Should().NotContain("posponer").And.NotContain("plegar");
    }

    [Fact]
    public void Sin_cola_que_recorrer_no_se_promete_ningun_atajo()
    {
        var (cut, _) = Renderizar();

        cut.FindAll(".bandeja-atajos").Should().BeEmpty();
        cut.FindAll(".bandeja-resumen").Should().BeEmpty();
    }

    // ------------------------------------------------------------ banda de bloqueo

    [Fact]
    public void El_grupo_que_bloquea_el_acceso_a_un_Centro_lleva_banda_y_el_que_no_bloquea_no()
    {
        var (cut, _) = Renderizar(
            Item("b1", TipoItemBandeja.RequisitoPendiente, Refrielectric, "Refrielectric S.A."),
            Item("v1", TipoItemBandeja.Vencido, MontajesEbro, "Montajes Ebro"));

        var grupos = cut.FindComponents<GrupoCola>();
        grupos.Should().HaveCount(2);

        var conBanda = cut.FindAll(".grupo-cola-bloquea");
        conBanda.Should().ContainSingle();
        conBanda[0].TextContent.Should().Contain("Refrielectric S.A.");
        conBanda[0].TextContent.Should().Contain("Bloquea acceso",
            "la banda es refuerzo: el badge sigue siendo quien dice el dato, porque el color solo no comunica");
    }

    // -------------------------------------------------------------- carreras

    /// <summary>
    /// Dos cargas en vuelo: la recarga que dispara el Drawer al enviar una
    /// reclamación adelanta a la inicial. La respuesta de la superada llega
    /// después y NO puede pisar la cola ya pintada.
    ///
    /// <para>
    /// La segunda carga se lanza sin esperarla: su manejador está esperando una
    /// respuesta retenida por el test, así que <c>await</c> aquí colgaría el
    /// caso. Se guarda la tarea y se espera al final.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Una_carga_superada_no_pisa_la_cola_que_pinto_la_vigente()
    {
        var mediator = new MediatorDeLaBandeja(Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A."));
        var inicial = new TaskCompletionSource<IReadOnlyList<ItemBandejaDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recarga = new TaskCompletionSource<IReadOnlyList<ItemBandejaDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Retenidas[1] = inicial;
        mediator.Retenidas[2] = recarga;

        var (cut, _) = Renderizar(mediator);
        cut.FindAll(".esqueleto-lista").Should().ContainSingle("la carga inicial sigue retenida");

        var drawer = cut.FindComponent<DrawerReclamacionLote>();
        var segunda = cut.InvokeAsync(() => drawer.Instance.OnReclamacionEnviada.InvokeAsync());
        cut.WaitForState(() => mediator.Cargas == 2);

        recarga.SetResult([Item("v9", TipoItemBandeja.Vencido, MontajesEbro, "Montajes Ebro")]);
        await segunda;
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Montajes Ebro"));

        inicial.SetResult([Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A.")]);

        // Un resultado vacío no es una ausencia: hay que comprobar que la
        // respuesta tardía LLEGÓ a ejecutarse antes de afirmar que no pintó
        // nada. El perfil de vocabulario es la segunda consulta de cada carga,
        // así que su segundo envío es la prueba de que la superada pasó de su
        // await y se encontró con la guarda de vigencia.
        cut.WaitForState(() => mediator.Perfiles == 2);

        cut.Markup.Should().Contain("Montajes Ebro");
        cut.Markup.Should().NotContain("Refrielectric S.A.",
            "la respuesta de la carga superada contesta a una pregunta que ya nadie hizo");
    }

    /// <summary>
    /// Al retirar la pantalla, la consulta en vuelo tiene que quedarse sin
    /// token: si viajara con <c>CancellationToken.None</c>, seguiría trabajando
    /// para nadie y su respuesta llegaría a un componente ya desmontado.
    /// </summary>
    [Fact]
    public async Task Al_retirar_la_pantalla_se_cancela_la_carga_en_vuelo()
    {
        var mediator = new MediatorDeLaBandeja();
        var inicial = new TaskCompletionSource<IReadOnlyList<ItemBandejaDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Retenidas[1] = inicial;

        var (cut, _) = Renderizar(mediator);
        cut.FindAll(".esqueleto-lista").Should().ContainSingle("la carga inicial sigue retenida");

        var token = mediator.TokensDeCarga.Should().ContainSingle().Subject;
        token.CanBeCanceled.Should().BeTrue(
            "con CancellationToken.None, retirar la pantalla no cancelaría nada");
        token.IsCancellationRequested.Should().BeFalse();

        await DisposeComponentsAsync();

        token.IsCancellationRequested.Should().BeTrue();

        Func<Task> llegaTarde = () => Renderer.Dispatcher.InvokeAsync(() => inicial.SetResult([]));
        await llegaTarde.Should().NotThrowAsync("una respuesta tardía no puede tocar un componente ya retirado");
        Renderer.UnhandledException.IsCompleted.Should().BeFalse();
    }

    // ---------------------------------------------------------------- ayudas

    private static IReadOnlyList<string> TitulosDeGrupo(IRenderedComponent<Bandeja> cut) =>
        [.. cut.FindAll(".grupo-cola-titulo").Select(e => e.TextContent.Trim())];

    private static AngleSharp.Dom.IElement Chip(IRenderedComponent<Bandeja> cut, string etiqueta) =>
        cut.FindAll("button.bandeja-chip").Single(b => b.TextContent.Trim().StartsWith(etiqueta, StringComparison.Ordinal));

    private static AngleSharp.Dom.IElement Cuenta(IRenderedComponent<Bandeja> cut, string etiqueta) =>
        Chip(cut, etiqueta).QuerySelector(".bandeja-chip-cuenta")!;
}
