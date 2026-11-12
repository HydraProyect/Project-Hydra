using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Domain.Common;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Para los tests que construyen los handlers a mano y no miden la concurrencia: ejecuta la operación sin transacción ni
/// candado. El candado real lo mide <c>SerializacionAltasAcreditacionEmpresaTests</c>.
/// </summary>
public sealed class SerializacionAltasSinBloqueo : ISerializacionAltasAcreditacionEmpresa
{
    public Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default) =>
        operacion(cancellationToken);
}