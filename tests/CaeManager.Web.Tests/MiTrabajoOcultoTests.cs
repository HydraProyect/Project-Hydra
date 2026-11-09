using System.Security.Claims;
using Bunit;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Dashboard.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Mi trabajo oculto para la demo con IT del 2026-11-10 (decisión de Chris, 2026-11-08): el menú, la paleta y los
/// atajos no lo ofrecen, Inicio no enlaza a él, y sus dos rutas (<c>/mi-trabajo</c> y <c>/bandeja</c>) acaban en
/// Gestiones. Es ocultación reversible por <c>MiTrabajo:Oculto</c>, no borrado: con la opción en falso todo vuelve.
/// </summary>
public class MiTrabajoOcultoTests : BunitContext
{
    private static ContextoMenuLateral Contexto(string rol, bool oculto, bool variosTenants = false) =>
        new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "prueba")),
            null, true, false, PerfilVocabularioTenant.Consultora, variosTenants,
            ParticipaEnIncorporacionCartera: rol is Roles.CoordinadorCae or Roles.GestorCae,
            MiTrabajoOculto: oculto);

    private static IServiceProvider Servicios(bool? oculto)
    {
        var servicios = new ServiceCollection();
        if (oculto is { } valor)
            servicios.AddSingleton<IOptions<MiTrabajoOptions>>(Options.Create(new MiTrabajoOptions { Oculto = valor }));
        return servicios.BuildServiceProvider();
    }

    [Fact]
    public void Sin_encender_la_opcion_un_host_con_IOptions_vacio_ve_Mi_trabajo()
    {
        var servicios = new ServiceCollection();
        servicios.AddOptions();

        MiTrabajoVisibilidad.EstaOculto(servicios.BuildServiceProvider()).Should().BeFalse();
    }

    [Fact]
    public void Con_la_opcion_en_falso_o_sin_registrar_Mi_trabajo_sigue_visible()
    {
        MiTrabajoVisibilidad.EstaOculto(Servicios(oculto: false)).Should().BeFalse();
        MiTrabajoVisibilidad.EstaOculto(Servicios(oculto: null)).Should().BeFalse();
        MiTrabajoVisibilidad.EstaOculto(null).Should().BeFalse();
    }

    [Theory]
    [InlineData(Roles.GestorCae, false)]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Administrador, false)]
    [InlineData(Roles.CoordinadorCae, true)]
    public void El_menu_no_muestra_Mi_trabajo_y_si_Gestiones(string rol, bool variosTenants)
    {
        var operacion = CatalogoMenuLateral.Visibles(Contexto(rol, oculto: true, variosTenants))
            .Single(g => g.Grupo.Id == "operacion").Enlaces.Select(e => e.Id).ToList();

        operacion.Should().NotContain("mi-trabajo");
        operacion.Should().Contain("gestiones");
    }

    [Fact]
    public void Con_la_opcion_en_falso_el_menu_devuelve_Mi_trabajo_como_primera_entrada_de_Operacion()
    {
        var operacion = CatalogoMenuLateral.Visibles(Contexto(Roles.GestorCae, oculto: false))
            .Single(g => g.Grupo.Id == "operacion").Enlaces.Select(e => e.Id).ToList();

        operacion.First().Should().Be("mi-trabajo");
    }

    [Theory]
    [InlineData("/bandeja")]
    [InlineData("/mi-trabajo")]
    [InlineData("/bandeja?pedir=x")]
    public void La_paleta_y_los_atajos_no_ofrecen_las_rutas_de_Mi_trabajo_pero_si_Gestiones(string destino)
    {
        var oculto = AdmisionDeDestinos.Predicado(Contexto(Roles.GestorCae, oculto: true));
        var visible = AdmisionDeDestinos.Predicado(Contexto(Roles.GestorCae, oculto: false));

        oculto(destino).Should().BeFalse();
        oculto("/gestiones").Should().BeTrue();
        visible(destino).Should().BeTrue("con la opción en falso la ocultación se revierte entera");
    }

    [Fact]
    public async Task El_contexto_de_la_paleta_y_los_atajos_toma_la_opcion_registrada()
    {
        var servicios = new ServiceCollection();
        servicios.Configure<MiTrabajoOptions>(o => o.Oculto = true);
        var estado = Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.GestorCae)], "prueba"))));

        var contexto = await AdmisionDeDestinos.ContextoAsync(estado, servicios.BuildServiceProvider(), consultarPlataforma: false);

        contexto!.MiTrabajoOculto.Should().BeTrue();
        AdmisionDeDestinos.Predicado(contexto)("/gestiones").Should().BeTrue();
        AdmisionDeDestinos.Predicado(contexto)("/mi-trabajo").Should().BeFalse();
    }

    [Fact]
    public void El_Inicio_de_cartera_no_pinta_el_enlace_a_Mi_trabajo_cuando_no_hay_ruta()
    {
        Services.AddLocalization();
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();

        var sin = Render<InicioCarteraOperador>(p => p.Add(c => c.Empresas, []).Add(c => c.RutaMiTrabajo, null));
        var con = Render<InicioCarteraOperador>(p => p.Add(c => c.Empresas, []).Add(c => c.RutaMiTrabajo, "/mi-trabajo"));

        sin.FindAll("a.inicio-cartera-enlace").Should().BeEmpty();
        con.FindAll("a.inicio-cartera-enlace").Should().ContainSingle();
    }

    [Fact]
    public void El_enlace_Volver_a_Mi_trabajo_no_se_pinta_con_Mi_trabajo_oculto()
    {
        Services.AddLocalization();
        Services.AddSingleton<IOptions<MiTrabajoOptions>>(Options.Create(new MiTrabajoOptions { Oculto = true }));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/centros?volver=%2Fmi-trabajo");

        Render<EnlaceVolverAMiTrabajo>().FindAll("a[data-enlace='volver-mi-trabajo']").Should().BeEmpty();
    }

    private static IServiceProvider HostReal(Dictionary<string, string?>? configuracion = null)
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(configuracion ?? []).Build();
        return new ServiceCollection().RegistrarOpcion(config).BuildServiceProvider();
    }

    [Fact]
    public void El_host_real_arranca_con_Mi_trabajo_oculto_si_la_configuracion_no_dice_nada()
    {
        MiTrabajoVisibilidad.EstaOculto(HostReal()).Should().BeTrue();
    }

    [Fact]
    public void MiTrabajo_Oculto_en_falso_revierte_la_ocultacion_del_host_real()
    {
        MiTrabajoVisibilidad.EstaOculto(HostReal(new() { ["MiTrabajo:Oculto"] = "false" })).Should().BeFalse();
        MiTrabajoVisibilidad.EstaOculto(HostReal(new() { ["MiTrabajo:Oculto"] = "true" })).Should().BeTrue();
    }

    [Fact]
    public void Program_cs_registra_la_opcion_y_nadie_la_quita_en_silencio()
    {
        var carpeta = new DirectoryInfo(AppContext.BaseDirectory);
        while (carpeta is not null && !File.Exists(Path.Combine(carpeta.FullName, "src", "CaeManager.Web", "Program.cs")))
            carpeta = carpeta.Parent;
        carpeta.Should().NotBeNull("el test lee Program.cs de este árbol");

        File.ReadAllText(Path.Combine(carpeta!.FullName, "src", "CaeManager.Web", "Program.cs"))
            .Should().Contain("MiTrabajoVisibilidad.RegistrarOpcion(builder.Services, builder.Configuration)");
    }

    [Fact]
    public void El_enlace_Volver_a_Mi_trabajo_sigue_pintandose_con_la_opcion_en_falso()
    {
        Services.AddLocalization();
        Services.AddSingleton<IOptions<MiTrabajoOptions>>(Options.Create(new MiTrabajoOptions { Oculto = false }));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/centros?volver=%2Fmi-trabajo");

        Render<EnlaceVolverAMiTrabajo>().FindAll("a[data-enlace='volver-mi-trabajo']").Should().ContainSingle();
    }

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }
}

public partial class MiTrabajoGen2Tests
{
    [Fact]
    public void Con_Mi_trabajo_oculto_la_URL_directa_redirige_a_Gestiones_sin_consultar_nada()
    {
        Services.AddSingleton<IOptions<MiTrabajoOptions>>(Options.Create(new MiTrabajoOptions { Oculto = true }));

        var cut = RenderizarPlegado(rol: CaeManager.Infrastructure.Identity.Roles.GestorCae);

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith(MiTrabajoVisibilidad.RutaAlternativa);
        _mediador!.ConsultasCandidatos.Should().Be(0);
        cut.FindAll(".mi-trabajo-fila").Should().BeEmpty();
    }
}
