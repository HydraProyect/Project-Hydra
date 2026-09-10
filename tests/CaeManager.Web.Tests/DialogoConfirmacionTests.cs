using AngleSharp.Dom;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Guarda de reentrada de <see cref="DialogoConfirmacion"/>: un segundo clic en
/// «confirmar» mientras el primero sigue en curso no puede volver a disparar el
/// comando destructivo.
///
/// <para>
/// <b>Por qué aquí y no en cada pantalla:</b> medido el 2026-09-10, ninguna de
/// las pantallas que usan este diálogo tenía guarda propia. Los botones se
/// desactivan con <c>EnProgreso</c>, pero ese valor lo pone el padre y solo
/// llega al diálogo cuando el padre vuelve a pintar; y <see cref="Boton"/>
/// mantiene su <c>@onclick</c> enganchado aunque esté <c>disabled</c>.
/// </para>
///
/// <para>
/// <b>Cómo se reproduce la carrera:</b> <c>EnProgreso</c> se deja en falso a
/// propósito, que es exactamente el estado del diálogo mientras el repintado del
/// padre no ha llegado. Así el botón sigue pulsable y lo único que puede frenar
/// el segundo clic es la guarda — si esta prueba pasase gracias al botón
/// desactivado, no estaría midiendo la guarda.
/// </para>
/// </summary>
public class DialogoConfirmacionTests : BunitContext
{
    /// <summary><see cref="Modal"/> importa dialogo-foco.js al abrirse; queda fuera de lo que se observa aquí.</summary>
    public DialogoConfirmacionTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private IRenderedComponent<DialogoConfirmacion> Renderizar(Func<Task> alConfirmar) =>
        Render<DialogoConfirmacion>(p => p
            .Add(c => c.Visible, true)
            .Add(c => c.Titulo, "¿Eliminar la tarifa?")
            .Add(c => c.EnProgreso, false)
            .Add(c => c.OnConfirmar, EventCallback.Factory.Create(this, alConfirmar)));

    private static IElement BotonConfirmar(IRenderedComponent<DialogoConfirmacion> cut) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Eliminar");

    [Fact]
    public async Task Un_segundo_clic_antes_de_que_termine_el_primero_no_confirma_dos_veces()
    {
        var invocaciones = 0;
        var enCurso = new TaskCompletionSource();
        var cut = Renderizar(async () =>
        {
            invocaciones++;
            await enCurso.Task;
        });

        var primero = BotonConfirmar(cut).ClickAsync(new MouseEventArgs());
        var segundo = BotonConfirmar(cut).ClickAsync(new MouseEventArgs());

        invocaciones.Should().Be(1,
            "el segundo clic llega mientras el primero espera al servidor: sin guarda, el comando destructivo saldría dos veces");

        enCurso.SetResult();
        await primero;
        await segundo;

        invocaciones.Should().Be(1, "el segundo clic se descartó, no quedó encolado para después");
    }

    /// <summary>
    /// La guarda se baja al terminar. Si no, tras cualquier confirmación —o tras
    /// un fallo— el diálogo quedaría inservible para un reintento.
    /// </summary>
    [Fact]
    public async Task Terminada_una_confirmacion_se_puede_volver_a_confirmar()
    {
        var invocaciones = 0;
        var cut = Renderizar(() =>
        {
            invocaciones++;
            return Task.CompletedTask;
        });

        await BotonConfirmar(cut).ClickAsync(new MouseEventArgs());
        await BotonConfirmar(cut).ClickAsync(new MouseEventArgs());

        invocaciones.Should().Be(2);
    }

    [Fact]
    public async Task Si_la_confirmacion_falla_la_guarda_se_libera_igualmente()
    {
        var invocaciones = 0;
        var cut = Renderizar(() =>
        {
            invocaciones++;
            throw new InvalidOperationException("Fallo simulado del padre.");
        });

        await FluentActions.Awaiting(() => BotonConfirmar(cut).ClickAsync(new MouseEventArgs()))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => BotonConfirmar(cut).ClickAsync(new MouseEventArgs()))
            .Should().ThrowAsync<InvalidOperationException>();

        invocaciones.Should().Be(2, "el finally baja la guarda aunque el padre lance");
    }
}
