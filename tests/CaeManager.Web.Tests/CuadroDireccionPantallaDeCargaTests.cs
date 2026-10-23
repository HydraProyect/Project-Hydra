using System.Security.Claims;
using Bunit;
using CaeManager.Application.Dashboard.Queries;
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
/// La pantalla de carga del Cuadro de dirección (petición de Chris, 2026-10-21): mientras el cuadro tarda, la directora ve
/// esqueletos con el hueco de lo que llega, una barra de progreso simulada y un texto por fases, y no una pantalla que parezca
/// colgada. Cada bloque se pinta en cuanto llega lo suyo, y si una parte falla se ve su error y la barra se retira.
/// </summary>
public partial class CuadroDireccionTests
{
    private const string FaseEmpresas = "Reuniendo los datos de las Empresas gestionadas…";
    private static readonly TimeSpan EsperaLarga = TimeSpan.FromSeconds(5);

    private IRenderedComponent<CuadroDireccionPagina> PintarSinEsperar(MediatorFalso falso)
    {
        Services.AddScoped<IMediator>(_ => falso);
        Services.AddScoped<ToastService>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var usuario = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UsuarioPrueba.ToString()), new Claim(ClaimTypes.Role, CaeManager.Infrastructure.Identity.Roles.CoordinadorCae)], "prueba"));
        return Render<CuadroDireccionPagina>(ps => ps.AddCascadingValue(Task.FromResult(new AuthenticationState(usuario))));
    }

    private static void BarraDebeVerse(IRenderedComponent<CuadroDireccionPagina> cut)
    {
        var barra = cut.Find(".progreso-simulado");
        barra.GetAttribute("role").Should().Be("status");
        barra.GetAttribute("aria-live").Should().Be("polite");
        barra.HasAttribute("aria-busy").Should().BeFalse("con aria-busy en la propia región los lectores retienen el texto de fase");
    }

    [Fact]
    public void Mientras_llega_el_cuadro_se_ven_esqueletos_barra_y_aria_busy_y_al_llegar_desaparecen()
    {
        var cuadro = new TaskCompletionSource<CuadroDireccionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = PintarSinEsperar(new MediatorFalso(() => throw new InvalidOperationException("No se pide por la vía síncrona.")) { CuadroPendiente = cuadro });

        cut.Find("[data-carga=cuadro]").GetAttribute("aria-busy").Should().Be("true");
        cut.FindAll("[data-carga=cuadro] .esqueleto").Should().HaveCount(8, "seis KPI y dos gráficas");
        cut.FindAll(".cuadro-kpi").Should().BeEmpty("los KPI reales aún no han llegado");
        BarraDebeVerse(cut);
        cut.Find(".progreso-simulado-mensaje").TextContent.Should().Be(FaseEmpresas);

        cuadro.SetResult(Cuadro(EstadoCifraMensual.Parcial));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".cuadro-kpi").Should().NotBeEmpty();
            cut.FindAll("[data-carga=cuadro]").Should().BeEmpty("el esqueleto del cuadro sale en cuanto llega el mes");
        }, EsperaLarga);
        cut.WaitForAssertion(() => cut.FindAll(".progreso-simulado").Should().BeEmpty("llegó todo: la barra se completa y se retira"), EsperaLarga);
        cut.FindAll("[data-carga=bloque]").Should().BeEmpty();
    }

    [Fact]
    public void Cada_bloque_se_pinta_al_llegar_y_los_que_siguen_en_vuelo_mantienen_su_esqueleto()
    {
        var serie = new TaskCompletionSource<SerieCuadroDireccionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gestiones = new TaskCompletionSource<GestionesPorDiaCuadroDireccionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = PintarSinEsperar(new MediatorFalso(() => Cuadro(EstadoCifraMensual.Parcial)) { SeriePendiente = serie, GestionesPendientes = gestiones });

        cut.WaitForAssertion(() => cut.FindAll("[data-tarjeta=g-evolucion] [data-carga=bloque]").Should().ContainSingle(), EsperaLarga);
        cut.FindAll("[data-kpi=facturacion] .cuadro-kpi-carga").Should().ContainSingle("la facturación sale de la serie");
        cut.FindAll("[data-kpi=trabajadores] .cuadro-kpi-spark-carga").Should().ContainSingle("el minigráfico sale de la serie");
        cut.FindAll("[data-kpi=trabajadores] .cuadro-kpi-valor").Should().ContainSingle().Which.TextContent.Should().NotBeNullOrWhiteSpace("las cifras del mes ya llegaron con el cuadro");
        BarraDebeVerse(cut);

        serie.SetResult(new SerieCuadroDireccionDto([], [], 0));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-tarjeta=g-evolucion] [data-carga=bloque]").Should().BeEmpty("la serie ya llegó: su tarjeta se pinta");
            cut.FindAll("[data-kpi=facturacion] .cuadro-kpi-carga").Should().BeEmpty();
            cut.FindAll("[data-kpi=extranjeros] .cuadro-kpi-carga").Should().BeEmpty("el conteo de extranjeros llegó tras la serie");
        }, EsperaLarga);
        BarraDebeVerse(cut);

        gestiones.SetResult(new GestionesPorDiaCuadroDireccionDto([], 0));

        cut.WaitForAssertion(() => cut.FindAll(".progreso-simulado").Should().BeEmpty(), EsperaLarga);
    }

    [Fact]
    public void Si_falla_el_cuadro_se_ve_el_error_y_se_retira_la_barra()
    {
        var cuadro = new TaskCompletionSource<CuadroDireccionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = PintarSinEsperar(new MediatorFalso(() => throw new InvalidOperationException("No se pide por la vía síncrona.")) { CuadroPendiente = cuadro });
        BarraDebeVerse(cut);

        cuadro.SetException(new InvalidOperationException("La base no respondió."));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No pudimos cargar el cuadro"), EsperaLarga);
        cut.WaitForAssertion(() => cut.FindAll(".progreso-simulado").Should().BeEmpty("fallar no deja la barra avanzando para siempre"), EsperaLarga);
        cut.FindAll("[data-carga=cuadro]").Should().BeEmpty();
    }

    [Fact]
    public void Si_falla_el_conteo_de_extranjeros_su_tarjeta_dice_el_error_y_la_barra_se_retira()
    {
        var cut = PintarSinEsperar(new MediatorFalso(() => Cuadro(EstadoCifraMensual.Parcial))
        {
            Conteo = () => throw new InvalidOperationException("La base no respondió."),
        });

        cut.WaitForAssertion(() => cut.Find("[data-kpi=extranjeros] .cuadro-kpi-sub").TextContent.Should().Contain("No pudimos contar los Trabajadores extranjeros"), EsperaLarga);
        cut.FindAll("[data-kpi=extranjeros] .cuadro-kpi-carga").Should().BeEmpty();
        cut.WaitForAssertion(() => cut.FindAll(".progreso-simulado").Should().BeEmpty(), EsperaLarga);
    }

    [Fact]
    public void Si_fallan_las_gestiones_por_dia_la_barra_se_retira()
    {
        var cut = PintarSinEsperar(new MediatorFalso(() => Cuadro(EstadoCifraMensual.Parcial))
        {
            GestionesDia = () => throw new InvalidOperationException("La base no respondió."),
        });

        cut.WaitForAssertion(() => cut.FindAll(".progreso-simulado").Should().BeEmpty(), EsperaLarga);
        cut.FindAll("[data-carga]").Should().BeEmpty();
    }

    [Fact]
    public void Si_fallan_la_serie_anual_y_las_gestiones_por_dia_la_Demanda_dice_el_error_en_vez_de_quedarse_calculando()
    {
        var cut = PintarSinEsperar(new MediatorFalso(() => Cuadro(EstadoCifraMensual.Parcial))
        {
            Serie = q => q.Meses == 24 ? throw new InvalidOperationException("La base no respondió.") : SerieDe(i => Cifras(EstadoCifraMensual.Estimado, 30)),
            GestionesDia = () => throw new InvalidOperationException("La base no respondió."),
        });
        cut.WaitForAssertion(() => cut.FindAll(".progreso-simulado").Should().BeEmpty(), EsperaLarga);

        IrA(cut, "Demanda");

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-tarjeta=p-estacional]").TextContent.Should().Contain("No pudimos calcular la serie");
            cut.Find("[data-tarjeta=p-calendario]").TextContent.Should().Contain("No pudimos calcular las gestiones por día");
            cut.FindAll("[data-carga=bloque]").Should().BeEmpty("ningún bloque se queda esperando para siempre");
        }, EsperaLarga);
    }

    [Fact]
    public void Si_fallan_las_conversaciones_su_pestana_dice_el_error_y_la_barra_se_retira()
    {
        var cut = PintarSinEsperar(new MediatorFalso(() => Cuadro(EstadoCifraMensual.Parcial))
        {
            Comunicaciones = () => throw new InvalidOperationException("La base no respondió."),
        });
        cut.WaitForAssertion(() => cut.FindAll(".progreso-simulado").Should().BeEmpty(), EsperaLarga);

        IrA(cut, "Comunicaciones");

        cut.Markup.Should().Contain("No pudimos calcular la serie");
        cut.FindAll("[data-carga=bloque]").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- la barra del kit

    [Fact]
    public void ProgresoSimulado_avanza_deprisa_sin_pasar_del_90_cambia_de_fase_y_se_completa_al_terminar()
    {
        string[] fases = ["Uno…", "Dos…", "Tres…"];
        var cut = Render<ProgresoSimulado>(p => p
            .Add(x => x.Activo, true).Add(x => x.Mensajes, fases)
            .Add(x => x.IntervaloAvanceMs, 10).Add(x => x.IntervaloMensajeMs, 40).Add(x => x.PermanenciaFinalMs, 30));

        cut.Find(".progreso-simulado-barra").GetAttribute("aria-hidden").Should().Be("true", "el avance es ficticio: no se anuncia como un valor");

        cut.WaitForAssertion(() => cut.Find(".progreso-simulado-mensaje").TextContent.Should().Be("Tres…", "la última fase se queda puesta"), EsperaLarga);
        cut.WaitForAssertion(() => int.Parse(cut.Find(".progreso-simulado").GetAttribute("data-avance")!).Should().BeGreaterThan(30), EsperaLarga);
        int.Parse(cut.Find(".progreso-simulado").GetAttribute("data-avance")!).Should().BeLessThanOrEqualTo(90, "se frena hacia el 90 % y no llega solo al 100");

        cut.Render(p => p.Add(x => x.Activo, false));

        cut.Find(".progreso-simulado").GetAttribute("data-avance").Should().Be("100");
        cut.WaitForAssertion(() => cut.FindAll(".progreso-simulado").Should().BeEmpty(), EsperaLarga);
    }
}
