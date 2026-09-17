namespace CaeManager.Domain.Tenants;

/// <summary>
/// Para qué existe una <see cref="DelegacionTenant"/>. No es una etiqueta
/// informativa: separa dos relaciones jurídicamente distintas que comparten
/// el mismo mecanismo técnico.
///
/// <b>PD-A5 (2026-09-17): <see cref="OperadorExterno"/> sustituye al antiguo
/// valor <c>Comercial</c>.</b> El nombre anterior colisionaba con el plano
/// Comercial del contrato de terminología (<c>Cliente comercial TALVEG</c>,
/// <c>Pagador TALVEG</c> — quién contrata o paga TALVEG) cuando lo que esta
/// delegación representa siempre fue el plano de Operación (ADR-011 § 1): una
/// Consultora de PRL —Operador CAE externo— gestionando la CAE de un Cliente
/// Delegante. Persistido como texto (<c>DelegacionTenantConfiguration.cs</c>),
/// así que el rename incluye una migración de datos, no solo de código —ver
/// <c>RenombrarPropositoDelegacionComercial</c>.
/// </summary>
public enum PropositoDelegacion
{
    /// <summary>
    /// Una Consultora de PRL —un Operador CAE externo— gestiona la CAE de un
    /// Cliente Delegante que la ha contratado (ADR-004). Es la delegación de
    /// operación, plano 2 del ADR-011: existe porque hay un contrato entre
    /// dos organizaciones y dura lo que dure. Nunca representa el plano
    /// Comercial (quién contrata o paga TALVEG) — de ahí el nombre, no
    /// <c>Comercial</c>.
    /// </summary>
    OperadorExterno,

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
