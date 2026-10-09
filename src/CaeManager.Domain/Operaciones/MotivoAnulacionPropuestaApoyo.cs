namespace CaeManager.Domain.Operaciones;

/// <summary>Por qué se anuló una <see cref="PropuestaApoyoCartera"/> al ir a aceptarla.</summary>
public enum MotivoAnulacionPropuestaApoyo
{
    /// <summary>El destinatario ya tenía el Tenant propietario en su cartera.</summary>
    YaEnCartera = 0,

    /// <summary>La Asignación de Operación ya no está vigente, o el Tenant propietario retiró la delegación al Operador CAE.</summary>
    OperacionNoVigente = 1,

    /// <summary>El destinatario ya no es una cuenta activa con rol Gestor CAE del Operador CAE.</summary>
    DestinatarioNoDisponible = 2,

    /// <summary>
    /// Quien la propuso ya no es el principal vigente de la Asignación de Operación: perdió la
    /// marca, su cartera se cerró o su cuenta dejó de ser de gestión CAE activa.
    /// </summary>
    ProponenteYaNoEsPrincipal = 3,
}
