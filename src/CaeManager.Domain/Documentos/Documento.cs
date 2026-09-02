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

    /// <summary>
    /// DCR-19: hasta ahora un documento sin propietario "mentía" devolviendo
    /// <see cref="AmbitoAplicacion.Empresa"/> por defecto. El constructor
    /// impide que eso ocurra para un agregado recién creado (ver
    /// <see cref="Documento(Guid?, Guid?, Guid?, Guid?, Guid?, Guid, DateOnly, DateOnly?, string?, string?)"/>),
    /// pero EF Core materializa las filas existentes por el constructor SIN
    /// parámetros (confirmado por inspección del <c>ConstructorBinding</c> del
    /// modelo: usa <c>Documento()</c>, no el privado con parámetros), así que
    /// esa guarda no protege una fila que ya hubiera quedado inválida en base.
    /// La única defensa real para una fila materializada es que este
    /// <c>throw</c> haga fallar ruidosamente en vez de inventar un propietario
    /// — la constraint <c>CK_Documentos_PropietarioXor</c>
    /// (<c>DocumentoConfiguration</c>, migración
    /// <c>RendimientoBusquedasYCheckXorDocumento</c> del 2026-08-01) es la que
    /// impide que esa fila llegue a existir.
    ///
    /// Cuenta los cinco, no se limita a mirar cuál es el primero no-nulo: una
    /// cadena de <c>is not null ? ... : ...</c> que se detiene en el primer
    /// match "resolvería" una fila con dos propietarios devolviendo el
    /// primero en vez de fallar — el mismo defecto que esta propiedad existe
    /// para eliminar, solo que con dos en vez de con cero.
    /// </summary>
    public AmbitoAplicacion Ambito
    {
        get
        {
            var propietarios = new[] { TrabajadorId, ClienteId, EmpresaId, VehiculoId, ProyectoId };
            if (propietarios.Count(id => id is not null) != 1)
                throw new InvalidOperationException(
                    "Documento sin exactamente un propietario entre Trabajador, Cliente, Empresa, Vehículo y " +
                    "Proyecto: viola CK_Documentos_PropietarioXor. Esto no puede ocurrir para un documento " +
                    "creado por las factorías de este agregado — indica una fila inválida materializada desde " +
                    "base de datos.");

            return TrabajadorId is not null ? AmbitoAplicacion.Trabajador
                : ClienteId is not null ? AmbitoAplicacion.Cliente
                : VehiculoId is not null ? AmbitoAplicacion.Vehiculo
                : ProyectoId is not null ? AmbitoAplicacion.Proyecto
                : AmbitoAplicacion.Empresa;
        }
    }

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

        // DCR-19: un Documento tiene exactamente un propietario entre las
        // cinco anclas — mismo backstop que CK_Documentos_PropietarioXor
        // (DocumentoConfiguration), pero esta guarda solo alcanza a un
        // agregado construido por este constructor, no a una fila
        // materializada por EF (ver comentario de Ambito).
        var numeroDePropietarios = new[] { trabajadorId, clienteId, empresaId, vehiculoId, proyectoId }
            .Count(id => id is not null);
        if (numeroDePropietarios != 1)
            throw new ArgumentException(
                "El documento debe tener exactamente un propietario entre Trabajador, Cliente, Empresa, " +
                $"Vehículo y Proyecto (CK_Documentos_PropietarioXor); tiene {numeroDePropietarios}.",
                nameof(trabajadorId));

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

    /// <summary>
    /// Cuándo se purgó el contenido, o null si sigue completo.
    /// </summary>
    public DateTime? AnonimizadoEnUtc { get; private set; }

    public bool EstaAnonimizado => AnonimizadoEnUtc is not null;

    /// <summary>
    /// Suprime el contenido personal del documento cumplido su plazo
    /// (RGPD-TRATAMIENTO-DATOS.md § 5).
    ///
    /// Aquí la anonimización no es solo limpiar campos: <b>el dato personal
    /// está dentro del PDF</b> —un reconocimiento médico, un DNI escaneado—,
    /// así que suprimirlo de verdad exige borrar el archivo del
    /// almacenamiento. Esta operación solo suelta la referencia; quien la
    /// invoca es responsable de borrar el fichero, y por eso devuelve la ruta
    /// que había: sin ella, el archivo quedaría huérfano en disco y la
    /// supresión sería aparente.
    ///
    /// Lo que se conserva son las fechas y el tipo, que es lo que sostiene el
    /// histórico de cumplimiento CAE sin identificar a nadie.
    ///
    /// Idempotente: repetirlo devuelve null porque ya no hay archivo.
    /// </summary>
    public string? Anonimizar(DateTime ahoraUtc)
    {
        if (EstaAnonimizado) return null;

        var archivoAsuprimir = ArchivoUrl;

        ArchivoUrl = null;
        Comentarios = null;
        AnonimizadoEnUtc = ahoraUtc;

        return archivoAsuprimir;
    }

    public EstadoDocumento CalcularEstado(DateOnly hoy, int umbralAmbarDias, int umbralRojoDias) =>
        CalculadoraEstadoDocumento.Calcular(FechaVencimiento, hoy, umbralAmbarDias, umbralRojoDias);
}
