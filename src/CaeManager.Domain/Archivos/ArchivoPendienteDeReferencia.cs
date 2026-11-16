using CaeManager.Domain.Common;

namespace CaeManager.Domain.Archivos;

/// <summary>
/// Un archivo que ya está en el almacenamiento del Tenant propietario y que todavía puede no tener ningún Documento ni
/// versión de plantilla que lo referencie (issue #2123, decisión de 2026-11-16: opción A). Se apunta justo después de
/// guardar el archivo y antes de crear la entidad que lo referenciará, con un guardado propio, para que un fallo
/// posterior del alta (una excepción, una caída de la base, una cancelación o un commit ambiguo) no lo deje huérfano
/// sin rastro.
///
/// <para>
/// La fila es del Tenant propietario de los datos (<see cref="EntidadConTenant.TenantId"/>, con su RLS), no de la
/// plataforma: el archivo vive en la carpeta de ese Tenant y su borrado lo decide lo que ese Tenant referencia. El alta
/// que se confirma quita su fila; si no llega a quitarla, el barrido periódico la descarta cuando, pasadas 24 h desde
/// <see cref="GuardadoEnUtc"/>, comprueba que un Documento o una versión de plantilla referencia la clave, y solo borra
/// el archivo si ninguno la referencia.
/// </para>
/// </summary>
public class ArchivoPendienteDeReferencia : EntidadConTenant
{
    /// <summary>La misma longitud que <c>Documento.ArchivoUrl</c>, la clave más larga que se apunta aquí.</summary>
    public const int LongitudMaximaClave = Documentos.Documento.LongitudMaximaArchivoUrl;

    /// <summary>La clave opaca que devolvió el almacenamiento al guardar el archivo.</summary>
    public string Clave { get; private set; } = string.Empty;

    /// <summary>Cuándo se apuntó, en la hora de la base: desde aquí cuentan las 24 h del barrido.</summary>
    public DateTime GuardadoEnUtc { get; private set; }

    private ArchivoPendienteDeReferencia() { }

    public ArchivoPendienteDeReferencia(string clave, DateTime guardadoEnUtc)
    {
        if (string.IsNullOrWhiteSpace(clave))
            throw new ArgumentException("Un archivo pendiente de referencia tiene clave.", nameof(clave));
        if (clave.Length > LongitudMaximaClave)
            throw new ArgumentException("La clave del archivo pasa de la longitud admitida.", nameof(clave));

        Clave = clave;
        GuardadoEnUtc = DateTime.SpecifyKind(guardadoEnUtc, DateTimeKind.Utc);
    }
}
