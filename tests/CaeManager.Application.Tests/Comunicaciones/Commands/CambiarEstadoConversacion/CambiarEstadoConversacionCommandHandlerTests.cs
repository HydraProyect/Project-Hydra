using CaeManager.Application.Comunicaciones.Commands.CambiarEstadoConversacion;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones.Commands.CambiarEstadoConversacion;

/// <summary>
/// El comando delega la máquina de estados en el dominio (decisión 2026-10-17, hueco 2 de Comunicaciones): lo que se prueba
/// aquí es que una transición no permitida vuelve como fallo legible, no se guarda, y que las válidas se guardan con su
/// instante y sin autor. La tabla completa de transiciones está en <c>ConversacionEstadosTests</c> (Domain.Tests).
/// </summary>
public class CambiarEstadoConversacionCommandHandlerTests
{
    [Fact]
    public async Task No_reabre_un_hilo_cerrado_que_ya_tiene_una_continuacion_activa()
    {
        var anterior = new Conversacion("Anterior");
        anterior.AsociarConexion(Guid.NewGuid(), "hilo");
        anterior.CambiarEstado(EstadoConversacion.Cerrada);
        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(anterior);
        repositorio.Agregar(Conversacion.ContinuarCorreo(anterior, "Continuación"));
        var guardado = new UnitOfWorkFalso();
        var handler = new CambiarEstadoConversacionCommandHandler(repositorio, new AlcanceDatosServiceFalso(), guardado);

        var resultado = await handler.Handle(new(anterior.Id, EstadoConversacion.Abierta), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Conversacion.TieneContinuacionActiva");
        anterior.Estado.Should().Be(EstadoConversacion.Cerrada);
        guardado.VecesGuardado.Should().Be(0);
    }

    private static (Conversacion Conversacion, CambiarEstadoConversacionCommandHandler Handler, UnitOfWorkFalso UnitOfWork) Preparar(
        EstadoConversacion estadoInicial = EstadoConversacion.Abierta)
    {
        var conversacion = new Conversacion("Documentación pendiente", Guid.NewGuid());
        if (estadoInicial != EstadoConversacion.Abierta)
            conversacion.CambiarEstado(estadoInicial).EsExitoso.Should().BeTrue();

        var repositorio = new ConversacionRepositorioFalso();
        repositorio.Agregar(conversacion);
        var unitOfWork = new UnitOfWorkFalso();
        return (conversacion, new CambiarEstadoConversacionCommandHandler(repositorio, new AlcanceDatosServiceFalso(), unitOfWork), unitOfWork);
    }

    [Theory]
    [InlineData(EstadoConversacion.Abierta, EstadoConversacion.EsperandoRespuestaExterna)]
    [InlineData(EstadoConversacion.Abierta, EstadoConversacion.EnEsperaDeTercero)]
    [InlineData(EstadoConversacion.EsperandoRespuestaExterna, EstadoConversacion.Resuelta)]
    [InlineData(EstadoConversacion.Resuelta, EstadoConversacion.Abierta)]
    public async Task Una_transicion_valida_se_aplica_se_guarda_y_deja_su_cambio(EstadoConversacion desde, EstadoConversacion hacia)
    {
        var (conversacion, handler, unitOfWork) = Preparar(desde);
        var cambiosAntes = conversacion.CambiosEstado.Count;

        var resultado = await handler.Handle(new CambiarEstadoConversacionCommand(conversacion.Id, hacia), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conversacion.Estado.Should().Be(hacia);
        conversacion.CambiosEstado.Should().HaveCount(cambiosAntes + 1);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData(EstadoConversacion.Cerrada, EstadoConversacion.EsperandoRespuestaExterna)]
    [InlineData(EstadoConversacion.Resuelta, EstadoConversacion.EnEsperaDeTercero)]
    [InlineData(EstadoConversacion.Abierta, EstadoConversacion.Abierta)]
    public async Task Una_transicion_no_permitida_vuelve_como_fallo_y_no_se_guarda(EstadoConversacion desde, EstadoConversacion hacia)
    {
        var (conversacion, handler, unitOfWork) = Preparar(desde);
        var cambiosAntes = conversacion.CambiosEstado.Count;

        var resultado = await handler.Handle(new CambiarEstadoConversacionCommand(conversacion.Id, hacia), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Conversacion.TransicionDeEstadoNoPermitida");
        conversacion.Estado.Should().Be(desde);
        conversacion.CambiosEstado.Should().HaveCount(cambiosAntes);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData(EstadoConversacion.Abierta, DireccionMensaje.Saliente, true)]
    [InlineData(EstadoConversacion.Abierta, DireccionMensaje.Entrante, false)]
    [InlineData(EstadoConversacion.EsperandoRespuestaExterna, DireccionMensaje.Entrante, true)]
    [InlineData(EstadoConversacion.EsperandoRespuestaExterna, DireccionMensaje.Saliente, true)]
    [InlineData(EstadoConversacion.EnEsperaDeTercero, DireccionMensaje.Saliente, false)]
    [InlineData(EstadoConversacion.Resuelta, DireccionMensaje.Saliente, false)]
    public void La_bandeja_Esperando_respuesta_externa_incluye_el_estado_explicito_y_mantiene_el_derivado(
        EstadoConversacion estado, DireccionMensaje ultimoMensaje, bool esperada)
    {
        // El estado explícito no depende de quién escribió el último mensaje; la forma derivada de antes (Abierta y último
        // mensaje saliente) se conserva. Esperar a un tercero no es esperar al Cliente.
        var dto = new ConversacionListaDto(
            Guid.NewGuid(), null, null, "Asunto", estado, null, "remitente@cliente.es", "preview",
            DateTime.UtcNow, 1, CanalConversacion.Correo, null, ultimoMensaje);

        dto.EsperandoCliente.Should().Be(esperada);
    }
}
