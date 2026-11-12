using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.Documentos;

public class DocumentoRepositorioFalso : IDocumentoRepository
{
    public List<Documento> Documentos { get; } = [];

    /// <summary>Si se establece, la próxima llamada a <see cref="Agregar"/> la lanza en vez de agregar — para simular un fallo inesperado a mitad de construir un plan.</summary>
    public Exception? ExcepcionAlAgregar { get; set; }

    public Task<Documento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Documentos.FirstOrDefault(d => d.Id == id));

    /// <summary>Si se establece, <see cref="PodriaExistirAsync"/> la lanza: para probar que una comprobación fallida no autoriza borrar.</summary>
    public Exception? ExcepcionAlComprobar { get; set; }

    public Task<bool> PodriaExistirAsync(Guid id, CancellationToken cancellationToken = default) =>
        ExcepcionAlComprobar is { } excepcion ? throw excepcion : Task.FromResult(Documentos.Any(d => d.Id == id));

    /// <summary>
    /// Lo que otra operación confirma en la base mientras se espera el candado. Solo lo ve la lectura «…Actualizado»,
    /// igual que EF, que no repone una instancia ya rastreada salvo que se relea.
    /// </summary>
    public Action<Documento>? CambioEnLaBase { get; set; }

    public Task<Documento?> ObtenerIncluidoEliminadoActualizadoAsync(Guid id, Guid? tenantId, CancellationToken cancellationToken = default)
    {
        var documento = Documentos.FirstOrDefault(d => d.Id == id && d.TenantId == tenantId);
        if (documento is not null)
            CambioEnLaBase?.Invoke(documento);
        return Task.FromResult(documento);
    }

    /// <summary>Lee de la base, como la lectura «…Actualizado»: también ve <see cref="CambioEnLaBase"/>.</summary>
    public Task<Documento?> ObtenerIncluidoEliminadoSinRastrearAsync(Guid id, Guid? tenantId, CancellationToken cancellationToken = default) =>
        ObtenerIncluidoEliminadoActualizadoAsync(id, tenantId, cancellationToken);

    /// <summary>Cuántas veces se pidió la lectura bloqueada; el bloqueo real y su transacción los prueba la integración.</summary>
    public int LecturasBloqueadas { get; private set; }

    public Task<Documento?> BloquearYObtenerIncluidoEliminadoSinRastrearAsync(Guid id, Guid? tenantId, CancellationToken cancellationToken = default)
    {
        LecturasBloqueadas++;
        return ObtenerIncluidoEliminadoActualizadoAsync(id, tenantId, cancellationToken);
    }

    public void Agregar(Documento documento)
    {
        if (ExcepcionAlAgregar is { } excepcion)
            throw excepcion;

        Documentos.Add(documento);
    }
}
