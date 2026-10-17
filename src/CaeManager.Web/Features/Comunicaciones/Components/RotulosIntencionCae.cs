using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Web.Features.Comunicaciones.Components;

public static class RotulosIntencionCae
{
    public static string Nombre(IntencionCae intencion) => intencion switch
    {
        IntencionCae.CoordinacionAcceso => "Coordinación de acceso",
        IntencionCae.DocumentacionMensual => "Documentación mensual",
        IntencionCae.AltaContratoAccesoCentro => "Alta de contrato y acceso a un Centro",
        IntencionCae.CopiaProyectoObra => "Copia en proyecto u obra",
        IntencionCae.UrgenciaAcceso => "Urgencia de acceso",
        IntencionCae.ConsultaRequisitos => "Consulta de requisitos",
        _ => "Otras"
    };
}
