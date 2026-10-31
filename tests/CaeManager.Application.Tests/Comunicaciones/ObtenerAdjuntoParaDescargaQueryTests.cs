using CaeManager.Application.Comunicaciones.Queries.ObtenerAdjuntoParaDescarga;
using CaeManager.Application.Tests.Auditoria;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// SEG-9: el endpoint de adjuntos solo exige sesión, así que quién se lleva un
/// adjunto de un hilo de triage lo decide esta consulta, y cada entrega deja
/// rastro antes de producirse (ASVS 4.0.3 V4.1.3, V4.2.1, V7.1.3 y V7.2.2).
/// </summary>
public class ObtenerAdjuntoParaDescargaQueryTests
{
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task Un_rol_de_gestion_descarga_el_adjunto_de_triage_y_queda_registrado(string rol)
    {
        var (contexto, adjunto) = Escenario(new Conversacion("Sin clasificar"));
        var registro = new RegistroAccesoDatoSensibleFalso();

        var resultado = await Handler(contexto, rol, registro).Handle(new ObtenerAdjuntoParaDescargaQuery(adjunto.Id), CancellationToken.None);

        resultado.Should().NotBeNull();
        registro.Registrados.Should().Equal((nameof(AdjuntoMensaje), adjunto.Id));
    }

    [Theory]
    [InlineData("Cliente")]
    [InlineData("RolQueTodaviaNoExiste")]
    [InlineData(null)]
    public async Task Fuera_de_la_gestion_el_adjunto_de_triage_no_se_entrega_ni_se_registra(string? rol)
    {
        var (contexto, adjunto) = Escenario(new Conversacion("Sin clasificar"));
        var registro = new RegistroAccesoDatoSensibleFalso();

        var resultado = await Handler(contexto, rol, registro).Handle(new ObtenerAdjuntoParaDescargaQuery(adjunto.Id), CancellationToken.None);

        resultado.Should().BeNull();
        registro.Registrados.Should().BeEmpty("sin entrega no hay acceso que registrar");
    }

    [Fact]
    public async Task Un_hilo_anclado_a_un_Cliente_se_sigue_acotando_por_cartera_y_no_por_rol()
    {
        var clienteId = Guid.NewGuid();
        var (contexto, adjunto) = Escenario(new Conversacion("Con Cliente", clienteId));
        var registro = new RegistroAccesoDatoSensibleFalso();
        var handler = new ObtenerAdjuntoParaDescargaQueryHandler(
            contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [clienteId]),
            new CurrentUserServiceFalso(rol: "Cliente"), registro);

        var resultado = await handler.Handle(new ObtenerAdjuntoParaDescargaQuery(adjunto.Id), CancellationToken.None);

        resultado.Should().NotBeNull();
        registro.Registrados.Should().Equal((nameof(AdjuntoMensaje), adjunto.Id));
    }

    [Fact]
    public async Task Un_hilo_de_un_Cliente_fuera_de_cartera_no_se_entrega_ni_se_registra()
    {
        var (contexto, adjunto) = Escenario(new Conversacion("Con Cliente", Guid.NewGuid()));
        var registro = new RegistroAccesoDatoSensibleFalso();
        var handler = new ObtenerAdjuntoParaDescargaQueryHandler(
            contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: []),
            new CurrentUserServiceFalso(rol: "Administrador"), registro);

        (await handler.Handle(new ObtenerAdjuntoParaDescargaQuery(adjunto.Id), CancellationToken.None))
            .Should().BeNull();
        registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_registro_posible_el_adjunto_no_se_entrega()
    {
        var (contexto, adjunto) = Escenario(new Conversacion("Sin clasificar"));
        var handler = Handler(contexto, "GestorCae", new RegistroAccesoDatoSensibleFalso(falla: true));

        var accion = () => handler.Handle(new ObtenerAdjuntoParaDescargaQuery(adjunto.Id), CancellationToken.None);

        await accion.Should().ThrowAsync<InvalidOperationException>();
    }

    private static ObtenerAdjuntoParaDescargaQueryHandler Handler(
        ComunicacionesQueryContextFalso contexto, string? rol, RegistroAccesoDatoSensibleFalso registro) =>
        new(contexto, new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(rol: rol), registro);

    private static (ComunicacionesQueryContextFalso Contexto, AdjuntoMensaje Adjunto) Escenario(Conversacion conversacion)
    {
        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "remitente@ejemplo.com", "Adjunto");
        var adjunto = new AdjuntoMensaje(mensaje.Id, "certificado.pdf", "application/pdf", 1024, "adjuntos/certificado.pdf");
        var contexto = new ComunicacionesQueryContextFalso();
        contexto.ListaConversaciones.Add(conversacion);
        contexto.ListaMensajes.Add(mensaje);
        contexto.ListaAdjuntosMensaje.Add(adjunto);
        return (contexto, adjunto);
    }
}
