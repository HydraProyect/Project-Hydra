using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Inbound.CentroInbound;
using CaeManager.Web.Features.Inbound.Configuracion;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Servicios;
using CaeManager.Web.Features.Inbound.InformacionDelCentro;
using CaeManager.Web.Features.Inbound.Plantillas.Servicios;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Features.Inbound;

/// <summary>
/// Registro de los servicios de Web de las pantallas de Inbound. La línea Inbound vigente se lee del Producto Contratado
/// (<see cref="LineaInboundVigenteDelProductoContratado"/>). Las lecturas que todavía no tienen backend fusionado
/// (actividad de un contrato, selector de Empresas contratistas) llevan aquí su implementación vacía, con la marca
/// «shortcut: sin backend, cablear cuando entre B-xx» en la propia implementación.
/// </summary>
public static class InboundServicios
{
    public static IServiceCollection Registrar(IServiceCollection servicios)
    {
        servicios.AddScoped<ILineaInboundVigente, LineaInboundVigenteDelProductoContratado>();
        servicios.AddScoped<IEsTenantTecInbound, EsTenantTecPorAutorizacionConcedida>();
        servicios.AddScoped<IActividadDeContratoAcceso, ActividadDeContratoAccesoNoDisponible>();
        servicios.AddScoped<ISelectorEmpresasContratistas, SelectorEmpresasContratistasNoDisponible>();
        servicios.AddScoped<IAptosHoyDelCentro, AptosHoyDelCentroNoDisponible>();
        servicios.AddScoped<IInformacionDelCentro, InformacionDelCentroNoDisponible>();
        servicios.AddScoped<ResolutorContratosAcceso>();
        servicios.AddScoped<IUsoDePlantillasRequisitos, UsoDePlantillasRequisitosNoDisponible>();
        servicios.AddScoped<ResolutorPlantillasRequisitos>();
        servicios.AddScoped<IServicioNoAplica, ServicioNoAplicaSinBackend>();
        servicios.AddScoped<IServicioReglasAviso, ServicioReglasAvisoSinBackend>();
        servicios.AddScoped<IServicioGobiernoInbound, ServicioGobiernoInboundSinBackend>();
        return servicios;
    }
}
