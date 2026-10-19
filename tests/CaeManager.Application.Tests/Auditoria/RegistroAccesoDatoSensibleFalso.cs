using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Auditoria;

/// <summary>
/// Anota cada lectura que un handler de credenciales registra, o falla a
/// propósito para probar que sin registro no se entrega el dato.
/// </summary>
public class RegistroAccesoDatoSensibleFalso(bool falla = false) : IRegistroAccesoDatoSensibleService
{
    public List<(string EntidadTipo, Guid EntidadId)> Registrados { get; } = [];

    public Task RegistrarAsync(string entidadTipo, Guid entidadId, CancellationToken cancellationToken = default)
    {
        if (falla)
            throw new InvalidOperationException("Fallo simulado al registrar el acceso.");

        Registrados.Add((entidadTipo, entidadId));
        return Task.CompletedTask;
    }

    /// <summary>Las lecturas registradas con el dato leído (usuario, contraseña…).</summary>
    public List<(string EntidadTipo, Guid EntidadId, string DatoLeido)> Lecturas { get; } = [];

    public async Task RegistrarLecturaAsync(string entidadTipo, Guid entidadId, string datoLeido, CancellationToken cancellationToken = default)
    {
        await RegistrarAsync(entidadTipo, entidadId, cancellationToken);
        Lecturas.Add((entidadTipo, entidadId, datoLeido));
    }
}
