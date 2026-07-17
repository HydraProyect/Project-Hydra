using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Visitas;

public class VisitaTests
{
    private static readonly Guid CentroIdValido = Guid.NewGuid();

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
}
