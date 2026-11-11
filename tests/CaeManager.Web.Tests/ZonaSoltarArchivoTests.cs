using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// ZonaSoltarArchivo: lo que hacía antes de este cambio queda fijado (estado vacío, el selector
/// nativo sigue siendo la vía de entrada, el InputFile conserva su etiqueta accesible) y lo nuevo
/// se prueba al lado: estado con archivo, «Quitar» / «Cambiar», errores de pegado visibles en la
/// zona y región viva para lectores de pantalla.
///
/// El oyente de Ctrl+V a nivel de documento es JavaScript y no corre en bUnit: aquí se fija el
/// contrato con el módulo (qué recibe <c>inicializar</c>, qué atributos lee del DOM de la zona);
/// su comportamiento sin foco previo lo cubre el guion de Node de
/// <c>ZonaSoltarArchivoJsTests</c>.
/// </summary>
public class ZonaSoltarArchivoTests : BunitContext
{
    private const string Modulo = "./js/zona-soltar-archivo.js";

    public ZonaSoltarArchivoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private IRenderedComponent<ZonaSoltarArchivo> Zona(Action<ComponentParameterCollectionBuilder<ZonaSoltarArchivo>>? ajustes = null) =>
        Render<ZonaSoltarArchivo>(p =>
        {
            p.Add(z => z.OnChange, EventCallback.Factory.Create<InputFileChangeEventArgs>(this, _ => { }));
            ajustes?.Invoke(p);
        });

    private static IReadOnlyList<ArchivoAdjuntoVista> L(params ArchivoAdjuntoVista[] archivos) => archivos;

    private static string Anuncio(IRenderedComponent<ZonaSoltarArchivo> cut) =>
        cut.Find(".zona-soltar-archivo-anuncio").TextContent.Trim();

    private static void Subir(IRenderedComponent<ZonaSoltarArchivo> cut, string nombre = "foto.png", string tipo = "image/png") =>
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("contenido", nombre, contentType: tipo));

    // ---- Lo que hacía hoy (fijado antes de cambiarlo) ----

    [Fact]
    public void Vacia_ensena_la_superficie_de_siempre_sin_estado_de_archivo()
    {
        var cut = Zona(p => p.Add(z => z.FormatosTexto, "PDF · JPG"));

        cut.Markup.Should().Contain("Suelta tu archivo aquí");
        cut.Markup.Should().Contain("busca archivos");
        cut.Markup.Should().Contain("PDF · JPG");
        cut.FindAll(".zona-soltar-archivo-con-archivo").Should().BeEmpty();
        cut.FindAll(".zona-soltar-archivo-fila").Should().BeEmpty();
        cut.FindAll("button").Should().BeEmpty("sin archivo no hay «Cambiar» ni «Quitar»");
    }

    [Fact]
    public void El_selector_nativo_sigue_siendo_accesible_por_teclado_y_con_etiqueta()
    {
        var cut = Zona(p => p.Add(z => z.Titulo, "Justificante"));

        var entrada = cut.Find("input[type=file]");
        entrada.GetAttribute("aria-label").Should().Be("Justificante");
        entrada.HasAttribute("disabled").Should().BeFalse();
        cut.Find(".zona-soltar-archivo").GetAttribute("tabindex").Should().Be("0", "la zona recibe el foco del teclado");
        cut.Find("label.zona-soltar-archivo-superficie").GetAttribute("for").Should().Be(entrada.Id);
    }

    [Fact]
    public void Cargando_deshabilita_el_input_y_saca_la_zona_del_orden_de_tabulacion()
    {
        var cut = Zona(p => p.Add(z => z.Cargando, true));

        cut.Find("input[type=file]").HasAttribute("disabled").Should().BeTrue();
        cut.Find(".zona-soltar-archivo").GetAttribute("tabindex").Should().Be("-1");
        cut.Markup.Should().Contain("Procesando…");
    }

    [Fact]
    public void Multiple_y_accept_llegan_al_input_y_al_DOM_que_lee_el_javascript()
    {
        var cut = Zona(p =>
        {
            p.Add(z => z.Multiple, true);
            p.Add(z => z.Accept, ".pdf,image/png");
            p.Add(z => z.TamanoMaximoBytes, 10L * 1024 * 1024);
        });

        cut.Find("input[type=file]").HasAttribute("multiple").Should().BeTrue();
        var zona = cut.Find(".zona-soltar-archivo");
        zona.GetAttribute("data-accept").Should().Be(".pdf,image/png");
        zona.GetAttribute("data-multiple").Should().Be("true");
        zona.GetAttribute("data-max-bytes").Should().Be("10485760");
    }

    [Fact]
    public void Sin_tamano_maximo_no_se_publica_ningun_limite()
    {
        var cut = Zona();

        cut.Find(".zona-soltar-archivo").HasAttribute("data-max-bytes").Should().BeFalse();
        cut.Find(".zona-soltar-archivo").GetAttribute("data-multiple").Should().Be("false");
    }

    [Fact]
    public void NombreArchivoActual_sin_lista_sigue_ensenandose_como_hasta_ahora()
    {
        var cut = Zona(p => p.Add(z => z.NombreArchivoActual, "firma.png"));

        cut.Find(".detalle-tecnico").TextContent.Should().Be("firma.png");
    }

    [Fact]
    public async Task Elegir_con_el_selector_reenvia_el_mismo_evento_a_quien_la_usa()
    {
        InputFileChangeEventArgs? recibido = null;
        var cut = Render<ZonaSoltarArchivo>(p => p.Add(z => z.OnChange,
            EventCallback.Factory.Create<InputFileChangeEventArgs>(this, e => recibido = e)));

        Subir(cut, "contrato.pdf", "application/pdf");

        await cut.WaitForStateAsync(() => recibido is not null);
        recibido!.FileCount.Should().Be(1);
        recibido.File.Name.Should().Be("contrato.pdf");
    }

    // ---- Estado con archivo ----

    [Fact]
    public void Con_un_archivo_la_zona_cambia_de_estado_y_ensena_nombre_tipo_y_tamano()
    {
        var cut = Zona(p => p.Add(z => z.ArchivosAdjuntos, L(new ArchivoAdjuntoVista("contrato.pdf", 1_258_291))));

        cut.FindAll(".zona-soltar-archivo-con-archivo").Should().ContainSingle();
        cut.FindAll(".zona-soltar-archivo-titulo").Should().BeEmpty("ya no pide un archivo: enseña el adjunto");
        cut.Markup.Should().NotContain("busca archivos");
        cut.Find(".zona-soltar-archivo-estado").TextContent.Should().Contain("Archivo adjuntado");
        cut.Find(".zona-soltar-archivo-fila-nombre").TextContent.Should().Be("contrato.pdf");
        cut.Find(".zona-soltar-archivo-fila-nombre").GetAttribute("title").Should().Be("contrato.pdf");
        cut.Find(".zona-soltar-archivo-fila-meta").TextContent.Should().MatchRegex(@"^PDF · 1[.,]2 MB$");
        cut.FindAll(".zona-soltar-archivo-fila-icono svg").Should().NotBeEmpty("cada fila lleva su icono");
        cut.Find("input[type=file]").Should().NotBeNull("el InputFile sigue montado: arrastrar y pegar siguen entrando por él");
    }

    [Fact]
    public void El_tipo_se_deduce_de_la_extension_y_el_tamano_se_formatea_en_unidades_legibles()
    {
        var cut = Zona(p => p.Add(z => z.ArchivosAdjuntos, L(
            new ArchivoAdjuntoVista("foto.JPG", 512),
            new ArchivoAdjuntoVista("informe.docx", 20 * 1024),
            new ArchivoAdjuntoVista("sin-extension"),
            new ArchivoAdjuntoVista("raro.dat", 1, "Datos"))));

        var metas = cut.FindAll(".zona-soltar-archivo-fila-meta").Select(m => m.TextContent).ToList();
        metas.Should().Equal("JPG · 512 B", "DOCX · 20 KB", "Datos · 1 B");
        cut.FindAll(".zona-soltar-archivo-fila").Should().HaveCount(4);
    }

    [Fact]
    public void Con_varios_archivos_ensena_la_lista_el_recuento_y_el_aviso_de_combinacion()
    {
        var cut = Zona(p =>
        {
            p.Add(z => z.ArchivosAdjuntos, L(new ArchivoAdjuntoVista("p1.jpg", 2048), new ArchivoAdjuntoVista("p2.jpg", 4096)));
            p.Add(z => z.AvisoCombinacion, "Estos 2 archivos se han combinado en un solo PDF.");
        });

        cut.FindAll(".zona-soltar-archivo-fila-nombre").Select(n => n.TextContent).Should().Equal("p1.jpg", "p2.jpg");
        cut.Find(".zona-soltar-archivo-estado").TextContent.Should().Contain("2 archivos adjuntados");
        cut.Find(".zona-soltar-archivo-aviso").TextContent.Should().Be("Estos 2 archivos se han combinado en un solo PDF.");
    }

    [Fact]
    public void Lista_vacia_o_nula_es_el_estado_vacio()
    {
        Zona(p => p.Add(z => z.ArchivosAdjuntos, Array.Empty<ArchivoAdjuntoVista>()))
            .Markup.Should().Contain("Suelta tu archivo aquí");
        Zona(p => p.Add(z => z.ArchivosAdjuntos, (IReadOnlyList<ArchivoAdjuntoVista>?)null))
            .FindAll(".zona-soltar-archivo-con-archivo").Should().BeEmpty();
    }

    [Fact]
    public void Con_archivo_la_gris_NombreArchivoActual_no_duplica_el_estado()
    {
        var cut = Zona(p =>
        {
            p.Add(z => z.ArchivosAdjuntos, L(new ArchivoAdjuntoVista("a.pdf")));
            p.Add(z => z.NombreArchivoActual, "Archivo adjuntado correctamente.");
        });

        cut.FindAll(".detalle-tecnico").Should().BeEmpty();
    }

    [Fact]
    public void Cambiar_es_un_boton_real_que_el_javascript_engancha_por_su_marca()
    {
        var cut = Zona(p => p.Add(z => z.ArchivosAdjuntos, L(new ArchivoAdjuntoVista("a.pdf"))));

        var cambiar = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cambiar");
        cambiar.GetAttribute("type").Should().Be("button", "no envía el formulario que la contiene");
        cambiar.HasAttribute("data-zona-cambiar").Should().BeTrue();
        cambiar.GetAttribute("aria-label").Should().Be("Cambiar el archivo adjunto");
    }

    // ---- Quitar ----

    [Fact]
    public async Task Quitar_avisa_a_quien_la_usa_y_lo_anuncia()
    {
        var quitado = 0;
        var cut = Zona(p =>
        {
            p.Add(z => z.ArchivosAdjuntos, L(new ArchivoAdjuntoVista("a.pdf")));
            p.Add(z => z.OnQuitar, EventCallback.Factory.Create(this, () => quitado++));
        });

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Quitar").ClickAsync(new MouseEventArgs());

        quitado.Should().Be(1);
        Anuncio(cut).Should().Be("Archivo quitado.");
    }

    [Fact]
    public void Sin_OnQuitar_o_sin_permiso_no_hay_boton_Quitar_pero_si_Cambiar()
    {
        var sinDelegado = Zona(p => p.Add(z => z.ArchivosAdjuntos, L(new ArchivoAdjuntoVista("a.pdf"))));
        var sinPermiso = Zona(p =>
        {
            p.Add(z => z.ArchivosAdjuntos, L(new ArchivoAdjuntoVista("a.pdf")));
            p.Add(z => z.OnQuitar, EventCallback.Factory.Create(this, () => { }));
            p.Add(z => z.PermitirQuitar, false);
        });

        foreach (var cut in new[] { sinDelegado, sinPermiso })
        {
            cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Equal("Cambiar");
        }
    }

    [Fact]
    public void Con_la_zona_cargando_Cambiar_y_Quitar_estan_deshabilitados()
    {
        var cut = Zona(p =>
        {
            p.Add(z => z.ArchivosAdjuntos, L(new ArchivoAdjuntoVista("a.pdf")));
            p.Add(z => z.OnQuitar, EventCallback.Factory.Create(this, () => { }));
            p.Add(z => z.Cargando, true);
        });

        cut.FindAll("button").Should().OnlyContain(b => b.HasAttribute("disabled"));
    }

    // ---- aria-live y los tres caminos de entrada ----

    [Fact]
    public void La_region_viva_existe_desde_el_primer_render_y_es_educada()
    {
        var cut = Zona();

        var region = cut.Find(".zona-soltar-archivo-anuncio");
        region.GetAttribute("role").Should().Be("status");
        region.GetAttribute("aria-live").Should().Be("polite");
        region.GetAttribute("aria-atomic").Should().Be("true");
        region.TextContent.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Elegir_con_el_selector_se_anuncia()
    {
        var cut = Zona();

        Subir(cut, "foto.png");

        cut.WaitForAssertion(() => Anuncio(cut).Should().Be("Archivo seleccionado: foto.png."));
    }

    [Fact]
    public void Sin_anunciar_el_nombre_el_aviso_no_deja_el_nombre_del_archivo_en_la_pagina()
    {
        var cut = Zona(p => p.Add(z => z.AnunciarNombre, false));

        Subir(cut, "nomina-de-alguien.png");

        cut.WaitForAssertion(() => Anuncio(cut).Should().Be("Archivo recibido."));
        cut.Markup.Should().NotContain("nomina-de-alguien");
    }

    [Fact]
    public async Task Pegar_con_Ctrl_V_se_anuncia_como_pegado()
    {
        var cut = Zona();
        await cut.InvokeAsync(() => cut.Instance.AnunciarOrigen("pegado"));

        Subir(cut, "image.png");

        cut.WaitForAssertion(() => Anuncio(cut).Should().Be("Archivo pegado: image.png."));
    }

    [Fact]
    public async Task Soltar_se_anuncia_como_soltado()
    {
        var cut = Zona();
        await cut.InvokeAsync(() => cut.Instance.AnunciarOrigen("soltado"));

        Subir(cut, "acta.pdf", "application/pdf");

        cut.WaitForAssertion(() => Anuncio(cut).Should().Be("Archivo soltado: acta.pdf."));
    }

    [Fact]
    public void Varios_archivos_se_anuncian_por_recuento()
    {
        var cut = Zona(p => p.Add(z => z.Multiple, true));

        cut.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromText("a", "a.png", contentType: "image/png"),
            InputFileContent.CreateFromText("b", "b.png", contentType: "image/png"));

        cut.WaitForAssertion(() => Anuncio(cut).Should().Be("2 archivos recibidos."));
    }

    [Fact]
    public async Task Pegar_dos_capturas_con_el_mismo_nombre_vuelve_a_anunciar()
    {
        var cut = Zona();
        await cut.InvokeAsync(() => cut.Instance.AnunciarOrigen("pegado"));
        Subir(cut, "image.png");
        cut.WaitForAssertion(() => Anuncio(cut).Should().Be("Archivo pegado: image.png."));
        var primero = cut.Find(".zona-soltar-archivo-anuncio > span");

        await cut.InvokeAsync(() => cut.Instance.AnunciarOrigen("pegado"));
        Subir(cut, "image.png");

        cut.WaitForAssertion(() => cut.Find(".zona-soltar-archivo-anuncio > span").Should().NotBeSameAs(primero,
            "el texto idéntico solo se vuelve a leer si el nodo de la región cambia"));
    }

    // ---- Errores de entrada (pegado) ----

    [Fact]
    public async Task Pegar_sin_archivo_ensena_un_error_visible_en_la_zona()
    {
        var cut = Zona();

        await cut.InvokeAsync(() => cut.Instance.RechazarEntrada("sin-archivo", null));

        var error = cut.Find(".zona-soltar-archivo .zona-soltar-archivo-error");
        error.GetAttribute("role").Should().Be("alert");
        error.TextContent.Should().Contain("El portapapeles no contiene ningún archivo");
        cut.FindAll(".detalle-tecnico").Should().BeEmpty("no es el texto pequeño y gris de antes");
    }

    [Fact]
    public async Task Pegar_un_tipo_no_admitido_dice_cual_y_que_formatos_si_valen()
    {
        var cut = Zona(p => p.Add(z => z.FormatosTexto, "PDF · JPG · PNG · DOCX"));

        await cut.InvokeAsync(() => cut.Instance.RechazarEntrada("tipo", "notas.txt"));

        cut.Find(".zona-soltar-archivo-error").TextContent.Should()
            .Be("«notas.txt» no es un tipo de archivo admitido. Formatos admitidos: PDF · JPG · PNG · DOCX.");
    }

    [Fact]
    public async Task Pegar_un_tipo_no_admitido_sin_lista_de_formatos_no_inventa_una()
    {
        var cut = Zona();

        await cut.InvokeAsync(() => cut.Instance.RechazarEntrada("tipo", "notas.txt"));

        cut.Find(".zona-soltar-archivo-error").TextContent.Should().Be("«notas.txt» no es un tipo de archivo admitido.");
    }

    [Fact]
    public async Task Pegar_un_archivo_demasiado_grande_dice_el_limite()
    {
        var cut = Zona(p => p.Add(z => z.TamanoMaximoBytes, 10L * 1024 * 1024));

        await cut.InvokeAsync(() => cut.Instance.RechazarEntrada("tamano", "enorme.pdf"));

        cut.Find(".zona-soltar-archivo-error").TextContent.Should().Be("«enorme.pdf» supera el tamaño máximo de 10 MB.");
    }

    [Fact]
    public async Task El_error_desaparece_al_entrar_un_archivo_valido_y_no_tapa_el_estado_con_archivo()
    {
        var cut = Zona();
        await cut.InvokeAsync(() => cut.Instance.RechazarEntrada("sin-archivo", null));
        cut.FindAll(".zona-soltar-archivo-error").Should().ContainSingle();

        Subir(cut, "foto.png");

        cut.WaitForAssertion(() => cut.FindAll(".zona-soltar-archivo-error").Should().BeEmpty());
    }

    [Fact]
    public async Task El_error_se_ve_tambien_con_la_zona_en_estado_con_archivo()
    {
        var cut = Zona(p => p.Add(z => z.ArchivosAdjuntos, L(new ArchivoAdjuntoVista("a.pdf"))));

        await cut.InvokeAsync(() => cut.Instance.RechazarEntrada("sin-archivo", null));

        cut.Find(".zona-soltar-archivo-con-archivo .zona-soltar-archivo-error").Should().NotBeNull();
        cut.FindAll(".zona-soltar-archivo-fila").Should().ContainSingle("el archivo que ya había sigue ahí");
    }

    // ---- Contrato con el módulo JavaScript ----

    [Fact]
    public void Inicializa_el_modulo_con_la_zona_el_input_y_la_referencia_para_los_avisos()
    {
        var modulo = JSInterop.SetupModule(Modulo);
        modulo.Mode = JSRuntimeMode.Loose;

        Zona();

        var llamada = modulo.Invocations["inicializar"].Should().ContainSingle().Subject;
        llamada.Arguments.Should().HaveCount(3, "zona, input y la referencia .NET a la que JS avisa de origen y rechazos");
        llamada.Arguments[2].Should().BeOfType<Microsoft.JSInterop.DotNetObjectReference<ZonaSoltarArchivo>>();
    }
}
