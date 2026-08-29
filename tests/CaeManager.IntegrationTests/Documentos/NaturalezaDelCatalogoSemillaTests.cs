using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// El catálogo semilla es donde el producto <b>afirma</b> por qué pide cada
/// documento, y esas afirmaciones llegan al usuario final y a los correos que
/// se envían a sus clientes. Este ratchet fija las que se verificaron contra
/// fuente oficial, y —sobre todo— fija que <b>nada asuma una autoridad que
/// nadie comprobó</b>.
/// </summary>
public class NaturalezaDelCatalogoSemillaTests
{
    private static TipoDocumento Buscar(string nombre) =>
        TipoDocumentoSeedData.CrearCopiasParaTenant().Single(t => t.Nombre == nombre);

    /// <summary>
    /// Lo único que el art. 10 del RD 171/2004 exige por escrito, más las
    /// obligaciones materiales de los arts. 16, 18 y 19 de la LPRL.
    /// </summary>
    [Theory]
    [InlineData("Evaluación de Riesgos Laborales")]
    [InlineData("Planificación de la Actividad Preventiva")]
    [InlineData("Plan de Prevención")]
    [InlineData("Modalidad Preventiva")]
    [InlineData("Información Art. 18")]
    [InlineData("Formación Art. 19")]
    public void Las_obligaciones_legales_verificadas_se_declaran_como_tales(string nombre)
    {
        Buscar(nombre).Naturaleza.Should().Be(NaturalezaJuridica.ObligacionLegal);
    }

    /// <summary>
    /// Los tres casos que motivaron partir el booleano: se piden siempre
    /// —todos los centros los exigen— pero <b>ninguna norma los impone</b>.
    /// Si alguno de estos volviera a rotularse como obligación legal, el
    /// producto estaría afirmando una ley inexistente.
    /// </summary>
    [Theory]
    [InlineData("Entrega de EPI")]
    [InlineData("Certificado de estar al corriente con la Seguridad Social")]
    [InlineData("Servicio de Prevención Ajeno")]
    public void La_practica_del_sector_no_se_disfraza_de_ley(string nombre)
    {
        Buscar(nombre).Naturaleza.Should().Be(NaturalezaJuridica.PracticaSector,
            "ninguna norma lo exige: se pide porque lo piden todos los centros, y eso es lo que hay que decirle al usuario");
    }

    /// <summary>
    /// Las tres afirmaciones que el producto no puede hacer nunca. Este test
    /// es el que impide que vuelvan por descuido.
    /// </summary>
    [Theory]
    [InlineData("Certificado de aptitud médica", "someterse a la vigilancia de la salud requiere el consentimiento del trabajador (art. 22.1 LPRL)")]
    [InlineData("Seguro de Responsabilidad Civil + recibo de pago", "no existe obligación legal general de RC en España")]
    public void Lo_que_no_es_obligacion_legal_no_se_rotula_como_tal(string nombre, string porque)
    {
        Buscar(nombre).Naturaleza.Should().NotBe(NaturalezaJuridica.ObligacionLegal, porque);
    }

    /// <summary>
    /// El filo que protege de la sobre-afirmación: lo que no se verificó cae
    /// en <see cref="NaturalezaJuridica.RequisitoCliente"/>, la afirmación más
    /// débil. Sub-afirmar se corrige leyendo el BOE; sobre-afirmar es lo que
    /// pone al cliente en un compromiso.
    /// </summary>
    [Theory]
    [InlineData("Carretillas elevadoras")]
    [InlineData("ISO 45001")]
    [InlineData("Relación de Maquinaria")]
    public void Lo_no_verificado_se_queda_en_la_afirmacion_mas_debil(string nombre)
    {
        Buscar(nombre).Naturaleza.Should().Be(NaturalezaJuridica.RequisitoCliente,
            "no se ha verificado contra fuente oficial, así que no se le atribuye ninguna autoridad");
    }

    /// <summary>
    /// El catálogo entero tiene naturaleza asignada. Es trivial hoy porque el
    /// enum no admite nulos, pero fija el contrato para cuando alguien añada
    /// tipos nuevos: ninguno puede quedarse sin respuesta a "¿por qué me pides
    /// esto?".
    /// </summary>
    [Fact]
    public void Ningun_tipo_del_catalogo_se_queda_sin_naturaleza()
    {
        var tipos = TipoDocumentoSeedData.CrearCopiasParaTenant().ToList();

        tipos.Should().NotBeEmpty();
        tipos.Should().OnlyContain(t => Enum.IsDefined(t.Naturaleza),
            "un valor fuera del enum significaría que alguien lo construyó con un cast");
    }
}
