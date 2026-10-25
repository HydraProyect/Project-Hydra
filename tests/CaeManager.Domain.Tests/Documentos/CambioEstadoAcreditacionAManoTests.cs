using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// Centro 360, bloque 3 de la sesión del 21-10: el Gestor CAE anota a mano el estado del documento en la plataforma CAE
/// del Cliente, con fecha, motivo (obligatorio si es un rechazo), nota, usuario y momento, y «Deshacer» devuelve el estado anterior.
/// </summary>
public class CambioEstadoAcreditacionAManoTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 21);
    private static readonly Guid Usuario = Guid.NewGuid();

    private static AcreditacionDocumentoPlataforma Crear() => new(Guid.NewGuid(), Guid.NewGuid());

    [Theory]
    [InlineData(EstadoAcreditacion.PendienteDeSubir, EstadoAcreditacion.Subida)]
    [InlineData(EstadoAcreditacion.Subida, EstadoAcreditacion.Aceptada)]
    [InlineData(EstadoAcreditacion.Aceptada, EstadoAcreditacion.PendienteDeSubir)]
    [InlineData(EstadoAcreditacion.Rechazada, EstadoAcreditacion.Subida)]
    [InlineData(EstadoAcreditacion.Rechazada, EstadoAcreditacion.PendienteDeSubir)]
    [InlineData(EstadoAcreditacion.Subida, EstadoAcreditacion.Rechazada)]
    public void Cualquier_estado_lleva_a_cualquiera_de_los_cuatro(EstadoAcreditacion desde, EstadoAcreditacion hasta)
    {
        var acreditacion = Crear();
        Llevar(acreditacion, desde);

        var cambio = acreditacion.CambiarEstadoAMano(hasta, Hoy, hasta == EstadoAcreditacion.Rechazada ? "Falta la firma" : null, null, Usuario, DateTime.UtcNow);

        acreditacion.Estado.Should().Be(hasta);
        cambio.EstadoAnterior.Should().Be(desde);
        cambio.Estado.Should().Be(hasta);
    }

    [Fact]
    public void Guarda_fecha_nota_usuario_y_momento()
    {
        var acreditacion = Crear();
        var momento = new DateTime(2026, 10, 21, 9, 30, 0, DateTimeKind.Utc);

        var cambio = acreditacion.CambiarEstadoAMano(EstadoAcreditacion.Subida, Hoy, "ignorado", "  Solicitud 4711  ", Usuario, momento);

        cambio.Fecha.Should().Be(Hoy);
        cambio.Nota.Should().Be("Solicitud 4711");
        cambio.Motivo.Should().BeNull("el motivo solo describe un rechazo");
        cambio.UsuarioId.Should().Be(Usuario);
        cambio.MomentoUtc.Should().Be(momento);
        acreditacion.CambiosEstado.Should().ContainSingle().Which.Should().BeSameAs(cambio);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Rechazado_sin_motivo_no_cambia_nada(string? motivo)
    {
        var acreditacion = Crear();
        acreditacion.MarcarSubida();

        var accion = () => acreditacion.CambiarEstadoAMano(EstadoAcreditacion.Rechazada, Hoy, motivo, null, Usuario, DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
        acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        acreditacion.CambiosEstado.Should().BeEmpty();
        acreditacion.HistorialRechazos.Should().BeEmpty();
    }

    [Fact]
    public void Rechazado_con_motivo_entra_en_el_historial_de_rechazos()
    {
        var acreditacion = Crear();

        var cambio = acreditacion.CambiarEstadoAMano(EstadoAcreditacion.Rechazada, Hoy, " Falta la firma ", null, Usuario, DateTime.UtcNow);

        cambio.Motivo.Should().Be("Falta la firma");
        acreditacion.HistorialRechazos.Should().ContainSingle()
            .Which.Should().Match<RechazoAcreditacionDocumentoPlataforma>(r => r.MotivoLiteral == "Falta la firma" && r.Causa == CausaRechazoAcreditacion.Otro);
    }

    [Fact]
    public void No_requerida_no_se_anota_a_mano()
    {
        var accion = () => Crear().CambiarEstadoAMano(EstadoAcreditacion.NoRequerida, Hoy, null, null, Usuario, DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Deshacer_restaura_el_estado_y_la_vigencia_anteriores_y_marca_el_cambio()
    {
        var acreditacion = Crear();
        acreditacion.MarcarSubida();
        acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 3, 1)));
        acreditacion.CambiarEstadoAMano(EstadoAcreditacion.PendienteDeSubir, Hoy, null, null, Usuario, DateTime.UtcNow);
        acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue();
        var cambio = acreditacion.CambiosEstado.Single();

        acreditacion.DeshacerCambioEstado(cambio.Id, DateTime.UtcNow);

        acreditacion.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        acreditacion.Vigencia.FechaVencimiento.Should().Be(new DateOnly(2027, 3, 1));
        cambio.EstaDeshecho.Should().BeTrue();
        acreditacion.CambiosEstado.Should().ContainSingle("deshacer no borra el cambio: sigue en el Historial");
        ((IAccionAuditoriaPropia)acreditacion).ConsumirAccionAuditoria().Should().Be(RegistroAuditoria.AccionRestaurado);
    }

    [Fact]
    public void Deshacer_marcar_subido_desde_rechazado_vuelve_a_rechazado()
    {
        var acreditacion = Crear();
        acreditacion.CambiarEstadoAMano(EstadoAcreditacion.Rechazada, Hoy, "Ilegible", null, Usuario, DateTime.UtcNow);
        var subido = acreditacion.CambiarEstadoAMano(EstadoAcreditacion.Subida, Hoy, null, null, Usuario, DateTime.UtcNow.AddSeconds(1));

        acreditacion.DeshacerCambioEstado(subido.Id, DateTime.UtcNow);

        acreditacion.Estado.Should().Be(EstadoAcreditacion.Rechazada);
        acreditacion.HistorialRechazos.Should().ContainSingle();
    }

    [Fact]
    public void Solo_se_deshace_el_ultimo_cambio_y_si_nadie_ha_cambiado_el_estado_despues()
    {
        var acreditacion = Crear();
        var primero = acreditacion.CambiarEstadoAMano(EstadoAcreditacion.Subida, Hoy, null, null, Usuario, DateTime.UtcNow);
        var segundo = acreditacion.CambiarEstadoAMano(EstadoAcreditacion.Aceptada, Hoy, null, null, Usuario, DateTime.UtcNow.AddSeconds(1));

        acreditacion.PuedeDeshacerCambioEstado(primero.Id).Should().BeFalse("no es el último");
        acreditacion.PuedeDeshacerCambioEstado(segundo.Id).Should().BeTrue();

        acreditacion.ReiniciarPorRenovacionDocumento();

        acreditacion.PuedeDeshacerCambioEstado(segundo.Id).Should().BeFalse("otra vía cambió el estado después");
        var accion = () => acreditacion.DeshacerCambioEstado(segundo.Id, DateTime.UtcNow);
        accion.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Un_cambio_deshecho_no_se_deshace_dos_veces()
    {
        var acreditacion = Crear();
        var cambio = acreditacion.CambiarEstadoAMano(EstadoAcreditacion.Subida, Hoy, null, null, Usuario, DateTime.UtcNow);
        acreditacion.DeshacerCambioEstado(cambio.Id, DateTime.UtcNow);

        acreditacion.PuedeDeshacerCambioEstado(cambio.Id).Should().BeFalse();
    }

    private static void Llevar(AcreditacionDocumentoPlataforma acreditacion, EstadoAcreditacion estado)
    {
        switch (estado)
        {
            case EstadoAcreditacion.Subida: acreditacion.MarcarSubida(); break;
            case EstadoAcreditacion.Aceptada: acreditacion.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar); break;
            case EstadoAcreditacion.Rechazada: acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Ilegible", DateTime.UtcNow); break;
        }
    }

    [Fact]
    public void Un_cambio_de_Centro_360_lleva_ese_origen()
    {
        var cambio = Crear().CambiarEstadoAMano(EstadoAcreditacion.Subida, Hoy, null, null, Usuario, DateTime.UtcNow);

        cambio.Origen.Should().Be(OrigenCambioEstadoAcreditacion.Centro360);
    }

    [Fact]
    public void Lo_he_enviado_de_la_extension_deja_subida_con_un_cambio_de_origen_extension()
    {
        var acreditacion = Crear();
        acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 1, 1)));

        var cambio = acreditacion.ConfirmarEnvioDesdeExtension(Hoy, Usuario, DateTime.UtcNow);

        acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue("la plataforma lo revisa otra vez, igual que con «Marcar subido»");
        cambio.Origen.Should().Be(OrigenCambioEstadoAcreditacion.Extension);
        cambio.EstadoAnterior.Should().Be(EstadoAcreditacion.Aceptada);
        cambio.Estado.Should().Be(EstadoAcreditacion.Subida);
        cambio.Fecha.Should().Be(Hoy);
        cambio.UsuarioId.Should().Be(Usuario);
        acreditacion.CambiosEstado.Should().ContainSingle().Which.Should().BeSameAs(cambio);
        acreditacion.UltimoCambioEstadoAManoId.Should().Be(cambio.Id);
    }

    [Fact]
    public void Lo_he_enviado_se_deshace_y_devuelve_estado_y_vigencia_exactos()
    {
        var acreditacion = Crear();
        var vigencia = VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 1, 1));
        acreditacion.MarcarAceptada(vigencia);
        var cambio = acreditacion.ConfirmarEnvioDesdeExtension(Hoy, Usuario, DateTime.UtcNow);

        acreditacion.PuedeDeshacerCambioEstado(cambio.Id).Should().BeTrue();
        acreditacion.DeshacerCambioEstado(cambio.Id, DateTime.UtcNow);

        acreditacion.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        acreditacion.Vigencia.Should().Be(vigencia);
        cambio.EstaDeshecho.Should().BeTrue();
        acreditacion.PuedeDeshacerCambioEstado(cambio.Id).Should().BeFalse("un cambio deshecho no se deshace dos veces");
    }
}
