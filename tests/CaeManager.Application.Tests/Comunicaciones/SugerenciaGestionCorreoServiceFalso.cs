using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>Fake en memoria — nunca llama a un proveedor de IA real (ver CODING_STANDARDS.md).</summary>
public class SugerenciaGestionCorreoServiceFalso : ISugerenciaGestionCorreoService
{
    public List<(Guid MensajeId, Guid ClienteId)> Llamadas { get; } = [];

    public ResultadoDeteccionGestionDto Resultado { get; set; } = ResultadoDeteccionGestionDto.Vacio;

    public Task<ResultadoDeteccionGestionDto> ProcesarAsync(Mensaje mensaje, Guid clienteId, CancellationToken cancellationToken = default)
    {
        Llamadas.Add((mensaje.Id, clienteId));
        return Task.FromResult(Resultado);
    }
}
