using CaeManager.Application.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// <see cref="PuertaAccesoDatos.RetenerHastaSalir"/>: la capa de datos retiene
/// la conexión durante la operación que tiene la puerta (decisión D3 del plan
/// de rendimiento). Lo que estos tests fijan es lo que la conexión necesita
/// para no cruzar de una operación a otra: solo retiene el flujo de la
/// operación en curso, se libera siempre al salir —también con excepción— y
/// antes de que la puerta deje entrar a la siguiente o de que
/// <see cref="PuertaAccesoDatos.CerrarAsync"/> dé el scope por libre.
/// </summary>
public class PuertaAccesoDatosRetencionTests
{
    [Fact]
    public void Fuera_de_una_operacion_no_retiene_ni_adquiere()
    {
        var puerta = new PuertaAccesoDatos();
        var adquirido = false;

        var retenido = puerta.RetenerHastaSalir(new object(), () => adquirido = true, () => ValueTask.CompletedTask);

        retenido.Should().BeFalse();
        adquirido.Should().BeFalse("sin operación no hay quien libere lo adquirido");
    }

    [Fact]
    public async Task Retiene_una_vez_por_recurso_y_libera_al_salir()
    {
        var puerta = new PuertaAccesoDatos();
        var recurso = new object();
        var adquisiciones = 0;
        var liberaciones = 0;

        await puerta.EjecutarAsync(async () =>
        {
            puerta.RetenerHastaSalir(recurso, () => adquisiciones++, Liberar).Should().BeTrue();
            puerta.RetenerHastaSalir(recurso, () => adquisiciones++, Liberar).Should().BeFalse(
                "el segundo comando de la operación encuentra la conexión ya retenida");
            await Task.Yield();
            liberaciones.Should().Be(0, "dentro de la operación la conexión sigue retenida");
        });

        adquisiciones.Should().Be(1);
        liberaciones.Should().Be(1);

        ValueTask Liberar()
        {
            liberaciones++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Lo_retenido_en_una_anidada_se_libera_al_salir_la_externa()
    {
        var puerta = new PuertaAccesoDatos();
        var liberaciones = 0;
        var liberadoAlSalirLaAnidada = -1;

        await puerta.EjecutarAsync(async () =>
        {
            await puerta.EjecutarAsync(() =>
            {
                puerta.RetenerHastaSalir(new object(), () => { }, () =>
                {
                    liberaciones++;
                    return ValueTask.CompletedTask;
                }).Should().BeTrue();
                return Task.CompletedTask;
            });
            liberadoAlSalirLaAnidada = liberaciones;
        });

        liberadoAlSalirLaAnidada.Should().Be(0, "la anidada no es la dueña de la operación");
        liberaciones.Should().Be(1);
    }

    [Fact]
    public async Task Con_excepcion_libera_y_propaga_la_excepcion_original()
    {
        var puerta = new PuertaAccesoDatos();
        var liberado = false;

        var accion = () => puerta.EjecutarAsync(() =>
        {
            puerta.RetenerHastaSalir(new object(), () => { }, () =>
            {
                liberado = true;
                return ValueTask.CompletedTask;
            });
            throw new InvalidOperationException("fallo a mitad de la operación");
        });

        await accion.Should().ThrowAsync<InvalidOperationException>().WithMessage("fallo a mitad de la operación");
        liberado.Should().BeTrue("una conexión retenida no puede quedarse abierta tras una excepción");
    }

    [Fact]
    public async Task La_siguiente_operacion_no_entra_hasta_que_la_anterior_libero()
    {
        var puerta = new PuertaAccesoDatos();
        var liberando = new TaskCompletionSource();
        var liberacionTerminada = false;
        var siguienteVioLiberado = (bool?)null;

        var primera = puerta.EjecutarAsync(() =>
        {
            puerta.RetenerHastaSalir(new object(), () => { }, async () =>
            {
                await liberando.Task;
                liberacionTerminada = true;
            });
            return Task.CompletedTask;
        });

        var segunda = puerta.EjecutarAsync(() =>
        {
            siguienteVioLiberado = liberacionTerminada;
            return Task.CompletedTask;
        });

        await Task.Delay(50);
        siguienteVioLiberado.Should().BeNull("la segunda espera a que la primera suelte la puerta");

        liberando.SetResult();
        await Task.WhenAll(primera, segunda);

        siguienteVioLiberado.Should().BeTrue(
            "la operación siguiente del scope nunca hereda la conexión de la anterior");
    }

    [Fact]
    public async Task CerrarAsync_vuelve_con_lo_retenido_ya_liberado()
    {
        var puerta = new PuertaAccesoDatos();
        var liberando = new TaskCompletionSource();
        var dentro = new TaskCompletionSource();
        var liberado = false;

        var operacion = puerta.EjecutarAsync(async () =>
        {
            puerta.RetenerHastaSalir(new object(), () => { }, async () =>
            {
                await liberando.Task;
                liberado = true;
            });
            dentro.SetResult();
            await Task.Yield();
        });

        await dentro.Task;
        var cierre = puerta.CerrarAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        cierre.IsCompleted.Should().BeFalse("el scope no se puede disponer con la conexión aún retenida");

        liberando.SetResult();
        (await cierre).Should().BeTrue();
        liberado.Should().BeTrue();
        await operacion;
    }

    [Fact]
    public async Task Un_flujo_rezagado_de_una_operacion_terminada_no_retiene()
    {
        var puerta = new PuertaAccesoDatos();
        var sigue = new TaskCompletionSource();
        Task<bool>? rezagado = null;

        await puerta.EjecutarAsync(() =>
        {
            // Tarea hija que sigue viva cuando la operación ya salió: hereda
            // el AsyncLocal, pero la operación ya no es la que tiene la puerta.
            rezagado = Task.Run(async () =>
            {
                await sigue.Task;
                return puerta.RetenerHastaSalir(new object(), () => { }, () => ValueTask.CompletedTask);
            });
            return Task.CompletedTask;
        });

        sigue.SetResult();
        (await rezagado!).Should().BeFalse("nadie liberaría lo que retuviera");
    }

    [Fact]
    public async Task Un_flujo_rezagado_que_vuelve_a_entrar_espera_a_la_operacion_que_tiene_la_puerta()
    {
        var puerta = new PuertaAccesoDatos();
        var sigue = new TaskCompletionSource();
        var segundaDentro = new TaskCompletionSource();
        var sueltaSegunda = new TaskCompletionSource();
        var rezagadoEntro = false;
        Task? rezagado = null;

        await puerta.EjecutarAsync(() =>
        {
            rezagado = Task.Run(async () =>
            {
                await sigue.Task;
                await puerta.EjecutarAsync(() =>
                {
                    rezagadoEntro = true;
                    return Task.CompletedTask;
                });
            });
            return Task.CompletedTask;
        });

        var segunda = puerta.EjecutarAsync(async () =>
        {
            segundaDentro.SetResult();
            await sueltaSegunda.Task;
        });
        await segundaDentro.Task;

        sigue.SetResult();
        await Task.Delay(50);
        rezagadoEntro.Should().BeFalse(
            "un flujo de una operación terminada no es reentrada: no corre a la vez que la que tiene la puerta");

        sueltaSegunda.SetResult();
        await Task.WhenAll(segunda, rezagado!);
        rezagadoEntro.Should().BeTrue("entra como operación nueva cuando la puerta queda libre");
    }

    /// <summary>
    /// Segunda revisión de Codex: mientras la operación libera lo retenido
    /// (cierra la conexión, restaura el sellado), un flujo rezagado que despierte
    /// no puede pasar por reentrada y usar el DbContext a la vez que la limpieza.
    /// </summary>
    [Fact]
    public async Task Un_flujo_rezagado_que_entra_mientras_la_operacion_libera_espera_a_que_termine_la_limpieza()
    {
        var puerta = new PuertaAccesoDatos();
        var liberando = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sueltaLiberacion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rezagadoEntro = false;
        Task? rezagado = null;

        var operacion = puerta.EjecutarAsync(() =>
        {
            puerta.RetenerHastaSalir(new object(), () => { }, async () =>
            {
                liberando.SetResult();
                await sueltaLiberacion.Task;
            }).Should().BeTrue("control: el recurso queda retenido hasta salir");

            rezagado = Task.Run(async () =>
            {
                await liberando.Task;
                await puerta.EjecutarAsync(() =>
                {
                    rezagadoEntro = true;
                    return Task.CompletedTask;
                });
            });
            return Task.CompletedTask;
        });

        await liberando.Task;
        await Task.Delay(50);
        rezagadoEntro.Should().BeFalse(
            "durante la limpieza la operación ya no es la vigente: el flujo rezagado espera al semáforo");

        sueltaLiberacion.SetResult();
        await Task.WhenAll(operacion, rezagado!);
        rezagadoEntro.Should().BeTrue("entra como operación nueva cuando la limpieza termina y la puerta queda libre");
    }

    [Fact]
    public async Task Si_adquirir_lanza_no_queda_nada_retenido()
    {
        var puerta = new PuertaAccesoDatos();
        var recurso = new object();
        var liberaciones = 0;

        await puerta.EjecutarAsync(() =>
        {
            var accion = () => puerta.RetenerHastaSalir(
                recurso, () => throw new InvalidOperationException("no abre"), Liberar);
            accion.Should().Throw<InvalidOperationException>();

            puerta.RetenerHastaSalir(recurso, () => { }, Liberar).Should().BeTrue(
                "el intento fallido no cuenta como retenido");
            return Task.CompletedTask;
        });

        liberaciones.Should().Be(1, "solo se libera lo que sí se adquirió");

        ValueTask Liberar()
        {
            liberaciones++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Si_liberar_lanza_la_puerta_se_suelta_igual()
    {
        var puerta = new PuertaAccesoDatos();

        var accion = () => puerta.EjecutarAsync(() =>
        {
            puerta.RetenerHastaSalir(new object(), () => { },
                () => throw new InvalidOperationException("no cierra"));
            return Task.CompletedTask;
        });
        await accion.Should().ThrowAsync<InvalidOperationException>();

        var entra = puerta.EjecutarAsync(() => Task.FromResult(1));
        (await Task.WhenAny(entra, Task.Delay(TimeSpan.FromSeconds(5)))).Should().BeSameAs(entra,
            "una puerta que no se suelta deja el scope entero bloqueado");
    }
}
