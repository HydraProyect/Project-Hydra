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
/// navegador no la condiciona, ni siquiera una que nombre una Sesión Privilegiada. Lo que Postgres
/// haga después con el rol de esa conexión queda fuera de esta prueba, que solo fija que la página
/// no añade una barrera propia.
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

    /// <summary>La persona tiene que saber que su cuenta sigue abierta: ni «protegida» ni el icono de éxito.</summary>
    [Fact]
    public async Task Con_las_sesiones_sin_cerrar_la_pagina_no_dice_que_la_cuenta_este_protegida()
    {
        Services.AddSingleton<IVigilanciaInicioSesion>(new VigilanciaFalsa { Resultado = ResultadoNoHeSidoYo.SesionesSinCerrar });
        Services.AddSingleton<IClienteActivoSeleccionado>(new SeleccionPrivilegiadaDePrueba());
        Services.AddLocalization();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/cuenta/no-he-sido-yo?userId=123&code=abc");

        var cut = Render<NoHeSidoYo>();
        await cut.Find("form").SubmitAsync();

        cut.Markup.Should().Contain("No hemos podido cerrar tus sesiones").And.Contain("pueden seguir activas");
        cut.Markup.Should().NotContain("Cuenta protegida");
        cut.Markup.Should().NotContain("Hemos cerrado");
        cut.FindAll(".acceso-exito").Should().BeEmpty();
        var enlace = cut.Find("a.acceso-entrar");
        enlace.GetAttribute("href").Should().Be("/cuenta/olvide-contrasena");
        enlace.TextContent.Should().Be("Crear una contraseña nueva");
    }

    private sealed class VigilanciaFalsa : IVigilanciaInicioSesion
    {
        public ResultadoNoHeSidoYo Resultado { get; init; } = ResultadoNoHeSidoYo.Protegida;
        public int Llamadas { get; private set; }
        public Task<bool> PermiteOrigenAsync(Guid usuarioId, Guid tenantId, CancellationToken ct = default) => Task.FromResult(true);
        public Task RegistrarAsync(Guid? usuarioId, Guid? tenantId, MetodoInicioSesion metodo, ResultadoInicioSesion resultado,
            SenalesInicioSesion senalesConocidas = SenalesInicioSesion.Ninguna, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ResultadoNoHeSidoYo> ConfirmarNoHeSidoYoAsync(string usuarioId, string codigo, CancellationToken ct = default)
        {
            Llamadas++;
            return Task.FromResult(Resultado);
        }
    }
}
