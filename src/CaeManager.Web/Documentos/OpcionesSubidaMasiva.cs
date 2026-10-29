namespace CaeManager.Web.Documentos;

/// <summary>
/// Topes de lo que la Subida múltiple de Documentos puede retener mientras los
/// archivos esperan a leerse o a que una persona los confirme (sección
/// <c>SubidaMasiva</c> de la configuración). Cierra S-C-3 de la auditoría del
/// 2026-10-28 (OWASP API4:2023, ASVS 4.0.3 V12.1.1, CWE-400).
///
/// <para>
/// Lo retenido ya no vive en <c>byte[]</c> del circuito Blazor sino en ficheros
/// temporales (<see cref="AlmacenTemporalSubidaMasiva"/>); en memoria solo quedan
/// el archivo que se está leyendo, la vista previa abierta y las miniaturas, y
/// estas también descuentan de estos topes. Los tres topes cuentan lo mismo —bytes
/// retenidos— a tres escalas: la pantalla abierta, el Tenant de origen del usuario
/// y el proceso entero, de modo que ni un circuito ni un Tenant puedan llenar el
/// disco que comparte el servidor.
/// </para>
///
/// <para>
/// Las cifras por defecto son una elección técnica prudente, no una decisión de
/// producto: <see cref="MaximoBytesPorCircuito"/> conserva lo que un lote de 60
/// archivos de 10 MB podía ocupar antes, para no rechazar ningún lote que hoy se
/// acepta; el de Tenant deja trabajar a la vez a algo menos de dos pantallas
/// llenas del mismo Tenant, y el global acota el disco en el peor caso.
/// </para>
/// </summary>
public sealed class OpcionesSubidaMasiva
{
    public const string SeccionConfiguracion = "SubidaMasiva";

    /// <summary>Bytes retenidos por una pantalla de Subida múltiple, sumando todos sus lotes.</summary>
    public long MaximoBytesPorCircuito { get; set; } = 600L * 1024 * 1024;

    /// <summary>Bytes retenidos entre todas las pantallas abiertas por usuarios de un mismo Tenant de origen.</summary>
    public long MaximoBytesPorTenant { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Bytes retenidos entre todas las pantallas del proceso.</summary>
    public long MaximoBytesGlobal { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// Raíz de los temporales. Vacía: <c>talveg-subida-masiva</c> dentro del
    /// directorio temporal del sistema.
    /// </summary>
    public string? DirectorioTemporal { get; set; }

    /// <summary>
    /// Una pantalla tiene que poder retener al menos un archivo del máximo admitido, y
    /// los topes van de menor a mayor: circuito, Tenant y proceso.
    /// </summary>
    public bool SonValidas() =>
        MaximoBytesPorCircuito >= CaeManager.Application.Common.LimitesArchivoSubido.TamanoMaximoBytes
        && MaximoBytesPorCircuito <= MaximoBytesPorTenant
        && MaximoBytesPorTenant <= MaximoBytesGlobal;
}
