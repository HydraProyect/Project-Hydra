using CaeManager.Application.AsistenteIa.Preparacion;
using FluentAssertions;

namespace CaeManager.Application.Tests.AsistenteIa.Preparacion;

/// <summary>
/// Lo que se fija aquí es qué sale de TALVEG hacia el proveedor de IA. Un
/// identificador que se escape no da ningún error en ningún sitio: por eso cada
/// formato tiene su caso, y cada cosa que no debe enmascararse también.
/// Todos los datos son inventados.
/// </summary>
public class EnmascaradorIdentificadoresTests
{
    [Fact]
    public void La_orden_del_propietario_sale_sin_el_DNI_pero_con_la_empresa_el_centro_y_las_fechas()
    {
        const string orden = "Dar de alta a trabajador juan perez DNI 1233443F que entrará a hacer un " +
                             "mantenimiento el 18 de 09 hasta el 30 del 09 en Churros Mi Fritura SL en el " +
                             "centro Frituritas Valencia";

        var r = EnmascaradorIdentificadores.Enmascarar(orden);

        r.Texto.Should().NotContain("1233443");
        r.Texto.Should().Contain("DNI [DOC_1]", "la palabra que dice qué es el marcador se queda");
        r.Texto.Should().Contain("Churros Mi Fritura SL").And.Contain("Frituritas Valencia")
            .And.Contain("18 de 09").And.Contain("30 del 09").And.Contain("juan perez");
        r.Identificadores.Should().ContainSingle().Which.Should().BeEquivalentTo(new IdentificadorEnmascarado(
            "[DOC_1]", "1233443F", "01233443F", TipoIdentificadorEnmascarado.DocumentoPersona));
    }

    [Theory]
    [InlineData("DNI 12345678Z", "12345678Z", TipoIdentificadorEnmascarado.DocumentoPersona)]
    [InlineData("dni 12345678-z", "12345678Z", TipoIdentificadorEnmascarado.DocumentoPersona)]
    [InlineData("con el 12345678 Z", "12345678Z", TipoIdentificadorEnmascarado.DocumentoPersona)]
    [InlineData("NIE X1234567L", "X1234567L", TipoIdentificadorEnmascarado.DocumentoPersona)]
    [InlineData("nie y-1234567-x", "Y1234567X", TipoIdentificadorEnmascarado.DocumentoPersona)]
    [InlineData("la empresa B12345674", "B12345674", TipoIdentificadorEnmascarado.NifEmpresa)]
    [InlineData("pasaporte PAA123456", "PAA123456", TipoIdentificadorEnmascarado.Pasaporte)]
    [InlineData("pasaporte nº 7K4410392", "7K4410392", TipoIdentificadorEnmascarado.Pasaporte)]
    [InlineData("TIE ABC123456", "ABC123456", TipoIdentificadorEnmascarado.Pasaporte)]
    [InlineData("escribe a marta.gil@ejemplo.es", "marta.gil@ejemplo.es", TipoIdentificadorEnmascarado.Correo)]
    [InlineData("DNI 12345678", "12345678", TipoIdentificadorEnmascarado.DocumentoPersona)]
    public void Cada_formato_se_enmascara(string texto, string normalizado, TipoIdentificadorEnmascarado tipo)
    {
        var r = EnmascaradorIdentificadores.Enmascarar(texto);

        r.Identificadores.Should().ContainSingle();
        r.Identificadores[0].ValorNormalizado.Should().Be(normalizado);
        r.Identificadores[0].Tipo.Should().Be(tipo);
        r.Texto.Should().NotContain(r.Identificadores[0].ValorOriginal);
    }

    [Theory]
    [InlineData("mantenimiento del 18/09/2026 al 30/09/2026")]
    [InlineData("pedido 12345678 de material")]
    [InlineData("el pasaporte francés de Ana")]
    [InlineData("Centro Frituritas Valencia Norte, nave 7")]
    [InlineData("llama al 612345678")]
    public void Lo_que_no_es_un_identificador_no_se_toca(string texto)
    {
        // Enmascarar de más también es un fallo: un Centro o una fecha enmascarados
        // hacen que el modelo se abstenga en vez de resolverlos.
        var r = EnmascaradorIdentificadores.Enmascarar(texto);

        r.Identificadores.Should().BeEmpty();
        r.Texto.Should().Be(texto);
    }

    [Fact]
    public void El_mismo_documento_escrito_dos_veces_recibe_el_mismo_marcador()
    {
        var r = EnmascaradorIdentificadores.Enmascarar(
            "alta de 12345678Z; repito, el 12345678-z. Y aparte X1234567L");

        r.Texto.Should().Be("alta de [DOC_1]; repito, el [DOC_1]. Y aparte [DOC_2]");
        r.Identificadores.Select(i => i.Marcador).Should().Equal("[DOC_1]", "[DOC_2]");
    }

    [Fact]
    public void Un_documento_con_la_letra_mal_se_enmascara_igual()
    {
        // Mal tecleado sigue siendo el DNI de alguien.
        var r = EnmascaradorIdentificadores.Enmascarar("DNI 12345678A");

        r.Texto.Should().Be("DNI [DOC_1]");
    }

    [Fact]
    public void Un_marcador_escrito_en_la_orden_se_desactiva_y_no_se_restaura()
    {
        // Sin esto, «[DOC_1]» escrito a mano recibiría al restaurar el DNI de otra persona.
        var r = EnmascaradorIdentificadores.Enmascarar("asigna [DOC_1] a la persona 12345678Z");

        r.Texto.Should().Be("asigna (DOC_1) a la persona [DOC_1]");
        r.Restaurar(r.Texto).Should().Be("asigna (DOC_1) a la persona 12345678Z");
    }

    [Fact]
    public void Restaurar_devuelve_el_original_y_deja_a_la_vista_los_marcadores_inventados()
    {
        var r = EnmascaradorIdentificadores.Enmascarar("de 12345678Z a marta.gil@ejemplo.es");

        r.Restaurar("persona [DOC_1], correo [CORREO_1], y [DOC_9]")
            .Should().Be("persona 12345678Z, correo marta.gil@ejemplo.es, y [DOC_9]");
        r.Buscar("[DOC_9]").Should().BeNull();
    }

    [Fact]
    public void El_correo_se_toma_entero_aunque_su_parte_local_parezca_un_DNI()
    {
        var r = EnmascaradorIdentificadores.Enmascarar("12345678z@ejemplo.es");

        r.Texto.Should().Be("[CORREO_1]");
        r.Identificadores.Should().ContainSingle().Which.Tipo.Should().Be(TipoIdentificadorEnmascarado.Correo);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("sin nada que retirar", "sin nada que retirar")]
    public void Un_texto_sin_identificadores_sale_igual(string? texto, string esperado)
    {
        var r = EnmascaradorIdentificadores.Enmascarar(texto);

        r.Texto.Should().Be(esperado);
        r.Identificadores.Should().BeEmpty();
    }
}
