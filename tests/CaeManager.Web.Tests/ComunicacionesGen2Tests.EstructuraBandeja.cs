using Bunit;
using CaeManager.Web.Features.Comunicaciones.Components;
using CaeManager.Web.Features.Comunicaciones.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Estructura de la bandeja de <c>/comunicaciones</c> según la maqueta del 2026-11-01 (PR-1): la lista de conversaciones está siempre a la
/// vista, el centro sin conversación dice «Ninguna conversación abierta» y la tabla es un modo secundario («Tabla» / «Ver como lista»).
/// Fija también lo que ya existía en main y no debe perderse al reordenar la pantalla (columna de filtros, buzón personal, compañeros,
/// atajos, divisor del ancho). La barra superior de la cartera y las vistas de D1 son de otra PR y aquí no se dibujan.
/// </summary>
public partial class ComunicacionesGen2Tests
{
    private (IRenderedComponent<Bandeja> Cut, MediadorControlado Mediador) RenderizarConUnaConversacion(Escenario? escenario = null)
    {
        escenario ??= new Escenario();
        var conversacion = Conversacion("Documentación pendiente", ClienteRefrielectric);
        escenario.Conversaciones.Add(conversacion);
        escenario.Detalle = _ => DetalleDe(conversacion);
        return Renderizar(escenario);
    }

    // ---------------------------------------------------------------- lista y centro

    [Fact]
    public void Sin_conversacion_abierta_la_lista_esta_a_la_vista_y_el_centro_dice_que_no_hay_ninguna()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        cut.FindAll(".bandeja-layout.bandeja-modo-lista").Should().ContainSingle();
        cut.FindAll(".bandeja-filtros .bandeja-lista .bandeja-fila").Should().ContainSingle();
        cut.Find(".bandeja-lis-cab-titulo").TextContent.Trim().Should().Be("Todas");
        cut.Find(".bandeja-lis-cab small").TextContent.Trim().Should().Be("1 conversación");
        cut.FindAll(".tabla-conversaciones").Should().BeEmpty("la tabla es un modo secundario");
        cut.Find(".bandeja-centro").TextContent.Should().Contain("Ninguna conversación abierta").And.Contain("Elige una de la lista.");
    }

    [Fact]
    public async Task Con_una_conversacion_abierta_la_lista_sigue_a_la_vista_y_marca_la_abierta()
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
    public async Task Volver_desde_una_conversacion_abierta_en_la_tabla_deja_la_tabla()
    {
        var (cut, _) = RenderizarConUnaConversacion();
        await AbrirTablaAsync(cut);

        await SeleccionarFila(cut, "Documentación pendiente");
        cut.FindAll(".bandeja-layout.bandeja-modo-hilo").Should().ContainSingle();
        await cut.Find(".bandeja-volver").ClickAsync(new MouseEventArgs());

        cut.FindAll(".bandeja-layout.bandeja-modo-tabla").Should().ContainSingle("el gestor eligió la tabla y vuelve a ella");
        cut.FindAll(".bandeja-centro .tabla-conversaciones").Should().ContainSingle();
    }

    [Fact]
    public async Task El_buscador_y_los_filtros_siguen_en_la_pantalla_al_cambiar_de_modo()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        cut.FindAll(".bandeja-cabecera-pagina input[type=search]").Should().ContainSingle();
        await AbrirTablaAsync(cut);
        cut.FindAll(".bandeja-cabecera-pagina input[type=search]").Should().ContainSingle();
        cut.FindAll(".bandeja-filtros .bandeja-toggles .bandeja-toggle").Should().HaveCount(4);
        cut.FindAll(".bandeja-filtros .bandeja-filtros-avanzados").Should().ContainSingle();
    }

    // ---------------------------------------------------------------- columna de filtros (sin cambios respecto a main)

    [Fact]
    public void La_columna_de_filtros_de_main_sigue_igual_con_la_lista_debajo()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        cut.FindAll(".bandeja-filtros .bandeja-nav-item").Select(b => b.TextContent.Trim())
            .Should().Equal("Clientes", "Mi buzón personal");
        cut.FindAll(".bandeja-filtros .bandeja-toggles .bandeja-toggle").Select(b => b.TextContent.Trim())
            .Should().Equal("Todas", "Asignadas a mí", "Sin asignar", "Esperando respuesta del Interlocutor");
        var avanzados = cut.Find(".bandeja-filtros .bandeja-filtros-avanzados");
        avanzados.TagName.Should().Be("DETAILS");
        avanzados.QuerySelector("summary")!.TextContent.Trim().Should().Be("Más filtros");
        cut.FindAll(".bandeja-filtros-avanzados select").Should().HaveCount(3, "Estado, Intención CAE y Cliente empresarial");
        cut.FindAll(".bandeja-filtros-avanzados input[type=month]").Should().ContainSingle();
        cut.FindComponents<AtajosListaTeclado>().Should().ContainSingle();
    }

    [Fact]
    public void La_cartera_y_las_vistas_de_la_barra_superior_no_estan_en_esta_pantalla()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        cut.FindAll(".bandeja-vista-w, .bandeja-crit-b, .bandeja-cartera-pie, .bandeja-nav-grupo-titulo, .bandeja-lista .bandeja-toggles")
            .Should().BeEmpty();
        cut.Find(".bandeja-layout").TextContent.Should().NotContain("Mi cartera").And.NotContain("Gestionar macros");
    }

    [Fact]
    public void Sin_companeros_la_columna_no_dibuja_su_seccion()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        cut.FindAll(".bandeja-filtros details.companeros").Should().BeEmpty();
    }

    [Fact]
    public void Los_companeros_del_Operador_CAE_siguen_en_la_columna_de_filtros()
    {
        var escenario = new Escenario();
        escenario.Companeros.Add(Marta);
        var (cut, _) = RenderizarConUnaConversacion(escenario);

        cut.Find(".bandeja-filtros details.companeros .companero-nombre").TextContent.Trim().Should().Be("Marta Solà");
    }

    [Fact]
    public void El_divisor_del_ancho_de_la_lista_sigue_en_el_DOM_con_su_etiqueta()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        var divisor = cut.Find(".bandeja-divisor");
        divisor.GetAttribute("role").Should().Be("separator");
        divisor.GetAttribute("data-redimensionable").Should().Be("--bandeja-lista-ancho");
        divisor.GetAttribute("aria-label").Should().Be("Ajustar el ancho de la lista de conversaciones");
    }

    // ---------------------------------------------------------------- buzón personal

    [Fact]
    public async Task El_buzon_personal_es_de_solo_lectura_y_sin_los_filtros_de_conversaciones()
    {
        var (cut, _) = RenderizarConUnaConversacion();

        await cut.FindAll(".bandeja-nav-item").Single(b => b.TextContent.Trim() == "Mi buzón personal").ClickAsync(new MouseEventArgs());

        cut.FindAll(".bandeja-layout.bandeja-modo-personal").Should().ContainSingle();
        cut.FindAll(".bandeja-lista .bandeja-vista-personal-descripcion, .bandeja-vista-personal-descripcion").Should().NotBeEmpty();
        cut.FindAll(".bandeja-toggles, .bandeja-filtros-avanzados, .bandeja-lis-cab").Should().BeEmpty();
        cut.FindComponents<AtajosListaTeclado>().Should().BeEmpty();
        cut.FindAll(".bandeja-cabecera-pagina input[type=search]").Should().BeEmpty("el buscador solo aplica a las conversaciones");
        cut.FindAll(".composer-correo").Should().BeEmpty();
        cut.Find(".bandeja-centro").TextContent.Should().Contain("Ninguna conversación abierta");
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
}
