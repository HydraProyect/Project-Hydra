using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Trabajadores;

/// <summary>
/// Criterio único del documento de identidad del Trabajador (decisión de Chris, 2026-10-20): obligatorio; solo DNI, NIE o
/// Pasaporte; separadores normalizados; lo que no es DNI ni NIE se guarda como Pasaporte con aviso; marcadores rechazados.
/// </summary>
public class DocumentoIdentidadTrabajadorTests
{
    [Theory]
    [InlineData("12345678Z", "12345678Z")]
    [InlineData("12.345.678-z", "12345678Z")]
    [InlineData(" 12 345 678 Z ", "12345678Z")]
    [InlineData("1234567L", "01234567L")]
    public void Un_DNI_con_su_letra_es_DNI_y_se_guarda_sin_separadores(string valor, string esperado)
    {
        var resultado = CriterioDocumentoIdentidadTrabajador.Clasificar(valor);

        resultado.EsValido.Should().BeTrue();
        resultado.Tipo.Should().Be(TipoDocumentoTrabajador.Dni);
        resultado.Normalizado.Should().Be(esperado);
        resultado.SeGuardaComoPasaporte.Should().BeFalse();
    }

    [Theory]
    [InlineData("X1234567L")]
    [InlineData("y-1234567-x")]
    public void Un_NIE_con_su_letra_es_NIE(string valor)
    {
        var resultado = CriterioDocumentoIdentidadTrabajador.Clasificar(valor);

        resultado.Tipo.Should().Be(TipoDocumentoTrabajador.Nie);
        resultado.SeGuardaComoPasaporte.Should().BeFalse();
    }

    [Theory]
    [InlineData("AB1234567", "AB1234567")]
    [InlineData("12345678", "12345678")]       // DNI sin letra: Pasaporte, con aviso
    [InlineData("ABC123456", "ABC123456")]     // soporte de la TIE: ya no es un tipo del Trabajador
    [InlineData("B12345674", "B12345674")]     // forma de CIF: tampoco
    [InlineData("p-1234.567", "P1234567")]
    public void Lo_que_no_es_DNI_ni_NIE_se_guarda_como_Pasaporte_con_aviso(string valor, string esperado)
    {
        var resultado = CriterioDocumentoIdentidadTrabajador.Clasificar(valor);

        resultado.EsValido.Should().BeTrue();
        resultado.Tipo.Should().Be(TipoDocumentoTrabajador.Pasaporte);
        resultado.Normalizado.Should().Be(esperado);
        resultado.SeGuardaComoPasaporte.Should().BeTrue();
        CriterioDocumentoIdentidadTrabajador.AvisoPasaporte.Should().Be("No coincide con un DNI ni un NIE: se guardará como Pasaporte");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" - . ")]
    [InlineData("PENDIENTE")]
    [InlineData("SIN DNI")]
    [InlineData("SINDNI")]
    [InlineData("AB 1234567")]                  // espacios internos en algo que no es DNI ni NIE
    [InlineData("AB/12345")]                    // caracteres que no son letras ni dígitos
    [InlineData("A1")]                          // demasiado corto
    [InlineData("AB12345678901234567890")]      // demasiado largo
    public void Los_marcadores_y_los_pasaportes_no_validos_se_rechazan(string? valor)
    {
        var resultado = CriterioDocumentoIdentidadTrabajador.Clasificar(valor);

        resultado.EsValido.Should().BeFalse();
        resultado.Error.Should().NotBeNullOrWhiteSpace();
        resultado.Normalizado.Should().BeNull();
    }

    [Theory]
    [InlineData("12345678A", "DNI", 'Z')]
    [InlineData("X1234567A", "NIE", 'L')]
    public void Un_DNI_o_un_NIE_con_la_letra_mal_se_rechaza_diciendo_la_letra(string valor, string etiqueta, char letra)
    {
        var resultado = CriterioDocumentoIdentidadTrabajador.Clasificar(valor);

        resultado.EsValido.Should().BeFalse();
        resultado.Error.Should().Contain(etiqueta).And.Contain($"debería ser {letra}");
    }

    [Fact]
    public void El_alta_aplica_el_criterio_y_rechaza_un_marcador()
    {
        var empresa = Guid.NewGuid();

        Trabajador.DeEmpresa(empresa, "Ana", "Ruiz", "12.345.678-z").Dni.Should().Be("12345678Z");
        var alta = () => Trabajador.DeEmpresa(empresa, "Ana", "Ruiz", "PENDIENTE");
        alta.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Corregir_el_documento_recalcula_la_casilla_solo_hacia_delante()
    {
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Oleg", "Ivanov", "AB1234567");
        var asignacionAnterior = new VisitaTrabajador(Guid.NewGuid(), trabajador);
        trabajador.FijarMarcaExtranjeroCobrable(true);

        var anterior = trabajador.CorregirDocumentoIdentidad("X1234567L");

        anterior.Should().Be("AB1234567");
        trabajador.Dni.Should().Be("X1234567L");
        trabajador.MarcaExtranjeroCobrable.Should().BeNull("la casilla vuelve a decidirla el documento nuevo");
        CriterioExtranjeroCobrable.Efectivo(trabajador.MarcaExtranjeroCobrable, trabajador.Dni).Should().BeFalse();
        asignacionAnterior.EsExtranjeroCobrable.Should().BeTrue("la foto de la asignación ya hecha no cambia");
        asignacionAnterior.DocumentoIdentidad.Should().Be("AB1234567");
        asignacionAnterior.NombreMostrado.Should().Be("Oleg Ivanov");
        new VisitaTrabajador(Guid.NewGuid(), trabajador).EsExtranjeroCobrable.Should().BeFalse();
    }

    [Fact]
    public void Corregir_con_un_marcador_se_rechaza_y_no_toca_nada()
    {
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Oleg", "Ivanov", "AB1234567");
        trabajador.FijarMarcaExtranjeroCobrable(false);

        var corregir = () => trabajador.CorregirDocumentoIdentidad("SIN DNI");

        corregir.Should().Throw<ArgumentException>();
        trabajador.Dni.Should().Be("AB1234567");
        trabajador.MarcaExtranjeroCobrable.Should().BeFalse();
    }

    [Fact]
    public void La_foto_de_un_Trabajador_anonimizado_se_vacia_pero_conserva_la_marca()
    {
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Oleg", "Ivanov", "AB1234567");
        var asignacion = new VisitaTrabajador(Guid.NewGuid(), trabajador);

        asignacion.AnonimizarFoto();

        asignacion.DocumentoIdentidad.Should().BeNull();
        asignacion.NombreMostrado.Should().BeNull();
        asignacion.EsExtranjeroCobrable.Should().BeTrue();
    }
}
