using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lo que ve quien opera un tenant por una ventana de soporte cuando esa ventana
/// se acaba: antes no veía nada. Un circuito abierto seguía pintando datos del
/// tenant visitado y la recarga lo devolvía a «Organización principal» sin
/// explicación (medido en local, 2026-09-21).
///
/// <para>
/// Aquí se fija el componente con reloj y expiración controlados. Que el aviso
/// de la respuesta tras recargar salga lo comprueba el E2E de
/// <c>VentanaDeSoporteTests</c>; lo que no puede hacer un E2E es distinguir por
/// qué camino apareció «terminó» en un circuito abierto (reloj del componente o
/// evento de la revalidación) — medido: un circuito que se inicializa después de
/// caducar llega al mismo texto por el reloj, y una mutación que quitaba el
/// evento seguía en verde. Por eso el camino del evento se prueba aquí.
/// </para>
/// </summary>
public class AvisoVentanaSoporteTests : BunitContext
{
    private static readonly DateTime Ahora = new(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);

    private sealed class RelojManual(DateTime ahora) : TimeProvider
    {
        private Action? _alTic;

        public DateTime Ahora { get; set; } = ahora;

        public override DateTimeOffset GetUtcNow() => new(Ahora, TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _alTic = () => callback(state);
            return new TemporizadorManual();
        }

        public void Tic() => _alTic!();

        private sealed class TemporizadorManual : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Ventana(DateTime? expira) : IVentanaDeSoporteActual
    {
        public Task<DateTime?> ObtenerExpiracionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(expira);
    }

    private (ClienteActivoSeleccionado Seleccion, RelojManual Reloj) Preparar(DateTime? expira)
    {
        var usuario = Guid.NewGuid();
        var proveedor = new EphemeralDataProtectionProvider();
        var token = ClienteActivoSeleccionado.Proteger(proveedor, usuario, Guid.NewGuid(), null);

        var contexto = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, usuario.ToString())], "prueba")),
        };
        contexto.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={Uri.EscapeDataString(token)}";

        var seleccion = new ClienteActivoSeleccionado(new HttpContextAccessor { HttpContext = contexto }, proveedor);
        seleccion.TenantIdSeleccionado.Should().NotBeNull(
            "el control positivo: sin selección viva el componente no tiene nada que avisar y las pruebas no observarían nada");

        var reloj = new RelojManual(Ahora);
        Services.AddSingleton<TimeProvider>(reloj);
        Services.AddSingleton<IClienteActivoSeleccionado>(seleccion);
        Services.AddSingleton<IVentanaDeSoporteActual>(new Ventana(expira));

        return (seleccion, reloj);
    }

    [Fact]
    public void Lejos_de_caducar_no_avisa_de_nada()
    {
        Preparar(Ahora.AddHours(3));

        var celda = Render<AvisoVentanaSoporte>();

        celda.Markup.Should().NotContain("aviso-ventana-soporte");
    }

    [Fact]
    public void A_diez_minutos_o_menos_avisa_con_los_minutos_que_quedan()
    {
        Preparar(Ahora.AddMinutes(4).AddSeconds(30));

        var celda = Render<AvisoVentanaSoporte>();

        celda.Markup.Should().Contain("La ventana de soporte termina en 5 minutos");
        celda.Markup.Should().NotContain("--terminada");
    }

    [Fact]
    public void El_reloj_del_componente_pasa_de_aviso_previo_a_terminada()
    {
        var (_, reloj) = Preparar(Ahora.AddMinutes(5));
        var celda = Render<AvisoVentanaSoporte>();
        celda.Markup.Should().Contain("termina en 5 minutos");

        reloj.Ahora = Ahora.AddMinutes(5).AddSeconds(1);
        celda.InvokeAsync(reloj.Tic);

        celda.WaitForAssertion(() =>
            celda.Markup.Should().Contain("La ventana de soporte terminó"));
        celda.Markup.Should().Contain("Volver a mi organización principal");
    }

    [Fact]
    public void Si_la_revalidacion_retira_la_seleccion_lo_dice_sin_esperar_al_reloj()
    {
        // El reloj marca 3 h de margen: lo único que puede producir «terminó»
        // aquí es el evento de la selección retirada. Es la prueba que un E2E
        // no puede hacer (ver el comentario de clase).
        var (seleccion, _) = Preparar(Ahora.AddHours(3));
        var celda = Render<AvisoVentanaSoporte>();
        celda.Markup.Should().NotContain("--terminada");

        celda.InvokeAsync(seleccion.Invalidar);

        celda.WaitForAssertion(() =>
            celda.Markup.Should().Contain("La ventana de soporte terminó"));
    }

    [Fact]
    public void Una_seleccion_retirada_que_no_era_de_soporte_no_habla_de_ventana_de_soporte()
    {
        // Delegación revocada, cartera cerrada...: sin caducidad de soporte el
        // texto es el general. Decir «ventana de soporte» aquí sería falso.
        var (seleccion, _) = Preparar(expira: null);
        var celda = Render<AvisoVentanaSoporte>();

        celda.InvokeAsync(seleccion.Invalidar);

        celda.WaitForAssertion(() =>
            celda.Markup.Should().Contain("ya no está vigente"));
        celda.Markup.Should().NotContain("ventana de soporte");
    }

    [Fact]
    public void Invalidar_sin_seleccion_previa_no_dispara_el_evento()
    {
        // El middleware y el handler llaman a Invalidar() también cuando no
        // había nada: no puede convertirse en un aviso.
        var disparos = 0;
        var seleccion = new ClienteActivoSeleccionado(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new EphemeralDataProtectionProvider());
        seleccion.SeleccionRetirada += () => disparos++;

        seleccion.Invalidar();

        disparos.Should().Be(0);
    }

    [Theory]
    [InlineData(MotivoFinDeAcceso.VentanaDeSoporte, "La ventana de soporte terminó")]
    [InlineData(MotivoFinDeAcceso.AccesoNoVigente, "ya no está vigente")]
    public void El_texto_del_aviso_depende_del_motivo(MotivoFinDeAcceso motivo, string esperado) =>
        AvisoFinDeAcceso.Texto(motivo).Should().Contain(esperado).And.Contain("organización principal");
}
