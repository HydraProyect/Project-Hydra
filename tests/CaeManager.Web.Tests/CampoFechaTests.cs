using System.Globalization;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// CampoFecha: la máscara dd/mm/aaaa (tecleo, borrado, pegado y barra a mano), la validación al salir del campo, el
/// calendario (abrir, elegir, teclado y Escape) y que el valor llega al formulario, también a través de
/// CampoTexto Type="date". El script del navegador (wwwroot/js/campo-fecha.js) no corre en bUnit: aquí se ve la copia en C#
/// de la máscara, que es la que decide si el texto es una fecha.
/// </summary>
public class CampoFechaTests : BunitContext
{
    public CampoFechaTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("10", "10")]
    [InlineData("101", "10/1")]
    [InlineData("1012", "10/12")]
    [InlineData("10122", "10/12/2")]
    [InlineData("10122026", "10/12/2026")]
    [InlineData("101220261", "10/12/2026")]
    [InlineData("10/12/2026", "10/12/2026")]
    [InlineData("10/", "10/")]
    [InlineData("1/", "01/")]
    [InlineData("1/2/2026", "1/2/2026")]
    [InlineData("10/1/2026", "10/1/2026")]
    [InlineData("10//12", "10/12")]
    [InlineData("10-12-2026", "10/12/2026")]
    [InlineData("10.12.2026", "10/12/2026")]
    [InlineData("2026-12-10", "10/12/2026")]
    [InlineData(" 2026-12-10 ", "10/12/2026")]
    [InlineData("ab10x12", "10/12")]
    [InlineData("", "")]
    [InlineData("/", "")]
    public void La_mascara_pone_las_barras_cuando_toca(string tecleado, string esperado) =>
        MascaraFecha.Aplicar(tecleado).Should().Be(esperado);

    [Theory]
    [InlineData("31/02/2026", false)]
    [InlineData("29/02/2027", false)]
    [InlineData("29/02/2028", true)]
    [InlineData("10/12/202", false)]
    [InlineData("10/13/2026", false)]
    [InlineData("10/12/2026", true)]
    [InlineData("1/2/2026", true)]
    [InlineData("1/2/26", false)]
    public void Solo_una_fecha_entera_y_existente_se_interpreta(string texto, bool esFecha) =>
        MascaraFecha.TryInterpretar(texto, out _).Should().Be(esFecha);

    [Fact]
    public async Task Al_teclear_las_cifras_se_insertan_las_barras_y_se_notifica_la_fecha()
    {
        var recibidos = new List<DateOnly?>();
        var cut = Render<CampoFecha>(p => p.Add(c => c.ValorChanged, v => recibidos.Add(v)));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "101" });
        cut.Find("input").GetAttribute("value").Should().Be("10/1");
        recibidos.Should().BeEmpty("a medias no hay fecha que notificar");

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "10122026" });
        cut.Find("input").GetAttribute("value").Should().Be("10/12/2026");
        recibidos.Should().Equal(new DateOnly(2026, 12, 10));
    }

    [Theory]
    [InlineData("10/12/2026")]
    [InlineData("10122026")]
    [InlineData("2026-12-10")]
    public async Task Pegar_la_fecha_entera_la_notifica(string pegado)
    {
        var recibidos = new List<DateOnly?>();
        var cut = Render<CampoFecha>(p => p.Add(c => c.ValorChanged, v => recibidos.Add(v)));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = pegado });

        recibidos.Should().Equal(new DateOnly(2026, 12, 10));
        if (pegado != "10/12/2026") cut.Find("input").GetAttribute("value").Should().Be("10/12/2026");
    }

    [Fact]
    public async Task La_barra_escrita_a_mano_cierra_el_grupo_y_completa_el_cero()
    {
        var recibidos = new List<DateOnly?>();
        var cut = Render<CampoFecha>(p => p.Add(c => c.ValorChanged, v => recibidos.Add(v)));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "1/" });
        cut.Find("input").GetAttribute("value").Should().Be("01/");

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "01/2/" });
        cut.Find("input").GetAttribute("value").Should().Be("01/02/");

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "01/02/2026" });
        recibidos.Should().Equal(new DateOnly(2026, 2, 1));
    }

    [Fact]
    public async Task Dia_y_mes_de_una_cifra_valen_al_salir_y_quedan_en_dd_mm_aaaa()
    {
        var recibidos = new List<DateOnly?>();
        var cut = Render<CampoFecha>(p => p.Add(c => c.ValorChanged, v => recibidos.Add(v)));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "1/2/2026" });
        recibidos.Should().BeEmpty("mientras se escribe solo cuenta la fecha entera dd/mm/aaaa");

        await cut.Find("input").BlurAsync(new FocusEventArgs());

        cut.Find("input").GetAttribute("value").Should().Be("01/02/2026");
        cut.FindAll(".campo-mensaje-error").Should().BeEmpty();
        recibidos.Should().Equal(new DateOnly(2026, 2, 1));
    }

    [Fact]
    public async Task La_mascara_del_navegador_da_lo_mismo_y_deja_el_cursor_donde_toca()
    {
        // js/campoFecha.test.mjs evalúa wwwroot/js/campo-fecha.js con un document falso (mismo patrón que
        // ShellDesplazaContenidoTests). Necesita Node; sin él esta prueba falla en vez de saltarse.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx"))) dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        var prueba = Path.Combine(dir!, "tests", "CaeManager.Web.Tests", "js", "campoFecha.test.mjs");
        File.Exists(prueba).Should().BeTrue();
        var info = new System.Diagnostics.ProcessStartInfo("node", $"--test \"{prueba}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        System.Diagnostics.Process proceso;
        try { proceso = System.Diagnostics.Process.Start(info)!; }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Hace falta Node en el PATH para ejecutar js/campoFecha.test.mjs: " + ex.Message, ex);
        }

        using (proceso)
        {
            var salida = proceso.StandardOutput.ReadToEndAsync();
            var error = proceso.StandardError.ReadToEndAsync();
            var termino = proceso.WaitForExit(TimeSpan.FromSeconds(60));
            if (!termino) proceso.Kill(entireProcessTree: true);
            termino.Should().BeTrue("node --test no debe colgarse");
            var textoSalida = await salida;
            var textoError = await error;
            proceso.ExitCode.Should().Be(0, "las pruebas de Node fallaron:\n" + textoSalida + textoError);
            textoSalida.Should().MatchRegex(@"(?m)\bpass [1-9]\d*\s*$", "node --test debe haber ejecutado casos");
        }
    }

    [Fact]
    public async Task Borrar_a_medias_no_notifica_y_vaciar_notifica_null()
    {
        var recibidos = new List<DateOnly?>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, new DateOnly(2026, 12, 10))
            .Add(c => c.ValorChanged, v => recibidos.Add(v)));
        cut.Find("input").GetAttribute("value").Should().Be("10/12/2026");

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "10/12/202" });
        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "10/1" });
        recibidos.Should().BeEmpty("borrar cifra a cifra no es todavía una fecha nueva");

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = string.Empty });
        recibidos.Should().Equal((DateOnly?)null);
    }

    [Fact]
    public async Task Una_fecha_imposible_se_avisa_al_salir_y_no_mientras_se_escribe()
    {
        var recibidos = new List<DateOnly?>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, new DateOnly(2026, 1, 15))
            .Add(c => c.ValorChanged, v => recibidos.Add(v)));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "31/02/2026" });
        cut.FindAll(".campo-mensaje-error").Should().BeEmpty("mientras se escribe no se juzga");
        cut.Find("input").HasAttribute("aria-invalid").Should().BeFalse();

        await cut.Find("input").BlurAsync(new FocusEventArgs());

        cut.Find(".campo-mensaje-error").GetAttribute("role").Should().Be("alert");
        cut.Find("input").GetAttribute("aria-invalid").Should().Be("true");
        cut.Find("input").GetAttribute("aria-describedby").Should().Be(cut.Find(".campo-mensaje-error").Id);
        recibidos.Should().Equal(new DateOnly?[] { null }, "el formulario no puede seguir creyendo que vale la fecha anterior");

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "28/02/2026" });
        cut.FindAll(".campo-mensaje-error").Should().BeEmpty("al corregir desaparece el aviso");
        recibidos.Should().Equal(null, new DateOnly(2026, 2, 28));
    }

    [Fact]
    public async Task Un_re_render_del_padre_con_el_mismo_valor_no_borra_lo_tecleado()
    {
        var cut = Render<CampoFecha>(p => p.Add(c => c.Valor, (DateOnly?)null));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "15" });
        cut.Render(p => p.Add(c => c.Valor, (DateOnly?)null));
        await cut.Find("input").BlurAsync(new FocusEventArgs());

        // Si el re-render hubiera devuelto el texto a «», salir del campo vacío no avisaría de nada.
        cut.FindAll(".campo-mensaje-error").Should().ContainSingle("«15» sigue en el campo y no es una fecha");
    }

    [Fact]
    public void El_padre_que_cambia_el_valor_por_su_cuenta_lo_impone()
    {
        var cut = Render<CampoFecha>(p => p.Add(c => c.Valor, new DateOnly(2026, 1, 15)));

        cut.Render(p => p.Add(c => c.Valor, new DateOnly(2026, 3, 1)));

        cut.Find("input").GetAttribute("value").Should().Be("01/03/2026");
    }

    [Fact]
    public async Task El_boton_abre_el_calendario_y_elegir_un_dia_rellena_el_campo()
    {
        var recibidos = new List<DateOnly?>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Etiqueta, "Fecha de emisión")
            .Add(c => c.Valor, new DateOnly(2026, 12, 10))
            .Add(c => c.ValorChanged, v => recibidos.Add(v)));
        var boton = cut.Find("button[data-campo-fecha-boton]");
        boton.GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
        boton.GetAttribute("aria-expanded").Should().Be("false");
        boton.GetAttribute("aria-describedby").Should().Be(cut.Find("label").Id, "el lector de pantalla dice de qué campo es");
        cut.FindAll("[role=dialog]").Should().BeEmpty();

        await boton.ClickAsync(new MouseEventArgs());

        cut.Find("button[data-campo-fecha-boton]").GetAttribute("aria-expanded").Should().Be("true");
        var dialogo = cut.Find("[role=dialog]");
        dialogo.Id.Should().Be(cut.Find("button[data-campo-fecha-boton]").GetAttribute("aria-controls"));
        cut.Find(".campo-fecha-dia[tabindex='0']").GetAttribute("data-fecha").Should().Be("2026-12-10", "abre en el día escrito");
        cut.Find(".campo-fecha-dia[data-fecha='2026-12-10']").GetAttribute("aria-pressed").Should().Be("true");

        await cut.Find(".campo-fecha-dia[data-fecha='2026-12-15']").ClickAsync(new MouseEventArgs());

        cut.Find("input").GetAttribute("value").Should().Be("15/12/2026");
        recibidos.Should().Equal(new DateOnly(2026, 12, 15));
        cut.FindAll("[role=dialog]").Should().BeEmpty("elegir cierra el calendario");
        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 2); // al abrir, el día; al cerrar, el campo
    }

    [Fact]
    public async Task Con_el_teclado_se_mueve_por_los_dias_y_Escape_cierra_y_devuelve_el_foco()
    {
        var recibidos = new List<DateOnly?>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, new DateOnly(2026, 1, 31))
            .Add(c => c.ValorChanged, v => recibidos.Add(v)));
        await cut.Find("button[data-campo-fecha-boton]").ClickAsync(new MouseEventArgs());

        await cut.Find(".campo-fecha-rejilla").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" });
        cut.Find(".campo-fecha-dia[tabindex='0']").GetAttribute("data-fecha").Should().Be("2026-02-01", "pasa al mes siguiente");

        await cut.Find(".campo-fecha-rejilla").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.Find(".campo-fecha-dia[tabindex='0']").GetAttribute("data-fecha").Should().Be("2026-02-08");

        await cut.Find(".campo-fecha-rejilla").KeyDownAsync(new KeyboardEventArgs { Key = "Home" });
        cut.Find(".campo-fecha-dia[tabindex='0']").GetAttribute("data-fecha").Should().Be("2026-02-02", "la semana empieza en lunes");

        await cut.Find(".campo-fecha-rejilla").KeyDownAsync(new KeyboardEventArgs { Key = "PageUp" });
        cut.Find(".campo-fecha-dia[tabindex='0']").GetAttribute("data-fecha").Should().Be("2026-01-02");
        cut.FindAll(".campo-fecha-dia[tabindex='0']").Should().ContainSingle("un solo día en el orden de tabulación");

        await cut.Find("[role=dialog]").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        cut.FindAll("[role=dialog]").Should().BeEmpty();
        recibidos.Should().BeEmpty("Escape no elige");
        cut.Find("input").GetAttribute("value").Should().Be("31/01/2026");
        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 6); // cuatro movimientos y la apertura en el día; el cierre en el campo
    }

    [Fact]
    public async Task Los_dias_y_el_titulo_salen_en_la_cultura_activa()
    {
        var anterior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ca-ES");
            var cut = Render<CampoFecha>(p => p.Add(c => c.Valor, new DateOnly(2026, 12, 10)));
            await cut.Find("button[data-campo-fecha-boton]").ClickAsync(new MouseEventArgs());

            cut.Find(".campo-fecha-titulo").TextContent.Should().StartWith("Desembre").And.EndWith("2026");
            cut.Find(".campo-fecha-dia[data-fecha='2026-12-10']").GetAttribute("aria-label").Should().Contain("desembre");
        }
        finally
        {
            CultureInfo.CurrentCulture = anterior;
        }
    }

    [Fact]
    public async Task CampoTexto_Type_date_sigue_enlazando_el_texto_ISO_del_formulario()
    {
        var recibidos = new List<string>();
        var cut = Render<CampoTexto>(p => p
            .Add(c => c.Type, "date")
            .Add(c => c.Etiqueta, "Fecha de alta")
            .Add(c => c.Valor, "2026-03-01")
            .Add(c => c.ValorChanged, v => recibidos.Add(v)));

        cut.Find("input[data-campo-fecha]").GetAttribute("value").Should().Be("01/03/2026");
        cut.Find("input[data-campo-fecha]").GetAttribute("type").Should().Be("text", "sin el selector nativo, que no admite máscara");

        await cut.Find("input[data-campo-fecha]").InputAsync(new ChangeEventArgs { Value = "15032026" });
        recibidos.Should().Equal("2026-03-15");

        await cut.Find("input[data-campo-fecha]").InputAsync(new ChangeEventArgs { Value = string.Empty });
        recibidos.Should().Equal("2026-03-15", string.Empty);
    }
}
