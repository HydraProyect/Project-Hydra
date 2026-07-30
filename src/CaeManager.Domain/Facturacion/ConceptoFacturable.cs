namespace CaeManager.Domain.Facturacion;

/// <summary>
/// Tipos de evento facturable que el consultor (tenant) puede tarificar
/// por cliente. Cada <see cref="TarifaCliente"/> asocia un precio unitario
/// a uno de estos conceptos, y el resumen de facturación calcula las
/// unidades consumidas en el período desde los datos ya existentes.
/// </summary>
public enum ConceptoFacturable
{
    /// <summary>Por cada trabajador activo asignado a un centro del cliente al cierre del período.</summary>
    TrabajadorActivo,

    /// <summary>Por cada centro dado de alta para el cliente durante el período.</summary>
    AltaCentro,

    /// <summary>Por cada trabajador extranjero (NIE/TIE/pasaporte) que registra visita a un centro del cliente en el período.</summary>
    VisitaTrabajadorExtranjero,

    /// <summary>Por cada documento creado o renovado para trabajadores asignados al cliente durante el período.</summary>
    DocumentoGestionado,

    /// <summary>Por cada técnico único asignado a algún Proyecto del cliente durante el período — facturación de obra, separada de la gestión CAE tradicional.</summary>
    TecnicoAsignadoProyecto,

    /// <summary>Por cada Documento gestionado (creado) en algún Proyecto del cliente durante el período.</summary>
    GestionProyectoRealizada,

    /// <summary>Por cada día que algún Proyecto del cliente permaneció abierto dentro del período.</summary>
    DiaProyectoAbierto,
}
