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

    /// <summary>
    /// Reporte de campo (worktree dni-field-character-loss): escribir una
    /// cadena larga de un tirón dejaba el valor final recortado por el
    /// final. Hipótesis descartada aquí: que el settle de una pulsación
    /// antigua (cuyo Task.Delay ya estaba en curso) se aplicara *después*
    /// del settle de una pulsación más reciente y pisara el valor completo
    /// con uno más corto. Cancel() en ManejarCambioAsync es síncrono y se
    /// ejecuta antes de crear el CTS de la pulsación siguiente, y la
    /// notificación lee _ultimoValor en el momento de vencer el debounce
    /// (no el valor capturado al pulsar la tecla), así que no hay ventana
    /// para que un valor viejo gane. Este test dispara 195 pulsaciones
    /// reales y solapadas (sin esperar a que cada una notifique) y
    /// comprueba esa invariante.
    ///
    /// Las aserciones son deliberadamente independientes del reloj. La
    /// versión anterior exigía que NINGUNA notificación intermedia llegase
    /// durante la ráfaga, lo que asume que cada Task.Delay(5) tarda 5ms:
    /// basta con que una sola de esas 195 esperas se estire por encima de
    /// los 300ms del debounce (JIT del primer render, arranque del thread
    /// pool, GC en un runner con contención) para que el debounce venza
    /// legítimamente a mitad de ráfaga y el test falle sin que el
    /// componente haya hecho nada mal — falló así en CI notificando "A", el
    /// primer carácter, es decir en la primera iteración. La invariante que
    /// sí importa se cumple con cualquier reparto de tiempos: toda
    /// notificación es un prefijo de lo tecleado, ninguna retrocede
    /// respecto a la anterior, y la última es la cadena completa.
    /// </summary>
    [Fact]
    public async Task Rafaga_de_pulsaciones_sin_esperar_el_debounce_no_pierde_los_ultimos_caracteres()
    {
        var valoresRecibidos = new List<string>();
        var cut = Render<CampoTexto>(parametros => parametros
            .Add(p => p.Valor, string.Empty)
            .Add(p => p.ValorChanged, v => valoresRecibidos.Add(v)));

        var input = cut.Find("input");

        var textoCompleto = string.Concat(Enumerable.Range(0, 195).Select(i => (char)('A' + (i % 26))));

        var tareas = new List<Task>();
        for (var i = 1; i <= textoCompleto.Length; i++)
        {
            // 5ms entre pulsaciones: mucho más rápido que el debounce
            // (300ms), como una escritura sostenida real, sin esperar a que
            // cada InputAsync complete su propio round-trip antes de
            // disparar la siguiente tecla.
            tareas.Add(input.InputAsync(textoCompleto[..i]));
            await Task.Delay(5);
        }

        // Cada InputAsync devuelve la tarea del propio manejador, así que
        // esto espera al Task.Delay(300) de la última pulsación y a su
        // notificación — la notificación final ya está en la lista aquí, sin
        // depender de cuánto haya tardado la ráfaga.
        await Task.WhenAll(tareas);

        valoresRecibidos.Should().NotBeEmpty("la última pulsación siempre acaba notificando");

        valoresRecibidos.Should().OnlyContain(v => v.Length > 0 && textoCompleto.StartsWith(v, StringComparison.Ordinal),
            "toda notificación debe ser un prefijo de lo tecleado, nunca un valor recortado por el medio o corrompido");

        // El núcleo del reporte de campo: una notificación no puede llegar
        // con menos caracteres que otra anterior. Si el settle de una
        // pulsación vieja pudiera aplicarse después del de una más reciente,
        // la longitud retrocedería justo aquí. No importa cuántas
        // notificaciones intermedias haya provocado el reloj del runner.
        valoresRecibidos.Select(v => v.Length).Should().BeInAscendingOrder(
            "ninguna notificación puede pisar a una anterior con un valor más corto");

        valoresRecibidos[^1].Should().Be(textoCompleto,
            "la escritura sostenida no puede perder los últimos caracteres");

        // Tolerancia explícita en vez de "exactamente una": lo normal es que
        // una ráfaga de ~975ms con debounce de 300ms notifique una sola vez,
        // pero cada espera de 5ms que un runner cargado estire por encima de
        // 300ms añade una notificación intermedia legítima. El umbral (una
        // notificación por cada diez pulsaciones) sigue detectando la
        // regresión que importa —que el debounce desaparezca y se notifique
        // tecla a tecla— y haría falta que veinte esperas distintas se
        // pasaran de 300ms para que fallara por jitter.
        valoresRecibidos.Count.Should().BeLessThan(textoCompleto.Length / 10,
            "el debounce debe seguir colapsando la ráfaga, no notificar tecla a tecla");
    }
}
