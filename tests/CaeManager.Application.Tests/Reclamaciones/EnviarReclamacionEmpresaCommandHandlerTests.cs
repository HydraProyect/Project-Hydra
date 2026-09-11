using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// Hermano de <see cref="EnviarReclamacionCommandHandlerTests"/> para
/// EnviarReclamacionEmpresaCommandHandler — mismo hallazgo (2026-09-11),
/// misma regla "todo o nada", mismo join más simple (Documento.EmpresaId
/// directo, sin Trabajador/Asignación/Centro por medio).
/// </summary>
public class EnviarReclamacionEmpresaCommandHandlerTests
{
    private sealed class Entorno
    {
        public required EmpresasQueryContextFalso Empresas { get; init; }
        public required DocumentosQueryContextFalso Documentos { get; init; }
        public required TiposDocumentoQueryContextFalso TiposDocumento { get; init; }
        public required ResolucionDestinatariosAgendaServiceFalso Agenda { get; init; }
        public required RegistroEnvioReclamacionServiceFalso RegistroEnvio { get; init; }

        public EnviarReclamacionEmpresaCommandHandler CrearHandler(AlcanceDatosServiceFalso? alcanceDatos = null) => new(
            Empresas, Documentos, TiposDocumento, alcanceDatos ?? new AlcanceDatosServiceFalso(), Agenda, RegistroEnvio);
    }

    private static readonly DateOnly Hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    private sealed record Escenario(Entorno Entorno, Empresa Contraparte, TipoDocumento TipoDocumento);

    private static Escenario ConstruirEscenario()
    {
        var contraparte = new Empresa("Contratista SL", "B12345674");
        var tipoDocumento = new TipoDocumento("Póliza RC", null, false, 1, AmbitoAplicacion.Empresa);

        var entorno = new Entorno
        {
            Empresas = new EmpresasQueryContextFalso(),
            Documentos = new DocumentosQueryContextFalso(),
            TiposDocumento = new TiposDocumentoQueryContextFalso(),
            Agenda = new ResolucionDestinatariosAgendaServiceFalso(),
            RegistroEnvio = new RegistroEnvioReclamacionServiceFalso(),
        };
        entorno.Empresas.ListaEmpresas.Add(contraparte);
        entorno.TiposDocumento.ListaTiposDocumento.Add(tipoDocumento);

        return new Escenario(entorno, contraparte, tipoDocumento);
    }

    private static Documento AgregarDocumento(Escenario escenario, DateOnly fechaVencimiento)
    {
        var documento = Documento.DeEmpresa(escenario.Contraparte.Id, escenario.TipoDocumento.Id, Hoy.AddYears(-1), fechaVencimiento);
        escenario.Entorno.Documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    private static DestinatarioAgendaDto Contacto(string nombre = "Ana Ruiz") =>
        new(Guid.NewGuid(), nombre, "ana@contratista.test", ["Póliza RC"]);

    [Fact]
    public async Task Envio_exitoso_devuelve_los_documentos_y_destinatarios_realmente_enviados()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddDays(10));
        var contacto = Contacto();
        escenario.Entorno.Agenda.RespuestaResolverParaEmpresaAsync = [contacto];
        var handler = escenario.Entorno.CrearHandler();

        var resultado = await handler.Handle(
            new EnviarReclamacionEmpresaCommand(escenario.Contraparte.Id, [documento.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.DocumentoIdsEnviados.Should().BeEquivalentTo([documento.Id]);
        resultado.Valor.Destinatarios.Should().BeEquivalentTo([contacto.Email]);
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(1);
        escenario.Entorno.RegistroEnvio.UltimoTitular!.Id.Should().Be(escenario.Contraparte.Id);
    }

    [Fact]
    public async Task Documento_que_deja_de_ser_reclamable_hace_fallar_el_envio_entero()
    {
        var escenario = ConstruirEscenario();
        var documentoValido = AgregarDocumento(escenario, Hoy.AddDays(10));
        var documentoYaRenovadoId = Guid.NewGuid();
        escenario.Entorno.Agenda.RespuestaResolverParaEmpresaAsync = [Contacto()];
        var handler = escenario.Entorno.CrearHandler();

        var resultado = await handler.Handle(
            new EnviarReclamacionEmpresaCommand(escenario.Contraparte.Id, [documentoValido.Id, documentoYaRenovadoId]),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.DocumentosDesactualizados");
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task Documento_fuera_de_la_ventana_de_tres_meses_no_es_reclamable()
    {
        var escenario = ConstruirEscenario();
        var documentoLejano = AgregarDocumento(escenario, Hoy.AddMonths(4));
        var handler = escenario.Entorno.CrearHandler();

        var resultado = await handler.Handle(
            new EnviarReclamacionEmpresaCommand(escenario.Contraparte.Id, [documentoLejano.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinDocumentosValidos");
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task Contacto_que_deja_de_resolver_en_la_agenda_hace_fallar_el_envio_entero()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddDays(10));
        var contactoVigente = Contacto();
        escenario.Entorno.Agenda.RespuestaResolverParaEmpresaAsync = [contactoVigente];
        var contactoYaBorradoId = Guid.NewGuid();
        var handler = escenario.Entorno.CrearHandler();

        var resultado = await handler.Handle(
            new EnviarReclamacionEmpresaCommand(
                escenario.Contraparte.Id, [documento.Id], ContactoIdsSeleccionados: [contactoVigente.ContactoId, contactoYaBorradoId]),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.ContactosDesactualizados");
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }

    /// <summary>
    /// Alcance de GESTIÓN de Empresas, no de lectura (CLAUDE.md § 14): una
    /// Empresa visible por tenant pero fuera de la cartera de gestión del
    /// usuario no puede recibir una reclamación en su nombre.
    /// </summary>
    [Fact]
    public async Task Empresa_fuera_de_la_cartera_de_gestion_del_usuario_falla_sin_acceso()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddDays(10));
        var handler = escenario.Entorno.CrearHandler(
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: []));

        var resultado = await handler.Handle(
            new EnviarReclamacionEmpresaCommand(escenario.Contraparte.Id, [documento.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinAcceso");
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }

    /// <summary>
    /// Un documento de ámbito Trabajador (o Cliente) que por lo que sea
    /// tuviera EmpresaId igual a esta Empresa contraparte no debe colarse
    /// como reclamable — el filtro por TipoDocumento.AmbitoAplicacion es lo
    /// que lo impide.
    /// </summary>
    [Fact]
    public async Task Documento_de_otro_ambito_no_es_reclamable_aunque_coincida_el_EmpresaId()
    {
        var escenario = ConstruirEscenario();
        var tipoDocumentoDeCliente = new TipoDocumento("RLC", null, false, 1, AmbitoAplicacion.Cliente);
        escenario.Entorno.TiposDocumento.ListaTiposDocumento.Add(tipoDocumentoDeCliente);
        var documentoDeOtroAmbito = Documento.DeEmpresa(escenario.Contraparte.Id, tipoDocumentoDeCliente.Id, Hoy.AddYears(-1), Hoy.AddDays(10));
        escenario.Entorno.Documentos.ListaDocumentos.Add(documentoDeOtroAmbito);
        var handler = escenario.Entorno.CrearHandler();

        var resultado = await handler.Handle(
            new EnviarReclamacionEmpresaCommand(escenario.Contraparte.Id, [documentoDeOtroAmbito.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinDocumentosValidos");
    }
}
