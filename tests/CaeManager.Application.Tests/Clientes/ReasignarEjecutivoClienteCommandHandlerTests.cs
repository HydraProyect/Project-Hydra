using CaeManager.Application.Clientes.Commands.ReasignarEjecutivoCliente;
using CaeManager.Application.Tests.Notificaciones;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Clientes;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Clientes;

public class ReasignarEjecutivoClienteCommandHandlerTests
{
    private static ReasignarEjecutivoClienteCommandHandler CrearHandler(
        ClienteRepositorioFalso clienteRepositorio,
        ConfiguracionIaDocumentoClienteRepositorioFalso configuracionIaRepositorio,
        NotificacionUsuarioRepositorioFalso notificacionRepositorio,
        UnitOfWorkFalso unitOfWork,
        string? rol,
        AlcanceDatosServiceFalso? alcanceDatos = null) =>
        new(clienteRepositorio, configuracionIaRepositorio, notificacionRepositorio, unitOfWork,
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), alcanceDatos ?? new AlcanceDatosServiceFalso(),
            new AsignacionesOperativasWriterFalso());

    [Fact]
    public async Task Reasigna_y_avisa_al_gestor_anterior_y_al_nuevo()
    {
        var gestorAnteriorId = Guid.NewGuid();
        var gestorNuevoId = Guid.NewGuid();
        var cliente = new Cliente("Cadena Industrial Iberia", "B12345674", false, ejecutivoUsuarioId: gestorAnteriorId);

        var clienteRepositorio = new ClienteRepositorioFalso();
        clienteRepositorio.Agregar(cliente);
        var configuracionIaRepositorio = new ConfiguracionIaDocumentoClienteRepositorioFalso();
        var notificacionRepositorio = new NotificacionUsuarioRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(clienteRepositorio, configuracionIaRepositorio, notificacionRepositorio, unitOfWork, "CoordinadorCae");

        var resultado = await handler.Handle(new ReasignarEjecutivoClienteCommand(cliente.Id, gestorNuevoId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        cliente.EjecutivoUsuarioId.Should().Be(gestorNuevoId);
        unitOfWork.VecesGuardado.Should().Be(1);

        notificacionRepositorio.Notificaciones.Should().Contain(n => n.UsuarioDestinatarioId == gestorAnteriorId && n.Mensaje.Contains("quitado"));
        notificacionRepositorio.Notificaciones.Should().Contain(n => n.UsuarioDestinatarioId == gestorNuevoId && n.Mensaje.Contains("asignado"));
    }

    [Fact]
    public async Task Avisa_al_nuevo_gestor_de_los_tipos_de_documento_sin_lectura_ia()
    {
        var gestorNuevoId = Guid.NewGuid();
        var cliente = new Cliente("Cadena Industrial Iberia", "B12345674", false);
        var tipoDocumentoId = Guid.NewGuid();

        var clienteRepositorio = new ClienteRepositorioFalso();
        clienteRepositorio.Agregar(cliente);
        var configuracionIaRepositorio = new ConfiguracionIaDocumentoClienteRepositorioFalso();
        configuracionIaRepositorio.NombresTipoDocumento[tipoDocumentoId] = "ITA";
        configuracionIaRepositorio.Agregar(new CaeManager.Domain.Documentos.ConfiguracionIaDocumentoCliente(cliente.Id, tipoDocumentoId, activa: false));
        var notificacionRepositorio = new NotificacionUsuarioRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(clienteRepositorio, configuracionIaRepositorio, notificacionRepositorio, unitOfWork, "Administrador");

        await handler.Handle(new ReasignarEjecutivoClienteCommand(cliente.Id, gestorNuevoId), CancellationToken.None);

        var aviso = notificacionRepositorio.Notificaciones.Should()
            .ContainSingle(n => n.UsuarioDestinatarioId == gestorNuevoId && n.UrlAccion != null).Subject;
        aviso.Mensaje.Should().Contain("ITA");
        aviso.UrlAccion.Should().Be($"/clientes/{cliente.Id}/lectura-ia");
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData((string?)null)]
    public async Task Roles_sin_permiso_no_pueden_reasignar(string? rol)
    {
        var cliente = new Cliente("Cadena Industrial Iberia", "B12345674", false);
        var clienteRepositorio = new ClienteRepositorioFalso();
        clienteRepositorio.Agregar(cliente);
        var configuracionIaRepositorio = new ConfiguracionIaDocumentoClienteRepositorioFalso();
        var notificacionRepositorio = new NotificacionUsuarioRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(clienteRepositorio, configuracionIaRepositorio, notificacionRepositorio, unitOfWork, rol);

        var resultado = await handler.Handle(new ReasignarEjecutivoClienteCommand(cliente.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.SinPermisoReasignar");
        cliente.EjecutivoUsuarioId.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    // ── D-001: CoordinadorCae acotado por su ambito de supervision ─────────

    [Fact]
    public async Task CoordinadorCae_reasigna_un_cliente_dentro_de_su_ambito_de_supervision()
    {
        var cliente = new Cliente("Cadena Industrial Iberia", "B12345674", false);
        var clienteRepositorio = new ClienteRepositorioFalso();
        clienteRepositorio.Agregar(cliente);
        var configuracionIaRepositorio = new ConfiguracionIaDocumentoClienteRepositorioFalso();
        var notificacionRepositorio = new NotificacionUsuarioRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var alcanceDatos = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [cliente.Id]);
        var handler = CrearHandler(
            clienteRepositorio, configuracionIaRepositorio, notificacionRepositorio, unitOfWork, "CoordinadorCae", alcanceDatos);

        var resultado = await handler.Handle(
            new ReasignarEjecutivoClienteCommand(cliente.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("el cliente está dentro de la cartera que supervisa");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task CoordinadorCae_no_puede_reasignar_un_cliente_fuera_de_su_ambito_de_supervision()
    {
        var cliente = new Cliente("Cadena Industrial Iberia", "B12345674", false);
        var clienteRepositorio = new ClienteRepositorioFalso();
        clienteRepositorio.Agregar(cliente);
        var configuracionIaRepositorio = new ConfiguracionIaDocumentoClienteRepositorioFalso();
        var notificacionRepositorio = new NotificacionUsuarioRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        // Cartera vacia: el cliente existe, pero ningun Gestor que reporte a
        // este Coordinador lo tiene asignado.
        var alcanceDatos = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: []);
        var handler = CrearHandler(
            clienteRepositorio, configuracionIaRepositorio, notificacionRepositorio, unitOfWork, "CoordinadorCae", alcanceDatos);

        var resultado = await handler.Handle(
            new ReasignarEjecutivoClienteCommand(cliente.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.NoEncontrado",
            "una denegación por ámbito no puede distinguirse de una fila inexistente");
        cliente.EjecutivoUsuarioId.Should().BeNull("no debe escribirse nada");
        notificacionRepositorio.Notificaciones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Administrador_reasigna_sin_restriccion_de_ambito()
    {
        var cliente = new Cliente("Cadena Industrial Iberia", "B12345674", false);
        var clienteRepositorio = new ClienteRepositorioFalso();
        clienteRepositorio.Agregar(cliente);
        var configuracionIaRepositorio = new ConfiguracionIaDocumentoClienteRepositorioFalso();
        var notificacionRepositorio = new NotificacionUsuarioRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        // Sin cartera propia: Administrador tiene acceso total, no deriva de
        // AlcanceDatosService una lista de clientes.
        var alcanceDatos = new AlcanceDatosServiceFalso(tieneAccesoTotal: true);
        var handler = CrearHandler(
            clienteRepositorio, configuracionIaRepositorio, notificacionRepositorio, unitOfWork, "Administrador", alcanceDatos);

        var resultado = await handler.Handle(
            new ReasignarEjecutivoClienteCommand(cliente.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task No_hace_nada_si_el_gestor_no_cambia()
    {
        var gestorId = Guid.NewGuid();
        var cliente = new Cliente("Cadena Industrial Iberia", "B12345674", false, ejecutivoUsuarioId: gestorId);
        var clienteRepositorio = new ClienteRepositorioFalso();
        clienteRepositorio.Agregar(cliente);
        var configuracionIaRepositorio = new ConfiguracionIaDocumentoClienteRepositorioFalso();
        var notificacionRepositorio = new NotificacionUsuarioRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(clienteRepositorio, configuracionIaRepositorio, notificacionRepositorio, unitOfWork, "Administrador");

        var resultado = await handler.Handle(new ReasignarEjecutivoClienteCommand(cliente.Id, gestorId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        notificacionRepositorio.Notificaciones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
