using CaeManager.Domain.Common;

namespace CaeManager.Domain.Gestiones;

/// <summary>
/// Tarea de gestión documental de un Trabajador en un Centro concreto —
/// p. ej. "renovar EPI de Juan Pérez en el Centro X". Cuando el mismo
/// Trabajador está de alta en varios Centros a la vez, se crea una Gestion
/// por cada uno (ver CrearGestionesParaTrabajadorCommand): el Gestor las va
/// cerrando una a una, porque la renovación real (entregar el EPI, firmar el
/// justificante) ocurre centro a centro, no de una sola vez.
///
/// Deliberadamente no crea ni referencia ningún Documento: es una tarea de
/// seguimiento ("hay que hacer esto"), no el justificante en sí. Cuando el
/// Gestor complete el trámite real subirá el Documento correspondiente desde
/// el flujo normal de Documentos — Gestion solo registra que hacía falta y
/// que ya se atendió.
/// </summary>
public class Gestion : EntidadBase
{
    public const int LongitudMaximaNotas = 1000;

    public Guid TrabajadorId { get; private set; }
    public Guid CentroId { get; private set; }
    public Guid TipoDocumentoId { get; private set; }
    public EstadoGestion Estado { get; private set; } = EstadoGestion.Pendiente;
    public string? Notas { get; private set; }

    /// <summary>Mensaje de correo que originó esta gestión (ver SugerenciaGestionCorreo) — null cuando se crea a mano.</summary>
    public Guid? MensajeCorreoOrigenId { get; private set; }

    private Gestion()
    {
    }

    public Gestion(Guid trabajadorId, Guid centroId, Guid tipoDocumentoId, Guid? mensajeCorreoOrigenId = null, string? notas = null)
    {
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("La gestión debe tener un trabajador.", nameof(trabajadorId));
        if (centroId == Guid.Empty)
            throw new ArgumentException("La gestión debe tener un centro.", nameof(centroId));
        if (tipoDocumentoId == Guid.Empty)
            throw new ArgumentException("La gestión debe tener un tipo de documento.", nameof(tipoDocumentoId));

        TrabajadorId = trabajadorId;
        CentroId = centroId;
        TipoDocumentoId = tipoDocumentoId;
        MensajeCorreoOrigenId = mensajeCorreoOrigenId;
        EstablecerNotas(notas);
    }

    public void Completar() => Estado = EstadoGestion.Completada;

    public void Reabrir() => Estado = EstadoGestion.Pendiente;

    public void ActualizarNotas(string? notas) => EstablecerNotas(notas);

    private void EstablecerNotas(string? notas)
    {
        if (notas?.Length > LongitudMaximaNotas)
            throw new ArgumentException($"Las notas no pueden superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        Notas = notas;
    }
}
