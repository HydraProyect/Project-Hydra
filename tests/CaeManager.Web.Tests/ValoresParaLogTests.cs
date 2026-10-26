using CaeManager.Web.Services;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

public class ValoresParaLogTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("/documentos", "/documentos")]
    [InlineData("/ruta\r\nEntrada falsa\r\n", "/rutaEntrada falsa")]
    [InlineData("rol\rinyectado\n", "rolinyectado")]
    [InlineData("rol\u001b[31minyectado\u0085\u2028", "rol[31minyectado")]
    public void Elimina_saltos_de_linea(string? entrada, string esperado)
        => ValoresParaLog.Sanear(entrada).Should().Be(esperado);

    [Fact]
    public void Limita_la_longitud_despues_de_eliminar_saltos()
        => ValoresParaLog.Sanear("\r\n" + new string('a', 300)).Should().Be(new string('a', 256));

    [Fact]
    public void El_corte_no_parte_un_par_sustituto()
    {
        var resultado = ValoresParaLog.Sanear(new string('a', 255) + "\U0001F600" + "bbb");

        resultado.Should().Be(new string('a', 255));
    }
}
