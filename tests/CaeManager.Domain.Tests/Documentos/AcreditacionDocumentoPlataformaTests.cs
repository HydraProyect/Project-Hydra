using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (b) — bloque Acreditación por plataforma destino (MVP1).</summary>
public class AcreditacionDocumentoPlataformaTests
{
    private static AcreditacionDocumentoPlataforma CrearAcreditacion() =>
        new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Nace_en_PendienteDeSubir()
    {
        var acreditacion = CrearAcreditacion();

        acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        acreditacion.HistorialRechazos.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_rechaza_documento_vacio()
    {
        var accion = () => new AcreditacionDocumentoPlataforma(Guid.Empty, Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_canal_vacio()
    {
        var accion = () => new AcreditacionDocumentoPlataforma(Guid.NewGuid(), Guid.Empty);

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(nameof(AcreditacionDocumentoPlataforma.MarcarSubida), EstadoAcreditacion.Subida)]
    [InlineData(nameof(AcreditacionDocumentoPlataforma.MarcarAceptada), EstadoAcreditacion.Aceptada)]
    [InlineData(nameof(AcreditacionDocumentoPlataforma.MarcarNoRequerida), EstadoAcreditacion.NoRequerida)]
    public void Las_transiciones_simples_cambian_el_estado(string metodo, EstadoAcreditacion esperado)
    {
        var acreditacion = CrearAcreditacion();

        switch (metodo)
        {
            case nameof(AcreditacionDocumentoPlataforma.MarcarSubida): acreditacion.MarcarSubida(); break;
            case nameof(AcreditacionDocumentoPlataforma.MarcarAceptada): acreditacion.MarcarAceptada(); break;
            case nameof(AcreditacionDocumentoPlataforma.MarcarNoRequerida): acreditacion.MarcarNoRequerida(); break;
        }

        acreditacion.Estado.Should().Be(esperado);
    }

    [Fact]
    public void Rechazar_exige_causa_tipificada_y_motivo_literal_y_los_registra_en_el_historial()
    {
        var acreditacion = CrearAcreditacion();
        var fecha = DateTime.UtcNow;

        acreditacion.Rechazar(CausaRechazoAcreditacion.DatosErroneos, "El NIF del trabajador no coincide con el certificado.", fecha);

        acreditacion.Estado.Should().Be(EstadoAcreditacion.Rechazada);
        acreditacion.HistorialRechazos.Should().ContainSingle();
        var rechazo = acreditacion.HistorialRechazos[0];
        rechazo.Causa.Should().Be(CausaRechazoAcreditacion.DatosErroneos);
        rechazo.MotivoLiteral.Should().Be("El NIF del trabajador no coincide con el certificado.");
        rechazo.FechaUtc.Should().Be(fecha);
    }

    [Fact]
    public void Rechazar_sin_motivo_literal_falla()
    {
        var acreditacion = CrearAcreditacion();

        var accion = () => acreditacion.Rechazar(CausaRechazoAcreditacion.Otro, "   ", DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
        acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
    }

    [Fact]
    public void Rechazos_sucesivos_se_acumulan_en_el_historial_sin_sobreescribir_los_anteriores()
    {
        var acreditacion = CrearAcreditacion();

        acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Escaneo borroso.", DateTime.UtcNow.AddDays(-10));
        acreditacion.MarcarSubida();
        acreditacion.Rechazar(CausaRechazoAcreditacion.FormatoNoAdmitido, "La plataforma exige PDF, se subió una imagen.", DateTime.UtcNow);

        acreditacion.Estado.Should().Be(EstadoAcreditacion.Rechazada);
        acreditacion.HistorialRechazos.Should().HaveCount(2);
        acreditacion.HistorialRechazos[0].Causa.Should().Be(CausaRechazoAcreditacion.Ilegible);
        acreditacion.HistorialRechazos[1].Causa.Should().Be(CausaRechazoAcreditacion.FormatoNoAdmitido);
    }

    [Fact]
    public void ReiniciarPorRenovacionDocumento_vuelve_a_PendienteDeSubir_sin_tocar_el_historial()
    {
        var acreditacion = CrearAcreditacion();
        acreditacion.Rechazar(CausaRechazoAcreditacion.CaducadoAlPresentar, "El certificado ya había caducado al subirlo.", DateTime.UtcNow);

        acreditacion.ReiniciarPorRenovacionDocumento();

        acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        acreditacion.HistorialRechazos.Should().ContainSingle("el historial de rechazos es un hecho pasado real, no se borra al renovar");
    }
}
