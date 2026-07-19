using CaeManager.Domain.Common;

namespace CaeManager.Application.Common;

/// <summary>
/// Lectura por IA de un documento PDF de Empresa (p. ej. ITA, RNT/TC2) para
/// extraer la lista de trabajadores que aparecen dados de alta en él. La
/// implementación real vive en Infrastructure (Claude, vía el mismo patrón
/// "inerte por defecto" que IAsistenteIaService: sin ApiKey configurada, no
/// hay lectura automática y el resto de la subida de documento sigue
/// funcionando igual).
/// </summary>
public interface IExtraccionTrabajadoresIaService
{
    Task<Result<IReadOnlyList<TrabajadorExtraidoDto>>> ExtraerAsync(byte[] contenidoPdf, CancellationToken cancellationToken = default);
}

public record TrabajadorExtraidoDto(string Nombre, string Apellidos, string Dni);
