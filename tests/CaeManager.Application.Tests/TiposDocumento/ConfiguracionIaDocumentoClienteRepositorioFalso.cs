using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.TiposDocumento;

public class ConfiguracionIaDocumentoClienteRepositorioFalso : IConfiguracionIaDocumentoClienteRepository
{
    public List<ConfiguracionIaDocumentoCliente> Configuraciones { get; } = [];

    /// <summary>TipoDocumentoId -> Nombre, para simular el JOIN que hace la implementación real.</summary>
    public Dictionary<Guid, string> NombresTipoDocumento { get; } = [];

    public Task<ConfiguracionIaDocumentoCliente?> ObtenerAsync(Guid clienteId, Guid tipoDocumentoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Configuraciones.FirstOrDefault(c => c.ClienteId == clienteId && c.TipoDocumentoId == tipoDocumentoId));

    public Task<IReadOnlyList<ConfiguracionIaDocumentoCliente>> ObtenerPorClienteAsync(Guid clienteId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConfiguracionIaDocumentoCliente>>(Configuraciones.Where(c => c.ClienteId == clienteId).ToList());

    public Task<IReadOnlyList<string>> ObtenerNombresTiposDocumentoSinLecturaIaAsync(Guid clienteId, CancellationToken cancellationToken = default)
    {
        var idsDesactivados = Configuraciones.Where(c => c.ClienteId == clienteId && !c.Activa).Select(c => c.TipoDocumentoId);
        var nombres = idsDesactivados.Where(NombresTipoDocumento.ContainsKey).Select(id => NombresTipoDocumento[id]).Distinct().ToList();
        return Task.FromResult<IReadOnlyList<string>>(nombres);
    }

    public void Agregar(ConfiguracionIaDocumentoCliente configuracion) => Configuraciones.Add(configuracion);

    public void Eliminar(ConfiguracionIaDocumentoCliente configuracion) => Configuraciones.Remove(configuracion);
}
