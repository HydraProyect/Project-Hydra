using CaeManager.Web.Components;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;

namespace CaeManager.Web.Tests;

/// <summary>
/// Composición de los filtros en la URL cuando el navegador todavía no ha confirmado la escritura anterior
/// (<see cref="NavigationManagerExtensions.ActualizarFiltrosEnUrl"/>): las escrituras seguidas de la misma página se
/// suman, y cualquier navegación ajena (otra página, «atrás», «adelante») corta la cadena aunque su evento llegue al
/// ayudante después que a la página nueva.
/// </summary>
public class NavigationManagerExtensionsTests
{
    private static Dictionary<string, string> Parametros(string uri) =>
        QueryHelpers.ParseQuery(new Uri(uri).Query).ToDictionary(p => p.Key, p => p.Value.ToString());

    [Fact]
    public void Una_escritura_confirmada_compone_sobre_la_url_actual_como_antes()
    {
        var navigation = new NavegacionRetenida("http://localhost/vehiculos?estado=Vencido");

        navigation.ActualizarFiltroEnUrl("q", "furgo");
        navigation.Uri.Should().Be("http://localhost/vehiculos?estado=Vencido&q=furgo");

        navigation.ActualizarFiltroEnUrl("estado", " ");
        navigation.Uri.Should().Be("http://localhost/vehiculos?q=furgo", "un valor en blanco quita el parámetro");

        navigation.NavigateTo("/centros?q=obra");
        navigation.ActualizarFiltroEnUrl("orden", "nombre");
        navigation.Uri.Should().Be("http://localhost/centros?q=obra&orden=nombre");
    }

    [Fact]
    public void Un_acuse_anterior_conserva_la_composicion_y_el_del_ultimo_destino_la_cierra()
    {
        var navigation = new NavegacionRetenida { Retener = true };
        navigation.ActualizarFiltroEnUrl("q", "furgo");
        navigation.ActualizarFiltroEnUrl("orden", "modelo");
        navigation.Confirmar(navigation.Destinos[0]);

        navigation.ActualizarFiltroEnUrl("estado", "Vencido");

        Parametros(navigation.Destinos.Last()).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["q"] = "furgo",
            ["orden"] = "modelo",
            ["estado"] = "Vencido",
        });

        navigation.Confirmar(navigation.Destinos[1]);
        navigation.Confirmar(navigation.Destinos[2]);
        navigation.Retener = false;
        navigation.ActualizarFiltroEnUrl("q", null);
        Parametros(navigation.Uri).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["orden"] = "modelo",
            ["estado"] = "Vencido",
        });
    }

    /// <summary>
    /// Con una escritura sin confirmar (o cancelada por el navegador al atender otra navegación), el usuario pulsa un
    /// enlace del menú o «atrás»/«adelante». La página a la que llega escribe sus filtros: unas veces cuando el ayudante
    /// ya vio el LocationChanged, y otras dentro del propio evento, antes que él (el Router lo atiende primero y la
    /// página nueva escribe en su OnInitialized). En ningún caso se arrastran parámetros ni la ruta de la página anterior.
    /// </summary>
    [Theory]
    [InlineData("http://localhost/clientes?accion=nuevo", "accion", null, "http://localhost/clientes", false)]
    [InlineData("http://localhost/clientes?accion=nuevo", "accion", null, "http://localhost/clientes", true)]
    [InlineData("http://localhost/vehiculos?estado=Vencido", "orden", "nombre", "http://localhost/vehiculos?estado=Vencido&orden=nombre", false)]
    [InlineData("http://localhost/vehiculos?estado=Vencido", "orden", "nombre", "http://localhost/vehiculos?estado=Vencido&orden=nombre", true)]
    [InlineData("http://localhost/vehiculos", "estado", "Vencido", "http://localhost/vehiculos?estado=Vencido", false)]
    [InlineData("http://localhost/vehiculos", "estado", "Vencido", "http://localhost/vehiculos?estado=Vencido", true)]
    public void Una_navegacion_ajena_con_escrituras_pendientes_compone_sobre_su_propia_url(
        string ajena, string clave, string? valor, string esperado, bool escribeDentroDelEvento)
    {
        var navigation = new NavegacionRetenida("http://localhost/vehiculos?q=fur");
        var escribio = false;
        if (escribeDentroDelEvento)
        {
            // Suscrito antes que el ayudante, como el Router y la página nueva.
            navigation.LocationChanged += (_, args) =>
            {
                if (args.Location != ajena || escribio) return;
                escribio = true;
                navigation.ActualizarFiltroEnUrl(clave, valor);
            };
        }

        navigation.Retener = true;
        navigation.ActualizarFiltroEnUrl("q", "furgo");
        navigation.ActualizarFiltroEnUrl("orden", "modelo");
        navigation.Confirmar(ajena);

        if (!escribeDentroDelEvento)
            navigation.ActualizarFiltroEnUrl(clave, valor);

        navigation.Destinos.Last().Should().Be(esperado);

        // La escritura hecha en la página nueva abre su propia cadena: la siguiente, aún sin acuse, se suma a ella.
        navigation.ActualizarFiltroEnUrl("agrupar", "empresa");
        navigation.Destinos.Last().Should().Be(esperado + (esperado.Contains('?') ? "&" : "?") + "agrupar=empresa");
    }

    /// <summary>
    /// La escritura queda sin acuse (el navegador la cancela al atender el clic) y el usuario sale a otra página y
    /// vuelve con «atrás» a la URL de partida, o pulsa el enlace del menú de la página en la que ya está: la página se
    /// carga de nuevo sin el filtro, y la siguiente escritura no lo resucita.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Volver_a_la_url_de_partida_con_una_escritura_sin_acuse_no_resucita_el_filtro(bool pasandoPorOtraPagina)
    {
        var navigation = new NavegacionRetenida { Retener = true };
        navigation.ActualizarFiltroEnUrl("q", "fur");

        if (pasandoPorOtraPagina)
            navigation.Confirmar("http://localhost/centros");
        navigation.Confirmar("http://localhost/vehiculos");
        navigation.ActualizarFiltroEnUrl("orden", "modelo");

        navigation.Destinos.Last().Should().Be("http://localhost/vehiculos?orden=modelo");
    }

    [Fact]
    public void Teclear_y_borrar_cierra_la_cadena_con_el_acuse_del_ultimo_destino()
    {
        var navigation = new NavegacionRetenida { Retener = true };
        navigation.ActualizarFiltroEnUrl("q", "x");
        navigation.ActualizarFiltroEnUrl("q", null);
        navigation.Destinos.Last().Should().Be("http://localhost/vehiculos");
        navigation.Confirmar(navigation.Destinos[0]);
        navigation.Confirmar(navigation.Destinos[1]);

        // Un enlace que lleva justo a la URL intermedia es ajeno: se compone sobre ella, no sobre el último destino.
        navigation.Confirmar("http://localhost/vehiculos?q=x");
        navigation.ActualizarFiltroEnUrl("orden", "modelo");
        navigation.Destinos.Last().Should().Be("http://localhost/vehiculos?q=x&orden=modelo");
    }

    [Fact]
    public void Un_NavigateTo_que_lanza_abandona_la_cadena_y_propaga_la_excepcion()
    {
        var navigation = new NavegacionRetenida { Retener = true };
        navigation.ActualizarFiltroEnUrl("q", "furgo");

        navigation.Lanzar = true;
        var escribir = () => navigation.ActualizarFiltroEnUrl("orden", "modelo");
        escribir.Should().Throw<Microsoft.AspNetCore.Components.NavigationException>();

        navigation.Lanzar = false;
        navigation.ActualizarFiltroEnUrl("estado", "Vencido");
        navigation.Destinos.Last().Should().Be("http://localhost/vehiculos?estado=Vencido");
    }

    [Fact]
    public void Mas_escrituras_sin_acuse_que_el_tope_siguen_componiendose_sobre_el_ultimo_destino()
    {
        var navigation = new NavegacionRetenida { Retener = true };
        for (var i = 0; i < 40; i++)
            navigation.ActualizarFiltroEnUrl($"p{i}", i.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Parametros(navigation.Destinos.Last()).Should().HaveCount(40).And.ContainKey("p0").And.ContainKey("p39");
    }

    [Fact]
    public void Las_escrituras_pendientes_de_un_circuito_no_alcanzan_a_otro()
    {
        var deUnUsuario = new NavegacionRetenida { Retener = true };
        var deOtroUsuario = new NavegacionRetenida("http://localhost/vehiculos?estado=Vencido") { Retener = true };

        deUnUsuario.ActualizarFiltroEnUrl("q", "furgo");
        deOtroUsuario.ActualizarFiltroEnUrl("orden", "modelo");
        deUnUsuario.ActualizarFiltroEnUrl("agrupar", "empresa");

        deOtroUsuario.Destinos.Should().Equal("http://localhost/vehiculos?estado=Vencido&orden=modelo");
        deUnUsuario.Destinos.Last().Should().Be("http://localhost/vehiculos?q=furgo&agrupar=empresa");
    }
}
