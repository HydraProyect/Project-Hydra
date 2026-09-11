using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// Handler sin ningún test hasta esta revisión (2026-09-11): manda correo
/// real a terceros y hasta ahora, si tras revalidar server-side sobrevivía
/// al menos un documento y un contacto de lo pedido, enviaba solo eso y
/// devolvía éxito sin decirlo — un tercero podía recibir menos de lo que el
/// diálogo de confirmación anunció, sin que nadie se enterara. Estos tests
/// cubren la regla "todo o nada" que lo sustituye: si algo de lo pedido ya
/// no es reclamable (se renovó, salió de la ventana de 3 meses) o algún
/// contacto ya no resuelve en la agenda, el envío entero falla y
/// <see cref="RegistroEnvioReclamacionServiceFalso"/> nunca se invoca.
/// </summary>
public class EnviarReclamacionCommandHandlerTests
{
    private sealed class Entorno
    {
        public required EmpresasQueryContextFalso Empresas { get; init; }
        public required DocumentosQueryContextFalso Documentos { get; init; }
        public required TrabajadoresQueryContextFalso Trabajadores { get; init; }
        public required TiposDocumentoQueryContextFalso TiposDocumento { get; init; }
        public required AsignacionesQueryContextFalso Asignaciones { get; init; }
        public required CentrosQueryContextFalso Centros { get; init; }
        public required ResolucionDestinatariosAgendaServiceFalso Agenda { get; init; }
        public required RegistroEnvioReclamacionServiceFalso RegistroEnvio { get; init; }

        public EnviarReclamacionCommandHandler CrearHandler(AlcanceDatosServiceFalso? alcanceDatos = null) => new(
            Empresas, Documentos, Trabajadores, TiposDocumento, Asignaciones, Centros,
            alcanceDatos ?? new AlcanceDatosServiceFalso(), Agenda, RegistroEnvio);
    }

    private const string Dni = "77189989B";
    private static readonly DateOnly Hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>Un Cliente con un Centro y un Trabajador asignado, listos para colgar Documentos.</summary>
    private sealed record Escenario(Entorno Entorno, Empresa Cliente, Centro Centro, Trabajador Trabajador, TipoDocumento TipoDocumento);

    private static Escenario ConstruirEscenario()
    {
        var cliente = Empresa.CrearComoCliente("Cliente SA", "B12345674", false, null, null);
        var contratista = new Empresa("Contratista SL", "B12345674");
        var centro = new Centro(cliente.Id, contratista.Id, "Centro Norte");
        var trabajador = Trabajador.DeEmpresa(contratista.Id, "Juan", "Pérez", Dni);
        var tipoDocumento = new TipoDocumento("Ficha de riesgos", null, false, 1, AmbitoAplicacion.Trabajador);

        var entorno = new Entorno
        {
            Empresas = new EmpresasQueryContextFalso(),
            Documentos = new DocumentosQueryContextFalso(),
            Trabajadores = new TrabajadoresQueryContextFalso(),
            TiposDocumento = new TiposDocumentoQueryContextFalso(),
            Asignaciones = new AsignacionesQueryContextFalso(),
            Centros = new CentrosQueryContextFalso(),
            Agenda = new ResolucionDestinatariosAgendaServiceFalso(),
            RegistroEnvio = new RegistroEnvioReclamacionServiceFalso(),
        };
        entorno.Empresas.ListaEmpresas.Add(cliente);
        entorno.Empresas.ListaEmpresas.Add(contratista);
        entorno.Centros.ListaCentros.Add(centro);
        entorno.Trabajadores.ListaTrabajadores.Add(trabajador);
        entorno.TiposDocumento.ListaTiposDocumento.Add(tipoDocumento);
        entorno.Asignaciones.ListaAsignaciones.Add(new Asignacion(trabajador.Id, centro.Id, new DateOnly(2026, 1, 1)));

        return new Escenario(entorno, cliente, centro, trabajador, tipoDocumento);
    }

    private static Documento AgregarDocumento(Escenario escenario, DateOnly fechaVencimiento)
    {
        var documento = Documento.DeTrabajador(escenario.Trabajador.Id, escenario.TipoDocumento.Id, Hoy.AddYears(-1), fechaVencimiento);
        escenario.Entorno.Documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    private static DestinatarioAgendaDto Contacto(string nombre = "Ana Ruiz") =>
        new(Guid.NewGuid(), nombre, "ana@contratista.test", ["RLC"]);

    [Fact]
    public async Task Envio_exitoso_devuelve_los_documentos_y_destinatarios_realmente_enviados()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddDays(10));
        var contacto = Contacto();
        escenario.Entorno.Agenda.RespuestaResolverAsync = [contacto];
        var handler = escenario.Entorno.CrearHandler();

        var resultado = await handler.Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [documento.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.DocumentoIdsEnviados.Should().BeEquivalentTo([documento.Id]);
        resultado.Valor.Destinatarios.Should().BeEquivalentTo([contacto.Email]);

        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(1);
        escenario.Entorno.RegistroEnvio.UltimoTitular!.Id.Should().Be(escenario.Cliente.Id);
        escenario.Entorno.RegistroEnvio.UltimosDocumentoIds.Should().BeEquivalentTo([documento.Id]);
        escenario.Entorno.RegistroEnvio.UltimosDestinatarios.Should().BeEquivalentTo([contacto.Email]);
    }

    /// <summary>
    /// El hallazgo del 2026-09-11: entre que se abrió la vista previa y se
    /// pulsó Enviar, uno de los documentos pedidos se renovó (su fila ya no
    /// aparece en la revalidación). Antes de este fix, el otro documento se
    /// enviaba igualmente y el Result decía éxito sin más. Ahora el envío
    /// entero falla y la cola común nunca se invoca.
    /// </summary>
    [Fact]
    public async Task Documento_que_deja_de_ser_reclamable_hace_fallar_el_envio_entero()
    {
        var escenario = ConstruirEscenario();
        var documentoValido = AgregarDocumento(escenario, Hoy.AddDays(10));
        var documentoYaRenovadoId = Guid.NewGuid();
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];
        var handler = escenario.Entorno.CrearHandler();

        var resultado = await handler.Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [documentoValido.Id, documentoYaRenovadoId]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.DocumentosDesactualizados");
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0, "no puede mandarse una parte de lo pedido sin avisar");
    }

    /// <summary>
    /// Mismo criterio de "reclamable" que ObtenerLoteReclamacionQuery: un
    /// vencimiento a más de 3 meses vista no es reclamable, ni siquiera si el
    /// documento existe y pertenece al trabajador/cliente correctos.
    /// </summary>
    [Fact]
    public async Task Documento_fuera_de_la_ventana_de_tres_meses_no_es_reclamable()
    {
        var escenario = ConstruirEscenario();
        var documentoLejano = AgregarDocumento(escenario, Hoy.AddMonths(4));
        var handler = escenario.Entorno.CrearHandler();

        var resultado = await handler.Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [documentoLejano.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinDocumentosValidos");
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }

    /// <summary>Justo en el límite (3 meses exactos) sigue siendo reclamable — el corte es "<=", no "<".</summary>
    [Fact]
    public async Task Documento_justo_en_el_limite_de_tres_meses_sigue_siendo_reclamable()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddMonths(3));
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];
        var handler = escenario.Entorno.CrearHandler();

        var resultado = await handler.Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [documento.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    /// <summary>
    /// El otro lado del hallazgo: un contacto marcado en la vista previa que
    /// ya no resuelve la agenda (se borró, cambió de tipo asignado) también
    /// hace fallar el envío entero, aunque los documentos sigan siendo
    /// válidos.
    /// </summary>
    [Fact]
    public async Task Contacto_que_deja_de_resolver_en_la_agenda_hace_fallar_el_envio_entero()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddDays(10));
        var contactoVigente = Contacto();
        escenario.Entorno.Agenda.RespuestaResolverAsync = [contactoVigente];
        var contactoYaBorradoId = Guid.NewGuid();
        var handler = escenario.Entorno.CrearHandler();

        var resultado = await handler.Handle(
            new EnviarReclamacionCommand(
                escenario.Cliente.Id, [documento.Id], ContactoIdsSeleccionados: [contactoVigente.ContactoId, contactoYaBorradoId]),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.ContactosDesactualizados");
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task Contacto_seleccionado_que_sigue_vigente_envia_solo_a_ese_contacto()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddDays(10));
        var contactoElegido = Contacto("Ana Ruiz");
        var otroContacto = Contacto("Luis Gómez");
        escenario.Entorno.Agenda.RespuestaResolverAsync = [contactoElegido, otroContacto];
        var handler = escenario.Entorno.CrearHandler();

        var resultado = await handler.Handle(
            new EnviarReclamacionCommand(
                escenario.Cliente.Id, [documento.Id], ContactoIdsSeleccionados: [contactoElegido.ContactoId]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Destinatarios.Should().BeEquivalentTo([contactoElegido.Email]);
    }

    [Fact]
    public async Task Cliente_fuera_de_la_cartera_del_usuario_falla_sin_acceso()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddDays(10));
        var handler = escenario.Entorno.CrearHandler(
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: []));

        var resultado = await handler.Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [documento.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinAcceso");
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_ningun_contacto_resuelto_en_la_agenda_falla_sin_destinatario()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddDays(10));
        escenario.Entorno.Agenda.RespuestaResolverAsync = [];
        var handler = escenario.Entorno.CrearHandler();

        var resultado = await handler.Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [documento.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinDestinatario");
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }
}
