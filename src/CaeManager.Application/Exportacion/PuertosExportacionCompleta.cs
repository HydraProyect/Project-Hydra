using CaeManager.Domain.Common;

namespace CaeManager.Application.Exportacion;

/// <summary>
/// Protege la contraseña del zip de una exportación completa mientras la fila la guarda, entre la solicitud y el
/// cifrado del fichero. La implementación usa ASP.NET Data Protection con un propósito propio: la clave no es la
/// de ninguna otra columna cifrada.
/// </summary>
public interface IProtectorContrasenaExportacion
{
    string Proteger(string contrasena);

    string Desproteger(string contrasenaProtegida);
}

/// <summary>
/// El candado por Tenant propietario que serializa las solicitudes de exportación completa
/// (<c>pg_advisory_xact_lock</c>, espacio de clave propio + TenantId): dentro, el recuento de solicitudes de las
/// últimas 24 horas ve lo que otra solicitud del mismo Tenant ya confirmó, y nadie más registra una hasta el
/// COMMIT. No se reintenta.
/// </summary>
public interface ICandadoExportacionCompleta
{
    Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default);
}

/// <summary>
/// El almacén de los zips de exportación completa. No es <see cref="Common.IFileStorageService"/>: aquel carga el
/// archivo entero en memoria, y una exportación completa puede ser grande. Los ficheros ya van cifrados con la
/// contraseña de la exportación.
/// </summary>
public interface IAlmacenExportacionesCompletas
{
    /// <summary>Abre el zip para leerlo en flujo. Quien lo abre lo cierra.</summary>
    /// <exception cref="FileNotFoundException">No existe, o la ubicación no es del Tenant pedido.</exception>
    Task<Stream> AbrirAsync(Guid tenantId, string ubicacion, CancellationToken cancellationToken = default);
}

/// <summary>El zip que se entrega al descargar. Quien lo recibe cierra <see cref="Contenido"/>.</summary>
public sealed record ExportacionCompletaDescargable(Stream Contenido, string NombreFichero, long TamanoBytes);

/// <summary>
/// Lo que devuelve la solicitud: la única vez que sale la contraseña del zip. <see cref="ToString"/> no la
/// imprime, para que un registro que serialice el resultado por accidente no la deje escrita.
/// </summary>
public sealed record SolicitudExportacionCompletaRegistrada(Guid SolicitudId, string Contrasena, DateTime SolicitadaEnUtc)
{
    public override string ToString() =>
        $"{nameof(SolicitudExportacionCompletaRegistrada)} {{ SolicitudId = {SolicitudId}, SolicitadaEnUtc = {SolicitadaEnUtc:O} }}";
}
