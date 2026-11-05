using System.Globalization;
using CaeManager.Web.Features.AltaAutonoma.Antiabuso;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// Lista estática de dominios de correo desechable (recurso incrustado) y su validador: dominio tras la última arroba,
/// normalizado, con subdominios cubiertos por etiquetas completas.
/// </summary>
public class DominiosDesechablesTests
{
    private static readonly ValidadorDominiosDesechables Validador = new();

    [Fact]
    public void El_recurso_incrustado_se_carga_con_una_lista_razonable()
    {
        Validador.Cantidad.Should().BeGreaterThan(80);
    }

    [Theory]
    [InlineData("alguien@mailinator.com")]
    [InlineData("ALGUIEN@MAILINATOR.COM")]
    [InlineData("  alguien@Yopmail.com.  ")]
    [InlineData("alguien@buzon.guerrillamail.com")]
    [InlineData("a@b@10minutemail.com")]
    [InlineData("alguien@trashmail.de")]
    // Separadores Unicode que IDNA convierte en punto, al final y en medio: el punto final que aparece tras la
    // conversión no puede sacar el dominio de la lista.
    [InlineData("alguien@mailinator.com。")]
    [InlineData("alguien@mailinator.com．")]
    [InlineData("alguien@mailinator.com｡")]
    [InlineData("alguien@mailinator。com")]
    public void Un_dominio_desechable_o_un_subdominio_suyo_se_detecta(string correo)
    {
        Validador.EsDominioDesechable(correo).Should().BeTrue();
    }

    [Theory]
    [InlineData("persona@gmail.com")]
    [InlineData("persona@empresa.es")]
    [InlineData("persona@notmailinator.com", "un sufijo que no cae en frontera de etiqueta no cuenta")]
    [InlineData("persona@mailinator.com.empresa.es", "el dominio listado en medio no cuenta")]
    [InlineData("mailinator.com", "sin arroba no hay dominio de correo")]
    [InlineData("persona@")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Un_dominio_corriente_o_un_correo_sin_dominio_no_es_desechable(string? correo, string porque = "")
    {
        Validador.EsDominioDesechable(correo).Should().BeFalse(porque);
    }

    [Fact]
    public void Un_dominio_de_mas_de_253_caracteres_no_se_normaliza_ni_se_clasifica()
    {
        var etiquetas = string.Join('.', Enumerable.Repeat("a", 125));

        Validador.EsDominioDesechable($"x@{etiquetas}.mailinator.com").Should().BeFalse("ya no es un nombre de dominio");
        Validador.EsDominioDesechable($"x@{string.Join('.', Enumerable.Repeat("a", 100))}.mailinator.com")
            .Should().BeTrue("control: con 214 caracteres sigue siendo un dominio y cae en la lista");
    }

    [Fact]
    public void Un_dominio_internacionalizado_se_compara_en_su_forma_ascii()
    {
        var validador = ValidadorDominiosDesechables.DesdeLineas(["# comentario", "", "correo.españa.test"]);

        validador.Cantidad.Should().Be(1);
        validador.EsDominioDesechable("a@correo.españa.test").Should().BeTrue();
        validador.EsDominioDesechable("a@CORREO.xn--espaa-rta.test").Should().BeTrue();
        validador.EsDominioDesechable("a@correo.espana.test").Should().BeFalse();
    }

    [Fact]
    public void Cada_linea_del_recurso_es_un_dominio_en_minusculas_sin_repetir()
    {
        using var flujo = typeof(ValidadorDominiosDesechables).Assembly.GetManifestResourceStream(ValidadorDominiosDesechables.NombreRecurso)!;
        using var lector = new StreamReader(flujo);
        var dominios = lector.ReadToEnd().Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        dominios.Should().OnlyHaveUniqueItems();
        dominios.Should().AllSatisfy(d =>
        {
            d.Should().Be(d.Trim().ToLower(CultureInfo.InvariantCulture));
            d.Should().MatchRegex(@"^[a-z0-9-]+(\.[a-z0-9-]+)+$");
        });
        dominios.Should().NotContain(["gmail.com", "outlook.com", "hotmail.com", "yahoo.com", "icloud.com", "proton.me"],
            "un proveedor corriente nunca entra en la lista");
    }
}
