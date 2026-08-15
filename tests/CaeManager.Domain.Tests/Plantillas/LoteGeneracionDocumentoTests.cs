using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Plantillas;

public class LoteGeneracionDocumentoTests
{
    private static LoteGeneracionDocumento Crear(int totalItems = 2) =>
        new(Guid.NewGuid(), totalItems, Guid.NewGuid(), DateTime.UtcNow);

    [Fact]
    public void Constructor_nace_en_progreso()
    {
        var lote = Crear();

        lote.Estado.Should().Be(EstadoLoteGeneracion.EnProgreso);
        lote.ItemsCompletados.Should().Be(0);
        lote.ItemsFallidos.Should().Be(0);
        lote.CompletadoEnUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rechaza_total_de_items_menor_que_uno(int total)
    {
        var accion = () => new LoteGeneracionDocumento(Guid.NewGuid(), total, Guid.NewGuid(), DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_version_vacia()
    {
        var accion = () => new LoteGeneracionDocumento(Guid.Empty, 1, Guid.NewGuid(), DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_usuario_vacio()
    {
        var accion = () => new LoteGeneracionDocumento(Guid.NewGuid(), 1, Guid.Empty, DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Se_completa_cuando_todos_los_items_terminan_sin_fallos()
    {
        var lote = Crear(totalItems: 2);
        var ahora = DateTime.UtcNow;

        lote.RegistrarItemCompletado(ahora);
        lote.Estado.Should().Be(EstadoLoteGeneracion.EnProgreso);

        lote.RegistrarItemCompletado(ahora);
        lote.Estado.Should().Be(EstadoLoteGeneracion.Completado);
        lote.CompletadoEnUtc.Should().Be(ahora);
    }

    [Fact]
    public void Se_completa_con_errores_si_algun_item_falla()
    {
        var lote = Crear(totalItems: 2);
        var ahora = DateTime.UtcNow;

        lote.RegistrarItemCompletado(ahora);
        lote.RegistrarItemFallido(ahora);

        lote.Estado.Should().Be(EstadoLoteGeneracion.CompletadoConErrores);
        lote.ItemsCompletados.Should().Be(1);
        lote.ItemsFallidos.Should().Be(1);
    }

    [Fact]
    public void Rechaza_registrar_items_en_un_lote_ya_terminado()
    {
        var lote = Crear(totalItems: 1);
        lote.RegistrarItemCompletado(DateTime.UtcNow);

        var accion = () => lote.RegistrarItemCompletado(DateTime.UtcNow);

        accion.Should().Throw<InvalidOperationException>();
    }
}
