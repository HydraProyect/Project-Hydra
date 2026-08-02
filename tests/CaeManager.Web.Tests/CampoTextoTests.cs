using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cubre el debounce de CampoTexto y el fix real de la Fase 11 (ver
/// ROADMAP.md, backlog "#11 Lag al escribir rápido"): escribir no debe
/// disparar ValorChanged en cada tecla, pero perder el foco sí debe volcar
/// el valor pendiente de inmediato, aunque el debounce (300ms) todavía no
/// haya terminado — si no, un clic rápido tras escribir (p. ej. en
/// "Guardar") podía llegar al padre con un valor desactualizado. Los
/// tiempos de espera son reales (Task.Delay real, no un reloj simulado):
/// CampoTexto.razor usa Task.Delay directamente, así que no hay forma de
/// probar el debounce real sin esperar de verdad — sigue siendo mucho más
/// rápido que un test de Playwright con navegador.
/// </summary>
public class CampoTextoTests : BunitContext
{
    [Fact]
    public async Task Escribir_no_dispara_ValorChanged_antes_de_que_termine_el_debounce()
    {
        var valoresRecibidos = new List<string>();
        var cut = Render<CampoTexto>(parametros => parametros
            .Add(p => p.Valor, string.Empty)
            .Add(p => p.ValorChanged, v => valoresRecibidos.Add(v)));

        var input = cut.Find("input");

        // No se espera esta tarea todavía a propósito: ManejarCambioAsync
        // arranca su Task.Delay(300ms) y le devuelve el control al llamador
        // en cuanto llega a ese await, así que podemos comprobar el estado
        // "a mitad de camino" antes de que el debounce termine.
        var tareaInput = input.InputAsync("nuevo valor");

        // 20ms en vez de 100ms: en runners de CI con contención, un
        // Task.Delay corto puede despertarse tarde: cuanto más cerca del
        // umbral de 300ms se comprueba, más fácil que el jitter del
        // scheduler lo cruce y el assert de "todavía no" salga en falso
        // (visto en CI, ver PR #44). 20ms deja el mismo margen que ya usa
        // el otro test de este archivo sin ese problema.
        await Task.Delay(20);
        valoresRecibidos.Should().BeEmpty("todavía no pasaron los 300ms de debounce");

        await tareaInput;
        valoresRecibidos.Should().ContainSingle().Which.Should().Be("nuevo valor");
    }

    [Fact]
    public async Task Perder_el_foco_vuelca_el_valor_pendiente_de_inmediato_sin_esperar_el_debounce()
    {
        var valoresRecibidos = new List<string>();
        var cut = Render<CampoTexto>(parametros => parametros
            .Add(p => p.Valor, string.Empty)
            .Add(p => p.ValorChanged, v => valoresRecibidos.Add(v)));

        var input = cut.Find("input");

        // Se dispara el input pero no se espera su debounce — simula al
        // usuario escribiendo y haciendo clic en otro sitio (p. ej.
        // "Guardar") casi inmediatamente después, antes de los 300ms.
        var tareaInput = input.InputAsync("valor sin confirmar");
        await Task.Delay(20);

        await input.BlurAsync(new FocusEventArgs());

        // El blur cancela el debounce pendiente y notifica ya mismo — no
        // hace falta esperar los 300ms para ver el valor en el padre.
        valoresRecibidos.Should().ContainSingle().Which.Should().Be("valor sin confirmar");

        // La tarea del input original se cancela internamente (capturada por
        // el try/catch de ManejarCambioAsync) y nunca vuelve a invocar
        // ValorChanged — sigue habiendo una única notificación, no dos.
        await tareaInput;
        valoresRecibidos.Should().ContainSingle();
    }

    /// <summary>
    /// P1-18 de docs/business/MATURITY_REVIEW.md: OnBlur es lo que permite
    /// al padre validar "al salir del campo" sin acoplar este componente a
    /// FluentValidation. Debe dispararse al perder el foco, independiente de
    /// ValorChanged, y no antes (mientras el usuario todavía escribe).
    /// </summary>
    [Fact]
    public async Task OnBlur_se_dispara_al_perder_el_foco_y_no_antes()
    {
        var vecesDisparado = 0;
        var cut = Render<CampoTexto>(parametros => parametros
            .Add(p => p.Valor, string.Empty)
            .Add(p => p.ValorChanged, _ => { })
            .Add(p => p.OnBlur, () => vecesDisparado++));

        var input = cut.Find("input");

        await input.InputAsync("algo");
        vecesDisparado.Should().Be(0, "todavía no perdió el foco");

        await input.BlurAsync(new FocusEventArgs());
        vecesDisparado.Should().Be(1);
    }
}
