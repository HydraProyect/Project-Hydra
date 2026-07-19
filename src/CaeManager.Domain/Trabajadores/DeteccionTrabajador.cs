using CaeManager.Domain.Common;

namespace CaeManager.Domain.Trabajadores;

/// <summary>
/// Candidato detectado por la lectura IA de un Documento de Empresa (p. ej.
/// ITA/RNT): un trabajador que aparece en el documento pero no está dado de
/// alta en el sistema (<see cref="Trabajadores.TipoDeteccion.Nuevo"/>), o un
/// trabajador activo en el sistema que ya no aparece en el documento
/// (<see cref="Trabajadores.TipoDeteccion.Ausente"/>). Solo se generan para
/// trabajadores de la propia Empresa — nunca de Subcontrata, que tiene su
/// propia documentación separada (ver DeteccionTrabajadoresService).
/// Queda pendiente hasta que un Gestor CAE decide qué hacer (ver
/// ResolverDeteccionNuevoCommand/ResolverDeteccionAusenteCommand).
/// </summary>
public class DeteccionTrabajador : Entity
{
    public const int LongitudMaximaNombre = 100;
    public const int LongitudMaximaApellidos = 150;
    public const int LongitudMaximaDni = 20;

    public Guid DocumentoId { get; private set; }
    public Guid EmpresaId { get; private set; }
    public TipoDeteccion Tipo { get; private set; }
    public string Nombre { get; private set; } = string.Empty;
    public string Apellidos { get; private set; } = string.Empty;
    public string Dni { get; private set; } = string.Empty;

    /// <summary>Solo para <see cref="Trabajadores.TipoDeteccion.Ausente"/> — el Trabajador activo que no apareció en el documento.</summary>
    public Guid? TrabajadorExistenteId { get; private set; }

    public bool Resuelta { get; private set; }

    /// <summary>"Creado"/"Descartado" para Nuevo; "Desactivado"/"Mantenido" para Ausente. Null mientras esté pendiente.</summary>
    public string? AccionTomada { get; private set; }

    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;

    private DeteccionTrabajador()
    {
    }

    private DeteccionTrabajador(
        Guid documentoId, Guid empresaId, TipoDeteccion tipo,
        string nombre, string apellidos, string dni, Guid? trabajadorExistenteId)
    {
        if (documentoId == Guid.Empty)
            throw new ArgumentException("La detección debe estar ligada a un documento.", nameof(documentoId));
        if (empresaId == Guid.Empty)
            throw new ArgumentException("La detección debe estar ligada a una empresa.", nameof(empresaId));
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre detectado es obligatorio.", nameof(nombre));
        if (string.IsNullOrWhiteSpace(dni))
            throw new ArgumentException("El DNI detectado es obligatorio.", nameof(dni));

        DocumentoId = documentoId;
        EmpresaId = empresaId;
        Tipo = tipo;
        Nombre = nombre.Length > LongitudMaximaNombre ? nombre[..LongitudMaximaNombre] : nombre;
        Apellidos = apellidos.Length > LongitudMaximaApellidos ? apellidos[..LongitudMaximaApellidos] : apellidos;
        Dni = dni.Length > LongitudMaximaDni ? dni[..LongitudMaximaDni] : dni;
        TrabajadorExistenteId = trabajadorExistenteId;
    }

    public static DeteccionTrabajador Nuevo(Guid documentoId, Guid empresaId, string nombre, string apellidos, string dni) =>
        new(documentoId, empresaId, TipoDeteccion.Nuevo, nombre, apellidos, dni, null);

    public static DeteccionTrabajador Ausente(Guid documentoId, Guid empresaId, Guid trabajadorExistenteId, string nombre, string apellidos, string dni)
    {
        if (trabajadorExistenteId == Guid.Empty)
            throw new ArgumentException("Una detección de ausencia debe referenciar al trabajador existente.", nameof(trabajadorExistenteId));

        return new(documentoId, empresaId, TipoDeteccion.Ausente, nombre, apellidos, dni, trabajadorExistenteId);
    }

    public void Resolver(string accionTomada)
    {
        if (string.IsNullOrWhiteSpace(accionTomada))
            throw new ArgumentException("Debe indicarse qué acción se tomó.", nameof(accionTomada));

        Resuelta = true;
        AccionTomada = accionTomada;
    }
}
