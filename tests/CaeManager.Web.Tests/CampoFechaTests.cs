using System.Globalization;
using System.Reflection;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

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

    private static ElementReference Referencia(IRenderedComponent<CampoFecha> cut, string campo) =>
        (ElementReference)typeof(CampoFecha).GetField(campo, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cut.Instance)!;

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
    [InlineData("1/2/2026", false)]
    [InlineData("10/1/2030", false)]
    [InlineData("01/02/2026", true)]
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

    // Borrar el «2» de 10/12/2030 dejaba «10/1/2030», que al salir se confirmaba como
    // 10/01/2030. Solo vale dd/mm/aaaa entera: un día o un mes de una cifra no se completa con cero, se queda a la vista
    // con el aviso y NoValidoChanged en true.
    [Theory]
    [InlineData("10/1/2030", "10/01/2030")]
    [InlineData("1/2/2026", "01/02/2026")]
    public async Task Dia_o_mes_de_una_cifra_no_valen_al_salir_y_el_texto_se_queda(string tecleado, string normalizado)
    {
        var recibidos = new List<DateOnly?>();
        var noValido = new List<bool>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, new DateOnly(2030, 12, 10))
            .Add(c => c.ValorChanged, v => recibidos.Add(v))
            .Add(c => c.NoValidoChanged, v => noValido.Add(v)));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = tecleado });
        recibidos.Should().Equal(new DateOnly?[] { null }, "fail-closed: lo que no es una fecha entera dd/mm/aaaa llega como null en la misma pulsación, sin esperar al blur");
        noValido.Should().Equal(true);

        await cut.Find("input").BlurAsync(new FocusEventArgs());

        // bUnit no ve el DOM del navegador (que conserva lo tecleado): lo que se comprueba es que el servidor no le impone
        // la fecha completada con ceros.
        cut.Find("input").GetAttribute("value").Should().NotBe(normalizado, "no se reescribe como otra fecha");
        cut.Find(".campo-mensaje-error").TextContent.Should().Contain("dd/mm/aaaa");
        recibidos.Should().Equal(new DateOnly?[] { null }, "ni la fecha anterior ni 10/01/2030, y salir del campo no vuelve a notificar el mismo null");
        noValido.Should().Equal(true);
    }

    // Pegar «mañana» sobre una fecha se enmascaraba en «» y contaba como vaciado voluntario
    // (NoValidoChanged en false): en Dar acceso, un acceso sin fin. Ahora el texto se queda y es no válido; solo el campo
    // vacío de verdad es «sin fecha».
    [Theory]
    [InlineData("mañana")]
    [InlineData("/")]
    [InlineData(" ")]
    public async Task Un_texto_sin_cifras_sobre_una_fecha_no_es_un_vaciado_voluntario(string pegado)
    {
        var recibidos = new List<DateOnly?>();
        var noValido = new List<bool>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, new DateOnly(2030, 12, 10))
            .Add(c => c.ValorChanged, v => recibidos.Add(v))
            .Add(c => c.NoValidoChanged, v => noValido.Add(v)));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = pegado });
        noValido.Should().Equal(true);
        recibidos.Should().Equal(new DateOnly?[] { null }, "la fecha anterior no sobrevive a un texto que no es fecha, sin esperar al blur");

        await cut.Find("input").BlurAsync(new FocusEventArgs());

        noValido.Should().Equal(true);
        cut.Find(".campo-mensaje-error").Should().NotBeNull();
        recibidos.Should().Equal(new DateOnly?[] { null });

        // Borrarlo a mano sí es el vaciado voluntario: deja de ser no válido (el valor ya era null: no se repite).
        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = string.Empty });
        noValido.Should().Equal(true, false);
        recibidos.Should().Equal(new DateOnly?[] { null });
        cut.FindAll(".campo-mensaje-error").Should().BeEmpty();
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
    public async Task Borrar_a_medias_emite_null_una_vez_y_la_marca_distingue_a_medias_de_vacio()
    {
        var recibidos = new List<DateOnly?>();
        var noValido = new List<bool>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, new DateOnly(2026, 12, 10))
            .Add(c => c.ValorChanged, v => recibidos.Add(v))
            .Add(c => c.NoValidoChanged, v => noValido.Add(v)));
        cut.Find("input").GetAttribute("value").Should().Be("10/12/2026");

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "10/12/202" });
        recibidos.Should().Equal(new DateOnly?[] { null }, "la primera pulsación que deja de ser fecha entera retira la fecha anterior");
        noValido.Should().Equal(true);

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "10/1" });
        recibidos.Should().Equal(new DateOnly?[] { null }, "seguir borrando no repite el mismo null");
        noValido.Should().Equal(new[] { true }, "ni la marca");

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = string.Empty });
        recibidos.Should().Equal(new DateOnly?[] { null }, "vaciar del todo sigue siendo null, que ya tiene el padre");
        noValido.Should().Equal(true, false);
    }

    // La marca de no válido cubre toda ventana en que Valor no es fiable: al dejar de ser fecha entera, primero la marca y luego el
    // null (el padre que anota la fecha previa al texto la lee todavía en su sitio); al volver a serlo, primero la fecha y luego se
    // retira la marca.
    [Fact]
    public async Task La_marca_se_pone_antes_de_retirar_la_fecha_y_se_retira_despues_de_entregarla()
    {
        var secuencia = new List<string>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, new DateOnly(2026, 12, 10))
            .Add(c => c.ValorChanged, v => secuencia.Add($"valor:{v?.ToString("yyyy-MM-dd") ?? "null"}"))
            .Add(c => c.NoValidoChanged, v => secuencia.Add($"marca:{v}")));
        secuencia.Clear();
        secuencia.Add("inicio");

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "10/12/202" });
        secuencia.Should().Equal("inicio", "marca:True", "valor:null");

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "11/12/2026" });
        secuencia.Should().Equal("inicio", "marca:True", "valor:null", "valor:2026-12-11", "marca:False");
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
        cut.Find(".campo-fecha-dia[data-fecha='2026-12-10']").ParentElement!.GetAttribute("aria-selected").Should().Be("true");
        cut.Find(".campo-fecha-dia[data-fecha='2026-12-11']").ParentElement!.GetAttribute("aria-selected").Should().Be("false");

        await cut.Find(".campo-fecha-dia[data-fecha='2026-12-15']").ClickAsync(new MouseEventArgs());

        cut.Find("input").GetAttribute("value").Should().Be("15/12/2026");
        recibidos.Should().Equal(new DateOnly(2026, 12, 15));
        cut.FindAll("[role=dialog]").Should().BeEmpty("elegir cierra el calendario");
        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 1); // al abrir, el día; al cerrar, devolverFoco decide si vuelve el campo (#2194)
        JSInterop.VerifyInvoke("talvegCampoFecha.devolverFoco");
    }

    // WCAG 2.1 AA 1.3.1, 4.1.2 y 3.3.2: la rejilla expone la semántica de cuadrícula (grid,
    // filas de siete celdas y cabeceras con el nombre entero del día), una etiqueta (el mes) y las instrucciones de teclado
    // a la vista y enlazadas por aria-describedby.
    [Fact]
    public async Task La_rejilla_del_calendario_es_una_cuadricula_etiquetada_y_con_instrucciones()
    {
        var cut = Render<CampoFecha>(p => p.Add(c => c.Valor, new DateOnly(2026, 2, 10)));
        await cut.Find("button[data-campo-fecha-boton]").ClickAsync(new MouseEventArgs());

        var rejilla = cut.Find(".campo-fecha-rejilla");
        rejilla.GetAttribute("role").Should().Be("grid");
        cut.Find("#" + rejilla.GetAttribute("aria-labelledby")).TextContent.Should().Contain("2026");
        var instrucciones = cut.Find("#" + rejilla.GetAttribute("aria-describedby"));
        instrucciones.TextContent.Should().Contain("Re Pág").And.Contain("Av Pág").And.Contain("Flechas");

        var filas = rejilla.QuerySelectorAll("[role=row]");
        filas.Should().HaveCount(6, "la cabecera y cinco semanas: febrero de 2026 empieza en domingo (seis celdas vacías) y tiene 28 días")
            .And.OnlyContain(f => f.Children.Length == 7);
        filas[0].QuerySelectorAll("[role=columnheader]").Should().HaveCount(7);
        filas[0].QuerySelectorAll("[role=columnheader]")[0].GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
        rejilla.QuerySelectorAll("[role=gridcell][aria-selected=true]").Should().ContainSingle()
            .Which.QuerySelector("button")!.GetAttribute("data-fecha").Should().Be("2026-02-10");
        rejilla.QuerySelectorAll("button[aria-pressed]").Should().BeEmpty("el estado lo lleva la celda");
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
        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 5); // cuatro movimientos y la apertura en el día
        JSInterop.VerifyInvoke("talvegCampoFecha.devolverFoco"); // el cierre devuelve el foco al campo por el script (#2194)
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

    // El texto que no es fecha llegaba tal cual al formulario, y unos 35 DateOnly.TryParse
    // sensibles a la cultura leían «1/2/26» como 01/02/2026. Contrato: el formulario recibe «» (como el campo nativo),
    // FechaNoValidaChanged lo dice y el aviso lo da el propio campo al salir.
    [Theory]
    [InlineData("1/2/26")]
    [InlineData("15/03")]
    [InlineData("10/12/202")]
    [InlineData("31/02/2026")]
    public async Task CampoTexto_Type_date_entrega_vacio_y_no_el_texto_cuando_no_es_una_fecha(string tecleado)
    {
        var recibidos = new List<string>();
        var noValida = new List<bool>();
        var cut = Render<CampoTexto>(p => p
            .Add(c => c.Type, "date")
            .Add(c => c.Valor, "2026-03-01")
            .Add(c => c.ValorChanged, v => recibidos.Add(v))
            .Add(c => c.FechaNoValidaChanged, v => noValida.Add(v)));

        await cut.Find("input[data-campo-fecha]").InputAsync(new ChangeEventArgs { Value = tecleado });
        await cut.Find("input[data-campo-fecha]").BlurAsync(new FocusEventArgs());

        recibidos.Should().Equal(new[] { string.Empty }, "lo que no es fecha es «sin fecha», nunca el texto");
        noValida.Should().Equal(true);
        cut.Find(".campo-mensaje-error").TextContent.Should().Contain("dd/mm/aaaa");
    }

    // Tras un texto no válido, vaciar el campo tenía que notificar el vacío (quitar la Baja de la ficha).
    [Fact]
    public async Task CampoTexto_Type_date_tras_un_texto_no_valido_vaciar_deja_el_valor_vacio_y_valido()
    {
        var recibidos = new List<string>();
        var noValida = new List<bool>();
        var cut = Render<CampoTexto>(p => p
            .Add(c => c.Type, "date")
            .Add(c => c.Valor, "2026-03-01")
            .Add(c => c.ValorChanged, v => recibidos.Add(v))
            .Add(c => c.FechaNoValidaChanged, v => noValida.Add(v)));

        await cut.Find("input[data-campo-fecha]").InputAsync(new ChangeEventArgs { Value = "31/02/2026" });
        await cut.Find("input[data-campo-fecha]").BlurAsync(new FocusEventArgs());
        await cut.Find("input[data-campo-fecha]").InputAsync(new ChangeEventArgs { Value = string.Empty });
        await cut.Find("input[data-campo-fecha]").BlurAsync(new FocusEventArgs());

        recibidos.Should().Equal(new[] { string.Empty }, "el último valor del formulario es «», no «31/02/2026»");
        noValida.Should().Equal(true, false);
        cut.FindAll(".campo-mensaje-error").Should().BeEmpty();
    }

    // Salir otra vez del campo con el mismo texto no válido no vuelve a notificar (recargaba Auditoría y
    // borraba el error de RevisionIaTab). OnBlur sí: es «perdió el foco».
    [Fact]
    public async Task Salir_otra_vez_con_el_mismo_texto_no_valido_no_vuelve_a_notificar()
    {
        var recibidos = new List<string>();
        var salidas = 0;
        var cut = Render<CampoTexto>(p => p
            .Add(c => c.Type, "date")
            .Add(c => c.Valor, "2026-03-01")
            .Add(c => c.ValorChanged, v => recibidos.Add(v))
            .Add(c => c.OnBlur, () => salidas++));

        await cut.Find("input[data-campo-fecha]").InputAsync(new ChangeEventArgs { Value = "15/03" });
        await cut.Find("input[data-campo-fecha]").BlurAsync(new FocusEventArgs());
        await cut.Find("input[data-campo-fecha]").BlurAsync(new FocusEventArgs());
        await cut.Find("input[data-campo-fecha]").BlurAsync(new FocusEventArgs());

        recibidos.Should().ContainSingle();
        salidas.Should().Be(3);
    }

    [Fact]
    public async Task NoValidoChanged_avisa_solo_al_cambiar_mientras_se_escribe()
    {
        var noValido = new List<bool>();
        var cut = Render<CampoFecha>(p => p.Add(c => c.NoValidoChanged, v => noValido.Add(v)));

        foreach (var texto in new[] { "1", "10", "10/1", "10/12", "10/12/2", "10/12/2026" })
            await cut.Find("input").InputAsync(new ChangeEventArgs { Value = texto });

        noValido.Should().Equal(true, false);
    }

    // Ronda 2 de Codex, ALTO 1 y 2: un campo recreado por un padre que conserva la marca (tramos, filas plegables) no puede
    // retirarla al crearse: la persona no la ha corregido ni vaciado.
    [Fact]
    public async Task Con_NoValido_en_true_el_campo_nuevo_no_retira_la_marca_ni_al_crearse_ni_al_salir_sin_tocarlo()
    {
        var noValido = new List<bool>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, (DateOnly?)null)
            .Add(c => c.NoValido, true)
            .Add(c => c.NoValidoChanged, v => noValido.Add(v)));

        noValido.Should().BeEmpty("el padre ya tiene la marca: el campo nuevo no la contradice");
        cut.Find("input").GetAttribute("value").Should().BeEmpty();
        cut.FindAll(".campo-mensaje-error").Should().ContainSingle("la persona ve por qué está bloqueado");

        await cut.Find("input").BlurAsync(new FocusEventArgs());

        noValido.Should().BeEmpty("entrar y salir sin tocar el campo no es corregirlo ni vaciarlo");
        cut.FindAll(".campo-mensaje-error").Should().ContainSingle();
    }

    [Fact]
    public async Task Con_NoValido_en_true_teclear_una_fecha_entera_retira_la_marca()
    {
        var noValido = new List<bool>();
        var recibidos = new List<DateOnly?>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, (DateOnly?)null)
            .Add(c => c.NoValido, true)
            .Add(c => c.ValorChanged, v => recibidos.Add(v))
            .Add(c => c.NoValidoChanged, v => noValido.Add(v)));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "15/03/2030" });

        noValido.Should().Equal(false);
        recibidos.Should().Equal(new DateOnly(2030, 3, 15));
        cut.FindAll(".campo-mensaje-error").Should().BeEmpty();
    }

    [Fact]
    public async Task Con_NoValido_en_true_teclear_y_vaciar_retira_la_marca()
    {
        var noValido = new List<bool>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, (DateOnly?)null)
            .Add(c => c.NoValido, true)
            .Add(c => c.NoValidoChanged, v => noValido.Add(v)));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "1" });
        noValido.Should().BeEmpty("el padre ya tenía la marca y «1» sigue sin ser una fecha");
        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "" });

        noValido.Should().Equal(false);
        cut.FindAll(".campo-mensaje-error").Should().BeEmpty();
    }

    [Fact]
    public async Task Con_NoValido_en_true_elegir_un_dia_del_calendario_retira_la_marca()
    {
        var noValido = new List<bool>();
        var recibidos = new List<DateOnly?>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, (DateOnly?)null)
            .Add(c => c.NoValido, true)
            .Add(c => c.ValorChanged, v => recibidos.Add(v))
            .Add(c => c.NoValidoChanged, v => noValido.Add(v)));

        await cut.Find("button[data-campo-fecha-boton]").ClickAsync(new MouseEventArgs());
        await cut.Find(".campo-fecha-dia[tabindex='0']").ClickAsync(new MouseEventArgs());

        recibidos.Should().ContainSingle().Which.Should().NotBeNull();
        noValido.Should().Equal(false);
        cut.FindAll(".campo-mensaje-error").Should().BeEmpty();
    }

    [Fact]
    public void Con_NoValido_en_true_y_Valor_no_nulo_el_campo_nace_con_la_marca_el_aviso_y_null_en_el_padre()
    {
        // Ronda 3 de #2185: recrear el campo (cambio de tramo, fila plegada) con la marca y una fecha aún en el padre
        // retiraba la marca, y se guardaba esa fecha, que no era la escrita. Fail-closed: la marca y el aviso siguen, el
        // campo nace vacío y el padre recibe null.
        var noValido = new List<bool>();
        var valores = new List<DateOnly?>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, (DateOnly?)new DateOnly(2030, 1, 2))
            .Add(c => c.NoValido, true)
            .Add(c => c.ValorChanged, v => valores.Add(v))
            .Add(c => c.NoValidoChanged, v => noValido.Add(v)));

        noValido.Should().BeEmpty("la marca del padre se conserva: no se retira al crearse");
        valores.Should().Equal([(DateOnly?)null], "el padre no se queda con una fecha que no se ve");
        cut.Find(".campo-mensaje-error").TextContent.Should().NotBeNullOrWhiteSpace();
        cut.Find("input").GetAttribute("value").Should().BeEmpty();
    }

    [Fact]
    public async Task Teclear_una_fecha_a_medias_notifica_null_y_la_marca_en_la_misma_pulsacion_sin_esperar_al_blur()
    {
        // Rondas 1 a 3 de #2185: con el texto a medias el padre conservaba la fecha anterior hasta el blur, y guardar sin
        // salir del campo la enviaba. Ahora el padre tiene null en cuanto el texto deja de ser una fecha entera.
        var noValido = new List<bool>();
        var valores = new List<DateOnly?>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, (DateOnly?)new DateOnly(2030, 12, 10))
            .Add(c => c.ValorChanged, v => valores.Add(v))
            .Add(c => c.NoValidoChanged, v => noValido.Add(v)));
        noValido.Should().BeEmpty("control: nacer no es un gesto de la persona y no avisa");

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "10/1/2030" });

        valores.Should().Equal([(DateOnly?)null], "sin blur, el padre ya no tiene 10/12/2030");
        noValido.Should().Equal(true);
    }

    [Fact]
    public void Con_NoValido_en_true_un_cambio_de_Valor_del_padre_retira_la_marca()
    {
        var noValido = new List<bool>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, (DateOnly?)null)
            .Add(c => c.NoValido, true)
            .Add(c => c.NoValidoChanged, v => noValido.Add(v)));
        noValido.Should().BeEmpty("control: nace con la marca heredada");

        cut.Render(p => p.Add(c => c.Valor, new DateOnly(2030, 1, 2)).Add(c => c.NoValido, true));

        noValido.Should().Equal(false);
        cut.FindAll(".campo-mensaje-error").Should().BeEmpty();
        cut.Find("input").GetAttribute("value").Should().Be("02/01/2030");
    }

    // Ronda 4 de #2185: avisar false al nacer retiraba la marca del padre cada vez que un cambio de pestaña o de tramo
    // recreaba el campo. Nacer no es un gesto de la persona: con o sin NoValido, el campo nuevo no avisa nada.
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Sin_marca_previa_el_campo_nuevo_no_avisa_su_estado_al_nacer(bool? previo)
    {
        var noValido = new List<bool>();
        Render<CampoFecha>(p => p
            .Add(c => c.Valor, (DateOnly?)null)
            .Add(c => c.NoValido, previo)
            .Add(c => c.NoValidoChanged, v => noValido.Add(v)));

        noValido.Should().BeEmpty();
    }

    [Fact]
    public void Un_campo_nuevo_apagado_no_hereda_la_marca_y_la_retira()
    {
        var noValido = new List<bool>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, (DateOnly?)null)
            .Add(c => c.Deshabilitado, true)
            .Add(c => c.NoValido, true)
            .Add(c => c.NoValidoChanged, v => noValido.Add(v)));

        noValido.Should().Equal(false);
        cut.FindAll(".campo-mensaje-error").Should().BeEmpty();
    }

    // El padre que deja el campo en el mismo valor (null tras un texto no válido) no se distingue de un
    // re-render cualquiera; al deshabilitarse («Sin caducidad», «No lo sé») el campo sí vuelve a su valor.
    [Fact]
    public async Task Al_deshabilitarse_el_campo_vuelve_al_valor_del_padre_y_quita_el_aviso()
    {
        var noValido = new List<bool>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, (DateOnly?)null)
            .Add(c => c.NoValidoChanged, v => noValido.Add(v)));
        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "15" });
        await cut.Find("input").BlurAsync(new FocusEventArgs());
        cut.FindAll(".campo-mensaje-error").Should().ContainSingle("control positivo");

        cut.Render(p => p.Add(c => c.Valor, (DateOnly?)null).Add(c => c.Deshabilitado, true));

        cut.FindAll(".campo-mensaje-error").Should().BeEmpty();
        cut.Find("input").GetAttribute("value").Should().BeEmpty();
        noValido.Should().Equal(true, false);

        cut.Render(p => p.Add(c => c.Valor, (DateOnly?)null).Add(c => c.Deshabilitado, false));
        await cut.Find("input").BlurAsync(new FocusEventArgs());
        cut.FindAll(".campo-mensaje-error").Should().BeEmpty("al volver a habilitarse el campo está vacío");
    }

    // En los extremos del calendario (0001 y 9999) moverse fuera no tira el circuito: no se mueve.
    [Theory]
    [InlineData("01/01/0001", "ArrowLeft")]
    [InlineData("01/01/0001", "ArrowUp")]
    [InlineData("01/01/0001", "PageUp")]
    [InlineData("31/12/9999", "ArrowRight")]
    [InlineData("31/12/9999", "ArrowDown")]
    [InlineData("31/12/9999", "End")]
    [InlineData("31/12/9999", "PageDown")]
    public async Task En_los_extremos_del_calendario_moverse_fuera_no_hace_nada(string fecha, string tecla)
    {
        var cut = Render<CampoFecha>();
        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = fecha });
        await cut.Find("button[data-campo-fecha-boton]").ClickAsync(new MouseEventArgs());
        var antes = cut.Find(".campo-fecha-dia[tabindex='0']").GetAttribute("data-fecha");

        await cut.Find(".campo-fecha-rejilla").KeyDownAsync(new KeyboardEventArgs { Key = tecla });

        cut.Find(".campo-fecha-dia[tabindex='0']").GetAttribute("data-fecha").Should().Be(antes);
    }

    [Theory]
    [InlineData("01/01/0001", -1)]
    [InlineData("31/12/9999", 1)]
    public async Task En_los_extremos_cambiar_de_mes_no_hace_nada(string fecha, int sentido)
    {
        var cut = Render<CampoFecha>();
        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = fecha });
        await cut.Find("button[data-campo-fecha-boton]").ClickAsync(new MouseEventArgs());
        var titulo = cut.Find(".campo-fecha-titulo").TextContent;

        await cut.FindAll("button.campo-fecha-mes")[sentido < 0 ? 0 : 1].ClickAsync(new MouseEventArgs());

        cut.Find(".campo-fecha-titulo").TextContent.Should().Be(titulo);
    }

    [Fact]
    public async Task Minimo_y_Maximo_acotan_los_dias_elegibles()
    {
        var recibidos = new List<DateOnly?>();
        var cut = Render<CampoFecha>(p => p
            .Add(c => c.Valor, new DateOnly(2026, 3, 15))
            .Add(c => c.Minimo, new DateOnly(2026, 3, 10))
            .Add(c => c.Maximo, new DateOnly(2026, 3, 20))
            .Add(c => c.ValorChanged, v => recibidos.Add(v)));
        await cut.Find("button[data-campo-fecha-boton]").ClickAsync(new MouseEventArgs());

        cut.Find(".campo-fecha-dia[data-fecha='2026-03-09']").GetAttribute("aria-disabled").Should().Be("true");
        cut.Find(".campo-fecha-dia[data-fecha='2026-03-21']").GetAttribute("aria-disabled").Should().Be("true");
        cut.Find(".campo-fecha-dia[data-fecha='2026-03-10']").HasAttribute("aria-disabled").Should().BeFalse();
        await cut.Find(".campo-fecha-dia[data-fecha='2026-03-09']").ClickAsync(new MouseEventArgs());
        recibidos.Should().BeEmpty();

        await cut.Find(".campo-fecha-dia[data-fecha='2026-03-20']").ClickAsync(new MouseEventArgs());
        recibidos.Should().Equal(new DateOnly(2026, 3, 20));
    }

    [Fact]
    public void CampoTexto_Type_date_pasa_min_y_max_al_calendario_y_no_al_input()
    {
        var cut = Render<CampoTexto>(p => p
            .Add(c => c.Type, "date")
            .AddUnmatched("min", "2026-03-10")
            .AddUnmatched("max", "2026-03-20"));

        var campo = cut.FindComponent<CampoFecha>().Instance;
        campo.Minimo.Should().Be(new DateOnly(2026, 3, 10));
        campo.Maximo.Should().Be(new DateOnly(2026, 3, 20));
        cut.Find("input").HasAttribute("min").Should().BeFalse();
        cut.Find("input").HasAttribute("max").Should().BeFalse();
    }

    // Salir del calendario con Tab lo cierra sin devolver el foco al campo (el navegador ya lo llevó al
    // control siguiente); Mayúsculas+Tab desde el mes anterior, igual.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Salir_del_calendario_con_Tab_lo_cierra_sin_robar_el_foco(bool haciaAtras)
    {
        var cut = Render<CampoFecha>(p => p.Add(c => c.Valor, new DateOnly(2026, 12, 10)));
        await cut.Find("button[data-campo-fecha-boton]").ClickAsync(new MouseEventArgs());

        if (haciaAtras)
            await cut.FindAll("button.campo-fecha-mes")[0].KeyDownAsync(new KeyboardEventArgs { Key = "Tab", ShiftKey = true });
        else
            await cut.Find(".campo-fecha-rejilla").KeyDownAsync(new KeyboardEventArgs { Key = "Tab" });

        cut.FindAll("[role=dialog]").Should().BeEmpty();
        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 1); // solo el del día al abrir; ninguno al cerrar
        JSInterop.VerifyNotInvoke("talvegCampoFecha.devolverFoco");
    }

    // El foco vuelve al campo por talvegCampoFecha.devolverFoco (campo-fecha.js), que no se lo quita a otro control al que
    // ya se fue mientras llegaba la orden del servidor (#2194; js/campoFecha.test.mjs prueba esa decisión).
    [Fact]
    public async Task Al_cerrar_con_Escape_el_foco_vuelve_al_campo_por_devolverFoco_con_el_campo_y_la_raiz()
    {
        var cut = Render<CampoFecha>(p => p.Add(c => c.Valor, new DateOnly(2026, 12, 10)));
        await cut.Find("button[data-campo-fecha-boton]").ClickAsync(new MouseEventArgs());

        await cut.Find("[role=dialog]").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        var devolver = JSInterop.VerifyInvoke("talvegCampoFecha.devolverFoco");
        devolver.Arguments.Should().HaveCount(2);
        devolver.Arguments[0].Should().BeOfType<ElementReference>().Which.Id.Should().Be(Referencia(cut, "_refInput").Id);
        devolver.Arguments[1].Should().BeOfType<ElementReference>().Which.Id.Should().Be(Referencia(cut, "_refRaiz").Id);
        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 1); // solo el del día al abrir
    }

    [Fact]
    public async Task Con_un_script_sin_devolverFoco_el_foco_vuelve_igual_al_campo()
    {
        JSInterop.SetupVoid("talvegCampoFecha.devolverFoco", _ => true).SetException(new JSException("devolverFoco no existe en el script en caché"));
        var cut = Render<CampoFecha>(p => p.Add(c => c.Valor, new DateOnly(2026, 12, 10)));
        await cut.Find("button[data-campo-fecha-boton]").ClickAsync(new MouseEventArgs());

        await cut.Find("[role=dialog]").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 2); // al abrir, el día; al cerrar, el campo
        JSInterop.Invocations.Last(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase))
            .Arguments[0].Should().BeOfType<ElementReference>().Which.Id.Should().Be(Referencia(cut, "_refInput").Id);
    }

    [Fact]
    public async Task Mayusculas_Tab_dentro_de_la_rejilla_no_cierra_el_calendario()
    {
        var cut = Render<CampoFecha>(p => p.Add(c => c.Valor, new DateOnly(2026, 12, 10)));
        await cut.Find("button[data-campo-fecha-boton]").ClickAsync(new MouseEventArgs());

        await cut.Find(".campo-fecha-rejilla").KeyDownAsync(new KeyboardEventArgs { Key = "Tab", ShiftKey = true });

        cut.FindAll("[role=dialog]").Should().ContainSingle("vuelve al botón del mes siguiente, dentro del calendario");
    }

    // El calendario se ancla a la capa superior del navegador (Popover API) al abrir y se suelta al cerrar.
    [Fact]
    public async Task El_calendario_se_ancla_a_la_capa_superior_al_abrir_y_se_suelta_al_cerrar()
    {
        var cut = Render<CampoFecha>(p => p.Add(c => c.Valor, new DateOnly(2026, 12, 10)));
        await cut.Find("button[data-campo-fecha-boton]").ClickAsync(new MouseEventArgs());
        var id = cut.Find("[role=dialog]").Id;

        var anclar = JSInterop.VerifyInvoke("talvegCampoFecha.anclar");
        anclar.Arguments.Should().HaveCount(3);
        anclar.Arguments[2].Should().Be(id);

        await cut.Find("[role=dialog]").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        JSInterop.VerifyInvoke("talvegCampoFecha.soltar").Arguments.Should().Equal(id);
    }
}
