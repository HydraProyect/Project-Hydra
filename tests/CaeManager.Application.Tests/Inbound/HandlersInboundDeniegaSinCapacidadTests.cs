using System.Reflection;
using System.Runtime.CompilerServices;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Inbound;

/// <summary>
/// Cada handler de Inbound pide su capacidad <b>antes de tocar nada</b> y, si se le niega, responde la denegación sin
/// ningún efecto (B-03; Codex, ronda 1: el doble permisivo de los tests de cada handler y el grafo de IL no ven que un
/// handler ignore el booleano). Se construye cada handler con una puerta que niega y anota la capacidad pedida, y
/// <c>null</c> en todas las demás dependencias, sobre una petición sin inicializar: si el handler leyera la base, el
/// alcance, el usuario o la petición antes de la puerta, fallaría con una excepción. La tabla fija qué capacidad pide cada
/// uno, así que pedir otra (una escritura con LeerInbound, una validación con Gestionar) también pone esto en rojo.
/// </summary>
public class HandlersInboundDeniegaSinCapacidadTests
{
    private const string Espacio = "CaeManager.Application.Inbound.";

    /// <summary>La capacidad que pide cada handler (sin el prefijo <see cref="Espacio"/>).</summary>
    private static readonly Dictionary<string, CapacidadInbound> Esperada = new()
    {
        ["AccesoFisico.Credenciales.EmitirCredencialCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["AccesoFisico.Credenciales.RegenerarCredencialCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["AccesoFisico.Credenciales.RevocarCredencialCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["AutorizacionesLectura.Commands.AsignarTrabajadoresAutorizacion.AsignarTrabajadoresAutorizacionCommandHandler"] = CapacidadInbound.ParticipanteTec,
        ["AutorizacionesLectura.Commands.ConcederAutorizacionLectura.ConcederAutorizacionLecturaCommandHandler"] = CapacidadInbound.ParticipanteTec,
        ["AutorizacionesLectura.Commands.RetirarTrabajadorAutorizacion.RetirarTrabajadorAutorizacionCommandHandler"] = CapacidadInbound.ParticipanteTec,
        ["AutorizacionesLectura.Commands.RevocarAutorizacionLectura.RevocarAutorizacionLecturaCommandHandler"] = CapacidadInbound.ParticipanteTec,
        ["AutorizacionesLectura.Commands.SubirDocumentoRequerimientoTec.SubirDocumentoRequerimientoTecCommandHandler"] = CapacidadInbound.ParticipanteTec,
        ["AutorizacionesLectura.Queries.ObtenerAutorizacionesLectura.ObtenerAutorizacionesLecturaQueryHandler"] = CapacidadInbound.ParticipanteTec,
        ["AutorizacionesLectura.Queries.TieneAutorizacionLecturaConcedida.TieneAutorizacionLecturaConcedidaQueryHandler"] = CapacidadInbound.ParticipanteTec,
        ["AutorizacionesLectura.Queries.ObtenerRequerimientosTec.ObtenerRequerimientosTecQueryHandler"] = CapacidadInbound.ParticipanteTec,
        ["ContratosAcceso.AptosHoy.ObtenerAptosHoyDelCentroQueryHandler"] = CapacidadInbound.LeerInbound,
        ["ContratosAcceso.Commands.ActualizarContratoAcceso.ActualizarContratoAccesoCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["ContratosAcceso.Commands.CancelarContratoAcceso.CancelarContratoAccesoCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["ContratosAcceso.Commands.CerrarContratoAcceso.CerrarContratoAccesoCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["ContratosAcceso.Commands.CrearContratoAcceso.CrearContratoAccesoCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["ContratosAcceso.Commands.EliminarContratoAcceso.EliminarContratoAccesoCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["ContratosAcceso.Commands.FijarCondicionesContratoAcceso.FijarCondicionesContratoAccesoCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["ContratosAcceso.Commands.RenovarContratoAcceso.RenovarContratoAccesoCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["ContratosAcceso.Commands.VincularTenantContratista.VincularTenantContratistaCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["ContratosAcceso.EvidenciasInbound.RegistrarEvidenciaInboundCommandHandler"] = CapacidadInbound.ValidarInbound,
        ["ContratosAcceso.EvidenciasInbound.ValidarEvidenciaInboundCommandHandler"] = CapacidadInbound.ValidarInbound,
        ["ContratosAcceso.EvidenciasInbound.RechazarEvidenciaInboundCommandHandler"] = CapacidadInbound.ValidarInbound,
        ["ContratosAcceso.EvidenciasInbound.ObtenerEvidenciasInboundQueryHandler"] = CapacidadInbound.LeerInbound,
        ["ContratosAcceso.EvidenciasInbound.ObtenerEvidenciaInboundPorIdQueryHandler"] = CapacidadInbound.LeerInbound,
        ["ContratosAcceso.Queries.ObtenerColaValidacionInbound.ObtenerColaValidacionInboundQueryHandler"] = CapacidadInbound.ValidarInbound,
        ["ContratosAcceso.Queries.ObtenerContratoAccesoPorId.ObtenerContratoAccesoPorIdQueryHandler"] = CapacidadInbound.LeerInbound,
        ["ContratosAcceso.Queries.ObtenerContratosAcceso.ObtenerContratosAccesoQueryHandler"] = CapacidadInbound.LeerInbound,
        ["ContratosAcceso.Queries.ObtenerLecturaInbound.ObtenerAutorizacionInboundQueryHandler"] = CapacidadInbound.LeerInbound,
        ["ContratosAcceso.Queries.ObtenerLecturaInbound.ObtenerDocumentosInboundQueryHandler"] = CapacidadInbound.LeerInbound,
        ["ContratosAcceso.Queries.ObtenerLecturaInbound.ObtenerArchivoInboundQueryHandler"] = CapacidadInbound.LeerInbound,
        // Pide primero ValidarInbound (la de la cola) y, solo si la tiene, LeerInbound: con la puerta que niega, solo la primera.
        ["ContratosAcceso.Queries.ObtenerLecturaInbound.ObtenerArchivoParaValidarInboundQueryHandler"] = CapacidadInbound.ValidarInbound,
        ["Requisitos.Commands.AnadirRequisitoAPlantilla.AnadirRequisitoAPlantillaCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["Requisitos.Commands.CrearPlantillaRequisitos.CrearPlantillaRequisitosCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["Requisitos.Commands.CrearRequisitoPropio.CrearRequisitoPropioCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["Requisitos.Commands.EditarRequisitoPlantilla.EditarRequisitoPlantillaCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["Requisitos.Commands.RetirarPlantillaRequisitos.RetirarPlantillaRequisitosCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["Requisitos.Commands.RetirarRequisitoPlantilla.RetirarRequisitoPlantillaCommandHandler"] = CapacidadInbound.GestionarInbound,
        ["Requisitos.Queries.BuscarRequisitosParecidosQueryHandler"] = CapacidadInbound.LeerInbound,
        ["Requisitos.Queries.ObtenerPlantillaRequisitosPorIdQueryHandler"] = CapacidadInbound.LeerInbound,
        ["Requisitos.Queries.ObtenerPlantillasRequisitosQueryHandler"] = CapacidadInbound.LeerInbound,
    };

    /// <summary>Niega toda capacidad y anota cuáles se pidieron.</summary>
    private sealed class PuertaQueNiega : IAutorizacionInbound
    {
        public List<CapacidadInbound> Pedidas { get; } = [];

        public Task<bool> TieneAsync(CapacidadInbound capacidad, CancellationToken cancellationToken)
        {
            Pedidas.Add(capacidad);
            return Task.FromResult(false);
        }
    }

    private static IEnumerable<Type> HandlersConPuerta() =>
        typeof(ICommand).Assembly.GetTypes().Where(t =>
            t is { IsClass: true, IsAbstract: false }
            && t.FullName!.StartsWith(Espacio, StringComparison.Ordinal)
            && t.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IAutorizacionInbound))));

    public static TheoryData<string> Handlers()
    {
        var datos = new TheoryData<string>();
        foreach (var h in HandlersConPuerta()) datos.Add(h.FullName![Espacio.Length..]);
        return datos;
    }

    [Fact]
    public void La_tabla_cubre_exactamente_los_handlers_que_reciben_la_puerta()
    {
        HandlersConPuerta().Select(h => h.FullName![Espacio.Length..]).Should().BeEquivalentTo(Esperada.Keys,
            "un handler Inbound nuevo con la puerta entra en la tabla con su capacidad; uno que la pierde sale");
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task Sin_la_capacidad_responde_la_denegacion_antes_de_tocar_nada(string nombre)
    {
        var tipo = typeof(ICommand).Assembly.GetType(Espacio + nombre)!;
        var puerta = new PuertaQueNiega();
        var constructor = tipo.GetConstructors().Single();
        var argumentos = constructor.GetParameters()
            .Select(p => p.ParameterType == typeof(IAutorizacionInbound) ? puerta : null)
            .ToArray();
        var handler = constructor.Invoke(argumentos);

        var interfaz = tipo.GetInterfaces().Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>));
        var tipoPeticion = interfaz.GetGenericArguments()[0];
        var tipoRespuesta = interfaz.GetGenericArguments()[1];
        var peticion = RuntimeHelpers.GetUninitializedObject(tipoPeticion);
        var handle = interfaz.GetMethod(nameof(IRequestHandler<IRequest<object>, object>.Handle))!;

        // Todo Handle devuelve Task<T>: se espera con dynamic para leer el resultado sin bloquear.
        object? respuesta = await (dynamic)handle.Invoke(handler, [peticion, CancellationToken.None])!;

        puerta.Pedidas.Should().Equal([Esperada[nombre]], "pide su capacidad, una vez, y ninguna otra");
        EsDenegacion(tipoRespuesta, respuesta).Should().BeTrue($"responde la denegación ({tipoRespuesta.Name})");
    }

    private static bool EsDenegacion(Type tipoRespuesta, object? respuesta)
    {
        if (respuesta is Result resultado)
            return resultado.EsFallido && resultado.Error.Codigo == AutorizacionInboundErrores.SinCapacidad.Codigo;
        if (respuesta is null)
            return true;
        // Query de interfaz que responde un booleano (p. ej. el gate del menú del Tenant TEC): la denegación es false.
        if (respuesta is bool permitido)
            return !permitido;
        if (respuesta is System.Collections.IEnumerable lista)
            return !lista.Cast<object>().Any();
        // ResultadoPaginado<T>: sin elementos y con total cero.
        var elementos = tipoRespuesta.GetProperty("Elementos")?.GetValue(respuesta) as System.Collections.IEnumerable;
        var total = tipoRespuesta.GetProperty("TotalElementos")?.GetValue(respuesta) as int?;
        return elementos is not null && !elementos.Cast<object>().Any() && total == 0;
    }
}
