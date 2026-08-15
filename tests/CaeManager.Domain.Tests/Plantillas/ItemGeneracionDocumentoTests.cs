using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Plantillas;

public class ItemGeneracionDocumentoTests
{
    [Fact]
    public void Constructor_nace_pendiente()
    {
        var item = new ItemGeneracionDocumento(Guid.NewGuid(), Guid.NewGuid());

        item.Estado.Should().Be(EstadoItemGeneracion.Pendiente);
        item.DocumentoGeneradoId.Should().BeNull();
        item.Error.Should().BeNull();
    }

    [Fact]
    public void Constructor_rechaza_lote_vacio()
    {
        var accion = () => new ItemGeneracionDocumento(Guid.Empty, Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_trabajador_vacio()
    {
        var accion = () => new ItemGeneracionDocumento(Guid.NewGuid(), Guid.Empty);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarcarCompletado_asigna_el_documento_generado()
    {
        var item = new ItemGeneracionDocumento(Guid.NewGuid(), Guid.NewGuid());
        var documentoGeneradoId = Guid.NewGuid();

        item.MarcarCompletado(documentoGeneradoId);

        item.Estado.Should().Be(EstadoItemGeneracion.Completado);
        item.DocumentoGeneradoId.Should().Be(documentoGeneradoId);
    }

    [Fact]
    public void MarcarFallido_guarda_el_mensaje_de_error()
    {
        var item = new ItemGeneracionDocumento(Guid.NewGuid(), Guid.NewGuid());

        item.MarcarFallido("No encontramos al trabajador.");

        item.Estado.Should().Be(EstadoItemGeneracion.Fallido);
        item.Error.Should().Be("No encontramos al trabajador.");
    }

    [Fact]
    public void MarcarFallido_con_mensaje_vacio_usa_un_mensaje_por_defecto()
    {
        var item = new ItemGeneracionDocumento(Guid.NewGuid(), Guid.NewGuid());

        item.MarcarFallido("  ");

        item.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Rechaza_marcar_un_item_ya_procesado()
    {
        var item = new ItemGeneracionDocumento(Guid.NewGuid(), Guid.NewGuid());
        item.MarcarCompletado(Guid.NewGuid());

        var accion = () => item.MarcarFallido("error tardío");

        accion.Should().Throw<InvalidOperationException>();
    }
}
