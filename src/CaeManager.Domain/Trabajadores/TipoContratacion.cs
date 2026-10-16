namespace CaeManager.Domain.Trabajadores;

/// <summary>
/// Cómo presta servicio el Trabajador: por cuenta ajena (contrato con su Empresa), como autónomo o como
/// voluntario. Es un eje distinto de <see cref="Trabajador.TipoVinculo"/>, que sigue siendo texto libre para la
/// modalidad contractual que piden las plataformas (indefinido, temporal, ETT...). Un voluntario sin un Proyecto
/// de tipo Evento vigente o futuro está inactivo (<see cref="Proyectos.ActividadVoluntario"/>): no se borra, pero
/// sus vencimientos no avisan ni cuentan en el Centro. Los ordinales se persisten: no se reordenan.
/// </summary>
public enum TipoContratacion
{
    CuentaAjena = 0,
    Autonomo = 1,
    Voluntario = 2,
}
