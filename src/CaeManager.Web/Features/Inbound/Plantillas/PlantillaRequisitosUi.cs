using CaeManager.Application.Inbound.Requisitos.Queries;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Inbound.Plantillas.Recursos;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Inbound.Plantillas;

/// <summary>
/// Presentación común de las plantillas de requisitos (listado, Editar requisito y Campos del formulario): rótulos de nivel,
/// Entidad, origen, vigencia y tolerancia. Una sola tabla para que las pantallas no se contradigan.
/// </summary>
public static class PlantillaRequisitosUi
{
    /// <summary>Opciones del formulario de Editar requisito: tolerancia en días tras el vencimiento (R-6 la limita por tipo).</summary>
    public static IReadOnlyList<int> ToleranciasOfrecidas { get; } = [0, 7, 15, 30];

    // shortcut: SePideA queda sin leerse en el Trabajador; en la Empresa contratista se sigue ofreciendo (decisión 2026-11-17).
    public static IReadOnlyList<DestinatarioRequisito> Destinatarios { get; } = Enum.GetValues<DestinatarioRequisito>();

    public static IReadOnlyList<ModoVigenciaRequisito> ModosVigencia { get; } = Enum.GetValues<ModoVigenciaRequisito>();

    public static IReadOnlyList<TipoPlantillaRequisitos> Niveles { get; } = Enum.GetValues<TipoPlantillaRequisitos>();

    public static string ClaveNivel(TipoPlantillaRequisitos tipo) => tipo switch
    {
        TipoPlantillaRequisitos.PorActividad => "NivelActividad",
        TipoPlantillaRequisitos.PorRiesgo => "NivelRiesgo",
        TipoPlantillaRequisitos.DeCentro => "NivelCentro",
        _ => "NivelExigencia",
    };

    public static string ClaveEntidad(EntidadRequisito entidad) => entidad switch
    {
        EntidadRequisito.Empresa => "EntidadEmpresa",
        EntidadRequisito.Trabajador => "EntidadTrabajador",
        _ => "EntidadVehiculo",
    };

    public static string ClaveOrigen(OrigenRequisito origen) => origen switch
    {
        OrigenRequisito.LegalN1 => "OrigenLegal",
        OrigenRequisito.Convenio => "OrigenConvenio",
        OrigenRequisito.Actividad => "OrigenActividad",
        OrigenRequisito.ExigenciaN3 => "OrigenExigencia",
        _ => "OrigenPropio",
    };

    public static TonoBadge TonoOrigen(OrigenRequisito origen) => origen switch
    {
        OrigenRequisito.Convenio => TonoBadge.Info,
        OrigenRequisito.Actividad => TonoBadge.Exito,
        OrigenRequisito.ExigenciaN3 => TonoBadge.Advertencia,
        _ => TonoBadge.Neutro,
    };

    public static string ClaveDestinatario(DestinatarioRequisito destinatario) => destinatario switch
    {
        DestinatarioRequisito.Nacionales => "DestinatarioNacionales",
        DestinatarioRequisito.Extranjeros => "DestinatarioExtranjeros",
        _ => "DestinatarioTodos",
    };

    public static string ClaveModoVigencia(ModoVigenciaRequisito modo) => modo switch
    {
        ModoVigenciaRequisito.Fija => "VigenciaFija",
        ModoVigenciaRequisito.LaPoneElDocumento => "VigenciaDocumento",
        _ => "VigenciaSinVencimiento",
    };

    public static string ClaveContratacion(TipoContratacion tipo) => tipo switch
    {
        TipoContratacion.CuentaAjena => "ContratacionCuentaAjena",
        TipoContratacion.Autonomo => "ContratacionAutonomo",
        TipoContratacion.Extranjero => "ContratacionExtranjero",
        _ => "ContratacionVoluntario",
    };

    /// <summary>«5 años», «18 meses», «La pone el documento» o «Sin vencimiento».</summary>
    public static string Vigencia(RequisitoPlantillaDto r, IStringLocalizer<TextosPlantillasRequisitos> textos) => r.ModoVigencia switch
    {
        ModoVigenciaRequisito.Fija when r.VigenciaMeses == 12 => textos["VigenciaUnAnio"],
        ModoVigenciaRequisito.Fija when r.VigenciaMeses is { } meses && meses % 12 == 0 => textos["VigenciaAnios", meses / 12],
        ModoVigenciaRequisito.Fija when r.VigenciaMeses == 1 => textos["VigenciaUnMes"],
        ModoVigenciaRequisito.Fija when r.VigenciaMeses is { } meses => textos["VigenciaMeses", meses],
        ModoVigenciaRequisito.Fija => textos["VigenciaSinVencimiento"],
        _ => textos[ClaveModoVigencia(r.ModoVigencia)],
    };

    /// <summary>
    /// La tolerancia que se enseña. No se aplica nunca a aptitud médica, formación obligatoria ni seguros (R-6); el DTO lleva la
    /// fijada y la efectiva la recalcula el backend al decidir (B-15), así que aquí «No se aplica» solo se dice del tipo
    /// sin tolerancia fijada y de vigencia sin vencimiento.
    /// </summary>
    public static string Tolerancia(RequisitoPlantillaDto r, IStringLocalizer<TextosPlantillasRequisitos> textos) =>
        r.ModoVigencia == ModoVigenciaRequisito.SinVencimiento ? textos["ToleranciaNoVence"]
        : r.ToleranciaDias is > 0 and var dias ? textos["ToleranciaDias", dias]
        : textos["ToleranciaSin"];

    public static IReadOnlyList<TipoContratacion> Contrataciones { get; } = Enum.GetValues<TipoContratacion>();

    public static EntidadRequisito EntidadDe(AmbitoAplicacion ambito) => ambito switch
    {
        AmbitoAplicacion.Trabajador => EntidadRequisito.Trabajador,
        AmbitoAplicacion.Vehiculo => EntidadRequisito.VehiculoOMaquina,
        _ => EntidadRequisito.Empresa,
    };
}
