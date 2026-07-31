using CaeManager.Domain.Common;

namespace CaeManager.Application.Common;

/// <summary>
/// Comprobación de concurrencia optimista compartida por los handlers de
/// edición.
///
/// El token de EF (<c>EntidadBase.Version</c> +
/// <c>ConcurrenciaOptimistaInterceptor</c>) no cubre por sí solo el caso que
/// importa: los handlers recargan la entidad justo antes de escribir, así que
/// el valor "original" que EF compara es el que acaba de leer y el UPDATE
/// nunca choca. Eso protege dos escrituras simultáneas, no a dos personas con
/// el formulario abierto — que es donde una pisaba a la otra en silencio.
///
/// Por eso el Command lleva la versión que se vio en pantalla y se compara
/// aquí. Centralizado para que los once agregados editables den exactamente
/// el mismo código de error y el mismo mensaje.
/// </summary>
public static class ConcurrenciaOptimista
{
    public const string CodigoConflicto = "Concurrencia.Conflicto";

    /// <summary>
    /// Devuelve el error si la entidad cambió desde que se cargó, o
    /// <c>null</c> si se puede seguir.
    ///
    /// <see cref="Guid.Empty"/> en <paramref name="versionEsperada"/>
    /// significa "sin comprobación": es lo que envían los llamadores que
    /// todavía no propagan la versión, y lo que permite migrar agregado a
    /// agregado sin dejar ninguna edición rota por el camino.
    /// </summary>
    public static Error? Verificar(EntidadBase entidad, Guid versionEsperada, string nombreParaElUsuario)
    {
        if (versionEsperada == Guid.Empty || entidad.Version == versionEsperada)
            return null;

        return Error.Crear(
            CodigoConflicto,
            $"Otra persona modificó {nombreParaElUsuario} mientras lo editabas. Vuelve a abrirlo para ver los cambios y aplica los tuyos de nuevo.");
    }
}
