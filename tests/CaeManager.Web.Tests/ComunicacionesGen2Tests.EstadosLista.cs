using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using CaeManager.Web.Features.Comunicaciones.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Estados de la lista de la bandeja por vista (lienzo «Pulida: bandeja de la cartera», L-14 y L-15): cargando con cuatro esqueletos y
/// «Cargando…», error con «No pudimos cargar la lista» y «Recargar la lista», y los vacíos «Al día», «Sin resultados», «Sin buzones
/// conectados» y «Nada en «vista»». Quedan fuera «Sin propuestas» (la vista Propuestas no existe, L-16) y «Al día» como vista
/// «Abiertas»: «Todas» ocupa su lugar hasta que ObtenerConversacionesQuery admita el filtro de estados vivos (DN-O).
/// </summary>
public partial class ComunicacionesGen2Tests
{
    private static int ConsultasDeConexiones(MediadorControlado mediador) => mediador.Enviados.OfType<ObtenerConexionesIntegracionQuery>().Count();

    private static string TituloDelVacio(IRenderedComponent<Bandeja> cut) => cut.Find(".bandeja-lista .estado-vacio h3").TextContent.Trim();

    private static string TextoDelVacio(IRenderedComponent<Bandeja> cut) => cut.Find(".bandeja-lista .estado-vacio p").TextContent.Trim();

    // ---------------------------------------------------------------- cargando (L-13/L-14)

    [Fact]
    public void Mientras_carga_la_lista_se_pintan_cuatro_esqueletos_y_Cargando()
    {
        var escenario = new Escenario();
        var pendiente = new TaskCompletionSource<object?>();
        escenario.Interceptar = p => p is ObtenerConversacionesQuery ? pendiente.Task : null;
        var (cut, _) = Renderizar(escenario);

        cut.FindAll(".bandeja-lista .esqueleto-lista .esqueleto-fila").Should().HaveCount(4, "el lienzo dibuja cuatro esqueletos");
        cut.Find(".bandeja-lista .bandeja-cargando-texto").TextContent.Trim().Should().Be("Cargando…");
        cut.FindAll(".bandeja-lista .estado-vacio").Should().BeEmpty("mientras carga no hay vacío ni error");
    }

    // ---------------------------------------------------------------- error (L-14)

    [Fact]
    public void Si_la_lista_falla_el_error_dice_el_titulo_y_el_texto_del_lienzo_y_solo_ofrece_Recargar_la_lista()
    {
        var escenario = new Escenario();
        escenario.Interceptar = p => p is ObtenerConversacionesQuery ? Task.FromException<object?>(new InvalidOperationException("boom")) : null;
        var (cut, _) = Renderizar(escenario);

        TituloDelVacio(cut).Should().Be("No pudimos cargar la lista");
        TextoDelVacio(cut).Should().Be("Las conversaciones siguen guardadas. Vuelve a intentarlo en unos segundos.");
        cut.FindAll(".bandeja-lista .estado-vacio button").Select(b => b.TextContent.Trim()).Should().Equal("Recargar la lista");
        cut.FindAll(".bandeja-lista .bandeja-al-dia").Should().BeEmpty();
    }

    [Fact]
    public async Task En_modo_tabla_el_error_tambien_ofrece_Recargar_la_lista()
    {
        var escenario = new Escenario();
        escenario.Interceptar = p => p is ObtenerConversacionesQuery ? Task.FromException<object?>(new InvalidOperationException("boom")) : null;
        var (cut, _) = Renderizar(escenario);

        await cut.Find(".bandeja-lis-cab .bandeja-mini").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            cut.FindAll(".bandeja-centro .estado-vacio button").Select(b => b.TextContent.Trim()).Should().Equal("Recargar la lista"), PlazoEspera);
    }

    // ---------------------------------------------------------------- vacíos (L-15)

    [Fact]
    public void Sin_conversaciones_en_la_vista_por_defecto_dice_Al_dia_en_texto_apagado_y_No_quedan_conversaciones()
    {
        var (cut, mediador) = Renderizar(new Escenario());

        cut.Find(".bandeja-lista .bandeja-al-dia .bandeja-al-dia-texto").TextContent.Trim().Should().Be("Al día");
        cut.FindAll(".bandeja-lista .bandeja-al-dia .badge").Should().BeEmpty("en el lienzo «Al día» es texto apagado, no un distintivo");
        TituloDelVacio(cut).Should().Be("No quedan conversaciones");
        TextoDelVacio(cut).Should().Be("Los mensajes nuevos entrarán en su buzón.");
        cut.FindAll(".bandeja-lista .estado-vacio button").Should().BeEmpty("sin filtros no hay nada que quitar");
        ConsultasDeConexiones(mediador).Should().Be(1, "con la lista vacía y sin filtros se comprueba que haya algún buzón conectado");
    }

    [Fact]
    public void Una_busqueda_sin_resultados_dice_Sin_resultados_con_el_texto_del_lienzo_y_no_dice_Al_dia()
    {
        var (cut, mediador) = Renderizar(new Escenario(), "comunicaciones?q=zzz");

        TituloDelVacio(cut).Should().Be("Sin resultados");
        TextoDelVacio(cut).Should().Be("Prueba con otro término.");
        cut.FindAll(".bandeja-al-dia").Should().BeEmpty();
        cut.FindAll(".bandeja-lista .estado-vacio button").Select(b => b.TextContent.Trim()).Should().Equal("Quitar los filtros");
        ConsultasDeConexiones(mediador).Should().Be(0, "con una búsqueda puesta el vacío no puede ser «Sin buzones conectados»");
    }

    [Fact]
    public void Un_filtro_de_Mas_filtros_sin_resultados_dice_Sin_resultados_con_el_texto_de_filtros_que_el_lienzo_no_tiene()
    {
        var (cut, _) = Renderizar(new Escenario(), "comunicaciones?estado=Resuelta");

        TituloDelVacio(cut).Should().Be("Sin resultados");
        TextoDelVacio(cut).Should().Be("Prueba con otro término o quita los filtros.");
        cut.FindAll(".bandeja-al-dia").Should().BeEmpty();
    }

    [Fact]
    public void Sin_ningun_buzon_conectado_y_sin_conversaciones_dice_Sin_buzones_conectados_y_no_dice_Al_dia()
    {
        var escenario = new Escenario();
        escenario.ConexionesBuzon.Clear();
        var (cut, _) = Renderizar(escenario);

        TituloDelVacio(cut).Should().Be("Sin buzones conectados");
        TextoDelVacio(cut).Should().Be("Conéctalos en Configuración, Integraciones.");
        cut.FindAll(".bandeja-al-dia").Should().BeEmpty();
        cut.FindAll(".bandeja-lista .estado-vacio button").Should().BeEmpty();
    }

    [Fact]
    public void Un_buzon_deshabilitado_o_personal_no_cuenta_como_buzon_conectado_de_la_bandeja()
    {
        var escenario = new Escenario();
        escenario.ConexionesBuzon.Clear();
        escenario.ConexionesBuzon.AddRange([
            new(Guid.NewGuid(), "off@example.invalid", "Apagado", null, null, EstadoConexionIntegracion.Deshabilitada, DateTime.UtcNow, null, null),
            new(Guid.NewGuid(), "mio@example.invalid", "Personal", null, null, EstadoConexionIntegracion.Habilitada, DateTime.UtcNow, Guid.NewGuid(), null)]);
        var (cut, _) = Renderizar(escenario);

        TituloDelVacio(cut).Should().Be("Sin buzones conectados");
    }

    [Fact]
    public void Con_conversaciones_no_se_pregunta_por_los_buzones()
    {
        var escenario = new Escenario();
        escenario.ConexionesBuzon.Clear();
        escenario.Conversaciones.Add(Conversacion("Certificado", ClienteRefrielectric));
        var (cut, mediador) = Renderizar(escenario);

        AsuntosVisibles(cut).Should().Equal(["Certificado"]);
        cut.FindAll(".bandeja-lista .estado-vacio").Should().BeEmpty();
        ConsultasDeConexiones(mediador).Should().Be(0);
    }

    [Fact]
    public void Si_no_se_puede_saber_si_hay_buzones_el_vacio_no_afirma_que_no_los_hay()
    {
        var escenario = new Escenario();
        escenario.Interceptar = p => p is ObtenerConexionesIntegracionQuery ? Task.FromException<object?>(new InvalidOperationException("sin acceso")) : null;
        var (cut, _) = Renderizar(escenario);

        TituloDelVacio(cut).Should().Be("No quedan conversaciones");
        cut.Find(".bandeja-lista .bandeja-al-dia-texto").TextContent.Trim().Should().Be("Al día");
    }

    [Theory]
    [InlineData("Mías")]
    [InlineData("Esperando respuesta del Interlocutor")]
    public async Task Una_vista_distinta_de_la_por_defecto_sin_conversaciones_dice_Nada_en_la_vista_y_Prueba_con_otra_vista_o_con_Todas(string vista)
    {
        var (cut, mediador) = Renderizar(new Escenario());

        await ElegirVistaAsync(cut, vista);

        await cut.WaitForAssertionAsync(() =>
        {
            TituloDelVacio(cut).Should().Be($"Nada en «{vista}»");
            TextoDelVacio(cut).Should().Be("Prueba con otra vista o con «Todas».");
        }, PlazoEspera);
        cut.FindAll(".bandeja-al-dia").Should().BeEmpty("«Al día» es solo de la vista por defecto");
        cut.FindAll(".bandeja-lista .estado-vacio button").Select(b => b.TextContent.Trim()).Should().Equal("Quitar los filtros");
        ConsultasDeConexiones(mediador).Should().Be(1, "solo la carga inicial, sin filtros, pregunta por los buzones");
    }

    [Fact]
    public async Task Quitar_los_filtros_desde_Nada_en_la_vista_vuelve_a_la_vista_por_defecto()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Certificado sin asignar", ClienteRefrielectric));
        var (cut, _) = Renderizar(escenario);
        await ElegirVistaAsync(cut, "Mías");
        await cut.WaitForAssertionAsync(() => TituloDelVacio(cut).Should().Be("Nada en «Mías»"), PlazoEspera);

        await cut.Find(".bandeja-lista .estado-vacio button").ClickAsync(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => AsuntosVisibles(cut).Should().Equal(["Certificado sin asignar"]), PlazoEspera);
        cut.FindAll(".bandeja-lista .estado-vacio").Should().BeEmpty();
    }
}
