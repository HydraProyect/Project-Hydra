using System.Security.Claims;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using CaeManager.Web.Features.Comunicaciones.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Estructura de la bandeja de <c>/comunicaciones</c> según la maqueta del 2026-11-01 (PR-1): columna de cartera, lista siempre visible,
/// «Tabla» como modo secundario y las vistas del selector «Vista». Fija lo que ya existía en main y no debe perderse (los filtros, el
/// buzón personal, el divisor D-26, las macros, los atajos de la lista) y lo nuevo; lo que la decisión D2 del 2026-11-17 deja fuera
/// (posponer, fusionar, dividir, conversación paralela, borradores, no deseado, Deshacer, «Recordar si no contestan») no se dibuja.
/// </summary>
public partial class ComunicacionesGen2Tests
{
    private static Task AbrirMenuVistasAsync(IRenderedComponent<Bandeja> cut) =>
        cut.Find(".bandeja-vista-b").ClickAsync(new MouseEventArgs());

    private static async Task ElegirVistaAsync(IRenderedComponent<Bandeja> cut, string texto)
    {
        await AbrirMenuVistasAsync(cut);
        await cut.FindAll(".bandeja-vistas-lista [role=menuitem]").Single(b => b.TextContent.Trim() == texto)
            .ClickAsync(new MouseEventArgs());
    }

    private static ObtenerConversacionesQuery UltimaListaPedida(MediadorControlado mediador) => ConsultasDeLista(mediador).Last();

    private (IRenderedComponent<Bandeja> Cut, MediadorControlado Mediador) RenderizarConUnaConversacion(
        Escenario? escenario = null, string url = "comunicaciones")
    {
        escenario ??= new Escenario();
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        escenario.Conversaciones.Add(conversacion);
        escenario.Detalle = _ => DetalleDe(conversacion);
        return Renderizar(escenario, url);
    }

    /// <summary>Concede todo salvo la política del Cuadro de dirección: el resto de la página se autoriza como en las demás pruebas.</summary>
    private sealed class AutorizacionSinCuadroDeDireccion : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(policyName == CaeManager.Web.Services.Autorizacion.PoliticaCuadroDireccion.Nombre
                ? AuthorizationResult.Failed()
                : AuthorizationResult.Success());
    }

    // ---------------------------------------------------------------- columna de cartera

    [Fact]
    public void La_cartera_agrupa_Mi_cartera_y_Personal_con_sus_destinos()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        cut.FindAll(".bandeja-filtros .bandeja-nav-grupo-titulo").Select(t => t.TextContent.Trim())
            .Should().Equal("Mi cartera", "Personal");
        cut.FindAll(".bandeja-filtros .bandeja-nav-item").Select(t => t.TextContent.Trim())
            .Should().Contain(["Clientes", "Mi buzón personal"]);
        cut.Find(".bandeja-nav-item-activo").TextContent.Trim().Should().Be("Clientes");
    }

    [Fact]
    public async Task El_selector_Vista_ofrece_las_cuatro_vistas_y_Sin_asignar_ya_no_es_una_pildora_aparte()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        cut.Find(".bandeja-vista-n").TextContent.Trim().Should().Be("Todas");
        cut.FindAll(".bandeja-vistas-lista").Should().BeEmpty("el menú está plegado hasta pulsar «Vista»");
        cut.Find(".bandeja-vista-b").GetAttribute("aria-expanded").Should().Be("false");

        await AbrirMenuVistasAsync(cut);

        cut.Find(".bandeja-vista-b").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll(".bandeja-vistas-lista [role=menuitem]").Select(b => b.TextContent.Trim())
            .Should().Equal("Todas", "Asignadas a mí", "Sin asignar", "Esperando respuesta del Interlocutor");
        cut.FindAll(".bandeja-filtros-form .bandeja-toggle").Should().BeEmpty(
            "las píldoras de asignación ya no están sueltas: son vistas del selector");
    }

    [Theory]
    [InlineData("Asignadas a mí", true, false, false)]
    [InlineData("Sin asignar", false, true, false)]
    [InlineData("Esperando respuesta del Interlocutor", false, false, true)]
    [InlineData("Todas", false, false, false)]
    public async Task Cada_vista_fija_sus_filtros_en_la_consulta_de_la_lista(
        string vista, bool asignadasAMi, bool sinAsignar, bool esperando)
    {
        var (cut, mediador) = RenderizarConUnaConversacion();
        if (vista == "Todas") await ElegirVistaAsync(cut, "Sin asignar");

        await ElegirVistaAsync(cut, vista);

        var consulta = UltimaListaPedida(mediador);
        consulta.SoloAsignadasAMi.Should().Be(asignadasAMi);
        consulta.SoloSinAsignar.Should().Be(sinAsignar);
        consulta.SoloEsperandoCliente.Should().Be(esperando);
        cut.Find(".bandeja-vista-n").TextContent.Trim().Should().Be(vista);
        cut.FindAll(".bandeja-vistas-lista").Should().BeEmpty("elegir una vista cierra el menú");
    }

    [Fact]
    public async Task Las_vistas_son_exclusivas_elegir_otra_quita_la_anterior()
    {
        var (cut, mediador) = RenderizarConUnaConversacion();

        await ElegirVistaAsync(cut, "Sin asignar");
        await ElegirVistaAsync(cut, "Esperando respuesta del Interlocutor");

        var consulta = UltimaListaPedida(mediador);
        consulta.SoloEsperandoCliente.Should().BeTrue();
        consulta.SoloSinAsignar.Should().BeFalse();
        consulta.SoloAsignadasAMi.Should().BeFalse();
    }

    [Fact]
    public async Task Escape_cierra_el_menu_de_vistas()
    {
        var (cut, _) = RenderizarConUnaConversacion();
        await AbrirMenuVistasAsync(cut);
        cut.FindAll(".bandeja-vistas-lista").Should().ContainSingle();

        await cut.Find(".bandeja-vista-w").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        cut.FindAll(".bandeja-vistas-lista").Should().BeEmpty();
    }

    [Fact]
    public void Fuera_de_plazo_solo_aparece_con_conversaciones_que_incumplen_su_plazo()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        cut.FindAll(".bandeja-crit-b").Should().BeEmpty("sin ninguna fuera de plazo no hay botón");
    }

    [Fact]
    public async Task Fuera_de_plazo_muestra_su_recuento_y_al_pulsarlo_acota_la_lista()
    {
        var escenario = new Escenario
        {
            Interceptar = p => p is ObtenerConversacionesQuery { SoloFueraDeSla: true, TamanoPagina: 1 }
                ? Task.FromResult<object?>(new ResultadoPaginado<ConversacionListaDto>(new List<ConversacionListaDto>(), 3, 1, 1))
                : null
        };
        var (cut, mediador) = RenderizarConUnaConversacion(escenario);

        cut.Find(".bandeja-crit-b .bandeja-crit-n").TextContent.Trim().Should().Be("3");
        cut.Find(".bandeja-crit-b").TextContent.Should().Contain("Fuera de plazo");
        cut.Find(".bandeja-crit-b").GetAttribute("aria-pressed").Should().Be("false");
        UltimaListaPedida(mediador).SoloFueraDeSla.Should().BeFalse();

        await cut.Find(".bandeja-crit-b").ClickAsync(new MouseEventArgs());

        UltimaListaPedida(mediador).SoloFueraDeSla.Should().BeTrue();
        cut.Find(".bandeja-crit-b").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find(".bandeja-lis-cab-titulo").TextContent.Trim().Should().Be("Fuera de plazo");

        await cut.Find(".bandeja-crit-b").ClickAsync(new MouseEventArgs());

        UltimaListaPedida(mediador).SoloFueraDeSla.Should().BeFalse("pulsarlo otra vez lo quita");
    }

    [Fact]
    public void Mas_filtros_queda_plegado_bajo_el_selector_y_conserva_Estado_Intencion_Mes_y_Cliente_empresarial()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        var detalles = cut.Find(".bandeja-filtros .bandeja-filtros-form .bandeja-filtros-avanzados");
        detalles.TagName.Should().Be("DETAILS");
        detalles.HasAttribute("open").Should().BeFalse("la maqueta lo pliega bajo «Vista»");
        detalles.QuerySelector("summary")!.TextContent.Trim().Should().Be("Más filtros");
        cut.FindAll(".bandeja-filtros-avanzados select").Should().HaveCount(3, "Estado, Intención CAE y Cliente empresarial");
        cut.FindAll(".bandeja-filtros-avanzados input[type=month]").Should().ContainSingle();
        cut.FindAll(".bandeja-filtros-avanzados label").Select(l => l.TextContent)
            .Should().Contain(t => t.Contains("Estado")).And.Contain(t => t.Contains("Mes")).And.Contain(t => t.Contains("Cliente empresarial"));
    }

    [Fact]
    public void El_buscador_vive_en_la_cabecera_de_la_pagina_no_en_la_cartera()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        cut.FindAll(".bandeja-cabecera-pagina input[type=search]").Should().ContainSingle();
        cut.FindAll(".bandeja-filtros input[type=search]").Should().BeEmpty();
    }

    [Fact]
    public void Los_atajos_de_la_lista_viven_en_la_cartera_de_Clientes()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        cut.FindComponents<AtajosListaTeclado>().Should().ContainSingle();
    }

    [Fact]
    public void Sin_companeros_la_cartera_no_dibuja_su_seccion()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        cut.FindAll(".bandeja-filtros details.companeros").Should().BeEmpty();
    }

    [Fact]
    public void Los_companeros_del_Operador_CAE_siguen_en_la_cartera()
    {
        var escenario = new Escenario();
        escenario.Companeros.Add(Marta);
        var (cut, _) = RenderizarConUnaConversacion(escenario);

        cut.Find(".bandeja-filtros details.companeros .companero-nombre").TextContent.Trim().Should().Be("Marta Solà");
    }

    [Fact]
    public void El_pie_de_la_cartera_lleva_a_las_macros_y_con_la_politica_tambien_al_Cuadro_de_direccion()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        var enlaces = cut.FindAll(".bandeja-cartera-pie a");
        enlaces[0].GetAttribute("href").Should().Be("/comunicaciones/macros");
        enlaces[0].TextContent.Trim().Should().Be("Gestionar macros →");
        enlaces[1].GetAttribute("href").Should().Be("/direccion");
        enlaces[1].TextContent.Trim().Should().Be("Métricas del equipo en el Cuadro de dirección →");
    }

    [Fact]
    public void Sin_la_politica_del_Cuadro_de_direccion_el_pie_no_ofrece_ese_enlace()
    {
        Services.AddSingleton<IAuthorizationService, AutorizacionSinCuadroDeDireccion>();
        var (cut, _) = RenderizarConUnaConversacion();

        var enlaces = cut.FindAll(".bandeja-cartera-pie a");
        enlaces.Should().ContainSingle().Which.GetAttribute("href").Should().Be("/comunicaciones/macros");
        cut.Markup.Should().NotContain("/direccion");
    }

    // ---------------------------------------------------------------- lista y centro

    [Fact]
    public void Sin_conversacion_abierta_la_lista_esta_a_la_vista_y_el_centro_dice_que_no_hay_ninguna()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        cut.FindAll(".bandeja-layout.bandeja-modo-lista").Should().ContainSingle();
        cut.FindAll(".bandeja-lista .bandeja-fila").Should().ContainSingle();
        cut.Find(".bandeja-lis-cab-titulo").TextContent.Trim().Should().Be("Todas");
        cut.Find(".bandeja-lis-cab small").TextContent.Trim().Should().Be("1 conversación");
        cut.FindAll(".tabla-conversaciones").Should().BeEmpty("la tabla es un modo secundario");
        cut.Find(".bandeja-centro").TextContent.Should().Contain("Ninguna conversación abierta").And.Contain("Elige una de la lista.");
    }

    [Fact]
    public async Task Con_una_conversacion_abierta_la_lista_sigue_a_la_vista()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        await SeleccionarFila(cut, "Documentación pendiente");

        cut.FindAll(".bandeja-layout.bandeja-modo-hilo").Should().ContainSingle();
        cut.FindAll(".bandeja-lista .bandeja-fila").Should().ContainSingle();
        cut.FindAll(".bandeja-lista .bandeja-fila-activa").Should().ContainSingle("la fila de la conversación abierta se marca");
        cut.FindAll(".bandeja-centro .bandeja-volver").Should().ContainSingle();
    }

    [Fact]
    public async Task Tabla_abre_el_modo_tabla_y_Ver_como_lista_vuelve_a_la_lista()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        await AbrirTablaAsync(cut);

        cut.FindAll(".bandeja-layout.bandeja-modo-tabla").Should().ContainSingle();
        cut.FindAll(".bandeja-lista").Should().BeEmpty("en la tabla la lista lateral se retira");
        cut.FindAll(".bandeja-divisor").Should().BeEmpty();
        cut.FindAll(".bandeja-centro .tabla-conversaciones").Should().ContainSingle();
        var volver = cut.Find(".bandeja-tabla-cabecera .bandeja-mini");
        volver.TextContent.Trim().Should().Be("Ver como lista");

        await volver.ClickAsync(new MouseEventArgs());

        cut.FindAll(".bandeja-layout.bandeja-modo-lista").Should().ContainSingle();
        cut.FindAll(".bandeja-lista .bandeja-fila").Should().ContainSingle();
        cut.FindAll(".tabla-conversaciones").Should().BeEmpty();
    }

    [Fact]
    public async Task Tabla_con_una_conversacion_abierta_la_cierra_y_muestra_la_tabla()
    {
        var (cut, _) = RenderizarConUnaConversacion();
        await SeleccionarFila(cut, "Documentación pendiente");

        await AbrirTablaAsync(cut);

        cut.FindAll(".bandeja-layout.bandeja-modo-tabla").Should().ContainSingle();
        cut.FindAll(".bandeja-centro .tabla-conversaciones").Should().ContainSingle();
        cut.FindAll(".bandeja-volver").Should().BeEmpty();
    }

    [Fact]
    public void Los_chips_de_canal_filtran_la_lista_y_llevan_su_recuento()
    {
        var (cut, mediador) = RenderizarConUnaConversacion();

        var chips = cut.FindAll(".bandeja-toggles .bandeja-toggle");
        chips.Select(c => c.TextContent.Trim()).Should().Equal("Todos · 1", "Correo · 1", "WhatsApp · 1");
        chips[0].GetAttribute("aria-pressed").Should().Be("true");
        UltimaListaPedida(mediador).Canal.Should().BeNull();
    }

    [Fact]
    public async Task Elegir_un_canal_lo_manda_en_la_consulta_y_Todos_lo_quita()
    {
        var (cut, mediador) = RenderizarConUnaConversacion();

        await cut.FindAll(".bandeja-toggles .bandeja-toggle")[2].ClickAsync(new MouseEventArgs());

        UltimaListaPedida(mediador).Canal.Should().Be(CanalConversacion.WhatsApp);
        cut.FindAll(".bandeja-toggles .bandeja-toggle")[2].GetAttribute("aria-pressed").Should().Be("true");

        await cut.FindAll(".bandeja-toggles .bandeja-toggle")[0].ClickAsync(new MouseEventArgs());

        UltimaListaPedida(mediador).Canal.Should().BeNull();
    }

    [Fact]
    public void Si_fallan_los_recuentos_de_la_cartera_la_lista_se_pinta_igual()
    {
        var escenario = new Escenario
        {
            Interceptar = p => p is ObtenerConversacionesQuery { TamanoPagina: 1 }
                ? Task.FromException<object?>(new InvalidOperationException("recuento caído"))
                : null
        };
        var (cut, _) = RenderizarConUnaConversacion(escenario);

        cut.FindAll(".bandeja-lista .bandeja-fila").Should().ContainSingle();
        cut.FindAll(".bandeja-lista .estado-vacio, .bandeja-lista [role=alert]").Should().BeEmpty();
    }

    [Fact]
    public void El_divisor_del_ancho_de_la_lista_sigue_en_el_hueco_entre_columnas()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        var divisor = cut.Find(".bandeja-lista + .bandeja-divisor");
        divisor.GetAttribute("role").Should().Be("separator");
        divisor.GetAttribute("data-redimensionable").Should().Be("--bandeja-lista-ancho");
        divisor.GetAttribute("aria-label").Should().Be("Ajustar el ancho de la lista de conversaciones");
        divisor.GetAttribute("tabindex").Should().Be("0");
        divisor.GetAttribute("aria-valuenow").Should().Be("320", "el ancho por defecto de la maqueta");
    }

    // ---------------------------------------------------------------- buzón personal

    [Fact]
    public async Task El_buzon_personal_es_de_solo_lectura_y_sin_filtros_de_cartera()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        await cut.FindAll(".bandeja-nav-item").Single(b => b.TextContent.Trim() == "Mi buzón personal").ClickAsync(new MouseEventArgs());

        cut.FindAll(".bandeja-layout.bandeja-modo-personal").Should().ContainSingle();
        cut.FindAll(".bandeja-lista .bandeja-vista-personal-descripcion").Should().ContainSingle();
        cut.FindAll(".bandeja-vista-w, .bandeja-crit-b, .bandeja-filtros-avanzados, .bandeja-lis-cab, .bandeja-toggles")
            .Should().BeEmpty("ni vistas, ni filtros, ni canales: es el correo personal del Gestor CAE");
        cut.FindComponents<AtajosListaTeclado>().Should().BeEmpty();
        cut.FindAll(".bandeja-cabecera-pagina input[type=search]").Should().BeEmpty("el buscador solo aplica a las conversaciones");
        cut.FindAll(".composer-correo").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- lo que la decisión D2 deja fuera

    [Theory]
    [InlineData("Posponer")]
    [InlineData("Fusionar")]
    [InlineData("Dividir")]
    [InlineData("Conversación paralela")]
    [InlineData("Borradores")]
    [InlineData("No deseado")]
    [InlineData("Deshacer")]
    [InlineData("Recordar si no contestan")]
    public void La_bandeja_no_dibuja_lo_que_la_decision_D2_deja_fuera(string texto)
    {
        var (cut, _) = RenderizarConUnaConversacion();

        cut.Find(".bandeja-layout").TextContent.Should().NotContain(texto);
    }

    [Fact]
    public void La_cartera_solo_muestra_el_Tenant_activo_sin_selector_de_Empresas_gestionadas()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        cut.Find(".bandeja-filtros").TextContent.Should().NotContain("Todas mis Empresas");
    }
}
