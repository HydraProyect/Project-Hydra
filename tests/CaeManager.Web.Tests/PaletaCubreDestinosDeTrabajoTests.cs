using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Web.Features.BusquedaGlobal;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// El trinquete de DEC-75 (REC-190, HO-190-01, continuación de REC-006):
/// <b>la paleta global cubre todo destino al que alguien va a propósito; las
/// excepciones son las páginas a las que se llega sin quererlo.</b>
/// HO-006-01 descartó un trinquete general sobre las rutas <c>@page</c> del
/// repositorio "con buen criterio y midiendo" —en su momento habría nacido
/// rojo, o exigido una lista de exclusión de dieciocho/veinte entradas
/// arbitrarias que nadie mantendría al día (ver
/// <see cref="BuscadorGlobalIrAAreasNuevasTests"/>). Lo que cambió es que
/// DEC-75 fijó un criterio explícito con una lista de excepciones corta y
/// justificable —<see cref="CoberturaDePaleta.SegmentosExcluidosDeLaPaleta"/>,
/// cuatro entradas— así que el trinquete general sí es viable ahora.
///
/// <para>
/// <b>Qué escanea.</b> Reflexión sobre <see cref="RouteAttribute"/> en el
/// ensamblado ya compilado de CaeManager.Web (no texto/regex sobre los
/// <c>.razor</c>): <c>@page</c> compila a ese atributo sobre la clase
/// generada, así que esto ve exactamente lo que ASP.NET Core enruta, sin los
/// falsos negativos de un grep que se salte un fichero o una plantilla
/// multilínea (mismo argumento que <c>AutorizacionDePaginasTests</c>).
/// </para>
///
/// <para>
/// <b>Qué cuenta como "destino de primer nivel".</b> Una ruta con un único
/// segmento no vacío (<c>/alertas</c>) o cuyos segmentos posteriores al
/// primero son TODOS parámetros opcionales (<c>/configuracion/{EntradaRuta?}</c>
/// colapsa a "configuracion", porque <c>/configuracion</c> a secas ya
/// resuelve esa misma página) — es decir, cualquier ruta que un usuario
/// podría teclear o buscar como "el área X", sin necesitar ya estar dentro
/// de ella. Una ruta anidada con un segmento LITERAL adicional
/// (<c>/plantillas/nueva</c>) o con un parámetro OBLIGATORIO
/// (<c>/centros/{CentroId:guid}</c>, un detalle al que solo se llega
/// habiendo entrado antes al listado) queda deliberadamente fuera: no es un
/// "destino de trabajo" en el sentido de DEC-75, es una sub-pantalla — ver
/// <see cref="SegmentoDePrimerNivelTests"/> para el contrato exacto de este
/// colapso.
/// </para>
///
/// <para>
/// <b>Hallazgo REC-190, no cubierto por las cuatro excepciones de DEC-75:</b>
/// <c>/Error</c> (el manejador genérico de excepciones de ASP.NET Core, ver
/// <c>Program.cs</c> — <c>app.UseExceptionHandler("/Error", ...)</c>, y el
/// propio comentario de <c>Error.razor</c>) es una ruta <c>@page</c> de
/// primer nivel real, de la misma naturaleza "se llega sin quererlo" que
/// <c>acceso-denegado</c>/<c>not-found</c> — pero DEC-75 no la nombra entre
/// sus cuatro excepciones (la propia acta corrige un desajuste de recuento
/// parecido en su sección "Una cifra propia corregida"). Por disciplina
/// contra el scope creep (§ 7/§ 9 de HO-190-01: "excluir una ruta del
/// trinquete para que pase" es una decisión que esta sesión no puede tomar
/// sin acta), NO se añade a <see cref="CoberturaDePaleta.SegmentosExcluidosDeLaPaleta"/>
/// —esa lista debe reflejar exactamente las cuatro de la acta, nada más—.
/// Se declara aparte, en <see cref="HallazgosPendientesDeRatificar"/>, y se
/// eleva en el retorno de HO-190-01 para que la Oficina de Reconciliación
/// decida si es la quinta excepción o si merece entrada propia. Quitar esa
/// entrada en cuanto exista una decisión escrita, sea cual sea.
/// </para>
/// </summary>
public class PaletaCubreDestinosDeTrabajoTests
{
    /// <summary>
    /// Ver el párrafo "Hallazgo REC-190" de la clase. Deliberadamente
    /// separada de <see cref="CoberturaDePaleta.SegmentosExcluidosDeLaPaleta"/>
    /// para que la lista con autoridad de acta nunca se confunda con un
    /// hallazgo todavía sin ratificar.
    /// </summary>
    private static readonly IReadOnlyCollection<string> HallazgosPendientesDeRatificar = ["Error"];

    [Fact]
    public void Toda_ruta_de_primer_nivel_tiene_entrada_en_la_paleta_o_excepcion_declarada()
    {
        var segmentosReales = SegmentosDePrimerNivelDelEnsamblado(Assembly.Load("CaeManager.Web"));

        var segmentosCubiertos = CoberturaDePaleta.DestinosNavegacion
            .Select(d => SegmentoDePrimerNivel(d.Ruta))
            .Where(s => s is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sinPuerta = segmentosReales
            .Where(s => !segmentosCubiertos.Contains(s))
            .Where(s => !CoberturaDePaleta.SegmentosExcluidosDeLaPaleta.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Where(s => !HallazgosPendientesDeRatificar.Contains(s, StringComparer.OrdinalIgnoreCase))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        sinPuerta.Should().BeEmpty(
            "toda ruta @page de primer nivel es un destino al que alguien va a propósito (DEC-75, REC-190) y debe " +
            "tener entrada en CoberturaDePaleta.DestinosNavegacion o figurar, con su motivo, en " +
            $"CoberturaDePaleta.SegmentosExcluidosDeLaPaleta — nació sin puerta: {string.Join(", ", sinPuerta)}");
    }

    /// <summary>Control positivo: si el escaneo no encuentra NINGUNA ruta real, el test de arriba pasaría vacío por un instrumento ciego, no por cobertura real.</summary>
    [Fact]
    public void El_escaneo_encuentra_rutas_de_primer_nivel_reales_control_positivo()
    {
        var segmentosReales = SegmentosDePrimerNivelDelEnsamblado(Assembly.Load("CaeManager.Web"));

        segmentosReales.Should().Contain("clientes", "el propio escaneo debe encontrar rutas de primer nivel conocidas — si no, el test principal pasaría vacío sin haber comprobado nada");
        segmentosReales.Should().Contain("bandeja", "la ruta que este mismo REC (REC-190) acaba de cubrir debe seguir siendo un segmento de primer nivel real");
        segmentosReales.Should().Contain("configuracion", "/configuracion/{EntradaRuta?} debe colapsar a su segmento base — si esto falla, el colapso de parámetros opcionales dejó de funcionar");
    }

    private static IReadOnlyCollection<string> SegmentosDePrimerNivelDelEnsamblado(Assembly assembly)
    {
        var segmentos = new List<string>();

        foreach (var tipo in TiposDe(assembly))
        {
            foreach (var atributo in tipo.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Cast<RouteAttribute>())
            {
                var segmento = SegmentoDePrimerNivel(atributo.Template);
                if (segmento is not null) segmentos.Add(segmento);
            }
        }

        return segmentos.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // GetTypes() puede lanzar ReflectionTypeLoadException en ensamblados Blazor
    // (mismo motivo que ReflexionArquitecturaHelper.TiposDe en
    // CaeManager.Architecture.Tests, no reutilizable aquí sin acoplar los dos
    // proyectos de test): se toman los tipos que sí resolvieron.
    private static IEnumerable<Type> TiposDe(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    /// <summary>
    /// Colapsa una plantilla de ruta a su segmento de primer nivel, o
    /// <c>null</c> si la ruta no representa un destino de primer nivel — ver
    /// <see cref="SegmentoDePrimerNivelTests"/> para el contrato completo con
    /// ejemplos reales del repositorio.
    /// </summary>
    internal static string? SegmentoDePrimerNivel(string plantilla)
    {
        var segmentos = plantilla.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segmentos.Length == 0) return null; // raíz "/" — Inicio.razor, ya cubierta por "Ir a Dashboard", fuera del barrido.
        if (segmentos[0].StartsWith('{')) return null; // ruta paramétrica sin prefijo literal — no hay "área" que nombrar.

        var resto = segmentos.Skip(1);
        var restoEsSoloParametrosOpcionales = resto.All(EsParametroOpcional);

        return restoEsSoloParametrosOpcionales ? segmentos[0] : null;
    }

    private static readonly Regex ParametroOpcional = new(@"^\{[^{}]+\?\}$", RegexOptions.Compiled);

    private static bool EsParametroOpcional(string segmento) => ParametroOpcional.IsMatch(segmento);
}

/// <summary>
/// Contrato de <see cref="PaletaCubreDestinosDeTrabajoTests.SegmentoDePrimerNivel"/>
/// fijado con ejemplos reales del repositorio (2026-09-04) — independiente de
/// que esas rutas cambien de forma mañana, para que un cambio en el criterio
/// de colapso (no en el inventario de rutas) se note aquí y no como un
/// efecto colateral confuso del trinquete principal.
/// </summary>
public class SegmentoDePrimerNivelTests
{
    public static TheoryData<string, string?> Plantillas => new()
    {
        { "/alertas", "alertas" },
        { "/bandeja", "bandeja" },
        { "/", null }, // Inicio — raíz, fuera del barrido (ya cubierta por "Ir a Dashboard").
        { "/configuracion/{EntradaRuta?}", "configuracion" }, // único parámetro, y opcional: colapsa a la base.
        { "/centros/{CentroId:guid}", null }, // parámetro OBLIGATORIO — sub-pantalla, no destino de primer nivel.
        { "/plantillas/{PlantillaDocumentoVersionId:guid}/editar", null }, // segmento literal tras el parámetro — no todo "el resto" es opcional.
        { "/plantillas/nueva", null }, // segundo segmento literal — sub-ruta de creación, no un área.
        { "/empresas/{EmpresaId:guid}/deteccion-trabajadores", null }, // parámetro obligatorio en medio.
        { "/configuracion/claves-api", null }, // segundo segmento literal — ya cubierta aparte por su propia entrada "Ir a Claves API".
    };

    [Theory]
    [MemberData(nameof(Plantillas))]
    public void Colapsa_al_segmento_de_primer_nivel_o_null_segun_el_contrato(string plantilla, string? esperado) =>
        PaletaCubreDestinosDeTrabajoTests.SegmentoDePrimerNivel(plantilla).Should().Be(esperado);
}
