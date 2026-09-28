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
    [InlineData("http://atacante.com/phish")]
    [InlineData("//atacante.com")]
    [InlineData("/\\atacante.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("documentos")]
    public void Todo_lo_que_no_sea_ruta_local_cae_a_la_raiz(string? entrada)
        => RedireccionLocal.Sanear(entrada).Should().Be("/");

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
}
