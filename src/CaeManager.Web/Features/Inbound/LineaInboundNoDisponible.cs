using CaeManager.Web.Components.Layout;

namespace CaeManager.Web.Features.Inbound;

/// <summary>
/// Implementación de <see cref="ILineaInboundVigente"/> (puerto del Núcleo) mientras no exista la lectura de la línea Inbound
/// del Producto Contratado: dice siempre que no, de modo que la sección «Inbound» no aparece en el menú de nadie (falla cerrado).
/// </summary>
public sealed class LineaInboundNoDisponible : ILineaInboundVigente
{
    // shortcut: sin backend, cablear cuando haya una Query de Application que lea LineaProductoContratado.Inbound del Producto
    // Contratado del Tenant beneficiario (hoy solo la base lo comprueba, DN-7). DECISIÓN NECESARIA de la coordinadora.
    public Task<bool> EstaVigenteAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}
