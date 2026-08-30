using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Auditoría Módulo 5, hueco arquitectónico: <c>CrearCentroCommandHandler</c>
/// busca el catálogo mínimo por defecto de un Centro nuevo por Nombre, no por
/// Id fijo — los Id de <see cref="TipoDocumentoSeedData"/> son solo del
/// catálogo semilla del tenant #1, así que referenciarlos por Id crearía una
/// fila cruzando tenants (ver el doc-comment del propio handler).
///
/// Eso es correcto cuando un TENANT concreto personaliza o renombra su copia
/// — degradación silenciosa deliberada. Pero si es el CATÁLOGO SEMILLA el que
/// renombra uno de esos cuatro nombres en una futura limpieza (como ya pasó
/// con la T3), todo tenant aprovisionado después nacería con el catálogo
/// mínimo incompleto para siempre, sin que nadie lo note — la fuente de
/// verdad y el criterio de búsqueda divergirían en silencio.
///
/// Este test es el ratchet: falla en CI el mismo commit que rompa la
/// coincidencia, en vez de descubrirse meses después en un tenant nuevo.
/// </summary>
public class CatalogoMinimoCentroCasaConSemillaTests
{
    [Fact]
    public void Cada_nombre_del_catalogo_minimo_existe_en_la_semilla_maestra()
    {
        var nombresDeLaSemilla = TipoDocumentoSeedData.Datos.Select(d => d.Nombre).ToHashSet();

        var faltantes = CrearCentroCommandHandler.NombresCatalogoMinimo
            .Where(nombre => !nombresDeLaSemilla.Contains(nombre))
            .ToList();

        faltantes.Should().BeEmpty(
            "si el catálogo semilla renombró alguno de estos tipos, todo tenant aprovisionado " +
            "después de ese cambio nacería con el catálogo mínimo de Centro incompleto en silencio — " +
            "actualiza CrearCentroCommandHandler.NombresCatalogoMinimo con el nombre nuevo");
    }
}
