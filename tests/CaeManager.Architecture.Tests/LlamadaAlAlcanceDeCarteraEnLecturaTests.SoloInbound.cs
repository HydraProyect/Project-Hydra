using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;
using MediatR;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>«Inbound sin Outbound»: las queries de Outbound que no consultan el alcance, inventariadas y congeladas.</b>
///
/// <para>
/// <b>Por qué existe.</b> Al Tenant solo Inbound con el papel de Tenant de origen, la guarda le cierra los comandos de
/// Outbound y <c>AlcanceDatosService</c> no le da alcance dentro de una petición de Outbound
/// (<see cref="AmbitoPeticionOutbound"/>). Una query de Outbound que no pregunta al alcance no se entera de ese cierre: lee
/// lo que el filtro de Tenant y la RLS le dejan, que es el Tenant entero. La segunda opinión sobre la PR #2379 encontró así
/// <c>ObtenerProximaVisitaPorCentroQuery</c>, que llama <c>/centros</c> (del Núcleo) y enseñaba las Visitas de sus Centros.
/// </para>
///
/// <para>
/// <b>Población.</b> Los handlers de las peticiones que no son comandos (<see cref="ICommandBase"/>) y que
/// <see cref="PeticionDeOutbound.CerradaAlSoloInbound"/> cierra al Tenant solo Inbound, cuyo constructor no recibe
/// <see cref="IAlcanceDatosService"/> ni el mediador (<see cref="IMediator"/>, <see cref="ISender"/>). Un handler que
/// recibe el mediador compone otras peticiones, y cada una fija su propio ámbito y pasa por su propio alcance. La lista
/// es <c>Congelados/Queries-de-Outbound-sin-alcance.txt</c>, con la misma clave que
/// <c>Queries-sin-comprobacion-de-cartera.txt</c> y, encima de cada entrada, el motivo por el que no devuelve datos de
/// Outbound al Tenant solo Inbound (o qué lo cierra en su lugar). Una NUEVA tiene que consultar el alcance o llevar su
/// motivo; una OBSOLETA se borra.
/// </para>
///
/// <para>
/// <b>Lo que esto NO demuestra.</b> (1) Que un handler que recibe el alcance lo use en todas sus ramas, ni que lo cruce con
/// las filas que devuelve: eso lo mira, para las listas de Ids visibles, <see cref="LlamadaAlAlcanceDeCarteraEnLecturaTests"/>.
/// (2) Lo que un handler alcanza a través de una interfaz cuya implementación consulta el alcance (la medida sigue las
    /// clases concretas de Application que recibe, dos saltos, no las interfaces): figura en la lista, con
/// ese motivo. (3) Que el motivo de cada entrada sea correcto: es una revisión, hecha entrada por entrada el 2026-11-19,
/// no una medida.
/// </para>
/// </summary>
public partial class LlamadaAlAlcanceDeCarteraEnLecturaTests
{
    private const string NombreDeListaSoloInbound = "Queries-de-Outbound-sin-alcance";

    // ICriterioDeOutbound: el handler pregunta él mismo el cierre del Tenant solo Inbound (AnalizarPlantillaMaestraQuery).
    private static readonly Type[] DependenciasQueCierranOComponen =
        [typeof(IAlcanceDatosService), typeof(ICriterioDeOutbound), typeof(IMediator), typeof(ISender)];

    private static readonly Lazy<IReadOnlyList<(Ubicacion Clave, Type Handler, Type Peticion)>> QueriesDeOutboundSinAlcance =
        new(MedirQueriesDeOutboundSinAlcance);

    [Fact]
    public void Toda_query_de_Outbound_que_no_consulta_el_alcance_esta_en_la_lista_congelada()
    {
        var medido = QueriesDeOutboundSinAlcance.Value.ToDictionary(q => q.Clave, _ => 1);

        var fallo = ListaCongelada.Verificar(
            NombreDeListaSoloInbound,
            medido,
            "NUEVA: una query de Outbound (PeticionDeOutbound.CerradaAlSoloInbound) cuyo handler no recibe " +
            "IAlcanceDatosService ni el mediador. Al Tenant solo Inbound con el papel de Tenant de origen el alcance no le " +
            "da nada dentro de una petición de Outbound, pero esta query no lo pregunta: lee lo que el filtro de Tenant y " +
            "la RLS le dejan. Filtra con el alcance (por ejemplo ObtenerCentroIdsVisiblesAsync) y añade su test, o, si no " +
            "devuelve datos de Outbound de ese Tenant, añade la línea con un comentario '#' encima que diga por qué. " +
            "OBSOLETA: la query ya consulta el alcance o ya no existe; borra la línea y su comentario.");

        fallo.Should().BeNull();
    }

    [Fact]
    public void La_medida_de_queries_de_Outbound_ve_las_que_consultan_el_alcance()
    {
        // Control positivo: si la reflexión dejara de ver los constructores, todas saldrían sin alcance; si dejara de ver
        // las peticiones de Outbound, la lista saldría entera OBSOLETA.
        var queriesDeOutbound = HandlersDeQueriesDeOutbound().ToList();
        queriesDeOutbound.Count.Should().BeGreaterThan(80, "hoy hay más de un centenar de queries de Outbound en Application");
        queriesDeOutbound.Count(h => DependeDe(h.Handler)).Should().BeGreaterThan(50,
            "la mayoría de las queries de Outbound reciben el alcance o el mediador");
        queriesDeOutbound.Should().Contain(h => h.Peticion.Name == "ObtenerVisitasAnterioresDeCentroQuery" && DependeDe(h.Handler),
            "control conocido: el handler vecino del hallazgo recibe IAlcanceDatosService");
    }

    private static IReadOnlyList<(Ubicacion Clave, Type Handler, Type Peticion)> MedirQueriesDeOutboundSinAlcance()
    {
        var resultado = HandlersDeQueriesDeOutbound()
            .Where(h => !DependeDe(h.Handler))
            .Select(h => (Clave(h.Handler, h.Peticion), h.Handler, h.Peticion))
            .ToList();
        resultado.Select(q => q.Item1).Should().OnlyHaveUniqueItems();
        return resultado;
    }

    private static IEnumerable<(Type Handler, Type Peticion)> HandlersDeQueriesDeOutbound() =>
        ReflexionArquitecturaHelper.TiposDe(typeof(ICommand).Assembly)
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && InterfacesDePeticion.Contains(i.GetGenericTypeDefinition()))
                .Select(i => (Handler: t, Peticion: i.GetGenericArguments()[0])))
            .Where(h => !typeof(ICommandBase).IsAssignableFrom(h.Peticion) && PeticionDeOutbound.CerradaAlSoloInbound(h.Peticion));

    /// <summary>
    /// El handler recibe el alcance o el mediador, directamente o a través de una clase concreta de Application que se le
    /// inyecta (como <c>RecorteDeAnalisisAlAmbito</c> en las queries de Importación), hasta dos saltos. Una interfaz no se
    /// sigue: su implementación está en Infrastructure y no se ve desde aquí.
    /// </summary>
    private static bool DependeDe(Type handler, int saltos = 2) =>
        handler.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Any(p => DependenciasQueCierranOComponen.Contains(p.ParameterType)
                      || (saltos > 0 && p.ParameterType is { IsClass: true, IsAbstract: false } tipo
                          && tipo.Assembly == typeof(ICommand).Assembly && DependeDe(tipo, saltos - 1)));
}
