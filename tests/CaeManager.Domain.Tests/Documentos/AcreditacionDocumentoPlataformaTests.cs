using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (b) — bloque Acreditación por plataforma destino (MVP1).</summary>
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
    public void DevolverAPendienteDeSubir_solo_desde_subida_o_validada_y_borra_la_vigencia_confirmada()
    {
        var validada = new AcreditacionDocumentoPlataforma(Guid.NewGuid(), Guid.NewGuid());
        validada.MarcarAceptada(VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 1, 1)));
        validada.DevolverAPendienteDeSubir();
        validada.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        validada.EstadoVigencia.Should().Be(EstadoVigenciaEnPlataforma.SinConfirmar);

        var rechazada = new AcreditacionDocumentoPlataforma(Guid.NewGuid(), Guid.NewGuid());
        rechazada.Rechazar(CausaRechazoAcreditacion.Ilegible, "Firma ilegible", DateTime.UtcNow);
        rechazada.PuedeDevolverAPendienteDeSubir.Should().BeFalse("una rechazada sale con una versión corregida");
        ((Action)rechazada.DevolverAPendienteDeSubir).Should().Throw<InvalidOperationException>();

        var noExigida = new AcreditacionDocumentoPlataforma(Guid.NewGuid(), Guid.NewGuid());
        noExigida.MarcarNoRequerida();
        noExigida.PuedeDevolverAPendienteDeSubir.Should().BeFalse();
    }

    [Fact]
    public void Marcar_no_requerida_desde_validada_con_una_fecha_vencida_borra_la_vigencia_confirmada()
    {
        var acreditacion = CrearAcreditacion();
        acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(new DateOnly(2020, 1, 1)));
        acreditacion.Vigencia.EstaSinConfirmar.Should().BeFalse("el escenario parte de una fecha confirmada");

        acreditacion.MarcarNoRequerida();

        acreditacion.Estado.Should().Be(EstadoAcreditacion.NoRequerida);
        acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue("un documento que no se pide no pone el Centro en rojo");
        acreditacion.FechaVencimientoEnPlataforma.Should().BeNull();
    }

    [Fact]
    public void Una_no_requerida_no_admite_vigencia_ni_por_anotacion_ni_por_deshacer()
    {
        var acreditacion = CrearAcreditacion();
        acreditacion.MarcarNoRequerida();
        var pasada = VigenciaEnPlataforma.VenceEl(new DateOnly(2020, 1, 1));

        acreditacion.PuedeConfirmarVigencia.Should().BeFalse();
        ((Action)(() => acreditacion.ConfirmarVigencia(pasada))).Should().Throw<InvalidOperationException>();
        acreditacion.PuedeDeshacerAnotacion(EstadoAcreditacion.NoRequerida, VigenciaEnPlataforma.SinConfirmar).Should()
            .BeTrue("control positivo: sin vigencia sí");
        acreditacion.DeshacerAnotacion(EstadoAcreditacion.NoRequerida, pasada);
        acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue("deshacer hacia No requerida no restaura una fecha");

        var validada = CrearAcreditacion();
        validada.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar);
        validada.PuedeConfirmarVigencia.Should().BeTrue("control positivo");
        validada.ConfirmarVigencia(pasada);
        validada.FechaVencimientoEnPlataforma.Should().Be(new DateOnly(2020, 1, 1));
    }

    /// <summary>
    /// Fila anterior a la invariante «No requerida implica vigencia Sin confirmar»: antes se podía anotar una vigencia sobre
    /// una No requerida, y esas filas siguen en la base. Se fabrica a mano porque el dominio ya no deja llegar a ella.
    /// </summary>
    private static AcreditacionDocumentoPlataforma NoRequeridaHistoricaConVigencia(VigenciaEnPlataforma vigencia)
    {
        var acreditacion = CrearAcreditacion();
        acreditacion.MarcarAceptada(vigencia);
        typeof(AcreditacionDocumentoPlataforma).GetProperty(nameof(AcreditacionDocumentoPlataforma.Estado))!
            .SetValue(acreditacion, EstadoAcreditacion.NoRequerida);
        return acreditacion;
    }

    [Fact]
    public void Deshacer_un_cambio_a_mano_sobre_una_no_requerida_historica_no_le_devuelve_su_fecha()
    {
        var pasada = VigenciaEnPlataforma.VenceEl(new DateOnly(2020, 1, 1));
        var acreditacion = NoRequeridaHistoricaConVigencia(pasada);
        var cambio = acreditacion.CambiarEstadoAMano(
            EstadoAcreditacion.Subida, new DateOnly(2026, 11, 10), null, null, Guid.NewGuid(), DateTime.UtcNow);
        cambio.FechaVencimientoAnterior.Should().Be(new DateOnly(2020, 1, 1), "precondición: el cambio guardó la fecha histórica");

        acreditacion.DeshacerCambioEstado(cambio.Id, DateTime.UtcNow);

        acreditacion.Estado.Should().Be(EstadoAcreditacion.NoRequerida);
        acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue("una fecha vencida de un documento que no se pide no pone el Centro en rojo");
    }

    [Fact]
    public void Un_segundo_rechazo_renueva_la_version_aunque_no_cambie_ninguna_columna_de_la_raiz()
    {
        var acreditacion = CrearAcreditacion();
        acreditacion.Rechazar(CausaRechazoAcreditacion.Ilegible, "Firma ilegible", DateTime.UtcNow);
        var antes = acreditacion.Version;

        acreditacion.Rechazar(CausaRechazoAcreditacion.Otro, "Falta la firma del médico", DateTime.UtcNow);

        acreditacion.Version.Should().NotBe(antes, "una pantalla abierta antes del segundo rechazo no lo puede pisar");
        acreditacion.HistorialRechazos.Should().HaveCount(2);
    }

    [Fact]
    public void Deshacer_un_cambio_a_mano_hacia_validada_si_devuelve_su_fecha()
    {
        // Control positivo: la excepción es solo para No requerida.
        var acreditacion = CrearAcreditacion();
        var vigencia = VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 1, 1));
        acreditacion.MarcarAceptada(vigencia);
        var cambio = acreditacion.CambiarEstadoAMano(
            EstadoAcreditacion.Subida, new DateOnly(2026, 11, 10), null, null, Guid.NewGuid(), DateTime.UtcNow);

        acreditacion.DeshacerCambioEstado(cambio.Id, DateTime.UtcNow);

        acreditacion.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        acreditacion.Vigencia.Should().Be(vigencia);
    }

    [Fact]
    public void Una_no_requerida_historica_con_fecha_no_admite_otra_y_deshacer_una_validacion_la_deja_sin_fecha()
    {
        var pasada = VigenciaEnPlataforma.VenceEl(new DateOnly(2020, 1, 1));
        var acreditacion = NoRequeridaHistoricaConVigencia(pasada);

        acreditacion.PuedeConfirmarVigencia.Should().BeFalse("la lectura no se rompe: es una No requerida como las demás");
        acreditacion.Vigencia.Should().Be(pasada, "se lee sin lanzar");

        // «Marcar validado» desde la Gestión y su «Deshacer» (RestaurarAnotacionAcreditacion), con el previo histórico.
        acreditacion.MarcarAceptada(VigenciaEnPlataforma.NoVenceAqui);
        acreditacion.PuedeDeshacerAnotacion(EstadoAcreditacion.NoRequerida, pasada).Should().BeTrue("deshacer no queda bloqueado");
        acreditacion.DeshacerAnotacion(EstadoAcreditacion.NoRequerida, pasada);

        acreditacion.Estado.Should().Be(EstadoAcreditacion.NoRequerida);
        acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue();
    }

    [Fact]
    public void Volver_de_validada_a_enviada_borra_la_vigencia_confirmada()
    {
        var acreditacion = CrearAcreditacion();
        acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(new DateOnly(2026, 1, 1)));

        acreditacion.MarcarSubida();

        acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue("una vigencia vieja contaría como vencida en plataforma");
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

    [Fact]
    public void ReiniciarPorRenovacionDocumento_renueva_la_version_aunque_ya_estuviera_pendiente_y_sin_vigencia()
    {
        // Codex r2 (ALTO): con los mismos valores, EF no la da por modificada y el interceptor no renovaría la versión.
        var acreditacion = CrearAcreditacion();
        var vista = acreditacion.Version;

        acreditacion.ReiniciarPorRenovacionDocumento();

        acreditacion.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir, "precondición: el estado no cambia");
        acreditacion.Vigencia.EstaSinConfirmar.Should().BeTrue("precondición: la vigencia tampoco");
        acreditacion.Version.Should().NotBe(vista);
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
