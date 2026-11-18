namespace CaeManager.Domain.Trabajadores;

/// <summary>
/// Cómo presta servicio el Trabajador: por cuenta ajena (contrato con su Empresa), como autónomo o como
/// voluntario. Es un eje distinto de <see cref="Trabajador.TipoVinculo"/>, que sigue siendo texto libre para la
/// modalidad contractual que piden las plataformas (indefinido, temporal, ETT...). Un voluntario sin un Proyecto
/// de tipo Evento vigente o futuro está inactivo (<see cref="Proyectos.ActividadVoluntario"/>): no se borra, pero
/// sus vencimientos no avisan ni cuentan en el Centro. Los ordinales se persisten: no se reordenan.
/// <para>
/// <see cref="Extranjero"/> (decisión de Chris de 2026-11-17, DECISION_LOG): el extranjero se identifica por el modelo de
/// contratación, que es exclusivo. Un Trabajador extranjero es <see cref="Extranjero"/> en vez de por cuenta ajena o
/// autónomo, así que de él no queda dicho cómo está contratado; un voluntario sigue siendo <see cref="Voluntario"/>. Es el
/// único criterio con el que un requisito de Trabajador o un tipo de documento se pide a Trabajadores extranjeros
/// (<see cref="TiposContratacion"/>); a la Empresa contratista se le sigue pidiendo según su «Se pide a».
/// No toca Facturación: el extranjero cobrable sigue saliendo del documento de identidad o de la marca a mano
/// (<see cref="CriterioExtranjeroCobrable"/>), no de este valor.
/// </para>
/// </summary>
public enum TipoContratacion
{
    CuentaAjena = 0,
    Autonomo = 1,
    Voluntario = 2,
    Extranjero = 3,
}
