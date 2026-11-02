using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comunicaciones;

public class AdjuntoMensajeTests
{
    [Fact]
    public void Mensaje_agrega_un_adjunto_valido()
    {
        var mensaje = new Mensaje(Guid.NewGuid(), DireccionMensaje.Saliente, CanalConversacion.Correo, "gestor@hydra.local", "<p>Adjunto el formato.</p>", DateTime.UtcNow);

        var adjunto = mensaje.AgregarAdjunto("formato-centro.pdf", "application/pdf", 12_345, "storage-key-1");

        mensaje.Adjuntos.Should().ContainSingle().Which.Should().BeSameAs(adjunto);
        adjunto.MensajeId.Should().Be(mensaje.Id);
        adjunto.NombreArchivo.Should().Be("formato-centro.pdf");
        adjunto.TipoContenido.Should().Be("application/pdf");
        adjunto.TamanoBytes.Should().Be(12_345);
        adjunto.ArchivoUrl.Should().Be("storage-key-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rechaza_un_adjunto_sin_nombre_de_archivo(string nombreInvalido)
    {
        var accion = () => new AdjuntoMensaje(Guid.NewGuid(), nombreInvalido, "application/pdf", 100, "storage-key-1");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_adjunto_sin_archivo_guardado()
    {
        var accion = () => new AdjuntoMensaje(Guid.NewGuid(), "formato.pdf", "application/pdf", 100, "");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_tamano_negativo()
    {
        var accion = () => new AdjuntoMensaje(Guid.NewGuid(), "formato.pdf", "application/pdf", -1, "storage-key-1");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_pertenecer_a_un_mensaje_vacio()
    {
        var accion = () => new AdjuntoMensaje(Guid.Empty, "formato.pdf", "application/pdf", 100, "storage-key-1");

        accion.Should().Throw<ArgumentException>();
    }

    private static AdjuntoMensaje Adjunto() => new(Guid.NewGuid(), "aptitud.pdf", "application/pdf", 10, "clave");

    [Fact]
    public void Nace_pendiente_y_sin_destino()
    {
        var adjunto = Adjunto();

        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Pendiente);
        adjunto.DestinoTrabajadorId.Should().BeNull();
        adjunto.DestinoEmpresaId.Should().BeNull();
    }

    [Fact]
    public void El_destino_es_un_Trabajador_o_una_Empresa_y_se_puede_vaciar()
    {
        var adjunto = Adjunto();
        var trabajadorId = Guid.NewGuid();

        adjunto.FijarDestino(trabajadorId, null, Guid.NewGuid());
        adjunto.DestinoTrabajadorId.Should().Be(trabajadorId);

        adjunto.Invoking(a => a.FijarDestino(Guid.NewGuid(), Guid.NewGuid(), null)).Should().Throw<InvalidOperationException>();

        adjunto.FijarDestino(null, null, null);
        adjunto.DestinoTrabajadorId.Should().BeNull();
        adjunto.TipoDocumentoPropuestoId.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rechazar_exige_motivo(string motivo)
    {
        Adjunto().Invoking(a => a.Rechazar(motivo, Guid.NewGuid(), DateTime.UtcNow)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechazar_guarda_motivo_autor_y_fecha_y_reabrir_los_retira()
    {
        var adjunto = Adjunto();
        var usuarioId = Guid.NewGuid();
        var ahora = DateTime.UtcNow;

        adjunto.Rechazar("  Caducado  ", usuarioId, ahora);

        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Rechazado);
        adjunto.MotivoRechazo.Should().Be("Caducado");
        adjunto.ResueltoPorUsuarioId.Should().Be(usuarioId);
        adjunto.ResueltoEnUtc.Should().Be(ahora);

        adjunto.Reabrir();

        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Pendiente);
        adjunto.MotivoRechazo.Should().BeNull();
        adjunto.ResueltoPorUsuarioId.Should().BeNull();
        adjunto.ResueltoEnUtc.Should().BeNull();
    }

    [Fact]
    public void Un_adjunto_resuelto_no_cambia_de_destino_ni_se_resuelve_otra_vez()
    {
        var adjunto = Adjunto();
        adjunto.Descartar(Guid.NewGuid(), DateTime.UtcNow);

        adjunto.Invoking(a => a.FijarDestino(Guid.NewGuid(), null, null)).Should().Throw<InvalidOperationException>();
        adjunto.Invoking(a => a.Rechazar("Motivo", Guid.NewGuid(), DateTime.UtcNow)).Should().Throw<InvalidOperationException>();
        adjunto.Invoking(a => a.MarcarAsignado(Guid.NewGuid(), null, DateTime.UtcNow)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Un_adjunto_asignado_apunta_a_su_Documento_y_no_se_reabre()
    {
        var adjunto = Adjunto();
        var documentoId = Guid.NewGuid();

        adjunto.MarcarAsignado(documentoId, null, DateTime.UtcNow);

        adjunto.EstadoReparto.Should().Be(EstadoRepartoAdjunto.Asignado);
        adjunto.DocumentoId.Should().Be(documentoId);
        adjunto.Invoking(a => a.Reabrir()).Should().Throw<InvalidOperationException>();
    }
}