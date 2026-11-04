namespace CaeManager.Application.Common;

/// <summary>
/// Marcador: este request lee datos del Tenant y, si corre bajo una Sesión Privilegiada,
/// la lectura deja antes una fila en <c>RegistrosActividadSoporte</c> con el Actor real,
/// la Sesión Privilegiada y el Tenant objetivo (issue #2001). Lo hace
/// <see cref="RegistroLecturaSesionPrivilegiadaBehavior{TRequest,TResponse}"/>, en fallo
/// cerrado: si la fila no se escribe, el handler no corre y los datos no se sirven.
///
/// Sin miembros a propósito, como <see cref="IComandoDeAprovisionamiento"/>: lista explícita
/// en vez de heurística de nombre o carpeta. La llevan todos los requests del Importador y todo
/// request que envíe su página (también <c>ObtenerNombreCuentaQuery</c>), salvo
/// <c>RegistrarHistorialImportacionCommand</c>, que solo escribe el historial
/// (<c>RequestsDelImportadorRegistranLecturaPrivilegiadaTests</c>); cualquier otra lectura
/// que se quiera registrar se marca igual.
/// </summary>
public interface ILecturaRegistradaEnSesionPrivilegiada;
