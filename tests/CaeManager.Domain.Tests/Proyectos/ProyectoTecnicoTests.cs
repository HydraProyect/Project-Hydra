using CaeManager.Domain.Proyectos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Proyectos;

public class ProyectoTecnicoTests
{
    private static readonly Guid ProyectoIdValido = Guid.NewGuid();
    private static readonly Guid TrabajadorIdValido = Guid.NewGuid();

    [Fact]
    public void Crea_una_asignacion_de_tecnico_valida()
    {
        var fechaAlta = new DateOnly(2026, 8, 1);

        var proyectoTecnico = new ProyectoTecnico(ProyectoIdValido, TrabajadorIdValido, fechaAlta);

        proyectoTecnico.ProyectoId.Should().Be(ProyectoIdValido);
        proyectoTecnico.TrabajadorId.Should().Be(TrabajadorIdValido);
        proyectoTecnico.FechaAlta.Should().Be(fechaAlta);
        proyectoTecnico.FechaBaja.Should().BeNull();
        proyectoTecnico.EstaActivo.Should().BeTrue();
    }

    [Fact]
    public void Rechaza_un_proyecto_vacio()
    {
        var accion = () => new ProyectoTecnico(Guid.Empty, TrabajadorIdValido, new DateOnly(2026, 8, 1));

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_trabajador_vacio()
    {
        var accion = () => new ProyectoTecnico(ProyectoIdValido, Guid.Empty, new DateOnly(2026, 8, 1));

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DarDeBaja_desactiva_la_asignacion()
    {
        var proyectoTecnico = new ProyectoTecnico(ProyectoIdValido, TrabajadorIdValido, new DateOnly(2026, 8, 1));

        proyectoTecnico.DarDeBaja(new DateOnly(2026, 9, 1));

        proyectoTecnico.EstaActivo.Should().BeFalse();
        proyectoTecnico.FechaBaja.Should().Be(new DateOnly(2026, 9, 1));
    }

    [Fact]
    public void DarDeBaja_rechaza_fecha_anterior_al_alta()
    {
        var proyectoTecnico = new ProyectoTecnico(ProyectoIdValido, TrabajadorIdValido, new DateOnly(2026, 8, 10));

        var accion = () => proyectoTecnico.DarDeBaja(new DateOnly(2026, 8, 1));

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReactivarAlta_vuelve_a_dejar_la_asignacion_activa()
    {
        var proyectoTecnico = new ProyectoTecnico(ProyectoIdValido, TrabajadorIdValido, new DateOnly(2026, 8, 1));
        proyectoTecnico.DarDeBaja(new DateOnly(2026, 9, 1));

        proyectoTecnico.ReactivarAlta();

        proyectoTecnico.EstaActivo.Should().BeTrue();
        proyectoTecnico.FechaBaja.Should().BeNull();
        proyectoTecnico.MotivoBaja.Should().BeNull("un participante activo no tiene motivo de baja");
    }

    /// <summary>D-4 (#2005): la baja guarda su motivo; sin indicarlo, es una desasignación a mano.</summary>
    [Theory]
    [InlineData(null, MotivoBajaProyectoTecnico.Desasignado)]
    [InlineData(MotivoBajaProyectoTecnico.FinDelEvento, MotivoBajaProyectoTecnico.FinDelEvento)]
    public void DarDeBaja_guarda_su_motivo(MotivoBajaProyectoTecnico? indicado, MotivoBajaProyectoTecnico esperado)
    {
        var proyectoTecnico = new ProyectoTecnico(ProyectoIdValido, TrabajadorIdValido, new DateOnly(2026, 8, 1));

        if (indicado is { } motivo)
            proyectoTecnico.DarDeBaja(new DateOnly(2026, 9, 1), motivo);
        else
            proyectoTecnico.DarDeBaja(new DateOnly(2026, 9, 1));

        proyectoTecnico.MotivoBaja.Should().Be(esperado);
    }

    [Fact]
    public void DarDeBaja_rechaza_un_motivo_desconocido()
    {
        var proyectoTecnico = new ProyectoTecnico(ProyectoIdValido, TrabajadorIdValido, new DateOnly(2026, 8, 1));

        var accion = () => proyectoTecnico.DarDeBaja(new DateOnly(2026, 9, 1), (MotivoBajaProyectoTecnico)99);

        accion.Should().Throw<ArgumentOutOfRangeException>();
        proyectoTecnico.EstaActivo.Should().BeTrue();
    }
}
