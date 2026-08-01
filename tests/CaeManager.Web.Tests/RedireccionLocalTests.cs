using CaeManager.Web.Components.Account;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El saneado de ReturnUrl es la defensa contra el open redirect post-login
/// (MATURITY_REVIEW.md § 5) — estos casos cubren las formas que un navegador
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
}
