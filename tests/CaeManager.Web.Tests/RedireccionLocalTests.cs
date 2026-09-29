using CaeManager.Web.Components.Account;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El saneado de ReturnUrl es la defensa contra el open redirect post-login
/// (Project-Hydra-Negocio/MATURITY_REVIEW.md § 5) — estos casos cubren las formas que un navegador
/// interpreta como salto a otro dominio aunque "parezcan" rutas.
/// </summary>
public class RedireccionLocalTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/documentos", "/documentos")]
    [InlineData("/documentos?filtro=urgente", "/documentos?filtro=urgente")]
    public void Rutas_locales_pasan_intactas(string entrada, string esperado)
        => RedireccionLocal.Sanear(entrada).Should().Be(esperado);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://atacante.com")]
    [InlineData("/	/atacante.com")]
    [InlineData("http://atacante.com/phish")]
    [InlineData("//atacante.com")]
    [InlineData("/\\atacante.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("documentos")]
    public void Todo_lo_que_no_sea_ruta_local_cae_a_la_raiz(string? entrada)
        => RedireccionLocal.Sanear(entrada).Should().Be("/");

    /// <summary>
    /// Aterrizaje D-2 tras iniciar sesión: sin returnUrl a otra ruta (o con uno
    /// saneado a la raíz), el destino es Inicio con la marca de un solo uso.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//atacante.com")]
    [InlineData("https://atacante.com")]
    [InlineData("/	/atacante.com")]
    public void El_destino_tras_login_sin_returnUrl_propio_es_Inicio_con_la_marca(string? entrada)
        => RedireccionLocal.DestinoTrasLogin(entrada).Should().Be("/?desde=login");

    [Theory]
    [InlineData("/documentos")]
    [InlineData("/mi-trabajo")]
    [InlineData("/documentos?filtro=urgente")]
    public void El_destino_tras_login_respeta_un_returnUrl_explicito_a_otra_ruta(string entrada)
        => RedireccionLocal.DestinoTrasLogin(entrada).Should().Be(entrada);

    /// <summary>
    /// Los navegadores descartan tabulador, CR y LF al interpretar una URL, así que
    /// "/" + tabulador + "/atacante.com" llega como "//atacante.com" aunque la segunda
    /// posición no sea una barra. Cualquier carácter de control, en cualquier posición,
    /// se rechaza.
    /// </summary>
    [Theory]
    [InlineData("/\t/atacante.com")]
    [InlineData("/\n/atacante.com")]
    [InlineData("/\r/atacante.com")]
    [InlineData("/\t\\atacante.com")]
    [InlineData("/\0/atacante.com")]
    [InlineData("/documentos\t")]
    [InlineData("/doc\numents")]
    public void Los_caracteres_de_control_caen_a_la_raiz_aunque_el_resto_parezca_local(string entrada)
        => RedireccionLocal.Sanear(entrada).Should().Be("/");

    [Theory]
    [InlineData("/documentos#seccion")]
    [InlineData("/clientes?q=Refri&critico=true")]
    public void Un_fragmento_o_una_consulta_normales_siguen_pasando(string entrada)
        => RedireccionLocal.Sanear(entrada).Should().Be(entrada);

    /// <summary>
    /// Los selectores de MainLayout reciben como returnUrl la URL con la que se creó el circuito,
    /// que tras iniciar sesión es "/?desde=login": volver ahí reactivaría el aterrizaje D-2. Solo se
    /// quita el parámetro exacto (todas sus apariciones); el resto de la consulta y su orden se
    /// conservan, y un fragmento se mantiene tras la consulta.
    /// </summary>
    [Theory]
    [InlineData("/?desde=login", "/")]
    [InlineData("/?desde=login&x=1", "/?x=1")]
    [InlineData("/?x=1&desde=login", "/?x=1")]
    [InlineData("/clientes?a=1&desde=login&b=2", "/clientes?a=1&b=2")]
    [InlineData("/?desde=login&desde=login", "/")]
    [InlineData("/?desde=login&a=1&desde=login", "/?a=1")]
    [InlineData("/?desde=login&", "/")]
    [InlineData("/?otro=1", "/?otro=1")]
    [InlineData("/?desde=otro", "/?desde=otro")]
    [InlineData("/?desde=login2", "/?desde=login2")]
    [InlineData("/?xdesde=login", "/?xdesde=login")]
    [InlineData("/?DESDE=login", "/?DESDE=login")]
    [InlineData("/?desde=Login", "/?desde=Login")]
    [InlineData("/?desde=", "/?desde=")]
    [InlineData("/documentos", "/documentos")]
    [InlineData("/", "/")]
    [InlineData("/clientes?a=1&desde=login#seccion", "/clientes?a=1#seccion")]
    [InlineData("/?desde=login#seccion", "/#seccion")]
    [InlineData("/documentos#a?desde=login", "/documentos#a?desde=login")]
    [InlineData("/documentos#desde=login", "/documentos#desde=login")]
    public void SanearParaVolver_descarta_solo_la_marca_exacta_de_login(string entrada, string esperado)
        => RedireccionLocal.SanearParaVolver(entrada).Should().Be(esperado);

    /// <summary>Nunca más permisivo que Sanear: lo peligroso sigue cayendo a la raíz.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("//atacante.com")]
    [InlineData("//atacante.com?desde=login")]
    [InlineData("/\\atacante.com")]
    [InlineData("/\t/atacante.com")]
    [InlineData("/?desde=login\t")]
    [InlineData("https://atacante.com")]
    [InlineData("https://atacante.com/?desde=login")]
    [InlineData("javascript:alert(1)")]
    public void SanearParaVolver_deja_caer_a_la_raiz_lo_que_Sanear_rechaza(string? entrada)
        => RedireccionLocal.SanearParaVolver(entrada).Should().Be("/");

    [Theory]
    [InlineData("/?desde=login")]
    [InlineData("/a?x=1&desde=login#f")]
    [InlineData("/documentos")]
    public void SanearParaVolver_siempre_devuelve_una_ruta_local_segura(string entrada)
    {
        var resultado = RedireccionLocal.SanearParaVolver(entrada);

        resultado.Should().StartWith("/");
        resultado.Should().NotStartWith("//").And.NotStartWith("/\\");
        resultado.Should().Be(RedireccionLocal.Sanear(resultado), "el resultado es un valor que Sanear acepta tal cual");
    }
}
