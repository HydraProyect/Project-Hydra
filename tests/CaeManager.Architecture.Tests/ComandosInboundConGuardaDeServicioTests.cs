using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Ningún comando Inbound sin guarda de servicio (B-02b; DN-21, punto 3). Todo comando de escritura
/// (<see cref="ICommandBase"/>) de <c>CaeManager.Application.Inbound</c> lleva <see cref="IComandoInbound"/>, salvo los
/// de <see cref="Exentos"/> con su motivo; ninguno de fuera lo lleva; y la guarda que lo comprueba
/// (<see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/>) está registrada en el pipeline. Así un comando Inbound
/// nuevo no puede ejecutarse con papel Outbound por olvido.
///
/// <para>
/// Mide por reflexión sobre el ensamblado compilado, no sobre el texto. Lo que no demuestra: que la guarda falle cerrada
/// (eso lo prueban <c>GuardaPorServicioBehaviorTests</c>) ni que el papel salga de la operación revalidada (los tests de
/// la vía de Operación Inbound y de <c>CurrentUserService</c>).
/// </para>
/// </summary>
public class ComandosInboundConGuardaDeServicioTests
{
    private const string EspacioInbound = "CaeManager.Application.Inbound.";

    /// <summary>Comandos de <c>CaeManager.Application.Inbound</c> que no llevan el marcador, con su motivo.</summary>
    private static readonly Dictionary<string, string> Exentos = new()
    {
        ["CaeManager.Application.Inbound.Comercial.Commands.ContratarLineaInbound.ContratarLineaInboundCommand"] =
            "Plano de Plataforma: lo ejecuta el Actor de Plataforma TALVEG bajo Sesión Privilegiada de Aprovisionamiento, " +
            "que no tiene papel de negocio (tenant isolation ≠ operational delegation ≠ platform privilege)",
        ["CaeManager.Application.Inbound.Comercial.Commands.CorregirLineaInbound.CorregirLineaInboundCommand"] =
            "Plano de Plataforma, como ContratarLineaInboundCommand",
        ["CaeManager.Application.Inbound.Comercial.Commands.DarDeBajaLineaInbound.DarDeBajaLineaInboundCommand"] =
            "Plano de Plataforma, como ContratarLineaInboundCommand",
        ["CaeManager.Application.Inbound.AutorizacionesLectura.Commands.AsignarTrabajadoresAutorizacion.AsignarTrabajadoresAutorizacionCommand"] =
            "Lado del Tenant TEC (DN-2): lo hace su Administrador propio o el Operador CAE externo que lo opera en Outbound; con el marcador, la guarda lo negaba con papel Outbound. Lo autoriza IAutorizacionInbound con ParticipanteTec",
        ["CaeManager.Application.Inbound.AutorizacionesLectura.Commands.ConcederAutorizacionLectura.ConcederAutorizacionLecturaCommand"] =
            "Lado del Tenant TEC (DN-2): lo hace su Administrador propio o el Operador CAE externo que lo opera en Outbound; con el marcador, la guarda lo negaba con papel Outbound. Lo autoriza IAutorizacionInbound con ParticipanteTec",
        ["CaeManager.Application.Inbound.AutorizacionesLectura.Commands.RetirarTrabajadorAutorizacion.RetirarTrabajadorAutorizacionCommand"] =
            "Lado del Tenant TEC (DN-2): lo hace su Administrador propio o el Operador CAE externo que lo opera en Outbound; con el marcador, la guarda lo negaba con papel Outbound. Lo autoriza IAutorizacionInbound con ParticipanteTec",
        ["CaeManager.Application.Inbound.AutorizacionesLectura.Commands.RevocarAutorizacionLectura.RevocarAutorizacionLecturaCommand"] =
            "Lado del Tenant TEC (DN-2): lo hace su Administrador propio o el Operador CAE externo que lo opera en Outbound; con el marcador, la guarda lo negaba con papel Outbound. Lo autoriza IAutorizacionInbound con ParticipanteTec",
    };

    private static IReadOnlyList<Type> Comandos() =>
        typeof(ICommandBase).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ICommandBase).IsAssignableFrom(t))
            .ToList();

    private static bool EsDeInbound(Type tipo) => tipo.FullName!.StartsWith(EspacioInbound, StringComparison.Ordinal);

    [Fact]
    public void El_inventario_de_comandos_Inbound_no_esta_vacio()
    {
        // Control positivo del instrumento: si la reflexión dejara de ver los comandos, todo lo de abajo pasaría vacío.
        Comandos().Count(EsDeInbound).Should().BeGreaterThanOrEqualTo(24);
    }

    [Fact]
    public void Todo_comando_de_Inbound_lleva_la_guarda_de_servicio_o_esta_exento_con_motivo()
    {
        var sinMarcador = Comandos()
            .Where(t => EsDeInbound(t) && !typeof(IComandoInbound).IsAssignableFrom(t) && !Exentos.ContainsKey(t.FullName!))
            .Select(t => t.FullName)
            .ToList();

        sinMarcador.Should().BeEmpty(
            "un comando de Inbound sin IComandoInbound se ejecutaría con papel Outbound; márcalo o exímelo con motivo");
    }

    [Fact]
    public void Los_exentos_existen_no_llevan_el_marcador_y_tienen_motivo()
    {
        var porNombre = Comandos().ToDictionary(t => t.FullName!);

        foreach (var (nombre, motivo) in Exentos)
        {
            porNombre.Should().ContainKey(nombre, "una exención obsoleta se retira");
            typeof(IComandoInbound).IsAssignableFrom(porNombre[nombre]).Should().BeFalse($"{nombre} no es exento si lleva el marcador");
            motivo.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Ningun_tipo_de_fuera_de_Inbound_lleva_el_marcador()
    {
        typeof(IComandoInbound).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IComandoInbound).IsAssignableFrom(t) && !EsDeInbound(t))
            .Select(t => t.FullName)
            .Should().BeEmpty("el marcador dice que el comando es propio de Inbound");
    }

    [Fact]
    public void La_guarda_por_servicio_esta_en_el_pipeline()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();

        var behaviors = servicios.Where(d => d.ServiceType == typeof(IPipelineBehavior<,>)).Select(d => d.ImplementationType).ToList();
        var guarda = behaviors.IndexOf(typeof(GuardaPorServicioBehavior<,>));
        guarda.Should().BeGreaterThanOrEqualTo(0, "la guarda tiene que estar registrada");
        guarda.Should().BeGreaterThan(behaviors.IndexOf(typeof(AutorizacionEscrituraBehavior<,>)),
            "después de la autorización de escritura, que decide antes las Sesiones Privilegiadas y el rol");
        guarda.Should().BeGreaterThan(behaviors.IndexOf(typeof(GateComercialTenantBehavior<,>)));
        guarda.Should().BeLessThan(behaviors.IndexOf(typeof(ValidationBehavior<,>)), "antes de llegar al handler");
    }
}
