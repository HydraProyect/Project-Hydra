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
