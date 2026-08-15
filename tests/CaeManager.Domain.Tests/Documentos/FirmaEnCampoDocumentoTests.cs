using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

public class FirmaEnCampoDocumentoTests
{
    private static readonly string HashValido = new('a', FirmaEnCampoDocumento.LongitudHashSha256);

    private static FirmaEnCampoDocumento CrearFirma(string? ubicacion = "Madrid") =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Juan Pérez", "GestorCae", DateTime.UtcNow, ubicacion, HashValido);

    [Fact]
    public void Constructor_asigna_los_campos_recibidos()
    {
        var documentoId = Guid.NewGuid();
        var firmanteUsuarioId = Guid.NewGuid();
        var firmadoEnUtc = DateTime.UtcNow;

        var firma = new FirmaEnCampoDocumento(
            documentoId, firmanteUsuarioId, "Juan Pérez", "GestorCae", firmadoEnUtc, "Madrid", HashValido);

        firma.DocumentoId.Should().Be(documentoId);
        firma.FirmanteUsuarioId.Should().Be(firmanteUsuarioId);
        firma.FirmanteNombre.Should().Be("Juan Pérez");
        firma.FirmanteRol.Should().Be("GestorCae");
        firma.FirmadoEnUtc.Should().Be(firmadoEnUtc);
        firma.Ubicacion.Should().Be("Madrid");
        firma.HashSha256Pdf.Should().Be(HashValido);
    }

    [Fact]
    public void Constructor_acepta_ubicacion_nula()
    {
        var firma = CrearFirma(ubicacion: null);

        firma.Ubicacion.Should().BeNull();
    }

    [Fact]
    public void Constructor_rechaza_documento_vacio()
    {
        var accion = () => new FirmaEnCampoDocumento(
            Guid.Empty, Guid.NewGuid(), "Juan Pérez", "GestorCae", DateTime.UtcNow, null, HashValido);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_firmante_vacio()
    {
        var accion = () => new FirmaEnCampoDocumento(
            Guid.NewGuid(), Guid.Empty, "Juan Pérez", "GestorCae", DateTime.UtcNow, null, HashValido);

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("demasiado-corto")]
    public void Constructor_rechaza_hash_con_longitud_incorrecta(string hashInvalido)
    {
        var accion = () => new FirmaEnCampoDocumento(
            Guid.NewGuid(), Guid.NewGuid(), "Juan Pérez", "GestorCae", DateTime.UtcNow, null, hashInvalido);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_hash_nulo()
    {
        var accion = () => new FirmaEnCampoDocumento(
            Guid.NewGuid(), Guid.NewGuid(), "Juan Pérez", "GestorCae", DateTime.UtcNow, null, null!);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_trunca_campos_de_texto_demasiado_largos()
    {
        var nombreLargo = new string('x', FirmaEnCampoDocumento.LongitudMaximaFirmanteNombre + 50);
        var rolLargo = new string('y', FirmaEnCampoDocumento.LongitudMaximaFirmanteRol + 10);
        var ubicacionLarga = new string('z', FirmaEnCampoDocumento.LongitudMaximaUbicacion + 10);

        var firma = new FirmaEnCampoDocumento(
            Guid.NewGuid(), Guid.NewGuid(), nombreLargo, rolLargo, DateTime.UtcNow, ubicacionLarga, HashValido);

        firma.FirmanteNombre.Should().HaveLength(FirmaEnCampoDocumento.LongitudMaximaFirmanteNombre);
        firma.FirmanteRol.Should().HaveLength(FirmaEnCampoDocumento.LongitudMaximaFirmanteRol);
        firma.Ubicacion.Should().HaveLength(FirmaEnCampoDocumento.LongitudMaximaUbicacion);
    }
}
