using System.Security.Claims;
using Bunit;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CuadroDireccionPagina = CaeManager.Web.Features.CuadroDireccion.Pages.CuadroDireccion;

namespace CaeManager.Web.Tests;

/// <summary>
/// El estado «Calculando…» de las tarjetas de la serie (prueba de flujos 2026-10-21, fila 16): sale en cuanto la serie
/// llega o falla, aunque las consultas que vienen después (conversaciones) sigan en vuelo, y nunca se queda puesto
/// cuando la serie terminó sin meses que enseñar.
/// </summary>
public partial class CuadroDireccionTests
{
    private const string CalculandoLaSerie = "Calculando la serie de los últimos meses…";

    private IRenderedComponent<CuadroDireccionPagina> PintarConSerieEnVuelo(
        TaskCompletionSource<SerieCuadroDireccionDto> serie, TaskCompletionSource<ComunicacionesCuadroDireccionDto> comunicaciones)
    {
        var cuadro = Cuadro(EstadoCifraMensual.Parcial);
        var falso = new MediatorFalso(() => cuadro) { SeriePendiente = serie, ComunicacionesPendientes = comunicaciones };
        Services.AddScoped<IMediator>(_ => falso);
        Services.AddScoped<ToastService>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var usuario = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UsuarioPrueba.ToString()), new Claim(ClaimTypes.Role, CaeManager.Infrastructure.Identity.Roles.CoordinadorCae)], "prueba"));
        var cut = Render<CuadroDireccionPagina>(ps => ps.AddCascadingValue(Task.FromResult(new AuthenticationState(usuario))));
        cut.WaitForAssertion(() => cut.Find("[data-tarjeta=g-evolucion]").TextContent.Should().Contain(CalculandoLaSerie));
        return cut;
    }

    private static string SubtituloFacturacion(IRenderedComponent<CuadroDireccionPagina> cut) =>
        cut.Find("[data-kpi=facturacion] .cuadro-kpi-sub").TextContent.Trim();

    [Fact]
    public void Calculando_sale_en_cuanto_llega_la_serie_aunque_las_conversaciones_sigan_en_vuelo()
    {
        var serie = new TaskCompletionSource<SerieCuadroDireccionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var comunicaciones = new TaskCompletionSource<ComunicacionesCuadroDireccionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = PintarConSerieEnVuelo(serie, comunicaciones);
        SubtituloFacturacion(cut).Should().Be("Calculando…", "control: mientras la serie se calcula, la tarjeta lo dice");

        serie.SetResult(new SerieCuadroDireccionDto([], [], 0));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-tarjeta=g-evolucion]").TextContent.Should().NotContain(CalculandoLaSerie)
                .And.Contain("Sin datos.", "la serie terminó sin meses: no se sigue calculando");
            SubtituloFacturacion(cut).Should().NotBe("Calculando…");
        });
        comunicaciones.Task.IsCompleted.Should().BeFalse("las conversaciones siguen en vuelo: la serie no las espera para pintarse");
    }

    [Fact]
    public void Si_la_serie_falla_la_tarjeta_dice_el_error_y_no_se_queda_calculando()
    {
        var serie = new TaskCompletionSource<SerieCuadroDireccionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var comunicaciones = new TaskCompletionSource<ComunicacionesCuadroDireccionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = PintarConSerieEnVuelo(serie, comunicaciones);

        serie.SetException(new InvalidOperationException("La base no respondió."));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-tarjeta=g-evolucion]").TextContent.Should().Contain("No pudimos calcular la serie").And.NotContain(CalculandoLaSerie);
            SubtituloFacturacion(cut).Should().NotBe("Calculando…");
        });
    }
}
