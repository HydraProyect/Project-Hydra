using CaeManager.Domain.Documentos;
using CaeManager.Domain.Reclamaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Reclamaciones;

public class ReclamacionDocumentalTests
{
    [Fact]
    public void ParaCliente_asigna_los_valores_y_crea_los_documentos_hijos()
    {
        var clienteId = Guid.NewGuid();
        var usuarioId = Guid.NewGuid();
        var fecha = DateTime.UtcNow;
        var documentoIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var reclamacion = ReclamacionDocumental.ParaCliente(clienteId, usuarioId, "cliente@example.com", fecha, documentoIds);

        reclamacion.ClienteId.Should().Be(clienteId);
        reclamacion.EmpresaId.Should().BeNull("el titular es excluyente: con Cliente informado, la otra ancla queda vacía");
        reclamacion.TitularId.Should().Be(clienteId);
        reclamacion.AmbitoTitular.Should().Be(AmbitoAplicacion.Cliente);
        reclamacion.EnviadoPorUsuarioId.Should().Be(usuarioId);
        reclamacion.DestinatarioEmail.Should().Be("cliente@example.com");
        reclamacion.FechaEnvioUtc.Should().Be(fecha);
        reclamacion.Documentos.Should().HaveCount(2);
        reclamacion.Documentos.Should().OnlyContain(d => d.ReclamacionDocumentalId == reclamacion.Id);
        reclamacion.Documentos.Select(d => d.DocumentoId).Should().BeEquivalentTo(documentoIds);
    }

    [Fact]
    public void ParaCliente_ignora_documentos_duplicados()
    {
        var documentoId = Guid.NewGuid();

        var reclamacion = ReclamacionDocumental.ParaCliente(
            Guid.NewGuid(), Guid.NewGuid(), "cliente@example.com", DateTime.UtcNow, [documentoId, documentoId]);

        reclamacion.Documentos.Should().ContainSingle();
    }

    [Fact]
    public void ParaCliente_rechaza_cliente_vacio()
    {
        var accion = () => ReclamacionDocumental.ParaCliente(
            Guid.Empty, Guid.NewGuid(), "cliente@example.com", DateTime.UtcNow, [Guid.NewGuid()]);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParaCliente_rechaza_destinatario_vacio()
    {
        var accion = () => ReclamacionDocumental.ParaCliente(
            Guid.NewGuid(), Guid.NewGuid(), "  ", DateTime.UtcNow, [Guid.NewGuid()]);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParaCliente_rechaza_sin_documentos()
    {
        var accion = () => ReclamacionDocumental.ParaCliente(
            Guid.NewGuid(), Guid.NewGuid(), "cliente@example.com", DateTime.UtcNow, []);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParaEmpresa_ancla_el_titular_en_EmpresaId_y_deja_ClienteId_vacio()
    {
        var empresaId = Guid.NewGuid();
        var usuarioId = Guid.NewGuid();
        var documentoIds = new[] { Guid.NewGuid() };

        var reclamacion = ReclamacionDocumental.ParaEmpresa(
            empresaId, usuarioId, "agenda@empresa.example", DateTime.UtcNow, documentoIds);

        reclamacion.EmpresaId.Should().Be(empresaId);
        reclamacion.ClienteId.Should().BeNull(
            "una reclamación de documentos de empresa no tiene Cliente titular — si lo tuviera, los lectores por cartera la contarían dos veces");
        reclamacion.TitularId.Should().Be(empresaId);
        reclamacion.AmbitoTitular.Should().Be(AmbitoAplicacion.Empresa);
        reclamacion.Documentos.Should().ContainSingle();
    }

    [Fact]
    public void ParaEmpresa_rechaza_empresa_vacia()
    {
        var accion = () => ReclamacionDocumental.ParaEmpresa(
            Guid.Empty, Guid.NewGuid(), "agenda@empresa.example", DateTime.UtcNow, [Guid.NewGuid()]);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParaEmpresa_rechaza_sin_documentos()
    {
        var accion = () => ReclamacionDocumental.ParaEmpresa(
            Guid.NewGuid(), Guid.NewGuid(), "agenda@empresa.example", DateTime.UtcNow, []);

        accion.Should().Throw<ArgumentException>();
    }
}
