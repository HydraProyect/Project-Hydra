using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Revisión IA, ronda 2: el visor del documento no se carga solo (cada GET de /documentos/{id}/archivo registra una Apertura),
/// el texto del diálogo de «Confirmar todos», «Confirmable» depende del umbral y el foco vuelve al disparador del desplegable.
/// </summary>
public partial class RevisionIaGen2Tests
{
    private static string RutaDelDocumento(RevisionIaDocumentoDto revision) => $"/documentos/{revision.DocumentoId}/archivo";

    private static IReadOnlyList<string> IframesDelVisor(IRenderedComponent<RevisionIaTab> cut) =>
        cut.FindAll("iframe").Select(i => i.GetAttribute("src") ?? string.Empty).ToList();

    private static async Task VerDocumentoAsync(IRenderedComponent<RevisionIaTab> cut) =>
        await Boton(cut, "Ver documento").ClickAsync(new MouseEventArgs());

    private static string MensajeDelDialogoDeLote(IRenderedComponent<RevisionIaTab> cut) => cut.FindComponents<DialogoConfirmacion>()
        .Single(d => d.Instance.Titulo == "Confirmar las lecturas de confianza alta").Instance.Mensaje;

    // ---- Visor: el documento solo se carga con «Ver documento» ----

    [Fact]
    public async Task Seleccionar_o_avanzar_no_pinta_ningun_iframe_y_las_acciones_siguen_disponibles()
    {
        var mediador = TresQueSeResuelven();
        var cut = Renderizar(mediador);

        IframesDelVisor(cut).Should().BeEmpty("abrir la pantalla no pide el documento: el endpoint registra una Apertura en cada GET");
        Boton(cut, "Ver documento");
        Boton(cut, "Aceptar detección").HasAttribute("disabled").Should().BeFalse("las acciones no necesitan abrir el documento");

        await Navegar(cut, "Siguiente (J)").ClickAsync(new MouseEventArgs());
        await SeleccionarEnDesplegableAsync(cut, "Tercera");
        await cut.InvokeAsync(() => cut.FindComponent<AtajosListaTeclado>().Instance.RecibirAtajo("k"));
        IframesDelVisor(cut).Should().BeEmpty("‹ ›, el desplegable y j/k solo seleccionan");

        await Boton(cut, "Aceptar detección").ClickAsync(new MouseEventArgs());
        IframesDelVisor(cut).Should().BeEmpty("el avance automático tampoco carga el documento de la siguiente");
        mediador.Enviadas.OfType<CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento.AplicarDeteccionIaDocumentoCommand>()
            .Should().ContainSingle("aceptar sin abrir el documento funciona");
    }

    [Fact]
    public async Task Pulsar_Ver_documento_pinta_el_iframe_del_documento_de_la_revision_seleccionada()
    {
        var segunda = Revision("Segunda", 60, Vence);
        var cut = Renderizar(new MediadorFalso { Revisiones = [Revision("Primera", 50, Vence), segunda, Revision("Tercera", 70, Vence)] });
        await SeleccionarEnDesplegableAsync(cut, "Segunda");

        await VerDocumentoAsync(cut);

        IframesDelVisor(cut).Should().ContainSingle().Which.Should().Be(RutaDelDocumento(segunda));
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Ver documento", "con el documento cargado el reposo desaparece");
    }

    public static TheoryData<string> CambiosDeSeleccion => new() { "siguiente", "anterior", "atajo-j", "atajo-k", "desplegable", "aceptar-con-avance", "codigo" };

    [Theory]
    [MemberData(nameof(CambiosDeSeleccion))]
    public async Task Cambiar_de_seleccion_devuelve_el_visor_al_reposo_y_nunca_muestra_el_documento_de_otra_revision(string cambio)
    {
        var primera = Revision("Primera", 50, Vence);
        var segunda = Revision("Segunda", 60, Vence);
        var tercera = Revision("Tercera", 70, Vence);
        var mediador = new MediadorFalso { Revisiones = [primera, segunda, tercera] };
        mediador.AlAplicar = c =>
        {
            mediador.Revisiones = mediador.Revisiones.Where(r => r.Id != c.RevisionId).ToList();
            return Result.Exito();
        };
        var cut = Renderizar(mediador);
        // «anterior» y «atajo-k» parten de la segunda; el resto, de la primera.
        if (cambio is "anterior" or "atajo-k")
        {
            await SeleccionarEnDesplegableAsync(cut, "Segunda");
        }

        var abierta = cambio is "anterior" or "atajo-k" ? segunda : primera;
        await VerDocumentoAsync(cut);
        IframesDelVisor(cut).Should().ContainSingle().Which.Should().Be(RutaDelDocumento(abierta), "control positivo: el documento de la revisión elegida está cargado");

        switch (cambio)
        {
            case "siguiente": await Navegar(cut, "Siguiente (J)").ClickAsync(new MouseEventArgs()); break;
            case "anterior": await Navegar(cut, "Anterior (K)").ClickAsync(new MouseEventArgs()); break;
            case "atajo-j": await cut.InvokeAsync(() => cut.FindComponent<AtajosListaTeclado>().Instance.RecibirAtajo("j")); break;
            case "atajo-k": await cut.InvokeAsync(() => cut.FindComponent<AtajosListaTeclado>().Instance.RecibirAtajo("k")); break;
            case "desplegable": await SeleccionarEnDesplegableAsync(cut, "Tercera"); break;
            case "aceptar-con-avance": await Boton(cut, "Aceptar detección").ClickAsync(new MouseEventArgs()); break;
            default:
                // Un cambio de selección que no pasa por un evento (no repinta solo): se repinta a mano para leer el DOM resultante.
                await CambiarSeleccionPorCodigoAsync(cut, tercera.Id);
                cut.Render();
                break;
        }

        IframesDelVisor(cut).Should().BeEmpty($"tras «{cambio}» el visor vuelve al reposo");
        Boton(cut, "Ver documento");
        cut.Markup.Should().NotContain(RutaDelDocumento(abierta), "el documento de la revisión anterior no se queda a la vista");
    }

    [Fact]
    public async Task Volver_a_una_revision_ya_vista_no_recupera_su_documento_cargado()
    {
        var primera = Revision("Primera", 50, Vence);
        var cut = Renderizar(new MediadorFalso { Revisiones = [primera, Revision("Segunda", 60, Vence)] });
        await VerDocumentoAsync(cut);

        await Navegar(cut, "Siguiente (J)").ClickAsync(new MouseEventArgs());
        await Navegar(cut, "Anterior (K)").ClickAsync(new MouseEventArgs());

        IframesDelVisor(cut).Should().BeEmpty("cada visita al documento es una petición explícita y queda registrada");
    }

    // ---- Texto del diálogo de «Confirmar todos» ----

    private const string PieDelDialogoDeLote = "Cada una aplica la fecha de emisión leída y calcula el vencimiento con la regla de su tipo de documento; queda una decisión registrada por documento a tu nombre.";

    [Fact]
    public async Task El_dialogo_de_confirmar_todos_sin_filtro_dice_cuantas_acepta_de_cuantas_pendientes()
    {
        var cut = Renderizar(new MediadorFalso { Revisiones = [Revision("Alta", 97, Vence), Revision("Media", 70, Vence), Revision("SinFecha", 99, null)] });

        await Boton(cut, "Confirmar todos los ≥95 % (1)").ClickAsync(new MouseEventArgs());

        MensajeDelDialogoDeLote(cut).Should().Be(
            $"Vas a aceptar 1 de las 3 lecturas pendientes de la cola: las de confianza de 95 % o más y fecha detectada. {PieDelDialogoDeLote} Las demás siguen pendientes.");
    }

    [Fact]
    public async Task El_dialogo_con_el_filtro_Manual_avisa_de_que_incluye_lecturas_que_el_filtro_oculta()
    {
        var (confirmable, manual, conAlerta, sinFecha) = UnaDeCada();
        var cut = Renderizar(new MediadorFalso { Revisiones = [manual, confirmable, conAlerta, sinFecha] });
        await ElegirFiltroAsync(cut, "manual");

        await Boton(cut, "Confirmar todos los ≥95 % (1)").ClickAsync(new MouseEventArgs());

        MensajeDelDialogoDeLote(cut).Should().Be(
            $"Vas a aceptar 1 de las 4 lecturas pendientes de la cola: las de confianza de 95 % o más y fecha detectada. {PieDelDialogoDeLote} Las demás siguen pendientes. " +
            "Con el filtro activo, 1 de ellas no se ven en la lista: se aceptan igual, porque este botón actúa sobre toda la cola y no solo sobre lo que muestra el filtro.");
    }

    [Fact]
    public async Task El_dialogo_con_el_filtro_Confirmables_no_dice_que_oculta_nada()
    {
        var (confirmable, manual, conAlerta, sinFecha) = UnaDeCada();
        var cut = Renderizar(new MediadorFalso { Revisiones = [manual, confirmable, conAlerta, sinFecha] });
        await ElegirFiltroAsync(cut, "confirmables");

        await Boton(cut, "Confirmar todos los ≥95 % (1)").ClickAsync(new MouseEventArgs());

        MensajeDelDialogoDeLote(cut).Should().NotContain("filtro", "con «Confirmables» el filtro no oculta ninguna de las que se aceptan");
    }

    [Fact]
    public async Task El_dialogo_con_mas_confirmables_que_el_maximo_dice_las_primeras_n_de_m()
    {
        var maximo = CaeManager.Application.Documentos.Commands.AceptarDeteccionesIaEnBloque.AceptarDeteccionesIaEnBloqueCommandHandler.MaximoRevisionesPorBloque;
        var revisiones = Enumerable.Range(0, maximo + 5).Select(i => Revision($"Doc{i}", 96, new DateOnly(2026, 9, 1))).ToList();
        var cut = Renderizar(new MediadorFalso { Revisiones = revisiones });

        await Boton(cut, $"Confirmar las primeras {maximo} de {maximo + 5} ≥95 %").ClickAsync(new MouseEventArgs());

        MensajeDelDialogoDeLote(cut).Should().Be(
            $"Vas a aceptar las primeras {maximo} de las {maximo + 5} lecturas de la IA con confianza de 95 % o más y fecha detectada: es el máximo de un bloque y el resto sigue pendiente. {PieDelDialogoDeLote}");
    }

    // ---- «Confirmable» depende del umbral ----

    [Fact]
    public async Task Una_lectura_del_58_con_fecha_no_es_confirmable_y_el_recuento_del_filtro_es_el_del_boton()
    {
        var baja = Revision("Baja", 58, Vence);
        var alta = Revision("Alta", 96, Vence);
        var cut = Renderizar(new MediadorFalso { Revisiones = [baja, alta] });

        cut.Find(".revision-ia-cab").TextContent.Should().Contain("Revisión manual").And.NotContain("Confirmable",
            "la lectura del 58 % con fecha no llega al umbral: se revisa a mano");
        await AbrirDesplegableAsync(cut);
        var recuentoDelFiltro = cut.Find("[data-filtro=confirmables] span").TextContent.Trim();
        var recuentoDeManual = cut.Find("[data-filtro=manual] span").TextContent.Trim();
        recuentoDelFiltro.Should().Be("1");
        recuentoDeManual.Should().Be("1", "la del 58 % es «Manual»: los tres filtros siguen repartiendo la cola");
        cut.FindAll("button").Should().ContainSingle(b => b.TextContent.Trim() == $"Confirmar todos los ≥95 % ({recuentoDelFiltro})");

        await cut.Find("[data-filtro=confirmables]").ClickAsync(new MouseEventArgs());
        await AbrirDesplegableAsync(cut);
        OpcionesDelDesplegable(cut).Should().Equal("Alta");
    }

    [Fact]
    public async Task La_etiqueta_Confirmable_exige_el_umbral_exacto()
    {
        var cut = Renderizar(new MediadorFalso { Revisiones = [Revision("Noventa y cuatro", 94, Vence), Revision("Noventa y cinco", 95, Vence)] });

        cut.Find(".revision-ia-cab").TextContent.Should().Contain("Revisión manual");
        await Navegar(cut, "Siguiente (J)").ClickAsync(new MouseEventArgs());
        cut.Find(".revision-ia-cab").TextContent.Should().Contain("Confirmable");
    }

    // ---- Foco del desplegable ----

    private static int FocosEnElDisparador(BunitJSInterop js) => js.Invocations.Count(i => i.Identifier.EndsWith("domWrapper.focus"));

    [Fact]
    public async Task Elegir_una_opcion_con_el_raton_devuelve_el_foco_al_disparador()
    {
        var cut = Renderizar(TresQueSeResuelven());
        await AbrirDesplegableAsync(cut);
        var antes = FocosEnElDisparador(JSInterop);

        await cut.Find("[role=option]:nth-child(3)").ClickAsync(new MouseEventArgs());

        cut.FindAll(".revision-ia-pop").Should().BeEmpty();
        FocosEnElDisparador(JSInterop).Should().Be(antes + 1, "al elegir se cierra el desplegable y el foco vuelve al disparador");
    }

    [Fact]
    public async Task Elegir_una_opcion_con_un_command_en_vuelo_cierra_el_desplegable_y_devuelve_el_foco_sin_cambiar_la_seleccion()
    {
        var inicio = new TaskCompletionSource();
        var espera = new TaskCompletionSource();
        var mediador = TresQueSeResuelven();
        mediador.Retener = r => r is CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento.AplicarDeteccionIaDocumentoCommand
            ? EsperarComandoAsync(inicio, espera)
            : null;
        var cut = Renderizar(mediador);
        var aceptar = Boton(cut, "Aceptar detección").ClickAsync(new MouseEventArgs());
        await inicio.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await AbrirDesplegableAsync(cut);
        var antes = FocosEnElDisparador(JSInterop);

        await cut.Find("[role=option]:nth-child(3)").ClickAsync(new MouseEventArgs());

        cut.FindAll(".revision-ia-pop").Should().BeEmpty("elegir cierra el desplegable aunque no cambie nada");
        Selector(cut).Should().Be("1 de 3", "la selección no cambia con un Command en vuelo");
        FocosEnElDisparador(JSInterop).Should().Be(antes + 1, "el foco vuelve al disparador también cuando la selección se rechaza");

        espera.SetResult();
        await aceptar.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
