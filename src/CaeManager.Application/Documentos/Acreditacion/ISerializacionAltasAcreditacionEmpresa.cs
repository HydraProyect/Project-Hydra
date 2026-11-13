using CaeManager.Domain.Common;

namespace CaeManager.Application.Documentos.Acreditacion;

/// <summary>
/// Serializa, dentro de un Tenant, las altas que ponen un Documento de Empresa ante los accesos de plataforma de sus
/// Centros (regla A-1): el alta de un Documento de Empresa (crear, renovar con archivo, generar desde plantilla) y toda
/// operación que cambie a qué Centros llega: el alta de un acceso de plataforma, empezar a exigir un tipo en un Centro o
/// de serie (EstablecerDocumentacionRequeridaCentro, EliminarDocumentacionRequeridaCentro, EditarTipoDocumento), volver
/// a gestión CAE (EstablecerGestionCaeCentro, EjecutarImportacionMaestra al confirmar, en los dos modos), restaurar un Centro o un
/// Documento de Empresa (RestaurarCentro, RestaurarDocumento) y crear o reabrir una Asignación (CrearAsignacion,
/// CrearAsignaciones, ReactivarAsignacion, EjecutarImportacion con una hoja de Asignaciones). Revisión Codex de la PR
/// #2074, ronda 3, hallazgos 2 a 4.
///
/// <para>
/// Sin ella, las dos altas simultáneas leen antes de que la otra confirme: el Documento no ve el acceso nuevo, el acceso
/// no ve el Documento nuevo, y el par queda sin crear (revisión Codex de la PR #2074, ronda 2, hallazgo 1). Con ella, la
/// operación corre en una transacción que toma primero un candado de asesoramiento de transacción del Tenant en curso
/// (<c>pg_advisory_xact_lock</c>), antes de cualquier lectura de <see cref="IAltaAcreditacionesPlataformaService"/>: quien
/// llega segundo espera al COMMIT del primero y, en READ COMMITTED, ve lo que confirmó.
/// </para>
///
/// <para>
/// La clave es el Tenant, no la Empresa: el acceso de un Centro afecta a los Documentos de su Empresa y de las Empresas
/// de quienes trabajan en él, y derivar ese conjunto exigiría una lectura previa al candado. Solo esperan entre sí estas
/// operaciones, y el candado dura lo que su guardado. Cada una lo toma al empezar su transacción, antes de cualquier otro
/// candado de la operación; dentro de la misma transacción, volver a pedirlo no espera (PostgreSQL lo cuenta otra vez y
/// lo suelta todo al terminar). Mismo mecanismo que <c>IBloqueoEstadoOperativoTenant</c>; el candado
/// no toca ninguna tabla, así que la RLS no interviene, y <c>cae_app_runtime</c> no necesita ningún privilegio para él.
/// </para>
///
/// <para>
/// La operación puede ejecutarse más de una vez (estrategia de reintentos, ver <see cref="Common.ITransaccionDeComando"/>):
/// tiene que agregar y leer dentro lo que guarda. Si ya hay una transacción en curso, la usa y el candado dura lo que ella.
/// </para>
///
/// <para>
/// Ninguna decisión tomada con lo leído antes de llamar aquí vale dentro de la operación: una validación, una exención
/// («es la clave que ya tenía», «no cambió»), una comprobación de duplicado o de estado se vuelve a tomar sobre lo que la
/// operación relee bajo el candado. Lo leído antes solo sirve para rechazar pronto, porque un rechazo no escribe nada.
/// Quedan fuera los datos que no cambian (Ids, Tenant propietario) y los que ninguna operación escribe bajo este candado,
/// porque releerlos aquí no ordenaría nada. Revisión Codex de la PR #2074, rondas 3, 4 y 6.
/// </para>
/// </summary>
public interface ISerializacionAltasAcreditacionEmpresa
{
    Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default);
}
