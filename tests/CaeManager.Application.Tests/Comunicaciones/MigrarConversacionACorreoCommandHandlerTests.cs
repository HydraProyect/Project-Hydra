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
    private const string Directorio = "11111111-2222-3333-4444-555555555555";

    private readonly ConversacionRepositorioFalso _conversaciones = new();
    private readonly IntegracionesQueryContextFalso _integraciones = new();
    private readonly ConexionIntegracionRepositorioFalso _conexiones = new();
    private readonly Microsoft365GraphClientFalso _graphClient = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly RegistroExportacionFalso _registro = new();

    private MigrarConversacionACorreoCommandHandler CrearHandler()
    {
        var credencialRepositorio = new CredencialIntegracionRepositorioFalso();
        foreach (var conexion in _conexiones.Conexiones)
            credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, "refresh-token"));

        var accesoGraph = new AccesoGraphService(credencialRepositorio, _graphClient);
        return new MigrarConversacionACorreoCommandHandler(
            _conversaciones, new ComunicacionesQueryContextFalso(), _integraciones, _conexiones, new AlcanceDatosServiceFalso(), _graphClient, accesoGraph,
            new ResolucionParticipanteConversacionServiceFalso(), _unitOfWork, _registro);
    }

    private ConexionIntegracion AgregarBuzonHabilitado(Guid? clienteId = null)
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE", clienteId).Verificada();
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
    public async Task La_migracion_deja_una_fila_de_salida_con_el_destinatario_enmascarado()
    {
        var conversacion = CrearConversacionWhatsAppConVentanaCerrada();
        AgregarBuzonHabilitado();

        await CrearHandler().Handle(
            new MigrarConversacionACorreoCommand(conversacion.Id, "contacto@cliente.com", "<p>Hola</p>"), CancellationToken.None);

        var fila = _registro.Filas.Should().ContainSingle().Subject;
        fila.Criterios["origen"].Should().Be("MigracionACorreo");
        fila.Criterios["destinatarios"].Should().Be("c***@cliente.com");
    }

    [Fact]
    public async Task Si_la_conexion_dejo_de_estar_operativa_en_la_base_antes_del_envio_no_migra()
    {
        var conversacion = CrearConversacionWhatsAppConVentanaCerrada();
        var conexion = AgregarBuzonHabilitado();
        _conexiones.NoOperativasEnBase.Add(conexion.Id);

        var resultado = await CrearHandler().Handle(
            new MigrarConversacionACorreoCommand(conversacion.Id, "contacto@tenant.com", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(CaeManager.Application.Integraciones.ErroresBuzonSinVerificar.Codigo);
        _graphClient.VecesEnviado.Should().Be(0);
        conversacion.Mensajes.Should().NotContain(m => m.Canal == CanalConversacion.Correo);
    }

    /// <summary>
    /// Dos buzones compartidos del Tenant: el más antiguo sin verificar no tapa al verificado. El desempate por
    /// antigüedad solo decide entre los que están en la misma situación.
    /// </summary>
    [Fact]
    public async Task Un_compartido_antiguo_sin_verificar_no_tapa_a_uno_verificado()
    {
        var conversacion = CrearConversacionWhatsAppConVentanaCerrada();
        var antiguo = ConexionIntegracion.CrearBuzonCompartido("antiguo@operador.test", "Buzón antiguo", Directorio, Guid.NewGuid());
        antiguo.FijarIdentidadBuzon("inbox-antiguo");
        typeof(ConexionIntegracion).GetProperty(nameof(ConexionIntegracion.FechaConectadaUtc))!
            .SetValue(antiguo, DateTime.UtcNow.AddDays(-30));
        var verificado = ConexionIntegracion.CrearBuzonCompartido("cae@operador.test", "Buzón CAE", Directorio, Guid.NewGuid()).Verificada();
        foreach (var conexion in new[] { antiguo, verificado })
        {
            _conexiones.Agregar(conexion);
            _integraciones.ConexionesLista.Add(conexion);
        }

        var resultado = await CrearHandler().Handle(
            new MigrarConversacionACorreoCommand(conversacion.Id, "contacto@tenant.com", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _graphClient.VecesEnviado.Should().Be(1);
        _graphClient.BuzonesUsados.Should().OnlyContain(b => b.Direccion == "cae@operador.test");
    }

    [Fact]
    public async Task Con_la_identidad_del_buzon_sin_verificar_no_migra_ni_deja_fila_de_salida()
    {
        var conversacion = CrearConversacionWhatsAppConVentanaCerrada();
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        _conexiones.Agregar(conexion);
        _integraciones.ConexionesLista.Add(conexion);

        var resultado = await CrearHandler().Handle(
            new MigrarConversacionACorreoCommand(conversacion.Id, "cliente@ejemplo.com", "<p>Hola</p>"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(CaeManager.Application.Integraciones.ErroresBuzonSinVerificar.Codigo);
        _registro.Filas.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
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

    /// <summary>Un buzón compartido no tiene credencial: la migración sale por su dirección y con el token de aplicación de su directorio.</summary>
    [Fact]
    public async Task Con_un_buzon_compartido_envia_por_su_direccion_y_con_el_token_de_aplicacion_del_directorio()
    {
        var conversacion = CrearConversacionWhatsAppConVentanaCerrada();
        var directorio = Guid.NewGuid().ToString("D");
        var compartido = ConexionIntegracion.CrearBuzonCompartido("piloto@tenant.com", "Buzón piloto", directorio, Guid.NewGuid()).Verificada();
        _conexiones.Agregar(compartido);
        _integraciones.ConexionesLista.Add(compartido);
        var handler = new MigrarConversacionACorreoCommandHandler(
            _conversaciones, new ComunicacionesQueryContextFalso(), _integraciones, _conexiones, new AlcanceDatosServiceFalso(), _graphClient,
            new AccesoGraphService(new CredencialIntegracionRepositorioFalso(), _graphClient),
            new ResolucionParticipanteConversacionServiceFalso(), _unitOfWork, _registro);

        var resultado = await handler.Handle(
            new MigrarConversacionACorreoCommand(conversacion.Id, "contacto@tenant.com", "<p>Seguimos por aquí.</p>"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _graphClient.BuzonesUsados.Should().ContainSingle().Which.Should().Be(new BuzonGraph("piloto@tenant.com", true));
        _graphClient.DirectoriosConTokenDeAplicacion.Should().Equal(directorio);
        _graphClient.VecesRefrescado.Should().Be(0, "no hay token delegado que refrescar");
        conversacion.Mensajes.Should().Contain(m => m.Canal == CanalConversacion.Correo && m.Remitente == "piloto@tenant.com");
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
