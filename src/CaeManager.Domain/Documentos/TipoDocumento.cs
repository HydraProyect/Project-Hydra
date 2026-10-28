using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;

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

    private readonly List<TipoDocumentoAlias> _aliases = [];

    public string Nombre { get; private set; } = string.Empty;
    public int? VigenciaMeses { get; private set; }
    public bool AplicaVencimientoAutomatico { get; private set; }

    /// <summary>
    /// La fecha de vencimiento de un Documento de este tipo es siempre un cálculo desde su emisión
    /// (<see cref="AplicaVencimientoAutomatico"/> con <see cref="VigenciaMeses"/>): nadie tiene que
    /// confirmarla a mano. Es el criterio de la aceptación en bloque de la Revisión IA, que nunca fija
    /// una vigencia que el Gestor CAE no haya visto. La consulta de revisiones pendientes repite este
    /// predicado como expresión SQL; <c>AceptarDeteccionesIaEnBloqueTests</c> comprueba que coinciden.
    /// </summary>
    public bool FijaVigenciaDesdeLaEmision => AplicaVencimientoAutomatico && VigenciaMeses.HasValue;
    public string? Notas { get; private set; }
    public int Orden { get; private set; }
    public string? Descripcion { get; private set; }

    /// <summary>
    /// Los términos de validación tal como los describe la plataforma
    /// documental del cliente (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.7, Lote 0-E) —
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
    /// ¿Se pide este documento? Lo usa la verificación de documentación para
    /// distinguir "no aplica, no cuenta como pendiente" de "se exige y falta,
    /// hay que gestionarlo".
    ///
    /// <para>
    /// Sustituye al antiguo booleano <c>EsObligatorio</c>, que respondía a la
    /// vez a esta pregunta y a la de <see cref="Naturaleza"/> — y colapsarlas
    /// obligaba a mentir en una de las dos.
    /// </para>
    /// </summary>
    public RequisitoDocumental Requerido { get; private set; }

    /// <summary>
    /// ¿Con qué autoridad se pide? Es lo que se le enseña al usuario cuando
    /// pregunta "¿por qué me pides esto?", y el eje donde está prohibido
    /// exagerar: llamar ley a una práctica del sector es el fallo que esta
    /// taxonomía existe para impedir.
    /// </summary>
    public NaturalezaJuridica Naturaleza { get; private set; }

    /// <summary>
    /// ¿Qué revela este tipo sobre una persona física? Eje independiente de
    /// <see cref="Requerido"/> y <see cref="Naturaleza"/> — DEC-34/36
    /// (REC-132): clasificación canónica compartida por la purga de
    /// derivados de IA y la auditoría de acceso a documentos sensibles, para
    /// que ninguna de las dos mantenga su propia lista. Nace en el valor más
    /// protector (<see cref="SensibilidadDocumental.CategoriaEspecialSalud"/>)
    /// para cualquier tipo que un Administrador cree sin especificarlo — ver
    /// el constructor. La edición de este eje para tipos ya existentes no
    /// está expuesta todavía (HO-132-01 § 9.3: decisión elevada al
    /// propietario, no implementada en este incremento).
    /// </summary>
    public SensibilidadDocumental Sensibilidad { get; private set; }

    /// <summary>
    /// <b>Punto único de consulta</b> de sensibilidad de salud — el que deben
    /// usar REC-036 (purga de derivados de IA) y REC-099 (auditoría de
    /// acceso), y el único lugar permitido para decidirlo. Ningún otro código
    /// debe comparar <see cref="Nombre"/> para inferir si un tipo revela
    /// salud (ver el ratchet en Architecture.Tests que lo vigila); la única
    /// excepción es la propuesta de clasificación del catálogo semilla en
    /// <see cref="Infrastructure.Persistence.Seed.TipoDocumentoSeedData"/>,
    /// que alimenta este mismo eje.
    ///
    /// <para>
    /// ⚠️ Igual que <see cref="CuentaParaCumplimiento"/>: propiedad calculada
    /// sin columna detrás, no se traduce a SQL dentro de un <c>Where</c>/
    /// <c>Select</c> sobre el <c>DbSet</c> — comparar
    /// <c>Sensibilidad == SensibilidadDocumental.CategoriaEspecialSalud</c>
    /// directamente ahí.
    /// </para>
    /// </summary>
    public bool RevelaSalud => Sensibilidad == SensibilidadDocumental.CategoriaEspecialSalud;

    /// <summary>
    /// Atajo de lectura: <b>solo <see cref="RequisitoDocumental.Si"/> cuenta
    /// para el cumplimiento</b>. <see cref="RequisitoDocumental.Condicional"/>
    /// queda fuera a propósito mientras no exista la maquinaria que evalúa la
    /// condición — ver el razonamiento en <see cref="RequisitoDocumental"/>.
    ///
    /// <para>
    /// ⚠️ <b>No usar dentro de una consulta EF.</b> Es una propiedad
    /// calculada sin columna detrás, así que no se traduce a SQL: compila,
    /// pasa los tests que no tocan base y revienta en ejecución. Dentro de un
    /// <c>Where</c> o un <c>Select</c> sobre el <c>DbSet</c>, escribir
    /// <c>Requerido == RequisitoDocumental.Si</c>, que sí se traduce.
    /// </para>
    /// </summary>
    public bool CuentaParaCumplimiento => Requerido == RequisitoDocumental.Si;

    /// <summary>
    /// A qué se asocian los Documentos de este tipo — Trabajador, Cliente o
    /// Empresa (ver <see cref="Documento"/>). Fijo desde la creación: no se
    /// puede cambiar más adelante porque ya podría haber Documentos creados
    /// bajo el ámbito original, y cambiarlo los dejaría inconsistentes.
    /// </summary>
    public AmbitoAplicacion AmbitoAplicacion { get; private set; }

    /// <summary>
    /// Serie documental por tipo de contratación (decisión de producto de 2026-10-16): a qué tipos de
    /// contratación del Trabajador (<see cref="TipoContratacion"/>) se pide <b>de serie</b> este tipo, es decir,
    /// cuando <see cref="CuentaParaCumplimiento"/> lo pide y el Centro no tiene fila explícita
    /// (<see cref="TipoDocumentoCentro"/>, «la fila explícita manda»). Solo tiene sentido para Ámbito Trabajador;
    /// en los demás ámbitos se ignora. Nace en <see cref="TiposContratacion.Todos"/>, que conserva el
    /// comportamiento anterior a la decisión: el Administrador del Tenant lo restringe explícitamente (p. ej. una
    /// serie de voluntario sin aptitud médica). Los requisitos del evento y la fila explícita del Centro no pasan
    /// por este filtro.
    /// </summary>
    public TiposContratacion TiposContratacionDeSerie { get; private set; } = TiposContratacion.Todos;

    /// <summary>
    /// ¿Se pide de serie a un Trabajador con este tipo de contratación? <see cref="CuentaParaCumplimiento"/> y,
    /// en Ámbito Trabajador, <see cref="TiposContratacionDeSerie"/>. Propiedad sin columna: no usar dentro de una
    /// consulta EF (mismo aviso que <see cref="CuentaParaCumplimiento"/>).
    /// </summary>
    public bool SePideDeSerieA(TipoContratacion tipoContratacion) =>
        CuentaParaCumplimiento
        && (AmbitoAplicacion != AmbitoAplicacion.Trabajador || TiposContratacionDeSerie.Incluye(tipoContratacion));

    /// <summary>
    /// Fija la serie por tipo de contratación. Exige al menos un tipo: un tipo que no se pide de serie a nadie
    /// se expresa con <see cref="Requerido"/> en No, no con un conjunto vacío que la pantalla no distinguiría.
    /// </summary>
    public void EstablecerTiposContratacionDeSerie(TiposContratacion tipos)
    {
        var normalizados = tipos & TiposContratacion.Todos;
        if (normalizados == TiposContratacion.Ninguno)
            throw new ArgumentException(
                "Marca al menos un tipo de contratación al que se pide de serie este documento.", nameof(tipos));

        TiposContratacionDeSerie = normalizados;
    }

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
    /// pendiente para confirmación humana, con los motivos detectados. Nunca
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
    /// Project-Hydra-Negocio/tecnico/PLAN-FIRMA-DIGITAL-PDF.md). Empieza en <see cref="PerfilDocumentoOficial.Ninguno"/>
    /// (apagado), mismo criterio que <see cref="DeteccionTrabajadoresActiva"/>:
    /// el Administrador lo asigna explícitamente a los tipos que corresponden
    /// (corriente TGSS/AEAT, RNT, RLC, ITA — TipoDocumentoSeedData ya los
    /// trae asignados para tenants nuevos).
    /// </summary>
    public PerfilDocumentoOficial PerfilDocumentoOficial { get; private set; }

    /// <summary>
    /// Siglas, alias históricos o nombres cortos por los que también se
    /// conoce este tipo de documento (p. ej. "TC2", "CIF") — buscables sin
    /// tener que meterlos dentro de <see cref="Nombre"/>. Ver
    /// <see cref="TipoDocumentoAlias"/>.
    /// </summary>
    public IReadOnlyList<TipoDocumentoAlias> Aliases => _aliases.AsReadOnly();

    /// <summary>
    /// Grupo del tipo en la interfaz (catálogo aprobado el 2026-10-17, ver <see cref="GrupoTipoDocumento"/>). Nulo para
    /// los tipos de Vehículo y de Proyecto, que se agrupan por su ámbito, y para un tipo creado por un Tenant que todavía
    /// no lo tiene asignado. Solo ordena y filtra en pantalla: no decide nada de cumplimiento.
    /// </summary>
    public GrupoTipoDocumento? Grupo { get; private set; }

    /// <summary>
    /// Datos que admite un Documento de este tipo además de las fechas y el archivo (horas, nivel, recibo adjunto,
    /// estado «Renuncia»). Empieza en <see cref="DatosAdicionalesDocumento.Ninguno"/>.
    /// </summary>
    public DatosAdicionalesDocumento DatosAdicionales { get; private set; }

    /// <summary>
    /// Tipo retirado del catálogo (decisión del 2026-10-17): no se ofrece en altas nuevas, pero los Documentos que ya lo
    /// tienen lo conservan con su estado. Un tipo archivado nunca se pide de serie (<see cref="Requerido"/> queda en No al
    /// archivarlo, ver <see cref="Archivar"/>): quien lo pedía pasa a pedir su <see cref="SustitutoId"/>, que es el que
    /// se ofrece al renovar.
    /// </summary>
    public bool Archivado { get; private set; }

    /// <summary>
    /// Tipo que sustituye a este cuando está <see cref="Archivado"/>, o nulo si no tiene sustituto. Siempre del mismo
    /// Tenant propietario y del mismo ámbito (la FK compuesta con TenantId lo garantiza en base; <see cref="Archivar"/>
    /// lo comprueba antes).
    /// </summary>
    public Guid? SustitutoId { get; private set; }

    /// <summary>
    /// Tipo «solo conclusión de aptitud» (Inbound M1, PR 4; DN-5 (b) del 2026-10-23): el certificado de aptitud médica,
    /// que por ley lleva solo la conclusión (apto, apto con restricciones, no apto) y no el informe. Solo puede marcarse
    /// en un tipo de categoría especial de salud (<see cref="RevelaSalud"/>), y la base lo repite con un CHECK, y de ámbito
    /// <see cref="AmbitoAplicacion.Trabajador"/> (invariante de dominio). Tiene dos
    /// efectos y ninguno más:
    /// <list type="bullet">
    /// <item>En el catálogo del Tenant propietario de la Empresa contratista, la lectura entre Tenants
    /// (<c>app_inbound_documentos_del_alcance</c>) deja leer los Documentos de este tipo, y solo de este tipo de salud:
    /// cualquier otro tipo <see cref="SensibilidadDocumental.CategoriaEspecialSalud"/>, el informe médico incluido, sigue
    /// excluido. Que el binario de un tipo marcado sea de verdad un certificado es responsabilidad del Tenant propietario
    /// que marca su tipo.</item>
    /// <item>En el catálogo del Tenant del Cliente empresarial, una <c>EvidenciaInbound</c> de este tipo no lleva archivo,
    /// solo la conclusión transcrita.</item>
    /// </list>
    /// </summary>
    public bool SoloConclusionAptitud { get; private set; }

    /// <summary>
    /// Marca o desmarca el tipo «solo conclusión de aptitud». Solo un tipo de salud y de ámbito Trabajador admite la marca:
    /// la conclusión de aptitud es de una persona, y una evidencia de solo conclusión necesita su Trabajador y su documento
    /// de identidad.
    /// </summary>
    public void EstablecerSoloConclusionAptitud(bool soloConclusion)
    {
        if (soloConclusion && !RevelaSalud)
            throw new InvalidOperationException(
                "Solo un tipo de documento de categoría especial de salud puede ser de «solo conclusión de aptitud».");
        if (soloConclusion && AmbitoAplicacion != AmbitoAplicacion.Trabajador)
            throw new InvalidOperationException(
                "Solo un tipo de documento de Trabajador puede ser de «solo conclusión de aptitud».");

        SoloConclusionAptitud = soloConclusion;
    }

    /// <summary>¿Admite un Documento de este tipo el dato indicado? Propiedad sin columna: no usar dentro de una consulta EF.</summary>
    public bool Admite(DatosAdicionalesDocumento dato) => dato != DatosAdicionalesDocumento.Ninguno && (DatosAdicionales & dato) == dato;

    private TipoDocumento()
    {
    }

    public TipoDocumento(
        string nombre,
        int? vigenciaMeses,
        bool aplicaVencimientoAutomatico,
        int orden,
        AmbitoAplicacion ambitoAplicacion,
        RequisitoDocumental requerido = RequisitoDocumental.No,
        NaturalezaJuridica naturaleza = NaturalezaJuridica.RequisitoCliente,
        string? notas = null,
        string? descripcion = null,
        string? criteriosValidacion = null,
        string? seSolicitaA = null,
        string? observaciones = null,
        SensibilidadDocumental sensibilidad = SensibilidadDocumental.CategoriaEspecialSalud)
    {
        EstablecerNombre(nombre);
        EstablecerVigencia(vigenciaMeses, aplicaVencimientoAutomatico);
        Orden = orden;
        AmbitoAplicacion = ambitoAplicacion;
        Requerido = requerido;
        Naturaleza = naturaleza;
        Notas = notas;
        Sensibilidad = sensibilidad;
        EstablecerGlosario(descripcion, criteriosValidacion, seSolicitaA, observaciones);
    }

    public void Actualizar(
        string nombre,
        int? vigenciaMeses,
        bool aplicaVencimientoAutomatico,
        int orden,
        RequisitoDocumental requerido,
        NaturalezaJuridica naturaleza,
        string? notas,
        string? descripcion,
        string? criteriosValidacion,
        string? seSolicitaA,
        string? observaciones)
    {
        if (Archivado && requerido != RequisitoDocumental.No)
            throw new InvalidOperationException(
                "Un tipo de documento archivado no se pide de serie: se pide su sustituto.");

        EstablecerNombre(nombre);
        EstablecerVigencia(vigenciaMeses, aplicaVencimientoAutomatico);
        Orden = orden;
        Requerido = requerido;
        Naturaleza = naturaleza;
        Notas = notas;
        EstablecerGlosario(descripcion, criteriosValidacion, seSolicitaA, observaciones);
    }

    public void EstablecerLecturaIaActiva(bool activa) => LecturaIaActiva = activa;

    public void EstablecerDeteccionTrabajadoresActiva(bool activa) => DeteccionTrabajadoresActiva = activa;

    public void EstablecerVerificacionIaActiva(bool activa) => VerificacionIaActiva = activa;

    public void EstablecerPerfilDocumentoOficial(PerfilDocumentoOficial perfil) => PerfilDocumentoOficial = perfil;

    public void EstablecerGrupo(GrupoTipoDocumento? grupo)
    {
        if (grupo is { } g && !Enum.IsDefined(g))
            throw new ArgumentOutOfRangeException(nameof(grupo), grupo, "Grupo de tipo de documento desconocido.");
        Grupo = grupo;
    }

    public void EstablecerDatosAdicionales(DatosAdicionalesDocumento datos) =>
        DatosAdicionales = datos & DatosAdicionalesDocumento.Todos;

    /// <summary>
    /// Retira el tipo del catálogo: deja de ofrecerse en altas nuevas y de pedirse de serie (<see cref="Requerido"/> pasa
    /// a No). No toca ningún Documento. El sustituto, si lo hay, tiene que ser otro tipo del mismo Tenant propietario y
    /// del mismo ámbito, y no estar archivado.
    /// </summary>
    public void Archivar(TipoDocumento? sustituto)
    {
        if (sustituto is not null)
        {
            if (sustituto.Id == Id)
                throw new ArgumentException("Un tipo de documento no puede sustituirse a sí mismo.", nameof(sustituto));
            if (sustituto.TenantId != TenantId)
                throw new ArgumentException("El sustituto debe ser del mismo Tenant propietario.", nameof(sustituto));
            if (sustituto.AmbitoAplicacion != AmbitoAplicacion)
                throw new ArgumentException("El sustituto debe ser del mismo ámbito.", nameof(sustituto));
            if (sustituto.Archivado)
                throw new ArgumentException("El sustituto no puede estar archivado.", nameof(sustituto));
        }

        Archivado = true;
        SustitutoId = sustituto?.Id;
        Requerido = RequisitoDocumental.No;
    }

    /// <summary>
    /// Reemplaza el conjunto completo de alias — mismo criterio que
    /// <c>ContactoAgenda.EstablecerTiposDocumento</c>: la pantalla edita la
    /// lista entera, así no queda a medias entre dos operaciones. Los
    /// duplicados (sin distinguir mayúsculas/espacios) se colapsan en uno
    /// solo antes de guardar.
    /// </summary>
    public void EstablecerAliases(IEnumerable<string> aliases)
    {
        _aliases.Clear();

        var normalizados = aliases
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var alias in normalizados)
            _aliases.Add(new TipoDocumentoAlias(Id, alias));
    }

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
