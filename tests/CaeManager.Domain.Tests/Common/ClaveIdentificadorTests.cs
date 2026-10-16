using CaeManager.Domain.Common;
using FluentAssertions;
using System.Globalization;
using Xunit;

namespace CaeManager.Domain.Tests.Common;

public class ClaveIdentificadorTests
{
    [Theory]
    [InlineData("12.345-678 z", TipoClaveIdentificador.Dni, "12345678Z", "DNI:12345678Z")]
    [InlineData("1233443e", TipoClaveIdentificador.Dni, "01233443E", "DNI:01233443E")]
    [InlineData("x 1234567-l", TipoClaveIdentificador.Nie, "X1234567L", "NIE:X1234567L")]
    [InlineData("y1234567x", TipoClaveIdentificador.Nie, "Y1234567X", "NIE:Y1234567X")]
    [InlineData("z1234567r", TipoClaveIdentificador.Nie, "Z1234567R", "NIE:Z1234567R")]
    [InlineData("b-1234567.4", TipoClaveIdentificador.NifEmpresa, "B12345674", "NIF:B12345674")]
    [InlineData("p1234567d", TipoClaveIdentificador.NifEmpresa, "P1234567D", "NIF:P1234567D")]
    public void Crea_claves_normalizadas(string documento, TipoClaveIdentificador tipo, string valor, string texto)
    {
        var clave = ClaveIdentificador.DesdeDocumentoEspanol(documento);
        clave.Should().NotBeNull();
        clave!.Tipo.Should().Be(tipo);
        clave.Valor.Should().Be(valor);
        clave.Pais.Should().BeNull();
        clave.ToString().Should().Be(texto);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    [InlineData("E")]
    [InlineData("H")]
    public void Cif_abeh_exigen_control_numerico(string prefijo)
    {
        ClaveIdentificador.DesdeDocumentoEspanol(prefijo + "12345674").Should().NotBeNull();
        ClaveIdentificador.DesdeDocumentoEspanol(prefijo + "1234567D").Should().BeNull();
    }

    [Theory]
    [InlineData("P")]
    [InlineData("Q")]
    [InlineData("S")]
    public void Cif_kpqs_exigen_control_alfabetico(string prefijo)
    {
        ClaveIdentificador.DesdeDocumentoEspanol(prefijo + "1234567D").Should().NotBeNull();
        ClaveIdentificador.DesdeDocumentoEspanol(prefijo + "12345674").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" .- ")]
    [InlineData("12345678A")]
    [InlineData("1233443F")]
    [InlineData("X1234567A")]
    [InlineData("B12345671")]
    [InlineData("K1234567A")]
    [InlineData("AAA123456")]
    [InlineData("AB123456")]
    [InlineData("١٢٣٤٥٦٧٨Z")]
    public void Rechaza_documentos_sin_control_valido(string? documento)
    {
        ClaveIdentificador.DesdeDocumentoEspanol(documento).Should().BeNull();
    }

    [Fact]
    public void Conserva_ceros_del_pasaporte_y_normaliza_sus_separadores()
    {
        var clave = ClaveIdentificador.DesdePasaporte(" fr ", " 00 ab-123456\u00a0");
        clave!.Tipo.Should().Be(TipoClaveIdentificador.Pasaporte);
        clave.Pais.Should().Be("FR");
        clave.Valor.Should().Be("00AB123456");
        clave.ToString().Should().Be("PAS:FR:00AB123456");
    }

    [Theory]
    [InlineData(null, "AB123456")]
    [InlineData("", "AB123456")]
    [InlineData("ZZ", "AB123456")]
    [InlineData("UK", "AB123456")]
    [InlineData("XK", "AB123456")]
    [InlineData("FRA", "AB123456")]
    [InlineData("F R", "AB123456")]
    [InlineData("FR", null)]
    [InlineData("FR", "")]
    [InlineData("FR", " - ")]
    [InlineData("FR", "1234")]
    [InlineData("FR", "123456789012345678901")]
    [InlineData("FR", "AB.123456")]
    [InlineData("FR", "ÁB123456")]
    [InlineData("FR", "AB/123456")]
    public void Rechaza_pasaportes_sin_pais_oficial_o_numero_admitido(string? pais, string? numero)
    {
        ClaveIdentificador.DesdePasaporte(pais, numero).Should().BeNull();
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("12345678901234567890")]
    public void Acepta_los_limites_de_longitud_del_pasaporte(string numero)
    {
        ClaveIdentificador.DesdePasaporte("ES", numero)!.Valor.Should().Be(numero);
    }

    [Fact]
    public void La_igualdad_y_el_hash_dependen_del_valor_normalizado()
    {
        var primera = ClaveIdentificador.DesdeDocumentoEspanol("1233443e");
        var segunda = ClaveIdentificador.DesdeDocumentoEspanol("01.233.443-E");
        primera.Should().Be(segunda);
        primera!.GetHashCode().Should().Be(segunda!.GetHashCode());
        ClaveIdentificador.DesdePasaporte("fr", "ab-123456").Should()
            .Be(ClaveIdentificador.DesdePasaporte("FR", "AB123456"));
    }

    [Fact]
    public void El_pais_y_el_tipo_distinguen_claves()
    {
        ClaveIdentificador.DesdePasaporte("FR", "AB123456").Should()
            .NotBe(ClaveIdentificador.DesdePasaporte("ES", "AB123456"));
        ClaveIdentificador.DesdeDocumentoEspanol("01234567L").Should()
            .NotBe(ClaveIdentificador.DesdeDocumentoEspanol("X1234567L"));
        ClaveIdentificador.DesdeDocumentoEspanol("12345678Z").Should()
            .NotBe(ClaveIdentificador.DesdePasaporte("ES", "12345678Z"));
    }

    [Fact]
    public void Admite_exactamente_249_codigos_de_pais()
    {
        var aceptados = 0;
        for (var primera = 'A'; primera <= 'Z'; primera++)
        for (var segunda = 'A'; segunda <= 'Z'; segunda++)
            if (ClaveIdentificador.DesdePasaporte($"{primera}{segunda}", "12345") is not null)
                aceptados++;
        aceptados.Should().Be(249);
        foreach (var pais in new[] { "AX", "BQ", "CW", "SX", "SS", "PS", "VA", "AQ", "UM" })
            ClaveIdentificador.DesdePasaporte(pais, "12345").Should().NotBeNull();
    }

    [Fact]
    public void Una_clave_con_K_no_se_genera_porque_es_de_persona_fisica()
    {
        ClaveIdentificador.DesdeDocumentoEspanol("K1234567D").Should().BeNull();
    }

    [Fact]
    public void Normaliza_con_cultura_invariante_aunque_la_cultura_actual_sea_turca()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var clave = ClaveIdentificador.DesdePasaporte("it", "ab-123456i");
            clave.Should().NotBeNull();
            clave!.Pais.Should().Be("IT");
            clave.Valor.Should().Be("AB123456I");
            ValidadorIdentificacion.Normalizar("x1234567l").Should().Be("X1234567L");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Rechaza_entradas_no_ascii_aunque_se_plieguen_a_ascii_en_mayusculas()
    {
        ClaveIdentificador.DesdeDocumentoEspanol("ſ1234567D").Should().BeNull();
        ClaveIdentificador.DesdePasaporte("ES", "ABſ1234").Should().BeNull();
    }

    [Fact]
    public void La_lista_de_paises_es_exactamente_la_oficial()
    {
        var aceptados = new List<string>();
        for (var primera = 'A'; primera <= 'Z'; primera++)
        for (var segunda = 'A'; segunda <= 'Z'; segunda++)
            if (ClaveIdentificador.DesdePasaporte($"{primera}{segunda}", "12345") is not null)
                aceptados.Add($"{primera}{segunda}");

        var huella = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(string.Join(' ', aceptados))));
        huella.Should().Be("E1C950D24CEB933AC49EAA44DE8D1A7FFB6BF40BFE519A5A99D791CB50332197");
    }
}
