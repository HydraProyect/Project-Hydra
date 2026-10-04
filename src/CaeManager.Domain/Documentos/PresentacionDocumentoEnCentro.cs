using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Cómo se presentó un Documento a un Centro (decisión del propietario del producto, 2026-10-04): qué hecho registra
/// una <see cref="PresentacionDocumentoEnCentro"/>. Valores numéricos explícitos y congelados: se persisten como entero.
/// </summary>
public enum OrigenPresentacionDocumentoEnCentro
{
    /// <summary>El Gestor CAE marcó como subida la acreditación del Documento en la plataforma del Centro («Marcar subida», también desde la extensión de navegador).</summary>
    SubidaAPlataforma = 0,

    /// <summary>El Gestor CAE marcó como aceptada la acreditación del Documento en la plataforma del Centro («Marcar aceptada»).</summary>
    AceptadaEnPlataforma = 1,

    /// <summary>El Gestor CAE volvió a presentar el MISMO Documento al Centro (acción «Volver a presentar»): ni crea un Documento ni lo renueva.</summary>
    VolverAPresentar = 2,

    /// <summary>El Gestor CAE envió por correo, desde el compositor de la Visita, el paquete documental que contenía el Documento.</summary>
    EnvioPorCorreo = 3
}

/// <summary>
/// Un hecho del historial: el Documento <see cref="DocumentoId"/> se presentó al Centro <see cref="CentroId"/> el día
/// <see cref="FechaPresentacion"/>. Es el <b>ancla</b> de la periodicidad especial de un Centro
/// (<see cref="TipoDocumentoCentro.PeriodicidadEspecialMeses"/>): el Documento vence en el Centro en
/// <c>min(última presentación en el Centro + periodicidad, vigencia propia del Documento)</c>
/// (<see cref="ReglaBloqueoDeAcceso.VencimientoEfectivo"/>); sin ninguna presentación en el Centro, el ancla es la fecha de
/// emisión del Documento. No hay filas sintéticas: lo que el sistema no ha visto presentar no se inventa.
///
/// <para>
/// <b>Historial inmutable</b>: solo se añaden filas; nada las edita ni las borra (desaparecen únicamente con el Documento,
/// por la clave foránea). El Documento nuevo de una renovación (<c>RenovarDocumentoCommand</c>, ruta «sustituir por uno nuevo»)
/// es OTRO Documento y no hereda las presentaciones del sustituido: su ancla es su emisión.
/// </para>
///
/// <para>
/// El Tenant propietario es el del Documento y el del Centro (clave foránea compuesta por Tenant): un Documento nunca se
/// presenta a un Centro de otro Tenant. <see cref="FechaPresentacion"/> es un día de negocio
/// (<see cref="DiaDeNegocio.Hoy"/>), no un instante; <see cref="RegistradaEnUtc"/> es el instante real del registro.
/// </para>
/// </summary>
public class PresentacionDocumentoEnCentro : EntidadConTenant
{
    public Guid DocumentoId { get; private set; }
    public Guid CentroId { get; private set; }
    public DateOnly FechaPresentacion { get; private set; }
    public OrigenPresentacionDocumentoEnCentro Origen { get; private set; }
    public DateTime RegistradaEnUtc { get; private set; }

    private PresentacionDocumentoEnCentro()
    {
    }

    public PresentacionDocumentoEnCentro(
        Guid documentoId, Guid centroId, DateOnly fechaPresentacion, OrigenPresentacionDocumentoEnCentro origen, DateTime registradaEnUtc)
    {
        if (documentoId == Guid.Empty)
            throw new ArgumentException("La presentación debe ser de un Documento.", nameof(documentoId));
        if (centroId == Guid.Empty)
            throw new ArgumentException("La presentación debe ser a un Centro.", nameof(centroId));
        if (!Enum.IsDefined(origen))
            throw new ArgumentException("El origen de la presentación no es válido.", nameof(origen));

        DocumentoId = documentoId;
        CentroId = centroId;
        FechaPresentacion = fechaPresentacion;
        Origen = origen;
        RegistradaEnUtc = registradaEnUtc;
    }
}
