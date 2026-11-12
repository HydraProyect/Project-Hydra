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

    /// <summary>
    /// Si se informa, un primer intento con éxito se da por confirmado en la base pero comunicado como un error transitorio:
    /// se ejecuta esto y se repite la operación, como hace la estrategia de reintentos, y se devuelve lo que diga el reintento.
    /// </summary>
    public Action? AntesDeReintentarTrasConfirmar { get; set; }

    /// <summary>
    /// Lo que devolvió la última operación: un fallo deshace la transacción del candado real. Con
    /// <see cref="AntesDeReintentarTrasConfirmar"/>, es lo que devolvió el reintento.
    /// </summary>
    public Result? UltimoResultado { get; private set; }

    public async Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
    {
        Llamadas++;
        AlTomarElCandado?.Invoke();
        UltimoResultado = await operacion(cancellationToken);
        if (UltimoResultado.EsFallido || AntesDeReintentarTrasConfirmar is not { } antesDeReintentar)
            return UltimoResultado;

        antesDeReintentar();
        UltimoResultado = await operacion(cancellationToken);
        return UltimoResultado;
    }
}