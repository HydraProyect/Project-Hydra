namespace CaeManager.Domain.Inbound.Requisitos;

/// <summary>
/// Capa de la plantilla de requisitos del Tenant TCE (CA-1; plan backend Inbound § 2.3). Cada plantilla cuelga de una
/// sola referencia: una Actividad o un riesgo del catálogo base, un Centro propio o un Cliente empresarial propio. Las
/// capas se resuelven al leer (B-15), nunca se copian al contrato. Se guarda por nombre, con un CHECK que repite la lista.
/// </summary>
public enum TipoPlantillaRequisitos
{
    PorActividad = 1,
    PorRiesgo = 2,
    DeCentro = 3,

    /// <summary>
    /// Exigencias del Cliente empresarial (N3, tarjeta 17): lo que «+ Pedir … como exigencia» guarda (D-15). Nunca se
    /// presenta como obligación legal.
    /// </summary>
    ExigenciasClienteEmpresarial = 4,
}

/// <summary>
/// A qué sujeto se pide el requisito. Sale del <c>AmbitoAplicacion</c> del tipo de documento, que es fijo: un requisito
/// de un tipo de Trabajador se pide a Trabajadores. La maquinaria es un atributo del vehículo (D-22), sin entidad propia.
/// </summary>
public enum EntidadRequisito
{
    Empresa = 1,
    Trabajador = 2,
    VehiculoOMaquina = 3,
}

/// <summary>
/// Con qué autoridad se pide (CA-6): <see cref="LegalN1"/> lo exige una norma, <see cref="Convenio"/> un convenio (N2),
/// <see cref="Actividad"/> la práctica de la Actividad, <see cref="ExigenciaN3"/> es exigencia contractual del Cliente
/// empresarial y <see cref="PropioTenant"/> lo crea el Gestor CAE del Tenant TCE sin catálogo (DECISION_LOG 2026-11-14).
/// </summary>
public enum OrigenRequisito
{
    LegalN1 = 1,
    Convenio = 2,
    Actividad = 3,
    ExigenciaN3 = 4,
    PropioTenant = 5,
}

/// <summary>Cómo vence lo que acredita el requisito.</summary>
public enum ModoVigenciaRequisito
{
    /// <summary>Vigencia fija en meses desde la emisión.</summary>
    Fija = 1,

    /// <summary>La fecha de vencimiento la pone el propio documento.</summary>
    LaPoneElDocumento = 2,

    SinVencimiento = 3,
}

/// <summary>
/// A quién se pide según su origen (DECISION_LOG 2026-11-14): vale para la Empresa contratista y para el Trabajador.
/// Quién es extranjero lo decide el país de la Empresa contratista o el documento de identidad del Trabajador (B-15).
/// <para>
/// Desde el 2026-11-17, en el Trabajador ya no decide nada: el extranjero se identifica por el modelo de contratación
/// (<see cref="CaeManager.Domain.Trabajadores.TipoContratacion.Extranjero"/>) y a quién se pide un requisito de Trabajador lo dice
/// solo <see cref="RequisitoPlantilla.TiposContratacion"/>. En la Empresa contratista sigue decidiendo (decisión del 2026-11-14,
/// el país de la Empresa), y los Commands lo siguen recibiendo para los dos.
/// </para>
/// </summary>
// shortcut: SePideA queda sin leerse en el Trabajador; en la Empresa contratista sigue vigente (decisión 2026-11-17)
public enum DestinatarioRequisito
{
    Todos = 1,
    Nacionales = 2,
    Extranjeros = 3,
}

/// <summary>
/// Ámbito del documento que lo acredita (CA-15 a CA-17): general del Tenant TEC, específico del Tenant TCE o específico del
/// Centro. Lo aplica EP-6 (B-10).
/// </summary>
public enum AmbitoDocumentoRequisito
{
    GeneralTec = 1,
    EspecificoTce = 2,
    EspecificoCentro = 3,
}
