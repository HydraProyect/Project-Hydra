using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;
using MediatR;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>«Inbound sin Outbound», D6=a: las peticiones de Comunicaciones que se abren al Tenant solo Inbound no piden la
/// cartera de Clientes empresariales ni la de Centros a <see cref="IAlcanceDatosService"/> directamente.</b>
///
/// <para>
/// <b>Por qué existe.</b> Al Tenant solo Inbound con el papel de Tenant de origen, la guarda le abre Comunicaciones acotada
/// a los hilos con sus Empresas contratistas (<see cref="PeticionDeOutbound.AcotadaAlSoloInbound"/>). Dentro de esas
/// peticiones <c>AlcanceDatosService</c> da el alcance de Outbound de siempre (no fija <see cref="AmbitoPeticionOutbound"/>
/// para ellas), así que el recorte lo aplican solo las extensiones que miran <see cref="AmbitoComunicacionesSoloInbound"/>:
/// <see cref="AlcanceDatosServiceExtensions.ClientesEmpresarialesVisiblesAsync"/>,
/// <see cref="AlcanceDatosServiceExtensions.CentrosVisiblesAsync"/>, <see cref="AlcanceDatosServiceExtensions.CentroVisibleAsync"/>
/// y <see cref="AlcanceDatosServiceExtensions.CentroParaGestionVisibleAsync"/>. Un handler que llama directamente a
/// <see cref="IAlcanceDatosService.ObtenerCentroIdsVisiblesAsync"/> recibe los Centros de Outbound del Tenant: es lo que
/// hacía la búsqueda por Centro de la bandeja (<c>ObtenerConversacionesQuery</c>, hallazgo A1 del revisor-puente sobre la
/// PR #2379).
/// </para>
///
/// <para>
/// <b>Cómo se mide.</b> Con <see cref="GrafoDeLlamadasIl"/> sobre Application: el <c>Handle</c> de cada handler de una
/// petición acotada no alcanza, por ningún camino que no pase por esas cuatro extensiones, ninguna de las lecturas de
/// cartera de Clientes o de Centros de <see cref="IAlcanceDatosService"/>. La cartera de Empresas queda fuera a propósito:
/// D6=a conserva los hilos anclados a una Empresa.
/// </para>
///
/// <para>
/// <b>Lo que esto NO demuestra.</b> (1) Lo que el handler lee sin pasar por el alcance (una tabla de Outbound leída sin
/// filtro de cartera, como las sugerencias del detalle del hilo): eso lo fijan los tests de Application
/// (<c>ComunicacionesSoloInboundSinCentrosNiSugerenciasTests</c>). (2) Las peticiones que el handler envía por el mediador:
/// cada una fija su propio marco. (3) Una interfaz con varias implementaciones de las que solo alguna alcanza la lectura
/// directa: el grafo solo la cuenta si todas la alcanzan. (4) Que las extensiones apliquen bien el recorte: lo fijan sus
/// tests de Application.
/// </para>
/// </summary>
public class LecturasDeCarteraEnComunicacionesSoloInboundTests
{
    private static readonly Type[] InterfacesDePeticion =
        [typeof(IRequestHandler<,>), typeof(IRequestHandler<>), typeof(IStreamRequestHandler<,>)];

    private static readonly MethodBase[] LecturasDirectas =
    [
        typeof(IAlcanceDatosService).GetMethod(nameof(IAlcanceDatosService.ObtenerClienteIdsVisiblesAsync))!,
        typeof(IAlcanceDatosService).GetMethod(nameof(IAlcanceDatosService.ObtenerCentroIdsVisiblesAsync))!,
        typeof(IAlcanceDatosService).GetMethod(nameof(IAlcanceDatosService.ObtenerCentroIdsParaGestionAsync))!,
    ];

    private static readonly MethodBase[] ExtensionesConRecorte =
    [
        typeof(AlcanceDatosServiceExtensions).GetMethod(nameof(AlcanceDatosServiceExtensions.ClientesEmpresarialesVisiblesAsync))!,
        typeof(AlcanceDatosServiceExtensions).GetMethod(nameof(AlcanceDatosServiceExtensions.CentrosVisiblesAsync))!,
        typeof(AlcanceDatosServiceExtensions).GetMethod(nameof(AlcanceDatosServiceExtensions.CentroVisibleAsync))!,
        typeof(AlcanceDatosServiceExtensions).GetMethod(nameof(AlcanceDatosServiceExtensions.CentroParaGestionVisibleAsync))!,
    ];

    private static readonly Lazy<(GrafoDeLlamadasIl Grafo, HashSet<ClaveMetodo> Alcanzan)> Medida = new(() =>
    {
        var grafo = new GrafoDeLlamadasIl([typeof(ICommand).Assembly]);
        return (grafo, grafo.QueAlcanzan(LecturasDirectas, ExtensionesConRecorte));
    });

    [Fact]
    public void Ningun_handler_de_Comunicaciones_abierto_al_Tenant_solo_Inbound_lee_Clientes_o_Centros_sin_el_recorte()
    {
        var (grafo, alcanzan) = Medida.Value;

        var infractores = Handles(PeticionDeOutbound.AcotadaAlSoloInbound)
            .Where(h => alcanzan.Contains(ClaveMetodo.De(h.Handle)))
            .Select(h => $"{h.Handle.DeclaringType!.FullName}: {string.Join(" -> ", grafo.Camino(h.Handle, LecturasDirectas, alcanzan))}")
            .ToList();

        infractores.Should().BeEmpty(
            "dentro de una petición acotada al Tenant solo Inbound (D6=a) la cartera de Clientes y la de Centros se piden " +
            "por AlcanceDatosServiceExtensions (ClientesEmpresarialesVisiblesAsync, CentrosVisiblesAsync, CentroVisibleAsync, " +
            "CentroParaGestionVisibleAsync), que la vacían con AmbitoComunicacionesSoloInbound; IAlcanceDatosService da ahí " +
            "el alcance de Outbound entero");
    }

    [Fact]
    public void La_medida_ve_las_peticiones_acotadas_y_las_lecturas_directas()
    {
        var (grafo, alcanzan) = Medida.Value;

        // Control de población: si PeticionDeOutbound dejara de abrir Comunicaciones, el test de arriba pasaría vacío.
        Handles(PeticionDeOutbound.AcotadaAlSoloInbound).Count().Should().BeGreaterThan(30,
            "hoy Comunicaciones abre al Tenant solo Inbound 39 peticiones con handler");

        // Control positivo: una petición cerrada de Comunicaciones que sí lee los Centros directamente se ve como tal. Si la
        // reflexión, el grafo o los cortes dejaran de verla, el test de arriba pasaría sin medir nada.
        Handles(PeticionDeOutbound.CerradaAlSoloInbound)
            .Should().Contain(h => h.Handle.DeclaringType!.Name == "ObtenerSugerenciasVisitaCorreoPendientesQueryHandler"
                                   && alcanzan.Contains(ClaveMetodo.De(h.Handle)),
                "ObtenerSugerenciasVisitaCorreoPendientesQueryHandler llama a ObtenerCentroIdsVisiblesAsync directamente");

        // Control de los cortes: la bandeja pide los Centros por CentrosVisiblesAsync y los Clientes empresariales por
        // VisibilidadConversacion (ClientesEmpresarialesVisiblesAsync), así que sin cortes sí alcanza las lecturas directas
        // y con ellos no. La primera mitad solo caería si la bandeja dejara de tocar las dos carteras, no una sola.
        var bandeja = Handles(PeticionDeOutbound.AcotadaAlSoloInbound)
            .Single(h => h.Handle.DeclaringType!.Name == "ObtenerConversacionesQueryHandler").Handle;
        grafo.QueAlcanzan(LecturasDirectas).Should().Contain(ClaveMetodo.De(bandeja),
            "sin cortes, la bandeja llega a ObtenerCentroIdsVisiblesAsync por CentrosVisiblesAsync y a " +
            "ObtenerClienteIdsVisiblesAsync por ClientesEmpresarialesVisiblesAsync");
        alcanzan.Should().NotContain(ClaveMetodo.De(bandeja), "con los cortes, ese camino pasa por la extensión con recorte");
    }

    private static IEnumerable<(Type Peticion, MethodInfo Handle)> Handles(Func<Type, bool> filtro) =>
        ReflexionArquitecturaHelper.TiposDe(typeof(ICommand).Assembly)
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && InterfacesDePeticion.Contains(i.GetGenericTypeDefinition()))
                .Select(i => (Peticion: i.GetGenericArguments()[0], Handle: t.GetInterfaceMap(i).TargetMethods.Single())))
            .Where(h => filtro(h.Peticion));
}
