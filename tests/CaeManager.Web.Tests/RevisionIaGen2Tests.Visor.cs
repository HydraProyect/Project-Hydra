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
/// Revisión IA, ronda 2: el visor carga solo el documento de la revisión seleccionada (D-49, opción C) por la ruta de la
/// revisión, que el servidor registra como «Revisión IA» una vez por persona y Documento; «Descargar» sigue en
/// /documentos/{id}/archivo (Apertura). También el texto del diálogo de «Confirmar todos», «Confirmable» según el umbral y
/// el foco del desplegable.
/// </summary>
public partial class RevisionIaGen2Tests
{
    /// <summary>La ruta del visor: la de la revisión, no la del Documento.</summary>
    private static string RutaDelVisor(RevisionIaDocumentoDto revision) => $"/documentos/revision-ia/{revision.Id}/archivo";

    private static string RutaDelDocumento(RevisionIaDocumentoDto revision) => $"/documentos/{revision.DocumentoId}/archivo";

    private static IReadOnlyList<string> IframesDelVisor(IRenderedComponent<RevisionIaTab> cut) =>
        cut.FindAll("iframe").Select(i => i.GetAttribute("src") ?? string.Empty).ToList();

    private static string MensajeDelDialogoDeLote(IRenderedComponent<RevisionIaTab> cut, string titulo = "Confirmar las lecturas de confianza alta") =>
        cut.FindComponents<DialogoConfirmacion>().Single(d => d.Instance.Titulo == titulo).Instance.Mensaje;

    // ---- Visor: el documento de la seleccionada se carga solo ----

    [Fact]
    public void Abrir_la_pantalla_carga_el_documento_de_la_primera_revision_por_la_ruta_de_la_revision()
    {
        var primera = Revision("Primera", 50, Vence);
        var cut = Renderizar(new MediadorFalso { Revisiones = [primera, Revision("Segunda", 60, Vence)] });

        IframesDelVisor(cut).Should().ContainSingle().Which.Should().Be(RutaDelVisor(primera),
            "D-49: el documento se carga sin pulsar nada, por la ruta que lo registra como «Revisión IA»");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Ver documento", "ya no hay estado de reposo");
        cut.FindAll(".revision-ia-pdf-reposo").Should().BeEmpty();
    }

    [Fact]
    public void El_visor_nunca_pide_la_ruta_del_Documento_y_Descargar_sigue_en_ella()
    {
        var primera = Revision("Primera", 50, Vence);
        var cut = Renderizar(new MediadorFalso { Revisiones = [primera] });

        IframesDelVisor(cut).Should().NotContain(RutaDelDocumento(primera), "esa ruta registra una Apertura en cada GET");
        cut.FindAll("a").Where(a => a.TextContent.Trim() == "Descargar").Select(a => a.GetAttribute("href"))
            .Should().ContainSingle().Which.Should().Be(RutaDelDocumento(primera), "«Descargar» sigue siendo una Apertura");
    }

    [Fact]
    public async Task Seleccionar_carga_el_documento_de_la_revision_seleccionada_y_las_acciones_siguen_disponibles()
    {
        var segunda = Revision("Segunda", 60, Vence);
        var tercera = Revision("Tercera", 70, Vence);
        var mediador = new MediadorFalso { Revisiones = [Revision("Primera", 50, Vence), segunda, tercera] };
        var cut = Renderizar(mediador);

        await SeleccionarEnDesplegableAsync(cut, "Segunda");
        IframesDelVisor(cut).Should().ContainSingle().Which.Should().Be(RutaDelVisor(segunda));

        await Navegar(cut, "Siguiente (J)").ClickAsync(new MouseEventArgs());
        IframesDelVisor(cut).Should().ContainSingle().Which.Should().Be(RutaDelVisor(tercera));
        Boton(cut, "Aceptar detección").HasAttribute("disabled").Should().BeFalse("las acciones no esperan al documento");
    }

    public static TheoryData<string> CambiosDeSeleccion => new() { "siguiente", "anterior", "atajo-j", "atajo-k", "desplegable", "aceptar-con-avance", "codigo" };

    [Theory]
    [MemberData(nameof(CambiosDeSeleccion))]
    public async Task Cambiar_de_seleccion_carga_el_documento_de_la_nueva_y_nunca_deja_el_de_otra_revision(string cambio)
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
        IframesDelVisor(cut).Should().ContainSingle().Which.Should().Be(RutaDelVisor(abierta), "control positivo: el documento de la revisión elegida está cargado");

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

        var esperada = cambio switch
        {
            "siguiente" or "atajo-j" => segunda,
            "anterior" or "atajo-k" => primera,
            "desplegable" or "codigo" => tercera,
            _ => null,
        };
        var visor = IframesDelVisor(cut).Should().ContainSingle($"tras «{cambio}» hay un visor, el de la nueva selección").Which;
        visor.Should().NotBe(RutaDelVisor(abierta), "el documento de la revisión anterior no se queda a la vista");
        if (esperada is not null)
            visor.Should().Be(RutaDelVisor(esperada));
        else
            visor.Should().BeOneOf(RutaDelVisor(segunda), RutaDelVisor(tercera), "el avance pasa a otra pendiente");
        cut.Markup.Should().NotContain(RutaDelVisor(abierta));
    }

    [Fact]
    public async Task Volver_a_una_revision_ya_vista_vuelve_a_cargar_su_documento_por_su_ruta()
    {
        var primera = Revision("Primera", 50, Vence);
        var cut = Renderizar(new MediadorFalso { Revisiones = [primera, Revision("Segunda", 60, Vence)] });

        await Navegar(cut, "Siguiente (J)").ClickAsync(new MouseEventArgs());
        await Navegar(cut, "Anterior (K)").ClickAsync(new MouseEventArgs());

        IframesDelVisor(cut).Should().ContainSingle().Which.Should().Be(RutaDelVisor(primera),
            "la nueva petición no añade fila: el servidor deja una por persona y Documento");
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
            "Con el filtro activo, 1 de ellas no se ve en la lista: se acepta igual, porque este botón actúa sobre toda la cola y no solo sobre lo que muestra el filtro.");
    }

    [Fact]
    public async Task El_dialogo_con_una_sola_lectura_dice_la_unica_y_no_habla_de_las_demas()
    {
        var cut = Renderizar(new MediadorFalso { Revisiones = [Revision("Unica", 97, Vence)] });

        await Boton(cut, "Confirmar todos los ≥95 % (1)").ClickAsync(new MouseEventArgs());

        var mensaje = MensajeDelDialogoDeLote(cut);
        mensaje.Should().Be("Vas a aceptar la única lectura pendiente de la cola: tiene confianza de 95 % o más y fecha detectada. " +
            "Aplica la fecha de emisión leída y calcula el vencimiento con la regla de su tipo de documento; queda una decisión registrada a tu nombre.");
        mensaje.Should().NotContain("1 de las 1").And.NotContain("demás");
    }

    [Fact]
    public async Task El_dialogo_cuando_se_confirman_todas_las_pendientes_omite_la_frase_de_las_demas()
    {
        var cut = Renderizar(new MediadorFalso { Revisiones = [Revision("Alta", 97, Vence), Revision("Otra", 96, Vence)] });

        await Boton(cut, "Confirmar todos los ≥95 % (2)").ClickAsync(new MouseEventArgs());

        var mensaje = MensajeDelDialogoDeLote(cut);
        mensaje.Should().Be($"Vas a aceptar las 2 lecturas pendientes de la cola: las de confianza de 95 % o más y fecha detectada. {PieDelDialogoDeLote}");
        mensaje.Should().NotContain("2 de las 2").And.NotContain("demás");
    }

    [Fact]
    public async Task El_dialogo_con_mas_confirmables_que_el_maximo_y_el_filtro_Manual_cuenta_las_ocultas_sobre_las_que_se_envian()
    {
        var maximo = CaeManager.Application.Documentos.Commands.AceptarDeteccionesIaEnBloque.AceptarDeteccionesIaEnBloqueCommandHandler.MaximoRevisionesPorBloque;
        var revisiones = new List<RevisionIaDocumentoDto> { Revision("Manual", 60, Vence) };
        revisiones.AddRange(Enumerable.Range(0, maximo + 5).Select(i => Revision($"Doc{i}", 96, new DateOnly(2026, 9, 1))));
        var mediador = new MediadorFalso { Revisiones = revisiones };
        var cut = Renderizar(mediador);
        await ElegirFiltroAsync(cut, "manual");

        await Boton(cut, $"Confirmar las primeras {maximo} de {maximo + 5} ≥95 %").ClickAsync(new MouseEventArgs());

        MensajeDelDialogoDeLote(cut).Should().Be(
            $"Vas a aceptar las primeras {maximo} de las {maximo + 5} lecturas de la IA con confianza de 95 % o más y fecha detectada: es el máximo de un bloque y el resto sigue pendiente. {PieDelDialogoDeLote} " +
            $"Con el filtro activo, {maximo} de ellas no se ven en la lista: se aceptan igual, porque este botón actúa sobre toda la cola y no solo sobre lo que muestra el filtro.",
            "las ocultas son las que se envían (recortadas al máximo), no las 205 de la cola");
    }

    private const string TituloDelDialogoDeLoteEnCatalan = "Confirma les lectures de confiança alta";
    private const string PieDelDialogoDeLoteEnCatalan = "Cadascuna aplica la data d'emissió llegida i calcula el venciment amb la regla del seu tipus de document; queda una decisió registrada per document a nom teu.";

    private static async Task ConCulturaCatalanaAsync(Func<Task> prueba)
    {
        var anterior = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("ca-ES");
            await prueba();
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Fact]
    public async Task El_dialogo_de_confirmar_todos_se_dice_en_catalan_sin_filtro()
    {
        await ConCulturaCatalanaAsync(async () =>
        {
            var cut = Renderizar(new MediadorFalso { Revisiones = [Revision("Alta", 97, Vence), Revision("Media", 70, Vence), Revision("SinFecha", 99, null)] });

            await Boton(cut, "Confirmar tots els ≥95 % (1)").ClickAsync(new MouseEventArgs());

            MensajeDelDialogoDeLote(cut, TituloDelDialogoDeLoteEnCatalan).Should().Be(
                $"Acceptaràs 1 de les 3 lectures pendents de la cua: les de confiança de 95 % o més i data detectada. {PieDelDialogoDeLoteEnCatalan} Les altres continuen pendents.");
        });
    }

    [Fact]
    public async Task El_dialogo_de_confirmar_todos_en_catalan_con_una_oculta_usa_el_singular()
    {
        await ConCulturaCatalanaAsync(async () =>
        {
            var (confirmable, manual, conAlerta, sinFecha) = UnaDeCada();
            var cut = Renderizar(new MediadorFalso { Revisiones = [manual, confirmable, conAlerta, sinFecha] });
            await ElegirFiltroAsync(cut, "manual");

            await Boton(cut, "Confirmar tots els ≥95 % (1)").ClickAsync(new MouseEventArgs());

            MensajeDelDialogoDeLote(cut, TituloDelDialogoDeLoteEnCatalan).Should().Be(
                $"Acceptaràs 1 de les 4 lectures pendents de la cua: les de confiança de 95 % o més i data detectada. {PieDelDialogoDeLoteEnCatalan} Les altres continuen pendents. " +
                "Amb el filtre actiu, 1 d'aquestes no es veu a la llista: s'accepta igualment, perquè aquest botó actua sobre tota la cua i no només sobre el que mostra el filtre.");
        });
    }

    [Fact]
    public async Task El_dialogo_en_catalan_con_una_sola_lectura_dice_lunica()
    {
        await ConCulturaCatalanaAsync(async () =>
        {
            var cut = Renderizar(new MediadorFalso { Revisiones = [Revision("Unica", 97, Vence)] });

            await Boton(cut, "Confirmar tots els ≥95 % (1)").ClickAsync(new MouseEventArgs());

            MensajeDelDialogoDeLote(cut, TituloDelDialogoDeLoteEnCatalan).Should().Be(
                "Acceptaràs l'única lectura pendent de la cua: té confiança de 95 % o més i data detectada. " +
                "Aplica la data d'emissió llegida i calcula el venciment amb la regla del seu tipus de document; queda una decisió registrada a nom teu.");
        });
    }

    [Fact]
    public async Task Reintentar_tras_un_error_de_recarga_vuelve_a_mostrar_el_documento_de_la_seleccionada()
    {
        var manual = Revision("Manual", 60, Vence);
        var confirmable = Revision("Confirmable", 97, Vence);
        var mediador = new MediadorFalso { Revisiones = [manual, confirmable] };
        var recargaCaida = false;
        mediador.Retener = peticion => recargaCaida && peticion is ObtenerRevisionesIaPendientesQuery
            ? Task.FromException(new InvalidOperationException("la recarga falla"))
            : null;
        var cut = Renderizar(mediador);
        IframesDelVisor(cut).Should().ContainSingle().Which.Should().Be(RutaDelVisor(manual), "control positivo: el documento está abierto");

        recargaCaida = true;
        await ConfirmarTodosAsync(cut, "Confirmar todos los ≥95 % (1)");
        Boton(cut, "Reintentar");
        recargaCaida = false;
        await Boton(cut, "Reintentar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".revision-ia-lateral").Should().NotBeEmpty("la cola se recuperó y la selección sigue ahí");
        IframesDelVisor(cut).Should().ContainSingle("tras Reintentar el visor vuelve con la revisión seleccionada")
            .Which.Should().StartWith("/documentos/revision-ia/");
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
