using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.AsignarEjecutivoConversacion;
using CaeManager.Application.Comunicaciones.Commands.CambiarEstadoConversacion;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Regresión del hallazgo N-3 de INFORME-AUDITORIA-2.md: los comandos del
/// módulo cargaban la conversación por un repositorio filtrado por tenant
/// —así que el cruce entre tenants estaba cerrado— pero no comprobaban el
/// alcance *dentro* del tenant, que el lado de lectura sí comprobaba.
///
/// El escenario es real, no hipotético: la cartera se reasigna
/// (ReasignarEjecutivoClienteCommand existe), así que un gestor conserva
/// Guids de conversaciones de clientes que ya no lleva.
/// </summary>
public class AlcanceEscrituraConversacionTests
{
    private static readonly Guid ClienteAjeno = Guid.NewGuid();

    // Cartera restringida que no incluye ClienteAjeno.
    private static AlcanceDatosServiceFalso AlcanceSinElClienteAjeno() =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]);

    private static ResponderConversacionCommandHandler CrearHandlerResponder(
        ConversacionRepositorioFalso repositorio, AlcanceDatosServiceFalso alcance, UnitOfWorkFalso unitOfWork,
        bool permitirRemitenteSimulado = false)
    {
        var graphClient = new Microsoft365GraphClientFalso();
        var accesoGraph = new AccesoGraphService(new CredencialIntegracionRepositorioFalso(), graphClient);
        var opciones = Options.Create(new ComunicacionesRemitenteOptions { PermitirRemitenteSimulado = permitirRemitenteSimulado });
        return new ResponderConversacionCommandHandler(
            repositorio, new ConexionIntegracionRepositorioFalso(), alcance, graphClient, accesoGraph,
            new FileStorageServiceFalso(), opciones, unitOfWork, NullLogger<ResponderConversacionCommandHandler>.Instance);
    }

    [Fact]
    public async Task No_se_puede_responder_en_el_hilo_de_un_cliente_fuera_de_la_cartera()
    {
        var (conversacion, repositorio, unitOfWork) = Preparar();
        var handler = CrearHandlerResponder(repositorio, AlcanceSinElClienteAjeno(), unitOfWork);

        var resultado = await handler.Handle(
            new ResponderConversacionCommand(conversacion.Id, "<p>Me meto en tu hilo</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        // "No encontrada", no "no autorizado": no se confirma que el hilo exista.
        resultado.Error.Codigo.Should().Be("Conversacion.NoEncontrada");
        conversacion.Mensajes.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task No_se_puede_cerrar_el_hilo_de_un_cliente_fuera_de_la_cartera()
    {
        var (conversacion, repositorio, unitOfWork) = Preparar();
        var handler = new CambiarEstadoConversacionCommandHandler(repositorio, AlcanceSinElClienteAjeno(), unitOfWork);

        var resultado = await handler.Handle(
            new CambiarEstadoConversacionCommand(conversacion.Id, EstadoConversacion.Cerrada), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        conversacion.Estado.Should().Be(EstadoConversacion.Abierta);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task No_se_puede_reasignar_el_hilo_de_un_cliente_fuera_de_la_cartera()
    {
        var (conversacion, repositorio, unitOfWork) = Preparar();
        var handler = new AsignarEjecutivoConversacionCommandHandler(
            repositorio, AlcanceSinElClienteAjeno(), new DirectorioUsuariosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new AsignarEjecutivoConversacionCommand(conversacion.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        conversacion.EjecutivoAsignadoId.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task No_se_puede_asignar_la_conversacion_a_un_usuario_de_otra_organizacion()
    {
        // Hallazgo N-10: se justificaba no validar el Guid con "Web ya valida
        // que viene de un selector". Un selector no es una frontera de
        // autorización.
        var conversacion = new Conversacion("Consulta", clienteId: ClienteAjeno);
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new AsignarEjecutivoConversacionCommandHandler(
            repositorio,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [ClienteAjeno]),
            new DirectorioUsuariosServiceFalso(esVisible: false),
            unitOfWork);

        var resultado = await handler.Handle(
            new AsignarEjecutivoConversacionCommand(conversacion.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        conversacion.EjecutivoAsignadoId.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Desasignar_el_ejecutivo_sigue_siendo_posible()
    {
        // null no es un usuario que validar: es "quitar el asignado".
        var conversacion = new Conversacion("Consulta", clienteId: ClienteAjeno);
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new AsignarEjecutivoConversacionCommandHandler(
            repositorio,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [ClienteAjeno]),
            new DirectorioUsuariosServiceFalso(esVisible: false),
            unitOfWork);

        var resultado = await handler.Handle(
            new AsignarEjecutivoConversacionCommand(conversacion.Id, null), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Con_el_cliente_en_la_cartera_la_respuesta_sigue_funcionando()
    {
        // Contrapeso: el alcance no puede romper el uso normal del módulo.
        var (conversacion, repositorio, unitOfWork) = Preparar();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [ClienteAjeno]);
        var handler = CrearHandlerResponder(repositorio, alcance, unitOfWork, permitirRemitenteSimulado: true);

        var resultado = await handler.Handle(
            new ResponderConversacionCommand(conversacion.Id, "<p>Respondo lo mío</p>"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conversacion.Mensajes.Should().ContainSingle();
    }

    [Fact]
    public async Task La_cola_de_triage_sigue_siendo_escribible_pese_a_la_cartera()
    {
        // Una conversación sin cliente resuelto no pertenece a ninguna
        // cartera: acotarla por cartera dejaría el triaje sin nadie que lo
        // pudiera hacer (ver ClienteOpcionalVisibleAsync).
        var conversacion = new Conversacion("Correo sin remitente reconocido");
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandlerResponder(repositorio, AlcanceSinElClienteAjeno(), unitOfWork, permitirRemitenteSimulado: true);

        var resultado = await handler.Handle(
            new ResponderConversacionCommand(conversacion.Id, "<p>Pido datos</p>"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    private static (Conversacion, ConversacionRepositorioFalso, UnitOfWorkFalso) Preparar()
    {
        var conversacion = new Conversacion("Documentación pendiente", clienteId: ClienteAjeno);
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);

        return (conversacion, repositorio, new UnitOfWorkFalso());
    }
}
