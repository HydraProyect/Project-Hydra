namespace CaeManager.Domain.Plantillas;

/// <summary>
/// Enum cerrado y determinista — nunca texto libre — de dónde sale el valor
/// de un <see cref="PlantillaElemento"/> de tipo dato. La detección inicial
/// (IA) solo puede *proponer* uno de estos valores; una vez confirmado por
/// el gestor, resolverlo es una lectura directa del dominio, sin IA
/// (ADR-010 § 2.4).
/// </summary>
public enum FuenteDatoPlantilla
{
    EmpresaRazonSocial,
    EmpresaCif,
    TrabajadorNombreCompleto,
    TrabajadorDni,
    TrabajadorPuesto,
    CentroNombre,
    CentroDireccion,
    ClienteRazonSocial,
    ClienteCif,

    /// <summary>Fecha de generación del documento — calculado, no leído del dominio.</summary>
    DocumentoFechaGeneracion,

    /// <summary>Vía <c>Empresa.ContactoConRol(RolContacto.ResponsablePrl)</c> — requiere <c>ContactoAgendaRol</c> configurado.</summary>
    EmpresaResponsablePrl,

    /// <summary>Vía <c>Empresa.ContactoConRol(RolContacto.RepresentanteLegal)</c>.</summary>
    EmpresaRepresentanteLegal,

    /// <summary>Vía <c>Empresa.ContactoConRol(RolContacto.ContactoCae)</c>.</summary>
    EmpresaContactoCae,

    /// <summary>El gestor lo rellena a mano en la revisión — nunca autofill.</summary>
    Manual,

    /// <summary>Texto fijo configurado en el propio <see cref="PlantillaElemento.ValorConstante"/>, igual para cada documento generado.</summary>
    Constante
}
