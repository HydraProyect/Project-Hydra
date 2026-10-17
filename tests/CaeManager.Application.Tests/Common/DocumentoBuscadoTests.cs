using CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;
using CaeManager.Domain.Common;
using FluentAssertions;

namespace CaeManager.Application.Tests.Common;

public class DocumentoBuscadoTests
{
    [Theory]
    [InlineData("12.345.678-z", TipoIdentificacion.Dni)]
    [InlineData("x1234567l", TipoIdentificacion.Nie)]
    [InlineData("Y1234567X", TipoIdentificacion.Nie)]
    [InlineData("Z1234567R", TipoIdentificacion.Nie)]
    [InlineData("B12345674", TipoIdentificacion.NifEmpresa)]
    [InlineData("P1234567D", TipoIdentificacion.NifEmpresa)]
    public void Reconoce_documentos_completos_con_control_valido(string valor, TipoIdentificacion tipo)
    {
        var documento = DocumentoBuscado.Analizar(valor)!;
        documento.Tipo.Should().Be(tipo);
        documento.EsValido.Should().BeTrue();
        documento.Aviso.Should().BeNull();
    }

    [Theory]
    [InlineData("12345678A", "12345678Z")]
    [InlineData("X1234567A", "X1234567L")]
    [InlineData("Y1234567A", "Y1234567X")]
    [InlineData("Z1234567A", "Z1234567R")]
    public void Sugiere_la_letra_correcta_sin_convertirla_en_busqueda(string valor, string corregido)
    {
        var documento = DocumentoBuscado.Analizar(valor)!;
        documento.EsValido.Should().BeFalse();
        documento.Valor.Should().Be(valor);
        documento.Aviso.Should().Be($"La letra no cuadra: ¿quisiste decir {corregido}?");
    }

    [Theory]
    [InlineData("B12345671")]
    [InlineData("P1234567A")]
    public void Rechaza_un_cif_con_control_incorrecto(string valor) =>
        DocumentoBuscado.Analizar(valor)!.EsValido.Should().BeFalse();

    [Theory]
    [InlineData("5678")]
    [InlineData("78Z")]
    [InlineData("Ana Ruiz")]
    [InlineData("")]
    public void Un_fragmento_o_nombre_no_es_un_documento(string valor) =>
        DocumentoBuscado.Analizar(valor).Should().BeNull();
}
