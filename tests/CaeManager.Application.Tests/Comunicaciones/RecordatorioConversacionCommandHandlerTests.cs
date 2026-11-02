using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.AprobarRecordatorioConversacion;
using CaeManager.Application.Comunicaciones.Commands.DescartarRecordatorioConversacion;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;
using CaeManager.Application.Comunicaciones.Commands.RestaurarRecordatorioConversacion;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRecordatorioPropuestoConversacion;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Aprobar y descartar el recordatorio propuesto a los dos días laborables de espera (decisión del 2026-10-17). El envío
/// usa el handler real de la respuesta con el remitente simulado: ni Graph ni SMTP.
/// </summary>
public class RecordatorioConversacionCommandHandlerTests
{
    private readonly Guid _clienteId = Guid.NewGuid();
    private readonly ConversacionRepositorioFalso _conversaciones = new();
    private readonly PropuestasFalsas _propuestas = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly DescarteCambiosPendientesFalso _descarte = new();

    private (Conversacion Conversacion, PropuestaRecordatorioConversacion Propuesta) Sembrar()
    {
        var conversacion = new Conversacion("Documentación pendiente", clienteId: _clienteId);
        conversacion.CambiarEstado(EstadoConversacion.EsperandoRespuestaExterna, DateTime.UtcNow.AddDays(-10)).EsExitoso.Should().BeTrue();
        _conversaciones.Agregar(conversacion);
        var propuesta = PropuestaRecordatorioConversacion.ProponerSiCorresponde(
            conversacion, DateTime.UtcNow, new CalendarioLaboralOperadorCae([]))!;
        _propuestas.Lista.Add(propuesta);
        return (conversacion, propuesta);
    }

    private AprobarRecordatorioConversacionCommandHandler CrearAprobar(
        IAlcanceDatosService? alcance = null, bool permitirRemitenteSimulado = true)
    {
        var graphClient = new Microsoft365GraphClientFalso();
        var responder = new ResponderConversacionCommandHandler(
            _conversaciones, new ConexionIntegracionRepositorioFalso(), alcance ?? new AlcanceDatosServiceFalso(), graphClient,
            new AccesoGraphService(new CredencialIntegracionRepositorioFalso(), graphClient), new FileStorageServiceFalso(),
            Options.Create(new ComunicacionesRemitenteOptions { PermitirRemitenteSimulado = permitirRemitenteSimulado }),
            _unitOfWork, NullLogger<ResponderConversacionCommandHandler>.Instance, new RegistroExportacionFalso(), new CaeManager.Application.Tests.Clientes.DescarteCambiosPendientesFalso());
        return new AprobarRecordatorioConversacionCommandHandler(
            _conversaciones, _propuestas, alcance ?? new AlcanceDatosServiceFalso(), responder, _unitOfWork, _descarte);
    }

    private DescartarRecordatorioConversacionCommandHandler CrearDescartar(IAlcanceDatosService? alcance = null) =>
        new(_conversaciones, _propuestas, alcance ?? new AlcanceDatosServiceFalso(), _unitOfWork, _descarte);

    private static AlcanceDatosServiceFalso SinCarteraSobreElCliente() =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: []);

    [Fact]
    public async Task Aprobar_envia_el_borrador_editado_por_el_camino_de_respuesta_y_marca_la_propuesta_aprobada()
    {
        var (conversacion, propuesta) = Sembrar();

        var resultado = await CrearAprobar().Handle(
            new AprobarRecordatorioConversacionCommand(conversacion.Id, propuesta.Id, "Hola, ¿pudisteis revisarlo?"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conversacion.Mensajes.Should().ContainSingle(m =>
            m.Direccion == DireccionMensaje.Saliente && m.CuerpoHtml == "Hola, ¿pudisteis revisarlo?");
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Aprobada);
        _unitOfWork.VecesGuardado.Should().Be(2, "primero se reserva la aprobación y después se guarda el mensaje enviado");
    }

    /// <summary>Doble del envío: anota en qué estado estaba la reserva cuando se envió, o lanza como un fallo de red.</summary>
    private sealed class ResponderQueObserva(Func<Result> alEnviar) : IRequestHandler<ResponderConversacionCommand, Result>
    {
        public int Envios { get; private set; }
        public Task<Result> Handle(ResponderConversacionCommand request, CancellationToken cancellationToken)
        {
            Envios++;
            return Task.FromResult(alEnviar());
        }
    }

    [Fact]
    public async Task La_aprobacion_se_reserva_y_guarda_antes_de_enviar()
    {
        var (conversacion, propuesta) = Sembrar();
        EstadoPropuestaRecordatorio? estadoAlEnviar = null;
        var guardadosAlEnviar = -1;
        var responder = new ResponderQueObserva(() =>
        {
            estadoAlEnviar = propuesta.Estado;
            guardadosAlEnviar = _unitOfWork.VecesGuardado;
            return Result.Exito();
        });

        var resultado = await new AprobarRecordatorioConversacionCommandHandler(
                _conversaciones, _propuestas, new AlcanceDatosServiceFalso(), responder, _unitOfWork, _descarte)
            .Handle(new AprobarRecordatorioConversacionCommand(conversacion.Id, propuesta.Id, "Hola"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        estadoAlEnviar.Should().Be(EstadoPropuestaRecordatorio.Aprobada,
            "la reserva es lo que impide que una segunda aprobación concurrente envíe otro correo");
        guardadosAlEnviar.Should().Be(1, "la reserva tiene que estar guardada (token de concurrencia) antes del envío");
    }

    [Fact]
    public async Task Si_el_envio_lanza_la_propuesta_vuelve_a_Pendiente_y_la_excepcion_sigue()
    {
        var (conversacion, propuesta) = Sembrar();
        var responder = new ResponderQueObserva(() => throw new HttpRequestException("Graph no responde"));

        var accion = () => new AprobarRecordatorioConversacionCommandHandler(
                _conversaciones, _propuestas, new AlcanceDatosServiceFalso(), responder, _unitOfWork, _descarte)
            .Handle(new AprobarRecordatorioConversacionCommand(conversacion.Id, propuesta.Id, "Hola"), CancellationToken.None);

        await accion.Should().ThrowAsync<HttpRequestException>();
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Pendiente,
            "un recordatorio que no salió tiene que poder aprobarse otra vez");
    }

    [Fact]
    public async Task Sin_Asignacion_de_Cartera_sobre_la_conversacion_ni_aprueba_ni_envia()
    {
        var (conversacion, propuesta) = Sembrar();

        var resultado = await CrearAprobar(SinCarteraSobreElCliente()).Handle(
            new AprobarRecordatorioConversacionCommand(conversacion.Id, propuesta.Id, "Hola"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.NoEncontrada");
        conversacion.Mensajes.Should().BeEmpty();
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Pendiente);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Si_el_envio_falla_la_propuesta_vuelve_a_pendiente()
    {
        var (conversacion, propuesta) = Sembrar();

        var resultado = await CrearAprobar(permitirRemitenteSimulado: false).Handle(
            new AprobarRecordatorioConversacionCommand(conversacion.Id, propuesta.Id, "Hola"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.SinBuzonConectado");
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Pendiente, "el recordatorio no salió: se puede reintentar");
        _descarte.VecesDescartado.Should().BeGreaterThan(0, "lo que el envío dejó a medias no puede guardarlo otro comando");
    }

    [Fact]
    public async Task Si_la_respuesta_ya_llego_no_envia_y_descarta_la_propuesta()
    {
        var (conversacion, propuesta) = Sembrar();
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "empresa@contraparte.es", "<p>Enviado.</p>");

        var resultado = await CrearAprobar().Handle(
            new AprobarRecordatorioConversacionCommand(conversacion.Id, propuesta.Id, "Hola"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(AprobarRecordatorioConversacionCommandHandler.ErrorYaNoAplica.Codigo);
        conversacion.Mensajes.Should().NotContain(m => m.Direccion == DireccionMensaje.Saliente);
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Descartada);
    }

    [Fact]
    public async Task Una_propuesta_ya_aprobada_no_se_envia_dos_veces()
    {
        var (conversacion, propuesta) = Sembrar();
        var handler = CrearAprobar();
        (await handler.Handle(new AprobarRecordatorioConversacionCommand(conversacion.Id, propuesta.Id, "Hola"), CancellationToken.None))
            .EsExitoso.Should().BeTrue();

        var segunda = await handler.Handle(
            new AprobarRecordatorioConversacionCommand(conversacion.Id, propuesta.Id, "Hola"), CancellationToken.None);

        segunda.EsFallido.Should().BeTrue();
        segunda.Error.Codigo.Should().Be(PropuestaRecordatorioConversacion.ErrorYaResuelta.Codigo);
        conversacion.Mensajes.Count(m => m.Direccion == DireccionMensaje.Saliente).Should().Be(1);
    }

    [Fact]
    public async Task Una_propuesta_de_otra_conversacion_no_se_aprueba_por_esta()
    {
        var (_, propuesta) = Sembrar();
        var (otra, _) = Sembrar();

        var resultado = await CrearAprobar().Handle(
            new AprobarRecordatorioConversacionCommand(otra.Id, propuesta.Id, "Hola"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Recordatorio.NoEncontrado");
        otra.Mensajes.Should().BeEmpty();
    }

    [Fact]
    public async Task Descartar_no_envia_nada_y_exige_la_cartera()
    {
        var (conversacion, propuesta) = Sembrar();

        var denegado = await CrearDescartar(SinCarteraSobreElCliente()).Handle(
            new DescartarRecordatorioConversacionCommand(conversacion.Id, propuesta.Id), CancellationToken.None);
        denegado.EsFallido.Should().BeTrue();
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Pendiente);

        var resultado = await CrearDescartar().Handle(
            new DescartarRecordatorioConversacionCommand(conversacion.Id, propuesta.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Descartada);
        conversacion.Mensajes.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Restaurar_deshace_el_descarte_y_exige_la_cartera()
    {
        var (conversacion, propuesta) = Sembrar();
        (await CrearDescartar().Handle(
            new DescartarRecordatorioConversacionCommand(conversacion.Id, propuesta.Id), CancellationToken.None)).EsExitoso.Should().BeTrue();

        var denegado = await new RestaurarRecordatorioConversacionCommandHandler(
                _conversaciones, _propuestas, SinCarteraSobreElCliente(), _unitOfWork, _descarte)
            .Handle(new RestaurarRecordatorioConversacionCommand(conversacion.Id, propuesta.Id), CancellationToken.None);
        denegado.EsFallido.Should().BeTrue();
        denegado.Error.Codigo.Should().Be("Conversacion.NoEncontrada", "fuera de cartera no se delata la conversación");
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Descartada);

        var resultado = await new RestaurarRecordatorioConversacionCommandHandler(
                _conversaciones, _propuestas, new AlcanceDatosServiceFalso(), _unitOfWork, _descarte)
            .Handle(new RestaurarRecordatorioConversacionCommand(conversacion.Id, propuesta.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Pendiente);
        conversacion.Mensajes.Should().BeEmpty("deshacer el descarte no envía nada");
        _unitOfWork.VecesGuardado.Should().Be(2, "un guardado del descarte y otro de la restauración");
    }

    [Fact]
    public async Task Restaurar_una_propuesta_de_otra_conversacion_falla()
    {
        var (_, propuesta) = Sembrar();
        var (otra, _) = Sembrar();
        propuesta.Descartar(DateTime.UtcNow).EsExitoso.Should().BeTrue();

        var resultado = await new RestaurarRecordatorioConversacionCommandHandler(
                _conversaciones, _propuestas, new AlcanceDatosServiceFalso(), _unitOfWork, _descarte)
            .Handle(new RestaurarRecordatorioConversacionCommand(otra.Id, propuesta.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Recordatorio.NoEncontrado");
        propuesta.Estado.Should().Be(EstadoPropuestaRecordatorio.Descartada);
    }

    [Fact]
    public async Task La_consulta_solo_devuelve_la_propuesta_vigente_y_dentro_de_cartera()
    {
        var (conversacion, propuesta) = Sembrar();
        var contexto = new ComunicacionesQueryContextFalso();
        contexto.ListaConversaciones.Add(conversacion);
        contexto.ListaPropuestasRecordatorioConversacion.Add(propuesta);

        (await new ObtenerRecordatorioPropuestoConversacionQueryHandler(contexto, new AlcanceDatosServiceFalso())
                .Handle(new ObtenerRecordatorioPropuestoConversacionQuery(conversacion.Id), CancellationToken.None))!
            .PropuestaId.Should().Be(propuesta.Id);

        (await new ObtenerRecordatorioPropuestoConversacionQueryHandler(contexto, SinCarteraSobreElCliente())
                .Handle(new ObtenerRecordatorioPropuestoConversacionQuery(conversacion.Id), CancellationToken.None))
            .Should().BeNull("fuera de cartera no se delata si hay propuesta");

        conversacion.CambiarEstado(EstadoConversacion.Abierta).EsExitoso.Should().BeTrue();
        (await new ObtenerRecordatorioPropuestoConversacionQueryHandler(contexto, new AlcanceDatosServiceFalso())
                .Handle(new ObtenerRecordatorioPropuestoConversacionQuery(conversacion.Id), CancellationToken.None))
            .Should().BeNull("la espera terminó aunque el proceso de fondo aún no la haya descartado");
    }

    [Fact]
    public async Task El_destinatario_es_el_remitente_del_ultimo_entrante_al_que_responde_el_envio()
    {
        // Los mensajes van antes de entrar en la espera: un entrante posterior la terminaría.
        var conversacion = new Conversacion("Documentación pendiente", clienteId: _clienteId);
        var contexto = new ComunicacionesQueryContextFalso();
        var ahora = DateTime.UtcNow;
        contexto.ListaMensajes.AddRange([
            conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "antiguo@empresa.example", "a",
                ahora.AddDays(-12), "ext-1"),
            conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "real@empresa.example", "b",
                ahora.AddDays(-11), "ext-2"),
            conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "sin-id@empresa.example", "c",
                ahora.AddDays(-10.5)),
            conversacion.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.Correo, "buzon@operador.example", "d",
                ahora.AddDays(-10.2), "ext-3"),
        ]);
        conversacion.CambiarEstado(EstadoConversacion.EsperandoRespuestaExterna, ahora.AddDays(-10)).EsExitoso.Should().BeTrue();
        var propuesta = PropuestaRecordatorioConversacion.ProponerSiCorresponde(conversacion, ahora, new CalendarioLaboralOperadorCae([]))!;
        contexto.ListaConversaciones.Add(conversacion);
        contexto.ListaPropuestasRecordatorioConversacion.Add(propuesta);

        var dto = await new ObtenerRecordatorioPropuestoConversacionQueryHandler(contexto, new AlcanceDatosServiceFalso())
            .Handle(new ObtenerRecordatorioPropuestoConversacionQuery(conversacion.Id), CancellationToken.None);

        dto!.Destinatario.Should().Be("real@empresa.example",
            "el envío responde al último entrante con id externo: ni a uno sin id ni a un saliente");
    }

    private sealed class PropuestasFalsas : IPropuestaRecordatorioConversacionRepository
    {
        public List<PropuestaRecordatorioConversacion> Lista { get; } = [];

        public Task<PropuestaRecordatorioConversacion?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Lista.FirstOrDefault(p => p.Id == id));
    }
}
