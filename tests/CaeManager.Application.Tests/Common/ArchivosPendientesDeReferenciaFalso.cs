using CaeManager.Application.Common.Archivos;
using CaeManager.Domain.Archivos;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Outbox en memoria con la misma semántica que el repositorio real: <see cref="ObtenerApuntadosHastaAsync"/> filtra por
/// la hora, y <see cref="ReferenciadasAsync"/> responde con lo que el test declare referenciado.
/// </summary>
public class ArchivosPendientesDeReferenciaFalso : IArchivosPendientesDeReferencia
{
    private readonly List<ArchivoPendienteDeReferencia> _filas = [];

    public IReadOnlyList<ArchivoPendienteDeReferencia> Filas => _filas;

    /// <summary>La hora con la que <see cref="ApuntarAsync"/> apunta (la de la base, en el real).</summary>
    public DateTime Ahora { get; set; } = DateTime.UtcNow;

    /// <summary>Las claves que algún Documento o versión de plantilla referencia.</summary>
    public HashSet<string> Referenciadas { get; } = [];

    public Exception? ExcepcionAlApuntar { get; set; }

    /// <summary>Si se informa, la consulta devuelve también estas filas aunque no hayan vencido: prueba la defensa del servicio.</summary>
    public bool DevolverTambienNoVencidas { get; set; }

    public ArchivoPendienteDeReferencia Sembrar(string clave, DateTime guardadoEnUtc)
    {
        var fila = new ArchivoPendienteDeReferencia(clave, guardadoEnUtc);
        _filas.Add(fila);
        return fila;
    }

    public Task ApuntarAsync(string clave, CancellationToken cancellationToken = default)
    {
        if (ExcepcionAlApuntar is not null)
            throw ExcepcionAlApuntar;
        Sembrar(clave, Ahora);
        return Task.CompletedTask;
    }

    public Task QuitarAsync(string clave, CancellationToken cancellationToken = default)
    {
        _filas.RemoveAll(f => f.Clave == clave);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ArchivoPendienteDeReferencia>> ObtenerApuntadosHastaAsync(
        DateTime limiteUtc, int maximo, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ArchivoPendienteDeReferencia>>(_filas
            .Where(f => DevolverTambienNoVencidas || f.GuardadoEnUtc <= limiteUtc)
            .OrderBy(f => f.GuardadoEnUtc)
            .Take(maximo)
            .ToList());

    public Task<IReadOnlySet<string>> ReferenciadasAsync(IReadOnlyCollection<string> claves, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<string>>(claves.Where(Referenciadas.Contains).ToHashSet());

    public Task DescartarAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _filas.RemoveAll(f => f.Id == id);
        return Task.CompletedTask;
    }
}
