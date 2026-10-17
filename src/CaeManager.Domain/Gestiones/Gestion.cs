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
    public Guid? MensajeOrigenId { get; private set; }

    /// <summary>
    /// Instante (UTC) en que la Gestión pasó a Completada; null mientras está
    /// Pendiente y en las completadas antes de que existiera el campo (no se
    /// reconstruyen). Deliberadamente no se registra quién la completó: el
    /// Cuadro de dirección mide ritmo y antigüedad, no fiscaliza al Gestor CAE.
    /// </summary>
    public DateTime? CompletadaEnUtc { get; private set; }

    private Gestion()
    {
    }

    public Gestion(Guid trabajadorId, Guid centroId, Guid tipoDocumentoId, Guid? mensajeOrigenId = null, string? notas = null)
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
        MensajeOrigenId = mensajeOrigenId;
        EstablecerNotas(notas);
    }

    /// <summary>
    /// Marca la Gestión como Completada en <paramref name="ahoraUtc"/>.
    /// Idempotente: completar una ya completada conserva el instante original.
    /// </summary>
    public void Completar(DateTime ahoraUtc)
    {
        if (ahoraUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("El instante en que se completa la gestión debe estar en UTC.", nameof(ahoraUtc));
        if (Estado == EstadoGestion.Completada)
            return;

        Estado = EstadoGestion.Completada;
        CompletadaEnUtc = ahoraUtc;
    }

    /// <summary>Vuelve a Pendiente y olvida cuándo se había completado.</summary>
    public void Reabrir()
    {
        Estado = EstadoGestion.Pendiente;
        CompletadaEnUtc = null;
    }

    public void ActualizarNotas(string? notas) => EstablecerNotas(notas);

    private void EstablecerNotas(string? notas)
    {
        if (notas?.Length > LongitudMaximaNotas)
            throw new ArgumentException($"Las notas no pueden superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        Notas = notas;
    }
}
