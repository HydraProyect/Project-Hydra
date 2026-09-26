using System.Text.Json;
using System.Text.RegularExpressions;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.AsistenteIa;

/// <summary>
/// Un paso del plan de una <see cref="TareaAsistente"/>: una orden del catálogo
/// con sus datos. Mientras le falte algo es un <b>borrador</b> que vive aquí —
/// nunca un Trabajador, un Centro o una Asignación a medio rellenar— y se
/// reanuda cuando llega el dato.
///
/// <para>
/// Todos los cambios de estado son <c>internal</c> y solo los invoca
/// <see cref="TareaAsistente"/>, que es quien sabe si el plan está confirmado.
/// Por eso un paso ejecutado sin plan confirmado no es representable: no hay
/// camino desde fuera del agregado hasta <see cref="EstadoPasoTareaAsistente.Confirmado"/>,
/// y la base lo sostiene además con una restricción (<c>ConfirmadoEnUtc</c>
/// obligatorio en los estados posteriores a la confirmación).
/// </para>
///
/// <para>
/// <b>Idempotencia</b>: el <see cref="Entity.Id"/> del paso es la clave con la
/// que su Command se ejecuta. Registrar dos veces la misma ejecución no hace
/// nada; registrar un resultado distinto para un paso ya ejecutado es un error.
/// </para>
/// </summary>
public class PasoTareaAsistente : EntidadConTenant
{
    public const int LongitudMaximaOrdenAsistenteId = 64;
    public const int LongitudMaximaDatosJson = 16000;
    public const int LongitudMaximaResumen = 500;
    public const int LongitudMaximaMotivoFallo = 1000;
    public const int MaximoCamposPendientes = 30;
    public const int MaximoAvisos = 30;
    public const int LongitudMaximaCampoPendiente = 64;

    // Mismo formato que los identificadores estables del catálogo de órdenes.
    private static readonly Regex FormatoOrdenAsistenteId = new("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public Guid TareaAsistenteId { get; private set; }

    /// <summary>Posición en el plan, desde 1.</summary>
    public int Posicion { get; private set; }

    /// <summary>Identificador estable de la orden del catálogo.</summary>
    public string OrdenAsistenteId { get; private set; } = string.Empty;

    /// <summary>Datos de la orden como objeto JSON, en claro (nunca la versión enmascarada).</summary>
    public string DatosJson { get; private set; } = "{}";

    public string? Resumen { get; private set; }

    /// <summary>Lista JSON de nombres de campo que faltan. Se expone ya leída en <see cref="CamposPendientes"/>.</summary>
    public string CamposPendientesJson { get; private set; } = "[]";

    /// <summary>Lista JSON de avisos. Se expone ya leída en <see cref="Avisos"/>.</summary>
    public string AvisosJson { get; private set; } = "[]";

    /// <summary>
    /// El paso salió de una interpretación por IA (y no de una macro o de una
    /// edición manual). Viaja a la auditoría de la ejecución.
    /// </summary>
    public bool AsistidoPorIa { get; private set; }

    public EstadoPasoTareaAsistente Estado { get; private set; }

    /// <summary>Momento de la confirmación del plan que incluyó este paso.</summary>
    public DateTime? ConfirmadoEnUtc { get; private set; }

    public DateTime? EjecutadoEnUtc { get; private set; }

    /// <summary>Id de la entidad que produjo el Command (el Trabajador dado de alta, la Visita creada…), si produjo alguna.</summary>
    public Guid? EntidadResultadoId { get; private set; }

    public string? MotivoFallo { get; private set; }

    public DateTime ActualizadoEnUtc { get; private set; }

    public IReadOnlyList<string> CamposPendientes =>
        JsonSerializer.Deserialize<List<string>>(CamposPendientesJson, OpcionesJson) ?? [];

    public IReadOnlyList<AvisoPasoTareaAsistente> Avisos =>
        JsonSerializer.Deserialize<List<AvisoPasoTareaAsistente>>(AvisosJson, OpcionesJson) ?? [];

    /// <summary>Vivo = forma parte del plan (no descartado).</summary>
    public bool EstaVivo => Estado != EstadoPasoTareaAsistente.Descartado;

    private PasoTareaAsistente()
    {
    }

    internal PasoTareaAsistente(Guid tareaAsistenteId, int posicion, DefinicionPasoTareaAsistente definicion, bool asistidoPorIa, DateTime ahoraUtc)
    {
        TareaAsistenteId = tareaAsistenteId;
        Posicion = posicion;

        var orden = definicion.OrdenAsistenteId?.Trim() ?? string.Empty;
        if (orden.Length == 0 || orden.Length > LongitudMaximaOrdenAsistenteId || !FormatoOrdenAsistenteId.IsMatch(orden))
            throw new ArgumentException("El paso debe nombrar una orden del catálogo por su identificador estable.", nameof(definicion));

        OrdenAsistenteId = orden;
        AsistidoPorIa = asistidoPorIa;
        FijarContenido(definicion.DatosJson, definicion.Resumen, definicion.CamposPendientes, definicion.Avisos, ahoraUtc);
    }

    internal void ActualizarBorrador(
        string datosJson,
        string? resumen,
        IReadOnlyList<string> camposPendientes,
        IReadOnlyList<AvisoPasoTareaAsistente> avisos,
        DateTime ahoraUtc)
    {
        if (Estado is not (EstadoPasoTareaAsistente.Borrador or EstadoPasoTareaAsistente.Listo))
            throw new InvalidOperationException($"Solo se edita un paso en borrador o listo; este está {Estado}.");

        FijarContenido(datosJson, resumen, camposPendientes, avisos, ahoraUtc);
    }

    internal void Descartar(DateTime ahoraUtc)
    {
        if (Estado is EstadoPasoTareaAsistente.Ejecutado or EstadoPasoTareaAsistente.Descartado)
            return;

        Estado = EstadoPasoTareaAsistente.Descartado;
        ActualizadoEnUtc = ahoraUtc;
    }

    internal void Confirmar(DateTime ahoraUtc)
    {
        if (Estado != EstadoPasoTareaAsistente.Listo)
            throw new InvalidOperationException($"Solo se confirma un paso listo; este está {Estado}.");

        Estado = EstadoPasoTareaAsistente.Confirmado;
        ConfirmadoEnUtc = ahoraUtc;
        ActualizadoEnUtc = ahoraUtc;
    }

    /// <returns><c>false</c> si ya estaba ejecutado con el mismo resultado (reintento idempotente).</returns>
    internal bool RegistrarEjecucion(Guid? entidadResultadoId, DateTime ahoraUtc)
    {
        if (Estado == EstadoPasoTareaAsistente.Ejecutado)
        {
            if (EntidadResultadoId == entidadResultadoId) return false;
            throw new InvalidOperationException("El paso ya se ejecutó con otro resultado.");
        }

        if (Estado is not (EstadoPasoTareaAsistente.Confirmado or EstadoPasoTareaAsistente.Fallido) || ConfirmadoEnUtc is null)
            throw new InvalidOperationException($"Solo se ejecuta un paso confirmado; este está {Estado}.");

        Estado = EstadoPasoTareaAsistente.Ejecutado;
        EjecutadoEnUtc = ahoraUtc;
        EntidadResultadoId = entidadResultadoId;
        MotivoFallo = null;
        ActualizadoEnUtc = ahoraUtc;
        return true;
    }

    internal void RegistrarFallo(string motivo, DateTime ahoraUtc)
    {
        if (Estado is not (EstadoPasoTareaAsistente.Confirmado or EstadoPasoTareaAsistente.Fallido) || ConfirmadoEnUtc is null)
            throw new InvalidOperationException($"Solo falla la ejecución de un paso confirmado; este está {Estado}.");
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("El fallo debe tener motivo.", nameof(motivo));

        var limpio = motivo.Trim();
        Estado = EstadoPasoTareaAsistente.Fallido;
        MotivoFallo = limpio.Length > LongitudMaximaMotivoFallo ? limpio[..LongitudMaximaMotivoFallo] : limpio;
        ActualizadoEnUtc = ahoraUtc;
    }

    private void FijarContenido(
        string datosJson,
        string? resumen,
        IReadOnlyList<string> camposPendientes,
        IReadOnlyList<AvisoPasoTareaAsistente> avisos,
        DateTime ahoraUtc)
    {
        ArgumentNullException.ThrowIfNull(camposPendientes);
        ArgumentNullException.ThrowIfNull(avisos);

        DatosJson = ValidarObjetoJson(datosJson);

        var resumenLimpio = string.IsNullOrWhiteSpace(resumen) ? null : resumen.Trim();
        if (resumenLimpio is { Length: > LongitudMaximaResumen })
            throw new ArgumentException($"El resumen de un paso no puede superar {LongitudMaximaResumen} caracteres.", nameof(resumen));
        Resumen = resumenLimpio;

        if (camposPendientes.Count > MaximoCamposPendientes)
            throw new ArgumentException($"Un paso no puede tener más de {MaximoCamposPendientes} campos pendientes.", nameof(camposPendientes));
        var campos = camposPendientes
            .Select(c => c?.Trim() ?? string.Empty)
            .ToList();
        if (campos.Any(c => c.Length == 0 || c.Length > LongitudMaximaCampoPendiente))
            throw new ArgumentException($"Cada campo pendiente debe tener nombre y no superar {LongitudMaximaCampoPendiente} caracteres.", nameof(camposPendientes));
        campos = campos.Distinct(StringComparer.Ordinal).ToList();

        if (avisos.Count > MaximoAvisos)
            throw new ArgumentException($"Un paso no puede tener más de {MaximoAvisos} avisos.", nameof(avisos));
        foreach (var aviso in avisos)
        {
            ArgumentNullException.ThrowIfNull(aviso, nameof(avisos));
            aviso.Validar();
        }

        CamposPendientesJson = JsonSerializer.Serialize(campos, OpcionesJson);
        AvisosJson = JsonSerializer.Serialize(avisos.ToList(), OpcionesJson);

        // El único sitio donde se decide borrador o listo: con un dato que
        // falte o un aviso bloqueante, el paso no se puede confirmar.
        Estado = campos.Count == 0 && avisos.All(a => a.Gravedad != GravedadAvisoPasoTareaAsistente.Bloqueante)
            ? EstadoPasoTareaAsistente.Listo
            : EstadoPasoTareaAsistente.Borrador;
        ActualizadoEnUtc = ahoraUtc;
    }

    private static string ValidarObjetoJson(string datosJson)
    {
        if (string.IsNullOrWhiteSpace(datosJson))
            throw new ArgumentException("Los datos del paso deben ser un objeto JSON.", nameof(datosJson));
        if (datosJson.Length > LongitudMaximaDatosJson)
            throw new ArgumentException($"Los datos del paso no pueden superar {LongitudMaximaDatosJson} caracteres.", nameof(datosJson));

        try
        {
            using var documento = JsonDocument.Parse(datosJson);
            if (documento.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Los datos del paso deben ser un objeto JSON.", nameof(datosJson));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Los datos del paso no son JSON válido.", nameof(datosJson), ex);
        }

        return datosJson;
    }
}
