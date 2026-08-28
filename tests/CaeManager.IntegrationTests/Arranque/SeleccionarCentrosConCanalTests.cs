using CaeManager.Domain.Centros;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// <see cref="DatosPruebaSeeder.SeleccionarCentrosConCanal"/> es la pieza que
/// garantiza el defecto 1 de la auditoría de determinismo del sembrador de
/// demo: el requisito "bloqueante garantizado" se graba sobre
/// <c>centrosConCanal[0..2]</c>, y esos tres tienen que tener plantilla real
/// — si no, el requisito se graba pero no hay a quién le falte el documento,
/// y el centro nunca sale rojo.
///
/// <para>
/// Sin base de datos a propósito: la propiedad es pura (una función de
/// selección), así que se puede forzar con un escenario donde la MAYORÍA de
/// los centros NO tienen plantilla y barrer muchos <see cref="Random"/>
/// distintos — con el defecto presente (sortear entre TODOS los centros),
/// la probabilidad de que ningún seed de los 200 caiga en un centro sin
/// plantilla es insignificante; con el arreglo, es matemáticamente cero
/// pase lo que pase con el dado.
/// </para>
/// </summary>
public class SeleccionarCentrosConCanalTests
{
    [Fact]
    public void Los_tres_primeros_centros_tienen_siempre_plantilla_aunque_la_mayoria_no()
    {
        var clienteId = Guid.NewGuid();
        var empresaId = Guid.NewGuid();

        // 12 centros; solo 4 con plantilla (los índices 0-3), 8 sin ella —
        // si el sorteo fuera sobre TODOS, la probabilidad de que los tres
        // primeros salgan siempre entre los 4 "con gente" es (4/12 · 3/11 ·
        // 2/10) ≈ 1.8% por siembra; barrido en 200 semillas, la probabilidad
        // de que el defecto pase inadvertido es astronómicamente baja.
        var centros = Enumerable.Range(0, 12)
            .Select(i => new Centro(clienteId, empresaId, $"Centro {i:D2}", codigoCentro: $"CTR-{i:D4}"))
            .ToList();
        var centrosConGente = centros.Take(4).Select(c => c.Id).ToHashSet();

        for (var semilla = 0; semilla < 200; semilla++)
        {
            var seleccion = DatosPruebaSeeder.SeleccionarCentrosConCanal(centros, centrosConGente, new Random(semilla));

            seleccion.Count.Should().BeGreaterThanOrEqualTo(3,
                $"semilla {semilla}: con 4 centros elegibles hay que poder completar los 3 primeros");
            seleccion.Take(3).Should().OnlyContain(c => centrosConGente.Contains(c.Id),
                $"semilla {semilla}: MEDIDO — los tres primeros (donde se graba el requisito bloqueante) " +
                "tienen que salir siempre de los centros con plantilla, no del sorteo general");
        }
    }

    [Fact]
    public void Con_menos_de_tres_centros_con_plantilla_no_rompe__degrada_al_conjunto_completo()
    {
        var clienteId = Guid.NewGuid();
        var empresaId = Guid.NewGuid();
        var centros = Enumerable.Range(0, 5)
            .Select(i => new Centro(clienteId, empresaId, $"Centro {i:D2}", codigoCentro: $"CTR-{i:D4}"))
            .ToList();
        var centrosConGente = centros.Take(2).Select(c => c.Id).ToHashSet();

        var seleccion = DatosPruebaSeeder.SeleccionarCentrosConCanal(centros, centrosConGente, new Random(1));

        seleccion.Should().NotBeEmpty("con menos de 3 centros con plantilla no hay garantía posible, " +
            "pero el sembrador tiene que seguir produciendo canales igualmente");
    }
}
