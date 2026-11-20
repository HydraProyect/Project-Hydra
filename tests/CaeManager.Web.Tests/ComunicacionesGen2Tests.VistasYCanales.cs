using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Selector «Vista» y chips de canal de la lista (lienzo «Pulida: bandeja de la cartera», L-6 y L-10): textos, orden, iconos, cifras,
/// estado activo y filtro que cada elección manda al servidor. Solo se dibujan las vistas que <c>ObtenerConversacionesQuery</c> ya sabe
/// filtrar; las que no (Abiertas, Archivadas, Por aprobar, Cubro, Propuestas, Borradores, Pospuestas, Enviados, No deseado) quedan fuera.
/// </summary>
public partial class ComunicacionesGen2Tests
{
    /// <summary>Tres conversaciones: dos de correo y una de WhatsApp; una sin asignar, una con el plazo vencido y una esperando respuesta.</summary>
    private (IRenderedComponent<Bandeja> Cut, MediadorControlado Mediador) RenderizarConCanalesYVistas()
    {
        var escenario = new Escenario();
        var gestor = Guid.NewGuid();
        escenario.Conversaciones.AddRange([
            Conversacion("Certificado sin asignar", ClienteRefrielectric),
            Conversacion("Alta con plazo vencido", ClienteRefrielectric) with { EjecutivoAsignadoId = gestor, SlaRestante = TimeSpan.FromHours(-2) },
            Conversacion("Aviso por WhatsApp", ClienteRefrielectric, estado: EstadoConversacion.EsperandoRespuestaExterna)
                with { Canal = CanalConversacion.WhatsApp, EjecutivoAsignadoId = gestor }]);
        return Renderizar(escenario);
    }

    private static IElement DisparadorDeVista(IRenderedComponent<Bandeja> cut) => cut.Find(".bandeja-vista-disp");

    private static async Task AbrirPanelDeVistasAsync(IRenderedComponent<Bandeja> cut)
    {
        if (cut.FindAll(".bandeja-vista-panel").Count == 0) await DisparadorDeVista(cut).ClickAsync(new MouseEventArgs());
    }

    private static IReadOnlyList<string> NombresDeVista(IRenderedComponent<Bandeja> cut) =>
        cut.FindAll(".bandeja-vista-panel .bandeja-vista-fila:not(.bandeja-vista-mas) .bandeja-vista-q").Select(e => e.TextContent.Trim()).ToList();

    private static IElement FilaDeVista(IRenderedComponent<Bandeja> cut, string nombre) =>
        cut.FindAll(".bandeja-vista-panel .bandeja-vista-fila").Single(f => f.QuerySelector(".bandeja-vista-q")!.TextContent.Trim() == nombre);

    private static string? CifraDeVista(IRenderedComponent<Bandeja> cut, string nombre) =>
        FilaDeVista(cut, nombre).QuerySelector(".bandeja-vista-contador")?.TextContent.Trim();

    private static async Task ElegirVistaAsync(IRenderedComponent<Bandeja> cut, string nombre, bool enMasVistas = false)
    {
        await AbrirPanelDeVistasAsync(cut);
        if (enMasVistas && cut.Find(".bandeja-vista-mas").GetAttribute("aria-expanded") != "true")
            await cut.Find(".bandeja-vista-mas").ClickAsync(new MouseEventArgs());
        await FilaDeVista(cut, nombre).ClickAsync(new MouseEventArgs());
    }

    private static IReadOnlyList<IElement> ChipsDeCanal(IRenderedComponent<Bandeja> cut) => cut.FindAll(".bandeja-canales .bandeja-canal");

    // ---------------------------------------------------------------- disparador y panel «Vista»

    [Fact]
    public void El_disparador_de_Vista_dice_la_vista_y_el_total_y_el_panel_empieza_cerrado()
    {
        var (cut, _) = RenderizarConCanalesYVistas();

        var disparador = DisparadorDeVista(cut);
        disparador.QuerySelector(".bandeja-vista-v")!.TextContent.Trim().Should().Be("Todas · 3");
        disparador.GetAttribute("aria-label").Should().Be("Vista: Todas · 3. Cambiar de vista");
        disparador.GetAttribute("aria-haspopup").Should().Be("dialog");
        disparador.GetAttribute("aria-expanded").Should().Be("false");
        disparador.QuerySelector("svg").Should().NotBeNull("el chevron del disparador");
        cut.FindAll(".bandeja-vista-panel").Should().BeEmpty();
    }

    [Fact]
    public async Task El_panel_de_Vista_ofrece_las_vistas_principales_y_Mas_vistas_plegado()
    {
        var (cut, _) = RenderizarConCanalesYVistas();

        await DisparadorDeVista(cut).ClickAsync(new MouseEventArgs());

        DisparadorDeVista(cut).GetAttribute("aria-expanded").Should().Be("true");
        var panel = cut.Find(".bandeja-vista-panel");
        panel.GetAttribute("role").Should().Be("dialog");
        panel.GetAttribute("aria-label").Should().Be("Elegir vista");
        panel.QuerySelector(".bandeja-vista-titulo")!.TextContent.Trim().Should().Be("Vistas principales");
        NombresDeVista(cut).Should().Equal("Todas", "Mías", "Esperando respuesta del Interlocutor");
        var mas = cut.Find(".bandeja-vista-mas");
        mas.TextContent.Trim().Should().Be("Más vistas");
        mas.GetAttribute("aria-expanded").Should().Be("false");
        mas.QuerySelector("svg").Should().NotBeNull("el chevron de «Más vistas»");
    }

    [Fact]
    public async Task Mas_vistas_se_despliega_con_Sin_asignar_y_Fuera_de_plazo_y_se_pliega_otra_vez()
    {
        var (cut, _) = RenderizarConCanalesYVistas();
        await AbrirPanelDeVistasAsync(cut);

        await cut.Find(".bandeja-vista-mas").ClickAsync(new MouseEventArgs());

        cut.Find(".bandeja-vista-mas").GetAttribute("aria-expanded").Should().Be("true");
        NombresDeVista(cut).Should().Equal("Todas", "Mías", "Esperando respuesta del Interlocutor", "Sin asignar", "Fuera de plazo");

        await cut.Find(".bandeja-vista-mas").ClickAsync(new MouseEventArgs());

        NombresDeVista(cut).Should().Equal("Todas", "Mías", "Esperando respuesta del Interlocutor");
    }

    [Fact]
    public async Task La_vista_vigente_lleva_aria_current_y_la_marca_activa()
    {
        var (cut, _) = RenderizarConCanalesYVistas();

        await AbrirPanelDeVistasAsync(cut);

        FilaDeVista(cut, "Todas").GetAttribute("aria-current").Should().Be("true");
        FilaDeVista(cut, "Todas").ClassList.Should().Contain("bandeja-vista-fila-activa");
        FilaDeVista(cut, "Mías").GetAttribute("aria-current").Should().Be("false");
        FilaDeVista(cut, "Mías").ClassList.Should().NotContain("bandeja-vista-fila-activa");
        FilaDeVista(cut, "Esperando respuesta del Interlocutor").GetAttribute("aria-current").Should().Be("false");
    }

    [Fact]
    public async Task Cada_vista_con_conversaciones_muestra_su_cifra_y_Fuera_de_plazo_en_tono_de_alerta()
    {
        var (cut, _) = RenderizarConCanalesYVistas();
        await AbrirPanelDeVistasAsync(cut);
        await cut.Find(".bandeja-vista-mas").ClickAsync(new MouseEventArgs());

        CifraDeVista(cut, "Todas").Should().Be("3");
        CifraDeVista(cut, "Esperando respuesta del Interlocutor").Should().Be("1");
        CifraDeVista(cut, "Sin asignar").Should().Be("1");
        CifraDeVista(cut, "Fuera de plazo").Should().Be("1");
        FilaDeVista(cut, "Fuera de plazo").QuerySelector(".bandeja-vista-alerta").Should().NotBeNull();
        FilaDeVista(cut, "Todas").QuerySelector(".bandeja-vista-alerta").Should().BeNull("solo «Fuera de plazo» va en tono de alerta");
    }

    [Fact]
    public async Task Una_vista_sin_conversaciones_no_pinta_cifra()
    {
        var (cut, _) = RenderizarConUnaConversacion();
        await AbrirPanelDeVistasAsync(cut);
        await cut.Find(".bandeja-vista-mas").ClickAsync(new MouseEventArgs());

        CifraDeVista(cut, "Fuera de plazo").Should().BeNull("el lienzo no pinta un cero");
        CifraDeVista(cut, "Todas").Should().Be("1");
    }

    [Fact]
    public async Task El_panel_de_Vista_no_dibuja_las_vistas_que_la_query_no_sabe_filtrar_ni_las_que_D2_deja_fuera()
    {
        var (cut, _) = RenderizarConCanalesYVistas();
        await AbrirPanelDeVistasAsync(cut);
        await cut.Find(".bandeja-vista-mas").ClickAsync(new MouseEventArgs());

        var texto = cut.Find(".bandeja-vista-panel").TextContent;
        foreach (var ausente in new[]
                 {
                     "Abiertas", "Archivadas", "Por aprobar", "Cubro", "Propuestas", "Borradores", "Pospuestas", "Enviados",
                     "No deseado", "Avisos automáticos", "Resueltas", "En espera de tercero"
                 })
            texto.Should().NotContain(ausente);
    }

    [Fact]
    public async Task Esc_cierra_el_panel_de_Vista()
    {
        var (cut, _) = RenderizarConCanalesYVistas();
        await AbrirPanelDeVistasAsync(cut);

        await cut.Find(".bandeja-vista-panel").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        cut.FindAll(".bandeja-vista-panel").Should().BeEmpty();
        DisparadorDeVista(cut).GetAttribute("aria-expanded").Should().Be("false");
    }

    [Fact]
    public async Task Un_clic_fuera_cierra_el_panel_de_Vista()
    {
        var (cut, _) = RenderizarConCanalesYVistas();
        await AbrirPanelDeVistasAsync(cut);

        await cut.Find(".bandeja-vista-superposicion").ClickAsync(new MouseEventArgs());

        cut.FindAll(".bandeja-vista-panel").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- elegir una vista

    [Theory]
    [InlineData("Mías", false, true, false, false, false)]
    [InlineData("Esperando respuesta del Interlocutor", false, false, true, false, false)]
    [InlineData("Sin asignar", true, false, false, true, false)]
    [InlineData("Fuera de plazo", true, false, false, false, true)]
    public async Task Elegir_una_vista_manda_su_filtro_al_servidor_y_la_marca_como_vigente(
        string vista, bool enMasVistas, bool mias, bool esperando, bool sinAsignar, bool fueraDePlazo)
    {
        var (cut, mediador) = RenderizarConCanalesYVistas();

        await ElegirVistaAsync(cut, vista, enMasVistas);

        var consulta = ConsultasDeLista(mediador).Last();
        (consulta.SoloAsignadasAMi, consulta.SoloEsperandoCliente, consulta.SoloSinAsignar, consulta.SoloFueraDeSla)
            .Should().Be((mias, esperando, sinAsignar, fueraDePlazo));
        cut.FindAll(".bandeja-vista-panel").Should().BeEmpty("elegir una vista cierra el panel");
        DisparadorDeVista(cut).QuerySelector(".bandeja-vista-v")!.TextContent.Trim().Should().StartWith($"{vista} · ");
        cut.Find(".bandeja-lis-cab-titulo").TextContent.Trim().Should().Be(vista);

        await AbrirPanelDeVistasAsync(cut);
        if (enMasVistas) cut.Find(".bandeja-vista-mas").GetAttribute("aria-expanded").Should().Be("true", "«Más vistas» sigue desplegado al reabrir el panel");
        FilaDeVista(cut, vista).GetAttribute("aria-current").Should().Be("true");
        FilaDeVista(cut, "Todas").GetAttribute("aria-current").Should().Be("false", "las vistas son excluyentes");
    }

    [Fact]
    public async Task Esperando_respuesta_deja_en_la_lista_solo_las_que_esperan_y_el_disparador_lleva_su_total()
    {
        var (cut, _) = RenderizarConCanalesYVistas();

        await ElegirVistaAsync(cut, "Esperando respuesta del Interlocutor");

        cut.FindAll(".bandeja-lista .bandeja-fila-asunto").Select(a => a.TextContent.Trim()).Should().Equal("Aviso por WhatsApp");
        DisparadorDeVista(cut).QuerySelector(".bandeja-vista-v")!.TextContent.Trim().Should().Be("Esperando respuesta del Interlocutor · 1");
        cut.Find(".bandeja-lis-cab small").TextContent.Trim().Should().Be("1 conversación");
    }

    [Fact]
    public async Task Volver_a_Todas_quita_el_filtro_de_la_vista()
    {
        var (cut, mediador) = RenderizarConCanalesYVistas();
        await ElegirVistaAsync(cut, "Mías");

        await ElegirVistaAsync(cut, "Todas");

        var consulta = ConsultasDeLista(mediador).Last();
        (consulta.SoloAsignadasAMi, consulta.SoloEsperandoCliente, consulta.SoloSinAsignar, consulta.SoloFueraDeSla)
            .Should().Be((false, false, false, false));
        cut.Find(".bandeja-lis-cab-titulo").TextContent.Trim().Should().Be("Todas");
    }

    [Fact]
    public async Task Elegir_la_vista_vigente_no_vuelve_a_pedir_la_lista()
    {
        var (cut, mediador) = RenderizarConCanalesYVistas();
        var antes = ListasPedidas(mediador);

        await ElegirVistaAsync(cut, "Todas");

        ListasPedidas(mediador).Should().Be(antes);
    }

    // ---------------------------------------------------------------- chips de canal

    [Fact]
    public void Los_chips_de_canal_van_en_su_grupo_en_el_orden_del_lienzo_con_su_icono_y_su_cifra()
    {
        var (cut, _) = RenderizarConCanalesYVistas();

        var grupo = cut.Find(".bandeja-canales");
        grupo.GetAttribute("role").Should().Be("group");
        grupo.GetAttribute("aria-label").Should().Be("Canal");

        var chips = ChipsDeCanal(cut);
        chips.Should().HaveCount(3);

        chips[0].QuerySelector(".bandeja-canal-cifras")!.TextContent.Trim().Should().Be("3");
        chips[0].TextContent.Replace("3", "").Trim().Should().Be("Todos", "el chip «Todos» lleva texto y no icono");
        chips[0].QuerySelector("svg").Should().BeNull();
        chips[0].GetAttribute("aria-label").Should().Be("Todos los canales: 3 conversaciones");

        chips[1].QuerySelector("svg").Should().NotBeNull("Correo lleva el icono de correo");
        chips[1].QuerySelector(".bandeja-canal-cifras")!.TextContent.Trim().Should().Be("2");
        chips[1].TextContent.Replace("2", "").Trim().Should().BeEmpty("Correo no lleva texto visible");
        chips[1].GetAttribute("aria-label").Should().Be("Correo: 2 conversaciones");
        chips[1].GetAttribute("title").Should().Be("Correo: 2 conversaciones");

        chips[2].QuerySelector("svg.bandeja-canal-glifo").Should().NotBeNull("WhatsApp lleva el glifo de marca");
        chips[2].QuerySelector(".bandeja-canal-cifras")!.TextContent.Trim().Should().Be("1");
        chips[2].TextContent.Replace("1", "").Trim().Should().BeEmpty("WhatsApp no lleva texto visible");
        chips[2].GetAttribute("aria-label").Should().Be("WhatsApp: 1 conversación", "singular con una sola conversación");
    }

    [Fact]
    public void Con_Todos_activo_es_el_unico_chip_pulsado()
    {
        var (cut, _) = RenderizarConCanalesYVistas();

        ChipsDeCanal(cut).Select(c => c.GetAttribute("aria-pressed")).Should().Equal("true", "false", "false");
        cut.FindAll(".bandeja-canales .bandeja-toggle-activo").Should().ContainSingle()
            .Which.GetAttribute("aria-label").Should().StartWith("Todos los canales");
    }

    [Theory]
    [InlineData(1, CanalConversacion.Correo, "Correo: 2 conversaciones", new[] { "Certificado sin asignar", "Alta con plazo vencido" })]
    [InlineData(2, CanalConversacion.WhatsApp, "WhatsApp: 1 conversación", new[] { "Aviso por WhatsApp" })]
    public async Task Elegir_un_canal_lo_manda_al_servidor_lo_marca_activo_y_filtra_la_lista(
        int indice, CanalConversacion canal, string nombreAccesible, string[] asuntos)
    {
        var (cut, mediador) = RenderizarConCanalesYVistas();

        await ChipsDeCanal(cut)[indice].ClickAsync(new MouseEventArgs());

        ConsultasDeLista(mediador).Last().Canal.Should().Be(canal);
        ChipsDeCanal(cut).Select(c => c.GetAttribute("aria-pressed"))
            .Should().Equal(Enumerable.Range(0, 3).Select(i => i == indice ? "true" : "false"));
        cut.FindAll(".bandeja-canales .bandeja-toggle-activo").Should().ContainSingle()
            .Which.GetAttribute("aria-label").Should().Be(nombreAccesible);
        cut.FindAll(".bandeja-lista .bandeja-fila-asunto").Select(a => a.TextContent.Trim()).Should().BeEquivalentTo(asuntos);
        ChipsDeCanal(cut).Select(c => c.QuerySelector(".bandeja-canal-cifras")!.TextContent.Trim())
            .Should().Equal(["3", "2", "1"], "las cifras de cada canal no cambian al elegir uno");
    }

    [Fact]
    public async Task Volver_a_Todos_quita_el_filtro_de_canal()
    {
        var (cut, mediador) = RenderizarConCanalesYVistas();
        await ChipsDeCanal(cut)[2].ClickAsync(new MouseEventArgs());

        await ChipsDeCanal(cut)[0].ClickAsync(new MouseEventArgs());

        ConsultasDeLista(mediador).Last().Canal.Should().BeNull();
        ChipsDeCanal(cut).Select(c => c.GetAttribute("aria-pressed")).Should().Equal("true", "false", "false");
        cut.FindAll(".bandeja-lista .bandeja-fila").Should().HaveCount(3);
    }

    [Fact]
    public async Task Pulsar_el_canal_vigente_no_vuelve_a_pedir_la_lista()
    {
        var (cut, mediador) = RenderizarConCanalesYVistas();
        var antes = ListasPedidas(mediador);

        await ChipsDeCanal(cut)[0].ClickAsync(new MouseEventArgs());

        ListasPedidas(mediador).Should().Be(antes);
    }

    [Fact]
    public async Task El_canal_y_la_vista_se_combinan_en_la_misma_consulta()
    {
        var (cut, mediador) = RenderizarConCanalesYVistas();

        await ChipsDeCanal(cut)[1].ClickAsync(new MouseEventArgs());
        await ElegirVistaAsync(cut, "Sin asignar", enMasVistas: true);

        var consulta = ConsultasDeLista(mediador).Last();
        consulta.Canal.Should().Be(CanalConversacion.Correo);
        consulta.SoloSinAsignar.Should().BeTrue();
        cut.FindAll(".bandeja-lista .bandeja-fila-asunto").Select(a => a.TextContent.Trim()).Should().Equal("Certificado sin asignar");
        ChipsDeCanal(cut)[1].GetAttribute("aria-pressed").Should().Be("true");
    }

    [Fact]
    public async Task Cambiar_de_canal_vuelve_a_la_primera_pagina()
    {
        var escenario = new Escenario();
        for (var i = 1; i <= 21; i++) escenario.Conversaciones.Add(Conversacion($"Hilo {i}", ClienteRefrielectric));
        var (cut, mediador) = Renderizar(escenario);
        await cut.FindAll(".bandeja-lista .paginador-simple button").Single(b => b.TextContent.Trim() == "Siguiente").ClickAsync(new MouseEventArgs());
        ConsultasDeLista(mediador).Last().Pagina.Should().Be(2);

        await ChipsDeCanal(cut)[1].ClickAsync(new MouseEventArgs());

        ConsultasDeLista(mediador).Last().Pagina.Should().Be(1);
    }

    [Fact]
    public async Task Un_canal_sin_conversaciones_dice_Sin_resultados_y_Quitar_filtros_lo_deja_en_Todos_y_Todas()
    {
        var (cut, mediador) = RenderizarConUnaConversacion();
        await ElegirVistaAsync(cut, "Mías");
        await ChipsDeCanal(cut)[2].ClickAsync(new MouseEventArgs());

        cut.Find(".bandeja-vacio-bloque").TextContent.Should().Contain("Sin resultados");
        ChipsDeCanal(cut)[2].QuerySelector(".bandeja-canal-cifras")!.TextContent.Trim().Should().Be("0");

        await cut.Find(".bandeja-vacio-bloque button").ClickAsync(new MouseEventArgs());

        var consulta = ConsultasDeLista(mediador).Last();
        consulta.Canal.Should().BeNull();
        consulta.SoloAsignadasAMi.Should().BeFalse();
        ChipsDeCanal(cut).Select(c => c.GetAttribute("aria-pressed")).Should().Equal("true", "false", "false");
        cut.Find(".bandeja-lis-cab-titulo").TextContent.Trim().Should().Be("Todas");
        cut.FindAll(".bandeja-lista .bandeja-fila").Should().ContainSingle();
    }

    [Fact]
    public async Task Mientras_carga_otra_lista_los_chips_no_anuncian_las_cifras_de_la_anterior_y_las_nuevas_llegan_despues()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.AddRange([
            Conversacion("Certificado de correo", ClienteRefrielectric),
            Conversacion("Aviso por WhatsApp", ClienteRefrielectric) with { Canal = CanalConversacion.WhatsApp }]);
        var correo = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        escenario.Interceptar = p => p is ObtenerConversacionesQuery { Canal: CanalConversacion.Correo, TamanoPagina: > 1 } ? correo.Task : null;
        var (cut, _) = Renderizar(escenario);
        ChipsDeCanal(cut).Select(c => c.QuerySelector(".bandeja-canal-cifras")?.TextContent.Trim()).Should().Equal("2", "1", "1");

        var eleccion = ChipsDeCanal(cut)[1].ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll(".bandeja-canales .bandeja-canal-cifras").Should().BeEmpty(
            "con otra lista en camino las cifras de la anterior no se anuncian"));
        await cut.InvokeAsync(() => correo.SetResult(escenario.Pagina(new ObtenerConversacionesQuery(Canal: CanalConversacion.Correo, TamanoPagina: 20))));
        await eleccion;

        cut.WaitForAssertion(() => ChipsDeCanal(cut).Select(c => c.QuerySelector(".bandeja-canal-cifras")?.TextContent.Trim())
            .Should().Equal("2", "1", "1"));
    }

    [Fact]
    public void Si_el_recuento_de_canal_falla_la_lista_se_ve_y_los_chips_quedan_sin_cifra()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Certificado de correo", ClienteRefrielectric));
        escenario.Interceptar = p => p is ObtenerConversacionesQuery { TamanoPagina: 1 }
            ? Task.FromException<object?>(new InvalidOperationException("recuento"))
            : null;

        var (cut, _) = Renderizar(escenario);

        cut.FindAll(".bandeja-lista .bandeja-fila").Should().ContainSingle();
        cut.FindAll(".bandeja-canales .bandeja-canal-cifras").Should().BeEmpty();
        ChipsDeCanal(cut)[0].GetAttribute("aria-label").Should().Be("Todos los canales");
    }

    [Fact]
    public async Task Si_el_recuento_de_vistas_falla_el_panel_se_abre_sin_cifras()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Certificado de correo", ClienteRefrielectric));
        escenario.Interceptar = p => p is ObtenerConversacionesQuery { TamanoPagina: 1 }
            ? Task.FromException<object?>(new InvalidOperationException("recuento"))
            : null;
        var (cut, _) = Renderizar(escenario);

        await AbrirPanelDeVistasAsync(cut);

        NombresDeVista(cut).Should().Equal("Todas", "Mías", "Esperando respuesta del Interlocutor");
        cut.FindAll(".bandeja-vista-panel .bandeja-vista-contador, .bandeja-vista-panel .bandeja-vista-alerta").Should().BeEmpty();
    }

    [Fact]
    public async Task Un_clic_fuera_devuelve_el_foco_al_disparador_de_Vista()
    {
        var (cut, _) = RenderizarConCanalesYVistas();
        await AbrirPanelDeVistasAsync(cut);

        await cut.Find(".bandeja-vista-superposicion").ClickAsync(new MouseEventArgs());

        JSInterop.VerifyFocusAsyncInvoke(1);
    }

    [Fact]
    public void El_buzon_personal_no_dibuja_el_selector_de_Vista_ni_los_chips_de_canal()
    {
        var (cut, _) = RenderizarConCanalesYVistas();

        cut.FindAll(".bandeja-vista-sel, .bandeja-canales").Should().HaveCount(2);

        var personal = cut.FindAll(".bandeja-nav-item").Single(b => b.TextContent.Trim() == "Mi buzón personal");
        personal.Click();

        cut.FindAll(".bandeja-vista-sel, .bandeja-canales, .bandeja-vista-panel").Should().BeEmpty();
    }
}
