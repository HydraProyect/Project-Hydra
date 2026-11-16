using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Servicios;
using CaeManager.Web.Features.Inbound.Plantillas.Servicios;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Features.Inbound;

/// <summary>
/// Registro de los servicios de Web de las pantallas de Inbound. Las lecturas que todavía no tienen backend fusionado
/// (actividad de un contrato, línea Inbound contratada) llevan aquí su implementación vacía, con la marca
/// «shortcut: sin backend, cablear cuando entre B-xx» en la propia implementación.
/// </summary>
public static class InboundServicios
{
    public static IServiceCollection Registrar(IServiceCollection servicios)
    {
        servicios.AddScoped<ILineaInboundVigente, LineaInboundNoDisponible>();
        servicios.AddScoped<IActividadDeContratoAcceso, ActividadDeContratoAccesoNoDisponible>();
        servicios.AddScoped<ISelectorEmpresasContratistas, SelectorEmpresasContratistasNoDisponible>();
        servicios.AddScoped<ResolutorContratosAcceso>();
        servicios.AddScoped<IUsoDePlantillasRequisitos, UsoDePlantillasRequisitosNoDisponible>();
        servicios.AddScoped<ResolutorPlantillasRequisitos>();
        return servicios;
    }
}
