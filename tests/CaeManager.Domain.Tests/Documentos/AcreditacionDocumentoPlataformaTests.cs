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
            case nameof(AcreditacionDocumentoPlataforma.MarcarAceptada): acreditacion.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar); break;
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
    // --- Vigencia en la plataforma ---------------------------------------

    [Fact]
    public void Nace_con_la_vigencia_sin_confirmar()
    {
        var acreditacion = CrearAcreditacion();

        acreditacion.Vigencia.Should().Be(VigenciaEnPlataforma.SinConfirmar);
        acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue();
    }

    [Fact]
    public void Aceptar_anota_la_vigencia_que_confirma_el_Gestor()
    {
        var acreditacion = CrearAcreditacion();
        var vence = new DateOnly(2027, 2, 1);

        acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(vence));

        acreditacion.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        acreditacion.Vigencia.FechaVencimiento.Should().Be(vence);
    }

    [Fact]
    public void Aceptar_sin_saber_la_vigencia_la_deja_sin_confirmar_no_sin_caducidad()
    {
        var acreditacion = CrearAcreditacion();

        acreditacion.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);

        acreditacion.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue();
        acreditacion.Vigencia.Should().NotBe(VigenciaEnPlataforma.NoVenceAqui);
    }

    [Fact]
    public void Rechazar_borra_la_vigencia_confirmada()
    {
        // Lo confirmado se refería a un documento que esta plataforma ya no
        // acepta. Conservarlo dejaría una fecha respaldando algo no acreditado.
        var acreditacion = CrearAcreditacion();
        acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 2, 1)));

        acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "No se lee el sello.", DateTime.UtcNow);

        acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue();
        acreditacion.Vigencia.FechaVencimiento.Should().BeNull();
    }

    [Fact]
    public void Renovar_el_documento_no_hereda_la_vigencia_de_la_version_anterior()
    {
        var acreditacion = CrearAcreditacion();
        acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 2, 1)));

        acreditacion.ReiniciarPorRenovacionDocumento();

        acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue();
    }

    [Fact]
    public void La_vigencia_se_puede_corregir_sin_tocar_el_estado()
    {
        // El Gestor entra en la plataforma más veces que una, y la primera vez
        // puede no haber mirado la fecha.
        var acreditacion = CrearAcreditacion();
        acreditacion.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);

        acreditacion.ConfirmarVigencia(VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 5, 5)));

        acreditacion.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        acreditacion.Vigencia.FechaVencimiento.Should().Be(new DateOnly(2027, 5, 5));
    }
}
