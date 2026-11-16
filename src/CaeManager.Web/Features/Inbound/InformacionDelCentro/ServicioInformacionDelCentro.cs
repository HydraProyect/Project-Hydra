namespace CaeManager.Web.Features.Inbound.InformacionDelCentro;

/// <summary>Modalidad del acuse que pide un documento de Información del Centro: la Empresa contratista lo firma o acusa su lectura.</summary>
public enum ModalidadInformacion
{
    Firmado,
    Leido,
}

/// <summary>El acuse de una Empresa contratista sobre la versión vigente de un documento.</summary>
public sealed record AcuseEmpresaContratista(string Empresa, bool AlDia, string Estado, string Quien, string TrabajadoresInformados);

/// <summary>Una versión publicada de un documento, con lo que cambió.</summary>
public sealed record VersionInformacion(string Version, DateOnly Fecha, string Cambio);

/// <summary>Un documento de Información del Centro con su versión vigente y los acuses de las Empresas contratistas del Centro.</summary>
public sealed record DocumentoInformacionCentro(
    Guid Id, Guid CentroId, string Centro, string Nombre, string Tipo, ModalidadInformacion Modalidad,
    string Version, DateOnly Fecha, string Explicacion, IReadOnlyList<AcuseEmpresaContratista> Empresas,
    IReadOnlyList<VersionInformacion> Versiones)
{
    public int EmpresasTotal => Empresas.Count;

    public int EmpresasAlDia => Empresas.Count(e => e.AlDia);

    /// <summary>Hay Empresas contratistas sin el acuse de la versión vigente: no pueden asignar a nadie más.</summary>
    public bool Pendiente => EmpresasAlDia < EmpresasTotal;
}

/// <summary>
/// Puerto de «Información del Centro» (tarjetas 15 y pestaña de la 14): documentos por Centro con versión, acuse por Empresa
/// contratista y declaración por Trabajador. Devuelve null cuando la lectura no está disponible.
/// </summary>
public interface IInformacionDelCentro
{
    /// <param name="centroId">Solo los documentos de este Centro; null para los de todos los Centros.</param>
    Task<IReadOnlyList<DocumentoInformacionCentro>?> ListarAsync(Guid? centroId, CancellationToken cancellationToken = default);
}

/// <summary>Implementación vacía mientras no exista la información del Centro de B-29.</summary>
public sealed class InformacionDelCentroNoDisponible : IInformacionDelCentro
{
    // shortcut: sin backend, cablear cuando entre B-29 (Información del Centro, Publicar, plazo del acuse, acuse y declaración).
    // Publicar una versión nueva y Añadir un documento tampoco existen hasta entonces; la cifra del plazo del acuse es D-14 (la fija Chris).
    public Task<IReadOnlyList<DocumentoInformacionCentro>?> ListarAsync(Guid? centroId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DocumentoInformacionCentro>?>(null);
}
