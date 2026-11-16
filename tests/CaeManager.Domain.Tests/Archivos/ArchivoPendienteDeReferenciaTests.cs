using CaeManager.Domain.Archivos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Archivos;

public class ArchivoPendienteDeReferenciaTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_clave_no_se_apunta(string clave)
    {
        var crear = () => new ArchivoPendienteDeReferencia(clave, DateTime.UtcNow);

        crear.Should().Throw<ArgumentException>().WithParameterName("clave");
    }

    [Fact]
    public void Una_clave_que_pasa_de_la_columna_no_se_apunta()
    {
        var crear = () => new ArchivoPendienteDeReferencia(
            new string('a', ArchivoPendienteDeReferencia.LongitudMaximaClave + 1), DateTime.UtcNow);

        crear.Should().Throw<ArgumentException>().WithParameterName("clave");
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Una_hora_que_no_va_en_UTC_no_se_apunta(DateTimeKind tipo)
    {
        var crear = () => new ArchivoPendienteDeReferencia("a/x.pdf", new DateTime(2026, 11, 16, 20, 0, 0, tipo));

        crear.Should().Throw<ArgumentException>().WithParameterName("guardadoEnUtc");
    }

    [Fact]
    public void La_clave_en_el_limite_se_apunta_con_su_hora_UTC()
    {
        var clave = new string('a', ArchivoPendienteDeReferencia.LongitudMaximaClave);
        var hora = new DateTime(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);

        var pendiente = new ArchivoPendienteDeReferencia(clave, hora);

        pendiente.Clave.Should().Be(clave);
        pendiente.GuardadoEnUtc.Should().Be(hora);
        pendiente.GuardadoEnUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void La_clave_cabe_en_las_dos_columnas_que_la_referencian()
    {
        ArchivoPendienteDeReferencia.LongitudMaximaClave.Should()
            .BeGreaterThanOrEqualTo(global::CaeManager.Domain.Documentos.Documento.LongitudMaximaArchivoUrl)
            .And.BeGreaterThanOrEqualTo(global::CaeManager.Domain.Plantillas.PlantillaDocumentoVersion.LongitudMaximaArchivoOriginalUrl);
    }
}
