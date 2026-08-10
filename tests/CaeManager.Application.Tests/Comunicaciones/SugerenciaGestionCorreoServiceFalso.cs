using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>Fake en memoria — nunca llama a un proveedor de IA real (ver CODING_STANDARDS.md).</summary>
public class SugerenciaGestionCorreoServiceFalso : ISugerenciaGestionCorreoService
{
    public List<(Guid MensajeId, Guid ClienteId)> Llamadas { get; } = [];

    public Task<SugerenciaGestionCorreo?> ProcesarAsync(Mensaje mensaje, Guid clienteId, CancellationToken cancellationToken = default)
    {
        Llamadas.Add((mensaje.Id, clienteId));
        return Task.FromResult<SugerenciaGestionCorreo?>(null);
    }
}
