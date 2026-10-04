using CaeManager.Domain.Common;
using CaeManager.Application.Alertas;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Commands.PrepararVistaPreviaReclamacion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Reclamaciones;
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

        public EnviarReclamacionCommandHandler CrearHandler(AlcanceDatosServiceFalso? alcanceDatos = null)
        {
            alcanceDatos ??= new AlcanceDatosServiceFalso();
            return new(
                Empresas, Documentos, Trabajadores, TiposDocumento, Asignaciones, Centros,
                alcanceDatos, Agenda, RegistroEnvio, CrearPendientes(alcanceDatos));
        }

        public EnviarReclamacionEmpresaCommandHandler CrearHandlerEmpresa(AlcanceDatosServiceFalso? alcanceDatos = null)
        {
            alcanceDatos ??= new AlcanceDatosServiceFalso();
            return new(Empresas, Documentos, TiposDocumento, alcanceDatos, Agenda, RegistroEnvio, CrearPendientes(alcanceDatos));
        }

        /// <summary>El servicio REAL de lo pendiente sin fecha (con sus dos dependencias reales) sobre los dobles de contexto: lo que se prueba es su regla, no una respuesta enlatada.</summary>
        public PendientesDeReclamacionService CrearPendientes(AlcanceDatosServiceFalso? alcanceDatos = null)
        {
            alcanceDatos ??= new AlcanceDatosServiceFalso();
            return new(
                Documentos, TiposDocumento, Trabajadores, Asignaciones, Centros, Empresas,
                new DocumentosFaltantesService(TiposDocumento, Documentos, Centros),
                new EvaluacionDeAccesoPorCentroService(Centros, TiposDocumento, Trabajadores, Asignaciones, Documentos, alcanceDatos),
                alcanceDatos);
        }
    }

    private const string Dni = "77189989B";
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

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
        var documento = Documento.DeTrabajador(escenario.Trabajador.Id, escenario.TipoDocumento.Id, Hoy.AddYears(-1), VigenciaDocumento.VenceEl(fechaVencimiento));
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

    // ---- Vista previa de «Reclamar de nuevo»: mismo resultado que el envío ----

    private static PrepararVistaPreviaReclamacionCommandHandler CrearVistaPrevia(Escenario escenario, AlcanceDatosServiceFalso? alcance = null)
    {
        var alcanceDatos = alcance ?? new AlcanceDatosServiceFalso();
        var e = escenario.Entorno;
        return new PrepararVistaPreviaReclamacionCommandHandler(
            e.CrearHandler(alcanceDatos),
            e.CrearHandlerEmpresa(alcanceDatos));
    }

    [Fact]
    public async Task La_vista_previa_y_el_envio_resuelven_los_mismos_destinatarios_asunto_y_cuerpo()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddDays(10));
        var ana = Contacto("Ana Ruiz");
        var luis = new DestinatarioAgendaDto(Guid.NewGuid(), "Luis Gil", "luis@contratista.test", ["RLC"]);
        escenario.Entorno.Agenda.RespuestaResolverAsync = [ana, luis];

        var previa = await CrearVistaPrevia(escenario).Handle(
            new PrepararVistaPreviaReclamacionCommand(AmbitoAplicacion.Cliente, escenario.Cliente.Id, [documento.Id]), CancellationToken.None);
        previa.EsExitoso.Should().BeTrue();
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0, "la vista previa no envía nada");

        var envio = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [documento.Id]), CancellationToken.None);
        envio.EsExitoso.Should().BeTrue();

        var registro = escenario.Entorno.RegistroEnvio;
        previa.Valor.Correos.Should().Equal(registro.UltimosDestinatarios, "a quién se enseña es a quién se envía");
        previa.Valor.Destinatarios.Select(d => d.Email).Should().Equal(registro.UltimosDestinatarios);
        previa.Valor.Destinatarios.Select(d => d.Nombre).Should().Equal("Ana Ruiz", "Luis Gil");
        previa.Valor.DocumentoIds.Should().Equal(registro.UltimosDocumentoIds);
        previa.Valor.Asunto.Should().Be(registro.UltimoAsunto);
        previa.Valor.CuerpoHtml.Should().Be(registro.UltimoCuerpoHtml);
    }

    [Fact]
    public async Task La_vista_previa_falla_igual_que_el_envio_si_el_documento_ya_no_es_reclamable()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddMonths(6));
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];

        var previa = await CrearVistaPrevia(escenario).Handle(
            new PrepararVistaPreviaReclamacionCommand(AmbitoAplicacion.Cliente, escenario.Cliente.Id, [documento.Id]), CancellationToken.None);
        var envio = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [documento.Id]), CancellationToken.None);

        previa.EsFallido.Should().BeTrue();
        previa.Error.Codigo.Should().Be(envio.Error.Codigo);
    }

    /// <summary>
    /// La vista previa pasa por la misma puerta que el envío (rol con escritura,
    /// sesión privilegiada, puerta comercial): lo garantiza ser un ICommand.
    /// </summary>
    [Fact]
    public void La_vista_previa_es_un_comando_para_heredar_la_autorizacion_de_escritura_del_envio() =>
        typeof(ICommandBase).IsAssignableFrom(typeof(PrepararVistaPreviaReclamacionCommand)).Should().BeTrue();

    [Fact]
    public async Task La_vista_previa_de_titular_Empresa_resuelve_como_el_envio_de_Empresa()
    {
        var escenario = ConstruirEscenario();
        var contraparte = new Empresa("Contraparte SL", "B12345674");
        escenario.Entorno.Empresas.ListaEmpresas.Add(contraparte);
        var tipoEmpresa = new TipoDocumento("Seguro RC", null, false, 1, AmbitoAplicacion.Empresa);
        escenario.Entorno.TiposDocumento.ListaTiposDocumento.Add(tipoEmpresa);
        var documento = Documento.DeEmpresa(contraparte.Id, tipoEmpresa.Id, Hoy.AddYears(-1), VigenciaDocumento.VenceEl(Hoy.AddDays(10)));
        escenario.Entorno.Documentos.ListaDocumentos.Add(documento);
        escenario.Entorno.Agenda.RespuestaResolverParaEmpresaAsync = [Contacto("Marta Gil")];
        var alcance = new AlcanceDatosServiceFalso();

        var previa = await CrearVistaPrevia(escenario, alcance).Handle(
            new PrepararVistaPreviaReclamacionCommand(AmbitoAplicacion.Empresa, contraparte.Id, [documento.Id]), CancellationToken.None);
        var envio = await escenario.Entorno.CrearHandlerEmpresa(alcance)
            .Handle(new EnviarReclamacionEmpresaCommand(contraparte.Id, [documento.Id]), CancellationToken.None);

        previa.EsExitoso.Should().BeTrue();
        envio.EsExitoso.Should().BeTrue();
        var registro = escenario.Entorno.RegistroEnvio;
        previa.Valor.Correos.Should().Equal(registro.UltimosDestinatarios);
        previa.Valor.Asunto.Should().Be(registro.UltimoAsunto);
        previa.Valor.CuerpoHtml.Should().Be(registro.UltimoCuerpoHtml);
        registro.UltimoTitular!.Ambito.Should().Be(AmbitoAplicacion.Empresa);
    }

    // ---- Alcance de trabajadores y centros visibles (igual que ObtenerLoteReclamacionQuery) ----

    [Fact]
    public async Task Documento_de_un_trabajador_fuera_del_alcance_no_es_reclamable_y_no_revela_que_existe()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddDays(10));
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];
        var alcance = new AlcanceDatosServiceFalso(trabajadorIdsVisibles: [Guid.NewGuid()]);

        var resultado = await escenario.Entorno.CrearHandler(alcance).Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [documento.Id]), CancellationToken.None);
        var inexistente = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [Guid.NewGuid()]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinDocumentosValidos");
        resultado.Error.Codigo.Should().Be(inexistente.Error.Codigo, "no se distingue «no existe» de «fuera de tu alcance»");
        resultado.Error.Mensaje.Should().Be(inexistente.Error.Mensaje);
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task Documento_en_un_centro_fuera_del_alcance_no_es_reclamable()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddDays(10));
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [escenario.Cliente.Id], centroIdsVisibles: [Guid.NewGuid()]);

        var resultado = await escenario.Entorno.CrearHandler(alcance).Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [documento.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinDocumentosValidos");
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task Con_el_trabajador_y_el_centro_en_el_alcance_el_envio_legitimo_sigue_funcionando()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddDays(10));
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [escenario.Cliente.Id],
            trabajadorIdsVisibles: [escenario.Trabajador.Id], centroIdsVisibles: [escenario.Centro.Id]);

        var resultado = await escenario.Entorno.CrearHandler(alcance).Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [documento.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(1);
    }

    [Fact]
    public async Task La_vista_previa_tampoco_resuelve_un_documento_fuera_del_alcance_de_trabajadores()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddDays(10));
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];

        var previa = await CrearVistaPrevia(escenario, new AlcanceDatosServiceFalso(trabajadorIdsVisibles: [Guid.NewGuid()])).Handle(
            new PrepararVistaPreviaReclamacionCommand(AmbitoAplicacion.Cliente, escenario.Cliente.Id, [documento.Id]), CancellationToken.None);

        previa.EsFallido.Should().BeTrue();
        previa.Error.Codigo.Should().Be("Reclamacion.SinDocumentosValidos");
    }

    [Fact]
    public async Task La_vista_previa_respeta_la_cartera_del_usuario()
    {
        var escenario = ConstruirEscenario();
        var documento = AgregarDocumento(escenario, Hoy.AddDays(10));
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];

        var previa = await CrearVistaPrevia(escenario, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [])).Handle(
            new PrepararVistaPreviaReclamacionCommand(AmbitoAplicacion.Cliente, escenario.Cliente.Id, [documento.Id]), CancellationToken.None);

        previa.EsFallido.Should().BeTrue();
        previa.Error.Codigo.Should().Be("Reclamacion.SinAcceso");
    }

    // ---- «Pedir» lo que no vence: documento AUSENTE o «Sin confirmar» sin fecha (decisión de Chris, 2026-10-04) ----

    /// <summary>El correo codifica en HTML todo dato interpolado («é» sale como «&amp;#233;»): se busca lo que de verdad hay en el cuerpo.</summary>
    private static string Cod(string texto) => System.Net.WebUtility.HtmlEncode(texto);

    /// <summary>Un Tipo que el Centro exige (Requerido = Sí) y que el Trabajador del escenario no ha subido nunca.</summary>
    private static TipoDocumento AgregarTipoRequerido(Escenario escenario, string nombre = "Formación PRL")
    {
        var tipo = new TipoDocumento(nombre, null, false, 2, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        escenario.Entorno.TiposDocumento.ListaTiposDocumento.Add(tipo);
        return tipo;
    }

    private static Documento AgregarSinConfirmar(Escenario escenario, TipoDocumento tipo)
    {
        var documento = Documento.DeTrabajador(escenario.Trabajador.Id, tipo.Id, Hoy.AddYears(-1), VigenciaDocumento.SinConfirmar);
        escenario.Entorno.Documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    [Fact]
    public async Task Un_documento_ausente_se_pide_por_el_mismo_envio_con_su_tipo_y_su_trabajador_y_el_correo_dice_que_falta()
    {
        var escenario = ConstruirEscenario();
        var tipo = AgregarTipoRequerido(escenario);
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];

        var resultado = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [], Pendientes: [PendienteSinFecha.Ausente(escenario.Trabajador.Id, tipo.Id)]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.DocumentosQueFaltaban.Should().Be(1);
        var registro = escenario.Entorno.RegistroEnvio;
        registro.VecesLlamado.Should().Be(1, "es el mismo registro y la misma cola que la reclamación por vencimiento");
        registro.UltimoTitular!.Id.Should().Be(escenario.Cliente.Id, "el destinatario es el titular de siempre");
        registro.UltimosDocumentoIds.Should().BeEmpty("no hay Documento al que anclar la línea");
        registro.UltimosDocumentosQueFaltan.Should().Equal(new DocumentoQueFaltaPedido(tipo.Id, escenario.Trabajador.Id));
        registro.UltimoCuerpoHtml.Should().Contain(Cod("Formación PRL")).And.Contain(Cod("Juan Pérez"))
            .And.Contain("faltan. Por favor").And.Contain(">Falta<")
            .And.NotContain("Falta confirmar su vigencia").And.NotContain("próximos a vencer");
        escenario.Entorno.Agenda.UltimaLlamadaResolverAsync!.Value.TipoDocumentoIds.Should().Contain(tipo.Id,
            "la agenda decide a quién se le pide también lo que falta");
    }

    [Fact]
    public async Task Un_sin_confirmar_sin_fecha_se_pide_por_su_documento_y_el_correo_pide_confirmar_la_vigencia()
    {
        var escenario = ConstruirEscenario();
        var tipo = AgregarTipoRequerido(escenario, "Aptitud médica");
        var documento = AgregarSinConfirmar(escenario, tipo);
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];

        var resultado = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [], Pendientes: [PendienteSinFecha.SinConfirmar(documento.Id)]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.DocumentosQueFaltaban.Should().Be(0, "un «Sin confirmar» existe: no es un documento que falte");
        var registro = escenario.Entorno.RegistroEnvio;
        // El «Sin confirmar» es una línea con su Documento, como cualquier otra.
        registro.UltimosDocumentoIds.Should().Equal(documento.Id);
        registro.UltimosDocumentosQueFaltan.Should().BeNullOrEmpty();
        registro.UltimoCuerpoHtml.Should().Contain(Cod("Aptitud médica")).And.Contain("Falta confirmar su vigencia");
    }

    [Fact]
    public async Task Lo_que_vence_y_lo_que_falta_viajan_en_el_mismo_correo()
    {
        var escenario = ConstruirEscenario();
        var vence = AgregarDocumento(escenario, Hoy.AddDays(10));
        var tipo = AgregarTipoRequerido(escenario);
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];

        var resultado = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [vence.Id], Pendientes: [PendienteSinFecha.Ausente(escenario.Trabajador.Id, tipo.Id)]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(1, "un solo envío, no uno por cada forma");
        escenario.Entorno.RegistroEnvio.UltimosDocumentoIds.Should().Equal(vence.Id);
        escenario.Entorno.RegistroEnvio.UltimosDocumentosQueFaltan.Should().HaveCount(1);
        escenario.Entorno.RegistroEnvio.UltimoCuerpoHtml.Should().Contain("próximos a vencer").And.Contain(Cod("Formación PRL")).And.Contain(">Falta<");
    }

    [Fact]
    public void Sin_pendientes_el_cuerpo_del_correo_es_exactamente_el_de_siempre()
    {
        var documentos = new[] { ("Juan Pérez", "Ficha de riesgos", new DateOnly(2026, 12, 1)) };

        var sin = EnviarReclamacionCommandHandler.ConstruirCuerpoHtml("Cliente SA", documentos);

        EnviarReclamacionCommandHandler.ConstruirCuerpoHtml("Cliente SA", documentos, []).Should().Be(sin);
        sin.Should().NotContain("Situación").And.NotContain("faltan");
    }

    [Fact]
    public async Task Un_ausente_que_ya_se_subio_hace_fallar_el_envio_entero()
    {
        var escenario = ConstruirEscenario();
        var tipo = AgregarTipoRequerido(escenario);
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];
        var pedido = PendienteSinFecha.Ausente(escenario.Trabajador.Id, tipo.Id);

        // Entre que se abrió el cajón y se pulsó Enviar, el Trabajador subió el documento.
        escenario.Entorno.Documentos.ListaDocumentos.Add(
            Documento.DeTrabajador(escenario.Trabajador.Id, tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1))));

        var resultado = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [], Pendientes: [pedido]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.PendientesDesactualizados");
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task Un_sin_confirmar_cuya_vigencia_ya_se_anoto_hace_fallar_el_envio_entero()
    {
        var escenario = ConstruirEscenario();
        var tipo = AgregarTipoRequerido(escenario);
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];
        var confirmado = Documento.DeTrabajador(escenario.Trabajador.Id, tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1)));
        escenario.Entorno.Documentos.ListaDocumentos.Add(confirmado);

        var resultado = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [], Pendientes: [PendienteSinFecha.SinConfirmar(confirmado.Id)]),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.PendientesDesactualizados");
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task Un_pedido_valido_no_salva_a_otro_que_ya_no_lo_es_todo_o_nada()
    {
        var escenario = ConstruirEscenario();
        var tipo = AgregarTipoRequerido(escenario);
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];

        var resultado = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [], Pendientes:
            [
                PendienteSinFecha.Ausente(escenario.Trabajador.Id, tipo.Id),
                PendienteSinFecha.Ausente(escenario.Trabajador.Id, Guid.NewGuid()),
            ]),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.PendientesDesactualizados");
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task Un_tipo_que_el_centro_no_exige_no_se_puede_pedir_como_ausente()
    {
        var escenario = ConstruirEscenario();
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];

        // «Ficha de riesgos» no es Requerido: que falte no es un hueco, así que no se pide aunque se conozca su Id.
        var resultado = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [], Pendientes: [PendienteSinFecha.Ausente(escenario.Trabajador.Id, escenario.TipoDocumento.Id)]),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.PendientesDesactualizados");
    }

    [Theory]
    [InlineData("ausente sin trabajador")]
    [InlineData("ausente sin tipo")]
    [InlineData("sin confirmar con tipo")]
    [InlineData("sin confirmar con trabajador")]
    [InlineData("documento vacio")]
    public async Task Un_pedido_con_una_forma_mixta_o_incompleta_no_se_interpreta_y_falla(string caso)
    {
        var escenario = ConstruirEscenario();
        var tipo = AgregarTipoRequerido(escenario);
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];
        var documento = AgregarSinConfirmar(escenario, AgregarTipoRequerido(escenario, "Aptitud médica"));
        PendienteSinFecha pedido = caso switch
        {
            "ausente sin trabajador" => new(null, null, tipo.Id),
            "ausente sin tipo" => new(null, escenario.Trabajador.Id, null),
            "sin confirmar con tipo" => new(documento.Id, null, tipo.Id),
            "sin confirmar con trabajador" => new(documento.Id, escenario.Trabajador.Id, null),
            _ => new(Guid.Empty, null, null),
        };

        var resultado = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [], Pendientes: [pedido]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.PendientesDesactualizados");
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task Pedir_a_un_trabajador_fuera_del_alcance_falla_igual_que_con_un_pedido_inexistente()
    {
        var escenario = ConstruirEscenario();
        var tipo = AgregarTipoRequerido(escenario);
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];
        var alcance = new AlcanceDatosServiceFalso(trabajadorIdsVisibles: [Guid.NewGuid()]);

        var fuera = await escenario.Entorno.CrearHandler(alcance).Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [], Pendientes: [PendienteSinFecha.Ausente(escenario.Trabajador.Id, tipo.Id)]),
            CancellationToken.None);
        var inexistente = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [], Pendientes: [PendienteSinFecha.Ausente(Guid.NewGuid(), tipo.Id)]),
            CancellationToken.None);

        fuera.EsFallido.Should().BeTrue();
        fuera.Error.Codigo.Should().Be(inexistente.Error.Codigo, "no se distingue «no existe» de «fuera de tu alcance»");
        fuera.Error.Mensaje.Should().Be(inexistente.Error.Mensaje);
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0);
    }

    [Fact]
    public async Task Pedir_con_un_cliente_fuera_de_la_cartera_falla_sin_acceso_como_la_reclamacion_por_vencimiento()
    {
        var escenario = ConstruirEscenario();
        var tipo = AgregarTipoRequerido(escenario);
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];

        var resultado = await escenario.Entorno.CrearHandler(new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [])).Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [], Pendientes: [PendienteSinFecha.Ausente(escenario.Trabajador.Id, tipo.Id)]),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinAcceso");
    }

    [Fact]
    public async Task Sin_documentos_ni_pendientes_sigue_sin_haber_nada_que_enviar()
    {
        var escenario = ConstruirEscenario();

        var resultado = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [], Pendientes: []), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Reclamacion.SinDocumentos");
    }

    [Fact]
    public async Task La_vista_previa_de_lo_que_falta_es_lo_que_sale_en_el_envio()
    {
        var escenario = ConstruirEscenario();
        var tipo = AgregarTipoRequerido(escenario);
        var vence = AgregarDocumento(escenario, Hoy.AddDays(10));
        var sinConfirmar = AgregarSinConfirmar(escenario, AgregarTipoRequerido(escenario, "Aptitud médica"));
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto("Ana Ruiz")];
        PendienteSinFecha[] pedidos = [PendienteSinFecha.Ausente(escenario.Trabajador.Id, tipo.Id), PendienteSinFecha.SinConfirmar(sinConfirmar.Id)];

        var previa = await CrearVistaPrevia(escenario).Handle(
            new PrepararVistaPreviaReclamacionCommand(AmbitoAplicacion.Cliente, escenario.Cliente.Id, [vence.Id], pedidos), CancellationToken.None);
        previa.EsExitoso.Should().BeTrue();
        escenario.Entorno.RegistroEnvio.VecesLlamado.Should().Be(0, "la vista previa no envía nada");

        var envio = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [vence.Id], Pendientes: pedidos), CancellationToken.None);
        envio.EsExitoso.Should().BeTrue();

        var registro = escenario.Entorno.RegistroEnvio;
        previa.Valor.CuerpoHtml.Should().Be(registro.UltimoCuerpoHtml);
        previa.Valor.Asunto.Should().Be(registro.UltimoAsunto);
        previa.Valor.Correos.Should().Equal(registro.UltimosDestinatarios);
        previa.Valor.DocumentoIds.Should().BeEquivalentTo(registro.UltimosDocumentoIds);
        previa.Valor.DocumentosQueFaltan.Should().Equal(registro.UltimosDocumentosQueFaltan);
    }

    [Fact]
    public async Task La_vista_previa_falla_igual_que_el_envio_si_lo_que_falta_ya_no_falta()
    {
        var escenario = ConstruirEscenario();
        var tipo = AgregarTipoRequerido(escenario);
        escenario.Entorno.Agenda.RespuestaResolverAsync = [Contacto()];
        escenario.Entorno.Documentos.ListaDocumentos.Add(
            Documento.DeTrabajador(escenario.Trabajador.Id, tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1))));
        PendienteSinFecha[] pedidos = [PendienteSinFecha.Ausente(escenario.Trabajador.Id, tipo.Id)];

        var previa = await CrearVistaPrevia(escenario).Handle(
            new PrepararVistaPreviaReclamacionCommand(AmbitoAplicacion.Cliente, escenario.Cliente.Id, [], pedidos), CancellationToken.None);
        var envio = await escenario.Entorno.CrearHandler().Handle(
            new EnviarReclamacionCommand(escenario.Cliente.Id, [], Pendientes: pedidos), CancellationToken.None);

        previa.EsFallido.Should().BeTrue();
        previa.Error.Codigo.Should().Be(envio.Error.Codigo);
    }
}
