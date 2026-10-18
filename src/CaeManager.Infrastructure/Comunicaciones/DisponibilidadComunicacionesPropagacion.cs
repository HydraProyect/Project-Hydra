using CaeManager.Application.Documentos.Commands.GestionarPropagacion;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Comunicaciones;

public sealed class DisponibilidadComunicacionesPropagacion(IOptions<ComunicacionesOptions> opciones)
    : IDisponibilidadComunicacionesPropagacion
{
    public bool Activa => opciones.Value.Activo;
}
