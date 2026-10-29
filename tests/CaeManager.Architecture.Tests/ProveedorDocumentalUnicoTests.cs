using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Infrastructure.AsistenteIa;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La IA documental tiene un único proveedor, Anthropic, sin caída automática a
/// otro (decisión de proveedor único, 2026-10-29). El router conserva el
/// mecanismo general de recorrer candidatos y reintentar con el siguiente, y la
/// fábrica ordena detrás a cualquier proveedor que no esté en su tabla: un
/// segundo <see cref="IDocumentAIProvider"/> registrado reactivaría la caída sin
/// que ningún test de comportamiento fallara. Esta guarda lo impide en origen:
/// las únicas implementaciones son Anthropic y el doble del arnés E2E.
/// </summary>
public class ProveedorDocumentalUnicoTests
{
    [Fact]
    public void Las_unicas_implementaciones_de_IDocumentAIProvider_son_Anthropic_y_el_doble_E2E()
    {
        var implementaciones = new[]
            {
                typeof(IDocumentAIProvider).Assembly,
                typeof(AnthropicDocumentAIProvider).Assembly,
                typeof(CaeManager.Web.Features.AsistenteIa.DisponibilidadAsistente).Assembly,
            }
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDocumentAIProvider).IsAssignableFrom(t))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        implementaciones.Should().Equal(
            [nameof(AnthropicDocumentAIProvider), nameof(ProveedorFalsoDocumentAI)],
            "un segundo proveedor documental reabriría la caída automática a otro proveedor");
    }
}
