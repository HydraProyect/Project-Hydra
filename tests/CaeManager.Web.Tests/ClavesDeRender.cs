using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Rendering;

// BL0006: los tipos de RenderTree no son API estable fuera del framework. Es justo lo que este instrumento necesita leer (las claves del
// árbol ya pintado); si cambian, el test de control positivo (ClavesUnicasEnListasTests) se pone rojo antes de dar un verde ciego.
#pragma warning disable BL0006

namespace CaeManager.Web.Tests;

/// <summary>
/// Detector genérico de <c>@key</c> repetidas entre hermanos en el árbol de render ACTUAL de un componente y de todos sus
/// descendientes. Mide lo que Blazor rechaza —dos hermanos con la misma clave: «Attempting to return wrong pooled instance»
/// y el circuito muere— sin esperar a que lo rechace.
///
/// <para>
/// <b>Por qué no basta con «que no lance».</b> Blazor solo diferencia (y por tanto solo lanza) cuando repinta contra un árbol
/// anterior: un primer pintado con claves repetidas pasa en silencio, y los tests síncronos que pintan una sola vez nunca ven
/// el defecto (#1064). Este detector lee las claves del árbol ya pintado, así que ve el duplicado en el primer pintado.
/// </para>
///
/// <para>
/// <b>Qué es «hermano».</b> Los marcos de un mismo rango dentro del mismo padre (elemento, componente o región): es el alcance
/// con el que el diff de Blazor casa claves. Dos hermanos bajo padres distintos con la misma clave son legales y no se cuentan.
/// El control positivo (<c>ClavesDeRenderTests</c>) alimenta al detector con un componente que repite claves y otro que no.
/// </para>
/// </summary>
internal static class ClavesDeRender
{
    /// <summary>Descripción de cada clave repetida entre hermanos (vacío = ninguna), con la ruta del padre.</summary>
    public static IReadOnlyList<string> Duplicadas<T>(BunitContext contexto, IRenderedComponent<T> raiz) where T : IComponent
    {
        var duplicadas = new List<string>();
        var visitados = new HashSet<int>();
        Componente(contexto, raiz.ComponentId, typeof(T).Name, duplicadas, visitados);
        return duplicadas;
    }

    /// <summary>Cuántas claves distintas del árbol (de elementos y de componentes) se vieron: el control positivo de que el detector observa algo.</summary>
    public static int ClavesObservadas<T>(BunitContext contexto, IRenderedComponent<T> raiz) where T : IComponent
    {
        var total = 0;
        Recorrer(contexto, raiz.ComponentId, (_, _) => total++, []);
        return total;
    }

    private static readonly System.Reflection.MethodInfo LeerMarcos = typeof(Renderer).GetMethod(
        "GetCurrentRenderTreeFrames", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Renderer.GetCurrentRenderTreeFrames ya no existe: el detector de claves de render hay que reescribirlo.");

    /// <summary>El método es protegido en <c>Renderer</c>: se llama por reflexión; si deja de existir, el instrumento falla ruidosamente.</summary>
    private static ArrayRange<RenderTreeFrame> MarcosActuales(BunitContext contexto, int componentId) =>
        (ArrayRange<RenderTreeFrame>)LeerMarcos.Invoke(contexto.Renderer, [componentId])!;

    private static void Componente(BunitContext contexto, int componentId, string ruta, List<string> duplicadas, HashSet<int> visitados)
    {
        if (!visitados.Add(componentId)) return;
        var marcos = MarcosActuales(contexto, componentId);
        Rango(contexto, marcos.Array, 0, marcos.Count, ruta, duplicadas, visitados);
    }

    private static void Rango(
        BunitContext contexto, RenderTreeFrame[] marcos, int desde, int hasta, string ruta,
        List<string> duplicadas, HashSet<int> visitados)
    {
        var vistas = new HashSet<object>();
        var i = desde;
        while (i < hasta)
        {
            var marco = marcos[i];
            switch (marco.FrameType)
            {
                case RenderTreeFrameType.Element:
                    Anotar(vistas, marco.ElementKey, $"{ruta}/<{marco.ElementName}>", duplicadas);
                    Rango(contexto, marcos, i + 1, i + marco.ElementSubtreeLength, $"{ruta}/<{marco.ElementName}>", duplicadas, visitados);
                    i += marco.ElementSubtreeLength;
                    break;
                case RenderTreeFrameType.Component:
                    Anotar(vistas, marco.ComponentKey, $"{ruta}/{marco.ComponentType.Name}", duplicadas);
                    Componente(contexto, marco.ComponentId, $"{ruta}/{marco.ComponentType.Name}", duplicadas, visitados);
                    i += marco.ComponentSubtreeLength;
                    break;
                case RenderTreeFrameType.Region:
                    Rango(contexto, marcos, i + 1, i + marco.RegionSubtreeLength, ruta, duplicadas, visitados);
                    i += marco.RegionSubtreeLength;
                    break;
                default:
                    i++;
                    break;
            }
        }
    }

    private static void Anotar(HashSet<object> vistas, object? clave, string donde, List<string> duplicadas)
    {
        if (clave is not null && !vistas.Add(clave))
            duplicadas.Add($"{donde} @key repetida entre hermanos: {clave}");
    }

    private static void Recorrer(BunitContext contexto, int componentId, Action<string, object> alVerClave, HashSet<int> visitados)
    {
        if (!visitados.Add(componentId)) return;
        var marcos = MarcosActuales(contexto, componentId);
        for (var i = 0; i < marcos.Count; i++)
        {
            var marco = marcos.Array[i];
            if (marco.FrameType == RenderTreeFrameType.Element && marco.ElementKey is { } k) alVerClave(marco.ElementName, k);
            else if (marco.FrameType == RenderTreeFrameType.Component)
            {
                if (marco.ComponentKey is { } kc) alVerClave(marco.ComponentType.Name, kc);
                Recorrer(contexto, marco.ComponentId, alVerClave, visitados);
            }
        }
    }
}
