using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>«Validada» es de la plataforma del Cliente; «verificado», del archivo</b> (decisión del
/// propietario, 2026-10-10).
///
/// <para>
/// <b>Por qué existe.</b> El mapeo de <c>EstadoAcreditacion</c> a texto y la rejilla de la validación
/// oficial ya tienen su guardián en Web.Tests (<c>EstadoAcreditacionUiTests</c>,
/// <c>DocumentoWorkspacePanelTests</c>), pero el mismo vocabulario vive además en literales de
/// <c>PlataformaTab.razor</c> (botones, título del modal, aviso) y en claves de
/// <c>TextosDocumentos</c> en castellano y catalán que ningún test de componente afirma: devolver
/// «Marcado como aceptado.» o traducir «Validat automàticament» dejaba la suite en verde.
/// </para>
///
/// <para>
/// <b>Lo que ve:</b> todo <c>PlataformaTab.razor</c> salvo comentarios e identificadores, y los valores
/// de las claves <c>Plat*</c> y <c>ValDecision*</c> de <c>TextosDocumentos.resx</c> y su satélite
/// ca-ES. <b>Lo que NO ve:</b> comentarios, identificadores (el enum sigue llamándose
/// <c>Aceptada</c>) ni otras pantallas.
/// </para>
/// </summary>
public class ValidadaEsDeLaPlataformaYVerificadoDelArchivoTests
{
    private static readonly string Documentos = Path.Combine(
        FuentesDeSrc.RaizDelRepositorio(), "src", "CaeManager.Web", "Features", "Documentos");

    private static readonly Regex Aceptado = new(@"\b(aceptad[oa]s?|acceptad[ae]s?|acceptats?)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Validado = new(@"\b(validad[oa]s?|validat|validats|validades)\b", RegexOptions.IgnoreCase);
    private static readonly Regex ComentarioRazor = new(@"@\*.*?\*@", RegexOptions.Singleline);
    private static readonly Regex ComentarioDeLinea = new(@"(^|\s)//.*$");

    /// <summary>
    /// La palabra como la lee el usuario: <c>yaAceptada</c> no tiene frontera de palabra y
    /// <c>EstadoAcreditacion.Aceptada</c> va tras un punto, así que ningún identificador casa.
    /// </summary>
    private static Regex PalabraVisible(string raiz) => new($@"(?<![\w.]){raiz}[oa]s?\b", RegexOptions.IgnoreCase);

    [Fact]
    public void La_pestana_de_plataforma_no_rotula_aceptado()
    {
        var fuente = File.ReadAllText(Path.Combine(Documentos, "Components", "PlataformaTab.razor"));
        var sinComentarios = string.Join('\n', ComentarioRazor.Replace(fuente, string.Empty)
            .Split('\n')
            .Select(linea => ComentarioDeLinea.Replace(linea.TrimEnd('\r'), string.Empty)));

        PalabraVisible("aceptad").Matches(sinComentarios).Select(m => m.Value).Should().BeEmpty(
            "lo que la plataforma del Cliente da por bueno se dice «validado/validada», no «aceptado»");
        PalabraVisible("validad").Matches(sinComentarios).Should().HaveCountGreaterThanOrEqualTo(4,
            "control positivo: el botón de fila, el título y el botón del modal, y el aviso");
    }

    [Theory]
    [InlineData("TextosDocumentos.resx")]
    [InlineData("TextosDocumentos.ca-ES.resx")]
    public void Los_recursos_reparten_validada_a_la_plataforma_y_verificado_al_archivo(string fichero)
    {
        var valores = XDocument.Load(Path.Combine(Documentos, "Recursos", fichero)).Root!.Elements("data")
            .ToDictionary(dato => (string)dato.Attribute("name")!, dato => (string?)dato.Element("value") ?? string.Empty);

        var dePlataforma = valores.Where(par => par.Key.StartsWith("Plat", StringComparison.Ordinal)).ToList();
        var deDecision = valores.Where(par => par.Key.StartsWith("ValDecision", StringComparison.Ordinal)).ToList();

        dePlataforma.Where(par => Validado.IsMatch(par.Value)).Should().NotBeEmpty(
            "control positivo: las frases de vigencia en bloque nombran el estado");
        deDecision.Should().NotBeEmpty("control positivo: la decisión de la validación oficial se localiza aquí");

        dePlataforma.Where(par => Aceptado.IsMatch(par.Value)).Should().BeEmpty(
            "el estado de la acreditación en la plataforma del Cliente se dice «validada»");
        deDecision.Where(par => Validado.IsMatch(par.Value)).Should().BeEmpty(
            "la comprobación técnica del archivo se dice «verificado»: «validado» nombra la respuesta de la plataforma");
    }
}
