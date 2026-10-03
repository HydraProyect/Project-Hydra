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
    /// <summary>
    /// Solo tiene valor cuando <see cref="EstadoVigencia"/> es
    /// <see cref="EstadoVigenciaDocumento.VenceEnFecha"/>. Nula NO significa
    /// «no caduca»: para eso está <see cref="EstadoVigencia"/>. Léase siempre
    /// a través de <see cref="Vigencia"/>.
    /// </summary>
    public DateOnly? FechaVencimiento { get; private set; }

    /// <summary>
    /// Estado explícito de la vigencia: sin confirmar, no caduca o vence en
    /// <see cref="FechaVencimiento"/>. Se persiste junto a la fecha y los dos
    /// los vigila <c>CK_Documentos_EstadoVigenciaCoherente</c>.
    /// </summary>
    public EstadoVigenciaDocumento EstadoVigencia { get; private set; }

    /// <summary>La vigencia como valor único; revienta si la fila es incoherente.</summary>
    public VigenciaDocumento Vigencia => VigenciaDocumento.Rehidratar(EstadoVigencia, FechaVencimiento);
    public string? ArchivoUrl { get; private set; }
    public string? Comentarios { get; private set; }

    /// <summary>
    /// DCR-19: hasta ahora un documento sin propietario "mentía" devolviendo
    /// <see cref="AmbitoAplicacion.Empresa"/> por defecto. El constructor
    /// impide que eso ocurra para un agregado recién creado (ver
    /// <see cref="Documento(Guid?, Guid?, Guid?, Guid?, Guid?, Guid, DateOnly, VigenciaDocumento, string?, string?)"/>),
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
            if (Propietarios.Count(id => id is not null) != 1)
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

    /// <summary>Las cinco anclas de propietario, en un orden fijo: <see cref="Ambito"/> las cuenta y <see cref="SustituirPor"/> las compara.</summary>
    private Guid?[] Propietarios => [TrabajadorId, ClienteId, EmpresaId, VehiculoId, ProyectoId];

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
        VigenciaDocumento vigencia,
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
        CorregirVigencia(fechaEmision, vigencia);
        ArchivoUrl = archivoUrl;
        Comentarios = comentarios;
    }

    public static Documento DeTrabajador(
        Guid trabajadorId,
        Guid tipoDocumentoId,
        DateOnly fechaEmision,
        VigenciaDocumento vigencia,
        string? archivoUrl = null,
        string? comentarios = null)
    {
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("El documento debe pertenecer a un trabajador.", nameof(trabajadorId));

        return new Documento(trabajadorId, null, null, null, null, tipoDocumentoId, fechaEmision, vigencia, archivoUrl, comentarios);
    }

    public static Documento DeCliente(
        Guid clienteId,
        Guid tipoDocumentoId,
        DateOnly fechaEmision,
        VigenciaDocumento vigencia,
        string? archivoUrl = null,
        string? comentarios = null)
    {
        if (clienteId == Guid.Empty)
            throw new ArgumentException("El documento debe pertenecer a un cliente.", nameof(clienteId));

        return new Documento(null, clienteId, null, null, null, tipoDocumentoId, fechaEmision, vigencia, archivoUrl, comentarios);
    }

    public static Documento DeEmpresa(
        Guid empresaId,
        Guid tipoDocumentoId,
        DateOnly fechaEmision,
        VigenciaDocumento vigencia,
        string? archivoUrl = null,
        string? comentarios = null)
    {
        if (empresaId == Guid.Empty)
            throw new ArgumentException("El documento debe pertenecer a una empresa.", nameof(empresaId));

        return new Documento(null, null, empresaId, null, null, tipoDocumentoId, fechaEmision, vigencia, archivoUrl, comentarios);
    }

    public static Documento DeVehiculo(
        Guid vehiculoId,
        Guid tipoDocumentoId,
        DateOnly fechaEmision,
        VigenciaDocumento vigencia,
        string? archivoUrl = null,
        string? comentarios = null)
    {
        if (vehiculoId == Guid.Empty)
            throw new ArgumentException("El documento debe pertenecer a un vehículo.", nameof(vehiculoId));

        return new Documento(null, null, null, vehiculoId, null, tipoDocumentoId, fechaEmision, vigencia, archivoUrl, comentarios);
    }

    public static Documento DeProyecto(
        Guid proyectoId,
        Guid tipoDocumentoId,
        DateOnly fechaEmision,
        VigenciaDocumento vigencia,
        string? archivoUrl = null,
        string? comentarios = null)
    {
        if (proyectoId == Guid.Empty)
            throw new ArgumentException("El documento debe pertenecer a un proyecto.", nameof(proyectoId));

        return new Documento(null, null, null, null, proyectoId, tipoDocumentoId, fechaEmision, vigencia, archivoUrl, comentarios);
    }

    /// <summary>
    /// Corrige la fecha de emisión y la vigencia <b>de este mismo registro</b>: la corrección de un dato mal
    /// leído (revisión de la IA, edición del Gestor CAE), no una renovación. Renovar un documento es subir
    /// uno nuevo que lo sustituye (<see cref="SustituirPor"/>): el anterior pasa al historial, y este método no
    /// puede hacerlo porque pisa las fechas del propio registro. Hasta que el comando de renovar cree un
    /// registro nuevo (PR 4 del diseño del documento efectivo), <c>RenovarDocumentoCommand</c> sigue usando este
    /// método. Este método no rechaza un documento ya sustituido: si el historial es además inmutable frente a
    /// correcciones es una decisión pendiente del PR 4, no una garantía de hoy. La vigencia la
    /// decide el llamador (Application): la calcula con CalculadoraEstadoDocumento cuando el TipoDocumento
    /// tiene vencimiento automático, o la confirma el Gestor CAE a mano. Una vigencia sin confirmar queda
    /// <see cref="VigenciaDocumento.SinConfirmar"/>, nunca «no caduca».
    /// </summary>
    public void CorregirVigencia(DateOnly fechaEmision, VigenciaDocumento vigencia)
    {
        if (fechaEmision > DiaDeNegocio.Hoy())
            throw new ArgumentException("La fecha de emisión no puede ser futura.", nameof(fechaEmision));
        if (vigencia.FechaVencimiento is { } fecha && fecha < fechaEmision)
            throw new ArgumentException("La fecha de vencimiento no puede ser anterior a la de emisión.", nameof(vigencia));

        FechaEmision = fechaEmision;
        EstadoVigencia = vigencia.Estado;
        FechaVencimiento = vigencia.FechaVencimiento;
    }

    /// <summary>
    /// Id del documento que sustituyó a este, o null si sigue operativo. Se persiste con
    /// <see cref="SustituidoEnUtc"/> y <see cref="MotivoSustitucion"/>: las tres columnas son nulas a la vez o
    /// ninguna lo es (<c>CK_Documentos_SustitucionCoherente</c>), y la FK compuesta con el Tenant impide que
    /// apunte a un documento de otro Tenant propietario.
    /// </summary>
    public Guid? SustituidoPorDocumentoId { get; private set; }

    /// <summary>Cuándo se sustituyó, o null si sigue operativo.</summary>
    public DateTime? SustituidoEnUtc { get; private set; }

    /// <summary>Por qué se sustituyó (auditoría), o null si sigue operativo.</summary>
    public MotivoSustitucionDocumento? MotivoSustitucion { get; private set; }

    /// <summary>
    /// Historial: otro documento lo sustituyó y ya no cuenta. Es irreversible. Para filtrar en una consulta
    /// se usa <see cref="DocumentoOperativo"/>, que además excluye los eliminados.
    /// </summary>
    public bool EstaSustituido => SustituidoEnUtc is not null;

    /// <summary>
    /// Este documento pasa al historial porque <paramref name="nuevo"/> ocupa su lugar. <b>Nunca borra</b>:
    /// esta operación no toca el archivo, las fechas, la identidad ni las acreditaciones del sustituido, que
    /// deja de contar (<see cref="DocumentoOperativo"/>). La sustitución no puede deshacerse y el Id del sustituto es otro
    /// distinto del del sustituido: ningún camino reutiliza un Id (decisiones D5 y D8, 2026-10-03).
    ///
    /// <para>
    /// Invariantes (todas lanzan, ninguna se corrige en silencio): un documento no se sustituye a sí mismo; no
    /// se sustituye dos veces (el sustituto no puede quedar a su vez sustituido, y así tampoco hay ciclos); el
    /// sustituto y el sustituido tienen el mismo titular (los cinco propietarios), el mismo Tipo de documento y,
    /// cuando los dos ya tienen Tenant propietario sellado, el mismo Tenant; ninguno de los dos está eliminado.
    /// </para>
    ///
    /// <para>
    /// <b>Qué no decide</b>: cuál de los dos es más reciente. Subir un documento anterior al que está en uso
    /// (D5) se expresa sustituyendo <i>el nuevo</i> por el que ya estaba: el más antiguo nace en el historial y
    /// nunca pasa a efectivo por subirlo. Tampoco decide el destinatario (PR 6 del diseño, aún sin columna).
    /// </para>
    ///
    /// <para>
    /// El Tenant solo se compara cuando los dos lo tienen: un documento recién creado aún no lo tiene —lo sella
    /// el interceptor al guardar—, y la barrera para ese caso es la FK compuesta
    /// <c>(TenantId, SustituidoPorDocumentoId)</c> hacia <c>(TenantId, Id)</c>, probada en PostgreSQL.
    /// </para>
    /// </summary>
    public void SustituirPor(Documento nuevo, MotivoSustitucionDocumento motivo, DateTime ahoraUtc)
    {
        ArgumentNullException.ThrowIfNull(nuevo);

        if (ReferenceEquals(nuevo, this) || nuevo.Id == Id)
            throw new ArgumentException("Un documento no puede sustituirse a sí mismo.", nameof(nuevo));
        if (!Enum.IsDefined(motivo))
            throw new ArgumentOutOfRangeException(nameof(motivo), motivo, "Motivo de sustitución desconocido.");
        if (ahoraUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("El instante de la sustitución debe estar en UTC.", nameof(ahoraUtc));

        if (EstaSustituido)
            throw new InvalidOperationException(
                "El documento ya está sustituido: la sustitución es irreversible y no se repite.");
        if (nuevo.EstaSustituido)
            throw new InvalidOperationException(
                "El documento que sustituiría ya está sustituido: no puede ocupar el lugar de otro.");
        if (EstaEliminado || nuevo.EstaEliminado)
            throw new InvalidOperationException("Un documento eliminado no sustituye ni es sustituido.");

        if (TipoDocumentoId != nuevo.TipoDocumentoId)
            throw new ArgumentException("El sustituto debe ser del mismo Tipo de documento.", nameof(nuevo));
        if (!Propietarios.SequenceEqual(nuevo.Propietarios))
            throw new ArgumentException("El sustituto debe tener el mismo titular.", nameof(nuevo));
        if (TenantId != Guid.Empty && nuevo.TenantId != Guid.Empty && TenantId != nuevo.TenantId)
            throw new ArgumentException("El sustituto debe ser del mismo Tenant propietario.", nameof(nuevo));

        SustituidoPorDocumentoId = nuevo.Id;
        SustituidoEnUtc = ahoraUtc;
        MotivoSustitucion = motivo;
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
    /// (Project-Hydra-Negocio/tecnico/RGPD-TRATAMIENTO-DATOS.md § 5).
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
        CalculadoraEstadoDocumento.Calcular(Vigencia, hoy, umbralAmbarDias, umbralRojoDias);
}
