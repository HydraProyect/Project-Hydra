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
    /// separado como el resto de repositorios de este proyecto, porque quien
    /// llama (Application) no puede distinguir el fallo concreto que
    /// justifica no propagar la excepción: una sesión privilegiada de
    /// plataforma (ADR-011 § 4bis) conecta bajo <c>cae_app_soporte</c>, sin
    /// privilegio de escritura sobre ninguna tabla — deliberado, ver
    /// <c>TenantRlsConnectionInterceptor</c> — así que ese <c>INSERT</c> falla
    /// en Postgres por diseño. Detectar esa causa exige conocer el proveedor
    /// de EF (<c>Npgsql.PostgresException</c>), y Application tiene prohibido
    /// depender de él (<c>FronterasDeCapaTests.Application_no_depende_de_un_proveedor_EF_concreto</c>).
    ///
    /// <para>
    /// Devuelve <c>false</c> — sin propagar la excepción — cuando el guardado
    /// falla específicamente por falta de privilegio de escritura; la
    /// descarga del documento debe completarse igual (perder este registro es
    /// preferible a romper el acceso de soporte a una incidencia). Cualquier
    /// otro fallo de guardado se propaga tal cual.
    /// </para>
    /// </summary>
    Task<bool> GuardarAsync(RegistroAccesoDocumentoSensible registro, CancellationToken cancellationToken = default);
}
