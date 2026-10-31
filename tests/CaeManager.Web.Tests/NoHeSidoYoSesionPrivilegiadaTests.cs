using Bunit;
using CaeManager.Application.Common;
using CaeManager.Domain.Seguridad;
using CaeManager.Infrastructure.Seguridad;
using CaeManager.Web.Components.Account.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// «No he sido yo» es una vía de seguridad cuya autoridad es el token del buzón: la selección del
/// navegador no la condiciona, ni siquiera una que nombre una Sesión Privilegiada. Esta prueba fija
/// que la página no añade una barrera propia; que la conexión no adopte <c>cae_app_soporte</c> lo
/// fijan <c>SeleccionDescartadaEnIdentificacionTests</c> y <c>FronteraCuentaFirmadaTests</c>.
/// </summary>
public class NoHeSidoYoSesionPrivilegiadaTests : BunitContext
{
    [Fact]
    public async Task Una_seleccion_con_Sesion_Privilegiada_no_impide_confirmar_el_enlace()
    {
        var vigilancia = new VigilanciaFalsa();
        Services.AddSingleton<IVigilanciaInicioSesion>(vigilancia);
        Services.AddSingleton<IClienteActivoSeleccionado>(new SeleccionPrivilegiadaDePrueba());
        Services.AddLocalization();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/cuenta/no-he-sido-yo?userId=123&code=abc");

        var cut = Render<NoHeSidoYo>();
        await cut.Find("form").SubmitAsync();

        vigilancia.Llamadas.Should().Be(1);
    }

    private sealed class VigilanciaFalsa : IVigilanciaInicioSesion
    {
        public int Llamadas { get; private set; }
        public Task<bool> PermiteOrigenAsync(Guid usuarioId, Guid tenantId, CancellationToken ct = default) => Task.FromResult(true);
        public Task RegistrarAsync(Guid? usuarioId, Guid? tenantId, MetodoInicioSesion metodo, ResultadoInicioSesion resultado,
            SenalesInicioSesion senalesConocidas = SenalesInicioSesion.Ninguna, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ResultadoNoHeSidoYo> ConfirmarNoHeSidoYoAsync(string usuarioId, string codigo, CancellationToken ct = default)
        {
            Llamadas++;
            return Task.FromResult(ResultadoNoHeSidoYo.Protegida);
        }
    }
}
