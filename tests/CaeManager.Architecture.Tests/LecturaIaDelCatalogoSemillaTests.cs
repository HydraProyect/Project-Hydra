using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// DPA 16.4: la lectura por IA de un tipo que revela salud nace apagada. Las filas de HasData no pasan por
/// el constructor de <see cref="TipoDocumento"/> y repiten el criterio a mano; las copias por Tenant sí pasan.
/// </summary>
public class LecturaIaDelCatalogoSemillaTests
{
    [Fact]
    public void Las_filas_de_la_migracion_apagan_la_lectura_solo_en_los_tipos_de_salud()
    {
        var filas = TipoDocumentoSeedData.ComoFilasParaMigracion()
            .Select(f => new { Sensibilidad = Leer<SensibilidadDocumental>(f, "Sensibilidad"), Lectura = Leer<bool>(f, "LecturaIaActiva") })
            .ToList();

        filas.Should().Contain(f => f.Sensibilidad == SensibilidadDocumental.CategoriaEspecialSalud,
            "guarda del instrumento: el catálogo semilla trae tipos de salud");
        filas.Should().OnlyContain(f => f.Lectura == (f.Sensibilidad != SensibilidadDocumental.CategoriaEspecialSalud));
    }

    [Fact]
    public void Las_copias_por_Tenant_apagan_la_lectura_solo_en_los_tipos_de_salud()
    {
        var copias = TipoDocumentoSeedData.CrearCopiasParaTenant().ToList();

        copias.Should().Contain(c => c.RevelaSalud, "guarda del instrumento: el catálogo semilla trae tipos de salud");
        copias.Should().OnlyContain(c => c.LecturaIaActiva == !c.RevelaSalud);
    }

    private static T Leer<T>(object fila, string propiedad) =>
        (T)fila.GetType().GetProperty(propiedad)!.GetValue(fila)!;
}
