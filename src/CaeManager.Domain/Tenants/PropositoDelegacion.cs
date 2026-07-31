namespace CaeManager.Domain.Tenants;

/// <summary>
/// Para qué existe una <see cref="DelegacionTenant"/>. No es una etiqueta
/// informativa: separa dos relaciones jurídicamente distintas que comparten
/// el mismo mecanismo técnico.
/// </summary>
public enum PropositoDelegacion
{
    /// <summary>
    /// Una Consultora de PRL gestiona la CAE de un Cliente Delegante que la
    /// ha contratado (ADR-004). Es la delegación de negocio: existe porque
    /// hay un contrato entre dos organizaciones y dura lo que dure.
    /// </summary>
    Comercial,

    /// <summary>
    /// El equipo de Hydra entra en el tenant de un cliente para reproducir o
    /// verificar una incidencia. Es Hydra —encargado del tratamiento—
    /// accediendo a datos personales de los que el cliente es responsable,
    /// así que necesita motivo, ventana acotada y traza propia; nunca debería
    /// estar activa "por si acaso".
    ///
    /// Debe quedar reflejada en el DPA que ADR-003 tiene pendiente antes de
    /// usarse con un cliente real.
    /// </summary>
    Soporte
}
