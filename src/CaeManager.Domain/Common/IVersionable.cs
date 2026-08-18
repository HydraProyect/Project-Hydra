namespace CaeManager.Domain.Common;

/// <summary>
/// Entidad protegida por concurrencia optimista. Existe porque hasta ahora el
/// token vivía solo en <see cref="EntidadBase"/>, y tanto el marcado del modelo
/// (<c>IsConcurrencyToken</c> en <c>CaeManagerDbContext</c>) como la renovación
/// del valor (<c>ConcurrenciaOptimistaInterceptor</c>) recorrían el modelo
/// buscando <b>esa clase base</b>.
///
/// Los catálogos globales de autorización (<c>AsignacionOperacion</c>,
/// <c>AsignacionCartera</c>) no pueden heredar de <see cref="EntidadBase"/> —
/// no pertenecen a un tenant — pero sí necesitan la protección: sus
/// transiciones de estado son concurrentes por naturaleza (dos administradores
/// a la vez, o un administrador y el job de expiración de vigencias), y el
/// índice único parcial solo cubre las altas, no los <c>UPDATE</c>.
///
/// Declarar la columna sin esta interfaz habría dejado un token <b>inerte</b>:
/// la columna existiría, nunca cambiaría de valor, y el <c>WHERE</c> del
/// <c>UPDATE</c> compararía siempre contra algo que no se mueve — cero
/// protección, y en silencio. Es exactamente el fallo que el comentario de
/// <c>ConcurrenciaOptimistaInterceptor</c> describe.
/// </summary>
public interface IVersionable
{
    Guid Version { get; }
}
