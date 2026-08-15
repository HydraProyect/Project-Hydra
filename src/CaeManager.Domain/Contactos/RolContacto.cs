namespace CaeManager.Domain.Contactos;

/// <summary>
/// Clasificación estructurada de un <see cref="ContactoAgenda"/>, en paralelo
/// a <see cref="ContactoAgenda.Cargo"/> (texto libre que ve el usuario, p.ej.
/// "Coordinador de Prevención"). No son excluyentes entre sí: en una empresa
/// pequeña la misma persona puede ser a la vez <see cref="ResponsablePrl"/> y
/// <see cref="ContactoCae"/> — de ahí que <see cref="ContactoAgendaRol"/> sea
/// una colección, no un campo único en <see cref="ContactoAgenda"/>
/// (ADR-010 § 2.5).
/// </summary>
public enum RolContacto
{
    ResponsablePrl,
    RepresentanteLegal,
    ContactoCae,
    Otro
}
