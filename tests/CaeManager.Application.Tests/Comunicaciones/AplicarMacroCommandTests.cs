using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Commands.AplicarMacro;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reclamaciones;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Visitas;
using CaeManager.Application.Reclamaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Visitas;
using FluentAssertions;

namespace CaeManager.Application.Tests.Comunicaciones;

public class AplicarMacroCommandTests
{
    private readonly ComunicacionesQueryContextFalso _comunicaciones = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly EventoConversacionRepositorioFalso _eventos = new();
    private readonly UnitOfWorkFalso _unidad = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly VisitasVacias _visitas = new();
    private readonly PendientesFalsos _pendientes = new();

    private AplicarMacroCommandHandler Handler(AlcanceDatosServiceFalso? alcance = null)
    {
        alcance ??= new AlcanceDatosServiceFalso();
        return new(
            _comunicaciones, new RellenoMacroRespuesta(_comunicaciones, _empresas, _centros, _trabajadores, _visitas, _pendientes, alcance),
            alcance, _eventos, _unidad);
    }

    [Fact]
    public async Task Sin_pendientes_en_la_regla_documental_la_variable_queda_por_rellenar()
    {
        // El comando no calcula vencidos por su cuenta: un documento sustituido no se pide otra vez.
        var empresa = new Empresa("Empresa Norte");
        _empresas.ListaEmpresas.Add(empresa);
        var conversacion = new Conversacion("Documentación", empresaId: empresa.Id);
        var macro = new MacroRespuesta("Vencidos", "{{documentos pendientes}}");
        _comunicaciones.ListaConversaciones.Add(conversacion);
        _comunicaciones.ListaMacrosRespuesta.Add(macro);

        var resultado = await Handler().Handle(new(conversacion.Id, macro.Id), CancellationToken.None);
        resultado.Valor.Should().Be("{{documentos pendientes}}");
    }

    [Fact]
    public async Task Con_un_Centro_y_sin_Trabajador_no_pide_los_pendientes_de_los_Trabajadores_del_Centro()
    {
        var destinataria = new Empresa("Empresa Sur");
        _empresas.ListaEmpresas.Add(destinataria);
        var centro = new CaeManager.Domain.Centros.Centro(destinataria.Id, Guid.NewGuid(), "Centro");
        _centros.ListaCentros.Add(centro);
        var conversacion = new Conversacion("Visita", clienteId: destinataria.Id);
        var macro = new MacroRespuesta("Pendientes", "{{documentos pendientes}}");
        _comunicaciones.ListaConversaciones.Add(conversacion);
        _comunicaciones.ListaMacrosRespuesta.Add(macro);
        _comunicaciones.ListaParticipantesConversacion.Add(
            new(conversacion.Id, "centro@example.com", RolParticipante.Para, TipoParticipanteOrigen.Centro, centro.Id));

        await Handler().Handle(new(conversacion.Id, macro.Id), CancellationToken.None);
        _pendientes.ConsultasPorCliente.Should().Be(0);
    }

    [Fact]
    public async Task Un_Trabajador_o_Centro_fuera_de_la_Asignacion_de_Cartera_no_se_rellena()
    {
        var empresa = new Empresa("Empresa Norte");
        var destinataria = new Empresa("Empresa Sur");
        _empresas.ListaEmpresas.AddRange([empresa, destinataria]);
        var centro = new CaeManager.Domain.Centros.Centro(destinataria.Id, empresa.Id, "Centro oculto");
        _centros.ListaCentros.Add(centro);
        var trabajador = CaeManager.Domain.Trabajadores.Trabajador.DeEmpresa(empresa.Id, "Ana", "Pérez", "12345678Z");
        _trabajadores.ListaTrabajadores.Add(trabajador);
        var conversacion = new Conversacion("Visita", clienteId: destinataria.Id);
        var macro = new MacroRespuesta("Visita", "{{Trabajador}}|{{Centro}}");
        _comunicaciones.ListaConversaciones.Add(conversacion);
        _comunicaciones.ListaMacrosRespuesta.Add(macro);
        _comunicaciones.ListaParticipantesConversacion.AddRange([
            new(conversacion.Id, "ana@example.com", RolParticipante.De, TipoParticipanteOrigen.Trabajador, trabajador.Id),
            new(conversacion.Id, "centro@example.com", RolParticipante.Para, TipoParticipanteOrigen.Centro, centro.Id)]);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [destinataria.Id],
            trabajadorIdsVisibles: [], centroIdsVisibles: [], empresaIdsVisibles: [], empresaIdsParaGestion: [], centroIdsParaGestion: []);

        var resultado = await Handler(alcance).Handle(new(conversacion.Id, macro.Id), CancellationToken.None);
        resultado.Valor.Should().Be("{{Trabajador}}|{{Centro}}");
    }

    [Fact]
    public async Task Los_documentos_pendientes_reutilizan_el_calculo_existente_para_la_Empresa_del_hilo()
    {
        var empresa = new Empresa("Empresa Norte");
        _empresas.ListaEmpresas.Add(empresa);
        var conversacion = new Conversacion("Documentación", empresaId: empresa.Id);
        var macro = new MacroRespuesta("Pendientes", "Solicitamos {{documentos pendientes}}");
        _comunicaciones.ListaConversaciones.Add(conversacion);
        _comunicaciones.ListaMacrosRespuesta.Add(macro);
        _pendientes.PorEmpresa = [new(empresa.Id, empresa.RazonSocial,
            [new(null, null, null, Guid.NewGuid(), "Certificado A1", MotivoPendienteDeReclamacion.Ausente)])];

        var resultado = await Handler().Handle(new(conversacion.Id, macro.Id), CancellationToken.None);

        resultado.Valor.Should().Be("Solicitamos Certificado A1");
        _pendientes.EmpresaConsultada.Should().Be(empresa.Id);
    }

    /// <summary>
    /// Seguimiento de la PR #1528, con el servicio real de lo pendiente: un documento de la Empresa que el Centro exige sin marcarlo
    /// «bloquea el acceso» y que falta entra en «documentos pendientes». Antes la macro lo callaba.
    /// </summary>
    [Fact]
    public async Task El_documento_de_la_Empresa_que_el_Centro_exige_sin_bloquear_y_falta_entra_en_los_documentos_pendientes()
    {
        var cliente = new Empresa("Cliente Sur");
        var empresa = new Empresa("Empresa Norte");
        _empresas.ListaEmpresas.AddRange([cliente, empresa]);
        var centro = new CaeManager.Domain.Centros.Centro(cliente.Id, empresa.Id, "Centro de trabajo");
        _centros.ListaCentros.Add(centro);
        var trabajador = CaeManager.Domain.Trabajadores.Trabajador.DeEmpresa(empresa.Id, "Ana", "Pérez", "12345678Z");
        _trabajadores.ListaTrabajadores.Add(trabajador);
        var asignaciones = new AsignacionesQueryContextFalso();
        asignaciones.ListaAsignaciones.Add(new CaeManager.Domain.Asignaciones.Asignacion(trabajador.Id, centro.Id, new DateOnly(2026, 1, 1)));
        var tipos = new TiposDocumentoQueryContextFalso();
        var plan = new CaeManager.Domain.Documentos.TipoDocumento("Plan de prevención", null, false, 1, CaeManager.Domain.Documentos.AmbitoAplicacion.Empresa);
        tipos.ListaTiposDocumento.Add(plan);
        tipos.ListaTiposDocumentoCentros.Add(new CaeManager.Domain.Documentos.TipoDocumentoCentro(plan.Id, centro.Id, incluido: true, bloqueaAcceso: false));
        var conversacion = new Conversacion("Documentación", empresaId: empresa.Id);
        var macro = new MacroRespuesta("Pendientes", "Solicitamos {{documentos pendientes}}");
        _comunicaciones.ListaConversaciones.Add(conversacion);
        _comunicaciones.ListaMacrosRespuesta.Add(macro);
        var alcance = new AlcanceDatosServiceFalso();
        var pendientes = PendientesDeReclamacionPruebas.Crear(
            new DocumentosQueryContextFalso(), tipos, _trabajadores, asignaciones, _centros, _empresas, alcance);
        var handler = new AplicarMacroCommandHandler(
            _comunicaciones, new RellenoMacroRespuesta(_comunicaciones, _empresas, _centros, _trabajadores, _visitas, pendientes, alcance),
            alcance, _eventos, _unidad);

        var resultado = await handler.Handle(new(conversacion.Id, macro.Id), CancellationToken.None);

        resultado.Valor.Should().Be("Solicitamos Plan de prevención");
    }

    [Fact]
    public async Task Rellena_la_Empresa_el_Trabajador_el_Centro_y_la_fecha_solo_desde_el_hilo()
    {
        var empresa = new Empresa("Empresa Norte");
        var empresaDestinataria = new Empresa("Empresa Sur");
        _empresas.ListaEmpresas.AddRange([empresa, empresaDestinataria]);
        var centro = new CaeManager.Domain.Centros.Centro(empresaDestinataria.Id, empresa.Id, "Centro de trabajo");
        _centros.ListaCentros.Add(centro);
        var trabajador = CaeManager.Domain.Trabajadores.Trabajador.DeEmpresa(empresa.Id, "Ana", "Pérez", "12345678Z");
        _trabajadores.ListaTrabajadores.Add(trabajador);
        var conversacion = new Conversacion("Visita", clienteId: empresaDestinataria.Id);
        var macro = new MacroRespuesta("Visita", "{{Empresa}}|{{Trabajador}}|{{Centro}}|{{fecha de la visita}}");
        _comunicaciones.ListaConversaciones.Add(conversacion);
        _comunicaciones.ListaMacrosRespuesta.Add(macro);
        _comunicaciones.ListaParticipantesConversacion.AddRange([
            new(conversacion.Id, "empresa@example.com", RolParticipante.Para, TipoParticipanteOrigen.Empresa, empresa.Id),
            new(conversacion.Id, "ana@example.com", RolParticipante.De, TipoParticipanteOrigen.Trabajador, trabajador.Id)]);
        var visita = new Visita(centro.Id, new(2026, 10, 20), new(2026, 10, 20), null);
        _visitas.Lista.Add(visita);
        _comunicaciones.ListaEventosConversacion.Add(new(conversacion.Id, TipoEventoConversacion.VisitaCreada, visita.Id, DateTime.UtcNow));

        var resultado = await Handler().Handle(new(conversacion.Id, macro.Id), CancellationToken.None);
        resultado.Valor.Should().Be("Empresa Norte|Ana Pérez|Centro de trabajo|20/10/2026");

        var otraVisita = new Visita(centro.Id, new(2026, 10, 21), new(2026, 10, 21), null);
        _visitas.Lista.Add(otraVisita);
        _comunicaciones.ListaEventosConversacion.Add(new(conversacion.Id, TipoEventoConversacion.VisitaCreada, otraVisita.Id, DateTime.UtcNow));
        var ambiguo = await Handler().Handle(new(conversacion.Id, macro.Id), CancellationToken.None);
        ambiguo.Valor.Should().EndWith("{{fecha de la visita}}");
    }

    [Fact]
    public async Task Aplicar_rellena_el_Cliente_empresarial_y_registra_la_plantilla_sin_enviar_mensajes()
    {
        var empresa = new Empresa("Empresa & Norte");
        _empresas.ListaEmpresas.Add(empresa);
        var conversacion = new Conversacion("Consulta", clienteId: empresa.Id);
        var macro = new MacroRespuesta("Documentación", "Hola {{Cliente empresarial}}, falta {{documentos pendientes}}", empresa.Id);
        _comunicaciones.ListaConversaciones.Add(conversacion);
        _comunicaciones.ListaMacrosRespuesta.Add(macro);

        var resultado = await Handler().Handle(new(conversacion.Id, macro.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be("Hola Empresa &amp; Norte, falta {{documentos pendientes}}");
        var evento = _eventos.Eventos.Should().ContainSingle().Subject;
        evento.Tipo.Should().Be(TipoEventoConversacion.PlantillaAplicada);
        evento.ReferenciaId.Should().Be(macro.Id);
        evento.ConversacionId.Should().Be(conversacion.Id);
        _unidad.VecesGuardado.Should().Be(1);
        conversacion.Mensajes.Should().BeEmpty();
        macro.CuerpoHtml.Should().Contain("{{Cliente empresarial}}");
    }

    [Fact]
    public async Task Fuera_de_la_Asignacion_de_Cartera_no_aplica_ni_registra()
    {
        var conversacion = new Conversacion("Consulta", clienteId: Guid.NewGuid());
        var macro = new MacroRespuesta("Genérica", "Contenido");
        _comunicaciones.ListaConversaciones.Add(conversacion);
        _comunicaciones.ListaMacrosRespuesta.Add(macro);
        var resultado = await Handler(new AlcanceDatosServiceFalso(tieneAccesoTotal: false))
            .Handle(new(conversacion.Id, macro.Id), CancellationToken.None);
        resultado.Error.Codigo.Should().Be("Conversacion.NoEncontrada");
        _eventos.Eventos.Should().BeEmpty();
        _unidad.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Una_plantilla_ajena_o_inexistente_no_se_aplica(bool existe)
    {
        var conversacion = new Conversacion("Consulta", clienteId: Guid.NewGuid());
        var macro = new MacroRespuesta("Específica", "Contenido", Guid.NewGuid());
        _comunicaciones.ListaConversaciones.Add(conversacion);
        if (existe) _comunicaciones.ListaMacrosRespuesta.Add(macro);
        var resultado = await Handler().Handle(new(conversacion.Id, macro.Id), CancellationToken.None);
        resultado.Error.Codigo.Should().Be("Macro.NoEncontrada");
        _eventos.Eventos.Should().BeEmpty();
        _unidad.VecesGuardado.Should().Be(0);
    }

    private sealed class VisitasVacias : IVisitasQueryContext
    {
        public List<Visita> Lista { get; } = [];
        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(Lista.AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => throw new NotSupportedException();
    }

    private sealed class PendientesFalsos : IPendientesDeReclamacionService
    {
        public IReadOnlyList<PendientesDeUnTitular> PorEmpresa { get; set; } = [];
        public Guid? EmpresaConsultada { get; private set; }
        public Task<IReadOnlyList<PendientesDeUnTitular>> ListarParaEmpresasAsync(Guid? empresaId, IReadOnlyList<Guid>? tipos, CancellationToken ct)
        {
            EmpresaConsultada = empresaId;
            return Task.FromResult(PorEmpresa);
        }
        public int ConsultasPorCliente { get; private set; }
        public Task<IReadOnlyList<PendientesDeUnTitular>> ListarParaClientesAsync(Guid? centroId, Guid? empresaDestinatariaId,
            IReadOnlyCollection<Guid>? trabajadorIds, IReadOnlyList<Guid>? tipos, CancellationToken ct)
        {
            ConsultasPorCliente++;
            return Task.FromResult<IReadOnlyList<PendientesDeUnTitular>>([]);
        }
        public Task<Result<IReadOnlyList<DocumentoPendienteDto>>> ResolverParaClienteAsync(Guid id, IReadOnlyList<PendienteSinFecha> pedidos, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<DocumentoPendienteDto>>> ResolverParaEmpresaAsync(Guid id, IReadOnlyList<PendienteSinFecha> pedidos, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<Guid>> DocumentoIdsAunSinConfirmarSinFechaAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => throw new NotSupportedException();
    }
}
