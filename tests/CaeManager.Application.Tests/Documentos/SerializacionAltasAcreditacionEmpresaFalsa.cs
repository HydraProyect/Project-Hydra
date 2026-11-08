using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Ejecuta la operación en el acto y cuenta cuántas veces se pidió serializarla. El candado real (PostgreSQL) lo mide
/// <c>SerializacionAltasAcreditacionEmpresaTests</c> en la suite de integración.
/// </summary>
public sealed class SerializacionAltasAcreditacionEmpresaFalsa : ISerializacionAltasAcreditacionEmpresa
{
    public int Llamadas { get; private set; }

    public Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
    {
        Llamadas++;
        return operacion(cancellationToken);
    }
}