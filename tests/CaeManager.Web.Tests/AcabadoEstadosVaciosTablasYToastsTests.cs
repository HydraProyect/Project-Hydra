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
/// mientras el puntero o el foco están encima (C#, con tiempos reales cortos; la cuenta
/// atrás visible, con reloj de prueba), el
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

    // ---------- Listados 5/7: cuenta atrás visible de «Deshacer» (decisión del 2026-10-08) ----------

    private static readonly TimeSpan DosSegundos = TimeSpan.FromSeconds(2);

    [Fact]
    public void Solo_el_toast_con_accion_ensena_los_segundos_que_le_quedan()
    {
        var servicio = new ToastService();
        Services.AddSingleton(servicio);
        var cut = Render<AnfitrionToasts>();

        servicio.Mostrar("Guardado", TonoToast.Exito);
        servicio.Mostrar("No se pudo", TonoToast.Error);
        servicio.Mostrar("Vehículo eliminado correctamente.", TonoToast.Exito, "Deshacer", () => Task.CompletedTask);
        cut.WaitForState(() => cut.FindAll(".toast").Count == 3);

        var cuenta = cut.FindAll(".toast-cuenta").Should().ContainSingle("solo el aviso con «Deshacer» lleva cuenta atrás").Subject;
        cuenta.TextContent.Should().Be($"{(int)ToastService.DuracionAutoDescarteConAccion.TotalSeconds} s",
            "el aviso nace enseñando el tiempo configurado entero");
        cuenta.GetAttribute("aria-hidden").Should().Be("true",
            "la región es aria-live: un número que cambia cada segundo no se anuncia");
        cuenta.ParentElement!.QuerySelector(".toast-accion")!.TextContent.Should().Be("Deshacer");

        servicio.SegundosRestantes(servicio.Mensajes[0].Id).Should().BeNull("un aviso sin acción no enseña cuenta");
        servicio.SegundosRestantes(servicio.Mensajes[1].Id).Should().BeNull("un error no se autodescarta");
    }

    // Los dos casos que siguen afirman un número de segundos, así que el tiempo lo mueven ellos
    // (RelojConTemporizadores) en vez de esperarlo: contra reloj real, con la máquina cargada, el
    // hilo de la prueba llegaba tarde a mirar y el número ya era otro (medido el 2026-10-09: 5 y 1
    // fallos de 12 pasadas con la CPU saturada). Las esperas que quedan no son tiempo del aviso,
    // solo el margen para que corra la continuación que el temporizador despierta.
    private static readonly TimeSpan MargenDeContinuacion = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task La_cuenta_baja_segundo_a_segundo_y_el_anfitrion_la_repinta()
    {
        var reloj = new RelojConTemporizadores();
        var servicio = new ToastService(Corto, DosSegundos, reloj);
        Services.AddSingleton(servicio);
        var cut = Render<AnfitrionToasts>();

        servicio.Mostrar("Eliminado", TonoToast.Exito, "Deshacer", () => Task.CompletedTask);
        cut.WaitForState(() => cut.FindAll(".toast-cuenta").Count == 1, MargenDeContinuacion);
        cut.Find(".toast-cuenta").TextContent.Should().Be("2 s");

        reloj.Avanzar(TimeSpan.FromMilliseconds(900));
        servicio.SegundosRestantes(servicio.Mensajes.Single().Id).Should().Be(2, "el número cambia con el segundo, no antes");
        cut.Find(".toast-cuenta").TextContent.Should().Be("2 s");

        reloj.Avanzar(TimeSpan.FromMilliseconds(100));
        cut.WaitForAssertion(() => cut.Find(".toast-cuenta").TextContent.Should().Be("1 s"), MargenDeContinuacion);

        await EsperarAsync(() => reloj.Pendientes == 1, MargenDeContinuacion);
        reloj.Pendientes.Should().Be(1, "tras repintar, el servicio arma el tramo del último segundo");
        servicio.Mensajes.Should().ContainSingle("le queda un segundo entero");

        reloj.Avanzar(TimeSpan.FromSeconds(1));
        await EsperarAsync(() => servicio.Mensajes.Count == 0, MargenDeContinuacion);
        servicio.Mensajes.Should().BeEmpty("al agotarse la cuenta el aviso se descarta, como antes");
    }

    [Fact]
    public async Task Con_el_puntero_encima_la_cuenta_no_baja_y_al_salir_sigue_por_donde_iba()
    {
        var reloj = new RelojConTemporizadores();
        var servicio = new ToastService(Corto, DosSegundos, reloj);
        servicio.Mostrar("Eliminado", TonoToast.Exito, "Deshacer", () => Task.CompletedTask);
        var id = servicio.Mensajes.Single().Id;

        reloj.Avanzar(TimeSpan.FromMilliseconds(300));
        servicio.PunteroSobre(id, true);
        reloj.Avanzar(TimeSpan.FromSeconds(10));
        servicio.SegundosRestantes(id).Should().Be(2, "con el puntero encima el número no baja");
        servicio.Mensajes.Should().ContainSingle("ni el aviso caduca, por mucho que pase");

        // Quedaban 1,7 s al entrar el puntero: al salir, el cambio de segundo llega a los 0,7 s,
        // no al segundo entero (eso sería empezar de nuevo) ni de inmediato (eso sería haber
        // descontado la pausa).
        servicio.PunteroSobre(id, false);
        reloj.Avanzar(TimeSpan.FromMilliseconds(600));
        servicio.SegundosRestantes(id).Should().Be(2, "al salir el puntero, la cuenta sigue por donde iba");
        reloj.Avanzar(TimeSpan.FromMilliseconds(200));
        servicio.SegundosRestantes(id).Should().Be(1, "al salir el puntero, la cuenta sigue por donde iba");

        await EsperarAsync(() => reloj.Pendientes == 1, MargenDeContinuacion);
        servicio.Mensajes.Should().ContainSingle("le queda el último segundo");
        reloj.Avanzar(TimeSpan.FromSeconds(1));
        await EsperarAsync(() => servicio.Mensajes.Count == 0, MargenDeContinuacion);
        servicio.Mensajes.Should().BeEmpty("y se descarta cuando se agota lo que quedaba");
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

    private static Task EsperarAsync(Func<bool> condicion) => EsperarAsync(condicion, TimeSpan.FromSeconds(5));

    private static async Task EsperarAsync(Func<bool> condicion, TimeSpan margen)
    {
        var limite = DateTime.UtcNow + margen;
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
