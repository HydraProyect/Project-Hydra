using CaeManager.Application.Contactos;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// Fake de <see cref="IResolucionDestinatariosAgendaService"/> para los tests
/// de EnviarReclamacionCommandHandler/EnviarReclamacionEmpresaCommandHandler:
/// devuelve una respuesta fija por llamada y graba con qué la llamaron, sin
/// reproducir la lógica real de resolución de agenda (esa la cubre
/// ResolucionDestinatariosAgendaServiceTests).
/// </summary>
public class ResolucionDestinatariosAgendaServiceFalso : IResolucionDestinatariosAgendaService
{
    public IReadOnlyList<DestinatarioAgendaDto> RespuestaResolverAsync { get; set; } = [];
    public IReadOnlyList<DestinatarioAgendaDto> RespuestaResolverParaEmpresaAsync { get; set; } = [];

    public (Guid ClienteId, Guid? CentroId, IReadOnlyList<Guid> TipoDocumentoIds)? UltimaLlamadaResolverAsync { get; private set; }
    public (Guid EmpresaId, IReadOnlyList<Guid> TipoDocumentoIds)? UltimaLlamadaResolverParaEmpresaAsync { get; private set; }

    public Task<IReadOnlyList<DestinatarioAgendaDto>> ResolverAsync(
        Guid clienteId, Guid? centroId, IReadOnlyList<Guid> tipoDocumentoIds, CancellationToken cancellationToken = default)
    {
        UltimaLlamadaResolverAsync = (clienteId, centroId, tipoDocumentoIds);
        return Task.FromResult(RespuestaResolverAsync);
    }

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<DestinatarioAgendaDto>>> ResolverParaClientesAsync(
        Guid? centroId, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> tipoDocumentoIdsPorCliente, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EnviarReclamacionCommandHandler usa ResolverAsync, no la versión por lote.");

    public Task<IReadOnlyList<DestinatarioAgendaDto>> ResolverParaEmpresaAsync(
        Guid empresaId, IReadOnlyList<Guid> tipoDocumentoIds, CancellationToken cancellationToken = default)
    {
        UltimaLlamadaResolverParaEmpresaAsync = (empresaId, tipoDocumentoIds);
        return Task.FromResult(RespuestaResolverParaEmpresaAsync);
    }

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<DestinatarioAgendaDto>>> ResolverParaEmpresasAsync(
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> tipoDocumentoIdsPorEmpresa, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EnviarReclamacionEmpresaCommandHandler usa ResolverParaEmpresaAsync, no la versión por lote.");
}
