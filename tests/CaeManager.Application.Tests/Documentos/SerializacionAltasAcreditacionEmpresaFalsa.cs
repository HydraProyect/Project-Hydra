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

    /// <summary>
    /// Se ejecuta al tomar el candado, antes de la operación: deja ver qué había hecho el llamante antes de serializar
    /// y simular lo que otra alta confirmó mientras esta esperaba.
    /// </summary>
    public Action? AlTomarElCandado { get; set; }

    public Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
    {
        Llamadas++;
        AlTomarElCandado?.Invoke();
        return operacion(cancellationToken);
    }
}