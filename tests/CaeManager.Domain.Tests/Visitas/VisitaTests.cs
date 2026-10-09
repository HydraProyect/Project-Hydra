using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Visitas;

public class VisitaTests
{
    private static readonly Guid CentroIdValido = Guid.NewGuid();

    [Theory]
    [InlineData(false, null, OrigenVisita.Manual)]
    [InlineData(false, CanalConversacion.WhatsApp, OrigenVisita.Manual)]
    [InlineData(true, null, OrigenVisita.Correo)]
    [InlineData(true, CanalConversacion.Correo, OrigenVisita.Correo)]
    [InlineData(true, CanalConversacion.WhatsApp, OrigenVisita.WhatsApp)]
    public void El_origen_al_crear_sale_del_canal_del_mensaje_de_la_sugerencia(
        bool desdeSugerencia, CanalConversacion? canal, OrigenVisita esperado)
    {
        Visita.OrigenAlCrear(desdeSugerencia, canal).Should().Be(esperado);
    }

    [Fact]
    public void Manual_se_anade_al_final_del_enum_sin_renumerar_los_valores_persistidos()
    {
        ((int)OrigenVisita.Plataforma).Should().Be(0);
        ((int)OrigenVisita.Correo).Should().Be(1);
        ((int)OrigenVisita.WhatsApp).Should().Be(2);
        ((int)OrigenVisita.Manual).Should().Be(3);
    }

    [Fact]
    public void Crea_una_visita_valida()
    {
        var inicio = new DateOnly(2026, 8, 1);
        var fin = new DateOnly(2026, 8, 5);

        var visita = new Visita(CentroIdValido, inicio, fin, "Revisión anual");

        visita.CentroId.Should().Be(CentroIdValido);
        visita.FechaInicio.Should().Be(inicio);
        visita.FechaFin.Should().Be(fin);
        visita.Notas.Should().Be("Revisión anual");
        visita.NotificadoCliente.Should().BeFalse();
        visita.Origen.Should().Be(OrigenVisita.Plataforma);
    }

    [Fact]
    public void Origen_es_Correo_cuando_se_indica_explicitamente()
    {
        var visita = new Visita(CentroIdValido, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5), null, OrigenVisita.Correo);

        visita.Origen.Should().Be(OrigenVisita.Correo);
    }

    [Fact]
    public void Rechaza_un_centro_vacio()
    {
        var accion = () => new Visita(Guid.Empty, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5), null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_fecha_fin_anterior_a_fecha_inicio()
    {
        var accion = () => new Visita(CentroIdValido, new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 1), null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Permite_un_solo_dia_de_visita()
    {
        var dia = new DateOnly(2026, 8, 1);

        var visita = new Visita(CentroIdValido, dia, dia, null);

        visita.FechaInicio.Should().Be(dia);
        visita.FechaFin.Should().Be(dia);
    }

    [Theory]
    [InlineData(2026, 7, 17, true)]  // FechaFin (18) es futura respecto al "hoy" simulado
    [InlineData(2026, 7, 18, true)]  // FechaFin == hoy sigue activa
    [InlineData(2026, 7, 19, false)] // FechaFin ya pasó
    public void EstaActiva_depende_de_si_FechaFin_ya_paso(int anio, int mes, int dia, bool esperado)
    {
        var visita = new Visita(CentroIdValido, new DateOnly(2026, 7, 15), new DateOnly(2026, 7, 18), null);

        visita.EstaActiva(new DateOnly(anio, mes, dia)).Should().Be(esperado);
    }

    [Fact]
    public void Actualizar_cambia_fechas_y_notas()
    {
        var visita = new Visita(CentroIdValido, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5), "Original");

        visita.Actualizar(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 3), "Actualizada");

        visita.FechaInicio.Should().Be(new DateOnly(2026, 9, 1));
        visita.FechaFin.Should().Be(new DateOnly(2026, 9, 3));
        visita.Notas.Should().Be("Actualizada");
    }

    [Fact]
    public void Actualizar_rechaza_fecha_fin_anterior_a_fecha_inicio()
    {
        var visita = new Visita(CentroIdValido, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5), null);

        var accion = () => visita.Actualizar(new DateOnly(2026, 9, 5), new DateOnly(2026, 9, 1), null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarcarNotificadoCliente_cambia_el_estado()
    {
        var visita = new Visita(CentroIdValido, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5), null);

        visita.MarcarNotificadoCliente(true);
        visita.NotificadoCliente.Should().BeTrue();

        visita.MarcarNotificadoCliente(false);
        visita.NotificadoCliente.Should().BeFalse();
    }

    [Fact]
    public void Rechaza_notas_demasiado_largas()
    {
        var notasLargas = new string('a', Visita.LongitudMaximaNotas + 1);

        var accion = () => new Visita(CentroIdValido, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5), notasLargas);

        accion.Should().Throw<ArgumentException>();
    }

    // ---- Documentación gestionada (estado guardado, decisión del 2026-10-09)

    [Fact]
    public void Una_visita_nueva_esta_por_gestionar()
    {
        var visita = new Visita(CentroIdValido, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5), null);

        visita.DocumentacionGestionada.Should().BeFalse();
        visita.DocumentacionGestionadaEnUtc.Should().BeNull();
    }

    [Fact]
    public void MarcarDocumentacionGestionada_guarda_la_fecha_y_un_segundo_envio_la_actualiza()
    {
        var visita = new Visita(CentroIdValido, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5), null);
        var primera = new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc);
        var segunda = primera.AddDays(2);

        visita.MarcarDocumentacionGestionada(primera);
        visita.DocumentacionGestionadaEnUtc.Should().Be(primera);

        visita.MarcarDocumentacionGestionada(segunda);
        visita.DocumentacionGestionadaEnUtc.Should().Be(segunda);
        visita.DocumentacionGestionada.Should().BeTrue();
    }

    [Fact]
    public void Cambiar_los_trabajadores_borra_la_marca_y_renueva_la_version()
    {
        var visita = new Visita(CentroIdValido, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5), null);
        visita.MarcarDocumentacionGestionada(new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc));
        var versionAntes = visita.Version;

        visita.RegistrarCambioDeTrabajadores();

        visita.DocumentacionGestionada.Should().BeFalse("lo gestionado era para quienes entraban entonces");
        visita.DocumentacionGestionadaEnUtc.Should().BeNull();
        visita.Version.Should().NotBe(versionAntes);
    }

    [Fact]
    public void Cambiar_fechas_o_notas_no_borra_la_marca()
    {
        var visita = new Visita(CentroIdValido, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5), null);
        visita.MarcarDocumentacionGestionada(new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc));

        visita.Actualizar(new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 6), "otra nota");
        visita.MarcarNotificadoCliente(true);

        visita.DocumentacionGestionada.Should().BeTrue("«Avisada» y la edición de fechas son independientes de la marca");
    }

    [Fact]
    public void Una_visita_cancelada_no_se_marca_como_gestionada()
    {
        var visita = new Visita(CentroIdValido, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5), null);
        visita.Cancelar(DateTime.UtcNow, null);

        var accion = () => visita.MarcarDocumentacionGestionada(DateTime.UtcNow);

        accion.Should().Throw<InvalidOperationException>();
        visita.DocumentacionGestionada.Should().BeFalse();
    }

    /// <summary>
    /// El sello del expediente mide la antelación y es de un solo sentido; la marca de
    /// documentación gestionada es otro dato y sí se borra. Uno no arrastra al otro.
    /// </summary>
    [Fact]
    public void El_sello_del_expediente_y_la_marca_de_gestionada_son_independientes()
    {
        var visita = new Visita(CentroIdValido, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5), null);
        var solicitud = new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc);
        visita.RegistrarOrigenSolicitud(Guid.NewGuid(), solicitud);
        visita.MarcarExpedienteCompleto(solicitud.AddHours(3), new ResultadoAntelacion(100m, 100m, 0m, TramoAntelacion.Estandar, AtribucionUrgencia.SinUrgencia))
            .Should().BeTrue();

        visita.DocumentacionGestionada.Should().BeFalse("que deje de faltar papel no es haberlo enviado");

        visita.MarcarDocumentacionGestionada(solicitud.AddHours(4));
        visita.RegistrarCambioDeTrabajadores();

        visita.DocumentacionGestionada.Should().BeFalse();
        visita.FechaHoraExpedienteCompletoUtc.Should().Be(solicitud.AddHours(3), "el sello no se retira");
    }
}
