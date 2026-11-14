namespace CaeManager.Domain.Auditoria;

/// <summary>
/// Solo escritura, a propósito — mismo criterio que
/// <see cref="RegistroAuditoria"/>: la lectura del rastro no pasa por un
/// repositorio de agregado (que cualquier handler podría inyectar sin pasar
/// por la superficie de consulta acotada), sino por
/// <c>IAuditoriaQueryContext.RegistrosAccesoDocumentoSensible</c>, expuesto
/// solo a la query que la sirve.
/// </summary>
public interface IRegistroAccesoDocumentoSensibleRepository
{
    /// <summary>
    /// Agrega y guarda en un solo paso — no un <c>Agregar</c> + <c>IUnitOfWork</c>
    /// separado como el resto de repositorios de este proyecto, porque el camino
    /// depende de la conexión: una Sesión Privilegiada de plataforma (ADR-011
    /// § 4bis) conecta bajo <c>cae_app_soporte</c>, sin <c>INSERT</c> sobre
    /// ninguna tabla — deliberado, ver <c>TenantRlsConnectionInterceptor</c> —, y
    /// su registro se escribe por la función de base
    /// <c>app_registrar_acceso_documento_sensible_por_soporte</c>, que vuelve a
    /// comprobar la sesión. Distinguir y traducir esos fallos exige conocer el
    /// proveedor de EF (<c>Npgsql.PostgresException</c>), y Application tiene
    /// prohibido depender de él (<c>FronterasDeCapaTests.Application_no_depende_de_un_proveedor_EF_concreto</c>).
    ///
    /// <para>
    /// Devuelve <c>false</c> — sin propagar la excepción — cuando el registro no
    /// quedó escrito por falta de privilegio o porque la base rechazó la sesión;
    /// quien llama tiene que fallar en cerrado y no servir el contenido (DEC-36,
    /// REC-208). Cualquier otro fallo de guardado se propaga tal cual.
    /// </para>
    /// </summary>
    Task<bool> GuardarAsync(RegistroAccesoDocumentoSensible registro, CancellationToken cancellationToken = default);
}
