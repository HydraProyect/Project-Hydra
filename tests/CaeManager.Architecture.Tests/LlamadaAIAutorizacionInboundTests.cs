using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Todo handler de Inbound llama a <see cref="IAutorizacionInbound"/> (B-03; plan de backend de Inbound § 8.1, fila B-03),
/// como <see cref="LlamadaAlAlcanceDeCarteraEnEscrituraTests"/> con el alcance de cartera: un handler Inbound nuevo que no
/// pase por la puerta de capacidades pone esto en rojo.
///
/// <para>
/// <b>Cómo se mide «llama».</b> Con el IL compilado (<see cref="GrafoDeLlamadasIl"/>), no con el texto: el método
/// <c>Handle</c> del handler alcanza, directa o transitivamente, <see cref="IAutorizacionInbound.TieneAsync"/>. Inyectar la
/// dependencia y no llamarla no cuenta. El control positivo del instrumento está en el propio test del grafo, y aquí, en
/// que la población no está vacía.
/// </para>
///
/// <para>
/// <b>Lo que esto NO demuestra.</b> Que la comprobación vaya antes de leer o escribir, ni en todas las ramas: basta una
/// llamada alcanzable. Que la capacidad pedida sea la correcta para el handler: eso lo fijan los tests de Application. Nada
/// de la RLS, del filtro de Tenant ni del alcance de cartera de cada recurso.
/// </para>
/// </summary>
public class LlamadaAIAutorizacionInboundTests
{
    private const string EspacioInbound = "CaeManager.Application.Inbound.";

    /// <summary>Handlers de Inbound que no pasan por la puerta de capacidades, con su motivo.</summary>
    private static readonly Dictionary<string, string> Exentos = new()
    {
        ["CaeManager.Application.Inbound.Comercial.Commands.ContratarLineaInbound.ContratarLineaInboundCommandHandler"] =
            "Plano de Plataforma: el Actor de Plataforma TALVEG bajo Sesión Privilegiada de Aprovisionamiento, sin papel ni " +
            "rol de negocio; lo autorizan la sesión y AutorizacionEscrituraBehavior",
        ["CaeManager.Application.Inbound.Comercial.Commands.CorregirLineaInbound.CorregirLineaInboundCommandHandler"] =
            "Plano de Plataforma, como ContratarLineaInboundCommandHandler",
        ["CaeManager.Application.Inbound.Comercial.Commands.DarDeBajaLineaInbound.DarDeBajaLineaInboundCommandHandler"] =
            "Plano de Plataforma, como ContratarLineaInboundCommandHandler",
        ["CaeManager.Application.Inbound.Comercial.Queries.LineaInboundVigente.LineaInboundVigenteQueryHandler"] =
            "Lo pide el menú y la propia pantalla para saber si el Tenant activo tiene la línea Inbound: responde solo un " +
            "booleano sobre su propio Tenant, sin datos de Inbound",
        ["CaeManager.Application.Inbound.CatalogoBase.Queries.ObtenerCatalogoBaseQueryHandler"] =
            "Catálogo de Plataforma (Actividades, riesgos y reglas) sin datos de ningún Tenant",
    };

    private static IReadOnlyList<Type> HandlersDeInbound() =>
        typeof(ICommand).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.FullName!.StartsWith(EspacioInbound, StringComparison.Ordinal)
                        && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
            .ToList();

    [Fact]
    public void Todo_handler_de_Inbound_llama_a_la_autorizacion_de_Inbound_o_esta_exento_con_motivo()
    {
        var grafo = new GrafoDeLlamadasIl([typeof(ICommand).Assembly]);
        var alcanzan = grafo.QueAlcanzan([typeof(IAutorizacionInbound).GetMethod(nameof(IAutorizacionInbound.TieneAsync))!]);

        var handlers = HandlersDeInbound();
        handlers.Count.Should().BeGreaterThanOrEqualTo(35, "control del instrumento: la reflexión ve los handlers de Inbound");

        var sinLlamar = handlers
            .Where(h => !Exentos.ContainsKey(h.FullName!))
            .Where(h => !alcanzan.Contains(ClaveMetodo.De(h.GetMethod("Handle")!)))
            .Select(h => h.FullName)
            .ToList();

        sinLlamar.Should().BeEmpty("todo handler de Inbound pasa por IAutorizacionInbound; si no, va a Exentos con su motivo");
    }

    [Fact]
    public void Los_exentos_existen_y_no_llaman_a_la_puerta()
    {
        var grafo = new GrafoDeLlamadasIl([typeof(ICommand).Assembly]);
        var alcanzan = grafo.QueAlcanzan([typeof(IAutorizacionInbound).GetMethod(nameof(IAutorizacionInbound.TieneAsync))!]);
        var porNombre = HandlersDeInbound().ToDictionary(h => h.FullName!);

        foreach (var (nombre, motivo) in Exentos)
        {
            porNombre.Should().ContainKey(nombre, "una exención obsoleta se retira");
            alcanzan.Contains(ClaveMetodo.De(porNombre[nombre].GetMethod("Handle")!)).Should()
                .BeFalse($"{nombre} ya llama a la puerta: retira su exención");
            motivo.Should().NotBeNullOrWhiteSpace();
        }
    }
}
