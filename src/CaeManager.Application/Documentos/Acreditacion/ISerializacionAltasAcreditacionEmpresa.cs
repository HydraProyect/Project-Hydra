using CaeManager.Domain.Common;

namespace CaeManager.Application.Documentos.Acreditacion;

/// <summary>
/// Serializa, dentro de un Tenant, las altas que ponen un Documento de Empresa ante los accesos de plataforma de sus
/// Centros (regla A-1): el alta de un Documento de Empresa y el de un acceso de plataforma.
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
/// dos altas, y el candado dura lo que su guardado. Mismo mecanismo que <c>IBloqueoEstadoOperativoTenant</c>; el candado
/// no toca ninguna tabla, así que la RLS no interviene, y <c>cae_app_runtime</c> no necesita ningún privilegio para él.
/// </para>
///
/// <para>
/// La operación puede ejecutarse más de una vez (estrategia de reintentos, ver <see cref="Common.ITransaccionDeComando"/>):
/// tiene que agregar y leer dentro lo que guarda. Si ya hay una transacción en curso, la usa y el candado dura lo que ella.
/// </para>
/// </summary>
public interface ISerializacionAltasAcreditacionEmpresa
{
    Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default);
}
