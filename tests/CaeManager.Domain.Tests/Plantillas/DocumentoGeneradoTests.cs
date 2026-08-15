using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Plantillas;

public class DocumentoGeneradoTests
{
    private static DocumentoGenerado Crear(
        Guid? trabajadorId = null, Guid? empresaId = null, Guid? centroId = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow, trabajadorId, empresaId, centroId);

    [Fact]
    public void Constructor_asigna_los_campos_recibidos_y_nace_en_estado_generado()
    {
        var versionId = Guid.NewGuid();
        var documentoId = Guid.NewGuid();
        var usuarioId = Guid.NewGuid();
        var ahora = DateTime.UtcNow;

        var documentoGenerado = new DocumentoGenerado(versionId, documentoId, "{\"CIF\":\"B123\"}", usuarioId, ahora);

        documentoGenerado.PlantillaDocumentoVersionId.Should().Be(versionId);
        documentoGenerado.DocumentoId.Should().Be(documentoId);
        documentoGenerado.DatosUtilizadosJson.Should().Be("{\"CIF\":\"B123\"}");
        documentoGenerado.GeneradoPorUsuarioId.Should().Be(usuarioId);
        documentoGenerado.GeneradoEnUtc.Should().Be(ahora);
        documentoGenerado.Estado.Should().Be(EstadoDocumentoGenerado.Generado);
    }

    [Fact]
    public void Constructor_rechaza_version_vacia()
    {
        var accion = () => new DocumentoGenerado(Guid.Empty, Guid.NewGuid(), "{}", Guid.NewGuid(), DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_documento_vacio()
    {
        var accion = () => new DocumentoGenerado(Guid.NewGuid(), Guid.Empty, "{}", Guid.NewGuid(), DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_usuario_vacio()
    {
        var accion = () => new DocumentoGenerado(Guid.NewGuid(), Guid.NewGuid(), "{}", Guid.Empty, DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_admite_contexto_de_trabajador_empresa_y_centro_opcionales()
    {
        var trabajadorId = Guid.NewGuid();
        var empresaId = Guid.NewGuid();
        var centroId = Guid.NewGuid();

        var documentoGenerado = Crear(trabajadorId, empresaId, centroId);

        documentoGenerado.TrabajadorId.Should().Be(trabajadorId);
        documentoGenerado.EmpresaId.Should().Be(empresaId);
        documentoGenerado.CentroId.Should().Be(centroId);
    }
}
