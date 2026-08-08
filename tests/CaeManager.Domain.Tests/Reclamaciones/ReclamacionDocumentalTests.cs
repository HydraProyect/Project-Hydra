using CaeManager.Domain.Reclamaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Reclamaciones;

public class ReclamacionDocumentalTests
{
    [Fact]
    public void Constructor_asigna_los_valores_y_crea_los_documentos_hijos()
    {
        var clienteId = Guid.NewGuid();
        var usuarioId = Guid.NewGuid();
        var fecha = DateTime.UtcNow;
        var documentoIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var reclamacion = new ReclamacionDocumental(clienteId, usuarioId, "cliente@example.com", fecha, documentoIds);

        reclamacion.ClienteId.Should().Be(clienteId);
        reclamacion.EnviadoPorUsuarioId.Should().Be(usuarioId);
        reclamacion.DestinatarioEmail.Should().Be("cliente@example.com");
        reclamacion.FechaEnvioUtc.Should().Be(fecha);
        reclamacion.Documentos.Should().HaveCount(2);
        reclamacion.Documentos.Should().OnlyContain(d => d.ReclamacionDocumentalId == reclamacion.Id);
        reclamacion.Documentos.Select(d => d.DocumentoId).Should().BeEquivalentTo(documentoIds);
    }

    [Fact]
    public void Constructor_ignora_documentos_duplicados()
    {
        var documentoId = Guid.NewGuid();

        var reclamacion = new ReclamacionDocumental(
            Guid.NewGuid(), Guid.NewGuid(), "cliente@example.com", DateTime.UtcNow, [documentoId, documentoId]);

        reclamacion.Documentos.Should().ContainSingle();
    }

    [Fact]
    public void Constructor_rechaza_cliente_vacio()
    {
        var accion = () => new ReclamacionDocumental(
            Guid.Empty, Guid.NewGuid(), "cliente@example.com", DateTime.UtcNow, [Guid.NewGuid()]);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_destinatario_vacio()
    {
        var accion = () => new ReclamacionDocumental(
            Guid.NewGuid(), Guid.NewGuid(), "  ", DateTime.UtcNow, [Guid.NewGuid()]);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_sin_documentos()
    {
        var accion = () => new ReclamacionDocumental(
            Guid.NewGuid(), Guid.NewGuid(), "cliente@example.com", DateTime.UtcNow, []);

        accion.Should().Throw<ArgumentException>();
    }
}
