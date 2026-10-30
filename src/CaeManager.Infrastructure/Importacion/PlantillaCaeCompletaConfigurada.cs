using CaeManager.Application.Importacion;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Importacion;

/// <summary>Lee la sección <see cref="OpcionesPlantillaCaeCompleta.Seccion"/> con la misma definición de «hoja válida» que el lector.</summary>
public sealed class PlantillaCaeCompletaConfigurada(IOptionsMonitor<OpcionesPlantillaCaeCompleta> opciones) : IPlantillaCaeCompletaConfigurada
{
    public bool EstaConfigurada => opciones.CurrentValue.HojasConfiguradas().Count > 0;
}
