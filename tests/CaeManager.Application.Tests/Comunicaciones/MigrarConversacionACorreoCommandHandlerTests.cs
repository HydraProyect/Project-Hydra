using CaeManager.Application.Comunicaciones.Commands.MigrarConversacionACorreo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

public class MigrarConversacionACorreoCommandHandlerTests
{
    private const string Telefono = "+34600111222";

    private readonly ConversacionRepositorioFalso _conversaciones = new();
    private readonly IntegracionesQueryContextFalso _integraciones = new();
    private readonly ConexionIntegracionRepositorioFalso _conexiones = new();
    private readonly Microsoft365GraphClientFalso _graphClient = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    private MigrarConversacionACorreoCommandHandler CrearHandler()
    {
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        foreach (var conexion in _conexiones.Conexiones)
            credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));

        var accesoGraph = new AccesoGraphService(credencialRepositorio, _graphClient);
        return new MigrarConversacionACorreoCommandHandler(
            _conversaciones, _integraciones, _conexiones, new AlcanceDatosServiceFalso(), _graphClient, accesoGraph,
            new ResolucionParticipanteConversacionServiceFalso(), _unitOfWork);
    }

    private ConexionIntegracion AgregarBuzonHabilitado(Guid? clienteId = null)
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE", clienteId);
        _conexiones.Agregar(conexion);
        _integraciones.ConexionesLista.Add(conexion);
        return conexion;
    }

    private Conversacion CrearConversacionWhatsAppConVentanaCerrada()
    {
        var conexionWhatsApp = new ConexionIntegracion("+34686543364", "Línea", proveedor: ProveedorIntegracion.WhatsApp);
        var conversacion = Conversacion.CrearWhatsApp(Telefono, conexionWhatsApp.Id, null, Guid.NewGuid());
        conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.WhatsApp, Telefono, "Hola", DateTime.UtcNow.AddHours(-30));
        _conversaciones.Agregar(conversacion);
        return conversacion;
    }

    [Fact]
    public async Task Con_ventana_cerrada_y_buzon_disponible_envia_y_agrega_el_mensaje_como_Correo()
    {
        var conversacion = CrearConversacionWhatsAppConVentanaCerrada();
        AgregarBuzonHabilitado();

        var resultado = await CrearHandler().Handle(
            new MigrarConversacionACorreoCommand(conversacion.Id, "cliente@ejemplo.com", "<p>Seguimos por aquí.</p>"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conversacion.Mensajes.Should().Contain(m =>
            m.Direccion == DireccionMensaje.Saliente && m.Canal == CanalConversacion.Correo && m.CuerpoHtml == "<p>Seguimos por aquí.</p>");
        conversacion.Participantes.Should().Contain(p => p.Email == "cliente@ejemplo.com" && p.Rol == RolParticipante.Para);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Con_ventana_abierta_devuelve_fallo_y_no_persiste_nada()
    {
        var conexionWhatsApp = new ConexionIntegracion("+34686543364", "Línea", proveedor: ProveedorIntegracion.WhatsApp);
        var conversacion = Conversacion.CrearWhatsApp(Telefono, conexionWhatsApp.Id, null, Guid.NewGuid());
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, Telefono, "Hola", DateTime.UtcNow.AddHours(-1));
        _conversaciones.Agregar(conversacion);
        AgregarBuzonHabilitado();

        var resultado = await CrearHandler().Handle(
            new MigrarConversacionACorreoCommand(conversacion.Id, "cliente@ejemplo.com", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.VentanaAbierta");
        conversacion.Mensajes.Should().HaveCount(1);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_buzon_de_correo_conectado_devuelve_fallo()
    {
        var conversacion = CrearConversacionWhatsAppConVentanaCerrada();

        var resultado = await CrearHandler().Handle(
            new MigrarConversacionACorreoCommand(conversacion.Id, "cliente@ejemplo.com", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.SinBuzonConectado");
    }

    [Fact]
    public async Task Sobre_una_conversacion_de_correo_devuelve_fallo()
    {
        var conversacion = new Conversacion("Consulta por correo", clienteId: Guid.NewGuid());
        _conversaciones.Agregar(conversacion);
        AgregarBuzonHabilitado();

        var resultado = await CrearHandler().Handle(
            new MigrarConversacionACorreoCommand(conversacion.Id, "cliente@ejemplo.com", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.CanalIncorrecto");
    }

    [Fact]
    public async Task Prefiere_el_buzon_dedicado_al_cliente_de_la_conversacion_sobre_el_del_tenant()
    {
        var conexionWhatsApp = new ConexionIntegracion("+34686543364", "Línea", proveedor: ProveedorIntegracion.WhatsApp);
        var clienteId = Guid.NewGuid();
        var conversacion = Conversacion.CrearWhatsApp(Telefono, conexionWhatsApp.Id, clienteId, Guid.NewGuid());
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, Telefono, "Hola", DateTime.UtcNow.AddHours(-30));
        _conversaciones.Agregar(conversacion);
        AgregarBuzonHabilitado(); // buzón general del tenant
        var buzonDelCliente = AgregarBuzonHabilitado(clienteId);

        var resultado = await CrearHandler().Handle(
            new MigrarConversacionACorreoCommand(conversacion.Id, "cliente@ejemplo.com", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conversacion.Mensajes.Should().Contain(m => m.Remitente == buzonDelCliente.BuzonEmail);
    }
}
