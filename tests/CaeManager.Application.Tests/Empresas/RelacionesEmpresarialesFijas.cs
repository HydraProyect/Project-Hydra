using CaeManager.Domain.RelacionesEmpresariales;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// Relaciones Empresariales vigentes fijas, para los handlers que solo las leen: <see cref="ClienteIds"/> son los Clientes
/// empresariales de cualquier Empresa por la que se pregunte. Las escrituras no se usan.
/// </summary>
public sealed class RelacionesEmpresarialesFijas(params Guid[] clienteIds) : IRelacionEmpresarialRepository
{
    public IReadOnlyList<Guid> ClienteIds { get; } = clienteIds;

    public Task<ContrapartesVigentes> ObtenerContrapartesVigentesAsync(Guid proveedoraId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ClienteIds.Count == 0 ? ContrapartesVigentes.Vacias : new ContrapartesVigentes(ClienteIds.ToList(), [], []));

    public void Agregar(RelacionEmpresarial relacion) => throw new NotSupportedException();

    public Task<bool> AgregarSiNoVigenteAsync(Guid proveedoraId, Guid clienteId, DateTime ahora, Guid? enmarcadaEnId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> CerrarVigenteAsync(Guid proveedoraId, Guid clienteId, DateTime ahora, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RelacionEmpresarial?> ObtenerVigentePorParAsync(Guid proveedoraId, Guid clienteId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Guid?> ObtenerCandidatoUnicoParaEnmarcarAsync(IReadOnlyCollection<Guid> empresaIdsCandidatas, Guid clienteId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> CreariaUnCicloAsync(Guid relacionId, Guid propuestaEnmarcadaEnId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
