using CaeManager.Application.Comunicaciones.Commands.ResponderConversacionWhatsApp;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

public class ResponderConversacionWhatsAppCommandHandlerTests
{
    private const string Telefono = "+34600111222";

    private readonly ConversacionRepositorioFalso _conversaciones = new();
    private readonly ConexionIntegracionRepositorioFalso _conexiones = new();
    private readonly LineaWhatsAppRepositorioFalso _lineas = new();
    private readonly WhatsAppCloudApiClientFalso _whatsApp = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    private ResponderConversacionWhatsAppCommandHandler CrearHandler(IUnitOfWork? unitOfWork = null) =>
        new(_conversaciones, _conexiones, _lineas, new AlcanceDatosServiceFalso(), _whatsApp, unitOfWork ?? _unitOfWork,
            NullLogger<ResponderConversacionWhatsAppCommandHandler>.Instance);

    private Conversacion CrearConversacionConLinea(DateTime? ultimoEntranteUtc)
    {
        var conexion = new ConexionIntegracion("+34686543364", "Línea", proveedor: ProveedorIntegracion.WhatsApp);
        _conexiones.Agregar(conexion);
        _lineas.Agregar(new LineaWhatsApp(
            conexion.Id, "111222333", "waba-1", "+34686543364", "token", ModoAsignacionLinea.GestorFijo, Guid.NewGuid()));

        var conversacion = Conversacion.CrearWhatsApp(Telefono, conexion.Id, null, Guid.NewGuid());
        if (ultimoEntranteUtc is { } fecha)
            conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, Telefono, "Hola", fecha, "wamid.in");
        _conversaciones.Agregar(conversacion);
        return conversacion;
    }

    [Fact]
    public async Task Envia_y_persiste_el_saliente_con_su_wamid_dentro_de_la_ventana()
    {
        var conversacion = CrearConversacionConLinea(DateTime.UtcNow.AddHours(-1));

        var resultado = await CrearHandler().Handle(
            new ResponderConversacionWhatsAppCommand(conversacion.Id, "Te lo miro ahora"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _whatsApp.EnviosRealizados.Should().ContainSingle().Which.Telefono.Should().Be(Telefono);

        var saliente = conversacion.Mensajes.Single(m => m.Direccion == DireccionMensaje.Saliente);
        saliente.MensajeExternoId.Should().StartWith("wamid.enviado-");
        saliente.EstadoEntrega.Should().Be(EstadoEntregaMensaje.Enviado);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Bloquea_el_envio_fuera_de_la_ventana_de_24_horas_sin_llamar_a_meta()
    {
        var conversacion = CrearConversacionConLinea(DateTime.UtcNow.AddHours(-25));

        var resultado = await CrearHandler().Handle(
            new ResponderConversacionWhatsAppCommand(conversacion.Id, "Llego tarde"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConversacionWhatsApp.FueraDeVentana");
        _whatsApp.EnviosRealizados.Should().BeEmpty();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_fallo_de_meta_no_deja_mensaje_fantasma()
    {
        var conversacion = CrearConversacionConLinea(DateTime.UtcNow.AddHours(-1));
        _whatsApp.FallarEnvios = true;

        var resultado = await CrearHandler().Handle(
            new ResponderConversacionWhatsAppCommand(conversacion.Id, "Hola"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        conversacion.Mensajes.Should().NotContain(m => m.Direccion == DireccionMensaje.Saliente);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Rechaza_una_conversacion_que_no_es_de_whatsapp()
    {
        var conversacion = new Conversacion("Correo normal");
        _conversaciones.Agregar(conversacion);

        var resultado = await CrearHandler().Handle(
            new ResponderConversacionWhatsAppCommand(conversacion.Id, "Hola"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConversacionWhatsApp.CanalIncorrecto");
    }

    [Fact]
    public async Task Rechaza_el_envio_si_la_linea_esta_deshabilitada()
    {
        var conversacion = CrearConversacionConLinea(DateTime.UtcNow.AddHours(-1));
        _conexiones.Conexiones.Single().Deshabilitar();

        var resultado = await CrearHandler().Handle(
            new ResponderConversacionWhatsAppCommand(conversacion.Id, "Hola"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConversacionWhatsApp.LineaNoDisponible");
        _whatsApp.EnviosRealizados.Should().BeEmpty();
    }

    /// <summary>
    /// Auditoría módulo 6: si Meta ya aceptó el envío y el guardado local
    /// falla después, devolver un fallo invitaría a reintentar — y un
    /// reintento reenviaría el texto de verdad, esta vez duplicado.
    /// </summary>
    [Fact]
    public async Task Si_meta_acepto_el_envio_pero_el_guardado_local_falla_no_devuelve_fallo()
    {
        var conversacion = CrearConversacionConLinea(DateTime.UtcNow.AddHours(-1));

        var resultado = await CrearHandler(new UnitOfWorkQueFallaFalso()).Handle(
            new ResponderConversacionWhatsAppCommand(conversacion.Id, "Te lo miro ahora"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("el mensaje ya salió de verdad por WhatsApp — un fallo aquí llevaría a reenviarlo duplicado");
        _whatsApp.EnviosRealizados.Should().ContainSingle();
    }

    private sealed class UnitOfWorkQueFallaFalso : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Fallo simulado de guardado.");
    }
}
