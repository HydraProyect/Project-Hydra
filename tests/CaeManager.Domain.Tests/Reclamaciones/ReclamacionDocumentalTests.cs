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

    // ---- Un documento que falta (nunca subido) es una línea sin Documento: «Pedir», 2026-10-04 ----

    [Fact]
    public void ParaCliente_registra_un_documento_que_falta_con_su_tipo_y_su_trabajador_y_sin_documento()
    {
        var tipoId = Guid.NewGuid();
        var trabajadorId = Guid.NewGuid();

        var reclamacion = ReclamacionDocumental.ParaCliente(
            Guid.NewGuid(), Guid.NewGuid(), "cliente@example.com", DateTime.UtcNow, [],
            documentosQueFaltan: [new DocumentoQueFaltaPedido(tipoId, trabajadorId)]);

        var linea = reclamacion.Documentos.Should().ContainSingle().Which;
        linea.EsDocumentoQueFalta.Should().BeTrue();
        linea.DocumentoId.Should().BeNull("no hay Documento al que apuntar");
        linea.TipoDocumentoId.Should().Be(tipoId);
        linea.TrabajadorId.Should().Be(trabajadorId);
        linea.ReclamacionDocumentalId.Should().Be(reclamacion.Id);
    }

    [Fact]
    public void Lo_que_vence_y_lo_que_falta_conviven_en_la_misma_reclamacion_cada_uno_con_su_forma()
    {
        var documentoId = Guid.NewGuid();

        var reclamacion = ReclamacionDocumental.ParaCliente(
            Guid.NewGuid(), Guid.NewGuid(), "cliente@example.com", DateTime.UtcNow, [documentoId],
            documentosQueFaltan: [new DocumentoQueFaltaPedido(Guid.NewGuid(), Guid.NewGuid())]);

        reclamacion.Documentos.Should().HaveCount(2);
        var conDocumento = reclamacion.Documentos.Single(d => !d.EsDocumentoQueFalta);
        conDocumento.DocumentoId.Should().Be(documentoId);
        conDocumento.TipoDocumentoId.Should().BeNull("la forma «Documento» no lleva Tipo ni Trabajador");
        conDocumento.TrabajadorId.Should().BeNull();
    }

    [Fact]
    public void Una_reclamacion_solo_de_lo_que_falta_es_valida_pero_sin_nada_sigue_rechazandose()
    {
        var soloFalta = () => ReclamacionDocumental.ParaCliente(
            Guid.NewGuid(), Guid.NewGuid(), "cliente@example.com", DateTime.UtcNow, [],
            documentosQueFaltan: [new DocumentoQueFaltaPedido(Guid.NewGuid(), Guid.NewGuid())]);
        var nada = () => ReclamacionDocumental.ParaCliente(
            Guid.NewGuid(), Guid.NewGuid(), "cliente@example.com", DateTime.UtcNow, [], documentosQueFaltan: []);

        soloFalta.Should().NotThrow();
        nada.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Lo_que_falta_a_un_cliente_debe_decir_a_que_trabajador_le_falta()
    {
        var accion = () => ReclamacionDocumental.ParaCliente(
            Guid.NewGuid(), Guid.NewGuid(), "cliente@example.com", DateTime.UtcNow, [],
            documentosQueFaltan: [new DocumentoQueFaltaPedido(Guid.NewGuid(), null)]);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParaEmpresa_registra_un_documento_de_empresa_que_falta_sin_trabajador()
    {
        var tipoId = Guid.NewGuid();

        var reclamacion = ReclamacionDocumental.ParaEmpresa(
            Guid.NewGuid(), Guid.NewGuid(), "agenda@empresa.example", DateTime.UtcNow, [],
            documentosQueFaltan: [new DocumentoQueFaltaPedido(tipoId, null)]);

        var linea = reclamacion.Documentos.Should().ContainSingle().Which;
        linea.DocumentoId.Should().BeNull();
        linea.TipoDocumentoId.Should().Be(tipoId);
        linea.TrabajadorId.Should().BeNull("el sujeto es la propia Empresa de la reclamación");
    }

    [Fact]
    public void Lo_que_falta_a_una_empresa_no_puede_nombrar_a_un_trabajador()
    {
        var accion = () => ReclamacionDocumental.ParaEmpresa(
            Guid.NewGuid(), Guid.NewGuid(), "agenda@empresa.example", DateTime.UtcNow, [],
            documentosQueFaltan: [new DocumentoQueFaltaPedido(Guid.NewGuid(), Guid.NewGuid())]);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Dos_veces_lo_mismo_que_falta_es_una_sola_linea()
    {
        var falta = new DocumentoQueFaltaPedido(Guid.NewGuid(), Guid.NewGuid());

        var reclamacion = ReclamacionDocumental.ParaCliente(
            Guid.NewGuid(), Guid.NewGuid(), "cliente@example.com", DateTime.UtcNow, [], documentosQueFaltan: [falta, falta]);

        reclamacion.Documentos.Should().ContainSingle();
    }

    [Fact]
    public void La_linea_de_un_documento_que_falta_exige_reclamacion_y_tipo_y_rechaza_un_trabajador_vacio()
    {
        var reclamacion = Guid.NewGuid();

        ((Action)(() => ReclamacionDocumentalDocumento.DeDocumentoQueFalta(Guid.Empty, Guid.NewGuid(), null))).Should().Throw<ArgumentException>();
        ((Action)(() => ReclamacionDocumentalDocumento.DeDocumentoQueFalta(reclamacion, Guid.Empty, null))).Should().Throw<ArgumentException>();
        ((Action)(() => ReclamacionDocumentalDocumento.DeDocumentoQueFalta(reclamacion, Guid.NewGuid(), Guid.Empty))).Should().Throw<ArgumentException>();
    }
}
