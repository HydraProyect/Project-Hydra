using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Plataforma;

/// <summary>
/// Forma del orden global del menú lateral: fila única, identificadores con forma de
/// <c>data-grupo</c>, sin repetidos y con el Actor real que lo cambió. Que los identificadores
/// existan en el catálogo NO se valida aquí: la reconciliación al leer los ignora si ya no existen.
/// </summary>
public class OrdenMenuLateralTests
{
    private static readonly DateTime Ahora = new(2026, 9, 23, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Se_crea_en_la_fila_unica_con_el_actor_real()
    {
        var actor = Guid.NewGuid();

        var orden = OrdenMenuLateral.Crear(["plataforma", "control"], ["conectores-cae"], actor, Ahora);

        orden.Id.Should().Be(OrdenMenuLateral.ClaveCanonica);
        orden.OrdenGrupos.Should().Equal("plataforma", "control");
        orden.OrdenEnlaces.Should().Equal("conectores-cae");
        orden.ActualizadoPorUsuarioId.Should().Be(actor);
        orden.ActualizadoEnUtc.Should().Be(Ahora);
    }

    [Fact]
    public void Restablecer_es_guardar_listas_vacias()
    {
        var orden = OrdenMenuLateral.Crear(["plataforma"], ["conectores-cae"], Guid.NewGuid(), Ahora);

        orden.Reordenar([], [], Guid.NewGuid(), Ahora.AddMinutes(1));

        orden.OrdenGrupos.Should().BeEmpty();
        orden.OrdenEnlaces.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Plataforma")]
    [InlineData("con espacio")]
    [InlineData("-guion-inicial")]
    [InlineData("doble--guion")]
    [InlineData("")]
    [InlineData("<script>")]
    public void Rechaza_un_identificador_sin_la_forma_de_data_grupo(string identificador)
    {
        var accion = () => OrdenMenuLateral.Crear([identificador], [], Guid.NewGuid(), Ahora);

        accion.Should().Throw<ArgumentException>().WithMessage("*no válido*");
    }

    [Fact]
    public void Rechaza_un_identificador_de_mas_de_64_caracteres()
    {
        var accion = () => OrdenMenuLateral.Crear([], [new string('a', 65)], Guid.NewGuid(), Ahora);

        accion.Should().Throw<ArgumentException>().WithMessage("*no válido*");
    }

    [Fact]
    public void Rechaza_repetidos()
    {
        var accion = () => OrdenMenuLateral.Crear(["control", "control"], [], Guid.NewGuid(), Ahora);

        accion.Should().Throw<ArgumentException>().WithMessage("*repetidos*");
    }

    [Fact]
    public void Rechaza_mas_identificadores_de_los_que_caben()
    {
        var muchos = Enumerable.Range(0, OrdenMenuLateral.MaximoIdentificadores + 1).Select(i => $"e{i}").ToList();

        var accion = () => OrdenMenuLateral.Crear([], muchos, Guid.NewGuid(), Ahora);

        accion.Should().Throw<ArgumentException>().WithMessage("*Como mucho*");
    }

    [Fact]
    public void Sin_actor_real_no_hay_cambio()
    {
        var accion = () => OrdenMenuLateral.Crear(["control"], [], Guid.Empty, Ahora);

        accion.Should().Throw<ArgumentException>().WithMessage("*Actor real*");
    }
}
