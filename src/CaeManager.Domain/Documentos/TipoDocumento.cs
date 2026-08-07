using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Catálogo maestro de documentos PRL exigibles (apto médico, EPIS,
/// formación, reciclajes, etc.). Corresponde 1:1 a la hoja "Parametros" del
/// Excel original. Es configuración del sistema, editable solo por
/// Administrador.
/// </summary>
public class TipoDocumento : EntidadConTenant
{
    public const int LongitudMaximaNombre = 150;
    public const int LongitudMaximaNotas = 500;
    public const int LongitudMaximaDescripcion = 1000;
    public const int LongitudMaximaCriteriosValidacion = 1000;
    public const int LongitudMaximaSeSolicitaA = 300;
    public const int LongitudMaximaObservaciones = 1000;

    public string Nombre { get; private set; } = string.Empty;
    public int? VigenciaMeses { get; private set; }
    public bool AplicaVencimientoAutomatico { get; private set; }
    public string? Notas { get; private set; }
    public int Orden { get; private set; }
    public string? Descripcion { get; private set; }

    /// <summary>
    /// Los términos de validación tal como los describe la plataforma
    /// documental del cliente (PLAN-EJECUCION-UX.md § 0.7, Lote 0-E) —
    /// texto pegado del portal, no una reescritura: es lo que decide si un
    /// documento de este tipo se acepta o se rechaza allí. Marcado como
    /// <b>fuente de referencia para la automatización de lectura IA</b>
    /// (<c>VerificacionIaDocumentoService</c>, que hoy todavía no lo lee);
    /// no hace falta modelo nuevo para esa integración, el campo ya es este.
    /// </summary>
    public string? CriteriosValidacion { get; private set; }
    public string? SeSolicitaA { get; private set; }
    public string? Observaciones { get; private set; }

    /// <summary>
    /// Si es obligatorio para todos los Clientes/Empresas (o Vehículos, según
    /// el Ámbito) — lo usa la verificación de documentación de Visitas para
    /// distinguir "no aplica, no cuenta como pendiente" de "obligatorio y
    /// falta, hay que gestionarlo". No aplica a Ámbito Trabajador, donde
    /// todavía no existe una lista estructurada de qué es obligatorio.
    /// </summary>
    public bool EsObligatorio { get; private set; }

    /// <summary>
    /// A qué se asocian los Documentos de este tipo — Trabajador, Cliente o
    /// Empresa (ver <see cref="Documento"/>). Fijo desde la creación: no se
    /// puede cambiar más adelante porque ya podría haber Documentos creados
    /// bajo el ámbito original, y cambiarlo los dejaría inconsistentes.
    /// </summary>
    public AmbitoAplicacion AmbitoAplicacion { get; private set; }

    /// <summary>
    /// Interruptor global de Administrador: si está en false, la lectura
    /// automática por IA de este tipo de documento queda desactivada para
    /// todos los Clientes, sin excepción — ver
    /// <see cref="Documentos.ConfiguracionIaDocumentoCliente"/> para el
    /// nivel 2 (Gestor CAE, solo para su propia cartera). Empieza activo:
    /// solo se desactiva explícitamente.
    /// </summary>
    public bool LecturaIaActiva { get; private set; } = true;

    /// <summary>
    /// Solo tiene sentido para Ámbito Empresa: si está activo, un Documento
    /// de este tipo dispara la detección automática de altas/bajas de
    /// personal (ver DeteccionTrabajadoresService, Fase 36) — comparando el
    /// listado de trabajadores que aparece en el propio documento (ITA,
    /// RNT/TC2...) contra los Trabajadores activos de la Empresa. Empieza
    /// desactivado a propósito: la mayoría de documentos de Empresa
    /// (certificados, seguros, mutua...) no contienen ningún listado de
    /// personal, así que activarlo por defecto generaría llamadas a IA y
    /// detecciones sin sentido — el Administrador lo activa explícitamente
    /// solo para los tipos que sí traen un listado (ver TipoDocumentoSeedData,
    /// donde ITA y RNT/TC2 ya vienen activados).
    /// </summary>
    public bool DeteccionTrabajadoresActiva { get; private set; }

    /// <summary>
    /// Solo tiene sentido para Ámbito Trabajador: si está activo, cada
    /// Documento nuevo de este tipo pasa por una verificación IA que lee el
    /// archivo real y compara tipo/fecha de emisión/firma detectados contra
    /// lo introducido por el usuario, generando una <see cref="RevisionIaDocumento"/>
    /// pendiente cuando la confianza es baja o hay una discrepancia — nunca
    /// corrige nada automáticamente (ver Issue #19). Empieza desactivado a
    /// propósito, mismo criterio que <see cref="DeteccionTrabajadoresActiva"/>:
    /// el Administrador lo activa explícitamente solo para los tipos donde
    /// interesa (p. ej. reconocimiento médico, EPIs).
    /// </summary>
    public bool VerificacionIaActiva { get; private set; }

    /// <summary>
    /// Solo tiene sentido para Ámbitos Empresa y Cliente: qué documento
    /// oficial de la Administración es este tipo, a efectos de la validación
    /// automática (verificación criptográfica de firma + parser determinista
    /// + cotejo — sin IA, ver ValidacionDocumentoOficialService y
    /// PLAN-FIRMA-DIGITAL-PDF.md). Empieza en <see cref="PerfilDocumentoOficial.Ninguno"/>
    /// (apagado), mismo criterio que <see cref="DeteccionTrabajadoresActiva"/>:
    /// el Administrador lo asigna explícitamente a los tipos que corresponden
    /// (corriente TGSS/AEAT, RNT, RLC, ITA — TipoDocumentoSeedData ya los
    /// trae asignados para tenants nuevos).
    /// </summary>
    public PerfilDocumentoOficial PerfilDocumentoOficial { get; private set; }

    private TipoDocumento()
    {
    }

    public TipoDocumento(
        string nombre,
        int? vigenciaMeses,
        bool aplicaVencimientoAutomatico,
        int orden,
        AmbitoAplicacion ambitoAplicacion,
        bool esObligatorio = false,
        string? notas = null,
        string? descripcion = null,
        string? criteriosValidacion = null,
        string? seSolicitaA = null,
        string? observaciones = null)
    {
        EstablecerNombre(nombre);
        EstablecerVigencia(vigenciaMeses, aplicaVencimientoAutomatico);
        Orden = orden;
        AmbitoAplicacion = ambitoAplicacion;
        EsObligatorio = esObligatorio;
        Notas = notas;
        EstablecerGlosario(descripcion, criteriosValidacion, seSolicitaA, observaciones);
    }

    public void Actualizar(
        string nombre,
        int? vigenciaMeses,
        bool aplicaVencimientoAutomatico,
        int orden,
        bool esObligatorio,
        string? notas,
        string? descripcion,
        string? criteriosValidacion,
        string? seSolicitaA,
        string? observaciones)
    {
        EstablecerNombre(nombre);
        EstablecerVigencia(vigenciaMeses, aplicaVencimientoAutomatico);
        Orden = orden;
        EsObligatorio = esObligatorio;
        Notas = notas;
        EstablecerGlosario(descripcion, criteriosValidacion, seSolicitaA, observaciones);
    }

    public void EstablecerLecturaIaActiva(bool activa) => LecturaIaActiva = activa;

    public void EstablecerDeteccionTrabajadoresActiva(bool activa) => DeteccionTrabajadoresActiva = activa;

    public void EstablecerVerificacionIaActiva(bool activa) => VerificacionIaActiva = activa;

    public void EstablecerPerfilDocumentoOficial(PerfilDocumentoOficial perfil) => PerfilDocumentoOficial = perfil;

    private void EstablecerGlosario(string? descripcion, string? criteriosValidacion, string? seSolicitaA, string? observaciones)
    {
        if (descripcion?.Length > LongitudMaximaDescripcion)
            throw new ArgumentException($"La descripción no puede superar {LongitudMaximaDescripcion} caracteres.", nameof(descripcion));

        if (criteriosValidacion?.Length > LongitudMaximaCriteriosValidacion)
            throw new ArgumentException($"Los criterios de validación no pueden superar {LongitudMaximaCriteriosValidacion} caracteres.", nameof(criteriosValidacion));

        if (seSolicitaA?.Length > LongitudMaximaSeSolicitaA)
            throw new ArgumentException($"\"Se solicita a\" no puede superar {LongitudMaximaSeSolicitaA} caracteres.", nameof(seSolicitaA));

        if (observaciones?.Length > LongitudMaximaObservaciones)
            throw new ArgumentException($"Las observaciones no pueden superar {LongitudMaximaObservaciones} caracteres.", nameof(observaciones));

        Descripcion = descripcion;
        CriteriosValidacion = criteriosValidacion;
        SeSolicitaA = seSolicitaA;
        Observaciones = observaciones;
    }

    private void EstablecerVigencia(int? vigenciaMeses, bool aplicaVencimientoAutomatico)
    {
        if (aplicaVencimientoAutomatico && (vigenciaMeses is null || vigenciaMeses <= 0))
            throw new ArgumentException(
                "Un tipo de documento con vencimiento automático debe tener una vigencia en meses mayor que cero.",
                nameof(vigenciaMeses));

        VigenciaMeses = vigenciaMeses;
        AplicaVencimientoAutomatico = aplicaVencimientoAutomatico;
    }

    private void EstablecerNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre del tipo de documento es obligatorio.", nameof(nombre));

        var normalizado = nombre.Trim();

        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException(
                $"El nombre no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        Nombre = normalizado;
    }
}
