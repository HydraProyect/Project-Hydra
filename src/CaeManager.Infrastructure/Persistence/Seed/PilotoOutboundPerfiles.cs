namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// El perfil de Tenant beneficiario: cada Tenant del piloto representa un tipo de cliente real distinto, de modo que
/// cambiar de Tenant en pantalla enseña otro estado documental (plan 07-SIEMBRA-REALISTA-PLAN, § 2).
/// </summary>
public enum PerfilPilotoOutbound
{
    /// <summary>A · Al día: casi todo vigente, algo próximo y algo pendiente de validar; con un Centro «Vigente».</summary>
    AlDia,

    /// <summary>B · Recién incorporada: casi todo pendiente de aportar, algo pendiente de validar y algún rechazo.</summary>
    RecienIncorporada,

    /// <summary>C · Multicliente: varios Clientes empresariales con varios Centros cada uno y una mezcla de estados.</summary>
    Multicliente,

    /// <summary>D · Obras y maquinaria: Proyectos de obra activos, maquinaria con ITC y seguro.</summary>
    ObrasYMaquinaria,

    /// <summary>E · Autónomo: una sola persona, sin plantilla.</summary>
    Autonomo,

    /// <summary>F · Con subcontratas: cadena de dos niveles y una Subcontrata Supervisada con verificación externa.</summary>
    ConSubcontratas,

    /// <summary>G · Bloqueada: documentos caducados, un requisito que bloquea el acceso y reclamaciones sin respuesta.</summary>
    Bloqueada,

    /// <summary>
    /// H · Eventos con voluntarios: pocos Trabajadores fijos y muchos voluntarios que entran por Proyectos de tipo Evento
    /// (uno terminado, uno en curso y uno futuro); los que solo estuvieron en el terminado quedan inactivos.
    /// </summary>
    EventosConVoluntarios,

    /// <summary>
    /// I · Escaparate al día: una Empresa grande (plantilla, Subcontratas con sus Trabajadores, Vehículos y maquinaria,
    /// Proyectos, Visitas, plataforma y canal por correo) con todo vigente: ningún documento vencido, ninguno que venza en
    /// los próximos treinta días y ninguno que falte.
    /// </summary>
    EscaparateAlDia,

    /// <summary>
    /// J · Escaparate por vencer: todo vigente salvo un grupo de documentos (de Trabajador, de la Empresa propia, de un
    /// Vehículo y de una Subcontrata) que vence exactamente a los <see cref="TenantPilotoOutbound.UrgentesVencenALosDias"/>
    /// días de la demostración.
    /// </summary>
    EscaparatePorVencer,

    /// <summary>
    /// K · Escaparate con pocos vencidos: todo en orden salvo unos pocos documentos vencidos (de Trabajador, de un Vehículo
    /// y de una Subcontrata), los de Trabajador con su reclamación.
    /// </summary>
    EscaparatePocosVencidos
}

/// <summary>
/// De qué Gestor CAE del Operador CAE externo es la Asignación de Cartera del Tenant beneficiario. Cada Gestor CAE tiene
/// Tenants exclusivos y uno se comparte; la Coordinadora CAE ve los de los dos (por <c>CoordinadorUsuarioId</c>).
/// </summary>
public enum CarteraPilotoOutbound
{
    GestoraPrimera,
    GestorSegundo,
    Compartida
}

/// <summary>En qué está un Proyecto de tipo Evento todos los días en que se puede sembrar (D−9 a D).</summary>
public enum EstadoEventoPilotoOutbound { Terminado, EnCurso, Futuro }

/// <summary>
/// Un Proyecto de tipo Evento en un Centro. <paramref name="Participantes"/> son índices de Trabajador del Tenant (los
/// propios y, a continuación, los de sus Subcontratas): cada uno entra con un alta en el evento y, si el evento terminó,
/// con su baja el último día.
/// </summary>
public sealed record EventoPilotoOutbound(
    string Nombre, int Centro, int DiasDesdeLaDemostracion, int DuracionDias, EstadoEventoPilotoOutbound Estado,
    IReadOnlyList<int> Participantes);

/// <summary>La plantilla de requisitos que comparten los eventos de un Cliente empresarial: nombres de Tipo de documento.</summary>
public sealed record PlantillaEventoPilotoOutbound(string Nombre, IReadOnlyList<string> Tipos);

/// <param name="Clave">Letra del perfil (A..G): va en los mensajes de la autoverificación y de la siembra, nunca en un dato que se vea en pantalla.</param>
/// <param name="Nombre">Nombre del Tenant propietario y razón social de su Empresa propia (la única con EsPropia).</param>
/// <param name="Cartera">El Gestor CAE que lo lleva en su cartera, o los dos si es el compartido.</param>
/// <param name="Trabajadores">Trabajadores de la Empresa propia; los de cada Subcontrata se suman a continuación.</param>
public sealed record TenantPilotoOutbound(
    string Clave, string Nombre, PerfilPilotoOutbound Perfil, CarteraPilotoOutbound Cartera,
    int Trabajadores, IReadOnlyList<ClienteEmpresarialPilotoOutbound> ClientesEmpresariales)
{
    public bool EnCarteraDeLaGestoraPrimera => Cartera != CarteraPilotoOutbound.GestorSegundo;
    public bool EnCarteraDelGestorSegundo => Cartera != CarteraPilotoOutbound.GestoraPrimera;

    public IReadOnlyList<CentroPilotoOutbound> Centros { get; } = [.. ClientesEmpresariales.SelectMany(c => c.Centros)];

    /// <summary>Cuántos Trabajadores propios hay en cada Centro, en el orden de <see cref="Centros"/>: suman <see cref="Trabajadores"/>.</summary>
    public required IReadOnlyList<int> TrabajadoresPorCentro { get; init; }

    /// <summary>Trabajadores propios cuya Asignación está dada de baja y que han causado baja en la Empresa.</summary>
    public IReadOnlyList<int> DeBaja { get; init; } = [];

    /// <summary>Segundas Asignaciones activas (Trabajador, Centro): un Trabajador en dos Centros.</summary>
    public IReadOnlyList<(int Trabajador, int Centro)> DobleAsignacion { get; init; } = [];

    public IReadOnlyList<SubcontrataPilotoOutbound> Subcontratas { get; init; } = [];
    public IReadOnlyList<VehiculoPilotoOutbound> Vehiculos { get; init; } = [];
    public IReadOnlyList<ObraPilotoOutbound> Obras { get; init; } = [];
    public IReadOnlyList<VisitaPilotoOutbound> Visitas { get; init; } = [];

    /// <summary>Cuántos de los Trabajadores propios son voluntarios: los últimos de la lista (tipo de contratación «Voluntario»).</summary>
    public int Voluntarios { get; init; }

    public IReadOnlyList<EventoPilotoOutbound> Eventos { get; init; } = [];
    public PlantillaEventoPilotoOutbound? PlantillaDeEventos { get; init; }

    /// <summary>Si el Trabajador propio <paramref name="i"/> es voluntario.</summary>
    public bool EsVoluntario(int i) => i >= Trabajadores - Voluntarios && i < Trabajadores;

    /// <summary>
    /// Los voluntarios que quedan inactivos (regla de <c>ActividadVoluntario</c>): los que no participan en ningún evento en
    /// curso ni futuro. Deducido del catálogo, no medido.
    /// </summary>
    public int VoluntariosInactivos =>
        Enumerable.Range(Trabajadores - Voluntarios, Voluntarios)
            .Count(v => !Eventos.Any(e => e.Estado != EstadoEventoPilotoOutbound.Terminado && e.Participantes.Contains(v)));
    public IReadOnlyList<ExtranjeroPilotoOutbound> Extranjeros { get; init; } = [];

    /// <summary>Un Trabajador cedido por una ETT (tipo de vínculo «ETT»).</summary>
    public int? TrabajadorDeEtt { get; init; }

    public RecetaDocumentalPilotoOutbound Receta { get; init; } = new();

    /// <summary>El Centro con canal de plataforma (acreditaciones externas), si lo hay.</summary>
    public int? CentroConPlataforma { get; init; }

    /// <summary>El Centro que se gestiona por correo (canal principal por correo, sin plataforma), si lo hay.</summary>
    public int? CentroPorCorreo { get; init; }

    /// <summary>Un Centro que concede tolerancia a un tipo de documento, con un Trabajador suyo cuyo documento está vencido dentro de ella.</summary>
    public ToleranciaPilotoOutbound? Tolerancia { get; init; }

    /// <summary>
    /// Las tres filas de Mi trabajo que vienen de la IA y de los correos: una revisión de IA, una detección de alta y una
    /// sugerencia de Visita, pendientes. Son registros, no llaman a ningún proveedor.
    /// </summary>
    public bool ConSenalesDeIa { get; init; }

    /// <summary>El Centro con un requisito que bloquea el acceso (la aptitud médica), si lo hay.</summary>
    public int? CentroConRequisitoBloqueante { get; init; }

    /// <summary>Tipos de documento de Empresa que los Centros del Tenant no exigen.</summary>
    public IReadOnlyList<string> TiposDeEmpresaNoExigidos { get; init; } = [];

    /// <summary>
    /// Sin plantilla: no hay RLC, RNT ni ITA (un autónomo sin empleados no los tiene), así que la Empresa no lleva esos
    /// documentos aunque el catálogo los exija por defecto, y la persona es su propia Trabajadora.
    /// </summary>
    public bool EmpresaSinPlantilla { get; init; }

    /// <summary>
    /// Si se fija, todo lo «Urgente» del Tenant —los documentos de Trabajador de la receta, el del Vehículo, el de la
    /// Empresa propia y el de la Subcontrata— vence exactamente este número de días después de la demostración, en vez
    /// de repartirse entre D+5 y D+6. Tiene que caer dentro del umbral rojo (quince días) o no sería «Urgente».
    /// </summary>
    public int? UrgentesVencenALosDias { get; init; }

    /// <summary>
    /// Situación del primer documento exigido de la Empresa propia que no es mensual (los mensuales solo pueden estar
    /// «Próximo»); <c>null</c> = en regla, como todos los demás.
    /// </summary>
    public SituacionDocumentoPilotoOutbound? SituacionDocumentoDeEmpresa { get; init; }

    /// <summary>
    /// En el Centro con plataforma, los documentos de Empresa —los de la propia y los de cada Subcontrata— también se
    /// acreditan, y quedan aceptados: el Cliente empresarial los tiene validados en su portal.
    /// </summary>
    public bool AcreditaDocumentosDeEmpresa { get; init; }

    /// <summary>
    /// Trabajadores que causaron baja en la Empresa en el último año: se siembran aparte (no cuentan en
    /// <see cref="Trabajadores"/>), dados de baja en meses anteriores, para que la rotación del Cuadro de dirección tenga
    /// bajas además de altas.
    /// </summary>
    public int BajasDelUltimoAno { get; init; }

    public int ReclamacionesSinRespuesta { get; init; }
    public int ReclamacionesRespondidas { get; init; }
    public int GestionesPendientes { get; init; }
    public bool ConDocumentoRenovado { get; init; }

    /// <summary>Total de Trabajadores sembrados: los de la Empresa propia y los de sus Subcontratas.</summary>
    public int TrabajadoresSembrados => Trabajadores + Subcontratas.Sum(s => s.Trabajadores);

    /// <summary>La Subcontrata (por posición en <see cref="Subcontratas"/>) del Trabajador <paramref name="i"/>, o <c>null</c> si es propio.</summary>
    public int? SubcontrataDe(int i)
    {
        var resto = i - Trabajadores;
        if (resto < 0) return null;

        for (var s = 0; s < Subcontratas.Count; s++)
        {
            if (resto < Subcontratas[s].Trabajadores) return s;
            resto -= Subcontratas[s].Trabajadores;
        }

        return null;
    }

    /// <summary>
    /// El código del Centro, como lo escribiría la Empresa propia: tres letras y un número de orden, sin la clave
    /// de la matriz. Las letras son las de su localidad y el número cuenta los Centros del Tenant que las comparten.
    /// </summary>
    public string CodigoDe(CentroPilotoOutbound centro)
    {
        var letras = IdentidadesPilotoOutbound.LetrasDeLaLocalidad(centro.Localidad);
        var conEsasLetras = Centros.Where(c => IdentidadesPilotoOutbound.LetrasDeLaLocalidad(c.Localidad) == letras).ToList();
        return $"{letras}-{conEsasLetras.IndexOf(centro) + 1:D2}";
    }
}

/// <param name="Trabajadores">Trabajadores de la Subcontrata: ninguna Subcontrata del piloto es decorativa.</param>
/// <param name="Supervisada">Nivel de servicio Supervisada: TALVEG verifica su documentación en la plataforma del Cliente empresarial.</param>
/// <param name="ContratadaPor">Posición, en la lista de Subcontratas, de la que la contrata (cadena); <c>null</c> = la contrata la Empresa propia.</param>
/// <param name="Situacion">Situación del primer documento de su Empresa (<see cref="CatalogoPilotoOutbound.TiposDeDocumentoDeSubcontrata"/>); <c>null</c> = en regla.</param>
public sealed record SubcontrataPilotoOutbound(
    string RazonSocial, int Trabajadores, bool Supervisada = false, int? ContratadaPor = null, SituacionDocumentoPilotoOutbound? Situacion = null);

/// <param name="Situacion">Situación del primer documento exigido del Vehículo (la ITC); <c>null</c> = en regla.</param>
public sealed record VehiculoPilotoOutbound(
    string Nombre, string Modelo, string Placa, string Tipo, string Titularidad, string Aseguradora,
    IReadOnlyList<int> Conductores, SituacionDocumentoPilotoOutbound? Situacion = null);

/// <param name="DiasHastaLaCaducidad">Negativo = caducado.</param>
public sealed record ExtranjeroPilotoOutbound(int Trabajador, string Nacionalidad, string TipoPermiso, int DiasHastaLaCaducidad);

public sealed record ObraPilotoOutbound(string Nombre, int Centro, int DiasDesdeElInicio, int? MesesPrevistos, int Tecnicos, bool Cerrada = false);

/// <summary>
/// «EnCurso»: ya empezó y no ha terminado, y lo sigue estando todos los días en que se puede sembrar (D−9 a D): su duración
/// abarca ese margen entero. Una «Futura» que empieza a un día de la demostración está dentro de la ventana de aviso de 48 h
/// (urgente) el día D−1 y el propio D; los días anteriores del margen aún no lo es.
/// </summary>
public enum EstadoVisitaPilotoOutbound { Futura, EnCurso, Realizada, Vencida, Cancelada }

/// <summary>Una Visita: «Realizada» tiene la documentación gestionada; «Vencida» pasó sin gestionarla.</summary>
/// <param name="PorCorreo">Visita de origen «Correo» al Centro que se gestiona por correo: la comprobación previa y el ZIP salen de ella.</param>
public sealed record VisitaPilotoOutbound(
    int Centro, int DiasDesdeLaDemostracion, int DuracionDias, EstadoVisitaPilotoOutbound Estado, string Notas, bool PorCorreo = false);

/// <summary>El Centro que concede días de tolerancia a un tipo de documento, y el documento de un Trabajador suyo que venció hace <paramref name="DiasDesdeQueVencio"/>.</summary>
public sealed record ToleranciaPilotoOutbound(int Centro, string Tipo, int Dias, int DiasDesdeQueVencio);

/// <summary>
/// Cuántos pares (Trabajador, tipo exigido) se apartan de «en regla», repartidos sin azar entre los Trabajadores desde
/// <see cref="PrimerTrabajadorAfectado"/>. Los demás están Vigentes o sin caducidad. Un Vencido, Urgente o Próximo solo cae en
/// un tipo que vence solo, y un Sin confirmar solo en uno que no: si no quedan pares compatibles, el recuento es menor.
/// </summary>
/// <param name="AcreditacionesSubidas">En el Centro con plataforma: acreditaciones pendientes de validar.</param>
/// <param name="AcreditacionesRechazadas">En el Centro con plataforma: acreditaciones rechazadas, con motivo.</param>
public sealed record RecetaDocumentalPilotoOutbound(
    int Vencidos = 0, int Urgentes = 0, int Proximos = 0, int Ausentes = 0, int SinConfirmar = 0,
    int AcreditacionesSubidas = 0, int AcreditacionesRechazadas = 0, int PrimerTrabajadorAfectado = 0, int HastaElTrabajador = int.MaxValue);

/// <summary>Un intervalo cerrado: lo que la autoverificación admite donde el diseño no fija un número exacto.</summary>
public sealed record IntervaloPilotoOutbound(int Minimo, int Maximo)
{
    public bool Contiene(int? valor) => valor is { } v && v >= Minimo && v <= Maximo;

    public override string ToString() => $"entre {Minimo} y {Maximo}";
}

/// <summary>
/// Lo que Inicio tiene que contar de los documentos de Trabajador de un Tenant, deducido de la receta
/// (<see cref="CatalogoPilotoOutbound.DocumentosEsperadosEnInicio"/>): intervalos para lo que depende del día de medición.
/// </summary>
public sealed record InicioDocumentalEsperadoPilotoOutbound(
    IntervaloPilotoOutbound Vencidos, IntervaloPilotoOutbound Urgentes, IntervaloPilotoOutbound Proximos, int SinConfirmar);

/// <summary>
/// Lo que la autoverificación exige de cada perfil además de su estructura (que sale del catálogo): los porcentajes,
/// por intervalo, porque el perfil fija una proporción y no un número, escritos a mano.
/// </summary>
/// <param name="CentrosVigentesMinimo">Centros que Centros pinta «Vigente» como mínimo (el perfil A los tiene).</param>
public sealed record EsperadoPilotoOutbound(
    IntervaloPilotoOutbound CumplimientoInicio, IntervaloPilotoOutbound CumplimientoEmpresa, int CentrosVigentesMinimo = 0);

/// <summary>
/// El catálogo declarativo de la siembra del piloto del Servicio TALVEG Outbound: un Operador CAE externo de
/// demostración y ocho Tenants beneficiarios (A..H), uno por perfil. Todos los nombres propios viven aquí; el
/// sembrador, la autoverificación y los tests los referencian por constante. Las razones sociales y las personas son
/// inventadas y no coinciden con ninguna empresa ni persona real conocida.
/// </summary>
public static class CatalogoPilotoOutbound
{
    public const string NombreTenantOperador = "Coordinación Preventiva Nelvaris, S.L.";

    public const string NombreTenantA = "Frío y Clima Arvelo, S.L.";
    public const string NombreTenantB = "Montajes Eléctricos Tarnaval, S.L.";
    public const string NombreTenantC = "Servicios Técnicos Orvelta, S.L.";
    public const string NombreTenantD = "Obras y Estructuras Dalverde, S.A.";
    public const string NombreTenantE = "Rubén Salas Quintana";
    public const string NombreTenantF = "Instalaciones y Montajes Pelvira, S.A.";
    public const string NombreTenantG = "Reformas y Pavimentos Quiroga, S.L.";
    public const string NombreTenantH = "Eventos Deportivos Valmora, S.L.";
    public const string NombreTenantI = "Mantenimientos Integrales Corvesta, S.A.";
    public const string NombreTenantJ = "Instalaciones Térmicas Albrenza, S.L.";
    public const string NombreTenantK = "Servicios Industriales Quelvara, S.L.";

    /// <summary>
    /// Los nombres de los seis Tenants de la versión anterior del piloto. Ya no se siembran, pero la retirada del lote
    /// los sigue reconociendo para poder quitarlos de un servidor que los sembró.
    /// </summary>
    public static IReadOnlyList<string> NombresTenantsDeVersionesAnteriores { get; } =
    [
        "Instalaciones Gavrena, S.A.", "Climatización Kedrobal, S.L.", "Mantenimiento Industrial Ratvenco, S.L.",
        "Montajes Tilvar, S.L.", "Inspección Técnica Ostrimel, S.L.", "Servicios Técnicos Anfelor, S.L."
    ];

    public const string NombreGestoraPrimera = "Laura Benítez Soto";
    public const string NombreGestorSegundo = "Daniel Rubio Marín";
    public const string NombreCoordinadora = "Carmen Lozano Prieto";
    public const string NombreAdministradorOperador = "Jorge Sancho Vera";
    public const string NombreDireccionCae = "Elena Garrido Ocaña";
    public const string NombreAdministradorT1 = "Beatriz Molina Ferrer";
    public const string NombreUsuarioDeClienteEmpresarialT1 = "Ramón Ibáñez Cuesta";
    public const string NombreGestorPendienteDeActivacion = "Sergio Navas Olmedo";

    public const string AptitudMedica = "Certificado de aptitud médica";
    public const string EntregaEpi = "Entrega de EPI";
    public const string FormacionArt19 = "Formación Art. 19";
    public const string InformacionArt18 = "Información Art. 18";
    public const string DocumentoIdentidad = "Documento de identidad";

    /// <summary>Un tipo de ámbito Trabajador que NO se exige por defecto: solo lo pide el Centro que lo incluye.</summary>
    public const string CarretillasElevadoras = "Carretillas elevadoras";

    /// <summary>
    /// Los únicos Tipos de documento que la siembra deja «no caduca». Son los de vencimiento manual cuya nota del
    /// catálogo (<see cref="TipoDocumentoSeedData"/>) dice que no tienen caducidad, o no les da ninguna. Todos los demás
    /// Tipos sin vencimiento automático llevan una fecha anotada a mano y la siembra se la pone
    /// (<see cref="FechasPilotoOutbound.EnRegla"/>).
    /// </summary>
    public static IReadOnlyList<string> TiposQueNoCaducan { get; } =
        ["Formación 60h (base convenio)", InformacionArt18, CarretillasElevadoras, "Contrato de Trabajo", "Alta en Seguridad Social", LibroDeSubcontratacion];

    /// <summary>Los cinco tipos de ámbito Trabajador exigidos por defecto. La siembra comprueba que el catálogo del Tenant dice lo mismo antes de escribir.</summary>
    public static IReadOnlyList<string> TiposExigidosDeTrabajador { get; } =
        [AptitudMedica, EntregaEpi, FormacionArt19, InformacionArt18, DocumentoIdentidad];

    /// <summary>
    /// Los documentos de un Trabajador que nacen con su relación laboral: ninguno puede estar emitido antes de que la persona
    /// cause alta en la Empresa (el Documento de identidad y el permiso de trabajo sí: son anteriores al contrato).
    /// </summary>
    public static IReadOnlyList<string> TiposPosterioresAlAlta { get; } = [FormacionArt19, AptitudMedica, InformacionArt18, EntregaEpi];

    /// <summary>Lo que cada Subcontrata tiene de su propia Empresa.</summary>
    public static IReadOnlyList<string> TiposDeDocumentoDeSubcontrata { get; } =
        ["Seguro de Responsabilidad Civil + recibo de pago", "Evaluación de Riesgos Laborales", "Modalidad Preventiva"];

    /// <summary>
    /// El Registro de Empresas Acreditadas (Ley 32/2006): un tipo de Empresa que no se exige por defecto ni cuenta para el
    /// cumplimiento de quien no pisa una obra (es «Condicional»). El perfil de obras lo lleva y sus Centros, que son obras, lo piden.
    /// </summary>
    public const string CertificadoReaConstruccion = "Certificado de inscripción en el REA";

    /// <summary>Uno por obra (art. 8 Ley 32/2006): tipo de ámbito Proyecto, sin caducidad mientras dure la obra.</summary>
    public const string LibroDeSubcontratacion = "Libro de Subcontratación";

    /// <summary>El justificante mensual de la cuota del autónomo: el autónomo es su propia Empresa, y sus Centros se lo piden.</summary>
    public const string ReciboDeAutonomos = "Recibo de cuota de autónomos (RETA)";

    /// <summary>Los dos certificados mensuales de la Empresa: coherentes con su tipo solo pueden estar «Próximo», y tiñen de ámbar todo Centro que los exige.</summary>
    public static IReadOnlyList<string> CertificadosMensualesDeEmpresa { get; } =
        ["Certificado de estar al corriente con la Seguridad Social", "ITA"];

    /// <summary>Lo que un autónomo sin plantilla no tiene: ni RLC, ni RNT, ni ITA.</summary>
    public static IReadOnlyList<string> TiposDeEmpresaDePlantilla { get; } =
        ["RLC", "Recibo de pago RLC/TC1", "RLC/TC1 + Recibo de pago", "RNT", "ITA"];

    private static readonly ZonaPilotoOutbound? SinZona = null;

    private static CentroPilotoOutbound Centro(string nombre, string localidad) => new(nombre, localidad, SinZona);

    /// <summary>
    /// A · Al día. Dos Centros de un Cliente empresarial: el primero, con sus Trabajadores en regla, es «Vigente»; en el
    /// segundo está lo que aún se mueve (próximos y pendientes de validar). Los dos certificados mensuales no se exigen en
    /// sus Centros: un documento mensual coherente solo puede estar «Próximo» y los teñiría todos de ámbar.
    /// </summary>
    public static TenantPilotoOutbound A { get; } = new("A", NombreTenantA, PerfilPilotoOutbound.AlDia, CarteraPilotoOutbound.GestorSegundo, 16,
    [
        new("Hipermercados Valtora, S.A.",
        [
            Centro("Hipermercado de Getafe", "Getafe"), Centro("Almacén de frescos de Coslada", "Coslada")
        ])
    ])
    {
        TrabajadoresPorCentro = [10, 6],
        Vehiculos =
        [
            new("Furgón de taller", "Furgón de carga media", "4821KBT", "Furgoneta", "Propiedad", "Aseguradora Nortia", [0, 1]),
            new("Furgoneta de servicio", "Furgoneta compacta", "7305LMN", "Furgoneta", "Renting", "Seguros Almenara", [10],
                SituacionDocumentoPilotoOutbound.Proximo)
        ],
        Receta = new(Proximos: 6, SinConfirmar: 5, AcreditacionesSubidas: 3, PrimerTrabajadorAfectado: 10),
        CentroConPlataforma = 1,
        TiposDeEmpresaNoExigidos = CertificadosMensualesDeEmpresa,
        ConDocumentoRenovado = true,
        Visitas =
        [
            new(0, 21, 1, EstadoVisitaPilotoOutbound.Futura, "Revisión trimestral de instalaciones"),
            new(1, 45, 2, EstadoVisitaPilotoOutbound.Futura, "Parada programada de las cámaras frigoríficas"),
            new(0, -28, 1, EstadoVisitaPilotoOutbound.Realizada, "Revisión anual de los equipos de climatización")
        ]
    };

    /// <summary>B · Recién incorporada: un solo Centro, casi todo pendiente de aportar y algún rechazo en la plataforma.</summary>
    public static TenantPilotoOutbound B { get; } = new("B", NombreTenantB, PerfilPilotoOutbound.RecienIncorporada, CarteraPilotoOutbound.GestoraPrimera, 7,
    [
        new("Logística Burcena, S.A.", [Centro("Plataforma logística de Illescas", "Illescas")])
    ])
    {
        TrabajadoresPorCentro = [7],
        Vehiculos = [new("Furgoneta de montaje", "Furgoneta de carga ligera", "2967HRT", "Furgoneta", "Renting", "Seguros Almenara", [0, 1])],
        Receta = new(Ausentes: 24, SinConfirmar: 2, AcreditacionesSubidas: 3, AcreditacionesRechazadas: 2),
        CentroConPlataforma = 0,
        GestionesPendientes = 3,
        Visitas =
        [
            new(0, 10, 2, EstadoVisitaPilotoOutbound.Futura, "Entrada de la primera cuadrilla para el replanteo de las canalizaciones"),
            new(0, 1, 1, EstadoVisitaPilotoOutbound.Futura, "Reconocimiento inicial del centro antes del arranque")
        ]
    };

    /// <summary>
    /// C · Multicliente: tres Clientes empresariales con dos Centros cada uno, exigencias distintas, un Trabajador en dos
    /// Centros, uno de baja, otro cedido por una ETT y dos extranjeros con permiso de trabajo (uno próximo a caducar).
    /// </summary>
    public static TenantPilotoOutbound C { get; } = new("C", NombreTenantC, PerfilPilotoOutbound.Multicliente, CarteraPilotoOutbound.Compartida, 28,
    [
        new("Química Hastrel, S.A.",
        [
            Centro("Planta química de Tarragona", "Tarragona"), Centro("Laboratorio de control de Reus", "Reus")
        ]),
        new("Energías Lindavo, S.A.",
        [
            Centro("Subestación de Albacete", "Albacete"), Centro("Parque fotovoltaico de Almansa", "Almansa")
        ]),
        new("Conservas Palvecor, S.A.",
        [
            Centro("Fábrica de conservas de Calahorra", "Calahorra"), Centro("Planta de envasado de Alfaro", "Alfaro")
        ])
    ])
    {
        TrabajadoresPorCentro = [6, 5, 5, 4, 4, 4],
        DeBaja = [27],
        DobleAsignacion = [(0, 1), (8, 3)],
        TrabajadorDeEtt = 22,
        Extranjeros =
        [
            new(3, "Marroquí", "Autorización de residencia y trabajo por cuenta ajena", 24),
            new(11, "Ecuatoriana", "Autorización de residencia y trabajo por cuenta ajena", 300)
        ],
        Vehiculos =
        [
            new("Furgón de mantenimiento 1", "Furgón de carga media", "1340JBD", "Furgoneta", "Propiedad", "Aseguradora Nortia", [0, 1, 2],
                SituacionDocumentoPilotoOutbound.Vencido),
            new("Furgón de mantenimiento 2", "Furgón de carga media", "1342JBD", "Furgoneta", "Propiedad", "Aseguradora Nortia", [5, 6]),
            new("Furgoneta de guardia", "Furgoneta compacta", "6619KHM", "Furgoneta", "Renting", "Seguros Almenara", [9]),
            new("Pick-up de coordinación", "Pick-up doble cabina", "8854LCN", "Turismo", "Renting", "Seguros Almenara", [14])
        ],
        // Hasta el Trabajador 24 (exclusive): los del último Centro, el que se gestiona por correo, quedan con todo en regla.
        Receta = new(Vencidos: 11, Urgentes: 4, Proximos: 8, Ausentes: 10, SinConfirmar: 6, AcreditacionesSubidas: 5, AcreditacionesRechazadas: 2, HastaElTrabajador: 24),
        CentroConPlataforma = 2,
        CentroPorCorreo = 5,
        Tolerancia = new(3, FormacionArt19, 30, 12),
        ConSenalesDeIa = true,
        ReclamacionesRespondidas = 1,
        ConDocumentoRenovado = true,
        Visitas =
        [
            new(0, 4, 1, EstadoVisitaPilotoOutbound.Futura, "Parada técnica de la planta"),
            new(5, 4, 1, EstadoVisitaPilotoOutbound.Futura, "Revisión de inversores y cuadros de protección", PorCorreo: true),
            new(3, 30, 3, EstadoVisitaPilotoOutbound.Futura, "Mantenimiento anual del parque fotovoltaico"),
            new(4, -12, 1, EstadoVisitaPilotoOutbound.Realizada, "Revisión de las líneas de envasado"),
            new(1, -13, 1, EstadoVisitaPilotoOutbound.Vencida, "Auditoría interna del laboratorio"),
            new(2, 9, 1, EstadoVisitaPilotoOutbound.Cancelada, "Medición de tierras de la subestación")
        ]
    };

    /// <summary>D · Obras y maquinaria: tres Centros que son obras, cuatro Proyectos abiertos y uno cerrado, y maquinaria con ITC y seguro.</summary>
    public static TenantPilotoOutbound D { get; } = new("D", NombreTenantD, PerfilPilotoOutbound.ObrasYMaquinaria, CarteraPilotoOutbound.GestoraPrimera, 20,
    [
        new("Promociones Ibaltar, S.A.",
        [
            Centro("Obra residencial Los Pinares de Boadilla", "Boadilla del Monte"), Centro("Obra de la nave logística de Alcalá", "Alcalá de Henares")
        ]),
        new("Parques Solares Nevedra, S.L.", [Centro("Obra de la planta solar de Talavera", "Talavera de la Reina")])
    ])
    {
        TrabajadoresPorCentro = [8, 7, 5],
        Vehiculos =
        [
            new("Camión grúa", "Camión grúa articulada", "3412JKL", "Camión", "Propiedad", "Aseguradora Nortia", [0, 1],
                SituacionDocumentoPilotoOutbound.Proximo),
            new("Retroexcavadora", "Retroexcavadora mixta", "E5521BCD", "Maquinaria de obra", "Propiedad", "Aseguradora Nortia", [2, 3]),
            new("Plataforma elevadora", "Plataforma elevadora de tijera", "E7788BDF", "Maquinaria de obra", "Alquiler", "Seguros Almenara", [8],
                SituacionDocumentoPilotoOutbound.Vencido),
            new("Dumper", "Dumper articulado", "E1234CCD", "Maquinaria de obra", "Propiedad", "Aseguradora Nortia", [15]),
            new("Furgón de obra", "Furgón de carga media", "6104LLN", "Furgoneta", "Propiedad", "Seguros Almenara", [4, 5]),
            new("Pick-up del encargado", "Pick-up doble cabina", "9027MNP", "Turismo", "Renting", "Seguros Almenara", [6])
        ],
        Obras =
        [
            new("Estructura de la fase 1 de Los Pinares", 0, -150, 14, 5),
            new("Urbanización y viales de Los Pinares", 0, -60, 8, 3),
            new("Cimentación de la nave logística de Alcalá", 1, -90, 6, 4),
            new("Montaje de seguidores de la planta solar de Talavera", 2, -40, 10, 5),
            new("Demolición de la nave previa de Alcalá", 1, -300, 4, 3, Cerrada: true)
        ],
        Receta = new(Vencidos: 4, Urgentes: 4, Proximos: 6, Ausentes: 3, SinConfirmar: 3),
        Visitas =
        [
            new(0, 6, 1, EstadoVisitaPilotoOutbound.Futura, "Inspección de seguridad del andamiaje"),
            new(2, 27, 2, EstadoVisitaPilotoOutbound.Futura, "Visita de la propiedad al replanteo de seguidores"),
            new(1, -18, 1, EstadoVisitaPilotoOutbound.Realizada, "Replanteo de la cimentación"),
            new(0, -10, 14, EstadoVisitaPilotoOutbound.EnCurso, "Montaje de la grúa torre y de los medios auxiliares")
        ]
    };

    /// <summary>E · Autónomo: una persona que es su propia Trabajadora, sin plantilla ni documentos de plantilla, con dos Centros.</summary>
    public static TenantPilotoOutbound E { get; } = new("E", NombreTenantE, PerfilPilotoOutbound.Autonomo, CarteraPilotoOutbound.GestoraPrimera, 1,
    [
        new("Papelera Mirtaldo, S.A.", [Centro("Fábrica de papel de Hernani", "Hernani")]),
        new("Siderúrgica Moltrevi, S.A.", [Centro("Acería de Avilés", "Avilés")])
    ])
    {
        TrabajadoresPorCentro = [1, 0],
        DobleAsignacion = [(0, 1)],
        EmpresaSinPlantilla = true,
        TiposDeEmpresaNoExigidos = TiposDeEmpresaDePlantilla,
        Vehiculos = [new("Furgoneta de trabajo", "Furgoneta compacta", "5530JHB", "Furgoneta", "Propiedad", "Seguros Almenara", [0])],
        Visitas = [new(0, 18, 1, EstadoVisitaPilotoOutbound.Futura, "Reparación de la bomba de recirculación")]
    };

    /// <summary>F · Con subcontratas: tres Centros, tres Subcontratas (una Supervisada) y una de ellas contratada por otra Subcontrata.</summary>
    public static TenantPilotoOutbound F { get; } = new("F", NombreTenantF, PerfilPilotoOutbound.ConSubcontratas, CarteraPilotoOutbound.GestorSegundo, 14,
    [
        new("Terminal Portuaria Tusmedo, S.A.", [Centro("Terminal de contenedores de Algeciras", "Algeciras")]),
        new("Centros de Datos Cinvelto, S.L.", [Centro("Centro de datos de Alcobendas", "Alcobendas")]),
        new("Centros Comerciales Tramuel, S.A.", [Centro("Centro comercial de Málaga", "Málaga")])
    ])
    {
        TrabajadoresPorCentro = [5, 5, 4],
        DeBaja = [13],
        Subcontratas =
        [
            new("Andamios Torrevia, S.L.", 6),
            new("Electricidad Marnovel, S.L.", 5, Supervisada: true),
            new("Soldaduras Hiscana, S.L.", 3, ContratadaPor: 0)
        ],
        Vehiculos =
        [
            new("Furgón de montaje", "Furgón de carga media", "2208KDS", "Furgoneta", "Propiedad", "Aseguradora Nortia", [0, 1]),
            new("Furgoneta de supervisión", "Furgoneta compacta", "4471LGH", "Furgoneta", "Renting", "Seguros Almenara", [5]),
            new("Plataforma de trabajo", "Plataforma elevadora de tijera", "E3390BJK", "Maquinaria de obra", "Alquiler", "Seguros Almenara", [10])
        ],
        Receta = new(Vencidos: 3, Urgentes: 2, Proximos: 4, Ausentes: 3, SinConfirmar: 2),
        ConDocumentoRenovado = true,
        Visitas =
        [
            new(0, 8, 1, EstadoVisitaPilotoOutbound.Futura, "Parada de la grúa pórtico"),
            new(1, -10, 1, EstadoVisitaPilotoOutbound.Realizada, "Revisión de los cuadros eléctricos")
        ]
    };

    /// <summary>G · Bloqueada: un solo Centro con un requisito que bloquea el acceso, muchos documentos caducados y reclamaciones sin respuesta.</summary>
    public static TenantPilotoOutbound G { get; } = new("G", NombreTenantG, PerfilPilotoOutbound.Bloqueada, CarteraPilotoOutbound.GestoraPrimera, 8,
    [
        new("Laboratorios Visquena, S.A.", [Centro("Planta farmacéutica de Alcalá de Henares", "Alcalá de Henares")])
    ])
    {
        TrabajadoresPorCentro = [8],
        Extranjeros = [new(2, "Boliviana", "Autorización de residencia y trabajo por cuenta ajena", -35)],
        Vehiculos =
        [
            new("Furgoneta de obra", "Furgoneta de carga ligera", "3188KRM", "Furgoneta", "Propiedad", "Seguros Almenara", [0, 1],
                SituacionDocumentoPilotoOutbound.Vencido)
        ],
        Receta = new(Vencidos: 14, Urgentes: 3, Ausentes: 8),
        CentroConRequisitoBloqueante = 0,
        ReclamacionesSinRespuesta = 2,
        GestionesPendientes = 4,
        Visitas =
        [
            new(0, 5, 1, EstadoVisitaPilotoOutbound.Futura, "Reforma del pavimento de la zona de producción"),
            new(0, -12, 1, EstadoVisitaPilotoOutbound.Vencida, "Replanteo de la solera sin documentación gestionada")
        ]
    };

    /// <summary>
    /// H · Eventos con voluntarios: tres Trabajadores fijos por cuenta ajena y veinte voluntarios en el estadio de un
    /// Cliente empresarial, una Subcontrata de control de accesos y tres eventos con requisitos copiados de una plantilla.
    /// Los voluntarios que solo estuvieron en el evento terminado (del 3 al 9) quedan inactivos; los nuevos del evento
    /// futuro (del 19 al 22) tienen requisitos por aportar desde su asignación, igual que la Subcontrata.
    /// </summary>
    public static TenantPilotoOutbound H { get; } = new("H", NombreTenantH, PerfilPilotoOutbound.EventosConVoluntarios, CarteraPilotoOutbound.GestorSegundo, 23,
    [
        new("Recintos Deportivos Altaverde, S.A.", [Centro("Estadio Altaverde de Leganés", "Leganés")])
    ])
    {
        TrabajadoresPorCentro = [23],
        Voluntarios = 20,
        Subcontratas = [new("Control de Accesos Brivental, S.L.", 4)],
        Vehiculos = [new("Furgoneta de material", "Furgoneta de carga ligera", "8146LPR", "Furgoneta", "Propiedad", "Seguros Almenara", [0, 2])],
        // Solo los voluntarios del evento en curso y del futuro (del 17 al 22): los fijos, al día, y los inactivos sin ruido.
        Receta = new(Proximos: 2, Ausentes: 4, SinConfirmar: 1, PrimerTrabajadorAfectado: 17, HastaElTrabajador: 23),
        PlantillaDeEventos = new("Jornada en el estadio", [DocumentoIdentidad, FormacionArt19, InformacionArt18, EntregaEpi]),
        Eventos =
        [
            new("Partido de liga, jornada 4", 0, -45, 1, EstadoEventoPilotoOutbound.Terminado, [0, 1, 2, .. Enumerable.Range(3, 12)]),
            new("Torneo de otoño de fútbol base", 0, -12, 16, EstadoEventoPilotoOutbound.EnCurso, [0, 1, .. Enumerable.Range(10, 9), 23, 24]),
            new("Partido de liga, jornada 9", 0, 20, 1, EstadoEventoPilotoOutbound.Futuro, [0, 2, .. Enumerable.Range(17, 6), 23, 24, 25, 26])
        ]
    };

    /// <summary>
    /// I · Escaparate al día: tres Clientes empresariales con cinco Centros, sesenta y cuatro Trabajadores propios y tres
    /// Subcontratas con los suyos, Vehículos y maquinaria, dos Proyectos, Visitas, un Centro con plataforma (con los
    /// documentos de Empresa también acreditados) y otro por correo. Nada vencido, nada que venza en treinta días y nada
    /// que falte: los certificados mensuales no se exigen en sus Centros, porque uno coherente solo puede estar «Próximo»;
    /// sin Subcontrata Supervisada, porque su verificación externa trae un «no válido».
    /// </summary>
    public static TenantPilotoOutbound I { get; } = new("I", NombreTenantI, PerfilPilotoOutbound.EscaparateAlDia, CarteraPilotoOutbound.GestoraPrimera, 64,
    [
        new("Distribución Alimentaria Pradoverde, S.A.",
        [
            Centro("Plataforma de frío de Valdemoro", "Valdemoro"), Centro("Almacén regional de Zaragoza", "Zaragoza")
        ]),
        new("Hospitales Sanvitra, S.A.",
        [
            Centro("Hospital Sanvitra de Valladolid", "Valladolid"), Centro("Clínica Sanvitra de Burgos", "Burgos")
        ]),
        new("Automoción Ferrelda, S.A.", [Centro("Planta de montaje de Vigo", "Vigo")])
    ])
    {
        TrabajadoresPorCentro = [16, 12, 14, 10, 12],
        DeBaja = [63],
        DobleAsignacion = [(0, 1), (30, 3)],
        Subcontratas =
        [
            new("Andamios Pelaguera, S.L.", 6),
            new("Electricidad Industrial Sorvento, S.L.", 5),
            new("Soldaduras Cantabrana, S.L.", 3, ContratadaPor: 0)
        ],
        Extranjeros =
        [
            new(20, "Colombiana", "Autorización de residencia y trabajo por cuenta ajena", 290),
            new(45, "Peruana", "Autorización de residencia y trabajo por cuenta ajena", 410)
        ],
        Vehiculos =
        [
            new("Furgón de mantenimiento 1", "Furgón de carga media", "5162LRT", "Furgoneta", "Propiedad", "Aseguradora Nortia", [0, 1]),
            new("Furgón de mantenimiento 2", "Furgón de carga media", "5164LRT", "Furgoneta", "Propiedad", "Aseguradora Nortia", [16, 17]),
            new("Furgoneta de guardia", "Furgoneta compacta", "7748LSV", "Furgoneta", "Renting", "Seguros Almenara", [28]),
            new("Camión taller", "Camión ligero de taller", "3381LTZ", "Camión", "Propiedad", "Aseguradora Nortia", [52, 53]),
            new("Plataforma elevadora", "Plataforma elevadora de tijera", "E4471BKP", "Maquinaria de obra", "Alquiler", "Seguros Almenara", [42]),
            new("Pick-up de coordinación", "Pick-up doble cabina", "2093MBC", "Turismo", "Renting", "Seguros Almenara", [2])
        ],
        Obras =
        [
            new("Renovación de la climatización del bloque quirúrgico", 2, -60, 8, 4),
            new("Ampliación de la línea de pintura", 4, -30, 6, 3)
        ],
        CentroConPlataforma = 2,
        CentroPorCorreo = 4,
        AcreditaDocumentosDeEmpresa = true,
        TiposDeEmpresaNoExigidos = CertificadosMensualesDeEmpresa,
        ConSenalesDeIa = true,
        ConDocumentoRenovado = true,
        BajasDelUltimoAno = 6,
        Visitas =
        [
            new(0, 12, 1, EstadoVisitaPilotoOutbound.Futura, "Revisión trimestral de las cámaras de frío"),
            new(2, 25, 2, EstadoVisitaPilotoOutbound.Futura, "Parada programada de las unidades de tratamiento de aire"),
            new(4, 6, 1, EstadoVisitaPilotoOutbound.Futura, "Mantenimiento preventivo de los robots de pintura", PorCorreo: true),
            new(3, -5, 14, EstadoVisitaPilotoOutbound.EnCurso, "Sustitución de las enfriadoras de la clínica"),
            new(1, -20, 1, EstadoVisitaPilotoOutbound.Realizada, "Revisión anual de los muelles de carga"),
            new(2, -40, 1, EstadoVisitaPilotoOutbound.Realizada, "Inspección de la instalación de gases medicinales")
        ]
    };

    /// <summary>
    /// J · Escaparate por vencer: dos Clientes empresariales con tres Centros y dos Subcontratas. Todo vigente salvo seis
    /// documentos de Trabajador, el primero del primer Vehículo, uno de la Empresa propia y uno de la segunda Subcontrata,
    /// que vencen exactamente a los cuatro días de la demostración; una Visita entra el día antes.
    /// </summary>
    public static TenantPilotoOutbound J { get; } = new("J", NombreTenantJ, PerfilPilotoOutbound.EscaparatePorVencer, CarteraPilotoOutbound.GestorSegundo, 26,
    [
        new("Supermercados Garvesa, S.A.",
        [
            Centro("Supermercado de Alcorcón", "Alcorcón"), Centro("Centro logístico de Arganda", "Arganda del Rey")
        ]),
        new("Residencias Almenor, S.L.", [Centro("Residencia de mayores de Toledo", "Toledo")])
    ])
    {
        TrabajadoresPorCentro = [10, 8, 8],
        Subcontratas =
        [
            new("Montajes de Andamios Uxarra, S.L.", 4),
            new("Electricidad Brezalva, S.L.", 3, Situacion: SituacionDocumentoPilotoOutbound.Urgente)
        ],
        Vehiculos =
        [
            new("Furgoneta de instalación", "Furgoneta de carga media", "6235KZB", "Furgoneta", "Propiedad", "Aseguradora Nortia", [0, 1],
                SituacionDocumentoPilotoOutbound.Urgente),
            new("Furgoneta de guardia", "Furgoneta compacta", "1879LPD", "Furgoneta", "Renting", "Seguros Almenara", [10]),
            new("Plataforma elevadora", "Plataforma elevadora de tijera", "E2846BHJ", "Maquinaria de obra", "Alquiler", "Seguros Almenara", [18])
        ],
        Obras = [new("Renovación de la climatización de la residencia", 2, -45, 5, 3)],
        Receta = new(Urgentes: 6),
        UrgentesVencenALosDias = 4,
        SituacionDocumentoDeEmpresa = SituacionDocumentoPilotoOutbound.Urgente,
        CentroConPlataforma = 0,
        AcreditaDocumentosDeEmpresa = true,
        TiposDeEmpresaNoExigidos = CertificadosMensualesDeEmpresa,
        ConSenalesDeIa = true,
        ConDocumentoRenovado = true,
        BajasDelUltimoAno = 2,
        Visitas =
        [
            new(0, 3, 1, EstadoVisitaPilotoOutbound.Futura, "Revisión de los equipos de frío del obrador"),
            new(2, 15, 1, EstadoVisitaPilotoOutbound.Futura, "Revisión de la caldera y del agua caliente sanitaria"),
            new(1, -14, 1, EstadoVisitaPilotoOutbound.Realizada, "Mantenimiento de las cámaras del centro logístico")
        ]
    };

    /// <summary>
    /// K · Escaparate con pocos vencidos: dos Clientes empresariales con tres Centros y dos Subcontratas. Todo en orden
    /// salvo cinco documentos vencidos: tres de Trabajador (los tres con su reclamación, dos sin respuesta y una
    /// respondida), el primero del primer Vehículo y el primero de la primera Subcontrata.
    /// </summary>
    public static TenantPilotoOutbound K { get; } = new("K", NombreTenantK, PerfilPilotoOutbound.EscaparatePocosVencidos, CarteraPilotoOutbound.GestoraPrimera, 24,
    [
        new("Cementos Valdurna, S.A.",
        [
            Centro("Fábrica de cemento de Morata", "Morata de Tajuña"), Centro("Cantera de Chinchón", "Chinchón")
        ]),
        new("Aguas Depuradas Lirvana, S.A.", [Centro("Depuradora de Aranjuez", "Aranjuez")])
    ])
    {
        TrabajadoresPorCentro = [9, 8, 7],
        Subcontratas =
        [
            new("Montajes Galdarra, S.L.", 3, Situacion: SituacionDocumentoPilotoOutbound.Vencido),
            new("Electricidad Ribalmo, S.L.", 3)
        ],
        Vehiculos =
        [
            new("Camión taller", "Camión ligero de taller", "4410JTV", "Camión", "Propiedad", "Aseguradora Nortia", [0, 1],
                SituacionDocumentoPilotoOutbound.Vencido),
            new("Furgoneta de mantenimiento", "Furgón de carga media", "8053KSC", "Furgoneta", "Propiedad", "Seguros Almenara", [9]),
            new("Pick-up de guardia", "Pick-up doble cabina", "9127LGR", "Turismo", "Renting", "Seguros Almenara", [17])
        ],
        Obras = [new("Sustitución de la cinta transportadora de la cantera", 1, -20, 3, 3)],
        Receta = new(Vencidos: 3),
        CentroConPlataforma = 2,
        AcreditaDocumentosDeEmpresa = true,
        TiposDeEmpresaNoExigidos = CertificadosMensualesDeEmpresa,
        ConSenalesDeIa = true,
        ReclamacionesSinRespuesta = 2,
        ReclamacionesRespondidas = 1,
        BajasDelUltimoAno = 3,
        Visitas =
        [
            new(0, 9, 1, EstadoVisitaPilotoOutbound.Futura, "Revisión del horno y de la torre de intercambiadores"),
            new(2, 20, 2, EstadoVisitaPilotoOutbound.Futura, "Limpieza de los decantadores"),
            new(1, -16, 1, EstadoVisitaPilotoOutbound.Realizada, "Revisión de la machacadora primaria")
        ]
    };

    /// <summary>Los tres Tenants escaparate: el grande al día, el que tiene cosas por vencer y el que tiene unas pocas vencidas.</summary>
    public static IReadOnlyList<TenantPilotoOutbound> Escaparate { get; } = [I, J, K];

    /// <summary>A..K, en el orden de los perfiles.</summary>
    public static IReadOnlyList<TenantPilotoOutbound> Tenants { get; } = [A, B, C, D, E, F, G, H, I, J, K];

    /// <summary>
    /// El orden en que se siembran: los pequeños primero y los grandes al final (C y el escaparate al día), para que una
    /// ejecución en un servidor con poca memoria haya dejado hecho lo más posible si se interrumpe en el mayor.
    /// </summary>
    public static IReadOnlyList<TenantPilotoOutbound> EnOrdenDeSiembra { get; } = [E, B, G, A, F, H, D, K, J, C, I];

    /// <summary>
    /// El Tenant que lleva además las cuentas propias del piloto (un Administrador del Tenant y un Usuario de Cliente de su
    /// primer Cliente empresarial): el multicliente, que es donde un Usuario de Cliente tiene sentido.
    /// </summary>
    public static TenantPilotoOutbound TenantConCuentasPropias => C;

    /// <summary>Los Tenants del lote, con los de versiones anteriores y el del Operador CAE externo al final: es el orden de retirada.</summary>
    public static IReadOnlyList<string> NombresTenants { get; } =
        [.. Tenants.Select(t => t.Nombre), .. NombresTenantsDeVersionesAnteriores, NombreTenantOperador];

    /// <summary>Las razones sociales que el catálogo inventado da a las Empresas del Tenant: la propia, sus Clientes empresariales y sus Subcontratas.</summary>
    public static IReadOnlySet<string> RazonesSocialesDe(TenantPilotoOutbound tenant) =>
        new HashSet<string>([tenant.Nombre, .. tenant.ClientesEmpresariales.Select(c => c.RazonSocial), .. tenant.Subcontratas.Select(s => s.RazonSocial)]);

    /// <summary>
    /// El reparto de carteras del Operador CAE externo (decisión del 2026-10-16): cada Gestor CAE con al menos un Tenant
    /// exclusivo y exactamente un Tenant compartido. Vacía si el catálogo lo cumple.
    /// </summary>
    public static IReadOnlyList<string> DiscrepanciasDelRepartoDeCarteras(IReadOnlyList<TenantPilotoOutbound> tenants)
    {
        var d = new List<string>();
        if (!tenants.Any(t => t.Cartera == CarteraPilotoOutbound.GestoraPrimera))
            d.Add("Carteras · la Gestora CAE primera no tiene ningún Tenant exclusivo.");
        if (!tenants.Any(t => t.Cartera == CarteraPilotoOutbound.GestorSegundo))
            d.Add("Carteras · el segundo Gestor CAE no tiene ningún Tenant exclusivo.");
        if (tenants.Count(t => t.Cartera == CarteraPilotoOutbound.Compartida) != 1)
            d.Add("Carteras · no hay exactamente un Tenant compartido por los dos Gestores CAE.");
        return d;
    }

    /// <summary>Cuántos Trabajadores siembra el Tenant: los de su Empresa propia y los de sus Subcontratas.</summary>
    public static int TrabajadoresSembrados(TenantPilotoOutbound tenant) => tenant.TrabajadoresSembrados;

    /// <summary>Los tipos del catálogo que vencen solos, por nombre: lo único que el reparto de la receta necesita saber de un tipo.</summary>
    private static readonly Lazy<IReadOnlyDictionary<string, bool>> VencenSolos = new(() =>
    {
        var vencen = new Dictionary<string, bool>();
        foreach (var tipo in TipoDocumentoSeedData.CrearCopiasParaTenant())
            vencen.TryAdd(tipo.Nombre, tipo.AplicaVencimientoAutomatico);
        return vencen;
    });

    /// <summary>Si el tipo del catálogo vence solo (vencimiento automático): el reparto de la receta lo usa para decidir qué situación le cabe.</summary>
    public static bool VencePorSuTipo(string nombreDeTipo) => VencenSolos.Value[nombreDeTipo];

    /// <summary>
    /// Los pares (Trabajador, tipo exigido) que se apartan de «en regla», según la receta del perfil. Se reparten sin azar:
    /// los pares, de los Trabajadores desde el primero afectado y sin los de baja, se recorren con un paso primo con su
    /// número, y cada uno recibe la primera situación de la receta que le cabe. Es la única definición: la usan el
    /// sembrador, que la escribe, y la autoverificación, que mide lo escrito contra ella (el recuento de la receta es un
    /// máximo: si no quedan pares compatibles con una situación, el recuento realizado es menor).
    /// </summary>
    public static List<DesviacionDocumentoPilotoOutbound> Desviaciones(TenantPilotoOutbound tenant, int total, Func<string, bool> vencePorSuTipo)
    {
        var receta = tenant.Receta;
        var pares = new List<(int Trabajador, string Tipo)>();
        for (var t = receta.PrimerTrabajadorAfectado; t < Math.Min(total, receta.HastaElTrabajador); t++)
        {
            if (tenant.DeBaja.Contains(t)) continue;
            foreach (var tipo in TiposExigidosDeTrabajador)
                pares.Add((t, tipo));
        }

        var resultado = new List<DesviacionDocumentoPilotoOutbound>();
        if (pares.Count > 0)
        {
            var paso = new[] { 7, 11, 13, 17, 19, 23, 29 }.First(p => pares.Count % p != 0);
            var (vencidos, urgentes, proximos, ausentes, sinConfirmar) =
                (receta.Vencidos, receta.Urgentes, receta.Proximos, receta.Ausentes, receta.SinConfirmar);

            for (var k = 0; k < pares.Count; k++)
            {
                var (t, nombre) = pares[(int)((long)k * paso % pares.Count)];
                var vencePorSuTipoElPar = vencePorSuTipo(nombre);
                SituacionDocumentoPilotoOutbound? situacion = null;

                if (vencePorSuTipoElPar && vencidos > 0) { situacion = SituacionDocumentoPilotoOutbound.Vencido; vencidos--; }
                else if (vencePorSuTipoElPar && urgentes > 0) { situacion = SituacionDocumentoPilotoOutbound.Urgente; urgentes--; }
                else if (vencePorSuTipoElPar && proximos > 0) { situacion = SituacionDocumentoPilotoOutbound.Proximo; proximos--; }
                else if (!vencePorSuTipoElPar && sinConfirmar > 0) { situacion = SituacionDocumentoPilotoOutbound.SinConfirmar; sinConfirmar--; }
                else if (ausentes > 0) { situacion = SituacionDocumentoPilotoOutbound.Ausente; ausentes--; }

                if (situacion is { } s) resultado.Add(new DesviacionDocumentoPilotoOutbound(t, nombre, s));
            }
        }

        // El Centro con un requisito que bloquea el acceso lo tiene incumplido de verdad: el primer Trabajador, con la aptitud vencida.
        if (tenant.CentroConRequisitoBloqueante is not null)
        {
            resultado.RemoveAll(d => d.Trabajador == 0 && d.Tipo == AptitudMedica);
            resultado.Add(new DesviacionDocumentoPilotoOutbound(0, AptitudMedica, SituacionDocumentoPilotoOutbound.Vencido));
        }

        return resultado;
    }

    /// <summary>
    /// Lo que Inicio cuenta de los documentos de Trabajador de un Tenant, deducido de la receta y de lo demás que el
    /// catálogo declara: las desviaciones realizadas, el documento vencido del Trabajador de la tolerancia y el permiso
    /// de trabajo de cada extranjero. Los permisos están fechados desde la demostración y Inicio los clasifica con el día
    /// en que se mide (de D−<see cref="OpcionesPilotoOutbound.MargenMaximoDias"/> a D), así que lo suyo es un intervalo;
    /// lo demás está fijado para todos esos días y es exacto.
    /// </summary>
    public static InicioDocumentalEsperadoPilotoOutbound DocumentosEsperadosEnInicio(TenantPilotoOutbound tenant)
    {
        var desviaciones = Desviaciones(tenant, tenant.TrabajadoresSembrados, VencePorSuTipo);
        int Cuenta(SituacionDocumentoPilotoOutbound s) => desviaciones.Count(d => d.Situacion == s);

        var fijoVencidos = Cuenta(SituacionDocumentoPilotoOutbound.Vencido) + (tenant.Tolerancia is null ? 0 : 1);
        var fijoUrgentes = Cuenta(SituacionDocumentoPilotoOutbound.Urgente);
        var fijoProximos = Cuenta(SituacionDocumentoPilotoOutbound.Proximo);

        var (vencidos, urgentes, proximos) = (new List<int>(), new List<int>(), new List<int>());
        var referencia = new DateOnly(2030, 1, 1);
        for (var dias = 0; dias <= OpcionesPilotoOutbound.MargenMaximoDias; dias++)
        {
            var estados = tenant.Extranjeros
                .Select(e => CaeManager.Domain.Documentos.CalculadoraEstadoDocumento.Calcular(
                    CaeManager.Domain.Documentos.VigenciaDocumento.VenceEl(referencia.AddDays(e.DiasHastaLaCaducidad + dias)), referencia, ParametroSistemaSeedData.UmbralAmbarDias, ParametroSistemaSeedData.UmbralRojoDias))
                .ToList();
            vencidos.Add(estados.Count(e => e == CaeManager.Domain.Documentos.EstadoDocumento.Vencido));
            urgentes.Add(estados.Count(e => e == CaeManager.Domain.Documentos.EstadoDocumento.Urgente));
            proximos.Add(estados.Count(e => e == CaeManager.Domain.Documentos.EstadoDocumento.Proximo));
        }

        return new(
            new(fijoVencidos + vencidos.Min(), fijoVencidos + vencidos.Max()),
            new(fijoUrgentes + urgentes.Min(), fijoUrgentes + urgentes.Max()),
            new(fijoProximos + proximos.Min(), fijoProximos + proximos.Max()),
            Cuenta(SituacionDocumentoPilotoOutbound.SinConfirmar));
    }

    /// <summary>
    /// Cuántas Visitas «urgentes» cuenta Inicio un día de medición: las que están en curso y las que empiezan dentro de la
    /// ventana de aviso de 48 h (hoy no cuenta como futura). <paramref name="diasHastaLaDemostracion"/> es D menos el día de
    /// medición: de 0 (el propio día) a <see cref="OpcionesPilotoOutbound.MargenMaximoDias"/>.
    /// </summary>
    public static int VisitasUrgentesEn(TenantPilotoOutbound tenant, int diasHastaLaDemostracion) =>
        tenant.Visitas.Count(v => v.Estado == EstadoVisitaPilotoOutbound.EnCurso
            || (v.Estado == EstadoVisitaPilotoOutbound.Futura && v.DiasDesdeLaDemostracion + diasHastaLaDemostracion is >= 1 and <= 2));

    /// <summary>
    /// Los porcentajes que la autoverificación exige de cada perfil, escritos a mano a partir de lo que el perfil dice
    /// (plan 07, § 2) y de lo medido en la primera siembra.
    /// </summary>
    public static EsperadoPilotoOutbound Esperado(TenantPilotoOutbound tenant) =>
        tenant.Perfil switch
        {
            PerfilPilotoOutbound.AlDia => new(new(75, 98), new(75, 100), CentrosVigentesMinimo: 1),
            PerfilPilotoOutbound.RecienIncorporada => new(new(70, 95), new(10, 45)),
            PerfilPilotoOutbound.Multicliente => new(new(60, 95), new(50, 92)),
            PerfilPilotoOutbound.ObrasYMaquinaria => new(new(60, 95), new(60, 95)),
            PerfilPilotoOutbound.Autonomo => new(new(80, 100), new(80, 100)),
            PerfilPilotoOutbound.ConSubcontratas => new(new(60, 100), new(60, 95)),
            PerfilPilotoOutbound.Bloqueada => new(new(0, 60), new(0, 60)),
            PerfilPilotoOutbound.EventosConVoluntarios => new(new(60, 100), new(60, 100)),
            PerfilPilotoOutbound.EscaparateAlDia => new(new(100, 100), new(100, 100), CentrosVigentesMinimo: 5),
            PerfilPilotoOutbound.EscaparatePorVencer => new(new(90, 100), new(80, 100)),
            PerfilPilotoOutbound.EscaparatePocosVencidos => new(new(85, 99), new(75, 100)),
            _ => throw new ArgumentOutOfRangeException(nameof(tenant))
        };
}
