using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.RegularExpressions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Acabado de interfaz, fichas 12 (estados vacíos), 14 (tablas) y 15 (toasts).
/// <para>
/// Lo que estos tests observan: el temporizador del toast se detiene de verdad
/// mientras el puntero o el foco están encima (C#, con tiempos reales cortos), el
/// anfitrión conecta esos eventos, y las hojas de estilo declaran cada efecto y su
/// anulación con <c>prefers-reduced-motion</c>. Lo que NO observan: cómo se ve; bUnit no
/// aplica CSS, y el destello de fila es JS de navegador (se mide en el navegador).
/// </para>
/// </summary>
public class AcabadoEstadosVaciosTablasYToastsTests : BunitContext
{
    private static readonly TimeSpan Corto = TimeSpan.FromMilliseconds(250);

    public AcabadoEstadosVaciosTablasYToastsTests()
    {
        Services.AddLocalization();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    }

    // ---------- 15: toasts ----------

    [Fact]
    public async Task Sin_pausa_el_toast_se_autodescarta_al_vencer_su_duracion()
    {
        // Control positivo: sin esto, los casos de pausa podrían pasar porque el
        // temporizador nunca descarta nada.
        var servicio = new ToastService(Corto, Corto);
        servicio.Mostrar("hola");

        await EsperarAsync(() => servicio.Mensajes.Count == 0);

        servicio.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public async Task El_puntero_encima_detiene_el_temporizador_y_al_salir_sigue_por_donde_iba()
    {
        var servicio = new ToastService(Corto, Corto);
        servicio.Mostrar("hola");
        var id = servicio.Mensajes.Single().Id;

        servicio.PunteroSobre(id, true);
        await Task.Delay(Corto * 3);
        servicio.Mensajes.Should().ContainSingle("con el puntero encima el aviso no se cierra");

        servicio.PunteroSobre(id, false);
        await EsperarAsync(() => servicio.Mensajes.Count == 0);
        servicio.Mensajes.Should().BeEmpty("al salir el puntero, la cuenta atrás se reanuda");
    }

    [Fact]
    public async Task El_toast_con_accion_no_se_cierra_con_el_puntero_encima()
    {
        var servicio = new ToastService(Corto, Corto);
        servicio.Mostrar("Eliminado", TonoToast.Exito, "Deshacer", () => Task.CompletedTask);
        var id = servicio.Mensajes.Single().Id;

        servicio.PunteroSobre(id, true);
        await Task.Delay(Corto * 3);

        servicio.Mensajes.Should().ContainSingle();
    }

    [Fact]
    public async Task Puntero_y_foco_pausan_por_separado_y_solo_se_reanuda_sin_ninguno()
    {
        var servicio = new ToastService(Corto, Corto);
        servicio.Mostrar("hola");
        var id = servicio.Mensajes.Single().Id;

        servicio.FocoEn(id, true);
        servicio.PunteroSobre(id, true);
        servicio.PunteroSobre(id, false); // el foco sigue dentro: sigue pausado
        await Task.Delay(Corto * 3);
        servicio.Mensajes.Should().ContainSingle("el foco de teclado sigue dentro");

        servicio.FocoEn(id, false);
        await EsperarAsync(() => servicio.Mensajes.Count == 0);
        servicio.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public void Pausar_un_toast_que_no_existe_no_falla()
    {
        var servicio = new ToastService();

        var act = () =>
        {
            servicio.PunteroSobre(Guid.NewGuid(), true);
            servicio.FocoEn(Guid.NewGuid(), false);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public async Task El_anfitrion_conecta_puntero_y_foco_con_el_servicio()
    {
        var servicio = new ToastService(Corto, Corto);
        Services.AddSingleton(servicio);
        var cut = Render<AnfitrionToasts>();

        servicio.Mostrar("hola");
        cut.WaitForState(() => cut.FindAll(".toast").Count == 1);
        cut.Find(".toast").MouseEnter();
        await Task.Delay(Corto * 3);
        servicio.Mensajes.Should().ContainSingle("mouseenter del anfitrión pausa el temporizador");

        cut.Find(".toast").MouseLeave();
        cut.Find(".toast").FocusIn();
        await Task.Delay(Corto * 3);
        servicio.Mensajes.Should().ContainSingle("focusin del anfitrión también lo pausa");

        cut.Find(".toast").FocusOut();
        await EsperarAsync(() => servicio.Mensajes.Count == 0);
        servicio.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public void El_anfitrion_mantiene_aria_live_y_role_status()
    {
        Services.AddSingleton(new ToastService());
        var cut = Render<AnfitrionToasts>();
        Services.GetRequiredService<ToastService>().Mostrar("hola");
        cut.WaitForState(() => cut.FindAll(".toast").Count == 1);

        cut.Find(".anfitrion-toasts").GetAttribute("aria-live").Should().Be("polite");
        cut.Find(".toast").GetAttribute("role").Should().Be("status");
    }

    [Fact]
    public void La_hoja_de_toasts_entra_de_lado_pausa_la_barra_y_se_apaga_sin_movimiento()
    {
        var css = Leer("Components", "DesignSystem", "AnfitrionToasts.razor.css");

        css.Should().MatchRegex(@"@keyframes\s+entrar-toast\s*\{\s*from\s*\{[^}]*translateX\(24px\)");
        css.Should().MatchRegex(@"\.toast:hover\s+\.toast-progreso,\s*\.toast:focus-within\s+\.toast-progreso\s*\{\s*animation-play-state:\s*paused");
        css.Should().MatchRegex(@"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{[^@]*\.toast\s*\{\s*animation:\s*none");
    }

    // ---------- 12: estados vacíos ----------

    [Fact]
    public void La_hoja_del_estado_vacio_entra_con_fundido_y_el_icono_respira_salvo_el_de_severidad()
    {
        var css = Leer("Components", "DesignSystem", "EstadoVacio.razor.css");

        css.Should().MatchRegex(@"\.estado-vacio\s*\{\s*animation:\s*entrar-estado-vacio\b");
        css.Should().MatchRegex(@"\.estado-vacio-icono:not\(\.estado-vacio-icono-acento\)\s*\{\s*animation:\s*respirar-estado-vacio\b[^;]*infinite");
        css.Should().MatchRegex(@"@keyframes\s+entrar-estado-vacio");
        css.Should().MatchRegex(@"@keyframes\s+respirar-estado-vacio");
        css.Should().MatchRegex(@"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{\s*\.estado-vacio,\s*\.estado-vacio-icono:not\(\.estado-vacio-icono-acento\)\s*\{\s*animation:\s*none");
    }

    [Fact]
    public void El_estado_vacio_sigue_pintando_titulo_descripcion_e_icono()
    {
        // El cambio es solo de CSS: el marcado que consumen los ~85 usos no se toca.
        var cut = Render<EstadoVacio>(p => p
            .Add(c => c.Titulo, "Aún no hay documentos")
            .Add(c => c.Descripcion, "Sube el primero."));

        cut.Find(".estado-vacio h3").TextContent.Should().Be("Aún no hay documentos");
        cut.Find(".estado-vacio p").TextContent.Should().Be("Sube el primero.");
        cut.Find(".estado-vacio-icono").GetAttribute("aria-hidden").Should().Be("true");
    }

    // ---------- 14: tablas ----------

    [Fact]
    public void La_hoja_de_tablas_fija_la_cabecera_con_sombra_y_destello_anulados_sin_movimiento()
    {
        var css = Leer("wwwroot", "css", "list-page.css");

        // Control positivo: la regla base existe.
        css.Should().MatchRegex(@"(?m)^\.tabla-datos\s*\{");

        css.Should().MatchRegex(@"(?m)^\.tabla-datos\s+thead\s+th\s*\{\s*position:\s*sticky;\s*top:\s*0;");
        css.Should().MatchRegex(@"@supports\s*\(animation-timeline:\s*scroll\(\)\)\s*\{\s*@media\s*\(prefers-reduced-motion:\s*no-preference\)");
        css.Should().MatchRegex(@"\.tabla-datos\s+tr\.fila-destello\s+td\s*\{\s*animation:\s*destello-fila\s+1\.5s");
        css.Should().MatchRegex(@"@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{\s*\.tabla-datos\s+tr\.fila-destello\s+td\s*\{\s*animation:\s*none");
        // La sombra es un token, declarado en los tres bloques de tokens.css.
        css.Should().Contain("var(--shadow-cabecera-tabla)");
        Regex.Matches(Leer("wwwroot", "css", "tokens.css"), @"--shadow-cabecera-tabla:").Count
            .Should().Be(3, "raíz, tema oscuro y tema claro");
    }

    [Fact]
    public void El_destello_solo_lo_ponen_el_script_delegado_o_la_pantalla_y_se_carga_en_la_app()
    {
        var js = Leer("wwwroot", "js", "destello-fila.js");

        js.Should().Contain("prefers-reduced-motion: reduce");
        js.Should().Contain("fila-destello");
        // Las cuatro condiciones de «acción del usuario» están en el código.
        js.Should().Contain("VENTANA_MS").And.Contain("quitada").And.Contain("tocadas.size !== 1").And.Contain("previas.length === 0");
        Leer("Components", "App.razor").Should().Contain("js/destello-fila.js");
    }

    private static async Task EsperarAsync(Func<bool> condicion)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (!condicion() && DateTime.UtcNow < limite)
            await Task.Delay(25);
    }

    private static string Leer(params string[] partes)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer el fichero");
        return File.ReadAllText(Path.Combine([dir!, "src", "CaeManager.Web", .. partes]));
    }
}
