using CaeManager.Application.Gestiones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;

namespace CaeManager.Application.Tests.Gestiones;

/// <summary>
/// Doble de <see cref="INacimientoGestionAutomaticaService"/> para los Commands cuya prueba no mira la Gestión
/// automática: no agrega nada y recuerda con qué Documentos se le llamó, para comprobar que el acto la pide.
/// </summary>
public sealed class NacimientoGestionAutomaticaQueRegistra : INacimientoGestionAutomaticaService
{
    public List<(Documento Nacido, Documento? Sustituido)> Llamadas { get; } = [];

    public Task<GestionAutomatica?> AgregarAsync(Documento nacido, Documento? sustituido, CancellationToken cancellationToken = default)
    {
        Llamadas.Add((nacido, sustituido));
        return Task.FromResult<GestionAutomatica?>(null);
    }
}
