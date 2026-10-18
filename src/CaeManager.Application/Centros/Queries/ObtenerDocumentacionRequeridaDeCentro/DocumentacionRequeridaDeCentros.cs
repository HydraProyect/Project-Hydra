using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;

/// <summary>
/// Lo común a la pestaña «Requisitos» de Centro 360 (<see cref="ObtenerDocumentacionRequeridaDeCentroQuery"/>, un
/// Centro) y a la pestaña «Documentación» de Cliente 360
/// (<see cref="ObtenerDocumentacionExigidaDeCliente.ObtenerDocumentacionExigidaDeClienteQuery"/>, todos los Centros
/// con gestión CAE del Cliente empresarial): el catálogo de tipos de Trabajador y de Empresa del Tenant, las filas
/// explícitas de <see cref="TipoDocumentoCentro"/> y la regla de si un tipo aplica a un Centro
/// (<see cref="ResolucionTipoDocumentoCentro"/>).
/// Las dos pantallas tienen que contar lo mismo: si una cambia la regla, cambia aquí para las dos.
/// </summary>
internal static class DocumentacionRequeridaDeCentros
{
    /// <summary>Un tipo del catálogo con su criterio global (<c>TipoDocumento.CuentaParaCumplimiento</c>).</summary>
    internal sealed record TipoDelCatalogo(Guid Id, string Nombre, AmbitoAplicacion AmbitoAplicacion, bool CuentaParaCumplimiento);

    /// <summary>Catálogo de tipos de Trabajador y de Empresa del Tenant, por ámbito y por orden del catálogo.</summary>
    internal static Task<List<TipoDelCatalogo>> CargarCatalogoAsync(
        ITiposDocumentoQueryContext contexto, CancellationToken cancellationToken) =>
        contexto.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador || t.AmbitoAplicacion == AmbitoAplicacion.Empresa)
            .OrderBy(t => t.AmbitoAplicacion).ThenBy(t => t.Orden)
            .Select(t => new TipoDelCatalogo(t.Id, t.Nombre, t.AmbitoAplicacion, t.Requerido == RequisitoDocumental.Si))
            .ToListAsync(cancellationToken);

    /// <summary>Filas explícitas de <see cref="TipoDocumentoCentro"/> de esos Centros, en una sola lectura, por (tipo, Centro).</summary>
    internal static async Task<Dictionary<(Guid TipoDocumentoId, Guid CentroId), TipoDocumentoCentro>> CargarFilasAsync(
        ITiposDocumentoQueryContext contexto, IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken) =>
        (await contexto.TiposDocumentoCentros
            .Where(tc => centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

    /// <summary>Si el Centro pide hoy ese tipo: la fila explícita manda; sin fila, el criterio global del tipo.</summary>
    internal static bool Aplica(
        IReadOnlyDictionary<(Guid TipoDocumentoId, Guid CentroId), TipoDocumentoCentro> filas, TipoDelCatalogo tipo, Guid centroId) =>
        ResolucionTipoDocumentoCentro.Aplica(filas, tipo.Id, centroId, tipo.CuentaParaCumplimiento);

    /// <summary>Si el Centro bloquea el acceso por ese tipo: solo una fila explícita lo hace.</summary>
    internal static bool BloqueaAcceso(
        IReadOnlyDictionary<(Guid TipoDocumentoId, Guid CentroId), TipoDocumentoCentro> filas, Guid tipoDocumentoId, Guid centroId) =>
        filas.TryGetValue((tipoDocumentoId, centroId), out var fila) && fila.BloqueaAcceso;
}
