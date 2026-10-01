using CaeManager.Application.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class NombreArchivoDocumentoTests
{
    private static readonly DateOnly Emision = new(2026, 3, 14);

    [Fact]
    public void Suelto_sigue_el_formato_Apellidos_Nombre_Tipo_emitido_fecha()
    {
        NombreArchivoDocumento.Suelto("García López Marta", "Certificado de aptitud médica", Emision)
            .Should().Be("Garcia Lopez Marta - Aptitud medica - emitido 2026-03-14.pdf");
    }

    [Theory]
    [InlineData("Certificado de aptitud médica")]
    [InlineData("Reconocimiento médico")]
    [InlineData("RECONOCIMIENTO MEDICO")]
    public void Aptitud_medica_y_Reconocimiento_medico_son_el_mismo_tipo(string tipo)
    {
        NombreArchivoDocumento.Suelto("Perez Ana", tipo, Emision).Should().Contain(" - Aptitud medica - ");
    }

    [Fact]
    public void Los_otros_tipos_llevan_su_nombre_completo_sin_acentos()
    {
        NombreArchivoDocumento.Suelto("Perez Ana", "Formación Art. 19", Emision)
            .Should().Be("Perez Ana - Formacion Art. 19 - emitido 2026-03-14.pdf");
        NombreArchivoDocumento.Suelto("Perez Ana", "Información Art. 18", Emision)
            .Should().Contain("Informacion Art. 18");
    }

    [Fact]
    public void La_enie_y_los_acentos_desaparecen()
    {
        NombreArchivoDocumento.Suelto("Muñoz Peña Íñigo", "Entrega de EPI", Emision)
            .Should().StartWith("Munoz Pena Inigo - ");
    }

    [Fact]
    public void Una_coincidencia_de_tipo_y_fecha_lleva_sufijo_v2()
    {
        NombreArchivoDocumento.Suelto("Perez Ana", "Entrega de EPI", Emision, ordinalMismoDia: 2)
            .Should().Be("Perez Ana - Entrega de EPI - emitido 2026-03-14_v2.pdf");
        NombreArchivoDocumento.Suelto("Perez Ana", "Entrega de EPI", Emision, ordinalMismoDia: 1)
            .Should().NotContain("_v");
    }

    [Fact]
    public void Los_caracteres_invalidos_en_ficheros_se_sustituyen()
    {
        var nombre = NombreArchivoDocumento.Suelto("Pérez/Ana:*?\"<>|\\", "Tipo\0raro\n", Emision);
        nombre.Should().NotContainAny("/", "\\", ":", "*", "?", "\"", "<", ">", "|", "\0", "\n");
        nombre.Should().Be("Perez Ana - Tipo raro - emitido 2026-03-14.pdf");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    [InlineData("???", "***")]
    public void Los_nombres_vacios_caen_en_alternativas(string? propietario, string? tipo)
    {
        NombreArchivoDocumento.Suelto(propietario, tipo, Emision)
            .Should().Be("Sin nombre - Documento - emitido 2026-03-14.pdf");
    }

    [Fact]
    public void Un_nombre_muy_largo_se_recorta_conservando_fecha_version_y_extension()
    {
        var largo = string.Join(' ', Enumerable.Repeat("Apellidoextraordinariamentelargo", 10));
        var nombre = NombreArchivoDocumento.Suelto(largo, "Tipo " + largo, Emision, ordinalMismoDia: 3);

        nombre.Length.Should().BeLessThanOrEqualTo(NombreArchivoDocumento.LongitudMaxima);
        nombre.Should().EndWith(" - emitido 2026-03-14_v3.pdf");
    }

    [Fact]
    public void El_recorte_nunca_deja_una_parte_vacia()
    {
        var nombre = NombreArchivoDocumento.Suelto(new string('A', 500), new string('B', 500), Emision);
        nombre.Length.Should().BeLessThanOrEqualTo(NombreArchivoDocumento.LongitudMaxima);
        nombre.Split(" - ").Should().HaveCount(3).And.OnlyContain(p => p.Length > 0);
    }

    [Fact]
    public void Entrada_lleva_el_orden_con_dos_cifras_y_la_fecha_entre_parentesis()
    {
        NombreArchivoDocumento.Entrada(1, "Certificado de aptitud médica", Emision)
            .Should().Be("01-Aptitud medica (2026-03-14).pdf");
        NombreArchivoDocumento.Entrada(7, "Otro", Emision, 2)
            .Should().Be("07-Otro (2026-03-14)_v2.pdf");
    }

    [Fact]
    public void El_nombre_nunca_incluye_un_DNI_pasado_como_propietario_de_otro_campo()
    {
        // El servicio solo recibe nombres: un DNI solo aparecería si alguien lo pasara como nombre.
        NombreArchivoDocumento.Suelto("Perez Ana", "Entrega de EPI", Emision).Should().NotMatchRegex(@"\d{8}[A-Z]");
    }

    [Fact]
    public void MismoTipo_agrupa_los_alias_y_distingue_el_resto()
    {
        NombreArchivoDocumento.MismoTipo("Certificado de aptitud médica", "Reconocimiento médico").Should().BeTrue();
        NombreArchivoDocumento.MismoTipo("Entrega de EPI", "entrega de epi").Should().BeTrue();
        NombreArchivoDocumento.MismoTipo("Entrega de EPI", "Formación Art. 19").Should().BeFalse();
    }
}
