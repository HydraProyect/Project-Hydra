using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class DesconectarBuzonCommandHandlerTests
{
    private static DesconectarBuzonCommandHandler CrearHandler(
        ConexionIntegracionRepositorioFalso conexionRepositorio, SuscripcionWebhookRepositorioFalso suscripcionRepositorio,
        ReclamacionBuzonIntegracionRepositorioFalso reclamacionRepositorio,
        AlcanceDatosServiceFalso alcanceDatos, Microsoft365GraphClientFalso graphClient, CredencialIntegracionRepositorioFalso credencialRepositorio,
        UnitOfWorkFalso unitOfWork) =>
        new(conexionRepositorio, suscripcionRepositorio, reclamacionRepositorio, alcanceDatos, graphClient,
            new AccesoGraphService(credencialRepositorio, graphClient), unitOfWork, NullLogger<DesconectarBuzonCommandHandler>.Instance);

    // Ronda 10, hallazgo medio: primero se guarda la desconexión y después se borra en Graph.

    [Fact]
    public async Task Guarda_la_desconexion_antes_de_borrar_la_suscripcion_en_Graph()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var suscripciones = new SuscripcionWebhookRepositorioFalso();
        suscripciones.Agregar(new SuscripcionWebhook(conexion.Id, "sub-1", "estado", DateTime.UtcNow.AddDays(2)));
        var unitOfWork = new UnitOfWorkFalso();
        var guardadosAlBorrar = -1;
        var graph = new Microsoft365GraphClientFalso();
        graph.AlEliminarSuscripcion = () => guardadosAlBorrar = unitOfWork.VecesGuardado;
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        guardadosAlBorrar.Should().Be(1, "la Deshabilitada ya estaba guardada cuando se pidió el borrado");
        conexion.HuecoPendienteDesdeUtc.Should().BeNull("el borrado fue bien");
    }

    [Fact]
    public async Task Si_el_borrado_falla_la_marca_queda_en_el_momento_del_borrado_y_sigue_Deshabilitada()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var suscripciones = new SuscripcionWebhookRepositorioFalso();
        suscripciones.Agregar(new SuscripcionWebhook(conexion.Id, "sub-1", "estado", DateTime.UtcNow.AddDays(2)));
        var graph = new Microsoft365GraphClientFalso { ErrorEliminacion = CaeManager.Domain.Common.Error.Crear("x", "503") };
        var unitOfWork = new UnitOfWorkFalso();
        var antes = DateTime.UtcNow;
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        conexion.HuecoPendienteDesdeUtc.Should().BeOnOrAfter(antes).And.BeOnOrBefore(DateTime.UtcNow);
        unitOfWork.VecesGuardado.Should().Be(2);
    }

    [Fact]
    public async Task Deshabilita_una_conexion_habilitada_y_libera_la_reclamacion_del_buzon()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso();
        reclamacionRepositorio.Reclamar(new ReclamacionBuzonIntegracion("cae@cliente.com", null, Guid.NewGuid(), conexion.Id, DateTime.UtcNow));
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(
            conexionRepositorio, new SuscripcionWebhookRepositorioFalso(), reclamacionRepositorio, new AlcanceDatosServiceFalso(),
            new Microsoft365GraphClientFalso(), new CredencialIntegracionRepositorioFalso(), unitOfWork);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        unitOfWork.VecesGuardado.Should().Be(1);
        reclamacionRepositorio.Reclamaciones.Should().BeEmpty("desconectar debe dejar el buzón disponible para cualquier tenant");
    }

    /// <summary>
    /// Un buzón compartido no tiene credencial: la baja de su suscripción pide el token de aplicación de su directorio
    /// (la baja no lleva buzón en la ruta, <c>/subscriptions/{id}</c>, así que no hay <c>BuzonGraph</c> que comprobar).
    /// </summary>
    [Fact]
    public async Task Con_un_buzon_compartido_elimina_la_suscripcion_con_el_token_de_aplicacion_del_directorio()
    {
        var directorio = Guid.NewGuid().ToString("D");
        var conexion = ConexionIntegracion.CrearBuzonCompartido("piloto@tenant.com", "Buzón piloto", directorio, Guid.NewGuid());
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var suscripcionRepositorio = new SuscripcionWebhookRepositorioFalso();
        suscripcionRepositorio.Agregar(new SuscripcionWebhook(conexion.Id, "graph-sub-1", "secreto-compartido", DateTime.UtcNow.AddDays(2)));
        var graphClient = new Microsoft365GraphClientFalso();
        var handler = CrearHandler(
            conexionRepositorio, suscripcionRepositorio, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graphClient, new CredencialIntegracionRepositorioFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        graphClient.DirectoriosConTokenDeAplicacion.Should().Equal(directorio);
        graphClient.VecesRefrescado.Should().Be(0, "no hay token delegado que refrescar");
        graphClient.SuscripcionesEliminadas.Should().Equal("graph-sub-1");
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
    }

    [Fact]
    public async Task No_desconecta_el_buzon_personal_de_otro_gestor_aunque_se_le_pase_su_Id()
    {
        // Mismo hallazgo que EnviarMensajeNuevoCommandHandlerTests: un buzón
        // personal tiene ClienteId null, igual que el genérico del tenant —
        // sin ConexionIntegracionVisibleAsync, cualquiera con acceso a
        // Comunicaciones podía desconectar (y borrar la suscripción de Graph
        // de) el buzón personal de un colega pasando su Id a mano.
        var conexionPersonal = new ConexionIntegracion(
            "gestor.otro@ejemplo.local", "Buzón personal", gestorPropietarioId: Guid.NewGuid());
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexionPersonal);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(
            conexionRepositorio, new SuscripcionWebhookRepositorioFalso(), new ReclamacionBuzonIntegracionRepositorioFalso(),
            new AlcanceDatosServiceFalso(conexionIntegracionVisible: false),
            new Microsoft365GraphClientFalso(), new CredencialIntegracionRepositorioFalso(), unitOfWork);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexionPersonal.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        conexionPersonal.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
