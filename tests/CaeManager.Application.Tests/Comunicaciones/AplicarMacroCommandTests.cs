using CaeManager.Application.Comunicaciones.Commands.AplicarMacro;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Visitas;
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

    private AplicarMacroCommandHandler Handler(AlcanceDatosServiceFalso? alcance = null) => new(
        _comunicaciones, _empresas, _centros, _trabajadores,
        _visitas, new DocumentosQueryContextFalso(), new TiposDocumentoQueryContextFalso(),
        alcance ?? new AlcanceDatosServiceFalso(), _eventos, _unidad);

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
        resultado.Valor.Should().Be("Empresa Norte|Ana P&#233;rez|Centro de trabajo|20/10/2026");

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
}
