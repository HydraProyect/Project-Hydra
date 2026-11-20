using Bunit;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CaeManager.Web.Tests;

/// <summary>
/// El visor de documento a pantalla completa (tablero «Visor-Documento» del lienzo de Comunicaciones): diálogo modal con
/// cabecera, cola de adjuntos, miniaturas y hojas. Fija lo que la pantalla promete (rótulos, foco, teclado, cierre, zoom y giro)
/// y que no enlaza direcciones que no sean del propio sitio. La trampa de foco real y el desplazamiento viven en JS y los
/// prueba el navegador, no bUnit.
/// </summary>
public class VisorDocumentoPantallaCompletaTests : BunitContext
{
    private static readonly TimeSpan Plazo = TimeSpan.FromSeconds(10);

    private static AdjuntoVisor Adj(string nombre, int paginas = 1, EstadoAdjuntoVisor estado = EstadoAdjuntoVisor.Pendiente, string? url = null) =>
        new(Guid.NewGuid(), nombre, url ?? $"/comunicaciones/adjuntos/{Guid.NewGuid()}/archivo", paginas, "312 KB", estado);

    private IRenderedComponent<VisorDocumentoPantallaCompleta> Pintar(
        IReadOnlyList<AdjuntoVisor> lista, Action<bool>? alCambiar = null, int indice = 0, bool visible = true)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        return Render<VisorDocumentoPantallaCompleta>(p => p
            .Add(x => x.Visible, visible)
            .Add(x => x.Adjuntos, lista)
            .Add(x => x.IndiceInicial, indice)
            .Add(x => x.VisibleChanged, EventCallback.Factory.Create<bool>(this, v => alCambiar?.Invoke(v))));
    }

    private static Task TeclaAsync(AngleSharp.Dom.IElement elemento, string tecla) =>
        elemento.KeyDownAsync(new KeyboardEventArgs { Key = tecla });

    private static string Posicion(IRenderedComponent<VisorDocumentoPantallaCompleta> cut) => cut.Find(".vz-cola-n").TextContent.Trim();

    private static string Zoom(IRenderedComponent<VisorDocumentoPantallaCompleta> cut) => cut.Find(".vz-pct").TextContent.Trim();

    [Fact]
    public void Cerrado_o_sin_adjuntos_no_pinta_nada()
    {
        var cerrado = Pintar([Adj("Certificado.pdf")], visible: false);
        cerrado.Markup.Trim().Should().BeEmpty();

        var vacio = Render<VisorDocumentoPantallaCompleta>(p => p.Add(x => x.Visible, true));
        vacio.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Abierto_es_un_dialogo_modal_rotulado_por_su_titulo()
    {
        var cut = Pintar([Adj("Certificado.pdf")]);

        var dialogo = cut.Find("[role=dialog]");
        dialogo.GetAttribute("aria-modal").Should().Be("true");
        dialogo.GetAttribute("tabindex").Should().Be("-1");
        cut.Find($"#{dialogo.GetAttribute("aria-labelledby")}").TextContent.Should().Be("Certificado.pdf");
    }

    [Fact]
    public void El_titulo_y_la_linea_de_resumen_los_da_quien_abre_el_visor_o_salen_del_adjunto()
    {
        var sin = Pintar([Adj("Certificado.pdf"), Adj("Seguro.pdf")]);
        sin.Find(".vz-tit-h").TextContent.Should().Be("Certificado.pdf");
        sin.Find(".vz-meta").TextContent.Should().Be("2 adjuntos");

        var con = Render<VisorDocumentoPantallaCompleta>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.Adjuntos, [Adj("Certificado.pdf")])
            .Add(x => x.Titulo, "Revisar los adjuntos del correo de Marta Ruiz")
            .Add(x => x.Meta, "marta@obras.example · 1 adjunto"));
        con.Find(".vz-tit-h").TextContent.Should().Be("Revisar los adjuntos del correo de Marta Ruiz");
        con.Find(".vz-meta").TextContent.Should().Be("marta@obras.example · 1 adjunto");
    }

    [Fact]
    public void La_cabecera_ofrece_zoom_girar_descargar_y_cerrar_con_su_rotulo()
    {
        var adjunto = Adj("Certificado.pdf", url: "/comunicaciones/adjuntos/abc/archivo");
        var cut = Pintar([adjunto]);

        cut.Find("[role=group]").GetAttribute("aria-label").Should().Be("Zoom");
        cut.Find("button[aria-label='Reducir zoom']").Should().NotBeNull();
        cut.Find("button[aria-label='Ampliar zoom']").Should().NotBeNull();
        cut.Find("button[aria-label='Girar documento']").Should().NotBeNull();
        cut.Find("button[aria-label='Cerrar el visor']").Should().NotBeNull();
        var descargar = cut.Find("a[download]");
        descargar.GetAttribute("href").Should().Be("/comunicaciones/adjuntos/abc/archivo");
        descargar.GetAttribute("download").Should().Be("Certificado.pdf");
        descargar.TextContent.Trim().Should().Be("Descargar documento");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("//otro.sitio/archivo")]
    [InlineData("https://otro.sitio/archivo")]
    [InlineData("/\\otro.sitio/archivo")]
    [InlineData("/\t/otro.sitio/archivo")]
    [InlineData("/\n/otro.sitio/archivo")]
    [InlineData("")]
    public void La_descarga_no_enlaza_direcciones_que_no_son_del_propio_sitio(string url)
    {
        var cut = Pintar([Adj("Certificado.pdf", url: url)]);

        cut.FindAll("a[href]").Should().BeEmpty();
        cut.Find("button[download]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Una_imagen_se_pinta_en_la_hoja_solo_con_una_direccion_del_propio_sitio()
    {
        var segura = Pintar([Adj("Foto.png", url: "/comunicaciones/adjuntos/abc/archivo")]);
        var imagen = segura.Find("article img");
        imagen.GetAttribute("src").Should().Be("/comunicaciones/adjuntos/abc/archivo");
        imagen.GetAttribute("alt").Should().Be("Foto.png");

        using var otro = new BunitContext();
        otro.JSInterop.Mode = JSRuntimeMode.Loose;
        otro.Services.AddLocalization();
        var insegura = otro.Render<VisorDocumentoPantallaCompleta>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.Adjuntos, [Adj("Foto.png", url: "javascript:alert(1)")]));
        insegura.FindAll("article img").Should().BeEmpty();
        insegura.Find("article").TextContent.Should().Contain("Esta página no tiene vista previa");
    }

    [Fact]
    public void Un_pdf_sin_contenido_propio_avisa_de_que_no_hay_vista_previa_y_no_usa_jerga()
    {
        var cut = Pintar([Adj("Certificado.pdf")]);

        var hoja = cut.Find("article");
        hoja.TextContent.Should().Contain("Esta página no tiene vista previa").And.Contain("Descarga el documento para verlo.");
        cut.Markup.Should().NotContainAny("Tenant", "Inbound", "Outbound");
    }

    [Fact]
    public void El_contenido_de_cada_hoja_puede_darlo_quien_abre_el_visor()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        var cut = Render<VisorDocumentoPantallaCompleta>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.Adjuntos, [Adj("Certificado.pdf", paginas: 2)])
            .Add(x => x.Hoja, h => $"<p class=\"mi-hoja\">Hoja {h.Numero} de {h.Total}</p>"));

        cut.FindAll(".mi-hoja").Select(h => h.TextContent).Should().Equal("Hoja 1 de 2", "Hoja 2 de 2");
    }

    [Fact]
    public void La_cola_dice_en_que_documento_esta_y_cambia_con_anterior_y_siguiente_dando_la_vuelta()
    {
        var cut = Pintar([Adj("A.pdf"), Adj("B.pdf"), Adj("C.pdf")]);
        Posicion(cut).Should().Be("Documento 1 de 3");

        cut.Find("button[aria-label='Ir al adjunto siguiente']").Click();
        Posicion(cut).Should().Be("Documento 2 de 3");
        cut.Find(".vz-tit-h").TextContent.Should().Be("B.pdf");
        cut.Find(".vz-cola-nombre").TextContent.Should().Be("B.pdf");

        cut.Find("button[aria-label='Ir al adjunto anterior']").Click();
        cut.Find("button[aria-label='Ir al adjunto anterior']").Click();
        Posicion(cut).Should().Be("Documento 3 de 3");

        cut.Find("button[aria-label='Ir al adjunto siguiente']").Click();
        Posicion(cut).Should().Be("Documento 1 de 3");
    }

    [Fact]
    public void Con_un_solo_adjunto_anterior_y_siguiente_estan_deshabilitados()
    {
        var cut = Pintar([Adj("A.pdf")]);

        cut.Find("button[aria-label='Ir al adjunto anterior']").HasAttribute("disabled").Should().BeTrue();
        cut.Find("button[aria-label='Ir al adjunto siguiente']").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".vz-meta").TextContent.Should().Be("1 adjunto");
    }

    [Fact]
    public void Se_abre_en_el_adjunto_pedido_y_un_indice_fuera_de_rango_se_ajusta()
    {
        Posicion(Pintar([Adj("A.pdf"), Adj("B.pdf"), Adj("C.pdf")], indice: 2)).Should().Be("Documento 3 de 3");

        using var otro = new BunitContext();
        otro.JSInterop.Mode = JSRuntimeMode.Loose;
        otro.Services.AddLocalization();
        var fuera = otro.Render<VisorDocumentoPantallaCompleta>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.Adjuntos, [Adj("A.pdf"), Adj("B.pdf")])
            .Add(x => x.IndiceInicial, 9));
        Posicion(fuera).Should().Be("Documento 2 de 2");
    }

    [Fact]
    public void Una_marca_por_adjunto_dice_su_estado_con_texto_y_marca_el_actual()
    {
        var cut = Pintar([
            Adj("A.pdf"),
            Adj("B.pdf", estado: EstadoAdjuntoVisor.Asignado),
            Adj("C.pdf", estado: EstadoAdjuntoVisor.Rechazado)
        ]);

        var marcas = cut.FindAll(".vz-marca");
        marcas.Select(m => m.GetAttribute("aria-label")).Should().Equal(
            "Adjunto 1, pendiente: A.pdf", "Adjunto 2, asignado: B.pdf", "Adjunto 3, rechazado: C.pdf");
        marcas.Select(m => m.GetAttribute("aria-current")).Should().Equal("true", "false", "false");
        marcas[0].TextContent.Trim().Should().Be("1");
        marcas[1].QuerySelector("svg").Should().NotBeNull();
        marcas[2].QuerySelector("svg").Should().NotBeNull();
        marcas[1].ClassList.Should().Contain("vz-marca-asignado");
        marcas[2].ClassList.Should().Contain("vz-marca-rechazado");

        marcas[2].Click();
        Posicion(cut).Should().Be("Documento 3 de 3");
        cut.FindAll(".vz-marca").Select(m => m.GetAttribute("aria-current")).Should().Equal("false", "false", "true");
    }

    [Fact]
    public void La_cola_cuenta_lo_que_queda_por_revisar_y_avisa_cuando_no_queda_nada()
    {
        var quedan = Pintar([Adj("A.pdf"), Adj("B.pdf"), Adj("C.pdf", estado: EstadoAdjuntoVisor.Asignado)]);
        quedan.Find(".vz-cola-resto").TextContent.Should().Be("Quedan 2 por revisar");
        quedan.FindAll(".badge").Should().BeEmpty();

        using var una = new BunitContext();
        una.JSInterop.Mode = JSRuntimeMode.Loose;
        una.Services.AddLocalization();
        var queda = una.Render<VisorDocumentoPantallaCompleta>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.Adjuntos, [Adj("A.pdf"), Adj("B.pdf", estado: EstadoAdjuntoVisor.Rechazado)]));
        queda.Find(".vz-cola-resto").TextContent.Should().Be("Queda 1 por revisar");

        using var ninguna = new BunitContext();
        ninguna.JSInterop.Mode = JSRuntimeMode.Loose;
        ninguna.Services.AddLocalization();
        var todas = ninguna.Render<VisorDocumentoPantallaCompleta>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.Adjuntos, [Adj("A.pdf", estado: EstadoAdjuntoVisor.Asignado), Adj("B.pdf", estado: EstadoAdjuntoVisor.Rechazado)]));
        todas.FindAll(".vz-cola-resto").Should().BeEmpty();
        todas.Find(".badge").TextContent.Trim().Should().Be("Todos los adjuntos revisados");
    }

    [Fact]
    public void La_cola_rotula_tipo_paginas_y_peso_del_adjunto_actual()
    {
        var cut = Pintar([Adj("Certificado.pdf", paginas: 3), Adj("Foto.png"), Adj("Nota.docx")]);
        cut.Find(".vz-cola-peso").TextContent.Should().Be("PDF · 3 páginas · 312 KB");

        cut.Find("button[aria-label='Ir al adjunto siguiente']").Click();
        cut.Find(".vz-cola-peso").TextContent.Should().Be("Imagen · 312 KB");

        cut.Find("button[aria-label='Ir al adjunto siguiente']").Click();
        cut.Find(".vz-cola-peso").TextContent.Should().Be("Documento · 1 página · 312 KB");
    }

    [Fact]
    public void El_zoom_sube_y_baja_de_diez_en_diez_entre_el_50_y_el_200_y_se_restablece()
    {
        var cut = Pintar([Adj("A.pdf")]);
        Zoom(cut).Should().Be("100 %");

        cut.Find("button[aria-label='Ampliar zoom']").Click();
        Zoom(cut).Should().Be("110 %");
        cut.Find(".vz-hoja").GetAttribute("style").Should().Contain("--vz-zoom:110");
        cut.Find(".vz-hoja").ClassList.Should().Contain("vz-hoja-ampliada");

        for (var i = 0; i < 20; i++) cut.Find("button[aria-label='Ampliar zoom']").Click();
        Zoom(cut).Should().Be("200 %");

        cut.Find("button[aria-label$='restablecer el zoom']").Click();
        Zoom(cut).Should().Be("100 %");
        cut.Find(".vz-hoja").ClassList.Should().NotContain("vz-hoja-ampliada");

        for (var i = 0; i < 20; i++) cut.Find("button[aria-label='Reducir zoom']").Click();
        Zoom(cut).Should().Be("50 %");
        cut.Find("button[aria-label$='restablecer el zoom']").GetAttribute("aria-label").Should().Be("50 %, restablecer el zoom");
    }

    [Fact]
    public async Task Los_mas_y_menos_del_teclado_cambian_el_zoom()
    {
        var cut = Pintar([Adj("A.pdf")]);

        await TeclaAsync(cut.Find("[role=dialog]"), "+");
        await TeclaAsync(cut.Find("[role=dialog]"), "=");
        Zoom(cut).Should().Be("120 %");

        await TeclaAsync(cut.Find("[role=dialog]"), "-");
        Zoom(cut).Should().Be("110 %");
    }

    [Fact]
    public void Girar_da_cuartos_de_vuelta_y_a_la_cuarta_vuelve_a_su_sitio()
    {
        var cut = Pintar([Adj("A.pdf"), Adj("B.pdf")]);
        var girar = () => cut.Find("button[aria-label='Girar documento']").Click();

        cut.Find(".vz-hoja").ClassList.Should().NotContain(c => c.StartsWith("vz-giro-"));
        girar();
        cut.Find(".vz-hoja").ClassList.Should().Contain("vz-giro-90");
        girar();
        cut.Find(".vz-hoja").ClassList.Should().Contain("vz-giro-180");
        girar();
        cut.Find(".vz-hoja").ClassList.Should().Contain("vz-giro-270");
        girar();
        cut.Find(".vz-hoja").ClassList.Should().NotContain(c => c.StartsWith("vz-giro-"));

        girar();
        cut.Find("button[aria-label='Ir al adjunto siguiente']").Click();
        cut.Find(".vz-hoja").ClassList.Should().NotContain(c => c.StartsWith("vz-giro-"));
    }

    [Fact]
    public async Task Escape_cierra_el_visor_desde_el_panel_y_desde_el_lienzo_aunque_este_ampliado()
    {
        var cierres = new List<bool>();
        var cut = Pintar([Adj("A.pdf")], cierres.Add);

        await TeclaAsync(cut.Find("[role=dialog]"), "Escape");
        cierres.Should().Equal(false);

        await TeclaAsync(cut.Find("[role=dialog]"), "+");
        await TeclaAsync(cut.Find(".vz-lienzo"), "Escape");
        cierres.Should().Equal(false, false);
    }

    [Fact]
    public async Task Un_clic_en_el_fondo_cierra_solo_si_el_pulsado_empezo_en_el_fondo()
    {
        var cierres = new List<bool>();
        var cut = Pintar([Adj("A.pdf")], cierres.Add);

        // Un clic suelto sin pulsado previo en el fondo no cierra.
        await cut.Find(".vz-ov").ClickAsync(new MouseEventArgs());
        cierres.Should().BeEmpty();

        // Un arrastre que nace dentro del panel (seleccionar texto de una hoja) y acaba sobre el fondo tampoco.
        await cut.Find(".vz-ov").MouseDownAsync(new MouseEventArgs());
        await cut.Find(".vz-pn").MouseDownAsync(new MouseEventArgs());
        await cut.Find(".vz-ov").ClickAsync(new MouseEventArgs());
        cierres.Should().BeEmpty();

        await cut.Find(".vz-ov").MouseDownAsync(new MouseEventArgs());
        await cut.Find(".vz-ov").ClickAsync(new MouseEventArgs());
        cierres.Should().Equal(false);
    }

    [Fact]
    public void El_panel_declara_que_el_clic_y_el_pulsado_no_suben_al_fondo()
    {
        var cut = Pintar([Adj("A.pdf")]);

        // bUnit no despacha un clic a un elemento que solo lleva stopPropagation y ningún manejador (la propagación la corta el navegador):
        // aquí solo se fija que el panel lo declara; que de verdad no cierra dentro del panel lo prueba el navegador, no bUnit.
        var atributos = cut.Find(".vz-pn").Attributes.Select(a => a.Name).ToList();
        atributos.Should().Contain(n => n.Contains("stoppropagation", StringComparison.OrdinalIgnoreCase) && n.Contains("click", StringComparison.OrdinalIgnoreCase));
        atributos.Should().Contain(n => n.Contains("stoppropagation", StringComparison.OrdinalIgnoreCase) && n.Contains("mousedown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task El_boton_cerrar_avisa_del_cierre()
    {
        var cierres = new List<bool>();
        var cut = Pintar([Adj("A.pdf")], cierres.Add);

        await cut.Find("button[aria-label='Cerrar el visor']").ClickAsync(new MouseEventArgs());

        cierres.Should().Equal(false);
    }

    [Fact]
    public async Task Las_flechas_cambian_de_adjunto_desde_el_panel_y_desde_el_lienzo_sin_ampliar()
    {
        var cut = Pintar([Adj("A.pdf"), Adj("B.pdf"), Adj("C.pdf")]);

        await TeclaAsync(cut.Find("[role=dialog]"), "ArrowRight");
        Posicion(cut).Should().Be("Documento 2 de 3");

        // Desde el lienzo el evento no sube al panel (stopPropagation): si subiera, cambiaría dos veces.
        await TeclaAsync(cut.Find(".vz-lienzo"), "ArrowRight");
        Posicion(cut).Should().Be("Documento 3 de 3");

        await TeclaAsync(cut.Find("[role=dialog]"), "ArrowLeft");
        Posicion(cut).Should().Be("Documento 2 de 3");
    }

    [Fact]
    public async Task Con_el_lienzo_ampliado_las_flechas_se_dejan_al_desplazamiento()
    {
        var cut = Pintar([Adj("A.pdf"), Adj("B.pdf")]);
        await TeclaAsync(cut.Find("[role=dialog]"), "+");

        await TeclaAsync(cut.Find(".vz-lienzo"), "ArrowRight");
        Posicion(cut).Should().Be("Documento 1 de 2");

        // Con el foco fuera del lienzo siguen cambiando de adjunto.
        await TeclaAsync(cut.Find("[role=dialog]"), "ArrowRight");
        Posicion(cut).Should().Be("Documento 2 de 2");
    }

    [Fact]
    public async Task Ctrl_y_Meta_quedan_para_el_navegador()
    {
        var cut = Pintar([Adj("A.pdf"), Adj("B.pdf")]);

        await cut.Find("[role=dialog]").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight", CtrlKey = true });
        await cut.Find("[role=dialog]").KeyDownAsync(new KeyboardEventArgs { Key = "+", MetaKey = true });

        Posicion(cut).Should().Be("Documento 1 de 2");
        Zoom(cut).Should().Be("100 %");
    }

    [Fact]
    public void Hay_una_miniatura_y_una_hoja_por_pagina_y_la_miniatura_actual_se_marca()
    {
        var cut = Pintar([Adj("A.pdf", paginas: 3)]);

        cut.FindAll(".vz-pag").Should().HaveCount(3);
        cut.FindAll(".vz-hoja").Should().HaveCount(3);
        cut.Find(".vz-nav").GetAttribute("aria-label").Should().Be("Páginas");
        cut.Find(".vz-lienzo").GetAttribute("aria-label").Should().Be("Páginas del documento");
        cut.FindAll("article").Select(a => a.GetAttribute("aria-label")).Should().Equal("Página 1 de 3", "Página 2 de 3", "Página 3 de 3");
        cut.FindAll(".vz-pag").Select(b => b.GetAttribute("aria-current")).Should().Equal("true", "false", "false");

        cut.Find("button.vz-pag[aria-label='Página 3']").Click();
        cut.FindAll(".vz-pag").Select(b => b.GetAttribute("aria-current")).Should().Equal("false", "false", "true");
    }

    [Fact]
    public async Task AltGr_llega_como_Ctrl_mas_Alt_y_sigue_cambiando_de_pagina_con_los_corchetes()
    {
        var cut = Pintar([Adj("A.pdf", paginas: 3)]);

        // En teclado español y catalán « [ » y « ] » se escriben con AltGr, que el navegador entrega como Ctrl+Alt.
        await cut.Find("[role=dialog]").KeyDownAsync(new KeyboardEventArgs { Key = "]", CtrlKey = true, AltKey = true });

        cut.FindAll(".vz-pag")[1].GetAttribute("aria-current").Should().Be("true");
    }

    [Fact]
    public async Task Alt_con_flecha_queda_para_el_navegador()
    {
        var cut = Pintar([Adj("A.pdf"), Adj("B.pdf")]);

        await cut.Find("[role=dialog]").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight", AltKey = true });

        Posicion(cut).Should().Be("Documento 1 de 2");
    }

    [Fact]
    public async Task Los_corchetes_cambian_de_pagina_sin_salirse_de_los_extremos()
    {
        var cut = Pintar([Adj("A.pdf", paginas: 2)]);
        var actual = () => cut.FindAll(".vz-pag").Select(b => b.GetAttribute("aria-current")).ToArray();

        await TeclaAsync(cut.Find("[role=dialog]"), "[");
        actual().Should().Equal("true", "false");

        await TeclaAsync(cut.Find("[role=dialog]"), "]");
        await TeclaAsync(cut.Find("[role=dialog]"), "]");
        actual().Should().Equal("false", "true");

        await TeclaAsync(cut.Find("[role=dialog]"), "[");
        actual().Should().Equal("true", "false");
    }

    [Fact]
    public async Task Elegir_una_pagina_o_cambiar_de_adjunto_lleva_el_lienzo_a_esa_hoja()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var modulo = JSInterop.SetupModule("./js/visor-documento.js");
        modulo.Mode = JSRuntimeMode.Loose;
        var cut = Pintar([Adj("A.pdf", paginas: 3), Adj("B.pdf")]);

        await cut.Find("button.vz-pag[aria-label='Página 3']").ClickAsync(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => modulo.Invocations["irAPagina"].Should().HaveCount(1), Plazo);
        modulo.Invocations["irAPagina"][0].Arguments[1].Should().Be(2);

        await cut.Find("button[aria-label='Ir al adjunto siguiente']").ClickAsync(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => modulo.Invocations["irAPagina"].Should().HaveCount(2), Plazo);
        modulo.Invocations["irAPagina"][1].Arguments[1].Should().Be(0);
    }

    [Fact]
    public async Task Al_abrirse_el_foco_va_a_la_posicion_de_la_cola()
    {
        var cut = Pintar([Adj("A.pdf"), Adj("B.pdf")]);

        await cut.WaitForAssertionAsync(
            () => JSInterop.VerifyFocusAsyncInvoke().Arguments[0].ShouldBeElementReferenceTo(cut.Find(".vz-cola-n")), Plazo);
    }

    /// <summary>La trampa real (Tab cíclico y foco devuelto a quien abrió el visor) vive en dialogo-foco.js y la prueba dialogoFoco.test.mjs; aquí se fija que el visor la activa sobre su panel y la libera al cerrarse.</summary>
    [Fact]
    public async Task El_foco_queda_atrapado_en_el_panel_al_abrirse_y_se_libera_al_cerrarse()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        var modulo = JSInterop.SetupModule("./js/dialogo-foco.js");
        var trampa = modulo.SetupModule("atraparFoco", _ => true);

        var cut = Render<VisorDocumentoPantallaCompleta>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.Adjuntos, [Adj("A.pdf")]));

        await cut.WaitForAssertionAsync(() => modulo.Invocations["atraparFoco"].Should().HaveCount(1), Plazo);
        modulo.Invocations["atraparFoco"][0].Arguments[0].Should().BeOfType<ElementReference>();
        trampa.Invocations["dispose"].Should().BeEmpty();

        cut.Render(p => p.Add(x => x.Visible, false));

        await cut.WaitForAssertionAsync(() => trampa.Invocations["dispose"].Should().HaveCount(1), Plazo);
    }

    [Fact]
    public async Task Al_volver_a_abrirse_parte_de_cero_sin_arrastrar_zoom_ni_giro()
    {
        var cut = Pintar([Adj("A.pdf"), Adj("B.pdf")]);
        await TeclaAsync(cut.Find("[role=dialog]"), "+");
        cut.Find("button[aria-label='Girar documento']").Click();
        cut.Find("button[aria-label='Ir al adjunto siguiente']").Click();

        cut.Render(p => p.Add(x => x.Visible, false));
        cut.Markup.Trim().Should().BeEmpty();

        cut.Render(p => p.Add(x => x.Visible, true));
        Zoom(cut).Should().Be("100 %");
        Posicion(cut).Should().Be("Documento 1 de 2");
        cut.Find(".vz-hoja").ClassList.Should().NotContain(c => c.StartsWith("vz-giro-"));
    }

    [Fact]
    public void La_cola_rotula_las_paginas_que_se_pintan_aunque_el_adjunto_traiga_otro_total()
    {
        var cero = Pintar([Adj("A.pdf", paginas: 0)]);
        cero.Find(".vz-cola-peso").TextContent.Should().Contain("1 página");
        cero.FindAll(".vz-hoja").Should().HaveCount(1);

        using var largo = new BunitContext();
        largo.JSInterop.Mode = JSRuntimeMode.Loose;
        largo.Services.AddLocalization();
        var cut = largo.Render<VisorDocumentoPantallaCompleta>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.Adjuntos, [Adj("B.pdf", paginas: 600)]));
        cut.Find(".vz-cola-peso").TextContent.Should().Contain("500 páginas");
        cut.FindAll(".vz-hoja").Should().HaveCount(500);
    }

    [Fact]
    public void Si_quien_abre_el_visor_quita_otro_adjunto_se_sigue_en_el_mismo_documento()
    {
        var a = Adj("A.pdf");
        var b = Adj("B.pdf");
        var c = Adj("C.pdf");
        var cut = Pintar([a, b, c], indice: 1);

        cut.Render(p => p.Add(x => x.Adjuntos, [b, c]));

        Posicion(cut).Should().Be("Documento 1 de 2");
        cut.Find(".vz-tit-h").TextContent.Should().Be("B.pdf");
    }

    [Fact]
    public void Si_la_lista_cambia_con_el_visor_abierto_se_actualizan_las_marcas_sin_perder_el_adjunto()
    {
        var a = Adj("A.pdf");
        var b = Adj("B.pdf");
        var cut = Pintar([a, b], indice: 1);

        cut.Render(p => p.Add(x => x.Adjuntos, [a, b with { Estado = EstadoAdjuntoVisor.Asignado }]));

        Posicion(cut).Should().Be("Documento 2 de 2");
        cut.FindAll(".vz-marca")[1].ClassList.Should().Contain("vz-marca-asignado");
    }

    [Theory]
    [InlineData("/ruta/archivo", true)]
    [InlineData("/", true)]
    [InlineData("//otro.sitio", false)]
    [InlineData("/\\otro.sitio", false)]
    [InlineData("https://otro.sitio", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ruta/relativa", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Solo_son_seguras_las_direcciones_relativas_al_propio_sitio(string? url, bool esperado) =>
        VisorDocumentoPantallaCompleta.EsUrlRelativaSegura(url).Should().Be(esperado);
}
