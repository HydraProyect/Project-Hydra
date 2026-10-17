using CaeManager.Domain.Gestiones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Gestiones;

public class GestionTests
{
    private static readonly DateTime Instante = new(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc);

    private static Gestion CrearGestion() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Una_gestion_nueva_esta_pendiente_y_sin_instante_de_completado()
    {
        var gestion = CrearGestion();

        gestion.Estado.Should().Be(EstadoGestion.Pendiente);
        gestion.CompletadaEnUtc.Should().BeNull();
    }

    [Fact]
    public void Completar_fija_el_estado_y_el_instante()
    {
        var gestion = CrearGestion();

        gestion.Completar(Instante);

        gestion.Estado.Should().Be(EstadoGestion.Completada);
        gestion.CompletadaEnUtc.Should().Be(Instante);
        gestion.CompletadaEnUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Completar_una_ya_completada_conserva_el_instante_original()
    {
        var gestion = CrearGestion();
        gestion.Completar(Instante);

        gestion.Completar(Instante.AddHours(3));

        gestion.CompletadaEnUtc.Should().Be(Instante);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Completar_rechaza_un_instante_que_no_esta_en_UTC(DateTimeKind tipo)
    {
        var gestion = CrearGestion();

        var accion = () => gestion.Completar(DateTime.SpecifyKind(Instante, tipo));

        accion.Should().Throw<ArgumentException>();
        gestion.Estado.Should().Be(EstadoGestion.Pendiente);
        gestion.CompletadaEnUtc.Should().BeNull();
    }

    [Fact]
    public void Reabrir_vuelve_a_pendiente_y_limpia_el_instante()
    {
        var gestion = CrearGestion();
        gestion.Completar(Instante);

        gestion.Reabrir();

        gestion.Estado.Should().Be(EstadoGestion.Pendiente);
        gestion.CompletadaEnUtc.Should().BeNull();
    }

    [Fact]
    public void Completar_tras_reabrir_registra_el_nuevo_instante()
    {
        var gestion = CrearGestion();
        gestion.Completar(Instante);
        gestion.Reabrir();

        gestion.Completar(Instante.AddDays(2));

        gestion.CompletadaEnUtc.Should().Be(Instante.AddDays(2));
    }
}
