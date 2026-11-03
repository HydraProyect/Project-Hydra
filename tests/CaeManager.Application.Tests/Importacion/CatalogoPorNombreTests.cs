using CaeManager.Application.Importacion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Importacion;

public class CatalogoPorNombreTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public void Un_nombre_sin_entradas_no_existe_y_uno_con_una_sola_es_unico_sin_distinguir_mayusculas()
    {
        var catalogo = new CatalogoPorNombre<Guid>();
        catalogo.Agregar("Planta Norte", A);

        catalogo.Buscar("otro").Should().Be(ResultadoBusquedaPorNombre.NoExiste);
        catalogo.Buscar("PLANTA NORTE", out var id).Should().Be(ResultadoBusquedaPorNombre.Unico);
        id.Should().Be(A);
    }

    [Fact]
    public void Dos_entidades_distintas_con_el_mismo_nombre_son_ambiguas_y_la_misma_entidad_dos_veces_no()
    {
        var catalogo = new CatalogoPorNombre<Guid>();
        catalogo.Agregar("Acme", A);
        catalogo.Agregar("ACME", A);
        catalogo.Buscar("acme").Should().Be(ResultadoBusquedaPorNombre.Unico);

        catalogo.Agregar("acme", B);
        catalogo.Buscar("Acme").Should().Be(ResultadoBusquedaPorNombre.Ambiguo);
    }

    [Fact]
    public void Fijar_desambigua_cuando_la_entidad_ya_era_una_de_las_candidatas()
    {
        var catalogo = new CatalogoPorNombre<Guid>();
        catalogo.Agregar("Acme", A);
        catalogo.Agregar("ACME", B);

        catalogo.Fijar("Acme", A);

        catalogo.Buscar("acme", out var id).Should().Be(ResultadoBusquedaPorNombre.Unico);
        id.Should().Be(A);
    }

    [Fact]
    public void Fijar_dos_entidades_distintas_con_nombres_homonimos_deja_el_nombre_ambiguo_y_no_gana_la_ultima()
    {
        var catalogo = new CatalogoPorNombre<Guid>();
        catalogo.Agregar("Acme", A);
        catalogo.Agregar("ACME", B);

        catalogo.Fijar("Acme", A);
        catalogo.Fijar("ACME", B);

        catalogo.Buscar("acme").Should().Be(ResultadoBusquedaPorNombre.Ambiguo);
        catalogo.Fijar("Acme", A);
        catalogo.Buscar("acme").Should().Be(ResultadoBusquedaPorNombre.Ambiguo, "repetir la fila no deshace la ambigüedad");
    }

    [Fact]
    public void Fijar_un_nombre_que_ya_llevaba_otra_entidad_no_la_pisa()
    {
        var catalogo = new CatalogoPorNombre<Guid>();
        catalogo.Agregar("Nuevo", B);

        catalogo.Fijar("Nuevo", A);

        catalogo.Buscar("nuevo").Should().Be(ResultadoBusquedaPorNombre.Ambiguo);
    }

    [Fact]
    public void Fijar_un_nombre_libre_lo_asigna()
    {
        var catalogo = new CatalogoPorNombre<Guid>();

        catalogo.Fijar("Libre", A);

        catalogo.Buscar("libre", out var id).Should().Be(ResultadoBusquedaPorNombre.Unico);
        id.Should().Be(A);
    }
}
