using CaeManager.Application.Clientes;
using CaeManager.Application.Clientes.Commands.ReasignarEjecutivoCliente;
using CaeManager.Application.Common;
using Microsoft.EntityFrameworkCore;
using CaeManager.Application.Tests.Notificaciones;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Clientes;

public class ReasignarEjecutivoClienteCommandHandlerTests
{
    /// <summary>Quien reasigna en todos los tests: el Coordinador CAE de los destinos que da por buenos el directorio falso.</summary>
    private static readonly Guid ActorId = Guid.NewGuid();

    private static ReasignarEjecutivoClienteCommandHandler CrearHandler(
        EmpresaRepositorioFalso clienteRepositorio,
        ConfiguracionIaDocumentoClienteRepositorioFalso configuracionIaRepositorio,
        NotificacionUsuarioRepositorioFalso notificacionRepositorio,
        UnitOfWorkFalso unitOfWork,
        string? rol,
        AlcanceDatosServiceFalso? alcanceDatos = null,
        DirectorioDestinosCarteraFalso? directorio = null,
        DescarteCambiosPendientesFalso? descarte = null,
        AsignacionesOperativasWriterFalso? writer = null) =>
        CrearHandlerCon(new CurrentUserServiceFalso(ActorId, rol), clienteRepositorio, configuracionIaRepositorio,
            notificacionRepositorio, unitOfWork, alcanceDatos, directorio, descarte, writer);

    private static ReasignarEjecutivoClienteCommandHandler CrearHandlerCon(
        CurrentUserServiceFalso usuario,
        EmpresaRepositorioFalso clienteRepositorio,
        ConfiguracionIaDocumentoClienteRepositorioFalso configuracionIaRepositorio,
        NotificacionUsuarioRepositorioFalso notificacionRepositorio,
        UnitOfWorkFalso unitOfWork,
        AlcanceDatosServiceFalso? alcanceDatos,
        DirectorioDestinosCarteraFalso? directorio,
        DescarteCambiosPendientesFalso? descarte,
        AsignacionesOperativasWriterFalso? writer) =>
        new(Reasignador(usuario, clienteRepositorio, configuracionIaRepositorio, notificacionRepositorio,
                alcanceDatos, directorio, writer),
            unitOfWork, usuario, descarte ?? new DescarteCambiosPendientesFalso());

    internal static ReasignadorCarteraCliente Reasignador(
        CurrentUserServiceFalso usuario,
        EmpresaRepositorioFalso clienteRepositorio,
        ConfiguracionIaDocumentoClienteRepositorioFalso? configuracionIaRepositorio = null,
        NotificacionUsuarioRepositorioFalso? notificacionRepositorio = null,
        AlcanceDatosServiceFalso? alcanceDatos = null,
        DirectorioDestinosCarteraFalso? directorio = null,
        AsignacionesOperativasWriterFalso? writer = null) =>
        new(clienteRepositorio, configuracionIaRepositorio ?? new ConfiguracionIaDocumentoClienteRepositorioFalso(),
            notificacionRepositorio ?? new NotificacionUsuarioRepositorioFalso(), usuario,
            alcanceDatos ?? new AlcanceDatosServiceFalso(), writer ?? new AsignacionesOperativasWriterFalso(),
            directorio ?? new DirectorioDestinosCarteraFalso(new DestinoCartera(true, "GestorCae", ActorId, false)));

    [Fact]
    public async Task Reasigna_y_avisa_al_gestor_anterior_y_al_nuevo()
    {
        var gestorAnteriorId = Guid.NewGuid();
        var gestorNuevoId = Guid.NewGuid();
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, gestorAnteriorId);

        var clienteRepositorio = new EmpresaRepositorioFalso();
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
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, null);
        var tipoDocumentoId = Guid.NewGuid();

        var clienteRepositorio = new EmpresaRepositorioFalso();
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
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, null);
        var clienteRepositorio = new EmpresaRepositorioFalso();
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
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, null);
        var clienteRepositorio = new EmpresaRepositorioFalso();
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
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, null);
        var clienteRepositorio = new EmpresaRepositorioFalso();
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
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, null);
        var clienteRepositorio = new EmpresaRepositorioFalso();
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
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, gestorId);
        var clienteRepositorio = new EmpresaRepositorioFalso();
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

    // ── Destino de la cartera (revisión Codex de la PR #931) ────────────────

    public static TheoryData<string, DestinoCartera?, string> DestinosInvalidos => new()
    {
        { "Administrador", null, "Cliente.DestinoNoAlcanzable" },
        { "Administrador", new DestinoCartera(false, "GestorCae", ActorId, false), "Cliente.DestinoInactivo" },
        { "Administrador", new DestinoCartera(true, "CoordinadorCae", ActorId, false), "Cliente.DestinoNoEsGestorCae" },
        { "Administrador", new DestinoCartera(true, "Consulta", ActorId, false), "Cliente.DestinoNoEsGestorCae" },
        { "Administrador", new DestinoCartera(true, "Administrador", ActorId, false), "Cliente.DestinoNoEsGestorCae" },
        { "Administrador", new DestinoCartera(true, null, ActorId, false), "Cliente.DestinoNoEsGestorCae" },
        { "Administrador", new DestinoCartera(true, "CoordinadorCae", ActorId, true), "Cliente.DestinoNoEsGestorCae" },
        { "Administrador", new DestinoCartera(true, "Consulta", ActorId, true), "Cliente.DestinoNoEsGestorCae" },
        { "Administrador", new DestinoCartera(false, "GestorCae", ActorId, true), "Cliente.DestinoInactivo" },
        { "CoordinadorCae", new DestinoCartera(true, "GestorCae", Guid.NewGuid(), false), "Cliente.DestinoFueraDeAlcance" },
        { "CoordinadorCae", new DestinoCartera(true, "GestorCae", null, false), "Cliente.DestinoFueraDeAlcance" },
        { "CoordinadorCae", new DestinoCartera(true, "GestorCae", Guid.NewGuid(), true), "Cliente.DestinoFueraDeAlcance" },
    };

    [Theory]
    [MemberData(nameof(DestinosInvalidos))]
    public async Task Rechaza_un_destino_que_no_puede_llevar_la_cartera_sin_escribir_nada(
        string rolActor, DestinoCartera? destino, string codigoEsperado)
    {
        var gestorAnteriorId = Guid.NewGuid();
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, gestorAnteriorId);
        var clienteRepositorio = new EmpresaRepositorioFalso();
        clienteRepositorio.Agregar(cliente);
        var notificacionRepositorio = new NotificacionUsuarioRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var writer = new AsignacionesOperativasWriterFalso();
        var directorio = new DirectorioDestinosCarteraFalso(destino);
        var handler = CrearHandler(
            clienteRepositorio, new ConfiguracionIaDocumentoClienteRepositorioFalso(), notificacionRepositorio, unitOfWork,
            rolActor, directorio: directorio, writer: writer);
        var destinoId = Guid.NewGuid();

        var resultado = await handler.Handle(new ReasignarEjecutivoClienteCommand(cliente.Id, destinoId), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(codigoEsperado);
        directorio.Consultados.Should().Equal(destinoId);
        cliente.EjecutivoUsuarioId.Should().Be(gestorAnteriorId, "el cliente no cambia de manos");
        notificacionRepositorio.Notificaciones.Should().BeEmpty();
        writer.CarterasReasignadas.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinadorCae_pasa_la_cartera_a_un_Gestor_CAE_activo_que_le_reporta(bool esOperadorDelegado)
    {
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, Guid.NewGuid());
        var clienteRepositorio = new EmpresaRepositorioFalso();
        clienteRepositorio.Agregar(cliente);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(
            clienteRepositorio, new ConfiguracionIaDocumentoClienteRepositorioFalso(), new NotificacionUsuarioRepositorioFalso(),
            unitOfWork, "CoordinadorCae",
            directorio: new DirectorioDestinosCarteraFalso(new DestinoCartera(true, "GestorCae", ActorId, esOperadorDelegado)));
        var destinoId = Guid.NewGuid();

        var resultado = await handler.Handle(new ReasignarEjecutivoClienteCommand(cliente.Id, destinoId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        cliente.EjecutivoUsuarioId.Should().Be(destinoId);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Quitar_el_Gestor_CAE_no_consulta_ningun_destino()
    {
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, Guid.NewGuid());
        var clienteRepositorio = new EmpresaRepositorioFalso();
        clienteRepositorio.Agregar(cliente);
        var directorio = new DirectorioDestinosCarteraFalso(null);
        var handler = CrearHandler(
            clienteRepositorio, new ConfiguracionIaDocumentoClienteRepositorioFalso(), new NotificacionUsuarioRepositorioFalso(),
            new UnitOfWorkFalso(), "Administrador", directorio: directorio);

        var resultado = await handler.Handle(new ReasignarEjecutivoClienteCommand(cliente.Id, null), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        cliente.EjecutivoUsuarioId.Should().BeNull();
        directorio.Consultados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_conflicto_al_guardar_descarta_los_cambios_pendientes_del_contexto()
    {
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia", "B12345674", false, null, Guid.NewGuid());
        var clienteRepositorio = new EmpresaRepositorioFalso();
        clienteRepositorio.Agregar(cliente);
        var unitOfWork = new UnitOfWorkFalso { ExcepcionAlGuardar = new DbUpdateException("índice único de responsable vigente") };
        var descarte = new DescarteCambiosPendientesFalso();
        var handler = CrearHandler(
            clienteRepositorio, new ConfiguracionIaDocumentoClienteRepositorioFalso(), new NotificacionUsuarioRepositorioFalso(),
            unitOfWork, "Administrador", descarte: descarte);

        var resultado = await handler.Handle(new ReasignarEjecutivoClienteCommand(cliente.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.ConflictoDeReasignacion");
        descarte.VecesDescartado.Should().Be(1);
    }
}

public class DirectorioDestinosCarteraFalso(DestinoCartera? destino) : IDirectorioDestinosCartera
{
    public List<Guid> Consultados { get; } = [];

    /// <summary>
    /// Lo que devuelve cada lectura de cartera, en orden; la última se repite. Permite
    /// simular un Cliente empresarial que llega entre dos lecturas.
    /// </summary>
    public Queue<CarteraVigente> Carteras { get; } = new();

    private CarteraVigente _ultimaCartera = CarteraVigente.Vacia;

    public Task<DestinoCartera?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        Consultados.Add(usuarioId);
        return Task.FromResult(destino);
    }

    public Task<CarteraVigente> ObtenerCarteraVigenteAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        if (Carteras.TryDequeue(out var siguiente)) _ultimaCartera = siguiente;
        return Task.FromResult(_ultimaCartera);
    }
}

public class DescarteCambiosPendientesFalso : IDescarteCambiosPendientes
{
    public int VecesDescartado { get; private set; }

    public void DescartarCambiosPendientes() => VecesDescartado++;
}
