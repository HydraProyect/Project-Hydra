using CaeManager.Domain.Visitas;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>Cómo está la vigencia de un documento en la maqueta de las páginas 360.</summary>
public enum ClaseVigenciaMaqueta
{
    /// <summary>Con fecha de vencimiento.</summary>
    VenceEl,

    /// <summary>Emitido y sin fecha de vencimiento confirmada («Sin confirmar»).</summary>
    SinConfirmar,

    /// <summary>«No caduca», confirmado.</summary>
    NoCaduca,

    /// <summary>No hay documento: si la siembra de base lo trae, se retira («Pendiente»).</summary>
    Ausente
}

/// <param name="EmitidoEn">Días desde el hoy de la maqueta hasta la emisión (nunca positivo: una emisión no puede ser futura).</param>
/// <param name="VenceEn">Días desde el hoy de la maqueta hasta el vencimiento; solo con <see cref="ClaseVigenciaMaqueta.VenceEl"/>.</param>
public sealed record VigenciaMaqueta(ClaseVigenciaMaqueta Clase, int EmitidoEn, int? VenceEn)
{
    public static VigenciaMaqueta Vence(int emitidoEn, int venceEn) => new(ClaseVigenciaMaqueta.VenceEl, emitidoEn, venceEn);
    public static VigenciaMaqueta SinConfirmar(int emitidoEn) => new(ClaseVigenciaMaqueta.SinConfirmar, emitidoEn, null);
    public static VigenciaMaqueta NoCaduca(int emitidoEn) => new(ClaseVigenciaMaqueta.NoCaduca, emitidoEn, null);
    public static VigenciaMaqueta Ausente { get; } = new(ClaseVigenciaMaqueta.Ausente, 0, null);
}

/// <param name="Titular">Nombre completo del Trabajador, razón social de la Empresa, nombre del Vehículo o del Proyecto.</param>
/// <param name="Tipo">Nombre exacto del Tipo de documento en el catálogo del Tenant propietario.</param>
public sealed record DocumentoMaqueta(string Titular, string Tipo, VigenciaMaqueta Vigencia);

/// <param name="Empleador">Razón social de la Empresa que lo emplea (ya sembrada por la rama de Pizza Planet).</param>
/// <param name="DeSubcontrata">El empleador ocupa la posición de subcontrata en su Relación Empresarial.</param>
public sealed record TrabajadorNuevoMaqueta(string Nombre, string Apellidos, string Identificacion, string Empleador, bool DeSubcontrata)
{
    public string NombreCompleto => $"{Nombre} {Apellidos}";
}

/// <summary>Asignación de un Trabajador a un Centro que la maqueta enseña y la siembra de base no tiene.</summary>
public sealed record AsignacionMaqueta(string Trabajador, string Centro, int AltaEn, int? BajaEn = null);

/// <summary>Asignación de la siembra de base que la maqueta no enseña: se da de baja, no se borra.</summary>
public sealed record BajaDeAsignacionMaqueta(string Trabajador, string Centro, int BajaEn);

/// <summary>Lo que un Centro dice de un Tipo de documento (fila <c>TipoDocumentoCentro</c>).</summary>
public sealed record RequisitoDeCentroMaqueta(
    string Tipo, string Centro, bool Incluido = true, bool BloqueaAcceso = false, int? ToleranciaDias = null, int? PeriodicidadMeses = null);

public sealed record VehiculoMaqueta(string Nombre, string Modelo, string Matricula, string Empleador, bool DeSubcontrata);

/// <param name="ClienteEmpresarial">Razón social del Cliente empresarial titular del Centro del proyecto.</param>
public sealed record ProyectoMaqueta(
    string Nombre, string ClienteEmpresarial, string Centro, int InicioEn, int FinPrevistoEn, string? Notas);

public sealed record TecnicoDeProyectoMaqueta(string Proyecto, string Trabajador, int AltaEn, int? BajaEn = null);

/// <summary>Tipo de documento de ámbito Proyecto: el catálogo de serie no trae ninguno.</summary>
public sealed record TipoDeProyectoMaqueta(string Nombre, int? VigenciaMeses);

public sealed record VisitaMaqueta(
    string Centro, int DesdeEn, int HastaEn, TimeOnly? Entrada, OrigenVisita Origen, IReadOnlyList<string> Trabajadores);

/// <param name="ValidoHastaEn">Solo con resultado válido.</param>
public sealed record VerificacionExternaMaqueta(string Tipo, string Centro, int ComprobadaEn, bool Valido, int? ValidoHastaEn = null);

/// <summary>
/// Los datos de ejemplo de la maqueta «Páginas 360 nuevas» (Subcontrata, Vehículo, Tipo de documento, Proyecto y
/// Visita), declarados para que <see cref="Fichas360DemoSeeder"/> los deje en el Tenant propietario Pizza Planet
/// encima de lo que ya siembra su rama de <see cref="CatalogoEscenariosDireccionDemo"/>.
///
/// <para>
/// <b>Las fechas son desplazamientos, no fechas.</b> La maqueta está dibujada un día concreto
/// (<see cref="HoyDeLaMaqueta"/>) y el proceso no tiene reloj que fijar, así que cada fecha suya se guarda como los
/// días que la separan de ese día (<see cref="Dia"/>) y se siembra como <c>DiaDeNegocio.Hoy() + desplazamiento</c>.
/// Así un documento que en la maqueta «caduca en 8 días» caduca en 8 días el día que se siembre, y los estados
/// (Vencido, Por vencer, En tolerancia, Vigente, Sin confirmar, Pendiente) son los de la maqueta cualquier día.
/// Las fechas absolutas que pinte la ficha solo coinciden con las de la maqueta si se siembra ese mismo día.
/// </para>
///
/// <para>
/// <b>La maqueta no es coherente entre sus cinco páginas</b> (cada una es un ejemplo suelto) y una sola base de
/// datos no puede reproducir las cinco a la vez. Regla de desempate, única: para los documentos «Entrega de EPI» y
/// «Certificado de aptitud médica» de un Trabajador manda la página de Tipo de documento, que es la que da fecha y
/// estado por Centro; en todo lo demás manda la página en la que esa entidad es protagonista.
/// </para>
///
/// <para>Todo es ficticio: ningún nombre, identificación ni matrícula corresponde a nadie real.</para>
/// </summary>
public static class CatalogoFichas360Demo
{
    /// <summary>El día en que está dibujada la maqueta: «06/10 – 08/10/2026 · termina hoy», «ITV vencida el 02/09/2026, hace 36 días».</summary>
    public static readonly DateOnly HoyDeLaMaqueta = new(2026, 10, 8);

    /// <summary>Días desde <see cref="HoyDeLaMaqueta"/> hasta una fecha de la maqueta (negativo si es anterior).</summary>
    public static int Dia(int dia, int mes, int anio) => new DateOnly(anio, mes, dia).DayNumber - HoyDeLaMaqueta.DayNumber;

    public const string NombreTenant = CatalogoEscenariosDireccionDemo.NombreTenantPizzaPlanet;

    // Empresas que ya siembra la rama de Pizza Planet. Aquí solo se nombran para localizarlas.
    public const string Cyberdyne = "Cyberdyne Ibérica S.A.";
    public const string Skynet = "Montajes Skynet S.L.";
    public const string Terminator = "Transportes Terminator S.L.";
    public const string Umbrella = "Umbrella Corporation Ibérica S.A.";
    public const string Raccoon = "Limpiezas Raccoon S.L.";
    public const string Nemesis = "Laboratorios Nemesis S.L.";

    // Centros de Trabajo que ya siembra esa rama (su nombre sale del índice de cada Cliente empresarial).
    public const string SedeSevilla = "Sede Sevilla";
    public const string PlantaBilbao = "Planta Bilbao";
    public const string SedeBilbao = "Sede Bilbao";
    public const string PlantaMurcia = "Planta Murcia";
    public const string AlmacenVigo = "Almacén Vigo";

    /// <summary>Los cuatro Centros de Trabajo que la maqueta enseña exigiendo documentos.</summary>
    public static readonly IReadOnlyList<string> CentrosDeLaMaqueta = [SedeSevilla, PlantaBilbao, PlantaMurcia, AlmacenVigo];

    // Tipos de documento, con el nombre del catálogo (la maqueta abrevia algunos: «Aptitud médica», «ITV»…).
    public const string AptitudMedica = "Certificado de aptitud médica";
    public const string EntregaEpi = "Entrega de EPI";
    public const string FormacionArt19 = "Formación Art. 19";
    public const string InformacionArt18 = "Información Art. 18";
    public const string ContratoDeTrabajo = "Contrato de Trabajo";
    public const string DocumentoIdentidad = "Documento de identidad";
    public const string Rnt = "RNT";
    public const string Rlc = "RLC";
    public const string CorrienteSeguridadSocial = "Certificado de estar al corriente con la Seguridad Social";
    public const string CorrienteHacienda = "Certificado de estar al corriente con Hacienda";
    public const string SeguroResponsabilidadCivil = "Seguro de Responsabilidad Civil + recibo de pago";
    public const string EvaluacionDeRiesgos = "Evaluación de Riesgos Laborales";
    public const string PlanificacionPreventiva = "Planificación de la Actividad Preventiva";
    public const string ServicioDePrevencion = "Servicio de Prevención Ajeno";
    public const string InspeccionTecnica = "ITC";
    public const string SeguroDelVehiculo = "Seguro";
    public const string FichaTecnica = "Ficha técnica";
    public const string PermisoDeCirculacion = "Autorización de circulación";
    public const string ActaDeCoordinacion = "Acta de coordinación";
    public const string AperturaDeCentro = "Apertura de centro de trabajo";
    public const string PlanDeSeguridadYSalud = "Plan de seguridad y salud";

    public const string ProyectoReforma = "Reforma nave Sevilla";
    public const string ProyectoMantenimiento = "Mantenimiento Almacén Vigo";

    public const string CamionGrua = "Camión grúa";
    public const string MatriculaCamionGrua = "9012 GHI";

    public const string EtiquetaCanalPorCorreo = "Gestión por correo";
    public const string CorreoDeAccesosSedeSevilla = "accesos.sevilla@umbrella-iberica.example";

    /// <summary>Días de tolerancia tras vencer que Cyberdyne Ibérica S.A. concede a la Entrega de EPI en sus Centros.</summary>
    public const int ToleranciaEpiDeCyberdyne = 8;

    /// <summary>Cada cuántos meses pide Planta Bilbao renovar la Entrega de EPI.</summary>
    public const int PeriodicidadEpiEnPlantaBilbao = 6;

    /// <summary>
    /// Trabajadores que la maqueta enseña y la rama de Pizza Planet no tiene. Las identificaciones son las de la
    /// maqueta con la letra de control corregida (las suyas no pasan la validación del dominio); las de Iván Ruiz
    /// Sala y Marta Rey Soto, que la maqueta no da, siguen la misma serie.
    /// </summary>
    public static readonly IReadOnlyList<TrabajadorNuevoMaqueta> TrabajadoresNuevos =
    [
        new("Pedro", "Gil Mora", "60005208W", Terminator, DeSubcontrata: true),
        new("Lucía", "Prats Roca", "60005210G", Terminator, DeSubcontrata: true),
        new("Andrés", "Mora Vila", "60005211M", Terminator, DeSubcontrata: true),
        new("Nuria", "Sanz Coll", "X1005212C", Terminator, DeSubcontrata: true),
        new("Iván", "Ruiz Sala", "60005020K", Raccoon, DeSubcontrata: false),
        new("Marta", "Rey Soto", "60005021E", Nemesis, DeSubcontrata: true),
    ];

    /// <summary>
    /// Asignaciones de la siembra de base que contradicen a la maqueta: quien la maqueta sitúa en otro Centro, quien
    /// figura en ella «sin Centro que lo exija» (Rubén Vega Ortiz) y los dos de Montajes Skynet S.L. en Sede Bilbao,
    /// Centro en el que la maqueta no tiene a nadie de ese empleador.
    /// </summary>
    public static readonly IReadOnlyList<BajaDeAsignacionMaqueta> Bajas =
    [
        new("Paula Campos Lara", PlantaBilbao, Dia(11, 3, 2026)),
        new("Óscar Bravo Nieto", "Almacén Murcia", Dia(11, 3, 2026)),
        new("Héctor Pastor Rey", PlantaBilbao, Dia(1, 4, 2026)),
        new("Rubén Vega Ortiz", SedeSevilla, Dia(11, 3, 2026)),
        new("Mateo Soler Vidal", PlantaMurcia, Dia(20, 3, 2026)),
        new("Rubén Pastor Rey", SedeBilbao, Dia(20, 3, 2026)),
        new("Paula Salas Duque", SedeBilbao, Dia(20, 3, 2026)),
    ];

    public static readonly IReadOnlyList<AsignacionMaqueta> Asignaciones =
    [
        // Sede Sevilla: los técnicos del proyecto «Reforma nave Sevilla» y un Trabajador de Montajes Skynet S.L.
        new("Paula Campos Lara", SedeSevilla, Dia(12, 3, 2026)),
        new("Óscar Bravo Nieto", SedeSevilla, Dia(12, 3, 2026)),
        new("Héctor Pastor Rey", SedeSevilla, Dia(2, 4, 2026)),
        new("Iván Ruiz Sala", SedeSevilla, Dia(12, 3, 2026), BajaEn: Dia(30, 6, 2026)),
        new("Óscar Ferrer Pons", SedeSevilla, Dia(9, 4, 2026)),

        // Óscar Ferrer Pons está en los cuatro Centros de la maqueta (Almacén Vigo ya lo tiene de base).
        new("Óscar Ferrer Pons", PlantaMurcia, Dia(9, 4, 2026)),
        new("Óscar Ferrer Pons", PlantaBilbao, Dia(9, 4, 2026)),

        // Transportes Terminator S.L.: cuatro en Almacén Vigo (con Sonia Cano Prieto, de base) y el resto en Planta Bilbao.
        new("Mateo Soler Vidal", AlmacenVigo, Dia(21, 3, 2026)),
        new("Mateo Soler Vidal", PlantaBilbao, Dia(21, 3, 2026)),
        new("Pedro Gil Mora", AlmacenVigo, Dia(21, 3, 2026)),
        new("Andrés Mora Vila", AlmacenVigo, Dia(21, 3, 2026)),
        new("Lucía Prats Roca", PlantaBilbao, Dia(21, 3, 2026)),
        new("Nuria Sanz Coll", PlantaBilbao, Dia(21, 3, 2026)),

        // Montajes Skynet S.L.: cuatro en Almacén Vigo y tres en Planta Murcia («Centros de su empleador» del Vehículo).
        new("Rubén Pastor Rey", AlmacenVigo, Dia(21, 3, 2026)),
        new("Paula Salas Duque", AlmacenVigo, Dia(21, 3, 2026)),
    ];

    /// <summary>
    /// Los documentos de Trabajador cuya vigencia da la maqueta. Lo que no figura aquí se queda como lo deja la
    /// siembra de base (al día); a los Trabajadores nuevos se les da antes la misma documentación estándar.
    /// </summary>
    public static readonly IReadOnlyList<DocumentoMaqueta> DocumentosDeTrabajador =
    [
        // Transportes Terminator S.L.
        new("Sonia Cano Prieto", AptitudMedica, VigenciaMaqueta.Vence(Dia(12, 3, 2025), Dia(12, 3, 2026))),
        new("Sonia Cano Prieto", FormacionArt19, VigenciaMaqueta.SinConfirmar(Dia(24, 5, 2026))),
        new("Sonia Cano Prieto", EntregaEpi, VigenciaMaqueta.Vence(Dia(5, 10, 2025), Dia(5, 10, 2026))),
        new("Sonia Cano Prieto", InformacionArt18, VigenciaMaqueta.Vence(Dia(9, 4, 2026), Dia(9, 4, 2027))),
        new("Pedro Gil Mora", FormacionArt19, VigenciaMaqueta.Vence(Dia(30, 9, 2023), Dia(30, 9, 2026))),
        new("Pedro Gil Mora", EntregaEpi, VigenciaMaqueta.Vence(Dia(1, 9, 2025), Dia(1, 9, 2026))),
        new("Pedro Gil Mora", AptitudMedica, VigenciaMaqueta.SinConfirmar(Dia(24, 5, 2026))),
        new("Mateo Soler Vidal", EntregaEpi, VigenciaMaqueta.Vence(Dia(2, 3, 2026), Dia(2, 3, 2027))),
        new("Mateo Soler Vidal", AptitudMedica, VigenciaMaqueta.Vence(Dia(9, 4, 2026), Dia(9, 4, 2027))),
        new("Lucía Prats Roca", AptitudMedica, VigenciaMaqueta.Vence(Dia(20, 10, 2025), Dia(20, 10, 2026))),
        new("Lucía Prats Roca", FormacionArt19, VigenciaMaqueta.SinConfirmar(Dia(24, 5, 2026))),

        // Montajes Skynet S.L.
        new("Óscar Ferrer Pons", EntregaEpi, VigenciaMaqueta.Vence(Dia(9, 4, 2026), Dia(9, 4, 2027))),
        new("Óscar Ferrer Pons", AptitudMedica, VigenciaMaqueta.Vence(Dia(10, 4, 2026), Dia(10, 4, 2027))),
        new("Carla Molina Ríos", EntregaEpi, VigenciaMaqueta.Vence(Dia(16, 10, 2025), Dia(16, 10, 2026))),
        new("Carla Molina Ríos", AptitudMedica, VigenciaMaqueta.Vence(Dia(9, 4, 2026), Dia(9, 4, 2027))),
        new("Héctor Bravo Nieto", EntregaEpi, VigenciaMaqueta.SinConfirmar(Dia(24, 5, 2026))),
        new("Héctor Bravo Nieto", AptitudMedica, VigenciaMaqueta.SinConfirmar(Dia(24, 5, 2026))),
        new("Irene Navarro Gil", EntregaEpi, VigenciaMaqueta.Vence(Dia(9, 4, 2026), Dia(9, 4, 2027))),
        new("Irene Navarro Gil", AptitudMedica, VigenciaMaqueta.Vence(Dia(9, 4, 2026), Dia(9, 4, 2027))),

        // Limpiezas Raccoon S.L.
        new("Paula Campos Lara", AptitudMedica, VigenciaMaqueta.Vence(Dia(12, 3, 2025), Dia(12, 3, 2026))),
        new("Paula Campos Lara", EntregaEpi, VigenciaMaqueta.Ausente),
        new("Paula Campos Lara", ContratoDeTrabajo, VigenciaMaqueta.SinConfirmar(Dia(24, 5, 2026))),
        new("Paula Campos Lara", FormacionArt19, VigenciaMaqueta.Vence(Dia(9, 4, 2024), Dia(9, 4, 2027))),
        new("Paula Campos Lara", InformacionArt18, VigenciaMaqueta.Vence(Dia(9, 4, 2026), Dia(9, 4, 2027))),
        new("Óscar Bravo Nieto", EntregaEpi, VigenciaMaqueta.SinConfirmar(Dia(24, 5, 2026))),
        new("Óscar Bravo Nieto", AptitudMedica, VigenciaMaqueta.Vence(Dia(9, 4, 2026), Dia(9, 4, 2027))),
        new("Óscar Bravo Nieto", FormacionArt19, VigenciaMaqueta.Vence(Dia(9, 4, 2024), Dia(9, 4, 2027))),
        new("Óscar Bravo Nieto", InformacionArt18, VigenciaMaqueta.Vence(Dia(9, 4, 2026), Dia(9, 4, 2027))),
        new("Noelia Lozano Marín", EntregaEpi, VigenciaMaqueta.Vence(Dia(15, 6, 2026), Dia(15, 6, 2027))),
        new("Noelia Lozano Marín", AptitudMedica, VigenciaMaqueta.Vence(Dia(15, 6, 2026), Dia(15, 6, 2027))),

        // «Sin Centro que lo exija».
        new("Marta Rey Soto", EntregaEpi, VigenciaMaqueta.Vence(Dia(10, 1, 2026), Dia(10, 1, 2027))),
        new("Marta Rey Soto", AptitudMedica, VigenciaMaqueta.Ausente),
        new("Rubén Vega Ortiz", EntregaEpi, VigenciaMaqueta.Ausente),
        new("Rubén Vega Ortiz", AptitudMedica, VigenciaMaqueta.Ausente),
    ];

    /// <summary>
    /// Qué pide cada Centro de la maqueta. En los cuatro se exige el Contrato de Trabajo y no el Documento de
    /// identidad (la maqueta cuenta cinco requisitos por Trabajador: aptitud, formación, EPI, información y contrato).
    /// </summary>
    public static readonly IReadOnlyList<RequisitoDeCentroMaqueta> Requisitos =
    [
        .. CentrosDeLaMaqueta.Select(centro => new RequisitoDeCentroMaqueta(ContratoDeTrabajo, centro)),
        .. CentrosDeLaMaqueta.Select(centro => new RequisitoDeCentroMaqueta(DocumentoIdentidad, centro, Incluido: false)),

        // La aptitud médica bloquea el acceso en los cuatro, sin tolerancia.
        .. CentrosDeLaMaqueta.Select(centro => new RequisitoDeCentroMaqueta(AptitudMedica, centro, BloqueaAcceso: true, ToleranciaDias: 0)),

        // La Entrega de EPI: bloquea en Sede Sevilla; en los de Cyberdyne Ibérica S.A. hereda su tolerancia; en Planta Bilbao se renueva antes.
        new(EntregaEpi, SedeSevilla, BloqueaAcceso: true, ToleranciaDias: 0),
        new(EntregaEpi, AlmacenVigo),
        new(EntregaEpi, PlantaMurcia),
        new(EntregaEpi, PlantaBilbao, PeriodicidadMeses: PeriodicidadEpiEnPlantaBilbao),
    ];

    /// <summary>Documentación de empresa: la de Limpiezas Raccoon S.L. (página de Visita) y la de Transportes Terminator S.L. (página de Subcontrata).</summary>
    public static readonly IReadOnlyList<DocumentoMaqueta> DocumentosDeEmpresa =
    [
        new(Raccoon, Rnt, VigenciaMaqueta.Vence(Dia(1, 9, 2026), Dia(30, 9, 2026))),
        new(Raccoon, CorrienteSeguridadSocial, VigenciaMaqueta.Vence(Dia(1, 10, 2026), Dia(31, 10, 2026))),
        new(Raccoon, CorrienteHacienda, VigenciaMaqueta.Vence(Dia(15, 3, 2026), Dia(15, 3, 2027))),
        new(Raccoon, SeguroResponsabilidadCivil, VigenciaMaqueta.Vence(Dia(1, 1, 2026), Dia(31, 12, 2026))),

        // Sin Planificación de la Actividad Preventiva: en la maqueta «se exige y no hay documento».
        new(Terminator, Rnt, VigenciaMaqueta.Vence(Dia(1, 9, 2026), Dia(30, 9, 2026))),
        new(Terminator, CorrienteSeguridadSocial, VigenciaMaqueta.Vence(Dia(20, 4, 2026), Dia(20, 10, 2026))),
        new(Terminator, EvaluacionDeRiesgos, VigenciaMaqueta.SinConfirmar(Dia(24, 5, 2026))),
        new(Terminator, CorrienteHacienda, VigenciaMaqueta.Vence(Dia(15, 3, 2026), Dia(15, 3, 2027))),
        new(Terminator, Rlc, VigenciaMaqueta.Vence(Dia(30, 9, 2026), Dia(31, 12, 2026))),
        new(Terminator, ServicioDePrevencion, VigenciaMaqueta.Vence(Dia(1, 1, 2026), Dia(31, 12, 2026))),
        new(Terminator, SeguroResponsabilidadCivil, VigenciaMaqueta.Vence(Dia(1, 1, 2026), Dia(31, 12, 2026))),
    ];

    public static readonly IReadOnlyList<VehiculoMaqueta> Vehiculos =
    [
        new(CamionGrua, "Iveco Daily", MatriculaCamionGrua, Skynet, DeSubcontrata: false),
        new("Furgoneta T2", "Renault Master", "7788 LLK", Terminator, DeSubcontrata: true),
        new("Camión 7", "MAN TGL", "4455 MNP", Terminator, DeSubcontrata: true),
        new("Furgoneta 1", "Ford Transit", "1234 ABC", Raccoon, DeSubcontrata: false),
    ];

    /// <summary>La inspección técnica anterior del Camión grúa, que la vigente sustituye («Documento renovado» del historial).</summary>
    public static readonly VigenciaMaqueta InspeccionAnteriorDelCamionGrua = VigenciaMaqueta.Vence(Dia(2, 9, 2024), Dia(2, 9, 2025));

    public static readonly IReadOnlyList<DocumentoMaqueta> DocumentosDeVehiculo =
    [
        new(CamionGrua, InspeccionTecnica, VigenciaMaqueta.Vence(Dia(2, 9, 2025), Dia(2, 9, 2026))),
        new(CamionGrua, SeguroDelVehiculo, VigenciaMaqueta.Vence(Dia(19, 10, 2025), Dia(19, 10, 2026))),
        new(CamionGrua, FichaTecnica, VigenciaMaqueta.SinConfirmar(Dia(24, 5, 2026))),
        new(CamionGrua, PermisoDeCirculacion, VigenciaMaqueta.NoCaduca(Dia(14, 2, 2021))),

        new("Furgoneta T2", InspeccionTecnica, VigenciaMaqueta.Vence(Dia(19, 10, 2025), Dia(19, 10, 2026))),
        new("Furgoneta T2", SeguroDelVehiculo, VigenciaMaqueta.Vence(Dia(1, 1, 2026), Dia(31, 12, 2026))),
        new("Camión 7", InspeccionTecnica, VigenciaMaqueta.Vence(Dia(15, 3, 2026), Dia(15, 3, 2027))),
        new("Camión 7", SeguroDelVehiculo, VigenciaMaqueta.Vence(Dia(1, 1, 2026), Dia(31, 12, 2026))),
        new("Furgoneta 1", InspeccionTecnica, VigenciaMaqueta.Vence(Dia(15, 3, 2026), Dia(15, 3, 2027))),
        new("Furgoneta 1", SeguroDelVehiculo, VigenciaMaqueta.SinConfirmar(Dia(24, 5, 2026))),
    ];

    public static readonly IReadOnlyList<TipoDeProyectoMaqueta> TiposDeProyecto =
    [
        new(ActaDeCoordinacion, VigenciaMeses: 6),
        new(AperturaDeCentro, VigenciaMeses: null),
        new(PlanDeSeguridadYSalud, VigenciaMeses: null),
    ];

    /// <summary>
    /// «Reforma nave Sevilla» es el proyecto protagonista; «Mantenimiento Almacén Vigo» solo aparece como fila en la
    /// pestaña Proyectos de la Subcontrata, y sus fechas —que la maqueta no da— son las del alta de la subcontrata.
    /// </summary>
    public static readonly IReadOnlyList<ProyectoMaqueta> Proyectos =
    [
        new(ProyectoReforma, Umbrella, SedeSevilla, Dia(12, 3, 2026), Dia(30, 11, 2026),
            "Acceso por muelle 3. Coordinación semanal los lunes con el titular del Centro."),
        new(ProyectoMantenimiento, Cyberdyne, AlmacenVigo, Dia(21, 3, 2026), Dia(31, 12, 2026), Notas: null),
    ];

    public static readonly IReadOnlyList<TecnicoDeProyectoMaqueta> Tecnicos =
    [
        new(ProyectoReforma, "Paula Campos Lara", Dia(12, 3, 2026)),
        new(ProyectoReforma, "Lucía Prats Roca", Dia(1, 6, 2026)),
        new(ProyectoReforma, "Óscar Bravo Nieto", Dia(12, 3, 2026)),
        new(ProyectoReforma, "Noelia Lozano Marín", Dia(12, 3, 2026)),
        new(ProyectoReforma, "Héctor Pastor Rey", Dia(2, 4, 2026)),
        new(ProyectoReforma, "Iván Ruiz Sala", Dia(12, 3, 2026), BajaEn: Dia(30, 6, 2026)),

        new(ProyectoMantenimiento, "Sonia Cano Prieto", Dia(21, 3, 2026)),
        new(ProyectoMantenimiento, "Pedro Gil Mora", Dia(21, 3, 2026)),
        new(ProyectoMantenimiento, "Mateo Soler Vidal", Dia(21, 3, 2026)),
        new(ProyectoMantenimiento, "Andrés Mora Vila", Dia(21, 3, 2026)),
    ];

    public static readonly IReadOnlyList<DocumentoMaqueta> DocumentosDeProyecto =
    [
        new(ProyectoReforma, ActaDeCoordinacion, VigenciaMaqueta.Vence(Dia(30, 4, 2026), Dia(30, 10, 2026))),
        new(ProyectoReforma, AperturaDeCentro, VigenciaMaqueta.SinConfirmar(Dia(12, 3, 2026))),
        new(ProyectoReforma, PlanDeSeguridadYSalud, VigenciaMaqueta.NoCaduca(Dia(10, 3, 2026))),
    ];

    /// <summary>
    /// La Visita protagonista, que termina hoy, y la ya finalizada del mismo Centro. La segunda no es una Visita
    /// nueva: es la que la siembra de base deja en Sede Sevilla, movida de fecha y con un único Trabajador.
    /// </summary>
    public static readonly VisitaMaqueta VisitaEnCurso = new(
        SedeSevilla, Dia(6, 10, 2026), Dia(8, 10, 2026), new TimeOnly(8, 0), OrigenVisita.Correo,
        ["Paula Campos Lara", "Óscar Bravo Nieto"]);

    public static readonly VisitaMaqueta VisitaFinalizada = new(
        SedeSevilla, Dia(3, 9, 2026), Dia(3, 9, 2026), Entrada: null, OrigenVisita.Plataforma, ["Noelia Lozano Marín"]);

    /// <summary>Lo que el equipo comprobó en la plataforma del titular del Centro sobre Transportes Terminator S.L. (Supervisada).</summary>
    public static readonly IReadOnlyList<VerificacionExternaMaqueta> Verificaciones =
    [
        new(Rnt, AlmacenVigo, Dia(2, 10, 2026), Valido: false),
        new(CorrienteSeguridadSocial, AlmacenVigo, Dia(2, 9, 2026), Valido: true, Dia(20, 10, 2026)),
        new(EvaluacionDeRiesgos, AlmacenVigo, Dia(2, 9, 2026), Valido: true, Dia(2, 9, 2027)),
        new(SeguroResponsabilidadCivil, AlmacenVigo, Dia(2, 9, 2026), Valido: true, Dia(31, 12, 2026)),
        new(SeguroResponsabilidadCivil, PlantaBilbao, Dia(2, 9, 2026), Valido: true, Dia(31, 12, 2026)),
        new(AptitudMedica, PlantaBilbao, Dia(15, 9, 2026), Valido: false),
        new(AptitudMedica, AlmacenVigo, Dia(2, 9, 2026), Valido: true, Dia(2, 12, 2026)),
    ];
}
