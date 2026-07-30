using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Instancia de un TipoDocumento asociada a un Trabajador, un Cliente, una
/// Empresa, un Vehículo o un Proyecto (nunca más de uno — ver
/// <see cref="DeTrabajador"/>/<see cref="DeCliente"/>/<see cref="DeEmpresa"/>/
/// <see cref="DeVehiculo"/>/<see cref="DeProyecto"/>), según el
/// <see cref="Documentos.AmbitoAplicacion"/> de su TipoDocumento — p. ej. RLC/ITA/RNT
/// son documentos de Cliente, un formulario de higiene personalizado puede
/// ser de Empresa, la mayoría (apto médico, EPIS, formación...) son de
/// Trabajador, y la documentación de requerimiento de una obra nueva
/// (licencias, certificados de instalación) es de Proyecto.
/// </summary>
public class Documento : EntidadBase
{
    public const int LongitudMaximaArchivoUrl = 500;
    public const int LongitudMaximaComentarios = 1000;

    public Guid? TrabajadorId { get; private set; }
    public Guid? ClienteId { get; private set; }
    public Guid? EmpresaId { get; private set; }
    public Guid? VehiculoId { get; private set; }
    public Guid? ProyectoId { get; private set; }
    public Guid TipoDocumentoId { get; private set; }
    public DateOnly FechaEmision { get; private set; }
    public DateOnly? FechaVencimiento { get; private set; }
    public string? ArchivoUrl { get; private set; }
    public string? Comentarios { get; private set; }

    public AmbitoAplicacion Ambito =>
        TrabajadorId is not null ? AmbitoAplicacion.Trabajador
        : ClienteId is not null ? AmbitoAplicacion.Cliente
        : VehiculoId is not null ? AmbitoAplicacion.Vehiculo
        : ProyectoId is not null ? AmbitoAplicacion.Proyecto
        : AmbitoAplicacion.Empresa;

    private Documento()
    {
    }

    private Documento(
        Guid? trabajadorId,
        Guid? clienteId,
        Guid? empresaId,
        Guid? vehiculoId,
        Guid? proyectoId,
        Guid tipoDocumentoId,
        DateOnly fechaEmision,
        DateOnly? fechaVencimiento,
        string? archivoUrl,
        string? comentarios)
    {
        if (tipoDocumentoId == Guid.Empty)
            throw new ArgumentException("El documento debe tener un tipo de documento.", nameof(tipoDocumentoId));

        TrabajadorId = trabajadorId;
        ClienteId = clienteId;
        EmpresaId = empresaId;
        VehiculoId = vehiculoId;
        ProyectoId = proyectoId;
        TipoDocumentoId = tipoDocumentoId;
        Renovar(fechaEmision, fechaVencimiento);
        ArchivoUrl = archivoUrl;
        Comentarios = comentarios;
    }

    public static Documento DeTrabajador(
        Guid trabajadorId,
        Guid tipoDocumentoId,
        DateOnly fechaEmision,
        DateOnly? fechaVencimiento,
        string? archivoUrl = null,
        string? comentarios = null)
    {
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("El documento debe pertenecer a un trabajador.", nameof(trabajadorId));

        return new Documento(trabajadorId, null, null, null, null, tipoDocumentoId, fechaEmision, fechaVencimiento, archivoUrl, comentarios);
    }

    public static Documento DeCliente(
        Guid clienteId,
        Guid tipoDocumentoId,
        DateOnly fechaEmision,
        DateOnly? fechaVencimiento,
        string? archivoUrl = null,
        string? comentarios = null)
    {
        if (clienteId == Guid.Empty)
            throw new ArgumentException("El documento debe pertenecer a un cliente.", nameof(clienteId));

        return new Documento(null, clienteId, null, null, null, tipoDocumentoId, fechaEmision, fechaVencimiento, archivoUrl, comentarios);
    }

    public static Documento DeEmpresa(
        Guid empresaId,
        Guid tipoDocumentoId,
        DateOnly fechaEmision,
        DateOnly? fechaVencimiento,
        string? archivoUrl = null,
        string? comentarios = null)
    {
        if (empresaId == Guid.Empty)
            throw new ArgumentException("El documento debe pertenecer a una empresa.", nameof(empresaId));

        return new Documento(null, null, empresaId, null, null, tipoDocumentoId, fechaEmision, fechaVencimiento, archivoUrl, comentarios);
    }

    public static Documento DeVehiculo(
        Guid vehiculoId,
        Guid tipoDocumentoId,
        DateOnly fechaEmision,
        DateOnly? fechaVencimiento,
        string? archivoUrl = null,
        string? comentarios = null)
    {
        if (vehiculoId == Guid.Empty)
            throw new ArgumentException("El documento debe pertenecer a un vehículo.", nameof(vehiculoId));

        return new Documento(null, null, null, vehiculoId, null, tipoDocumentoId, fechaEmision, fechaVencimiento, archivoUrl, comentarios);
    }

    public static Documento DeProyecto(
        Guid proyectoId,
        Guid tipoDocumentoId,
        DateOnly fechaEmision,
        DateOnly? fechaVencimiento,
        string? archivoUrl = null,
        string? comentarios = null)
    {
        if (proyectoId == Guid.Empty)
            throw new ArgumentException("El documento debe pertenecer a un proyecto.", nameof(proyectoId));

        return new Documento(null, null, null, null, proyectoId, tipoDocumentoId, fechaEmision, fechaVencimiento, archivoUrl, comentarios);
    }

    /// <summary>
    /// Actualiza fecha de emisión/vencimiento — p. ej. cuando el trabajador
    /// presenta la renovación de un documento vencido. FechaVencimiento la
    /// calcula el llamador (Application) con CalculadoraEstadoDocumento,
    /// porque depende de la vigencia del TipoDocumento o de un
    /// RequisitoDocumental, que el Documento no conoce.
    /// </summary>
    public void Renovar(DateOnly fechaEmision, DateOnly? fechaVencimiento)
    {
        if (fechaEmision > DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ArgumentException("La fecha de emisión no puede ser futura.", nameof(fechaEmision));
        if (fechaVencimiento is not null && fechaVencimiento < fechaEmision)
            throw new ArgumentException("La fecha de vencimiento no puede ser anterior a la de emisión.", nameof(fechaVencimiento));

        FechaEmision = fechaEmision;
        FechaVencimiento = fechaVencimiento;
    }

    public void AdjuntarArchivo(string archivoUrl)
    {
        if (string.IsNullOrWhiteSpace(archivoUrl))
            throw new ArgumentException("La URL del archivo no puede estar vacía.", nameof(archivoUrl));
        ArchivoUrl = archivoUrl;
    }

    public void ActualizarComentarios(string? comentarios) => Comentarios = comentarios;

    public EstadoDocumento CalcularEstado(DateOnly hoy, int umbralAmbarDias, int umbralRojoDias) =>
        CalculadoraEstadoDocumento.Calcular(FechaVencimiento, hoy, umbralAmbarDias, umbralRojoDias);
}
